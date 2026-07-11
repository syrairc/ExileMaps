// Expeditions: rumour-data load, panel, highlight, waypoint. See docs/superpowers/specs/2026-07-09-expeditions-feature-design.md
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using GameOffsets2.Native;
using ImGuiNET;
using ExileMaps.Classes;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Shape of one json/rumors.json entry.
    private class RumorDef { public string content { get; set; } public string description { get; set; } public float defaultWeight { get; set; } }

    // Load the static rumour->content map and merge into the persisted RumorWeights, keeping any
    // weight the user already tuned (mirrors UpdateContentData's merge).
    public void UpdateRumorData()
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "json", "rumors.json");
            if (!File.Exists(path)) { LogError($"rumors.json missing: {path}"); return; }

            var defs = JsonSerializer.Deserialize<Dictionary<string, RumorDef>>(File.ReadAllText(path));
            if (defs == null) return;

            foreach (var (text, def) in defs)
            {
                if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var existing))
                {
                    // keep the user's Weight, refresh the metadata from json
                    existing.Text = text;
                    existing.Content = def.content;
                    existing.Description = def.description;
                }
                else
                {
                    Settings.Expeditions.RumorWeights.TryAdd(text, new Rumor
                    {
                        Text = text, Content = def.content, Description = def.description, Weight = def.defaultWeight
                    });
                }
            }
        }
        catch (Exception e) { LogError($"UpdateRumorData failed: {e.Message}"); }
    }

    // Rebuild the expeditions snapshot from AtlasPanel.Buttons. Called inside RefreshMapCache.
    // The Buttons list mixes Towers (ButtonType.Id != "Ocean", no rumours) with expeditions, and
    // has 4-8 Ocean buttons per region - one candidate spawn node each, all sharing the region's
    // rumour pool. Exactly one per region has IsVisible==true: the current spawn. So: keep Ocean
    // only, one Expedition per region, and take the visible button's coord as the spawn.
    private void SnapshotExpeditions()
    {
        var built = new List<Classes.Expedition>();
        var byRegion = new Dictionary<Vector2i, Classes.Expedition>();
        try
        {
            var buttons = AtlasPanel?.Buttons;
            if (buttons != null)
            {
                foreach (var b in buttons)
                {
                    if (b?.ButtonType?.Id != "Ocean") continue; // drop Towers / non-expedition buttons

                    var region = b.RegionCoordinate;
                    if (!byRegion.TryGetValue(region, out var exp))
                    {
                        exp = new Classes.Expedition { RegionCoord = region, SpawnCoord = b.Coordinate };
                        var nodes = b.RegionNodes;
                        if (nodes != null)
                            foreach (var n in nodes)
                                exp.MapCoords.Add(n.Coordinate);
                        var rumors = b.Rumors;
                        if (rumors != null)
                            foreach (var (k, v) in rumors)
                                exp.Rumors[k] = v;
                        byRegion[region] = exp;
                        built.Add(exp);
                    }

                    // Every Ocean button is a candidate spawn location; record each so we can mark them all.
                    exp.ButtonCoords.Add(b.Coordinate);

                    // The visible button is the real current spawn; prefer its coord over the first seen.
                    if (b.IsVisible)
                        exp.SpawnCoord = b.Coordinate;
                }
            }
        }
        catch (Exception e) { LogError($"SnapshotExpeditions failed: {e.Message}"); }

        // Stable ids: order by region coord so the same expedition keeps its id (and tint) across refreshes.
        built = built.OrderBy(x => x.RegionCoord.X).ThenBy(x => x.RegionCoord.Y).ToList();
        for (int i = 0; i < built.Count; i++) built[i].Id = i + 1;

        lock (mapCacheLock)
            expeditions = built;
    }

    // true once at least one expedition made it into the last cache refresh.
    private bool ExpeditionsLoaded()
    {
        lock (mapCacheLock) return expeditions.Count > 0;
    }

    // Sort score = sum(weight * count) over the expedition's rumours, using live weights.
    private float ExpeditionScore(Classes.Expedition e)
    {
        float score = 0f;
        foreach (var (text, count) in e.Rumors)
            if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r))
                score += r.Weight * count;
        return score;
    }

    // Label = highest-weight rumour's content + map count. Falls back to region coord if no known rumour.
    private string ExpeditionLabel(Classes.Expedition e)
    {
        string best = null; float bestW = float.NegativeInfinity;
        foreach (var (text, _) in e.Rumors)
            if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r) && r.Weight > bestW)
            { bestW = r.Weight; best = r.Content; }
        best ??= $"Region {e.RegionCoord.X},{e.RegionCoord.Y}";
        return $"{best}  ({e.MapCoords.Count} maps)";
    }

    // matches if search text is empty, or found in a rumour's raw text or its decoded content
    private bool ExpeditionMatchesSearch(Classes.Expedition e, string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var (rumorText, _) in e.Rumors)
        {
            if (rumorText.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (Settings.Expeditions.RumorWeights.TryGetValue(rumorText, out var r)
                && (r.Content?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)) return true;
        }
        return false;
    }

    // Rings on the hovered marker's region maps, in that expedition's tint. Mirrors DrawExpeditionHighlight.
    private void DrawExpeditionHoverRings()
    {
        if (frameHoverExpeditionMaps.Count == 0 || ShowMinimap) return;
        foreach (var (node, rect) in nodePositions)
        {
            if (!frameHoverExpeditionMaps.Contains(node.Coordinates)) continue;
            float radius = MathF.Max(rect.Width, rect.Height) * 0.62f;
            Graphics.DrawCircle(rect.Center, radius, frameHoverExpeditionTint, 3f, 24);
        }
    }

    // Ring on each on-screen node belonging to a highlighted expedition. Mirrors DrawSearchPing's
    // draw call (AtlasOverview.cs) so the two ring styles stay consistent.
    private void DrawExpeditionHighlight()
    {
        if (highlightedExpeditionCoords.Count == 0) return;
        // Minimap mode never repopulates nodePositions; skip like DrawSearchPing does.
        if (ShowMinimap) return;

        Color color = Settings.Expeditions.HighlightColor;
        foreach (var (node, rect) in nodePositions)
        {
            if (!highlightedExpeditionCoords.Contains(node.Coordinates)) continue;
            var center = rect.Center;
            float radius = MathF.Max(rect.Width, rect.Height) * 0.62f;
            Graphics.DrawCircle(center, radius, color, 3f, 24);
        }
    }

    // One laid-out rumour box (marker's possible-rumours, or the popup decode). Positions + text are
    // resolved during LayoutExpeditions so the rects can be excluded before the node/line passes draw.
    private sealed class ExpeditionPanel
    {
        public Vector2 TopLeft;
        public float Width, Height, Pad, LineH;
        public List<(string text, System.Drawing.Color color)> Lines;
    }
    // Placeholder textures (the atlas expedition button, drawn a bit bigger than the node). Loaded as
    // panel-button textures in LoadPanelButtonTextures, so drawable by these names.
    private const string ExpeditionMarkerNormal = "expeditions-normal.png";
    private const string ExpeditionMarkerHover = "expeditions-hover.png";
    private const float ExpeditionMarkerScale = 1.3f; // slightly larger than the node-sized icon

    // Laid out in UpdateScreenBounds, drawn later in DrawExpeditions. Their rects go into
    // cachedExcludeRects so nodes/lines/connections don't bleed through the marker or its panels.
    private readonly List<(RectangleF rect, string tex, System.Drawing.Color tint)> frameExpeditionIcons = new();
    private readonly List<ExpeditionPanel> frameExpeditionPanels = new();
    // Maps of the marker currently under the cursor, ringed in the expedition tint. Set during layout.
    private readonly HashSet<Vector2i> frameHoverExpeditionMaps = new();
    private System.Drawing.Color frameHoverExpeditionTint;

    // Distinct, stable tint per expedition so a region's scattered markers/overlays read as one group.
    // Golden-ratio hue spacing keeps neighbouring ids far apart in colour.
    private static System.Drawing.Color ExpeditionTint(int id)
    {
        float h = (id * 0.61803398875f) % 1f;
        float s = 0.55f, v = 1f, r = v, g = v, b = v;
        h *= 6f;
        int i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        switch (i % 6)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return System.Drawing.Color.FromArgb(255, (int)(r * 255), (int)(g * 255), (int)(b * 255));
    }

    // Resolve this frame's expedition marker icons + rumour panels and register their rects in the
    // exclude set. Runs at the end of UpdateScreenBounds (chrome/tooltip/popup excludes already in),
    // so the icon on-screen test avoids drawing over game chrome and the panels reserve their space.
    // A marker is placed at every candidate button location whose game button is NOT on-screen.
    private void LayoutExpeditions()
    {
        frameExpeditionIcons.Clear();
        frameExpeditionPanels.Clear();
        frameHoverExpeditionMaps.Clear();
        if (ShowMinimap) return;

        // Popup decode panel (beside the game's rumours tooltip), independent of the markers toggle.
        if (Settings.Features.ShowExpeditions && frameLogbookPopup != null)
            LayoutPopupOverlay();

        if (!Settings.Features.ShowExpeditionMarkers) return;

        List<Classes.Expedition> snapshot;
        lock (mapCacheLock) snapshot = expeditions.ToList();
        var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
        var mouse = ImGuiNET.ImGui.GetMousePos();

        // "nearest only" mode needs step counts from the explored frontier (memoized on cache version).
        bool drawAll = Settings.Features.DrawAllExpeditionMarkers;
        var steps = drawAll ? null : ComputeStepCounts();

        foreach (var e in snapshot)
        {
            var tint = ExpeditionTint(e.Id);

            // In nearest-only mode, reduce this expedition's locations to the single closest hidden one.
            IEnumerable<Vector2i> coords = e.ButtonCoords;
            if (!drawAll)
            {
                bool found = false; Vector2i best = default; int bestSteps = int.MaxValue;
                foreach (var c in e.ButtonCoords)
                {
                    if (frameVisibleExpeditionButtonCoords.Contains(c)) continue;
                    int s = steps.TryGetValue(c, out var v) ? v : int.MaxValue;
                    // take the fewest-steps hidden location; fall back to the first if none are reachable.
                    if (!found || s < bestSteps) { found = true; bestSteps = s; best = c; }
                }
                coords = found ? new[] { best } : System.Array.Empty<Vector2i>();
            }

            foreach (var coord in coords)
            {
                if (frameVisibleExpeditionButtonCoords.Contains(coord)) continue; // game button on-screen

                Node node;
                lock (mapCacheLock)
                    if (!mapCache.TryGetValue(coord, out node)) node = null;
                if (node == null) continue;

                var rect = GetNodeRect(node);
                if (rect.IsEmpty || rect.Width <= 0) continue;

                // icon sits just above the node, a bit larger than node-size.
                float size = MathF.Max(rect.Width, rect.Height) * ExpeditionMarkerScale;
                var iconRect = new RectangleF(rect.Center.X - size / 2f, rect.Top - size, size, size);
                if (!IsOnScreen(iconRect.Center)) continue; // don't draw over chrome/tooltips/HUD

                bool hover = iconRect.Contains(mouse);
                frameExpeditionIcons.Add((iconRect, hover ? ExpeditionMarkerHover : ExpeditionMarkerNormal, tint));
                cachedExcludeRects.Add(iconRect);

                if (hover)
                {
                    frameHoverExpeditionMaps.Clear();
                    frameHoverExpeditionMaps.UnionWith(e.MapCoords);
                    frameHoverExpeditionTint = tint;
                }

                LayoutPossibleRumors(e, iconRect, screen, hover, tint);
            }
        }
    }

    // The region's POSSIBLE rumour pool (Button.Rumors, decoded + weight-sorted), boxed above the icon.
    // Names only by default; hovering the marker adds each rumour's explanation line. Header carries the
    // expedition id, tinted to match the marker so same-region markers are easy to group.
    private void LayoutPossibleRumors(Classes.Expedition e, RectangleF iconRect, Vector2 screen, bool hover, System.Drawing.Color tint)
    {
        try
        {
            var rows = new List<(string content, string desc, float w)>();
            foreach (var (text, _) in e.Rumors)
                if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r))
                    rows.Add((r.Content, r.Description, r.Weight));
            if (rows.Count == 0) return;
            rows.Sort((a, b) => b.w.CompareTo(a.w));

            System.Drawing.Color color = Settings.Graphics.FontColor;
            System.Drawing.Color sub = System.Drawing.Color.FromArgb(180, 180, 180, 180);
            var lines = new List<(string, System.Drawing.Color)> { ($"#{e.Id} Possible rumours", tint) };
            foreach (var (content, desc, _) in rows)
            {
                lines.Add(("- " + content, color));
                if (hover && !string.IsNullOrEmpty(desc))
                    lines.Add(("    " + desc, sub));
            }

            const float pad = 6f;
            var (boxW, boxH, lineH) = MeasurePanel(lines, pad);

            // center above the icon; drop below it if it'd run off the top, and clamp horizontally.
            float x = iconRect.Center.X - boxW / 2f;
            float y = iconRect.Top - boxH - 2f;
            x = Math.Clamp(x, 0f, MathF.Max(0f, screen.X - boxW));
            if (y < 0f) y = iconRect.Bottom + 2f;

            RegisterPanel(new Vector2(x, y), boxW, boxH, pad, lineH, lines);
        }
        catch (Exception ex) { LogError($"LayoutPossibleRumors failed: {ex.Message}"); }
    }

    // Measure a box: max line width + count*lineHeight, both plus padding.
    private (float boxW, float boxH, float lineH) MeasurePanel(List<(string text, System.Drawing.Color color)> lines, float pad)
    {
        float lineH = 0f, maxW = 0f;
        foreach (var (text, _) in lines)
        {
            var m = Graphics.MeasureText(text);
            if (m.X > maxW) maxW = m.X;
            if (m.Y > lineH) lineH = m.Y;
        }
        return (maxW + pad * 2f, lines.Count * lineH + pad * 2f, lineH);
    }

    // Record a panel for drawing and reserve its rect in the exclude set.
    private void RegisterPanel(Vector2 tl, float w, float h, float pad, float lineH, List<(string text, System.Drawing.Color color)> lines)
    {
        frameExpeditionPanels.Add(new ExpeditionPanel { TopLeft = tl, Width = w, Height = h, Pad = pad, LineH = lineH, Lines = lines });
        cachedExcludeRects.Add(new RectangleF(tl.X, tl.Y, w, h));
    }

    // Draw the laid-out expedition icons + rumour panels. Runs late (after nodes/labels) so it sits on
    // top; the rects were already excluded during layout so nothing underdraws them.
    private void DrawExpeditions()
    {
        if (frameExpeditionIcons.Count == 0 && frameExpeditionPanels.Count == 0) return;
        try
        {
            var fullUV = new RectangleF(0, 0, 1, 1);
            foreach (var (rect, tex, tint) in frameExpeditionIcons)
                Graphics.DrawImage(tex, rect, fullUV, tint);

            System.Drawing.Color bg = Settings.Graphics.BackgroundColor;
            foreach (var p in frameExpeditionPanels)
            {
                Graphics.DrawBox(p.TopLeft, new Vector2(p.TopLeft.X + p.Width, p.TopLeft.Y + p.Height), bg, 5f);
                float cy = p.TopLeft.Y + p.Pad;
                foreach (var (text, color) in p.Lines)
                {
                    Graphics.DrawText(text, new Vector2(p.TopLeft.X + p.Pad, cy), color);
                    cy += p.LineH;
                }
            }
        }
        catch (Exception e) { LogError($"DrawExpeditions failed: {e.Message}"); }
    }

    // true only if every map in the expedition is currently highlighted (so the checkbox reflects
    // a partial toggle as unchecked rather than silently lying).
    private bool IsExpeditionHighlighted(Classes.Expedition e)
    {
        foreach (var c in e.MapCoords)
            if (!highlightedExpeditionCoords.Contains(c)) return false;
        return e.MapCoords.Count > 0;
    }

    private void SetExpeditionHighlight(Classes.Expedition e, bool on)
    {
        foreach (var c in e.MapCoords)
            if (on) highlightedExpeditionCoords.Add(c);
            else highlightedExpeditionCoords.Remove(c);
    }

    // Drop a waypoint on the expedition map fewest steps from the explored frontier.
    private void WaypointNearestInExpedition(Classes.Expedition e)
    {
        try
        {
            if (e.MapCoords.Count == 0) return;
            var steps = ComputeStepCounts(); // memoized on mapCacheVersion

            Vector2i best = e.MapCoords[0];
            int bestSteps = int.MaxValue;
            foreach (var c in e.MapCoords)
            {
                int s = steps.TryGetValue(c, out var v) ? v : int.MaxValue;
                if (s < bestSteps) { bestSteps = s; best = c; }
            }
            if (bestSteps == int.MaxValue)
                LogError($"WaypointNearestInExpedition: no map in this expedition is reachable from the explored frontier, falling back to first map at {best}");

            Node node;
            lock (mapCacheLock)
                if (!mapCache.TryGetValue(best, out node)) node = null;

            if (node != null) AddWaypoint(node);
            else LogError($"WaypointNearestInExpedition: no cached node at {best}");
        }
        catch (Exception ex) { LogError($"WaypointNearestInExpedition failed: {ex.Message}"); }
    }

    // Per-frame scan of the live expedition buttons (AtlasPanel.Buttons). Records which regions have an
    // on-screen button so our marker/overlay stand down there, and resolves the current rumours popup
    // straight off the hovered button's tooltip. Each AtlasButtonNode is a wrapper; its Children[0] is
    // the visual button that owns .Tooltip (the LogbookRevealPopupBg subtree), so no UI-wide texture
    // hunt is needed. Called from UpdateScreenBounds.
    private void ScanExpeditionButtons()
    {
        frameLogbookPopup = null;
        frameVisibleExpeditionButtonCoords.Clear();
        if (!Settings.Features.ShowExpeditions) return;
        try
        {
            var buttons = AtlasPanel?.Buttons;
            if (buttons == null) return;
            foreach (var b in buttons)
            {
                if (b == null || !b.IsVisible) continue;
                if (b.ButtonType?.Id != "Ocean") continue; // skip Towers
                frameVisibleExpeditionButtonCoords.Add(b.Coordinate);

                // The rumours popup lives on the button visual's tooltip, but that tooltip reports
                // IsVisible even when nothing is shown. The real hover signal is HasShinyHighlight on
                // the button visual (b.Children[0]).
                if (frameLogbookPopup != null) continue;
                var children = b.Children;
                var visual = children != null && children.Count > 0 ? children[0] : null;
                if (visual == null || !visual.HasShinyHighlight) continue;
                var tip = visual.Tooltip;
                if (tip == null || !tip.IsVisible) continue;
                var r = tip.GetClientRect();
                if (r.Width > 0 && r.Height > 0) frameLogbookPopup = tip;
            }
        }
        catch (Exception e) { LogError($"ScanExpeditionButtons failed: {e.Message}"); }
    }

    // Collect non-empty Text values from the popup subtree. These are the current rumour rows plus
    // header strings; the headers simply won't be found in RumorWeights and get dropped by the caller.
    private void ReadPopupRumors(ExileCore2.PoEMemory.Element popup, List<string> into, int depth = 0)
    {
        if (popup == null || depth > 6 || into.Count > 40) return;
        var t = popup.Text;
        if (!string.IsNullOrWhiteSpace(t)) into.Add(t);
        foreach (var ch in popup.Children)
            ReadPopupRumors(ch, into, depth + 1);
    }

    // Which expedition does the open popup belong to? Match by the popup's current rumour texts
    // against each region's pool and take the best overlap. currentTexts is the popup's raw rumour
    // keys (the `seen` set from the overlay). Returns null if nothing overlaps.
    private Classes.Expedition FindExpeditionForPopup(HashSet<string> currentTexts)
    {
        List<Classes.Expedition> snapshot;
        lock (mapCacheLock) snapshot = expeditions.ToList();

        Classes.Expedition best = null; int bestOverlap = 0;
        foreach (var e in snapshot)
        {
            int overlap = 0;
            foreach (var (text, _) in e.Rumors)
                if (currentTexts.Contains(text)) overlap++;
            if (overlap > bestOverlap) { bestOverlap = overlap; best = e; }
        }
        return best;
    }

    // Lay out the decode overlay beside the game's rumours popup: the popup's CURRENT rumours (read from
    // the popup itself, not Button.Rumors which is only the region's possible pool), decoded via
    // RumorWeights and ordered by weight. Position tracks the popup; drawn later by DrawExpeditions.
    private void LayoutPopupOverlay()
    {
        try
        {
            var popup = frameLogbookPopup;
            if (popup == null) return;
            RectangleF prect = popup.GetClientRect();
            if (prect.Width <= 0 || prect.Height <= 0) return;

            var texts = new List<string>();
            ReadPopupRumors(popup, texts);

            // decode current rumours, dedupe, order by weight desc. Popup text we can't decode (rumour
            // names not in RumorWeights, plus the popup's header strings) is dropped rather than shown raw.
            var rows = new List<(string content, string desc, float w, bool faded)>();
            var seen = new HashSet<string>();
            foreach (var t in texts)
            {
                if (!seen.Add(t)) continue;
                if (Settings.Expeditions.RumorWeights.TryGetValue(t, out var r))
                    rows.Add((r.Content, r.Description, r.Weight, false));
            }
            if (rows.Count == 0) return;
            rows.Sort((a, b) => b.w.CompareTo(a.w));

            // Hold Alt to also list the region's other possible rumours (the ones that didn't roll),
            // decoded from the matching expedition's pool and shown faded below the current ones.
            bool showPossible = ExileCore2.Input.GetKeyState(System.Windows.Forms.Keys.Menu);
            if (showPossible)
            {
                var exp = FindExpeditionForPopup(seen);
                if (exp != null)
                {
                    var extra = new List<(string content, string desc, float w, bool faded)>();
                    foreach (var (text, _) in exp.Rumors)
                    {
                        if (!seen.Add(text)) continue;
                        if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r))
                            extra.Add((r.Content, r.Description, r.Weight, true));
                    }
                    extra.Sort((a, b) => b.w.CompareTo(a.w));
                    rows.AddRange(extra);
                }
            }

            // Faded rows (possible-but-not-rolled) get a dimmed alpha; current rows stay full.
            System.Drawing.Color fontColor = Settings.Graphics.FontColor;
            System.Drawing.Color Dim(System.Drawing.Color c) => System.Drawing.Color.FromArgb(90, c.R, c.G, c.B);

            // Build the display lines (content header + indented description per rumour).
            var lines = new List<(string text, System.Drawing.Color color)>();
            lines.Add(("Rumours", fontColor));
            foreach (var (content, desc, _, faded) in rows)
            {
                var head = faded ? Dim(fontColor) : fontColor;
                lines.Add(("- " + content, head));
                if (!string.IsNullOrEmpty(desc))
                {
                    var sub = System.Drawing.Color.FromArgb(faded ? 90 : 180, 180, 180, 180);
                    lines.Add(("    " + desc, sub));
                }
            }
            if (showPossible)
                lines.Add(("(Alt: showing possible rumours)", Dim(fontColor)));

            const float pad = 8f;
            var (boxW, boxH, lineH) = MeasurePanel(lines, pad);

            // Place to the right of the popup; flip left if it would run off-screen.
            float x = prect.Right + 8f;
            var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
            if (x + boxW > screen.X) x = prect.Left - 8f - boxW;
            float y = prect.Top;
            // don't let a tall rumour list run off the bottom of the screen
            y = MathF.Min(y, screen.Y - boxH);

            RegisterPanel(new Vector2(x, y), boxW, boxH, pad, lineH, lines);
        }
        catch (Exception e) { LogError($"LayoutPopupOverlay failed: {e.Message}"); }
    }

    private void DrawExpeditionsPanel()
    {
        try
        {
            bool justOpened = !expeditionsPanelWasOpen; expeditionsPanelWasOpen = true;
            BeginPersistedWindow(Settings.Expeditions.PanelRect, justOpened, screenCenter, new Vector2(460, 520));
            ImGui.SetNextWindowSizeConstraints(new Vector2(360, 240), new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.9f);

            if (ImGui.Begin("Expeditions###expeditionspanel", ref expeditionsPanelOpen, ImGuiWindowFlags.NoCollapse))
            {
                SavePersistedWindow(Settings.Expeditions.PanelRect);

                // search filters rumour text/content, case insensitive
                string filter = expSearchText;
                ImGui.SetNextItemWidth(260);
                if (ImGui.InputTextWithHint("##expsearch", "Search rumors/content...", ref filter, 100))
                    expSearchText = filter;
                ImGui.SameLine();
                if (ImGui.Button("Clear##expsearchclear")) expSearchText = "";
                ImGui.Separator();
                ImGui.TextDisabled("Possible rumours for the region. Actual roll shows on hover on the atlas.");

                List<Classes.Expedition> snapshot;
                lock (mapCacheLock) snapshot = expeditions.ToList();

                if (snapshot.Count == 0)
                    ImGui.TextDisabled("No expeditions loaded. Open the atlas.");

                foreach (var e in snapshot.OrderByDescending(ExpeditionScore))
                {
                    if (!ExpeditionMatchesSearch(e, expSearchText)) continue;

                    ImGui.PushID($"exp_{e.RegionCoord.X}_{e.RegionCoord.Y}");

                    bool hl = IsExpeditionHighlighted(e);
                    if (ImGui.Checkbox("##hl", ref hl)) SetExpeditionHighlight(e, hl);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Highlight this expedition's maps on the atlas.");
                    ImGui.SameLine();

                    if (ImGui.SmallButton("WP##expwp")) WaypointNearestInExpedition(e);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waypoint the nearest map in this expedition (fewest steps from your explored frontier).");
                    ImGui.SameLine();

                    if (ImGui.CollapsingHeader(ExpeditionLabel(e)))
                    {
                        if (ImGui.BeginTable($"rumor_rows_{e.RegionCoord.X}_{e.RegionCoord.Y}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                        {
                            ImGui.TableSetupColumn("Possible Rumor", ImGuiTableColumnFlags.WidthFixed, 200);
                            ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 30);
                            ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 160);
                            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();

                            foreach (var (text, count) in e.Rumors.OrderByDescending(kv =>
                                Settings.Expeditions.RumorWeights.TryGetValue(kv.Key, out var r) ? r.Weight : 0f))
                            {
                                string content, desc;
                                if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var rw))
                                { content = rw.Content; desc = rw.Description; }
                                else { content = "(unknown content)"; desc = ""; }

                                ImGui.TableNextRow();
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted($"\"{text}\"");
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(count > 1 ? $"x{count}" : "");
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(content);
                                ImGui.TableNextColumn();
                                ImGui.TextDisabled(desc);
                            }
                            ImGui.EndTable();
                        }
                    }
                    ImGui.PopID();
                }

                if (ImGui.Button("Close##expeditions")) expeditionsPanelOpen = false;
            }
            ImGui.End();
        }
        catch (Exception ex)
        {
            LogError("Error drawing expeditions panel: " + ex.Message);
            expeditionsPanelOpen = false;
        }
    }
}
