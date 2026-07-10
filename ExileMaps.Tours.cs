// Named tours: build, optimize, draw, Tours panel, Build Mode, panel button bar.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using ExileMaps.Classes;
using GameOffsets2.Native;

namespace ExileMaps;

public partial class ExileMapsCore
{
    private bool toursPanelOpen = false;

    // Interactive Build Tour mode: clicking adds nodes to the active tour, right-click removes.
    // Transient, not persisted. Toggled by the panel button or hotkey; Escape or atlas-close cancels.
    private bool buildModeActive = false;

    // ---- Persisted panel window rects ----
    // Track per-panel "was open last frame" so we only force-restore the saved rect on the open frame,
    // then let the user drag/resize freely (saving the current rect each frame).
    private bool wpPanelWasOpen, overviewWasOpen, toursPanelWasOpen;

    // Call before ImGui.Begin. On the frame a panel opens, restores its saved rect (or a default).
    private void BeginPersistedWindow(PanelRect rect, bool justOpened, Vector2 defPos, Vector2 defSize)
    {
        if (!justOpened) return;
        Vector2 p = rect.Saved ? new Vector2(rect.X, rect.Y) : defPos;
        Vector2 s = rect.Saved && rect.W > 0 && rect.H > 0 ? new Vector2(rect.W, rect.H) : defSize;
        ImGui.SetNextWindowPos(p, ImGuiCond.Always);
        ImGui.SetNextWindowSize(s, ImGuiCond.Always);
    }

    // Call after ImGui.Begin (window active). Stores the current pos/size for next open.
    private static void SavePersistedWindow(PanelRect rect)
    {
        var p = ImGui.GetWindowPos();
        var s = ImGui.GetWindowSize();
        rect.X = p.X; rect.Y = p.Y; rect.W = s.X; rect.H = s.Y; rect.Saved = true;
    }

    // ---- Textured panel buttons ----

    // One atlas panel button: per-state textures, native tooltip size, and open/toggle hooks.
    private struct PanelButtonDef
    {
        public string Key;
        public IntPtr Normal, Hover, Pressed, Tooltip;
        public Vector2 NormalSize;    // native px of normal/hover sprite
        public Vector2 PressedSize;   // native px of pressed sprite (same 128 size as normal)
        public Vector2 TooltipSize;   // native px, used to keep tooltip aspect ratio
        public Func<bool> GetOpen;
        public Action Toggle;
    }

    private List<PanelButtonDef> panelButtons;

    private IntPtr LoadPanelTexture(string file)
    {
        try
        {
            var full = Path.Combine(DirectoryFullName, "textures", file);
            if (!File.Exists(full)) { LogError($"Panel button texture missing: {file}"); return IntPtr.Zero; }
            Graphics.InitImage(file, full);
            return Graphics.GetTextureId(file);
        }
        catch (Exception e) { LogError($"Error loading panel texture {file}: {e.Message}"); return IntPtr.Zero; }
    }

    // Called once from Initialise after the other textures load.
    private void LoadPanelButtonTextures()
    {
        IntPtr L(string f) => LoadPanelTexture(f);
        // All button states are 128x128 native; pressed is no longer a larger glow sprite.
        // Drawn at ~70% size.
        var btnSz = new Vector2(90, 90);
        panelButtons = new List<PanelButtonDef>
        {
            new PanelButtonDef {
                Key = "wp",
                Normal = L("waypoints-normal.png"), Hover = L("waypoints-hover.png"),
                Pressed = L("waypoints-pressed.png"), Tooltip = L("waypoints-tooltip.png"),
                NormalSize = btnSz, PressedSize = btnSz,
                TooltipSize = new Vector2(120, 39),
                GetOpen = () => WaypointPanelIsOpen, Toggle = () => WaypointPanelIsOpen = !WaypointPanelIsOpen,
            },
            new PanelButtonDef {
                Key = "tours",
                Normal = L("tours-normal.png"), Hover = L("tours-hover.png"),
                Pressed = L("tours-pressed.png"), Tooltip = L("tours-tooltip.png"),
                NormalSize = btnSz, PressedSize = btnSz,
                TooltipSize = new Vector2(82, 39),
                GetOpen = () => toursPanelOpen, Toggle = () => toursPanelOpen = !toursPanelOpen,
            },
            new PanelButtonDef {
                Key = "atlas",
                Normal = L("atlas-normal.png"), Hover = L("atlas-hover.png"),
                Pressed = L("atlas-pressed.png"), Tooltip = L("atlas-tooltip.png"),
                NormalSize = btnSz, PressedSize = btnSz,
                TooltipSize = new Vector2(165, 39),
                GetOpen = () => atlasOverviewOpen, Toggle = () => atlasOverviewOpen = !atlasOverviewOpen,
            },
            // Action button (not a toggle): GetOpen stays false so it returns to normal after the
            // click; the held state still shows the pressed texture while the mouse is down.
            new PanelButtonDef {
                Key = "export",
                Normal = L("export-normal.png"), Hover = L("export-hover.png"),
                Pressed = L("export-pressed.png"), Tooltip = L("export-tooltip.png"),
                NormalSize = btnSz, PressedSize = btnSz,
                TooltipSize = new Vector2(145, 39),
                GetOpen = () => false, Toggle = () => ExportAtlasHtml(),
            },
        };
    }

