using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using Newtonsoft.Json;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.Models;
using GameOffsets2.Native;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;
using ExileImGui2;
using ExileCore2.RitualAtlas;

namespace ExileMaps;

public partial class ExileMapsCore
{
    #region Path Drawing

    private void DrawWaypointPath(Waypoint waypoint)
    {
        DrawPath(waypoint.PathFromStart, waypoint.Color);
        
    }

    private static Vector2 HopPoint(Vector2[] curve, bool reversed, int hops, int k, Vector2 start, Vector2 end)
    {
        if (k == 0) return start;
        if (k == hops - 1) return end;
        return curve[reversed ? hops - 1 - k : k];
    }

    private void DrawPath(IReadOnlyList<Node> path, Color color)
    {
        if (path == null || path.Count <= 1)
            return;

        var g = Settings.Graphics;
        float dashLen = g.WaypointDashLength;
        float period = dashLen + g.WaypointDashGap;
        bool dashed = dashLen > 0f && period > 0f;
        float width = g.WaypointLineWidth;
        Color outline = Color.FromArgb(color.A, color.R / 4, color.G / 4, color.B / 4);

        float arc = 0f;

        for (int i = 0; i < path.Count - 1; i++)
        {
            var currentNode = path[i];
            var nextNode = path[i + 1];

            if (currentNode?.MapNode?.Element == null || nextNode?.MapNode?.Element == null)
                continue;

            var startRect = GetNodeRect(currentNode);
            var endRect = GetNodeRect(nextNode);
            if (startRect.Width <= 0 || endRect.Width <= 0)
                continue;

            Vector2 startPt = startRect.Center, endPt = endRect.Center;
            bool reversed = false;
            Vector2[] curve = g.UseGameConnectionCurves
                ? GetCurveScreenPoints(currentNode.Coordinates, nextNode.Coordinates, out reversed)
                : null;
            int hops = curve == null || curve.Length < 2 ? 2 : curve.Length;

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int k = 0; k < hops; k++) {
                var p = HopPoint(curve, reversed, hops, k, startPt, endPt);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            CollectExcludes(minX, minY, maxX, maxY, excludeScratch);

            for (int k = 0; k < hops - 1; k++)
            {
                Vector2 start = HopPoint(curve, reversed, hops, k, startPt, endPt);
                Vector2 end = HopPoint(curve, reversed, hops, k + 1, startPt, endPt);

                float segArcStart = arc;
                arc += (end - start).Length();

                if (!ClipLineToScreen(start, end, out Vector2 clippedStart, out Vector2 clippedEnd))
                    continue;
                if (CrossesExcluded(clippedStart, clippedEnd, excludeScratch))
                    continue;

                if (!dashed) {
                    DrawStyledSegment(clippedStart, clippedEnd, width, color, outline);
                    continue;
                }

                float clipArcStart = segArcStart + (clippedStart - start).Length();
                float clipArcEnd = segArcStart + (clippedEnd - start).Length();
                DrawDashedSegment(clippedStart, clippedEnd, clipArcStart, clipArcEnd, width, color, outline, dashLen, period);
            }
        }
    }

    private void DrawDashedSegment(Vector2 a, Vector2 b, float arcStart, float arcEnd,
        float width, Color color, Color outline, float dashLen, float period)
    {
        float span = arcEnd - arcStart;
        if (span <= 0.01f)
            return;

        Vector2 dir = (b - a) / span;

        float firstDash = MathF.Floor(arcStart / period) * period;
        for (float dashStart = firstDash; dashStart < arcEnd; dashStart += period)
        {
            float on0 = MathF.Max(dashStart, arcStart);
            float on1 = MathF.Min(dashStart + dashLen, arcEnd);
            if (on1 <= on0)
                continue;
            DrawStyledSegment(a + dir * (on0 - arcStart), a + dir * (on1 - arcStart), width, color, outline);
        }
    }

    private void DrawStyledSegment(Vector2 a, Vector2 b, float width, Color color, Color outline)
    {
        Graphics.DrawLine(a, b, width + 2f, outline);
        Graphics.DrawLine(a, b, width, color);
    }

    #endregion

    #region Waypoint Panel

    private int wpMaxSteps = 0;
    private string wpFilter = "";

    private void RebuildTours()
    {
        foreach (var t in Settings.Tours.Tours.Values) t.BuiltVersion = -1;
    }

    private void DrawWaypointBody() {
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
            float wpActionBtnWidth = Math.Max(ImGui.CalcTextSize("Goto").X, ImGui.CalcTextSize("Del").X) + ImGui.GetStyle().FramePadding.X * 2;
            float wpActionColWidth = wpActionBtnWidth * 2 + ImGui.GetStyle().ItemSpacing.X + 10;
            if (ImGui.BeginTable("waypoint_list_table", 6, flags))
            {
                ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Waypoint Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, wpActionColWidth);
                ImGui.TableHeadersRow();

                var wpRows = Settings.Waypoints.Waypoints.Values.Where(w =>
                    waypointListFilter == 0
                    || (waypointListFilter == 1 && !w.AutoCreated)
                    || (waypointListFilter == 2 && w.AutoCreated)).ToList();
                foreach (var waypoint in wpRows) {
                    string id = waypoint.Coordinates.ToString();
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
                    var (statusText, statusColor, statusTip) = WaypointStatus(waypoint);
                    ImGui.PushStyleColor(ImGuiCol.Text,
                        new Vector4(statusColor.R / 255f, statusColor.G / 255f, statusColor.B / 255f, 1f));
                    ImGui.TextUnformatted(statusText);
                    ImGui.PopStyleColor();
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(statusTip);

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
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    Color _color = waypoint.Color;
                    Vector4 _vector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{id}_nodecolor", ref _vector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
                        waypoint.Color = Color.FromArgb((int)(_vector.W * 255), (int)(_vector.X * 255), (int)(_vector.Y * 255), (int)(_vector.Z * 255));

                    ImGui.TableNextColumn();
                    if (ImGui.Button("Del", new Vector2(wpActionBtnWidth, 0))) {
                        RemoveWaypoint(waypoint);
                    }
                    if (HacksCameraPanReady) {
                        ImGui.SameLine();
                        if (ImGui.Button("Goto", new Vector2(wpActionBtnWidth, 0))) GotoNode(NodeAtCoords(waypoint.Coordinates));
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pan the atlas to this waypoint.");
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.PopStyleColor();
            }
            #endregion

        }

    }

