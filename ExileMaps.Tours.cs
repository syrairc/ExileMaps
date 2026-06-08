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
        panelButtons = new List<PanelButtonDef>
        {
            new PanelButtonDef {
                Key = "wp",
                Normal = L("waypoints-normal.png"), Hover = L("waypoints-hover.png"),
                Pressed = L("waypoints-pressed.png"), Tooltip = L("waypoints-tooltip.png"),
                TooltipSize = new Vector2(169, 82),
                GetOpen = () => WaypointPanelIsOpen, Toggle = () => WaypointPanelIsOpen = !WaypointPanelIsOpen,
            },
            new PanelButtonDef {
                Key = "tours",
                Normal = L("tours-normal.png"), Hover = L("tours-hover.png"),
                Pressed = L("tours-pressed.png"), Tooltip = L("tours-tooltip.png"),
                TooltipSize = new Vector2(133, 82),
                GetOpen = () => toursPanelOpen, Toggle = () => toursPanelOpen = !toursPanelOpen,
            },
            new PanelButtonDef {
                Key = "atlas",
                Normal = L("atlas-normal.png"), Hover = L("atlas-hover.png"),
                Pressed = L("atlas-pressed.png"), Tooltip = L("atlas-tooltip.png"),
                TooltipSize = new Vector2(215, 83),
                GetOpen = () => atlasOverviewOpen, Toggle = () => atlasOverviewOpen = !atlasOverviewOpen,
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
            t.BuiltVersion = -1;
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

                for (int i = 0; i < t.ResolvedStops.Count; i++)
                {
                    var node = t.ResolvedStops[i];
                    if (node?.MapNode?.Element == null) continue;
                    Vector2 center;
                    try { center = node.MapNode.Element.GetClientRect().Center; }
                    catch (Exception e) { DebugSwallow("DrawTours: stop rect", e); continue; }
                    DrawCenteredTextWithBackground((i + 1).ToString(), center,
                        Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 8, 4);
                }
            }
            catch (Exception e) { DebugSwallow("DrawTours", e); }
        }
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

            List<Node> candidates;
            lock (mapCacheLock)
            {
                candidates = mapCache.Values
                    .Where(x => !x.IsVisited
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

        ImGui.Spacing();
        int n = Settings.Tours.AutoTourStepRange;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Max steps between stops", ref n, 1, 10)) Settings.Tours.AutoTourStepRange.Value = n;

        int m = Settings.Tours.AutoTourScreenMarginPct;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Off-screen margin %", ref m, 0, 200)) Settings.Tours.AutoTourScreenMarginPct.Value = m;

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
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Reorder stops by shortest route from your explored frontier.");
                    ImGui.SameLine();
                    if (ImGui.Button("Build")) { t.BuiltVersion = -1; BuildTour(t); }
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

        const float btn = 140f, gap = 12f, pad = 10f, bottomMargin = 64f, pressedGrow = 12f, hoverLift = 14f;
        int count = panelButtons.Count;
        float barW = count * btn + (count - 1) * gap;
        float winW = barW + pad * 2f, winH = btn + pad * 2f;

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

            if (ImGui.Begin("##panelbuttons", flags))
            {
                var dl = ImGui.GetWindowDrawList();
                PanelButtonDef? hoveredDef = null;
                Vector2 hMin = default, hMax = default;

                for (int i = 0; i < count; i++)
                {
                    var def = panelButtons[i];
                    ImGui.InvisibleButton(def.Key, new Vector2(btn, btn));

                    bool hovered = ImGui.IsItemHovered();
                    if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                    bool held = ImGui.IsItemActive();
                    if (ImGui.IsItemClicked()) def.Toggle();
                    bool open = def.GetOpen();

                    var min = ImGui.GetItemRectMin();
                    var max = ImGui.GetItemRectMax();

                    IntPtr tex;
                    Vector2 drawMin = min, drawMax = max;
                    if (held || open)
                    {
                        // Pressed sits grounded with a slightly larger glow.
                        tex = def.Pressed;
                        drawMin -= new Vector2(pressedGrow); drawMax += new Vector2(pressedGrow);
                    }
                    else if (hovered)
                    {
                        // Lift the button upward on hover (hit rect stays put).
                        tex = def.Hover;
                        drawMin.Y -= hoverLift; drawMax.Y -= hoverLift;
                    }
                    else tex = def.Normal;

                    if (tex != IntPtr.Zero) dl.AddImage(tex, drawMin, drawMax);

                    if (hovered) { hoveredDef = def; hMin = min; hMax = max; }
                    if (i < count - 1) ImGui.SameLine();
                }

                // Tooltip plaque centered above the hovered button, on the foreground layer so it
                // isn't clipped by the (small) window.
                if (hoveredDef.HasValue && hoveredDef.Value.Tooltip != IntPtr.Zero)
                {
                    var d = hoveredDef.Value;
                    const float th = 92f;
                    float tw = th * (d.TooltipSize.X / d.TooltipSize.Y);
                    float cx = (hMin.X + hMax.X) / 2f;
                    float bottom = hMin.Y - hoverLift + 4f;   // sit just above the lifted button
                    Vector2 tMin = new(cx - tw / 2f, bottom - th);
                    Vector2 tMax = new(cx + tw / 2f, bottom);
                    ImGui.GetForegroundDrawList().AddImage(d.Tooltip, tMin, tMax);
                }
            }
            ImGui.End();
            ImGui.PopStyleVar(2);
        }
        catch (Exception e) { DebugSwallow("DrawPanelButtonBar", e); }
    }
}