    // ---- Routing ----

    // Resolve a stop's coordinate to a live cache node, or null if not present this scan.
    private Node ResolveStop(TourStop s)
    {
        lock (mapCacheLock)
            return mapCache.TryGetValue(new Vector2i(s.X, s.Y), out var node) ? node : null;
    }

    // Build the path segments for one tour in its current (manual) stop order. seg0 is the lead-in
    // from the explored frontier to stop0; seg_i is the path from stop_{i-1} to stop_i. Unresolvable
    // or unreachable stops are recorded in t.Skipped and skipped without breaking the chain.
    private void BuildTour(Tour t)
    {
        t.Segments = new();
        t.ResolvedStops = new();
        t.Skipped = new();
        try
        {
            // Auto-drop stops the player has finished. Only prune when the stop resolves to a live
            // node; an off-scan (unresolvable) stop is left in place rather than assumed done.
            if (Settings.Tours.AutoClearCompleted)
                t.Stops.RemoveAll(s =>
                {
                    var n = ResolveStop(s);
                    return n != null && (n.IsVisited || n.IsCompleted);
                });

            var nodes = new List<Node>();
            foreach (var s in t.Stops)
            {
                var node = ResolveStop(s);
                if (node != null) nodes.Add(node);
                else t.Skipped.Add($"({s.X},{s.Y})");
            }
            if (nodes.Count == 0) { t.BuiltVersion = mapCacheVersion; return; }

            var first = nodes[0];
            var (incoming, _) = FindPathToNearestCompleted(first);
            t.Segments.Add(incoming ?? new List<Node> { first });
            t.ResolvedStops.Add(first);

            Node anchor = first;
            for (int i = 1; i < nodes.Count; i++)
            {
                var path = FindPath(anchor, nodes[i]);
                if (path == null) { t.Skipped.Add(nodes[i].Name ?? $"(stop {i + 1})"); continue; }
                t.Segments.Add(path);
                t.ResolvedStops.Add(nodes[i]);
                anchor = nodes[i];
            }

            t.BuiltVersion = mapCacheVersion;
        }
        catch (Exception e)
        {
            LogError("Error building tour: " + e.Message);
            t.Segments = new();
            t.ResolvedStops = new();
            // Mark built against the current version so a persistent failure retries on the next cache
            // refresh, not every frame (BuiltVersion = -1 would re-run this multi-BFS + LogError per frame).
            t.BuiltVersion = mapCacheVersion;
        }
    }

    // Greedy nearest-neighbor reorder of a tour's stops: first = nearest to the explored frontier,
    // each next = shortest FindPath from the current. Unresolvable stops keep their relative order
    // appended at the end. Writes the new order back to t.Stops and rebuilds.
    private void OptimizeTour(Tour t)
    {
        try
        {
            var resolvable = new List<Node>();
            var unresolved = new List<TourStop>();
            foreach (var s in t.Stops)
            {
                var node = ResolveStop(s);
                if (node != null) resolvable.Add(node);
                else unresolved.Add(s);
            }
            if (resolvable.Count <= 1) { t.BuiltVersion = -1; BuildTour(t); return; }

            var stepCounts = ComputeStepCounts();
            int Steps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

            var remaining = resolvable.OrderBy(Steps).ToList();
            var ordered = new List<Node>();

            Node current = remaining.FirstOrDefault(n => Steps(n) != int.MaxValue) ?? remaining[0];
            ordered.Add(current);
            remaining.Remove(current);

            while (remaining.Count > 0)
            {
                List<Node> bestPath = null;
                Node bestNode = null;
                foreach (var cand in remaining)
                {
                    var path = FindPath(current, cand);
                    if (path == null) continue;
                    if (bestPath == null || path.Count < bestPath.Count) { bestPath = path; bestNode = cand; }
                }
                // Unreachable from current: fall back to nearest-to-frontier among the rest.
                if (bestNode == null) bestNode = remaining.OrderBy(Steps).First();
                ordered.Add(bestNode);
                remaining.Remove(bestNode);
                current = bestNode;
            }

            t.Stops = ordered.Select(n => new TourStop { X = n.Coordinates.X, Y = n.Coordinates.Y }).ToList();
            t.Stops.AddRange(unresolved);
            t.BuiltVersion = -1;
            BuildTour(t);
        }
        catch (Exception e) { LogError("Error optimizing tour: " + e.Message); }
    }