    private void DrawMapsBody()
    {
        ImGui.Text("Max Steps:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100);
        if (ImGui.InputInt("##maxSteps", ref wpMaxSteps))
            wpMaxSteps = Math.Max(0, wpMaxSteps);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only show maps within this many steps of the explored region. 0 = unlimited.");

        var stepCounts = ComputeStepCounts();
        int GetSteps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

        var atlasList = UnvisitedNodes("", wpMaxSteps) ?? [];

        ImGui.SameLine();
        ImGui.TextDisabled(atlasList.Count == 1 ? "1 map" : $"{atlasList.Count} maps");

        var cols = new[] {
            new TableColumn<Node> {
                Header = "Map Name", Width = 190f, SortKey = n => n.Name,
                Draw = (n, _) => { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(n.Name); },
            },
            new TableColumn<Node> {
                Header = "Content", SortKey = ContentSummary,
                Draw = (n, _) => {
                    ImGui.AlignTextToFramePadding();
                    bool first = true;
                    foreach (var (_, c) in n.Content) {
                        if (!first) { ImGui.SameLine(0, 0); ImGui.TextUnformatted(", "); ImGui.SameLine(0, 0); }
                        var col = ContentDefaultColor(c.Id);
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(col.R / 255f, col.G / 255f, col.B / 255f, col.A / 255f));
                        ImGui.TextUnformatted(c.Name);
                        ImGui.PopStyleColor();
                        first = false;
                    }
                    if (first) ImGui.TextUnformatted("");
                },
            },
            new TableColumn<Node> {
                Header = "Steps", Width = 50f, SortKey = n => GetSteps(n),
                Draw = (n, _) => {
                    ImGui.AlignTextToFramePadding();
                    int steps = GetSteps(n);
                    if (steps < 0 || steps == int.MaxValue) { ImGui.TextUnformatted("-"); return; }
                    Color sc = steps <= 3 ? Color.Green : steps <= 7 ? Color.Yellow : Color.Red;
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(sc.R / 255f, sc.G / 255f, sc.B / 255f, 1f));
                    ImGui.TextUnformatted(steps.ToString());
                    ImGui.PopStyleColor();
                },
            },
            new TableColumn<Node> {
                Header = "Weight", Width = 60f, SortKey = n => n.Weight,
                Draw = (n, _) => {
                    ImGui.AlignTextToFramePadding();
                    var wc = WeightRampColor(n.Weight);
                    ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(wc.R / 255f, wc.G / 255f, wc.B / 255f, wc.A / 255f));
                    ImGui.TextUnformatted(n.Weight.ToString("0.0"));
                    ImGui.PopStyleColor();
                },
            },
            new TableColumn<Node> {
                Header = "Unlocked", Width = 60f, SortKey = n => n.IsUnlocked,
                Draw = (n, _) => {
                    bool unlocked = n.IsUnlocked;
                    ImGui.BeginDisabled();
                    ImGui.Checkbox("##unlocked", ref unlocked);
                    ImGui.EndDisabled();
                },
            },
            new TableColumn<Node> {
                Header = "Waypoint", Width = 65f, SortKey = n => HasWaypoint(n),
                Draw = (n, _) => {
                    bool wp = HasWaypoint(n);
                    if (ImGui.Checkbox("##wp", ref wp)) {
                        if (wp) AddWaypoint(n); else RemoveWaypoint(n);
                    }
                    Controls.Tip(wp ? "Remove waypoint" : "Add waypoint");
                },
            },
        };

        if (HacksCameraPanReady) {
            cols = cols.Append(new TableColumn<Node> {
                Header = "Goto", Width = 50f,
                Draw = (n, _) => {
                    if (ImGui.SmallButton("Goto")) GotoNode(n);
                    Controls.Tip("Pan the atlas to this map.");
                },
            }).ToArray();
        }

        SortableTable.Draw("atlas_list", atlasList, cols, ref wpFilter, MapSearchText,
            Math.Max(120f, ImGui.GetContentRegionAvail().Y));
    }

    private bool HasWaypoint(Node n) =>
        Settings.Waypoints.Waypoints.ContainsKey(n.Coordinates.ToString());

    private Node NodeAtCoords(Vector2i coords)
    {
        lock (mapCacheLock)
            return mapCache.TryGetValue(coords, out var node) ? node : null;
    }

    private static string ContentSummary(Node n) =>
        n.Content.Count == 0 ? "" : string.Join(", ", n.Content.Values.Select(c => c.Name));

    private static string MapSearchText(Node n)
    {
        var sb = new StringBuilder(160);
        void Tag(string prefix, string value) {
            if (string.IsNullOrWhiteSpace(value)) return;
            sb.Append(' ').Append(value).Append(' ').Append(prefix).Append(':').Append(value);
        }

        Tag("map", n.Name);
        foreach (var c in n.Content.Values)
            Tag("content", c?.Name);
        foreach (var b in n.Biomes.Values)
            Tag("biome", b?.Name);
        foreach (var m in n.SpecialModifiers)
            Tag("mod", m);
        foreach (var d in n.ModifierDetails)
            Tag("mod", d);
        return sb.ToString();
    }

    #endregion

    #region Waypoints

    private void DrawWaypoint(Waypoint waypoint, Node node) {
        if (!Settings.Waypoints.ShowWaypoints || node?.MapNode?.Element == null || !waypoint.Show)
            return;

        if (Settings.Graphics.ShowPaths)
            DrawWaypointPath(waypoint);

        RectangleF nodeRect = GetNodeRect(node);
        if (nodeRect.Width <= 0 || !IsOnScreen(nodeRect.Center))
            return;

        Vector2 waypointSize = new Vector2(48, 48);

        Vector2 iconPosition = nodeRect.Center - new Vector2(0, nodeRect.Height / 2);

        var child = node.MapNode.Element.GetChildAtIndex(0);
        if (child != null)
            iconPosition -= new Vector2(0, child.GetClientRect().Height);

        iconPosition -= new Vector2(0, 20);
        Vector2 waypointTextPosition = iconPosition - new Vector2(0, 10);
        string displayText = waypoint.StepCount >= 0
            ? $"{waypoint.Name} ({waypoint.StepCount} steps)"
            : waypoint.Name;

        DrawCenteredTextWithBackground(displayText, waypointTextPosition, OverlayText, OverlayBg, true, 10, 4);

        iconPosition -= new Vector2(waypointSize.X / 2, 0);
        RectangleF iconSize = new RectangleF(iconPosition.X, iconPosition.Y, waypointSize.X, waypointSize.Y);
        DrawNodeSprite(iconSize.Center, iconSize.Width, iconSize.Height, SpriteIcon.TriangleDown, waypoint.Color, allowFlatten: false);
    }

    private static readonly Random paletteRng = new();

    private static Color NextDistinctColor(IEnumerable<Color> taken)
    {
        var used = new HashSet<int>();
        foreach (var c in taken) used.Add(c.ToArgb());

        for (int tier = 0; tier < 6; tier++) {
            int count = 8 + tier * 8;
            float sat = (tier % 2 == 0) ? 0.85f : 0.6f;
            float val = (tier < 2) ? 1.0f : 0.8f;
            float hueOffset = tier * 15f;

            var candidates = new List<Color>();
            for (int i = 0; i < count; i++) {
                var c = ColorUtils.ColorFromHSV((360f * i / count) + hueOffset, sat, val);
                if (!used.Contains(c.ToArgb()))
                    candidates.Add(c);
            }
            if (candidates.Count > 0)
                return candidates[paletteRng.Next(candidates.Count)];
        }
        return ColorUtils.ColorFromHSV(paletteRng.Next(360), 0.8f, 1.0f);
    }

    private Color GetNextWaypointColor() => NextDistinctColor(Settings.Waypoints.Waypoints.Values.Select(w => w.Color));

    private void AddWaypoint(Node cachedNode) {
        if (Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Color = GetNextWaypointColor();

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
        UpdateWaypointPaths();
    }

    private void SyncFavoriteWaypoints() {
        try {
            List<Node> favorited;
            lock (mapCacheLock)
                favorited = mapCache.Values.Where(x => x.IsFavorited && !x.IsDone).ToList();

            if (Settings.Waypoints.AutoWaypointNearestOnly && favorited.Count > 1) {
                var steps = ComputeStepCounts();
                int Steps(Node n) => steps.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;
                favorited = favorited
                    .GroupBy(FavoriteTypeKey)
                    .Select(g => g.OrderBy(Steps)
                                  .ThenBy(n => n.Coordinates.X)
                                  .ThenBy(n => n.Coordinates.Y)
                                  .First())
                    .ToList();
            }

            var favorites = favorited.ToDictionary(n => n.Coordinates.ToString(), n => n);

            if (!Settings.Waypoints.AutoWaypointFavorites) {
                foreach (var key in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(key);
                return;
            }

            foreach (var (key, node) in favorites) {
                if (Settings.Waypoints.Waypoints.ContainsKey(key))
                    continue;

                Waypoint newWaypoint = node.ToWaypoint();
                newWaypoint.Color = GetNextWaypointColor();
                newWaypoint.AutoCreated = true;
                Settings.Waypoints.Waypoints.Add(key, newWaypoint);
            }

            foreach (var key in Settings.Waypoints.Waypoints
                .Where(x => x.Value.AutoCreated && !favorites.ContainsKey(x.Key))
                .Select(x => x.Key).ToList())
                Settings.Waypoints.Waypoints.Remove(key);
        } catch (Exception e) {
            LogError("Error syncing favorite waypoints: " + e.Message);
        }
    }

    private static string FavoriteTypeKey(Node node) {
        var id = node.MapType?.ShortestId;
        if (!string.IsNullOrEmpty(id))
            return id;
        return string.IsNullOrEmpty(node.Name) ? "@" + node.Coordinates : node.Name;
    }

    private void RemoveCompletedWaypoints() {
        if (!Settings.Waypoints.AutoRemoveCompletedWaypoints)
            return;

        try {
            HashSet<string> completed;
            lock (mapCacheLock)
                completed = mapCache.Values
                    .Where(x => x.IsDone)
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

    private (string text, Color color, string tip) WaypointStatus(Waypoint waypoint)
    {
        Node node;
        lock (mapCacheLock)
            mapCache.TryGetValue(waypoint.Coordinates, out node);

        if (node == null)
            return ("Unknown", Color.Gray, "This map is not in the cache right now - pan to it.");

        if (node.IsDone)
            return ("Complete", Color.LimeGreen, "Already run.");

        if (!(waypoint.PathFromStart?.Count > 0))
            return ("Inaccessible", Color.OrangeRed, "No route from the explored region reaches this map.");

        if (node.IsUnlocked)
            return ("Incomplete", Color.Gold, "Reachable and not run yet.");

        return ("Locked", Color.SandyBrown, "On a route, but still fogged - the maps before it have to be run first.");
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

    private void DrawWaypointArrow(Waypoint waypoint, Node node) {
        if (!Settings.Waypoints.ShowWaypointArrows || node?.MapNode?.Element == null)
            return;

        Vector2 waypointPosition = GetNodeRect(node).Center;

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
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition,  OverlayBg, color, true, 10, 4);
        }
        else
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition, color, OverlayBg, true, 10, 4);
        }
    }

    private bool buildModeActive = false;

    #endregion

    #region Panel Chrome

    private ExileImGui2.NavItem[] navItems;

    private IntPtr LoadPanelTexture(string file, bool required = true)
    {
        try
        {
            var full = Path.Combine(DirectoryFullName, "textures", "nav", file);
            if (!File.Exists(full)) {
                if (required) LogError($"Panel button texture missing: {file}");
                return IntPtr.Zero;
            }
            Graphics.InitImage(file, full);
            return Graphics.GetTextureId(file);
        }
        catch (Exception e) { LogError($"Error loading panel texture {file}: {e.Message}"); return IntPtr.Zero; }
    }

    private void LoadPanelButtonTextures()
    {
        IntPtr Art(string k) => LoadPanelTexture($"rail-{k}.png", required: false);

        var wp = Art("wp");
        var tours = Art("tours");
        var maps = Art("maps");
        var atlas = Art("atlas");
        var exped = Art("expeditions");

        foreach (var k in MarkerArtKinds)
        {
            LoadPanelTexture(MarkerArt(k, false), required: false);
            LoadPanelTexture(MarkerArt(k, true), required: false);
        }

        navItems =
        [
            new ExileImGui2.NavItem {
                Key = "wp", Label = "Waypoints",
                Icon = wp,
                Badge = () => Settings.Waypoints.Waypoints.Count is var c && c > 0 ? c.ToString() : null,
                Body = () => { DrawWaypointBody(); return false; },
            },
            new ExileImGui2.NavItem {
                Key = "tours", Label = "Tours",
                Icon = tours,
                Body = () => { DrawToursBody(); return false; },
            },
            new ExileImGui2.NavItem {
                Key = "maps", Label = "Maps",
                Icon = maps,
                Body = () => { DrawMapsBody(); return false; },
            },
            new ExileImGui2.NavItem {
                Key = "atlas", Label = "Atlas Overview",
                Icon = atlas,
                Body = () => { DrawAtlasOverviewBody(); return false; },
            },
            new ExileImGui2.NavItem {
                Key = "rite", Label = "Rite of the Nameless",
                Icon = LoadPanelTexture("rail-rite.png", required: false),
                Body = () => { DrawRitualBody(); return false; },
            },
            new ExileImGui2.NavItem {
                Key = "expeditions", Label = "Expeditions",
                Icon = exped,
                Visible = ExpeditionsLoaded,
                Body = () => { DrawExpeditionsBody(); return false; },
            },
        ];
    }

    #endregion

    #region Tours

    private Node ResolveStop(TourStop s)
    {
        lock (mapCacheLock)
            return mapCache.TryGetValue(new Vector2i(s.X, s.Y), out var node) ? node : null;
    }

    private int tourVersion => mapCacheVersion + weightsRecalcVersion;

    private void BuildTour(Tour t)
    {
        long t0 = Stopwatch.GetTimestamp();
        t.Segments = new();
        t.ResolvedStops = new();
        t.Skipped = new();
        try
        {
            t.Stops.RemoveAll(s =>
            {
                var n = ResolveStop(s);
                return n != null && n.IsDone;
            });

            var nodes = new List<Node>();
            foreach (var s in t.Stops)
            {
                var node = ResolveStop(s);
                if (node != null) nodes.Add(node);
                else t.Skipped.Add($"({s.X},{s.Y})");
            }
            if (nodes.Count == 0) { t.BuiltVersion = tourVersion; return; }

            var first = nodes[0];
            var (incoming, _) = FindPathToNearestCompleted(first, TourExtraMapCost);
            t.Segments.Add(incoming ?? new List<Node> { first });
            t.ResolvedStops.Add(first);

            var done = new HashSet<Vector2i>(t.Segments[0].Select(n => n.Coordinates));
            Node anchor = first;
            for (int i = 1; i < nodes.Count; i++)
            {
                var (path, _) = FindPath(anchor, nodes[i], done);
                if (path == null) { t.Skipped.Add(nodes[i].Name ?? $"(stop {i + 1})"); continue; }
                t.Segments.Add(path);
                t.ResolvedStops.Add(nodes[i]);
                done.UnionWith(path.Select(n => n.Coordinates));
                anchor = nodes[i];
            }

            t.BuiltVersion = tourVersion;
        }
        catch (Exception e)
        {
            LogError("Error building tour: " + e.Message);
            t.Segments = new();
            t.ResolvedStops = new();
            t.BuiltVersion = tourVersion;
        }
        PerfMonitor.Record("Memo.BuildTour", Stopwatch.GetTimestamp() - t0);
    }

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

            var done = new HashSet<Vector2i>();
            while (remaining.Count > 0)
            {
                List<Node> bestPath = null;
                int bestCost = int.MaxValue;
                Node bestNode = null;
                foreach (var cand in remaining)
                {
                    var (path, cost) = FindPath(current, cand, done);
                    if (path == null) continue;
                    if (cost < bestCost) { bestPath = path; bestCost = cost; bestNode = cand; }
                }
                if (bestNode == null) bestNode = remaining.OrderBy(Steps).First();
                else done.UnionWith(bestPath.Select(n => n.Coordinates));
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

    private void DrawTours()
    {
        if (!Settings.Tours.ShowTours) return;

        foreach (var t in Settings.Tours.Tours.Values)
        {
            if (!t.Show || t.Stops.Count == 0) continue;

            if (t.BuiltVersion != tourVersion) BuildTour(t);

            try
            {
                foreach (var seg in t.Segments)
                    DrawPath(seg, t.Color);

                Node nextStop = Settings.Tours.ShowNextStopTarget
                    ? t.ResolvedStops.FirstOrDefault(n => n != null && !n.IsDone)
                    : null;

                for (int i = 0; i < t.ResolvedStops.Count; i++)
                {
                    var node = t.ResolvedStops[i];
                    if (node?.MapNode?.Element == null) continue;
                    Vector2 center; float width, height;
                    var rect = GetNodeRect(node);
                    if (rect.Width <= 0) continue;
                    center = rect.Center; width = rect.Width; height = rect.Height;

                    if (ReferenceEquals(node, nextStop))
                    {
                        float baseSize = width * 0.9f * Settings.Graphics.NodeRadius;
                        double secs = AnimSeconds;
                        float pulse = 1f + 0.18f * (float)Math.Sin(secs * (Math.PI * 2.0 / 1.1));
                        float size = baseSize * pulse;
                        float topY = center.Y - height / 2f - baseSize / 2f - 4f;
                        if (node.Content.Count > 0)
                            topY -= height;
                        DrawNodeSprite(new Vector2(center.X, topY), size, size, SpriteIcon.Target, t.Color, allowFlatten: false);
                    }

                    DrawCenteredTextWithBackground((i + 1).ToString(), center,
                        OverlayText, OverlayBg, true, 8, 4);
                }
            }
            catch (Exception e) { DebugSwallow("DrawTours", e); }
        }
    }

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
                ExileCore2.Shared.RectangleF r;
                try { r = WorldAlignedRect(d, d.Element.GetClientRectCache); } catch { continue; }
                float distSq = Vector2.DistanceSquared(cursor, r.Center);
                if (distSq < bestDistSq) { bestDistSq = distSq; bestCoord = d.Coordinate; bestRect = r; hasBest = true; }
            }
            if (!hasBest) return;

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

    private void DrawBuildModeIndicator()
    {
        try
        {
            var active = GetActiveTour();
            string tourName = active?.Name ?? "(new tour)";
            string exitKey = Keys.Tab.ToString();
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

    private void AddStopToActiveTourIfAbsent(Node node)
    {
        if (node == null) return;
        var t = GetActiveTour() ?? AddTour();
        int x = node.Coordinates.X, y = node.Coordinates.Y;
        if (t.Stops.FindIndex(s => s.X == x && s.Y == y) >= 0) return;
        t.Stops.Add(new TourStop { X = x, Y = y });
        t.BuiltVersion = -1;
    }

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

    private Color GetNextTourColor() => NextDistinctColor(Settings.Tours.Tours.Values.Select(t => t.Color));

    private static bool NodeMatchesContent(Node node, HashSet<string> selected)
    {
        foreach (var c in node.Content.Values)
            if (c.Name != null && selected.Contains(c.Name)) return true;
        return false;
    }

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

    private void AutoCreateTour()
    {
        try
        {
            var selected = new HashSet<string>(Settings.Tours.AutoTourContent ?? new List<string>());
            if (selected.Count == 0) return;

            int n = Settings.Tours.AutoTourReach;
            const float marginPct = 11 / 100f;
            var r = cachedScreenRect;
            float mx = r.Width * marginPct, my = r.Height * marginPct;
            float minX = r.Left - mx, maxX = r.Right + mx, minY = r.Top - my, maxY = r.Bottom + my;

            var stepCounts = ComputeStepCounts();
            int Steps(Node node) => stepCounts.TryGetValue(node.Coordinates, out var s) ? s : int.MaxValue;

            const bool onlyAtlasPoints = false;
            List<Node> candidates;
            lock (mapCacheLock)
            {
                candidates = mapCache.Values
                    .Where(x => !x.IsDone
                                && (!onlyAtlasPoints || x.GivesAtlasPoint)
                                && NodeMatchesContent(x, selected)
                                && CenterInBounds(x, minX, minY, maxX, maxY))
                    .ToList();
            }
            if (candidates.Count == 0) { LogMessage("Auto Tour: no matching on-screen content."); return; }

            var start = candidates.Where(c => Steps(c) != int.MaxValue).OrderBy(Steps).FirstOrDefault()
                        ?? candidates.OrderBy(Steps).First();

            var chain = new List<Node> { start };
            var remaining = candidates.Where(c => !ReferenceEquals(c, start)).ToList();
            Node current = start;
            var done = new HashSet<Vector2i>();
            while (remaining.Count > 0)
            {
                Node best = null;
                List<Node> bestPath = null;
                int bestSteps = int.MaxValue;
                foreach (var cand in remaining)
                {
                    var (path, steps) = FindPath(current, cand, done);
                    if (path == null) continue;
                    if (steps > n) continue;
                    if (steps < bestSteps) { bestSteps = steps; best = cand; bestPath = path; }
                }
                if (best == null) break;
                done.UnionWith(bestPath.Select(x => x.Coordinates));
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
            OpenPanelSection("tours");
        }
        catch (Exception e) { LogError("Error auto-creating tour: " + e.Message); }
    }

    private string StopLabel(TourStop s)
    {
        var node = ResolveStop(s);
        if (node != null && !string.IsNullOrEmpty(node.Name)) return $"{node.Name} ({s.X},{s.Y})";
        return $"({s.X},{s.Y})";
    }

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
        int n = Settings.Tours.AutoTourReach;
        ImGui.SetNextItemWidth(160);
        if (ImGui.SliderInt("Max steps between stops", ref n, 1, 10)) Settings.Tours.AutoTourReach = n;

        if (ImGui.Button("Create Auto Tour")) AutoCreateTour();
        ImGui.SameLine();
        ImGui.TextDisabled($"{sel.Count} selected");
        ImGui.Separator();
    }

    private void DrawToursBody()
    {
        try
        {
            {
                bool showAll = Settings.Tours.ShowTours;
                if (ImGui.Checkbox("Show Tours", ref showAll)) Settings.Tours.ShowTours = showAll;
                ImGui.SameLine();
                if (ImGui.Button("Add Tour")) AddTour();
                ImGui.SameLine();
                if (buildModeActive) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.16f, 0.55f, 0.30f, 1f));
                if (ImGui.Button(buildModeActive ? $"Building... ({Keys.Tab})" : "Build Mode")) buildModeActive = !buildModeActive;
                if (buildModeActive) ImGui.PopStyleColor();
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Left-click atlas nodes to add stops to the active tour; right-click removes. Press the Build Mode exit key (default Tab) to exit.");

                ImGui.Separator();

                DrawAutoTourSection();

                if (Settings.Tours.Tours.Count == 0)
                    ImGui.TextDisabled("No tours yet. Add one, then add stops with the Add Tour Stop hotkey\n(hover a node) or the active tour's controls.");

                Tour toDelete = null;
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
                    if (HacksCameraPanReady) {
                        ImGui.SameLine();
                        if (ImGui.Button("Goto"))
                            GotoNode(t.ResolvedStops.FirstOrDefault(n => n != null && !n.IsDone));
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pan the atlas to the next incomplete map in this tour.");
                    }

                    if (t.Skipped.Count > 0)
                        ImGui.TextDisabled($"Skipped: {string.Join(", ", t.Skipped)}");

                    if (t.Stops.Count > 0 && ImGui.TreeNode($"Stops##{t.Id}"))
                    {
                        int moveFrom = -1, moveTo = -1, removeAt = -1;
                        for (int i = 0; i < t.Stops.Count; i++)
                        {
                            ImGui.PushID(i);
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
            }
        }
        catch (Exception e)
        {
            LogError("Error drawing tours panel: " + e.Message);
        }
    }

    #endregion

    #region Expedition Data

    private class RumorDef { public string content { get; set; } public string description { get; set; } public float defaultWeight { get; set; } }

    public void UpdateRumorData()
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "json", "rumors.json");
            if (!File.Exists(path)) { LogError($"rumors.json missing: {path}"); return; }

            var defs = JsonConvert.DeserializeObject<Dictionary<string, RumorDef>>(File.ReadAllText(path));
            if (defs == null) return;

            foreach (var (text, def) in defs)
                Settings.GameData.Rumors[text] = new RumorInfo
                {
                    Text = text, Content = def.content, Description = def.description
                };
        }
        catch (Exception e) { LogError($"UpdateRumorData failed: {e.Message}"); }
    }

    private void SnapshotExpeditions()
    {
        var built = new List<Classes.Expedition>();
        var byRegion = new Dictionary<(Vector2i, string), Classes.Expedition>();
        try
        {
            var buttons = AtlasPanel?.Buttons;
            if (buttons != null)
            {
                foreach (var b in buttons)
                {
                    var kind = b?.ButtonType?.Id;
                    if (kind != ExpeditionKind && kind != RitualKind) continue;

                    var region = b.RegionCoordinate;
                    if (!byRegion.TryGetValue((region, kind), out var exp))
                    {
                        exp = new Classes.Expedition { Kind = kind, RegionCoord = region, SpawnCoord = b.Coordinate };
                        var nodes = b.RegionNodes;
                        if (nodes != null)
                            foreach (var n in nodes)
                                exp.MapCoords.Add(n.Coordinate);
                        if (kind == ExpeditionKind)
                        {
                            var rumors = b.Rumors;
                            if (rumors != null)
                                foreach (var (k, v) in rumors)
                                    exp.Rumors[k] = v;
                        }
                        byRegion[(region, kind)] = exp;
                        built.Add(exp);
                    }

                    exp.ButtonCoords.Add(b.Coordinate);

                    if (b.IsVisible)
                        exp.SpawnCoord = b.Coordinate;
                }
            }
        }
        catch (Exception e) { LogError($"SnapshotExpeditions failed: {e.Message}"); }

        built = built.OrderBy(x => x.Kind).ThenBy(x => x.RegionCoord.X).ThenBy(x => x.RegionCoord.Y).ToList();
        for (int i = 0; i < built.Count; i++) built[i].Id = i + 1;

        lock (mapCacheLock)
            expeditions = built;
    }

    private bool ExpeditionsLoaded()
    {
        lock (mapCacheLock)
        {
            foreach (var e in expeditions)
                if (e.Kind == ExpeditionKind) return true;
            return false;
        }
    }

    private float ExpeditionScore(Classes.Expedition e)
    {
        float score = 0f;
        foreach (var (text, count) in e.Rumors)
            score += Settings.RumorWeight(text) * count;
        return score;
    }

    private string ExpeditionLabel(Classes.Expedition e)
    {
        string best = null; float bestW = float.NegativeInfinity;
        foreach (var (text, _) in e.Rumors)
        {
            float w = Settings.RumorWeight(text);
            if (w > bestW && Settings.GameData.Rumors.TryGetValue(text, out var info))
            { bestW = w; best = info.Content; }
        }
        best ??= $"Region {e.RegionCoord.X},{e.RegionCoord.Y}";
        return $"{best}  ({e.MapCoords.Count} maps)";
    }

    private bool ExpeditionMatchesSearch(Classes.Expedition e, string text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var (rumorText, _) in e.Rumors)
        {
            if (rumorText.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (Settings.GameData.Rumors.TryGetValue(rumorText, out var info)
                && (info.Content?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)) return true;
        }
        return false;
    }

    #endregion

    #region Expedition Markers

    private void DrawExpeditionHoverRings()
    {
        if (frameHoverExpeditionMaps.Count == 0) return;
        foreach (var (node, rect) in nodePositions)
        {
            if (!frameHoverExpeditionMaps.Contains(node.Coordinates)) continue;
            float radius = MathF.Max(rect.Width, rect.Height) * 0.62f;
            Graphics.DrawCircle(rect.Center, radius, frameHoverExpeditionTint, 3f, 24);
        }
    }

    private void DrawExpeditionHighlight()
    {
        if (highlightedExpeditionCoords.Count == 0) return;
        Color color = Settings.Expeditions.HighlightColor;
        foreach (var (node, rect) in nodePositions)
        {
            if (!highlightedExpeditionCoords.Contains(node.Coordinates)) continue;
            var center = rect.Center;
            float radius = MathF.Max(rect.Width, rect.Height) * 0.62f;
            Graphics.DrawCircle(center, radius, color, 3f, 24);
        }
    }

    private sealed class ExpeditionPanel
    {
        public Vector2 TopLeft;
        public float Width, Height, Pad, LineH;
        public System.Drawing.Color Bg;
        public List<(string text, System.Drawing.Color color)> Lines;
    }
    private sealed class RumorPanelMemo
    {
        public Classes.Expedition Exp;
        public int Wver;
        public bool Hover;
        public List<(string text, System.Drawing.Color color)> Lines;
        public float BoxW, BoxH, LineH;
    }
    private readonly Dictionary<Vector2i, RumorPanelMemo> rumorPanelMemo = [];
    private const string ExpeditionKind = "Ocean";
    private const string RitualKind = "Forest";
    private const string BreachKind = "Breach";
    private const string TowerKind = "Tower";
    private const float MarkerScale = 3f;

    private static readonly string[] MarkerArtKinds = { ExpeditionKind, RitualKind, BreachKind, TowerKind };

    private static string MarkerArt(string kind, bool hover) => kind switch
    {
        ExpeditionKind => hover ? "btn-ocean-hover.png" : "btn-ocean-normal.png",
        RitualKind => hover ? "btn-forest-hover.png" : "btn-forest-normal.png",
        BreachKind => hover ? "btn-breach-hover.png" : "btn-breach-normal.png",
        TowerKind => hover ? "btn-tower-hover.png" : "btn-tower-normal.png",
        _ => hover ? "btn-ocean-hover.png" : "btn-ocean-normal.png",
    };

    private readonly List<(RectangleF rect, string tex, System.Drawing.Color tint)> frameExpeditionIcons = new();
    private readonly List<ExpeditionPanel> frameExpeditionPanels = new();
    private readonly HashSet<Vector2i> frameHoverExpeditionMaps = new();
    private System.Drawing.Color frameHoverExpeditionTint;
    private (Vector2i region, string kind)? frameHoverButtonRegion;

    private static System.Drawing.Color ExpeditionTint(Classes.Expedition e)
    {
        int seed = unchecked(e.RegionCoord.X * 73856093 ^ e.RegionCoord.Y * 19349663 ^ e.Kind.GetHashCode()) & 0x7fffffff;
        float h = (seed % 1000) * 0.61803398875f;
        h -= MathF.Floor(h);
        return ColorUtils.ColorFromHSV(h * 360f, 0.55f, 1f);
    }

    private void LayoutExpeditions()
    {
        frameExpeditionIcons.Clear();
        frameExpeditionPanels.Clear();
        frameHoverExpeditionMaps.Clear();

        LayoutAtlasButtonDebug();
        LayoutRitualForetellings();

        var snapshot = expeditions;

        if (frameHoverButtonRegion is (Vector2i hoverRegion, string hoverKind))
        {
            Classes.Expedition he = null;
            foreach (var x in snapshot)
                if (x.Kind == hoverKind && x.RegionCoord.Equals(hoverRegion)) { he = x; break; }
            if (he != null)
            {
                frameHoverExpeditionMaps.UnionWith(he.MapCoords);
                frameHoverExpeditionTint = ExpeditionTint(he);
            }
        }

        if (frameLogbookPopup != null)
            LayoutPopupOverlay();

        var markerMode = Settings.Features.ExpeditionMarkers;
        bool ritualMarkers = Settings.ContentDisplay.ShowRitualMarkers;
        if (markerMode == ExpeditionMarkers.Off && !ritualMarkers) return;

        var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
        var mouse = ImGuiNET.ImGui.GetMousePos();

        var steps = markerMode == ExpeditionMarkers.Nearest ? ComputeStepCounts() : null;

        foreach (var e in snapshot)
        {
            bool ritual = e.Kind == RitualKind;
            if (ritual ? !ritualMarkers : markerMode == ExpeditionMarkers.Off) continue;

            var tint = ExpeditionTint(e);

            bool revealed = false; Vector2i revealedCoord = default;
            foreach (var c in e.ButtonCoords)
                if (frameVisibleExpeditionButtonCoords.Contains(c)) { revealed = true; revealedCoord = c; break; }

            if (revealed)
            {
                bool buttonHover = frameHoverButtonRegion is (Vector2i br, string bk)
                    && bk == e.Kind && br.Equals(e.RegionCoord);
                if (!ritual && !buttonHover && NodeRectAt(revealedCoord, out var gameRect) && IsOnScreen(gameRect.Center))
                    LayoutPossibleRumors(e, gameRect, screen, gameRect.Contains(mouse), tint, below: true);
                continue;
            }

            IEnumerable<Vector2i> coords = e.ButtonCoords;
            if (!ritual && markerMode == ExpeditionMarkers.Nearest)
            {
                bool found = false; Vector2i best = default; int bestSteps = int.MaxValue;
                foreach (var c in e.ButtonCoords)
                {
                    int s = steps.TryGetValue(c, out var v) ? v : int.MaxValue;
                    if (!found || s < bestSteps) { found = true; bestSteps = s; best = c; }
                }
                coords = found ? new[] { best } : System.Array.Empty<Vector2i>();
            }

            foreach (var coord in coords)
            {
                if (!MarkerRect(coord, out var iconRect)) continue;

                bool hover = iconRect.Contains(mouse);
                frameExpeditionIcons.Add((iconRect, MarkerArt(e.Kind, hover), System.Drawing.Color.White));
                cachedExcludeRects.Add(iconRect);

                if (hover)
                {
                    frameHoverExpeditionMaps.Clear();
                    frameHoverExpeditionMaps.UnionWith(e.MapCoords);
                    frameHoverExpeditionTint = tint;
                }

                if (!ritual) LayoutPossibleRumors(e, iconRect, screen, hover, tint);
            }
        }
    }

    private bool NodeRectAt(Vector2i coord, out RectangleF rect)
    {
        rect = default;
        Node node;
        lock (mapCacheLock)
            if (!mapCache.TryGetValue(coord, out node)) node = null;
        if (node == null) return false;
        rect = GetNodeRect(node);
        return !rect.IsEmpty && rect.Width > 0;
    }

    private bool MarkerRect(Vector2i coord, out RectangleF iconRect)
    {
        iconRect = default;
        if (!NodeRectAt(coord, out var rect)) return false;
        float size = MathF.Max(rect.Width, rect.Height) * MarkerScale;
        iconRect = new RectangleF(rect.Center.X - size / 2f, rect.Top - size, size, size);
        return IsOnScreen(iconRect.Center);
    }

    private static string RumorRow(string content, string hint)
        => string.IsNullOrEmpty(hint) ? "- " + content : $"- {content} ({hint})";

    private System.Drawing.Color RumorTextColor(float w, System.Drawing.Color fallback)
    {
        if (!Settings.ContentDisplay.ColorRumorsByWeight || MathF.Abs(w) < 0.5f) return fallback;
        var c = WeightRampColor(w);
        return ColorUtils.WithAlphaOf(c, fallback);
    }

    private void LayoutPossibleRumors(Classes.Expedition e, RectangleF iconRect, Vector2 screen, bool hover, System.Drawing.Color tint, bool below = false)
    {
        try
        {
            const float pad = 6f;
            if (!rumorPanelMemo.TryGetValue(e.RegionCoord, out var m)
                || !ReferenceEquals(m.Exp, e) || m.Wver != weightsRecalcVersion || m.Hover != hover)
            {
                var rows = new List<(string content, string hint, string desc, float w)>();
                foreach (var (text, _) in e.Rumors)
                    if (Settings.GameData.Rumors.TryGetValue(text, out var info))
                        rows.Add((info.Content, info.Text, info.Description, Settings.RumorWeight(text)));
                if (rows.Count == 0) { rumorPanelMemo.Remove(e.RegionCoord); return; }
                rows.Sort((a, b) => b.w.CompareTo(a.w));

                var sub = System.Drawing.Color.FromArgb(180, 180, 180, 180);
                var lines = new List<(string, System.Drawing.Color)> { ($"Expedition #{e.Id}", tint) };
                foreach (var (content, hint, desc, w) in rows)
                {
                    lines.Add((RumorRow(content, hint), RumorTextColor(w, OverlayText)));
                    if (hover && !string.IsNullOrEmpty(desc))
                        lines.Add(("    " + desc, sub));
                }
                var (boxW, boxH, lineH) = MeasurePanel(lines, pad);
                m = new RumorPanelMemo { Exp = e, Wver = weightsRecalcVersion, Hover = hover, Lines = lines, BoxW = boxW, BoxH = boxH, LineH = lineH };
                rumorPanelMemo[e.RegionCoord] = m;
            }

            float x = iconRect.Center.X - m.BoxW / 2f;
            float y = below ? iconRect.Bottom + 2f : iconRect.Top - m.BoxH - 2f;
            x = Math.Clamp(x, 0f, MathF.Max(0f, screen.X - m.BoxW));
            if (y < 0f) y = iconRect.Bottom + 2f;
            if (y + m.BoxH > screen.Y) y = MathF.Max(0f, screen.Y - m.BoxH);

            RegisterPanel(new Vector2(x, y), m.BoxW, m.BoxH, pad, m.LineH, m.Lines);
        }
        catch (Exception ex) { LogError($"LayoutPossibleRumors failed: {ex.Message}"); }
    }

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

    private void RegisterPanel(Vector2 tl, float w, float h, float pad, float lineH, List<(string text, System.Drawing.Color color)> lines,
        System.Drawing.Color? bg = null)
    {
        frameExpeditionPanels.Add(new ExpeditionPanel { TopLeft = tl, Width = w, Height = h, Pad = pad, LineH = lineH, Lines = lines,
            Bg = bg ?? OverlayBg });
        cachedExcludeRects.Add(new RectangleF(tl.X, tl.Y, w, h));
    }

    private void DrawExpeditions()
    {
        if (frameExpeditionIcons.Count == 0 && frameExpeditionPanels.Count == 0) return;
        try
        {
            var fullUV = new RectangleF(0, 0, 1, 1);
            foreach (var (rect, tex, tint) in frameExpeditionIcons)
                Graphics.DrawImage(tex, rect, fullUV, tint);

            foreach (var p in frameExpeditionPanels)
            {
                Graphics.DrawBox(p.TopLeft, new Vector2(p.TopLeft.X + p.Width, p.TopLeft.Y + p.Height), p.Bg, 5f);
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

    private Node NearestNodeInExpedition(Classes.Expedition e)
    {
        try
        {
            if (e == null || e.MapCoords.Count == 0) return null;
            var steps = ComputeStepCounts();

            Vector2i best = e.MapCoords[0];
            int bestSteps = int.MaxValue;
            foreach (var c in e.MapCoords)
            {
                int s = steps.TryGetValue(c, out var v) ? v : int.MaxValue;
                if (s < bestSteps) { bestSteps = s; best = c; }
            }
            if (bestSteps == int.MaxValue)
                LogError($"NearestNodeInExpedition: no map in this expedition is reachable from the explored frontier, falling back to first map at {best}");

            Node node;
            lock (mapCacheLock)
                if (!mapCache.TryGetValue(best, out node)) node = null;

            if (node == null) LogError($"NearestNodeInExpedition: no cached node at {best}");
            return node;
        }
        catch (Exception ex) { LogError($"NearestNodeInExpedition failed: {ex.Message}"); return null; }
    }

    private void WaypointNearestInExpedition(Classes.Expedition e)
    {
        var node = NearestNodeInExpedition(e);
        if (node != null) AddWaypoint(node);
    }

    #endregion

    #region Atlas Button Debug

    private readonly List<(Vector2i coord, List<(string text, System.Drawing.Color color)> lines)> frameButtonDebug = new();

    private const int RegionSize = 16;

    private static Vector2i RegionOf(Vector2i coord) => new(
        (int)MathF.Floor(coord.X / (float)RegionSize),
        (int)MathF.Floor(coord.Y / (float)RegionSize));

    private static System.Drawing.Color ButtonKindColor(string kind) => kind switch
    {
        ExpeditionKind => System.Drawing.Color.FromArgb(255, 120, 200, 255),
        RitualKind => System.Drawing.Color.FromArgb(255, 255, 152, 105),
        "Breach" => System.Drawing.Color.FromArgb(255, 200, 145, 255),
        "Tower" => System.Drawing.Color.FromArgb(255, 255, 210, 120),
        _ => OverlayText,
    };

    private string ButtonTooltipText(ExileCore2.PoEMemory.Elements.AtlasElements.AtlasButtonNode b)
    {
        try
        {
            var children = b.Children;
            var visual = children != null && children.Count > 0 ? children[0] : null;
            var tip = visual?.Tooltip;
            if (tip == null || !tip.IsVisible) return null;
            var texts = new List<string>();
            ReadPopupRumors(tip, texts);
            return texts.Count == 0 ? null : string.Join("\n", texts);
        }
        catch { return null; }
    }

    private void ScanAtlasButtonDebug(IList<ExileCore2.PoEMemory.Elements.AtlasElements.AtlasButtonNode> buttons)
    {
        frameButtonDebug.Clear();
        if (!Settings.Features.DebugAtlasButtons || buttons == null) return;
        try
        {
            int i = -1;
            foreach (var b in buttons)
            {
                i++;
                if (b == null) continue;

                var kind = b.ButtonType?.Id ?? "(null)";
                var c = b.Coordinate;
                var r = b.RegionCoordinate;

                int bx0 = r.X * RegionSize, bx1 = bx0 + RegionSize - 1;
                int by0 = r.Y * RegionSize, by1 = by0 + RegionSize - 1;

                var nodes = b.RegionNodes;
                int n = nodes?.Count ?? 0;
                string ext = "-";
                int outside = 0;
                if (n > 0)
                {
                    int x0 = int.MaxValue, x1 = int.MinValue, y0 = int.MaxValue, y1 = int.MinValue;
                    foreach (var nd in nodes)
                    {
                        var q = nd.Coordinate;
                        if (q.X < x0) x0 = q.X;
                        if (q.X > x1) x1 = q.X;
                        if (q.Y < y0) y0 = q.Y;
                        if (q.Y > y1) y1 = q.Y;
                        if (q.X < bx0 || q.X > bx1 || q.Y < by0 || q.Y > by1) outside++;
                    }
                    ext = $"x {x0}..{x1}  y {y0}..{y1}";
                }

                bool formulaOk = RegionOf(c).Equals(r);
                string verdict = n == 0 ? "no nodes"
                    : outside == 0 ? "FITS region block"
                    : $"OUTSIDE block: {outside}/{n} nodes";
                if (!formulaOk) verdict += "  (region formula mismatch!)";
                var verdictColor = n == 0 ? OverlayText
                    : outside == 0 ? System.Drawing.Color.FromArgb(255, 130, 230, 130)
                    : System.Drawing.Color.FromArgb(255, 255, 140, 140);

                int rum = 0;
                try { rum = b.Rumors?.Count ?? 0; } catch { }

                var col = ButtonKindColor(kind);
                var lines = new List<(string, System.Drawing.Color)>
                {
                    ($"#{i} {kind}{(b.IsVisible ? "  VISIBLE" : "")}", col),
                    ($"at {c.X},{c.Y}   region {r.X},{r.Y}", OverlayText),
                    ($"nodes {n}   rumors {rum}", OverlayText),
                    ($"extent {ext}", OverlayText),
                    ($"block  x {bx0}..{bx1}  y {by0}..{by1}", OverlayText),
                    (verdict, verdictColor),
                };

                if (b.IsVisible)
                {
                    var kids = b.Children;
                    var vis0 = kids != null && kids.Count > 0 ? kids[0] : null;
                    var tt = vis0?.Tooltip;
                    var tr = tt?.GetClientRect() ?? default;
                    lines.Add(($"shiny {(vis0?.HasShinyHighlight ?? false)}   tipVis {(tt?.IsVisible ?? false)}   tipRect {tr.Width:0}x{tr.Height:0}", OverlayText));

                    var tip = ButtonTooltipText(b);
                    if (!string.IsNullOrWhiteSpace(tip))
                        foreach (var ln in tip.Split('\n'))
                            if (!string.IsNullOrWhiteSpace(ln)) lines.Add(("  " + ln.Trim(), col));
                }

                frameButtonDebug.Add((c, lines));
            }
        }
        catch (Exception e) { LogError($"ScanAtlasButtonDebug failed: {e.Message}"); }
    }

    private void LayoutAtlasButtonDebug()
    {
        if (frameButtonDebug.Count == 0) return;
        try
        {
            const float pad = 5f;
            var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
            foreach (var (coord, lines) in frameButtonDebug)
            {
                Node node;
                lock (mapCacheLock)
                    if (!mapCache.TryGetValue(coord, out node)) node = null;
                if (node == null) continue;

                var rect = GetNodeRect(node);
                if (rect.IsEmpty || rect.Width <= 0) continue;
                if (!IsOnScreen(rect.Center)) continue;

                var (boxW, boxH, lineH) = MeasurePanel(lines, pad);
                float x = Math.Clamp(rect.Center.X - boxW / 2f, 0f, MathF.Max(0f, screen.X - boxW));
                float y = rect.Bottom + 4f;
                if (y + boxH > screen.Y) y = MathF.Max(0f, rect.Top - boxH - 4f);

                RegisterPanel(new Vector2(x, y), boxW, boxH, pad, lineH, lines);
            }
        }
        catch (Exception e) { LogError($"LayoutAtlasButtonDebug failed: {e.Message}"); }
    }

    #endregion

    #region Ritual Foretelling

    private const int RitualGateOffset = 0x61F;

    private class RitualModDef { public string id { get; set; } public string text { get; set; } public string item { get; set; } public int count { get; set; } }

    private static List<RitualModDef> ritualMods;
    private static Dictionary<string, RitualModDef> ritualModsByKey;

    public void UpdateRitualModData()
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "json", "ritualmods.json");
            if (!File.Exists(path)) { LogError($"ritualmods.json missing: {path}"); return; }
            ritualMods = JsonConvert.DeserializeObject<List<RitualModDef>>(File.ReadAllText(path));
            if (ritualMods == null) return;
            foreach (var r in ritualMods)
                if (!string.IsNullOrEmpty(r.id))
                    Settings.GameData.Foretellings[r.id] = string.IsNullOrWhiteSpace(r.text) ? r.id : r.text;
            ritualModsByKey = new Dictionary<string, RitualModDef>(StringComparer.Ordinal);
            foreach (var r in ritualMods)
                if (!string.IsNullOrEmpty(r.id)) ritualModsByKey[r.id] = r;
            RebuildWeightEditorIds();
        }
        catch (Exception e) { LogError($"UpdateRitualModData failed: {e.Message}"); }
    }

    private Func<BaseItemType, double> ninjaPrice;

    private Func<BaseItemType, double> NinjaBridge()
    {
        if (ninjaPrice != null) return ninjaPrice;
        try { ninjaPrice = GameController.PluginBridge.GetMethod<Func<BaseItemType, double>>("NinjaPrice.GetBaseItemTypeValue"); }
        catch (Exception e) { LogError($"NinjaPricer bridge lookup failed: {e.Message}"); }
        return ninjaPrice;
    }

    private Dictionary<string, BaseItemType> BaseItemsByName()
    {
        var index = new Dictionary<string, BaseItemType>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var b in GameController.Files.BaseItemTypes.Contents.Values)
                if (!string.IsNullOrEmpty(b?.BaseName)) index.TryAdd(b.BaseName, b);
        }
        catch (Exception e) { LogError($"BaseItemsByName failed: {e.Message}"); }
        return index;
    }

    private readonly Dictionary<string, double> foretellPrices = new();
    private string foretellPriceUnit = "ex";
    private const string DivineBaseName = "Divine Orb";

    public double ForetellPrice(string id) =>
        id != null && foretellPrices.TryGetValue(id, out double v) ? v : 0d;

    public int RefreshForetellPrices(out string note)
    {
        note = null;
        foretellPrices.Clear();
        try
        {
            var rows = ritualMods;
            if (rows == null || rows.Count == 0) { note = "ritualmods.json not loaded"; return 0; }

            var bridge = NinjaBridge();
            if (bridge == null) { note = "NinjaPricer not loaded"; return 0; }

            var bases = BaseItemsByName();
            double divRate = bases.TryGetValue(DivineBaseName, out var divBase) ? bridge(divBase) : 0d;
            if (divRate > 0) foretellPriceUnit = "div";
            else { divRate = 1d; foretellPriceUnit = "ex"; }

            int unpriced = 0;
            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.id) || string.IsNullOrEmpty(r.item)) continue;
                if (!bases.TryGetValue(r.item, out var bit)) { unpriced++; continue; }
                double unit = bridge(bit);
                if (unit <= 0) { unpriced++; continue; }
                foretellPrices[r.id] = unit * Math.Max(1, r.count) / divRate;
            }
            note = unpriced > 0 ? $"{foretellPrices.Count} priced, {unpriced} unpriced" : $"{foretellPrices.Count} priced";
            return foretellPrices.Count;
        }
        catch (Exception e) { note = e.Message; LogError($"RefreshForetellPrices failed: {e.Message}"); return 0; }
    }

    public int AutoAssignForetellWeights(bool logScale, out string note)
    {
        note = null;
        try
        {
            var rows = ritualMods;
            if (rows == null || rows.Count == 0) { note = "ritualmods.json not loaded"; return 0; }


            RefreshForetellPrices(out _);
            var values = foretellPrices;
            if (values.Count == 0) { note = "no prices available"; return 0; }

            double max = 0;
            foreach (var v in values.Values) if (v > max) max = v;
            if (max <= 0) { note = "no prices available"; return 0; }

            foreach (var (id, v) in values)
            {
                double frac = logScale ? Math.Log(1 + v) / Math.Log(1 + max) : v / max;
                Settings.Active.Foretellings[id] = (float)Math.Round(frac * maxForetellWeight, 1);
            }

            weightsDirty = true;
            note = $"{values.Count} weighted";
            return values.Count;
        }
        catch (Exception e) { note = e.Message; LogError($"AutoAssignForetellWeights failed: {e.Message}"); return 0; }
    }

    private sealed class RitualRng
    {
        private readonly uint[] s = new uint[5];

        public RitualRng(uint k0, uint k1, uint k2, uint k3)
        {
            var key = new[] { k0, k1, k2, k3 };
            s[1] = 1077108818u;
            s[2] = unchecked((uint)-811371972);
            s[3] = 1017929585u;
            s[4] = 932445695u;

            int i = 1, cnt = 0, k = 0;
            while (cnt < 4 && k < 7)
            {
                int j = (i + 1) % 4;
                uint x = s[j + 1];
                uint m = s[i % 4 + 1] ^ s[(i + 3) % 4 + 1] ^ x;
                uint t = unchecked(1664525u * (m ^ (m >> 27)));
                s[j + 1] = unchecked(x + t);
                uint u = unchecked((uint)i + key[k] + t);
                s[(i + 2) % 4 + 1] = unchecked(s[(i + 2) % 4 + 1] + u);
                s[i % 4 + 1] = u;
                i = j; cnt++; k++;
            }
            while (cnt < 7)
            {
                int j = (i + 1) % 4;
                uint x = s[j + 1];
                uint m = s[i % 4 + 1] ^ s[(i + 3) % 4 + 1] ^ x;
                uint t = unchecked(1664525u * (m ^ (m >> 27)));
                s[j + 1] = unchecked(x + t);
                uint u = unchecked((uint)i + t);
                s[(i + 2) % 4 + 1] = unchecked(s[(i + 2) % 4 + 1] + u);
                s[i % 4 + 1] = u;
                i = j; cnt++;
            }
            for (int r = 0; r < 4; r++)
            {
                int j = (i + 1) % 4;
                uint x = s[j + 1];
                uint m = unchecked(s[i % 4 + 1] + x + s[(i + 3) % 4 + 1]);
                m ^= m >> 27;
                uint t = unchecked(1566083941u * m);
                s[j + 1] = t ^ x;
                uint u = unchecked(t - (uint)i);
                s[(i + 2) % 4 + 1] ^= u;
                s[i % 4 + 1] = u;
                i = j;
            }
            for (int r = 0; r < 8; r++) Next();
        }

        public uint Next()
        {
            uint z1 = s[2], z4 = s[4], z0 = s[1];
            s[0]++;
            s[1] = z1;
            uint a = s[3] ^ z1 ^ (z0 & 0x7FFFFFFFu);
            uint b = (2u * z4) ^ z4;
            uint c = b ^ a ^ (a >> 1);
            s[4] = c;
            uint mask = ((b ^ a ^ (a >> 1)) & 1u) != 0 ? 0xFFFFFFFFu : 0u;
            s[2] = s[3] ^ (mask & 0x8F7011EEu);
            uint d = b ^ (c << 10) ^ (mask & 0xFC78FF1Fu);
            s[3] = d;
            uint t = unchecked(z1 + (d >> 8));
            return c ^ t ^ (((t & 1u) != 0 ? 0xFFFFFFFFu : 0u) & 0x3793FDFFu);
        }

        public uint Below(uint n)
        {
            if (n <= 1) return 0;
            if (0xFFFFFFFFu % n == n - 1) return Next() % n;
            uint cap = 0xFFFFFFFFu / n;
            while (true)
            {
                uint v = Next();
                if (v / n < cap) return v % n;
            }
        }
    }

    private sealed class ForetellRow
    {
        public string key;
        public uint weight;
        public List<string> fam;
        public bool enabled;
    }

    private List<ForetellRow> foretellRows;

    private readonly Dictionary<(int depth, int index), List<RitualModDef>> foretellRollMemo = new();
    private int foretellRollSeed = int.MinValue;
    private int foretellRollLive = -1;
    private bool foretellRollOk;
    private bool foretellRollFailed;

    private bool BuildForetellRows()
    {
        if (foretellRows != null) return foretellRows.Count > 0;
        try
        {
            var rows = GameController.Files.RitualAtlasLineMods?.EntriesList;
            if (rows == null || rows.Count == 0) return false;

            var built = new List<ForetellRow>(rows.Count);
            foreach (var r in rows)
            {
                var mod = r?.Mod1;
                if (mod == null) continue;
                int statId = r.Stat?.ID ?? 0;
                built.Add(new ForetellRow
                {
                    key = mod.Key,
                    weight = (uint)Math.Max(0, r.Weight),
                    fam = mod.Groups,
                    enabled = statId == 0 || RitualStat(statId) > 0,
                });
            }
            int usable = built.Count(b => b.enabled && b.weight > 0);
            if (Settings.Features.DebugRitualRolls)
                LogMessage($"ForetellRows n={built.Count} usable={usable}");
            if (usable == 0) return false;

            foretellRows = built;
            return true;
        }
        catch (Exception e) { LogError($"BuildForetellRows failed: {e.Message}"); return false; }
    }

    private ForetellRow ForetellPick(int seed, int depth, int index, int modCount, List<string> exclude)
    {
        if (!BuildForetellRows()) return null;
        var rng = new RitualRng((uint)seed, (uint)depth, (uint)index, (uint)modCount);
        uint total = 0;
        ForetellRow chosen = null;
        foreach (var r in foretellRows)
        {
            if (!r.enabled || r.weight == 0) continue;
            if (exclude != null && r.fam != null)
            {
                bool clash = false;
                foreach (var f in r.fam) if (exclude.Contains(f)) { clash = true; break; }
                if (clash) continue;
            }
            total += r.weight;
            if (rng.Below(total) < r.weight) chosen = r;
        }
        return chosen;
    }

    private static RitualModDef ForetellDef(string key) =>
        ritualModsByKey != null && ritualModsByKey.TryGetValue(key, out var def)
            ? def
            : new RitualModDef { id = key, text = key };

    private List<RitualModDef> ForetellRoll(int depth, int index)
    {
        var empty = new List<RitualModDef>();
        var panel = AtlasPanel;
        if (panel == null) return empty;

        int seed = RitualSeedRaw();
        int live = RitualLineCount();
        if (seed != foretellRollSeed)
        {
            foretellRollOk = false;
            foretellRollFailed = false;
            foretellRows = null;
        }
        if (seed != foretellRollSeed || live != foretellRollLive)
        {
            foretellRollMemo.Clear();
            foretellRollSeed = seed;
            foretellRollLive = live;
        }
        if (foretellRollMemo.TryGetValue((depth, index), out var cached)) return cached;

        var first = ForetellPick(seed, depth, index, 0, null);
        if (first == null) { foretellRollFailed = true; return empty; }

        var result = new List<RitualModDef>(2) { ForetellDef(first.key) };
        if (RitualSecondMod(seed, depth, index))
        {
            var second = ForetellPick(seed, depth, index, 1, first.fam);
            if (second != null) result.Add(ForetellDef(second.key));
        }

        if (Settings.Features.DebugRitualRolls)
            LogMessage($"ForetellRoll seed={seed} live={live} d={depth} i={index} -> [{string.Join("|", result.Select(p => p.id))}]");

        foretellRollOk = true;
        foretellRollMemo[(depth, index)] = result;
        return result;
    }

    private bool RitualSecondMod(int seed, int depth, int index)
    {
        try
        {
            int chance = RitualStat(RitualSecondModStat);
            if (chance <= 0) return false;
            if (chance >= 100) return true;

            var gate = new RitualRng((uint)seed, (uint)depth, (uint)index, RitualSecondModSalt);
            uint roll = gate.Below(100);
            if (Settings.Features.DebugRitualRolls)
                LogMessage($"RitualSecondMod d={depth} i={index} chance={chance} roll={roll} -> {roll < (uint)chance}");
            return roll < (uint)chance;
        }
        catch (Exception e) { LogError($"RitualSecondMod failed: {e.Message}"); return false; }
    }

    private float ForetellValueOf(List<RitualModDef> picks)
    {
        float v = 0f;
        if (picks != null)
            foreach (var p in picks) v += (float)ForetellPrice(p.id);
        return v;
    }

    private float ForetellWeightOf(List<RitualModDef> picks)
    {
        float w = 0f;
        if (picks != null)
            foreach (var p in picks) w += Settings.ForetellingWeight(p.id);
        return w;
    }

    private const int RitualAdjBeginOffset = 0x578;
    private const int RitualAdjEndOffset = 0x580;
    private const int RitualAdjStride = 68;
    private const int RitualSeedOffset = 0x624;
    private const int RitualLineBeginOffset = 0x648;
    private const int RitualLineEndOffset = 0x650;

    private int RitualSeedRaw()
    {
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            return page == 0 ? 0 : GameController.Memory.Read<int>(page + RitualSeedOffset);
        }
        catch { return 0; }
    }

    private int RitualLineCountRaw()
    {
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            if (page == 0) return -1;
            var mem = GameController.Memory;
            long b = mem.Read<long>(page + RitualLineBeginOffset);
            long e = mem.Read<long>(page + RitualLineEndOffset);
            if (b == 0 || e < b) return -1;
            long n = (e - b) / 8;
            return n < 0 || n > 64 ? -1 : (int)n;
        }
        catch { return -1; }
    }

    private int RitualLineCount()
    {
        int n = RitualLineCountRaw();
        if (n >= 0) return n;
        try { return AtlasPanel?.SelectedForetoldRitualMaps?.Count ?? 0; }
        catch { return 0; }
    }

    private List<Vector2i> RitualSelectedRaw()
    {
        int n = RitualLineCountRaw();
        if (n < 0) return null;
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            if (page == 0) return null;
            var mem = GameController.Memory;
            long b = mem.Read<long>(page + RitualLineBeginOffset);
            var list = new List<Vector2i>(n);
            for (int i = 0; i < n; i++)
                list.Add(new Vector2i(mem.Read<int>(b + i * 8), mem.Read<int>(b + i * 8 + 4)));
            return list;
        }
        catch { return null; }
    }

    private static bool SameCoord(Vector2i a, Vector2i b) => a.X == b.X && a.Y == b.Y;

    private List<Vector2i> RitualSelected()
    {
        var raw = RitualSelectedRaw();
        if (raw != null) return raw;
        try { return new List<Vector2i>(AtlasPanel?.SelectedForetoldRitualMaps ?? new List<Vector2i>()); }
        catch { return new List<Vector2i>(); }
    }

    private bool RitualNeighbours(Vector2i coord, List<Vector2i> into)
    {
        into.Clear();
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            if (page == 0) return false;
            var mem = GameController.Memory;
            long b = mem.Read<long>(page + RitualAdjBeginOffset);
            long e = mem.Read<long>(page + RitualAdjEndOffset);
            if (b == 0 || e < b) return false;
            long n = (e - b) / RitualAdjStride;
            if (n <= 0 || n > 200000) return false;

            int want = (coord.X << 16) + coord.Y;
            long lo = 0, cnt = n;
            while (cnt > 0)
            {
                long half = cnt / 2;
                long rec = b + (lo + half) * RitualAdjStride;
                int key = (mem.Read<int>(rec) << 16) + mem.Read<int>(rec + 4);
                if (key >= want) cnt = half;
                else { lo += half + 1; cnt -= half + 1; }
            }
            if (lo >= n) return false;

            long found = b + lo * RitualAdjStride;
            if (mem.Read<int>(found) != coord.X || mem.Read<int>(found + 4) != coord.Y) return false;
            for (int i = 0; i < 5; i++)
            {
                long p = found + 8 + i * 12;
                var c = new Vector2i(mem.Read<int>(p), mem.Read<int>(p + 4));
                if (c.X == 0 && c.Y == 0) continue;
                into.Add(c);
            }
            return true;
        }
        catch { return false; }
    }

    private const int RitualSessionOffset = 0x308;
    private const int RitualAccountOffset = 0x1B0;
    private const int RitualStatsOffset = 0x3A20;
    private const int RitualStatsBegin = 1032;
    private const int RitualStatsEnd = 1040;
    private const int RitualStatStride = 40;
    private const int RitualMaxLineStat = 26381;
    private const int RitualSecondModStat = 26382;
    private const uint RitualSecondModSalt = 0x91DA3AD9u;
    private const int RitualBaseLineLength = 5;

    private int RitualStat(int statId)
    {
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            if (page == 0) return 0;
            var mem = GameController.Memory;
            long a = mem.Read<long>(page + RitualSessionOffset);
            if (a == 0) return 0;
            long b = mem.Read<long>(a + RitualAccountOffset);
            if (b == 0) return 0;
            long stats = mem.Read<long>(b + RitualStatsOffset);
            if (stats == 0) return 0;

            long begin = mem.Read<long>(stats + RitualStatsBegin);
            long end = mem.Read<long>(stats + RitualStatsEnd);
            if (begin == 0 || end < begin || end - begin > RitualStatStride * 8192) return 0;

            for (long p = begin; p < end; p += RitualStatStride)
                if (mem.Read<int>(p) == statId) return mem.Read<int>(p + 8);
        }
        catch { }
        return 0;
    }

    private const long RitualMaxLineCacheMs = 500;
    private long ritualMaxLineAt;
    private int ritualMaxLineCached = RitualBaseLineLength;

    private int RitualMaxLine()
    {
        long now = Environment.TickCount64;
        if (now - ritualMaxLineAt < RitualMaxLineCacheMs) return ritualMaxLineCached;
        ritualMaxLineAt = now;
        ritualMaxLineCached = RitualBaseLineLength + RitualStat(RitualMaxLineStat);
        return ritualMaxLineCached;
    }

    private const int RitualValidBeginOffset = 0x630;
    private const int RitualValidEndOffset = 0x638;

    private List<Vector2i> RitualValidTargets()
    {
        var list = new List<Vector2i>();
        try
        {
            long page = AtlasPanel?.Address ?? 0;
            if (page == 0) return list;
            var mem = GameController.Memory;
            long b = mem.Read<long>(page + RitualValidBeginOffset);
            long e = mem.Read<long>(page + RitualValidEndOffset);
            if (b == 0 || e < b || e - b > 8 * 4096) return list;
            for (long p = b; p < e; p += 8)
                list.Add(new Vector2i(mem.Read<int>(p), mem.Read<int>(p + 4)));
        }
        catch { }
        list.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return list;
    }

    private HashSet<Vector2i> ritualValidNow = new();
    private long ritualValidNowAt;
    private const int RitualValidCacheMs = 500;

    private HashSet<Vector2i> RitualValidNow()
    {
        long now = Environment.TickCount64;
        if (now - ritualValidNowAt > RitualValidCacheMs)
        {
            ritualValidNow = new HashSet<Vector2i>(RitualValidTargets());
            ritualValidNowAt = now;
            if (Settings.Features.DebugRitualRolls)
                LogMessage($"RitualValidNow n={ritualValidNow.Count} [{string.Join(" ", ritualValidNow.Select(c => $"({c.X},{c.Y})"))}]");
        }
        return ritualValidNow;
    }

    private bool RitualSelectableNow(Vector2i coord)
    {
        if (!RitualSelectable(coord)) return false;
        var valid = RitualValidNow();
        return valid.Count == 0 || valid.Contains(coord);
    }

    private bool RitualSelectable(Vector2i coord)
    {
        lock (mapCacheLock)
            return !mapCache.TryGetValue(coord, out var n) || n == null || !n.IsDone;
    }

    private List<Vector2i> RitualCandidates(Vector2i tip, List<Vector2i> selected)
    {
        var raw = new List<Vector2i>();
        var result = new List<Vector2i>();
        if (!RitualNeighbours(tip, raw)) return result;
        foreach (var c in raw)
        {
            bool taken = false;
            foreach (var s in selected) if (SameCoord(s, c)) { taken = true; break; }
            if (!taken) result.Add(c);
        }
        result.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return result;
    }

    private string PlanLabel(Vector2i coord) =>
        $"{NodeNameAt(coord)} [{coord.X},{coord.Y}]{(IsLockedNode(coord) ? "  (locked)" : "")}";

    private bool IsLockedNode(Vector2i coord) => NodeAtCoords(coord) is { IsVisible: false };

    private bool RitualPlannable(Vector2i coord) =>
        !Settings.Features.RitualPlanUnlockedOnly || !IsLockedNode(coord);

    private string NodeNameAt(Vector2i coord)
    {
        lock (mapCacheLock)
            return mapCache.TryGetValue(coord, out var n) && !string.IsNullOrWhiteSpace(n?.Name) ? n.Name : coord.ToString();
    }

    private bool ReadRitualLineState(out int seed, out int depth, out bool selecting)
    {
        seed = 0; depth = 0; selecting = false;
        try
        {
            var panel = AtlasPanel;
            if (panel == null) return false;
            seed = RitualSeedRaw();
            depth = RitualLineCount();

            long page = panel.Address;
            if (page != 0) selecting = GameController.Memory.Read<byte>(page + RitualGateOffset) != 0;

            if (Settings.Features.DebugRitualRolls)
            {
                var state = (seed, depth, selecting);
                if (!state.Equals(ritualLineStateLogged))
                {
                    ritualLineStateLogged = state;
                    int engineSeed = 0, engineDepth = -1;
                    try { engineSeed = panel.RitualForetoldSeed; engineDepth = panel.SelectedForetoldRitualMaps?.Count ?? -1; } catch { }
                    LogMessage($"RitualLineState seed={seed} depth={depth} selecting={selecting} rawCount={RitualLineCountRaw()} engineSeed={engineSeed} engineDepth={engineDepth} maxLine={RitualMaxLine()} valid={RitualValidTargets().Count}");
                }
            }
            return true;
        }
        catch { return false; }
    }

    private (int seed, int depth, bool selecting) ritualLineStateLogged = (int.MinValue, -1, false);

    private static readonly string[] ForetellPrefixes = { "Foretold Proliferation: ", "Foretold Bounty: " };

    private static string ForetellText(RitualModDef d)
    {
        if (d == null) return "-";
        var t = string.IsNullOrWhiteSpace(d.text) ? d.id : d.text;
        foreach (var pre in ForetellPrefixes)
            if (t.StartsWith(pre, StringComparison.Ordinal)) return t.Substring(pre.Length);
        return t;
    }

    private System.Drawing.Color ForetellColor(RitualModDef d)
    {
        if (d == null) return OverlayText;
        float w = Settings.ForetellingWeight(d.id);
        if (MathF.Abs(w) < 0.5f) return OverlayText;
        return ColorUtils.WithAlphaOf(WeightRampColor(w, minForetellWeight, maxForetellWeight), OverlayText);
    }

    private readonly Dictionary<Vector2i, List<Vector2i>> ritualAdjMemo = new();
    private List<Vector2i> ritualPlan = new();
    private readonly Dictionary<Vector2i, int> ritualPlanSteps = new();
    public bool ritualStartSearchPending;
    private List<Vector2i> ritualBestStartPlan = new();
    private (int seed, int wver, int steps, bool dataOk, bool unlocked) ritualBestStartKey;
    private string ritualStartSearchNote;
    private string ritualPlanActionNote;
    private bool ritualUiActive;
    private Vector2 ritualUiPos;
    private Vector2 ritualPanelSize;
    private bool ritualPanelDragging;
    private Vector2 ritualPanelGrab;
    private static readonly System.Drawing.Color RitualPanelBg = System.Drawing.Color.FromArgb(228, 0, 0, 0);
    private bool ritualUiAtStart;
    private List<Vector2i> ritualShownPlan = new();
    private const float RitualHoverSlack = 0.6f;
    public List<Vector2i> RitualPlanCoords => ritualPlan;
    private (int seed, int depth, Vector2i tip, int wver, int steps, bool dataOk, bool unlocked) ritualPlanKey;

    private List<Vector2i> RitualCandidatesMemo(Vector2i tip, List<Vector2i> selected)
    {
        if (!ritualAdjMemo.TryGetValue(tip, out var raw))
        {
            raw = new List<Vector2i>();
            RitualNeighbours(tip, raw);
            ritualAdjMemo[tip] = raw;
        }
        var result = new List<Vector2i>();
        foreach (var c in raw)
        {
            bool taken = false;
            foreach (var sel in selected) if (SameCoord(sel, c)) { taken = true; break; }
            if (!taken) result.Add(c);
        }
        result.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return result;
    }

    private float RitualBestPath(int depth, Vector2i tip, List<Vector2i> selected, int steps, List<Vector2i> into)
        => RitualBestPath(depth, tip, selected, steps, into, false);

    private float RitualBestPath(int depth, Vector2i tip, List<Vector2i> selected, int steps, List<Vector2i> into, bool liveStep)
    {
        into.Clear();
        if (steps <= 0) return 0f;
        var candidates = RitualCandidatesMemo(tip, selected);
        if (candidates.Count == 0) return 0f;

        float best = float.NegativeInfinity;
        var scratch = new List<Vector2i>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (liveStep ? !RitualSelectableNow(candidates[i]) : !RitualSelectable(candidates[i])) continue;
            if (!RitualPlannable(candidates[i])) continue;
            float here = ForetellWeightOf(ForetellRoll(depth, i));
            selected.Add(candidates[i]);
            float rest = RitualBestPath(depth + 1, candidates[i], selected, steps - 1, scratch);
            selected.RemoveAt(selected.Count - 1);
            if (here + rest > best)
            {
                best = here + rest;
                into.Clear();
                into.Add(candidates[i]);
                into.AddRange(scratch);
            }
        }
        return best == float.NegativeInfinity ? 0f : best;
    }

    private float RitualEvaluateStart(Vector2i start, int steps, List<Vector2i> into)
    {
        into.Clear();
        into.Add(start);
        if (steps <= 1) return 0f;
        var tail = new List<Vector2i>();
        float score = RitualBestPath(1, start, new List<Vector2i> { start }, steps - 1, tail);
        into.AddRange(tail);
        return score;
    }

    private int RitualStartSteps()
    {
        int steps = Math.Clamp(Settings.Features.RitualPlanSteps, 1, 8);
        int cap = RitualMaxLine();
        return cap > 0 ? Math.Min(steps, cap) : steps;
    }

    private List<Vector2i> RitualPlanFromStart(int seed, Vector2i start)
    {
        var key = (seed, -1, start, weightsRecalcVersion, RitualStartSteps(), foretellRollOk, Settings.Features.RitualPlanUnlockedOnly);
        if (key.Equals(ritualPlanKey)) return ritualPlan;
        ritualPlanKey = key;
        ritualAdjMemo.Clear();
        var plan = new List<Vector2i>();
        RitualEvaluateStart(start, RitualStartSteps(), plan);
        ritualPlan = plan;
        return plan;
    }

    private void SearchBestRitualStart(int seed, List<Vector2i> starts)
    {
        int steps = RitualStartSteps();
        ritualAdjMemo.Clear();

        var best = new List<Vector2i>();
        float bestScore = float.NegativeInfinity;
        var scratch = new List<Vector2i>();
        int looked = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var start in starts)
        {
            if (!RitualSelectable(start) || !RitualPlannable(start)) continue;
            looked++;
            float score = RitualEvaluateStart(start, steps, scratch);
            if (score > bestScore) { bestScore = score; best = new List<Vector2i>(scratch); }
        }
        sw.Stop();

        ritualBestStartPlan = best;
        ritualBestStartKey = (seed, weightsRecalcVersion, steps, foretellRollOk, Settings.Features.RitualPlanUnlockedOnly);
        ritualStartSearchNote = $"searched {looked} starts in {sw.ElapsedMilliseconds} ms";
        ritualPlanKey = default;
    }

    private void RefreshRitualPlan(int seed, int depth, Vector2i tip, List<Vector2i> selected)
    {
        int remaining = RitualMaxLine() - depth;
        int steps = Math.Clamp(Settings.Features.RitualPlanSteps, 1, 8);
        if (remaining > 0) steps = Math.Min(steps, remaining);
        else steps = 0;
        var key = (seed, depth, tip, weightsRecalcVersion, steps, foretellRollOk, Settings.Features.RitualPlanUnlockedOnly);
        if (key.Equals(ritualPlanKey)) return;
        ritualPlanKey = key;
        ritualAdjMemo.Clear();
        var plan = new List<Vector2i>();
        if (steps > 0)
            RitualBestPath(depth, tip, new List<Vector2i>(selected), steps, plan, true);
        ritualPlan = plan;
        
    }

    private bool RitualNodeRect(Vector2i coord, out RectangleF rect)
    {
        rect = default;
        Node node;
        lock (mapCacheLock)
            if (!mapCache.TryGetValue(coord, out node)) node = null;
        if (node == null) return false;
        rect = GetNodeRect(node);
        return !rect.IsEmpty && rect.Width > 0 && IsOnScreen(rect.Center);
    }

    private readonly HashSet<Vector2i> ritualPaneled = new();

    private void RitualNodePanel(Vector2i coord, RectangleF rect, Vector2 screen, List<(string text, System.Drawing.Color color)> lines)
    {
        if (!ritualPaneled.Add(coord)) return;
        const float pad = 6f;
        var (boxW, boxH, lineH) = MeasurePanel(lines, pad);
        float x = Math.Clamp(rect.Center.X - boxW / 2f, 0f, MathF.Max(0f, screen.X - boxW));
        float y = rect.Top - boxH - 2f;
        if (y < 0f) y = rect.Bottom + 2f;
        RegisterPanel(new Vector2(x, y), boxW, boxH, pad, lineH, lines);
    }

    private class RitualStartRow
    {
        public Vector2i Start;
        public string Name = "";
        public float Weight;
        public float Value;
        public int Maps;
        public int Visible;
        public List<Vector2i> Plan = new();
    }

    private List<RitualStartRow> ritualStartRows = new();
    private string ritualStartRowsNote;
    private string ritualStartFilter = "";

    private void BuildRitualStartRows()
    {
        var rows = new List<RitualStartRow>();
        ritualStartRowsNote = null;
        try
        {
            if (!ReadRitualLineState(out _, out int depth, out bool selecting) || !selecting)
            { ritualStartRowsNote = "Open Invoke Rite on the atlas."; ritualStartRows = rows; return; }
            if (depth > 0)
            { ritualStartRowsNote = "The Rite has already started."; ritualStartRows = rows; return; }

            var starts = RitualValidTargets();
            int steps = RitualStartSteps();
            ritualAdjMemo.Clear();
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var scratch = new List<Vector2i>();
            foreach (var start in starts)
            {
                if (!RitualSelectable(start) || !RitualPlannable(start)) continue;
                float weight = RitualEvaluateStart(start, steps, scratch);
                var plan = new List<Vector2i>(scratch);

                float value = 0f;
                var walk = new List<Vector2i>();
                Vector2i? at = null;
                for (int step = 0; step < plan.Count; step++)
                {
                    if (at.HasValue)
                    {
                        var cand = RitualCandidatesMemo(at.Value, walk);
                        int idx = cand.FindIndex(v => SameCoord(v, plan[step]));
                        if (idx >= 0) value += ForetellValueOf(ForetellRoll(step, idx));
                    }
                    walk.Add(plan[step]);
                    at = plan[step];
                }

                rows.Add(new RitualStartRow {
                    Start = start, Name = NodeNameAt(start),
                    Weight = weight, Value = value, Maps = plan.Count,
                    Visible = plan.Count(c => !IsLockedNode(c)), Plan = plan,
                });
            }
            sw.Stop();
            rows.Sort((a, b) => b.Weight.CompareTo(a.Weight));
            ritualStartRowsNote = $"{rows.Count} starts scored in {sw.ElapsedMilliseconds} ms";
        }
        catch (Exception e) { ritualStartRowsNote = e.Message; LogError($"BuildRitualStartRows failed: {e.Message}"); }
        ritualStartRows = rows;
    }

    private void DrawRitualBody()
    {
        if (ImGui.Button("Score all starts")) BuildRitualStartRows();
        Controls.Tip("Plans from every valid starting map.");
        if (!string.IsNullOrEmpty(ritualStartRowsNote))
        {
            ImGui.SameLine();
            ImGui.TextDisabled(ritualStartRowsNote);
        }

        if (ritualStartRows.Count == 0) return;

        var cols = new List<TableColumn<RitualStartRow>>
        {
            new() {
                Header = "WP", Width = 34f,
                Draw = (r, _) => {
                    var n = NodeAtCoords(r.Start);
                    bool wp = n != null && HasWaypoint(n);
                    if (ImGui.Checkbox("##wp", ref wp) && n != null) {
                        if (wp) AddWaypoint(n); else RemoveWaypoint(n);
                    }
                    Controls.Tip(wp ? "Remove waypoint" : "Add waypoint");
                },
            },
            new() {
                Header = "Map", Width = 0f, SortKey = r => r.Name,
                Draw = (r, _) => { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted($"{r.Name} [{r.Start.X},{r.Start.Y}]"); },
            },
            new() {
                Header = "Maps", Width = 50f, SortKey = r => r.Visible,
                Draw = (r, _) => {
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextUnformatted($"{r.Visible}/{r.Maps}");
                    Controls.Tip(r.Visible >= r.Maps ? "All maps visible" : $"{r.Maps - r.Visible} fogged, unfog first");
                },
            },
            new() {
                Header = "Weight", Width = 70f, SortKey = r => r.Weight,
                Draw = (r, _) => { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted($"{r.Weight:0.#}"); },
            },
            new() {
                Header = "Value", Width = 80f, SortKey = r => r.Value,
                Draw = (r, _) => { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted($"{r.Value:0.##} {foretellPriceUnit}"); },
            },
            new() {
                Header = "Tour", Width = 46f,
                Draw = (r, _) => {
                    if (ImGui.SmallButton("Tour")) { ritualShownPlan = r.Plan; RitualPlanToTour(); }
                    Controls.Tip("Build a tour of this whole route.");
                },
            },
        };

        if (HacksCameraPanReady)
            cols.Add(new TableColumn<RitualStartRow> {
                Header = "Goto", Width = 50f,
                Draw = (r, _) => {
                    if (ImGui.SmallButton("Goto")) GotoNode(NodeAtCoords(r.Start));
                    Controls.Tip("Pan the atlas to this map.");
                },
            });

        SortableTable.Draw("rite_starts", ritualStartRows, cols.ToArray(), ref ritualStartFilter,
            r => r.Name, Math.Max(120f, ImGui.GetContentRegionAvail().Y));
    }

    private Vector2 RitualPanelPos()
    {
        var p = Settings.Features.RitualPanelPos;
        if (p == Vector2.Zero) p = new Vector2(20f, 180f);
        return p;
    }

    private void HandleRitualPanelDrag()
    {
        var io = ImGui.GetIO();
        var pos = RitualPanelPos();
        if (!ritualPanelDragging)
        {
            if (io.WantCaptureMouse) return;
            if (!io.KeyShift || !ImGui.IsMouseClicked(ImGuiMouseButton.Left)) return;
            var rect = new RectangleF(pos.X, pos.Y, ritualPanelSize.X, ritualPanelSize.Y);
            if (!rect.Contains(io.MousePos.X, io.MousePos.Y)) return;
            ritualPanelDragging = true;
            ritualPanelGrab = io.MousePos - pos;
        }
        if (!ImGui.IsMouseDown(ImGuiMouseButton.Left)) { ritualPanelDragging = false; return; }

        var next = io.MousePos - ritualPanelGrab;
        var vp = ImGui.GetMainViewport();
        next.X = Math.Clamp(next.X, vp.Pos.X, vp.Pos.X + Math.Max(0f, vp.Size.X - ritualPanelSize.X));
        next.Y = Math.Clamp(next.Y, vp.Pos.Y, vp.Pos.Y + Math.Max(0f, vp.Size.Y - ritualPanelSize.Y));
        Settings.Features.RitualPanelPos = next;
    }

    public void DrawRitualOverlayButtons()
    {
        if (!ritualUiActive) { ritualPanelDragging = false; return; }
        try
        {
            HandleRitualPanelDrag();

            ImGui.SetNextWindowPos(ritualUiPos, ImGuiCond.Always);
            var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                      | ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoScrollbar
                      | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing;

            if (ImGui.Begin("##rite_actions", flags))
            {
                ImGui.BeginDisabled(!ritualUiAtStart);
                if (ImGui.Button("Find best start")) ritualStartSearchPending = true;
                ImGui.EndDisabled();

                bool hasPlan = ritualShownPlan is { Count: > 0 };
                ImGui.SameLine();
                ImGui.BeginDisabled(!hasPlan);
                if (HacksCameraPanReady)
                {
                    if (ImGui.Button("Go to first map")) GotoNode(NodeAtCoords(ritualShownPlan[0]));
                    ImGui.SameLine();
                }
                if (ImGui.Button("Waypoint to first map")) ritualPlanActionNote = RitualPlanToWaypoint();
                ImGui.SameLine();
                if (ImGui.Button("Tour whole Rite")) ritualPlanActionNote = RitualPlanToTour();
                ImGui.EndDisabled();

                if (!string.IsNullOrEmpty(ritualPlanActionNote))
                    ImGui.TextDisabled(ritualPlanActionNote);
            }
            ImGui.End();
        }
        catch (Exception e) { LogError($"DrawRitualOverlayButtons failed: {e.Message}"); }
    }

    public string RitualPlanToWaypoint()
    {
        var plan = ritualShownPlan;
        if (plan == null || plan.Count == 0) return "no plan";
        var node = NodeAtCoords(plan[0]);
        if (node == null) return "first map not cached";
        AddWaypoint(node);
        OpenPanelSection("waypoints");
        return $"waypoint on {PlanLabel(plan[0])}";
    }

    public string RitualPlanToTour()
    {
        var plan = ritualShownPlan;
        if (plan == null || plan.Count == 0) return "no plan";

        var tour = new Tour
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = $"Rite {NodeNameAt(plan[0])} [{plan[0].X},{plan[0].Y}]",
            Color = GetNextTourColor(),
            Stops = plan.Select(c => new TourStop { X = c.X, Y = c.Y }).ToList(),
        };
        Settings.Tours.Tours.Add(tour.Id, tour);
        Settings.Tours.ActiveTourId = tour.Id;
        BuildTour(tour);
        OpenPanelSection("tours");
        return $"tour of {plan.Count} maps";
    }

    private void LayoutRitualForetellings()
    {
        long tRitual = Stopwatch.GetTimestamp();
        try { LayoutRitualForetellingsInner(); }
        finally { PerfMonitor.Record("Render.RitualForetell", Stopwatch.GetTimestamp() - tRitual); }
    }

    private void LayoutRitualForetellingsInner()
    {
        ritualPlanSteps.Clear();
        ritualPaneled.Clear();
        ritualUiActive = false;
        if (!Settings.Features.ShowRitualForetellings) return;
        try
        {
            if (!ReadRitualLineState(out int seed, out int depth, out bool selecting)) return;
            if (ritualMods == null || ritualMods.Count == 0) return;

            if (!selecting) return;

            var selected = RitualSelected();
            bool atStart = selected.Count == 0;
            var tip = atStart ? default : selected[selected.Count - 1];

            var options = atStart ? RitualValidTargets() : RitualCandidates(tip, selected);
            if (options.Count == 0) return;

            var tint = ButtonKindColor(RitualKind);
            var sub = System.Drawing.Color.FromArgb(180, 180, 180, 180);
            var mouse = ImGuiNET.ImGui.GetMousePos();
            var screen = GameController.Window.GetWindowRectangleTimeCache.Size;

            Vector2i? hovered = null;
            float bestDist = float.MaxValue;
            foreach (var c in options)
            {
                if (!RitualNodeRect(c, out var hr)) continue;
                float pad2 = MathF.Max(hr.Width, hr.Height) * RitualHoverSlack;
                var grown = new RectangleF(hr.X - pad2, hr.Y - pad2, hr.Width + pad2 * 2f, hr.Height + pad2 * 2f);
                if (!grown.Contains(mouse)) continue;
                float dist = Vector2.Distance(hr.Center, mouse);
                if (dist < bestDist) { bestDist = dist; hovered = c; }
            }

            var pendingHover = new List<(Vector2i coord, RectangleF rect, List<(string, System.Drawing.Color)> lines)>();
            if (hovered.HasValue)
            {
                var next = new List<Vector2i>(selected) { hovered.Value };
                var onward = RitualCandidates(hovered.Value, next);
                for (int j = 0; j < onward.Count; j++)
                {
                    if (!RitualSelectable(onward[j])) continue;
                    if (!RitualNodeRect(onward[j], out var orect)) continue;
                    var lines = new List<(string, System.Drawing.Color)>();
                    foreach (var p2 in ForetellRoll(depth + 1, j))
                        lines.Add((ForetellText(p2), ForetellColor(p2)));
                    if (lines.Count == 0) continue;
                    pendingHover.Add((onward[j], orect, lines));
                }
            }

            void FlushHover()
            {
                foreach (var (c, r, l) in pendingHover) RitualNodePanel(c, r, screen, l);
            }

            if (!Settings.Features.RitualPlanner) { FlushHover(); return; }
            if (foretellPricesStale) { RefreshForetellPrices(out foretellPriceNote); foretellPricesStale = false; }

            List<Vector2i> plan;
            if (atStart)
            {
                if (ritualStartSearchPending)
                {
                    SearchBestRitualStart(seed, options);
                    ritualStartSearchPending = false;
                }

                if (hovered.HasValue) plan = RitualPlanFromStart(seed, hovered.Value);
                else if (ritualBestStartKey.Equals((seed, weightsRecalcVersion, RitualStartSteps(), foretellRollOk, Settings.Features.RitualPlanUnlockedOnly)))
                    plan = ritualBestStartPlan;
                else plan = null;

                if (plan == null || plan.Count == 0)
                {
                    var hint = new List<(string, System.Drawing.Color)>
                    {
                        ("Rite not started", tint),
                        ("hover any glowing map to plan from it, or use Find best start", sub),
                    };
                    var (hw2, hh2, hl2) = MeasurePanel(hint, 6f);
                    var hpos = RitualPanelPos();
                    RegisterPanel(hpos, hw2, hh2, 6f, hl2, hint, RitualPanelBg);
                    ritualPanelSize = new Vector2(hw2, hh2);
                    ritualUiActive = true; ritualUiAtStart = true;
                    ritualUiPos = new Vector2(hpos.X, hpos.Y + hh2 + 4f);
                    FlushHover();
                    return;
                }
            }
            else
            {
                RefreshRitualPlan(seed, depth, tip, selected);
                plan = ritualPlan;
            }

            if (plan == null || plan.Count == 0) return;
            ritualShownPlan = plan;
            for (int i = 0; i < plan.Count; i++) ritualPlanSteps[plan[i]] = i;

            int cap = RitualMaxLine();
            string head = atStart
                ? (hovered.HasValue ? $"Rite plan from {PlanLabel(plan[0])}" : $"Best start: {PlanLabel(plan[0])}   {ritualStartSearchNote}")
                : $"Rite plan   {plan.Count} of {Math.Max(0, cap - depth)} left";
            var route = new List<(string text, System.Drawing.Color color)> { ($"{head}   (line {depth}/{cap})", tint) };

            float total = 0f, totalWeight = 0f;
            var taken = System.Drawing.Color.FromArgb(120, 170, 170, 170);
            var walk = new List<Vector2i>();
            Vector2i? at = null;
            for (int k = 0; k < selected.Count; k++)
            {
                var chosen = new List<RitualModDef>();
                if (at.HasValue)
                {
                    var cand = RitualCandidatesMemo(at.Value, walk);
                    int idx = cand.FindIndex(v => SameCoord(v, selected[k]));
                    if (idx >= 0) chosen = ForetellRoll(k, idx);
                }
                total += ForetellValueOf(chosen);
                totalWeight += ForetellWeightOf(chosen);
                route.Add(($"{k + 1}. {PlanLabel(selected[k])} (taken)", taken));
                foreach (var pk in chosen)
                    route.Add(("     " + ForetellText(pk), ColorUtils.WithAlphaOf(ForetellColor(pk), taken)));
                walk.Add(selected[k]);
                at = selected[k];
            }
            for (int step = 0; step < plan.Count; step++)
            {
                var picks = new List<RitualModDef>();
                if (at.HasValue)
                {
                    var cand = RitualCandidatesMemo(at.Value, walk);
                    int idx = cand.FindIndex(v => SameCoord(v, plan[step]));
                    if (Settings.Features.DebugRitualRolls)
                        LogMessage($"RitePlan step={step} d={depth + step} tip=({at.Value.X},{at.Value.Y}) pick=({plan[step].X},{plan[step].Y}) idx={idx} cand=[{string.Join(" ", cand.Select(c => $"({c.X},{c.Y})"))}]");
                    if (idx >= 0) picks = ForetellRoll(depth + step, idx);
                }
                total += ForetellValueOf(picks);
                totalWeight += ForetellWeightOf(picks);

                float stepWeight = ForetellWeightOf(picks);
                route.Add(picks.Count == 0
                    ? ($"{depth + step + 1}. {PlanLabel(plan[step])}", sub)
                    : ($"{depth + step + 1}. {PlanLabel(plan[step])} ({stepWeight:0.#})", sub));
                foreach (var pk in picks) route.Add(("     " + ForetellText(pk), ForetellColor(pk)));
                if (picks.Count == 0) route.Add(("     -", sub));

                if (step > 0 && RitualNodeRect(plan[step], out var prect))
                {
                    var stamp = new List<(string, System.Drawing.Color)>();
                    for (int k = 0; k < picks.Count; k++)
                        stamp.Add((k == 0 ? $"{depth + step + 1}. " + ForetellText(picks[k]) : "    " + ForetellText(picks[k]), ForetellColor(picks[k])));
                    if (stamp.Count > 0) RitualNodePanel(plan[step], prect, screen, stamp);
                }

                walk.Add(plan[step]);
                at = plan[step];
            }
            if (!foretellRollOk && foretellRollFailed)
                route.Add(("ritual mod data not loaded - open the atlas once", sub));
            route.Add(($"total value {total:0.##} {foretellPriceUnit}   weight {totalWeight:0.#}", tint));

            FlushHover();

            const float pad = 6f;
            var (rw, rh, rl) = MeasurePanel(route, pad);
            var rpos = RitualPanelPos();
            RegisterPanel(rpos, rw, rh, pad, rl, route, RitualPanelBg);
            ritualPanelSize = new Vector2(rw, rh);
            ritualUiActive = true; ritualUiAtStart = atStart;
            ritualUiPos = new Vector2(rpos.X, rpos.Y + rh + 4f);
        }
        catch (Exception e) { LogError($"LayoutRitualForetellings failed: {e.Message}"); }
    }

    #endregion

    #region Rumour Popup Decode

    private void ScanExpeditionButtons()
    {
        frameLogbookPopup = null;
        frameVisibleExpeditionButtonCoords.Clear();
        frameHoverButtonRegion = null;
        try
        {
            var buttons = AtlasPanel?.Buttons;
            ScanAtlasButtonDebug(buttons);
            if (buttons == null) return;
            var mouse = ImGuiNET.ImGui.GetMousePos();
            foreach (var b in buttons)
            {
                if (b == null || !b.IsVisible) continue;
                var kind = b.ButtonType?.Id;
                if (kind != ExpeditionKind && kind != RitualKind) continue;
                frameVisibleExpeditionButtonCoords.Add(b.Coordinate);

                var btnRect = b.GetClientRect();
                if (btnRect.Width <= 0 || !btnRect.Contains(mouse)) continue;

                frameHoverButtonRegion = (b.RegionCoordinate, kind);
                if (kind != ExpeditionKind || frameLogbookPopup != null) continue;

                var children = b.Children;
                var visual = children != null && children.Count > 0 ? children[0] : null;
                var tip = visual?.Tooltip;
                if (tip == null) continue;
                var r = tip.GetClientRect();
                if (r.Width <= 0 || r.Height <= 0) continue;
                frameLogbookPopup = tip;
            }
        }
        catch (Exception e) { LogError($"ScanExpeditionButtons failed: {e.Message}"); }
    }

    private void ReadPopupRumors(ExileCore2.PoEMemory.Element popup, List<string> into, int depth = 0)
    {
        if (popup == null || depth > 6 || into.Count > 40) return;
        var t = popup.Text;
        if (!string.IsNullOrWhiteSpace(t)) into.Add(t);
        foreach (var ch in popup.Children)
            ReadPopupRumors(ch, into, depth + 1);
    }

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

            var rows = new List<(string content, string hint, string desc, float w)>();
            var seen = new HashSet<string>();
            foreach (var t in texts)
            {
                if (!seen.Add(t)) continue;
                if (Settings.GameData.Rumors.TryGetValue(t, out var info))
                    rows.Add((info.Content, info.Text, info.Description, Settings.RumorWeight(t)));
            }
            if (rows.Count == 0) return;
            rows.Sort((a, b) => b.w.CompareTo(a.w));

            System.Drawing.Color fontColor = OverlayText;

            var lines = new List<(string text, System.Drawing.Color color)>();
            lines.Add(("Rumours", fontColor));
            foreach (var (content, hint, desc, w) in rows)
            {
                lines.Add((RumorRow(content, hint), RumorTextColor(w, fontColor)));
                if (!string.IsNullOrEmpty(desc))
                {
                    var sub = System.Drawing.Color.FromArgb(180, 180, 180, 180);
                    lines.Add(("    " + desc, sub));
                }
            }
            const float pad = 8f;
            var (boxW, boxH, lineH) = MeasurePanel(lines, pad);

            float x = prect.Right + 8f;
            var screen = GameController.Window.GetWindowRectangleTimeCache.Size;
            if (x + boxW > screen.X) x = prect.Left - 8f - boxW;
            float y = prect.Top;
            y = MathF.Min(y, screen.Y - boxH);

            RegisterPanel(new Vector2(x, y), boxW, boxH, pad, lineH, lines);
        }
        catch (Exception e) { LogError($"LayoutPopupOverlay failed: {e.Message}"); }
    }

    #endregion

    #region Expeditions Panel

    private void DrawExpeditionsBody()
    {
        try
        {
            {

                string filter = expSearchText;
                ImGui.SetNextItemWidth(260);
                if (ImGui.InputTextWithHint("##expsearch", "Search rumors/content...", ref filter, 100))
                    expSearchText = filter;
                ImGui.SameLine();
                if (ImGui.Button("Clear##expsearchclear")) expSearchText = "";
                ImGui.Separator();
                ImGui.TextDisabled("Possible rumours for the region. Actual roll shows on hover on the atlas.");

                List<Classes.Expedition> snapshot;
                snapshot = expeditions;

                if (snapshot.Count == 0)
                    ImGui.TextDisabled("No expeditions loaded. Open the atlas.");

                foreach (var e in snapshot.OrderByDescending(ExpeditionScore))
                {
                    if (e.Kind != ExpeditionKind) continue;
                    if (!ExpeditionMatchesSearch(e, expSearchText)) continue;

                    ImGui.PushID($"exp_{e.RegionCoord.X}_{e.RegionCoord.Y}");

                    bool hl = IsExpeditionHighlighted(e);
                    if (ImGui.Checkbox("##hl", ref hl)) SetExpeditionHighlight(e, hl);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Highlight this expedition's maps on the atlas.");
                    ImGui.SameLine();

                    if (ImGui.SmallButton("WP##expwp")) WaypointNearestInExpedition(e);
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Waypoint the nearest map in this expedition (fewest steps from your explored frontier).");
                    ImGui.SameLine();

                    if (HacksCameraPanReady) {
                        if (ImGui.SmallButton("Goto##expgoto")) GotoNode(NearestNodeInExpedition(e));
                        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Pan the atlas to the nearest map in this expedition.");
                        ImGui.SameLine();
                    }

                    if (ImGui.CollapsingHeader(ExpeditionLabel(e)))
                    {
                        if (ImGui.BeginTable($"rumor_rows_{e.RegionCoord.X}_{e.RegionCoord.Y}", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                        {
                            ImGui.TableSetupColumn("Possible Rumor", ImGuiTableColumnFlags.WidthFixed, 200);
                            ImGui.TableSetupColumn("x", ImGuiTableColumnFlags.WidthFixed, 30);
                            ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 160);
                            ImGui.TableSetupColumn("Description", ImGuiTableColumnFlags.WidthStretch);
                            ImGui.TableHeadersRow();

                            foreach (var (text, count) in e.Rumors.OrderByDescending(kv => Settings.RumorWeight(kv.Key)))
                            {
                                string content, desc;
                                if (Settings.GameData.Rumors.TryGetValue(text, out var info))
                                { content = info.Content; desc = info.Description; }
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

            }
        }
        catch (Exception ex)
        {
            LogError("Error drawing expeditions panel: " + ex.Message);
        }
    }

    #endregion

}
