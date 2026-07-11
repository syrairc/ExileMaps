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

                    // The visible button is the real current spawn; prefer its coord over the first seen.
                    if (b.IsVisible)
                        exp.SpawnCoord = b.Coordinate;
                }
            }
        }
        catch (Exception e) { LogError($"SnapshotExpeditions failed: {e.Message}"); }

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

    // Draw the expedition icon at each region's current spawn node (the IsVisible button's coord).
    // Gated behind ShowExpeditionMarkers. Skips off-screen / unresolved nodes.
    private void DrawExpeditionMarkers()
    {
        if (!Settings.Features.ShowExpeditionMarkers) return;
        if (ShowMinimap) return;

        List<Classes.Expedition> snapshot;
        lock (mapCacheLock) snapshot = expeditions.ToList();

        var fullUV = new RectangleF(0, 0, 1, 1);
        foreach (var e in snapshot)
        {
            Node node;
            lock (mapCacheLock)
                if (!mapCache.TryGetValue(e.SpawnCoord, out node)) node = null;
            if (node == null) continue;

            var rect = GetNodeRect(node);
            if (rect.IsEmpty || rect.Width <= 0) continue;

            // icon sits just above the node, sized to the node.
            float size = MathF.Max(rect.Width, rect.Height);
            var iconRect = new RectangleF(rect.Center.X - size / 2f, rect.Top - size, size, size);
            Graphics.DrawImage("icon-expedition.png", iconRect, fullUV, Color.White);
        }
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

    // The rumours popup bg carries this texture (.../MapQuickUseButton/LogbookRevealPopupBg.dds). No
    // named accessor exists and its top-level IngameUi index is not stable, so find it by texture.
    private const string LogbookPopupTexture = "LogbookRevealPopupBg";
    // Cache the top-level IngameUi child index the popup was last found under, so the per-frame lookup
    // rechecks one subtree instead of scanning all ~120 children. Only helps while the popup is up -
    // once it closes the index misses and this falls back to a full top-level scan.
    private int cachedLogbookChildIndex = -1;

    // The visible Logbook rumours popup element, or null when it's not up. Fast-path rechecks the
    // cached top-level child before a full scan. Only ever finds anything while the popup is shown.
    private ExileCore2.PoEMemory.Element FindLogbookPopup()
    {
        try
        {
            var children = UI?.Children;
            if (children == null) return null;

            if (cachedLogbookChildIndex >= 0 && cachedLogbookChildIndex < children.Count)
            {
                var hit = FindPopupInSubtree(children[cachedLogbookChildIndex], 0);
                if (hit != null) return hit;
            }

            for (int i = 0; i < children.Count; i++)
            {
                var hit = FindPopupInSubtree(children[i], 0);
                if (hit != null) { cachedLogbookChildIndex = i; return hit; }
            }
            cachedLogbookChildIndex = -1;
        }
        catch (Exception e) { LogError($"FindLogbookPopup failed: {e.Message}"); }
        return null;
    }

    // Depth-limited hunt for the popup-bg texture in a visible subtree.
    private ExileCore2.PoEMemory.Element FindPopupInSubtree(ExileCore2.PoEMemory.Element el, int depth)
    {
        if (el == null || depth > 6 || !el.IsVisible) return null;
        if (el.TextureName != null && el.TextureName.Contains(LogbookPopupTexture)) return el;
        foreach (var ch in el.Children)
        {
            var hit = FindPopupInSubtree(ch, depth + 1);
            if (hit != null) return hit;
        }
        return null;
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

    // When the rumours popup is up, draw a decode overlay beside it: the popup's CURRENT rumours
    // (read from the popup itself, not Button.Rumors which is only the region's possible pool),
    // decoded via RumorWeights and ordered by weight. Graphics-drawn so it tracks the popup.
    private void DrawExpeditionOverlay()
    {
        if (!Settings.Features.ShowExpeditions) return;
        if (ShowMinimap) return;
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
            var rows = new List<(string content, string desc, float w)>();
            var seen = new HashSet<string>();
            foreach (var t in texts)
            {
                if (!seen.Add(t)) continue;
                if (Settings.Expeditions.RumorWeights.TryGetValue(t, out var r))
                    rows.Add((r.Content, r.Description, r.Weight));
            }
            if (rows.Count == 0) return;
            rows.Sort((a, b) => b.w.CompareTo(a.w));

            // Build the display lines (content header + indented description per rumour).
            var lines = new List<(string text, System.Drawing.Color color)>();
            lines.Add(("Rumours", Settings.Graphics.FontColor));
            foreach (var (content, desc, _) in rows)
            {
                lines.Add(("- " + content, Settings.Graphics.FontColor));
                if (!string.IsNullOrEmpty(desc))
                    lines.Add(("    " + desc, System.Drawing.Color.FromArgb(180, 180, 180)));
            }

            // Measure box.
            float pad = 8f, lineH = 0f, maxW = 0f;
            foreach (var (text, _) in lines)
            {
                var m = Graphics.MeasureText(text);
                if (m.X > maxW) maxW = m.X;
                if (m.Y > lineH) lineH = m.Y;
            }
            float boxW = maxW + pad * 2f;
            float boxH = lines.Count * lineH + pad * 2f;

            // Place to the right of the popup; flip left if it would run off-screen.
            float x = prect.Right + 8f;
            var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
            if (x + boxW > screen.X) x = prect.Left - 8f - boxW;
            float y = prect.Top;
            // don't let a tall rumour list run off the bottom of the screen
            y = MathF.Min(y, screen.Y - boxH);

            var tl = new Vector2(x, y);
            Graphics.DrawBox(tl, new Vector2(x + boxW, y + boxH), Settings.Graphics.BackgroundColor, 5f);
            float cy = y + pad;
            foreach (var (text, color) in lines)
            {
                Graphics.DrawText(text, new Vector2(x + pad, cy), color);
                cy += lineH;
            }
        }
        catch (Exception e) { LogError($"DrawExpeditionOverlay failed: {e.Message}"); }
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