    // ---- Drawing ----

    private void DrawTours()
    {
        if (!Settings.Tours.ShowTours) return;

        foreach (var t in Settings.Tours.Tours.Values)
        {
            if (!t.Show || t.Stops.Count == 0) continue;

            // Rebuild on first draw and whenever the atlas changed (stale Node references).
            if (t.BuiltVersion != mapCacheVersion) BuildTour(t);

            try
            {
                foreach (var seg in t.Segments)
                    DrawPath(seg, t.Color);

                // First not-yet-completed stop in route order gets a Target marker.
                Node nextStop = Settings.Tours.ShowNextStopTarget
                    ? t.ResolvedStops.FirstOrDefault(n => n != null && !n.IsVisited && !n.IsCompleted)
                    : null;

                for (int i = 0; i < t.ResolvedStops.Count; i++)
                {
                    var node = t.ResolvedStops[i];
                    if (node?.MapNode?.Element == null) continue;
                    Vector2 center; float width, height;
                    try { var rect = node.MapNode.Element.GetClientRect(); center = rect.Center; width = rect.Right - rect.Left; height = rect.Bottom - rect.Top; }
                    catch (Exception e) { DebugSwallow("DrawTours: stop rect", e); continue; }

                    if (ReferenceEquals(node, nextStop) && customIconsLoaded)
                    {
                        // Larger base than a node icon so it reads as the "go here next" marker.
                        float baseSize = width * 0.9f * Settings.Graphics.NodeRadius;
                        // Pulse the size ~±18% on a ~1.1s cycle so it draws the eye. High-res wall-clock
                        // (not DateTime.Now, whose ~15ms resolution steps at high FPS).
                        double secs = AnimSeconds;
                        float pulse = 1f + 0.18f * (float)Math.Sin(secs * (Math.PI * 2.0 / 1.1));
                        float size = baseSize * pulse;
                        // Float the marker above the node, clear of the number/art. When the node has
                        // content, the in-game atlas floats a circular content icon above it (sized to
                        // the node). Bump the Target up by ~a node height so it clears that icon.
                        float topY = center.Y - height / 2f - baseSize / 2f - 4f;
                        if (node.Content.Count > 0)
                            topY -= height;
                        DrawNodeSprite(new Vector2(center.X, topY), size, size, SpriteIcon.Target, t.Color, allowFlatten: false);
                    }

                    DrawCenteredTextWithBackground((i + 1).ToString(), center,
                        Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 8, 4);
                }
            }
            catch (Exception e) { DebugSwallow("DrawTours", e); }
        }
    }

    // ---- Build Mode ----

