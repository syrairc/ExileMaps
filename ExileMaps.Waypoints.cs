// Waypoint drawing, path rendering, add/remove waypoints, favorite sync.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Enums;

using GameOffsets2.Native;

using ImGuiNET;

using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    private void DrawWaypointPath(Waypoint waypoint)
    {
        // Snapshot once; UpdateWaypointPaths nulls/rebuilds this on the refresh thread.
        DrawPath(waypoint.PathFromStart, waypoint.Color);
    }

    // Draws a node polyline with the configured waypoint path style (textured/dashed/animated),
    // in the given color. Shared by waypoint paths and the multi-waypoint tour.
    private void DrawPath(System.Collections.Generic.IReadOnlyList<Node> path, System.Drawing.Color color)
    {
        if (path == null || path.Count <= 1)
            return;

        var g = Settings.Graphics;
        float dashLen = g.WaypointDashLength;
        float gap = g.WaypointDashGap;
        float period = dashLen + gap;
        bool dashed = dashLen > 0f && period > 0f;
        // Textured when UseNodeIcons is on and the selected sprite loaded; falls back to plain line.
        IntPtr pathTexId = default;
        bool textured = g.UseNodeIcons && pathTextureIds.TryGetValue(g.WaypointPathTexture, out pathTexId);
        float width = textured ? g.WaypointLineWidth * g.WaypointPathTextureScale : g.WaypointLineWidth;

        // Phase derived from wall-clock seconds (mod period); subtracted so dashes march toward
        // destination. Real-time based so speed/smoothness stay constant under frame-time jitter.
        // *60 keeps the old per-frame DashSpeed feel (was advanced once per ~60fps frame).
        float phase = (dashed && g.WaypointPathAnimated && g.WaypointDashSpeed > 0f)
            ? (AnimSeconds * g.WaypointDashSpeed.Value * 60f) % period
            : 0f;

        // Arc length along the whole polyline. Dash pattern positions against global arc so it
        // stays continuous across corners even when segment endpoints are clipped.
        float arc = 0f;

        for (int i = 0; i < path.Count - 1; i++)
        {
            var currentNode = path[i];
            var nextNode = path[i + 1];

            // Guard per-segment; a refresh can swap MapNode/Element, and one bad read would abort the frame.
            if (currentNode?.MapNode?.Element == null || nextNode?.MapNode?.Element == null)
                continue;

            // Reuse memoized rects (seeded by the node-position pass) instead of re-reading per segment.
            var startRect = GetNodeRect(currentNode);
            var endRect = GetNodeRect(nextNode);
            if (startRect.Width <= 0 || endRect.Width <= 0)   // null element or failed read, skip segment
                continue;
            Vector2 start = startRect.Center;
            Vector2 end = endRect.Center;

            float segArcStart = arc;
            arc += (end - start).Length();

            // Clip to screen instead of dropping off-screen endpoints; keeps paths visible at the edge.
            if (!ClipLineToScreen(start, end, out Vector2 clippedStart, out Vector2 clippedEnd))
                continue;

            // Skip segments crossing excluded UI rects (tooltip, title bar, etc.).
            bool crossesExcluded = false;
            foreach (var rect in cachedExcludeRects)
                if (rect.Contains(clippedStart) || rect.Contains(clippedEnd) ||
                    SegmentIntersectsRect(clippedStart, clippedEnd, rect)) {
                    crossesExcluded = true;
                    break;
                }
            if (crossesExcluded)
                continue;

            if (!dashed) {
                if (textured)
                    DrawTexturedSegment(clippedStart, clippedEnd, width, color, pathTexId);
                else
                    Graphics.DrawLine(clippedStart, clippedEnd, width, color);
                continue;
            }

            // Map clipped endpoints to global arc offsets so the dash pattern stays aligned.
            float clipArcStart = segArcStart + (clippedStart - start).Length();
            float clipArcEnd = segArcStart + (clippedEnd - start).Length();
            DrawDashedSegment(clippedStart, clippedEnd, clipArcStart, clipArcEnd, width, color, dashLen, period, phase, textured, pathTexId);
        }
    }

    // Draws dashes for arc offsets [arcStart, arcEnd] on segment a->b. A point is "on" when
    // ((arc - phase) % period) < dashLen.
    private void DrawDashedSegment(Vector2 a, Vector2 b, float arcStart, float arcEnd,
        float width, Color color, float dashLen, float period, float phase, bool textured, IntPtr texId)
    {
        float span = arcEnd - arcStart;
        if (span <= 0.01f)
            return;

        Vector2 dir = (b - a) / span; // unit vector (span == |b - a| for a clipped straight segment)

        // First dash-start at or before arcStart, aligned to the phase lattice.
        float firstDash = MathF.Floor((arcStart - phase) / period) * period + phase;
        for (float dashStart = firstDash; dashStart < arcEnd; dashStart += period)
        {
            float on0 = MathF.Max(dashStart, arcStart);
            float on1 = MathF.Min(dashStart + dashLen, arcEnd);
            if (on1 <= on0)
                continue;
            Vector2 p0 = a + dir * (on0 - arcStart);
            Vector2 p1 = a + dir * (on1 - arcStart);
            if (textured)
                DrawTexturedSegment(p0, p1, width, color, texId);
            else
                Graphics.DrawLine(p0, p1, width, color);
        }
    }

    // Draws the path sprite stretched over a->b as a rotated quad. No-op on degenerate segments.
    private void DrawTexturedSegment(Vector2 a, Vector2 b, float width, Color color, IntPtr texId)
    {
        Vector2 dir = b - a;
        float len = dir.Length();
        if (len < 0.001f)
            return;
        dir /= len;
        Vector2 n = new Vector2(-dir.Y, dir.X) * (width * 0.5f); // half-width perpendicular offset
        Graphics.DrawQuad(texId, a + n, b + n, b - n, a - n, color);
    }

    private void DrawWaypointPanel() {
        // Default footprint on first open: inset 30px from the screen's top-left, and 85% of the
        // interface height. Restored from saved rect after; the user can move/resize freely.
        var settingsRect = UI.SettingsPanel.GetClientRect();
        bool justOpened = !wpPanelWasOpen; wpPanelWasOpen = true;
        BeginPersistedWindow(Settings.Waypoints.PanelRect, justOpened, new Vector2(30, 30),
            new Vector2(settingsRect.Width, settingsRect.Height * 0.85f));
        ImGui.SetNextWindowSizeConstraints(new Vector2(420, 300), new Vector2(float.MaxValue, float.MaxValue));
        ImGui.SetNextWindowBgAlpha(0.8f);

        ImGui.Begin("Waypoints###WaypointPanel", ref WaypointPanelIsOpen, ImGuiWindowFlags.NoCollapse);
        SavePersistedWindow(Settings.Waypoints.PanelRect);

        if (ImGui.BeginTable("waypoint_top_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 60);                                                               
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool _show = Settings.Waypoints.ShowWaypoints;
            if(ImGui.Checkbox($"##show_waypoints", ref _show))                        
                Settings.Waypoints.ShowWaypoints = _show;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoints on Atlas");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool _showArrows = Settings.Waypoints.ShowWaypointArrows;
            if(ImGui.Checkbox($"##show_arrows", ref _showArrows))                        
                Settings.Waypoints.ShowWaypointArrows = _showArrows;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoint Arrows on Atlas");

            ImGui.TableNextRow();
        }
        ImGui.EndTable();

        ImGui.Spacing();

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 10));
        ImGui.Text("Waypoints");
        ImGui.PopStyleVar();        
        ImGui.Separator();


        #region Waypoints Table
        if (ImGui.CollapsingHeader("Waypoints", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Filter (All / Manual / Auto) + bulk delete. Manual = user-placed, Auto = favorite-synced.
            string[] wpFilters = { "All", "Manual", "Auto" };
            ImGui.Text("Show:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.Combo("##wpListFilter", ref waypointListFilter, wpFilters, wpFilters.Length);
            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            if (ImGui.Button("Clear Auto")) {
                foreach (var k in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(k);
                UpdateWaypointPaths();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove all auto-created (favorite) waypoints. Manual ones are kept.");
            ImGui.SameLine();
            if (ImGui.Button("Clear All")) {
                Settings.Waypoints.Waypoints.Clear();
                UpdateWaypointPaths();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove every waypoint, manual and auto.");
            ImGui.Spacing();

            var flags = ImGuiTableFlags.BordersInnerH;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0));
            if (ImGui.BeginTable("waypoint_list_table", 10, flags))//, new Vector2(-1, panelSize.Y/3)))
            {
                ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Waypoint Name", ImGuiTableColumnFlags.WidthFixed, 280);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 40);                    
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Scale", ImGuiTableColumnFlags.WidthFixed, 70);     
                ImGui.TableSetupColumn("Opt", ImGuiTableColumnFlags.WidthFixed, 80); 
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableHeadersRow();                    

                // Snapshot so row deletions don't invalidate the enumerator.
                var wpRows = Settings.Waypoints.Waypoints.Values.Where(w =>
                    waypointListFilter == 0
                    || (waypointListFilter == 1 && !w.AutoCreated)
                    || (waypointListFilter == 2 && w.AutoCreated)).ToList();
                foreach (var waypoint in wpRows) {
                    string id = waypoint.Address.ToString();
                    ImGui.PushID(id);
                    
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    bool _show = waypoint.Show;
                    if (ImGui.Checkbox($"##{id}_enabled", ref _show)) {
                        waypoint.Show = _show;
                    }

                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(300);
                    string _name = waypoint.Name;                    
                    if (ImGui.InputText($"##{id}_name", ref _name, 32)) {
                        waypoint.Name = _name;
                    }

                    ImGui.TableNextColumn();
                    int steps = waypoint.PathFromStart?.Count > 0 ? waypoint.PathFromStart.Count - 1 : -1;
                    if (steps >= 0)
                    {
                        Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                        Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                        ImGui.Text(steps.ToString());
                        ImGui.PopStyleColor();
                    }
                    else
                    {
                        ImGui.Text("-");
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(steps >= 0 ? waypoint.PathWeight.ToString("0") : "-");


                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.X.ToString());

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.Y.ToString());

                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    Color _color = waypoint.Color;
                    Vector4 _vector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{id}_nodecolor", ref _vector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        waypoint.Color = Color.FromArgb((int)(_vector.W * 255), (int)(_vector.X * 255), (int)(_vector.Y * 255), (int)(_vector.Z * 255));
                    
                    ImGui.TableNextColumn();
                    float _scale = waypoint.Scale;
                    ImGui.SetNextItemWidth(70);
                    if(ImGui.SliderFloat($"##{id}_weight", ref _scale, 0.1f, 2.0f, "%.2f"))                        
                        waypoint.Scale = _scale;


                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 50.0f) / 2.0f);
                    ImGui.SetNextItemWidth(50);
                    if (ImGui.Button("Del")) {
                        RemoveWaypoint(waypoint);
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.PopStyleColor();
            }
            #endregion
            
        }
       
        ImGui.Spacing();

        #region Atlas Table
        if (ImGui.CollapsingHeader("Atlas"))
        {
            

            ImGui.Text("Sort: ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy = Settings.Waypoints.WaypointPanelSortBy;
            if (ImGui.BeginCombo("##sortByCombo", sortBy))
            {
                if (ImGui.Selectable("Name", sortBy == "Name"))
                    sortBy = "Name";
                if (ImGui.Selectable("Weight", sortBy == "Weight"))
                    sortBy = "Weight";
                if (ImGui.Selectable("Steps", sortBy == "Steps"))
                    sortBy = "Steps";

                Settings.Waypoints.WaypointPanelSortBy = sortBy;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Text("then ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy2 = Settings.Waypoints.WaypointPanelSortBy2;
            if (ImGui.BeginCombo("##sortByCombo2", sortBy2))
            {
                if (ImGui.Selectable("None", sortBy2 == "None"))
                    sortBy2 = "None";
                if (ImGui.Selectable("Name", sortBy2 == "Name"))
                    sortBy2 = "Name";
                if (ImGui.Selectable("Weight", sortBy2 == "Weight"))
                    sortBy2 = "Weight";
                if (ImGui.Selectable("Steps", sortBy2 == "Steps"))
                    sortBy2 = "Steps";

                Settings.Waypoints.WaypointPanelSortBy2 = sortBy2;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Items: ");
            ImGui.SameLine();
            int maxItems = Settings.Waypoints.WaypointPanelMaxItems;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxItems", ref maxItems))
                Settings.Waypoints.WaypointPanelMaxItems = maxItems;

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Steps:");
            ImGui.SameLine();
            int maxSteps = Settings.Waypoints.WaypointPanelMaxSteps;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxSteps", ref maxSteps))
                Settings.Waypoints.WaypointPanelMaxSteps = Math.Max(0, maxSteps);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only show maps within this many steps of the explored region. 0 = unlimited.");

            ImGui.Separator();

            ImGui.Text("Search: ");
            ImGui.SameLine();
            string regex = Settings.Waypoints.WaypointPanelFilter;
            ImGui.SetNextItemWidth(250);
            if (ImGui.InputText("##search", ref regex, 32, ImGuiInputTextFlags.EnterReturnsTrue)) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemDeactivatedAfterEdit()) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("Searches for map names and/or mod text. Press enter to search.");
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            bool useRegex = Settings.Waypoints.WaypointsUseRegex;
            if (ImGui.Checkbox("Regex", ref useRegex))
                Settings.Waypoints.WaypointsUseRegex = useRegex;
            
            ImGui.Separator();
        
            var stepCounts = ComputeStepCounts();
            int GetSteps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

            int maxStepsFilter = Settings.Waypoints.WaypointPanelMaxSteps;
            var atlasList = GetAtlasPanelList(Settings.Waypoints.WaypointPanelFilter, useRegex, sortBy, sortBy2, maxStepsFilter, maxItems);

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2));
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2));
            try
            {
                if (ImGui.BeginTable("atlas_list_table", 7, flags))
                {
                    try
                    {
                        ImGui.TableSetupColumn("Map Name", ImGuiTableColumnFlags.WidthFixed, 200);
                        ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 110);
                        ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 50);
                        ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 80);
                        ImGui.TableSetupColumn("Unlocked", ImGuiTableColumnFlags.WidthFixed, 28);
                        ImGui.TableSetupColumn("Way", ImGuiTableColumnFlags.WidthFixed, 32);
                        ImGui.TableHeadersRow();

                        Vector4 _colorVector;
                        Color _color;

                        if (atlasList != null) {
                            foreach (var node in atlasList) {
                                string id = node.Address.ToString();
                                ImGui.PushID(id);
                                ImGui.TableNextRow();

                                // Name
                                ImGui.TableNextColumn();
                                ImGui.TextUnformatted(node.Name);

                                ImGui.SetWindowFontScale(0.9f);
                                ImGui.TableNextColumn();
                                foreach(var (k,content) in node.Content) {
                                    _color = content.Color;
                                    _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                                    ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                                    ImGui.TextUnformatted(content.Name);
                                    ImGui.PopStyleColor();
                                }

                                ImGui.SetWindowFontScale(1.0f);
                                ImGui.TableNextColumn();
                                int steps = GetSteps(node);
                                if (steps >= 0 && steps != int.MaxValue)
                                {
                                    Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                                    Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                                    ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                                    ImGui.TextUnformatted(steps.ToString());
                                    ImGui.PopStyleColor();
                                }
                                else
                                {
                                    ImGui.TextUnformatted("-");
                                }
                                ImGui.TableNextColumn();
                                float weight = (node.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
                                _color = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor,Settings.Maps.GoodNodeColor, weight);
                                _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                                ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                                ImGui.TextUnformatted(node.Weight.ToString("0.0"));
                                ImGui.PopStyleColor();

                                ImGui.TableNextColumn();
                                ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                                bool _unlocked = node.IsUnlocked;
                                ImGui.BeginDisabled();
                                ImGui.Checkbox($"##{id}_enabled", ref _unlocked);
                                ImGui.EndDisabled();
                                ImGui.TableNextColumn();
                                RectangleF icon = SpriteHelper.GetUV(MapIconsIndex.Waypoint);

                                if (!node.IsWaypoint){
                                    ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TableRowBg));
                                    if (ImGui.ImageButton($"$${id}_wp", iconsId, new Vector2(32,32), icon.TopLeft, icon.BottomRight)) {
                                        AddWaypoint(node);
                                    } else if (ImGui.IsItemHovered()) {
                                        ImGui.SetTooltip("Add Waypoint");
                                    }
                                    ImGui.PopStyleColor();
                                }

                                ImGui.PopID();
                            }
                        }
                    }
                    finally { ImGui.EndTable(); }
                }
            }
            finally { ImGui.PopStyleVar(2); }
            #endregion
            
        }

        ImGui.End();
    }

    private void DrawWaypoint(Waypoint waypoint, AtlasNodeDescription mapNode) {
        if (!Settings.Waypoints.ShowWaypoints || mapNode == null || !waypoint.Show)
            return;

        // Draw path even if destination is off-screen; ClipLineToScreen handles edge cases.
        if (Settings.Graphics.ShowPaths)
            DrawWaypointPath(waypoint);

        RectangleF nodeRect = mapNode.Element.GetClientRect();
        if (!IsOnScreen(nodeRect.Center))
            return;

        Vector2 waypointSize = new Vector2(48, 48);
        waypointSize *= waypoint.Scale;

        Vector2 iconPosition = nodeRect.Center - new Vector2(0, nodeRect.Height / 2);

        if (mapNode.Element.GetChildAtIndex(0) != null)
            iconPosition -= new Vector2(0, mapNode.Element.GetChildAtIndex(0).GetClientRect().Height);

        iconPosition -= new Vector2(0, 20);
        Vector2 waypointTextPosition = iconPosition - new Vector2(0, 10);
        // Add step count + path weight to waypoint label if available
        string displayText = waypoint.StepCount >= 0
            ? $"{waypoint.Name} ({waypoint.StepCount} steps, {waypoint.PathWeight:0} wt)"
            : waypoint.Name;

        DrawCenteredTextWithBackground(displayText, waypointTextPosition, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

        iconPosition -= new Vector2(waypointSize.X / 2, 0);
        RectangleF iconSize = new RectangleF(iconPosition.X, iconPosition.Y, waypointSize.X, waypointSize.Y);
        if (customIconsLoaded)
            DrawNodeSprite(iconSize.Center, iconSize.Width, iconSize.Height, waypoint.Icon, waypoint.Color, allowFlatten: false);
        else
            Graphics.DrawImage(IconsFile, iconSize, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse), waypoint.Color);


    }

    private static readonly Random waypointColorRng = new();

    // Returns a rainbow hue not already used by existing waypoints, expanding to finer steps as needed.
    private Color GetNextWaypointColor() {
        var used = new HashSet<int>();
        foreach (var (_, wp) in Settings.Waypoints.Waypoints)
            used.Add(wp.Color.ToArgb());

        for (int tier = 0; tier < 6; tier++) {
            int count = 8 + tier * 8;                   // 8, 16, 24, ... distinct hues per tier
            float sat = (tier % 2 == 0) ? 0.85f : 0.6f; // alternate saturation as tiers expand
            float val = (tier < 2) ? 1.0f : 0.8f;       // then add dimmer shades
            float hueOffset = tier * 15f;               // interleave later tiers between earlier hues

            var candidates = new List<Color>();
            for (int i = 0; i < count; i++) {
                var c = ColorUtils.ColorFromHSV((360f * i / count) + hueOffset, sat, val);
                if (!used.Contains(c.ToArgb()))
                    candidates.Add(c);
            }
            if (candidates.Count > 0)
                return candidates[waypointColorRng.Next(candidates.Count)];
        }

        return ColorUtils.ColorFromHSV(waypointColorRng.Next(360), 0.8f, 1.0f);
    }

    private void AddWaypoint(Node cachedNode) {
        if (Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Icon = SpriteIcon.TriangleDown;
        newWaypoint.Color = GetNextWaypointColor();

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
        UpdateWaypointPaths();
    }

    // Syncs auto-created waypoints with favorite map types. Never touches manual waypoints.
    private void SyncFavoriteWaypoints() {
        try {
            // Snapshot favorite, non-visited nodes under the lock (background job mutates mapCache).
            Dictionary<string, Node> favorites;
            lock (mapCacheLock)
                favorites = mapCache.Values
                    .Where(x => x.IsFavorited && !x.IsVisited)
                    .GroupBy(x => x.Coordinates.ToString())
                    .ToDictionary(g => g.Key, g => g.First());

            if (!Settings.Waypoints.AutoWaypointFavorites) {
                // Feature off: clean up everything we auto-created.
                foreach (var key in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(key);
                return;
            }

            // Add waypoints for favorites that don't have one yet.
            foreach (var (key, node) in favorites) {
                if (Settings.Waypoints.Waypoints.ContainsKey(key))
                    continue;

                Waypoint newWaypoint = node.ToWaypoint();
                newWaypoint.Icon = SpriteIcon.TriangleDown;
                newWaypoint.Color = GetNextWaypointColor();
                newWaypoint.AutoCreated = true;
                Settings.Waypoints.Waypoints.Add(key, newWaypoint);
            }

            // Remove auto-created waypoints whose map is no longer a favorite.
            foreach (var key in Settings.Waypoints.Waypoints
                .Where(x => x.Value.AutoCreated && !favorites.ContainsKey(x.Key))
                .Select(x => x.Key).ToList())
                Settings.Waypoints.Waypoints.Remove(key);
        } catch (Exception e) {
            LogError("Error syncing favorite waypoints: " + e.Message);
        }
    }

    // Removes waypoints whose map has been completed/run. Gated on AutoRemoveCompletedWaypoints.
    // Runs on the refresh thread (called from RefreshMapCache) so the cache read is stable; still
    // snapshots under the lock to match SyncFavoriteWaypoints.
    private void RemoveCompletedWaypoints() {
        if (!Settings.Waypoints.AutoRemoveCompletedWaypoints)
            return;

        try {
            HashSet<string> completed;
            lock (mapCacheLock)
                completed = mapCache.Values
                    .Where(x => x.IsVisited || x.IsCompleted)
                    .Select(x => x.Coordinates.ToString())
                    .ToHashSet();

            foreach (var key in Settings.Waypoints.Waypoints
                .Where(x => completed.Contains(x.Value.Coordinates.ToString()))
                .Select(x => x.Key).ToList())
                Settings.Waypoints.Waypoints.Remove(key);
        } catch (Exception e) {
            LogError("Error removing completed waypoints: " + e.Message);
        }
    }

    private void RemoveWaypoint(Node cachedNode) {
        if (!Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Settings.Waypoints.Waypoints.Remove(cachedNode.Coordinates.ToString());
        UpdateWaypointPaths();
    }
    private void RemoveWaypoint(Waypoint waypoint) {
        Settings.Waypoints.Waypoints.Remove(waypoint.Coordinates.ToString());
        UpdateWaypointPaths();
    }

    private void DrawWaypointArrow(Waypoint waypoint, AtlasNodeDescription mapNode) {
        if (!Settings.Waypoints.ShowWaypointArrows || mapNode == null)
            return;

        Vector2 waypointPosition = mapNode.Element.GetClientRect().Center;

        float distance = Vector2.Distance(screenCenter, waypointPosition);

        if (distance < Settings.Graphics.WaypointArrowMinDistance)
            return;

        Vector2 arrowSize = new(64, 64);
        Vector2 arrowPosition = waypointPosition;
        arrowPosition.X = Math.Clamp(arrowPosition.X, 0, GameController.Window.GetWindowRectangleTimeCache.Size.X);
        arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, GameController.Window.GetWindowRectangleTimeCache.Size.Y);
        arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
        arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

        Vector2 direction = waypointPosition - screenCenter;
        float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

        Color color = Color.FromArgb(255, waypoint.Color);
        DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, color);
         Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
        textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
        if (Settings.Waypoints.InverWaypointArrowsColors)
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition,  Settings.Graphics.BackgroundColor, color, true, 10, 4);
        }
        else
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition, color, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }
}