    // Per-frame Build Mode handling. Finds the node under/near the cursor, rings it (green = will add,
    // red = already a stop / will remove), and places an invisible ImGui button over it so the click
    // is captured by the overlay (WantCaptureMouse) and never reaches the game. When the cursor isn't
    // over a node we emit nothing, so empty-space click-drag still pans the atlas camera.
    private void HandleBuildMode()
    {
        try
        {
            var cursor = ImGui.GetMousePos();

            bool hasBest = false;
            Vector2i bestCoord = default;
            ExileCore2.Shared.RectangleF bestRect = default;
            float bestDistSq = float.MaxValue;
            foreach (var d in AtlasPanel.Descriptions)
            {
                // GetClientRectCache (cheap struct read) + squared distance instead of a per-node
                // GetClientRect parent-chain walk + sqrt over all ~1000 descriptions every frame.
                ExileCore2.Shared.RectangleF r;
                try { r = d.Element.GetClientRectCache; } catch { continue; }
                float distSq = Vector2.DistanceSquared(cursor, r.Center);
                if (distSq < bestDistSq) { bestDistSq = distSq; bestCoord = d.Coordinate; bestRect = r; hasBest = true; }
            }
            if (!hasBest) return;

            // "On / very near" test: cursor inside the node rect inflated by a margin. Outside this,
            // leave the click alone so it reaches the game (camera drag).
            float margin = bestRect.Width * 0.35f;
            bool near = cursor.X >= bestRect.Left - margin && cursor.X <= bestRect.Right + margin
                     && cursor.Y >= bestRect.Top - margin && cursor.Y <= bestRect.Bottom + margin;
            if (!near) return;

            Node node;
            lock (mapCacheLock)
                mapCache.TryGetValue(bestCoord, out node);
            if (node == null) return;

            var active = GetActiveTour();
            bool isStop = active != null &&
                active.Stops.FindIndex(s => s.X == node.Coordinates.X && s.Y == node.Coordinates.Y) >= 0;
            Color ringColor = isStop ? Color.FromArgb(255, 235, 80, 80) : Color.FromArgb(255, 80, 230, 120);
            Graphics.DrawCircle(bestRect.Center, bestRect.Width * 0.6f, ringColor, 3f, 32);

            // Invisible button over the (inflated) node rect captures left/right clicks. Borderless,
            // no-background window mirrors DrawPanelButtonBar.
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                      | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing;

            Vector2 winPos = new(bestRect.Left - margin, bestRect.Top - margin);
            Vector2 winSize = new(bestRect.Width + margin * 2f, bestRect.Height + margin * 2f);
            ImGui.SetNextWindowPos(winPos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(winSize, ImGuiCond.Always);
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
            if (ImGui.Begin("##buildmodecapture", flags))
            {
                ImGui.InvisibleButton("##buildmodenode", winSize);
                if (ImGui.IsItemHovered()) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) AddStopToActiveTourIfAbsent(node);
                if (ImGui.IsItemClicked(ImGuiMouseButton.Right)) RemoveStopFromActiveTour(node);
            }
            ImGui.End();
            ImGui.PopStyleVar();
        }
        catch (Exception e) { DebugSwallow("HandleBuildMode", e); }
    }

    // Top-center banner shown while Build Mode is active.
    private void DrawBuildModeIndicator()
    {
        try
        {
            var active = GetActiveTour();
            string tourName = active?.Name ?? "(new tour)";
            string exitKey = Settings.Keybinds.BuildModeExitHotkey.Value.ToString();
            string text = $"BUILD MODE  -  {tourName}    L-click add  |  R-click remove  |  {exitKey} exit";
            Color accent = active != null ? active.Color : Color.FromArgb(255, 80, 230, 120);

            var size = ImGui.CalcTextSize(text);
            const float padX = 12f, padY = 6f;
            Vector2 boxSize = new(size.X + padX * 2f, size.Y + padY * 2f);
            var r = cachedScreenRect;
            Vector2 pos = new(r.Center.X - boxSize.X / 2f, r.Top + 48f);

            Graphics.DrawBox(pos, pos + boxSize, Color.FromArgb(210, 0, 0, 0), 5f);
            Graphics.DrawBox(pos, new Vector2(pos.X + 4f, pos.Y + boxSize.Y), accent, 0f);
            Graphics.DrawText(text, pos + new Vector2(padX, padY), Color.White);
        }
        catch (Exception e) { DebugSwallow("DrawBuildModeIndicator", e); }
    }

    // ---- Mutation helpers ----

    private Tour GetActiveTour()
    {
        var id = Settings.Tours.ActiveTourId;
        if (!string.IsNullOrEmpty(id) && Settings.Tours.Tours.TryGetValue(id, out var t)) return t;
        return null;
    }

    private Tour AddTour()
    {
        var t = new Tour
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Tour {Settings.Tours.Tours.Count + 1}",
            Color = GetNextTourColor(),
        };
        Settings.Tours.Tours.Add(t.Id, t);
        Settings.Tours.ActiveTourId = t.Id;
        return t;
    }

    private void RemoveTour(Tour t)
    {
        if (t == null) return;
        Settings.Tours.Tours.Remove(t.Id);
        if (Settings.Tours.ActiveTourId == t.Id)
            Settings.Tours.ActiveTourId = Settings.Tours.Tours.Keys.FirstOrDefault() ?? "";
    }

    // Toggle a node in the active tour (add if absent, remove if present). Auto-creates a first
    // tour if none is active so the hotkey works out of the box.
    private void AddStopToActiveTour(Node node)
    {
        if (node == null) return;
        var t = GetActiveTour() ?? AddTour();
        int x = node.Coordinates.X, y = node.Coordinates.Y;
        int idx = t.Stops.FindIndex(s => s.X == x && s.Y == y);
        if (idx >= 0) t.Stops.RemoveAt(idx);
        else t.Stops.Add(new TourStop { X = x, Y = y });
        t.BuiltVersion = -1;
    }

    // Build Mode left-click: add the node to the active tour if it isn't already a stop. Auto-creates
    // a tour if none is active so the first click just works.
    private void AddStopToActiveTourIfAbsent(Node node)
    {
        if (node == null) return;
        var t = GetActiveTour() ?? AddTour();
        int x = node.Coordinates.X, y = node.Coordinates.Y;
        if (t.Stops.FindIndex(s => s.X == x && s.Y == y) >= 0) return;
        t.Stops.Add(new TourStop { X = x, Y = y });
        t.BuiltVersion = -1;
    }

    // Build Mode right-click: remove the node from the active tour if present.
    private void RemoveStopFromActiveTour(Node node)
    {
        if (node == null) return;
        var t = GetActiveTour();
        if (t == null) return;
        int x = node.Coordinates.X, y = node.Coordinates.Y;
        int idx = t.Stops.FindIndex(s => s.X == x && s.Y == y);
        if (idx < 0) return;
        t.Stops.RemoveAt(idx);
        t.BuiltVersion = -1;
    }

    // Same hue-spreading approach as GetNextWaypointColor, scanning tour colors instead.
    private Color GetNextTourColor()
    {
        var used = new HashSet<int>();
        foreach (var t in Settings.Tours.Tours.Values)
            used.Add(t.Color.ToArgb());

        for (int tier = 0; tier < 6; tier++)
        {
            int count = 8 + tier * 8;
            float sat = (tier % 2 == 0) ? 0.85f : 0.6f;
            float val = (tier < 2) ? 1.0f : 0.8f;
            float hueOffset = tier * 15f;

            var candidates = new List<Color>();
            for (int i = 0; i < count; i++)
            {
                var c = ColorUtils.ColorFromHSV((360f * i / count) + hueOffset, sat, val);
                if (!used.Contains(c.ToArgb())) candidates.Add(c);
            }
            if (candidates.Count > 0)
                return candidates[waypointColorRng.Next(candidates.Count)];
        }
        return ColorUtils.ColorFromHSV(waypointColorRng.Next(360), 0.8f, 1.0f);
    }

    // ---- Auto Create Tour ----

    private static bool NodeMatchesContent(Node node, HashSet<string> selected)
    {
        foreach (var c in node.Content.Values)
            if (c.Name != null && selected.Contains(c.Name)) return true;
        return false;
    }

    // Center within the (inflated) screen bounds. Off-screen-but-loaded nodes can still qualify;
    // far nodes have no valid element and are excluded, capping sprawl.
    private static bool CenterInBounds(Node node, float minX, float minY, float maxX, float maxY)
    {
        try
        {
            var el = node.MapNode?.Element;
            if (el == null) return false;
            var ctr = el.GetClientRect().Center;
            return ctr.X >= minX && ctr.X <= maxX && ctr.Y >= minY && ctr.Y <= maxY;
        }
        catch { return false; }
    }

    private static string AutoTourName(List<string> selected)
    {
        string joined = string.Join(", ", selected.Take(3));
        if (selected.Count > 3) joined += $" +{selected.Count - 3}";
        return $"Auto: {joined}";
    }

    // Build a tour from on/near-screen unvisited nodes carrying any selected content. Starts at the
    // matching node nearest the explored frontier, then greedily chains to the nearest remaining
    // match within AutoTourStepRange steps until none remain in range.
    private void AutoCreateTour()
    {
        try
        {
            var selected = new HashSet<string>(Settings.Tours.AutoTourContent ?? new List<string>());
            if (selected.Count == 0) return;

            int n = Settings.Tours.AutoTourStepRange;
            float marginPct = Settings.Tours.AutoTourScreenMarginPct / 100f;
            var r = cachedScreenRect;
            float mx = r.Width * marginPct, my = r.Height * marginPct;
            float minX = r.Left - mx, maxX = r.Right + mx, minY = r.Top - my, maxY = r.Bottom + my;

            var stepCounts = ComputeStepCounts();
            int Steps(Node node) => stepCounts.TryGetValue(node.Coordinates, out var s) ? s : int.MaxValue;

            bool onlyAtlasPoints = Settings.Tours.AutoTourOnlyAtlasPoints;
            List<Node> candidates;
            lock (mapCacheLock)
            {
                candidates = mapCache.Values
                    .Where(x => !x.IsVisited
                                && (!onlyAtlasPoints || x.GivesAtlasPoint)
                                && NodeMatchesContent(x, selected)
                                && CenterInBounds(x, minX, minY, maxX, maxY))
                    .ToList();
            }
            if (candidates.Count == 0) { LogMessage("Auto Tour: no matching on-screen content."); return; }

            // Start: nearest match to the explored frontier (reachable preferred).
            var start = candidates.Where(c => Steps(c) != int.MaxValue).OrderBy(Steps).FirstOrDefault()
                        ?? candidates.OrderBy(Steps).First();

            var chain = new List<Node> { start };
            var remaining = candidates.Where(c => !ReferenceEquals(c, start)).ToList();
            Node current = start;
            while (remaining.Count > 0)
            {
                Node best = null;
                int bestSteps = int.MaxValue;
                foreach (var cand in remaining)
                {
                    var path = FindPath(current, cand);
                    if (path == null) continue;
                    int steps = path.Count - 1;
                    if (steps > n) continue;                 // out of range
                    if (steps < bestSteps) { bestSteps = steps; best = cand; }
                }
                if (best == null) break;                     // no valid content within N steps
                chain.Add(best);
                remaining.Remove(best);
                current = best;
            }

            var tour = new Tour
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = AutoTourName(selected.ToList()),
                Color = GetNextTourColor(),
                Stops = chain.Select(node => new TourStop { X = node.Coordinates.X, Y = node.Coordinates.Y }).ToList(),
            };
            Settings.Tours.Tours.Add(tour.Id, tour);
            Settings.Tours.ActiveTourId = tour.Id;
            BuildTour(tour);
            toursPanelOpen = true;
        }
        catch (Exception e) { LogError("Error auto-creating tour: " + e.Message); }
    }

    // ---- UI ----

    private string StopLabel(TourStop s)
    {
        var node = ResolveStop(s);
        if (node != null && !string.IsNullOrEmpty(node.Name)) return $"{node.Name} ({s.X},{s.Y})";
        return $"({s.X},{s.Y})";
    }

    // Auto Create Tour section: pick content (same list as the Atlas Overview), then build a tour
    // from the matching on/near-screen nodes.
    private void DrawAutoTourSection()
    {
        if (!ImGui.CollapsingHeader("Auto Create Tour")) return;

        var stats = ComputeAtlasStats();
        if (stats.ContentCounts.Count == 0)
        {
            ImGui.TextDisabled("No reachable content. Open the atlas or widen the\nReachable Step Window in Atlas Overview.");
            ImGui.Separator();
            return;
        }

        var sel = Settings.Tours.AutoTourContent;
        ImGui.TextDisabled("Pick content to route through:");
        foreach (var kv in stats.ContentCounts.OrderByDescending(x => x.Value.count))
        {
            var c = kv.Value.color;
            ImGui.ColorButton($"##auc_{kv.Key}", new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f),
                ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, new Vector2(14, 14));
            ImGui.SameLine();
            bool on = sel.Contains(kv.Key);
            if (ImGui.Checkbox($"{kv.Key} ({kv.Value.count})##autosel_{kv.Key}", ref on))
            {
                if (on) { if (!sel.Contains(kv.Key)) sel.Add(kv.Key); }
                else sel.Remove(kv.Key);
            }
        }

        // Selected content that is no longer reachable (e.g. removed quest content like Great Beast)
        // won't appear in ContentCounts above, leaving it stuck selected. Render it here so it can
        // still be unchecked.
        var orphaned = sel.Where(k => !stats.ContentCounts.ContainsKey(k)).ToList();
        foreach (var key in orphaned)
        {
            bool on = true;
            if (ImGui.Checkbox($"{key} (unavailable)##autosel_{key}", ref on))
            {
                if (!on) sel.Remove(key);
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("No longer reachable on the atlas. Uncheck to remove.");
        }

        ImGui.Spacing();
        int n = Settings.Tours.AutoTourStepRange;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Max steps between stops", ref n, 1, 10)) Settings.Tours.AutoTourStepRange.Value = n;

        int m = Settings.Tours.AutoTourScreenMarginPct;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Off-screen margin %", ref m, 0, 200)) Settings.Tours.AutoTourScreenMarginPct.Value = m;

        bool onlyPoints = Settings.Tours.AutoTourOnlyAtlasPoints;
        if (ImGui.Checkbox("Only atlas-point content", ref onlyPoints)) Settings.Tours.AutoTourOnlyAtlasPoints.Value = onlyPoints;
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Only route through nodes whose content awards an atlas point.");

        if (ImGui.Button("Create Auto Tour")) AutoCreateTour();
        ImGui.SameLine();
        ImGui.TextDisabled($"{sel.Count} selected");
        ImGui.Separator();
    }

    private void DrawToursPanel()
    {
        try
        {
            bool justOpened = !toursPanelWasOpen; toursPanelWasOpen = true;
            BeginPersistedWindow(Settings.Tours.PanelRect, justOpened, screenCenter, new Vector2(440, 480));
            ImGui.SetNextWindowSizeConstraints(new Vector2(360, 240), new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.9f);

            if (ImGui.Begin("Tours###tourspanel", ref toursPanelOpen, ImGuiWindowFlags.NoCollapse))
            {
                SavePersistedWindow(Settings.Tours.PanelRect);

                bool showAll = Settings.Tours.ShowTours;
                if (ImGui.Checkbox("Show Tours", ref showAll)) Settings.Tours.ShowTours.Value = showAll;
                ImGui.SameLine();
                if (ImGui.Button("Add Tour")) AddTour();
                ImGui.SameLine();
                if (buildModeActive) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.55f, 0.30f, 1f));
                if (ImGui.Button(buildModeActive ? $"Building... ({Settings.Keybinds.BuildModeExitHotkey.Value})" : "Build Mode")) buildModeActive = !buildModeActive;
                if (buildModeActive) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Left-click atlas nodes to add stops to the active tour; right-click removes. Press the Build Mode exit key (default Tab) to exit.");
                ImGui.Separator();

                DrawAutoTourSection();

                if (Settings.Tours.Tours.Count == 0)
                    ImGui.TextDisabled("No tours yet. Add one, then add stops with the Add Tour Stop hotkey\n(hover a node) or the active tour's controls.");

                Tour toDelete = null;
                // Snapshot so deletes/reorders don't invalidate enumeration.
                foreach (var t in Settings.Tours.Tours.Values.ToList())
                {
                    ImGui.PushID(t.Id);

                    bool show = t.Show;
                    if (ImGui.Checkbox("##show", ref show)) { t.Show = show; t.BuiltVersion = -1; }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Show this tour's route on the atlas.");
                    ImGui.SameLine();

                    var col = new Vector4(t.Color.R / 255f, t.Color.G / 255f, t.Color.B / 255f, 1f);
                    if (ImGui.ColorEdit4("##color", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                        t.Color = Color.FromArgb(255, (int)(col.X * 255), (int)(col.Y * 255), (int)(col.Z * 255));
                    ImGui.SameLine();

                    bool active = Settings.Tours.ActiveTourId == t.Id;
                    if (ImGui.RadioButton("##active", active)) Settings.Tours.ActiveTourId = t.Id;
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Active tour: the Add Tour Stop hotkey targets this one.");
                    ImGui.SameLine();

                    ImGui.SetNextItemWidth(170);
                    string name = t.Name ?? "";
                    if (ImGui.InputText("##name", ref name, 32)) t.Name = name;
                    ImGui.SameLine();
                    ImGui.TextDisabled($"{t.Stops.Count} stops");

                    if (ImGui.Button("Optimize")) OptimizeTour(t);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reorder stops by shortest route from your explored frontier, then rebuild.");
                    ImGui.SameLine();
                    if (ImGui.Button("Delete")) toDelete = t;

                    if (t.Skipped.Count > 0)
                        ImGui.TextDisabled($"Skipped: {string.Join(", ", t.Skipped)}");

                    if (t.Stops.Count > 0 && ImGui.TreeNode($"Stops##{t.Id}"))
                    {
                        int moveFrom = -1, moveTo = -1, removeAt = -1;
                        for (int i = 0; i < t.Stops.Count; i++)
                        {
                            ImGui.PushID(i);
                            // Buttons first so they stay left-aligned regardless of label length.
                            if (ImGui.SmallButton("^")) { moveFrom = i; moveTo = i - 1; }
                            ImGui.SameLine();
                            if (ImGui.SmallButton("v")) { moveFrom = i; moveTo = i + 1; }
                            ImGui.SameLine();
                            if (ImGui.SmallButton("x")) removeAt = i;
                            ImGui.SameLine();
                            ImGui.TextUnformatted($"{i + 1}. {StopLabel(t.Stops[i])}");
                            ImGui.PopID();
                        }
                        if (moveFrom >= 0 && moveTo >= 0 && moveTo < t.Stops.Count)
                        {
                            var tmp = t.Stops[moveFrom];
                            t.Stops.RemoveAt(moveFrom);
                            t.Stops.Insert(moveTo, tmp);
                            t.BuiltVersion = -1;
                        }
                        if (removeAt >= 0) { t.Stops.RemoveAt(removeAt); t.BuiltVersion = -1; }
                        ImGui.TreePop();
                    }

                    ImGui.Separator();
                    ImGui.PopID();
                }
                if (toDelete != null) RemoveTour(toDelete);

                if (ImGui.Button("Close##tours")) toursPanelOpen = false;
            }
            ImGui.End();
        }
        catch (Exception e)
        {
            LogError("Error drawing tours panel: " + e.Message);
            toursPanelOpen = false;
        }
    }

    // Floating textured button bar at the bottom-center of the atlas to open the three atlas panels.
    // Each button swaps normal/hover/pressed textures and shows a textured tooltip plaque on hover.
    private void DrawPanelButtonBar()
    {
        if (!Settings.Features.ShowPanelButtons) return;
        if (panelButtons == null || panelButtons.Count == 0) return;

        const float gap = 6f, pad = 10f, bottomMargin = 64f, hoverLift = 5f;
        int count = panelButtons.Count;
        // Hit rect = NormalSize. Pressed sprite is the same size, centered over the same hit rect.
        var btnSz = panelButtons[0].NormalSize;
        float barW = count * btnSz.X + (count - 1) * gap;
        float winW = barW + pad * 2f, winH = btnSz.Y + pad * 2f;

        try
        {
            var r = cachedScreenRect;
            Vector2 pos = new(r.Center.X - winW / 2f, r.Bottom - winH - bottomMargin);

            ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
            ImGui.SetNextWindowSize(new Vector2(winW, winH), ImGuiCond.Always);

            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                      | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing;

            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(pad, pad));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(gap, 0));

            try
            {
                if (ImGui.Begin("##panelbuttons", flags))
                {
                    var dl = ImGui.GetWindowDrawList();
                    PanelButtonDef? hoveredDef = null;
                    Vector2 hMin = default, hMax = default;

                    for (int i = 0; i < count; i++)
                    {
                        var def = panelButtons[i];
                        ImGui.InvisibleButton(def.Key, def.NormalSize);

                        bool hovered = ImGui.IsItemHovered();
                        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                        bool held = ImGui.IsItemActive();
                        if (ImGui.IsItemClicked()) def.Toggle();
                        bool open = def.GetOpen();

                        var min = ImGui.GetItemRectMin();
                        var max = ImGui.GetItemRectMax();
                        Vector2 center = (min + max) * 0.5f;

                        IntPtr tex;
                        Vector2 drawMin, drawMax;
                        if (held || open)
                        {
                            // Pressed sprite, centered over the hit rect.
                            tex = def.Pressed;
                            var half = def.PressedSize * 0.5f;
                            drawMin = center - half;
                            drawMax = center + half;
                        }
                        else if (hovered)
                        {
                            // Lift the button upward on hover (hit rect stays put).
                            tex = def.Hover;
                            var half = def.NormalSize * 0.5f;
                            drawMin = center - half; drawMin.Y -= hoverLift;
                            drawMax = center + half; drawMax.Y -= hoverLift;
                        }
                        else
                        {
                            tex = def.Normal;
                            drawMin = min; drawMax = max;
                        }

                        if (tex != IntPtr.Zero) dl.AddImage(tex, drawMin, drawMax);

                        if (hovered) { hoveredDef = def; hMin = min; hMax = max; }
                        if (i < count - 1) ImGui.SameLine();
                    }

                    // Tooltip plaque centered above the hovered button, on the foreground layer so it
                    // isn't clipped by the (small) window.
                    if (hoveredDef.HasValue && hoveredDef.Value.Tooltip != IntPtr.Zero)
                    {
                        var d = hoveredDef.Value;
                        const float th = 39f;   // native tooltip height
                        float tw = th * (d.TooltipSize.X / d.TooltipSize.Y);
                        float cx = (hMin.X + hMax.X) / 2f;
                        float bottom = hMin.Y - hoverLift + 4f;   // sit just above the lifted button
                        Vector2 tMin = new(cx - tw / 2f, bottom - th);
                        Vector2 tMax = new(cx + tw / 2f, bottom);
                        ImGui.GetForegroundDrawList().AddImage(d.Tooltip, tMin, tMax);
                    }
                }
                ImGui.End();
            }
            finally { ImGui.PopStyleVar(2); }
        }
        catch (Exception e) { DebugSwallow("DrawPanelButtonBar", e); }
    }
}
