
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using ExileCore2;
using GameOffsets2.Native;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    #region Draw Passes

    private RectangleF GetNodeRect(Node node)
    {
        if (node?.MapNode?.Element == null)
            return default;
        if (frameRectCache.TryGetValue(node.Coordinates, out var cached))
            return cached;
        try {
            var rect = WorldAlignedRect(node, node.MapNode.Element.GetClientRect());
            frameRectCache[node.Coordinates] = rect;
            return rect;
        } catch (Exception e) {
            DebugSwallow("GetNodeRect: rect read", e);
            return default;
        }
    }

    private const int TextSizeCacheMax = 4096;

    private Vector2 MeasureCached(string text, float scale)
    {
        var key = (text, scale);
        if (textSizeCache.TryGetValue(key, out var size))
            return size;
        using (Graphics.SetTextScale(scale))
            size = Graphics.MeasureText(text);
        if (textSizeCache.Count >= TextSizeCacheMax)
            textSizeCache.Clear();
        textSizeCache[key] = size;
        return size;
    }

    private Vector2 NodeCenter(Node node)
    {
        if (frameWorldToScreen != null && node != null && node.HasWorldPos)
            return frameWorldToScreen(node.WorldPos);
        return GetNodeRect(node).Center;
    }

    private bool DrawnFromOtherEnd(Vector2i src, Vector2i dst)
    {
        if (!drawnCoords.Contains(dst))
            return false;
        return dst.X < src.X || (dst.X == src.X && dst.Y < src.Y);
    }

    private void DrawNodeLines(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawConnections(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node lines: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeLabels(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            using (Graphics.SetTextScale(1.0f)) {
                DrawMapName(cachedNode, nodeCurrentPosition);
                DrawWeight(cachedNode, nodeCurrentPosition);
            }
        } catch (Exception e) {
            LogError("Error drawing node labels: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawHoveredNodeOverTooltip()
    {
        if (Settings.Features.DebugMode || !mapTooltipVisible)
            return;

        try {
            var node = GetClosestNodeToCursor();
            if (node == null)
                return;

            var rect = WorldAlignedRect(node, node.MapNode.Element.GetClientRect());
            if (Settings.Graphics.DrawNodes.HasFlag(StateOf(node)))
                DrawMapNode(node, rect);
            DrawContentRow(node, rect);
            DrawOverrideIcon(node, rect);
            DrawSpecialIndicator(node, rect);
            using (Graphics.SetTextScale(1.0f))
                DrawMapName(node, rect);
            DrawSpecialMapName(node, rect);
        } catch (Exception e) {
            DebugSwallow("Render: hovered node over tooltip", e);
        }
    }

    private void DrawDebugging(Node cachedNode) {
        var rect = WorldAlignedRect(cachedNode, cachedNode.MapNode.Element.GetClientRect());
        string debugText = cachedNode.DebugText() + $"Size: {rect.Width:0} x {rect.Height:0}\n";
        using (Graphics.SetTextScale(1.0f))
            DrawCenteredTextWithBackground(debugText, rect.Center, OverlayText, OverlayBg, true, 10, 4);

    }

    #endregion

    #region Connection Lines

    private Color LineColor(Node n) =>
        n.IsDone ? Settings.Graphics.VisitedLineColor
        : n.IsUnlocked ? Settings.Graphics.UnlockedLineColor
        : Settings.Graphics.LockedLineColor;

    private Color FlatLineColor(Node a, Node b)
    {
        if (a.IsDone && b.IsDone) return Settings.Graphics.VisitedLineColor;
        if (a.IsUnlocked || b.IsUnlocked) return Settings.Graphics.UnlockedLineColor;
        return Settings.Graphics.LockedLineColor;
    }

    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        var g = Settings.Graphics;
        if (!g.ShowConnectionLines)
            return;

        bool srcEnabled = g.DrawLines.HasFlag(StateOf(cachedNode));

        foreach (Vector2i coordinates in cachedNode.NeighborCoordinates)
        {
            if (coordinates == default)
                continue;
            if (!mapCache.TryGetValue(coordinates, out Node destinationNode))
                continue;
            if (!srcEnabled && !g.DrawLines.HasFlag(StateOf(destinationNode)))
                continue;
            if (DrawnFromOtherEnd(cachedNode.Coordinates, coordinates))
                continue;

            Vector2 a = nodeCurrentPosition.Center;
            Vector2 b = NodeCenter(destinationNode);
            if (!ClipLineToScreen(a, b, out _, out _))
                continue;

            Color from, to;
            if (g.DrawGradientLines) { from = LineColor(cachedNode); to = LineColor(destinationNode); }
            else from = to = FlatLineColor(cachedNode, destinationNode);

            int n = 2;
            polyScratch[0] = a;
            polyScratch[1] = b;
            if (g.UseGameConnectionCurves) {
                var curve = GetCurveScreenPoints(cachedNode.Coordinates, destinationNode.Coordinates, out bool reversed);
                if (curve != null && curve.Length >= 2 && curve.Length <= polyScratch.Length) {
                    n = curve.Length;
                    for (int i = 0; i < n; i++)
                        polyScratch[i] = curve[reversed ? n - 1 - i : i];
                }
            }

            DrawPolylineRuns(polyScratch, n, from, to, g.MapLineWidth);
        }
    }

    private void DrawPolylineRuns(Vector2[] pts, int n, Color from, Color to, float width)
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++) {
            if (pts[i].X < minX) minX = pts[i].X;
            if (pts[i].X > maxX) maxX = pts[i].X;
            if (pts[i].Y < minY) minY = pts[i].Y;
            if (pts[i].Y > maxY) maxY = pts[i].Y;
        }
        CollectExcludes(minX, minY, maxX, maxY, excludeScratch);

        bool flat = from == to;
        if (n == 2 && !flat) {
            if (ClipLineToScreen(pts[0], pts[1], out var a, out var b) && !CrossesExcluded(a, b, excludeScratch))
                Graphics.DrawLine(a, b, width, from, to);
            return;
        }

        var dl = ImGuiNET.ImGui.GetBackgroundDrawList();
        int chunks = flat ? 1 : 3;
        int runStart = -1;
        int chunk = 0;

        void Flush(int s, int e)
        {
            if (s < 0 || e - s < 1) return;
            var col = flat ? from : ColorUtils.InterpolateColor(from, to, (chunk + 0.5f) / chunks);
            Vector2 p0 = pts[s], pe = pts[e];
            ClipLineToScreen(p0, pts[s + 1], out pts[s], out _);
            ClipLineToScreen(pts[e - 1], pe, out _, out pts[e]);
            dl.AddPolyline(ref pts[s], e - s + 1, ExileImGui2.EColor.U32(col), ImGuiNET.ImDrawFlags.None, width);
            pts[s] = p0; pts[e] = pe;
        }

        for (int i = 0; i < n - 1; i++) {
            int c = chunks == 1 ? 0 : i * chunks / (n - 1);
            bool ok = SegmentVisible(pts[i], pts[i + 1]);
            if (!ok || c != chunk) {
                Flush(runStart, i);
                runStart = ok ? i : -1;
                chunk = c;
            } else if (runStart < 0) {
                runStart = i;
            }
        }
        Flush(runStart, n - 1);
    }

    private bool SegmentVisible(Vector2 a, Vector2 b)
    {
        if (!ClipLineToScreen(a, b, out var ca, out var cb))
            return false;
        return !CrossesExcluded(ca, cb, excludeScratch);
    }

    #endregion

    #region Node Fill + Sprites

    private MapTuning MapTune(Node n) => Settings.ReadMap(n?.MapType?.ShortestId);

    public Color WeightRampColor(float weight) => WeightRampColor(weight, minMapWeight, maxMapWeight);

    public Color WeightRampColor(float weight, float min, float max)
    {
        var m = Settings.Maps;
        float denom = max - min;
        float t = denom > 0.0001f ? Math.Clamp((weight - min) / denom, 0f, 1f) : 0.5f;
        return t < 0.5f
            ? ColorUtils.InterpolateColor(m.BadNodeColor, m.NeutralNodeColor, t * 2f)
            : ColorUtils.InterpolateColor(m.NeutralNodeColor, m.GoodNodeColor, (t - 0.5f) * 2f);
    }

    private Color NodeFillColor(Node node)
    {
        var m = Settings.Maps;
        switch (m.NodeColorMode) {
            case NodeColorMode.Static:
                return m.StaticNodeColor;
            case NodeColorMode.Status:
                return StateOf(node) switch {
                    NodeStates.Visited => m.VisitedNodeColor,
                    NodeStates.Unlocked => m.UnlockedNodeColor,
                    NodeStates.Locked => m.LockedNodeColor,
                    _ => m.HiddenNodeColor,
                };
            default:
                return WeightRampColor(node.Weight);
        }
    }

    private LabelStyleOverride MapOverride(Node n)
    {
        var id = n?.MapType?.ShortestId;
        return id != null && Settings.Labels.Maps.TryGetValue(id, out var ov) && ov.Enabled ? ov : null;
    }

    private void DrawMapNode(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (cachedNode.IsDone || !MapTune(cachedNode).Highlight)
            return;

        if (Settings.Graphics.ShowSpecialMapIndicator && cachedNode.IsSpecial)
            return;

        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        var weight = cachedNode.Weight;
        Color color = NodeFillColor(cachedNode);

        var tintOv = ResolveMapIconTint(cachedNode);
        if (tintOv != null)
            color = tintOv.MapIconTint;

        if (Settings.Graphics.UseNodeIcons) {
            DrawNodeSprite(nodeCurrentPosition.Center, radius * 2f, radius * 2f,
                ResolveMapIcon(cachedNode)?.MapIcon ?? SpriteIcon.Circle, color);
        } else {
            Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
        }
    }

    private static RectangleF GetSpriteUV(SpriteIcon icon)
    {
        var (uv0, uv1) = SpriteAtlas.GetUVPair(icon);
        return new RectangleF(uv0.X, uv0.Y, uv1.X - uv0.X, uv1.Y - uv0.Y);
    }

    private void DrawNodeSprite(Vector2 center, float width, float height, SpriteIcon icon, Color color, bool allowFlatten = true)
    {
        float h = allowFlatten ? height * (1f - 0.180f) : height;
        Graphics.DrawImage(CustomIconsName, new RectangleF(center.X - width / 2f, center.Y - h / 2f, width, h), GetSpriteUV(icon), color);
    }

    #endregion

    #region Indicators

    private const float MapNameOffsetX = 0f;

    private void DrawSpecialIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowSpecialMapIndicator || !cachedNode.IsSpecial)
                return;

            if (SpecialHiddenWhenCompleted(cachedNode))
                return;
            bool fade = SpecialFadesWhenCompleted(cachedNode);

            var mapOv = MapOverride(cachedNode);
            SpriteIcon icon = mapOv != null && mapOv.MapIcon != SpriteIcon.Circle
                ? mapOv.MapIcon : Settings.Labels.Special.Icon;
            Color baseCol = mapOv != null && mapOv.OverrideMapIconTint
                ? mapOv.MapIconTint : Settings.Labels.Special.IconTint;
            Color color = fade ? ScaleAlpha(Desaturate(baseCol, 0.85f), 0.5f) : baseCol;
            float scale = fade ? 0.7f : 1.0f;

            Vector2 iconSize = new Vector2(Settings.Labels.Special.IconSize, Settings.Labels.Special.IconSize) * scale;

            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(iconSize.X / 2, SpecialMarkerLift + iconSize.Y / 2);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, icon, color, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing special map indicator: " + e.Message);
        }
    }

    private static Color Desaturate(Color c, float amount)
    {
        int v = System.Math.Max(c.R, System.Math.Max(c.G, c.B));
        int r = (int)(c.R + (v - c.R) * amount);
        int g = (int)(c.G + (v - c.G) * amount);
        int b = (int)(c.B + (v - c.B) * amount);
        return Color.FromArgb(c.A, r, g, b);
    }

    private static Color ScaleAlpha(Color c, float factor)
        => Color.FromArgb((int)(c.A * factor), c.R, c.G, c.B);

    private static readonly System.Collections.Generic.HashSet<string> FadeWhenCompletedSpecials =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Precursor Tower",
        "The Matriarch Halls",
        "The Patriarch Halls",
    };

    private static bool SpecialFadesWhenCompleted(Node node)
        => node.IsCompleted && FadeWhenCompletedSpecials.Contains(node.Name);

    private bool SpecialHiddenWhenCompleted(Node node)
        => Settings.Graphics.HideCompletedSpecialMaps && SpecialFadesWhenCompleted(node);

    private float IndicatorBaseTop(Node node, RectangleF rect)
    {
        float top = rect.Center.Y - rect.Height / 2f;

        if (contentRowTopByCoord.TryGetValue(node.Coordinates, out var rowTop)) {
            top = Math.Min(top, rowTop);
        } else if (node.IsVisible && NodeHasGameContentIcon(node)) {
            try {
                var host = node.MapNode?.Element?.GetChildAtIndex(0)?.GetChildAtIndex(0);
                if (host != null) {
                    var r = host.GetClientRect();
                    if (r.Width > 0 && r.Height > 0)
                        top = Math.Min(top, r.Top);
                }
            } catch (Exception e) { DebugSwallow("IndicatorBaseTop: host rect", e); }
        }

        return top;
    }

    private static bool NodeHasGameContentIcon(Node node)
    {
        foreach (var c in node.Content.Values)
            if (!string.IsNullOrEmpty(c.AtlasIcon))
                return true;
        return false;
    }

    private static readonly Color AtlasQuestColor = Color.FromArgb(255, 255, 200, 40);

    private void DrawAtlasQuestIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasQuestIndicator || !cachedNode.HasAtlasQuest || cachedNode.IsDone)
                return;

            float size = 18.918f;
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X + size, IndicatorBaseTop(cachedNode, nodeCurrentPosition) - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Exclamation, AtlasQuestColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas quest indicator: " + e.Message);
        }
    }

    private void DrawFavoriteIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            var fav = Settings.Labels.Favorite;
            if (!cachedNode.IsFavorited || cachedNode.IsDone || !fav.IconEnabled)
                return;

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);
            float favSize = fav.IconSize * zoom;

            RectangleF iconRect;
            if (biomeIconRectByCoord.TryGetValue(cachedNode.Coordinates, out var biomeRect)) {
                float gap = biomeRect.Height * 0.12f;
                iconRect = new RectangleF(biomeRect.X - gap - favSize, biomeRect.Center.Y - favSize / 2f, favSize, favSize);
            } else {
                iconRect = BiomeSlotRect(cachedNode, nodeCurrentPosition, favSize, zoom);
            }
            DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, fav.Icon, fav.IconTint, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing favorite indicator: " + e.Message);
        }
    }

    private bool ShowsWeightValue(Node node) =>
        Settings.Graphics.DrawWeightOnMap && !node.IsDone && MapTune(node).Highlight
        && Math.Abs(node.Weight) >= 0.5f;

    private void DrawWeight(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!ShowsWeightValue(cachedNode) || !Settings.Graphics.DrawNames.HasFlag(StateOf(cachedNode)))
            return;

        var ramp = WeightRampColor(cachedNode.Weight);
        Color wc = ColorUtils.WithAlphaOf(ramp, OverlayText);

        float zoom = LabelZoom(cachedNode);
        using var weightScale = Graphics.SetTextScale(zoom);
        float offsetX = Settings.Graphics.DrawNames.HasFlag(StateOf(cachedNode)) ? (MeasureCached(MapLabelText(cachedNode), zoom).X / 2) + 20 : 40;
        var text = cachedNode.WeightText;
        Vector2 pos = new(nodeCurrentPosition.Center.X + offsetX + MapNameOffsetX,
                          nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY);
        DrawCenteredTextWithBackground(text, new Vector2(pos.X + MeasureCached(text, zoom).X / 2f, pos.Y),
            wc, OverlayBg, true, 8, 3);
    }
    private static readonly Vector2[] StrokeOffsets = { new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };

    #endregion

    #region Label Styles

    private string MapLabelText(Node node)
        => Settings.Graphics.UppercaseMapNames ? node.UppercaseName : node.Name;

    private const float SpecialMarkerLift = 40f;

    private readonly LabelStyle _scratchLabelStyle = new();

    private LabelStyle ResolveLabelStyle(Node node)
    {
        var s = _scratchLabelStyle;
        s.CopyFrom(Settings.Labels.Base);

        MapOverride(node)?.ApplyTo(s);

        HighestWeightContentOverride(node)?.ApplyTo(s);

        if (node.IsFavorited && !node.IsDone)
            Settings.Labels.Favorite.ApplyTo(s);

        if (node.IsSpecial)
            Settings.Labels.Special.ApplyTo(s);

        ApplyWeightTints(s, node);
        s.Text.Scale *= LabelZoom(node);
        return s;
    }

    private float LabelZoom(Node node)
        => Settings.Graphics.ScaleLabelsWithZoom
            ? MathF.Round(NodeZoom(node, GetNodeRect(node)) * 20f) / 20f
            : 1f;

    private LabelStyleOverride HighestWeightContentOverride(Node node)
    {
        if (contentOverrideByCoord.TryGetValue(node.Coordinates, out var memo))
            return memo;
        var overrides = Settings.Labels.Content;
        LabelStyleOverride best = null;
        if (overrides.Count > 0 && node.Content.Count > 0) {
            float bestW = float.NegativeInfinity;
            foreach (var c in node.Content.Values) {
                if (c?.Id == null || !overrides.TryGetValue(c.Id, out var ov) || !ov.Enabled)
                    continue;
                float w = Settings.ReadContent(c.Id).Weight;
                if (w > bestW) { bestW = w; best = ov; }
            }
        }
        contentOverrideByCoord[node.Coordinates] = best;
        return best;
    }

    private LabelStyleOverride ResolveIconOverride(Node node)
    {
        var content = HighestWeightContentOverride(node);
        if (content != null && content.IconEnabled)
            return content;
        var map = MapOverride(node);
        if (map != null && map.IconEnabled)
            return map;
        return null;
    }

    private LabelStyleOverride ResolveMapIcon(Node node)
    {
        if (node.IsSpecial && Settings.Labels.Special.MapIcon != SpriteIcon.Circle)
            return Settings.Labels.Special;
        if (node.IsFavorited && !node.IsDone && Settings.Labels.Favorite.MapIcon != SpriteIcon.Circle)
            return Settings.Labels.Favorite;
        var content = HighestWeightContentOverride(node);
        if (content != null && content.MapIcon != SpriteIcon.Circle)
            return content;
        var map = MapOverride(node);
        if (map != null && map.MapIcon != SpriteIcon.Circle)
            return map;
        return null;
    }

    private LabelStyleOverride ResolveMapIconTint(Node node)
    {
        if (node.IsSpecial && Settings.Labels.Special.OverrideMapIconTint)
            return Settings.Labels.Special;
        if (node.IsFavorited && !node.IsDone && Settings.Labels.Favorite.OverrideMapIconTint)
            return Settings.Labels.Favorite;
        var content = HighestWeightContentOverride(node);
        if (content != null && content.OverrideMapIconTint)
            return content;
        var map = MapOverride(node);
        if (map != null && map.OverrideMapIconTint)
            return map;
        return null;
    }

    private void ApplyWeightTints(LabelStyle s, Node node)
    {
        if (!s.TextColorByWeight && !s.BoxColorByWeight && !s.BorderColorByWeight)
            return;
        var wc = WeightRampColor(node.Weight);
        if (s.TextColorByWeight) s.Text.Color = ColorUtils.WithAlphaOf(wc, s.Text.Color);
        if (s.BoxColorByWeight) s.Text.BgColor = ColorUtils.WithAlphaOf(wc, s.Text.BgColor);
        if (s.BorderColorByWeight) s.Text.BorderColor = ColorUtils.WithAlphaOf(wc, s.Text.BorderColor);
    }

    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (cachedNode.IsSpecial)
            return;

        if (!Settings.Graphics.DrawNames.HasFlag(StateOf(cachedNode)) ||
            cachedNode.IsDone || !MapTune(cachedNode).Highlight)
            return;

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(MapNameOffsetX, Settings.Graphics.MapNameOffsetY);
        var style = ResolveLabelStyle(cachedNode);
        DrawStyledLabel(MapLabelText(cachedNode), namePosition, style);
    }

    private void DrawSpecialMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!cachedNode.IsSpecial || Settings.Graphics.DrawNames == NodeStates.None || string.IsNullOrEmpty(cachedNode.Name))
            return;

        if (SpecialHiddenWhenCompleted(cachedNode))
            return;

        var style = ResolveLabelStyle(cachedNode);

        if (SpecialFadesWhenCompleted(cachedNode)) {
            Color washed = Desaturate(style.Text.BorderColor, 0.85f);
            style.Text.Color = Color.FromArgb(255, washed.R, washed.G, washed.B);
            style.Text.BorderColor = washed;
            style.Text.BgColor = Color.FromArgb(0, 0, 0);
            style.Text.BgEnabled = true;
            style.Text.Scale *= 1.15f;
        }

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(MapNameOffsetX, Settings.Graphics.MapNameOffsetY);
        DrawStyledLabel(MapLabelText(cachedNode), namePosition, style);
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> ContentIconAliases =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        ["Unique Map"]        = "unique",
        ["Corrupted"]         = "corruption",
        ["Corrupted Nexus"]   = "corruption",
        ["Map Boss"]          = "mapboss",
        ["Anomaly Map Boss"]  = "mapboss",
        ["Powerful Map Boss"] = "mapboss",
    };

    #endregion

    #region Content + Biome Icons

    private const float ContentRowOffsetY = 32f;

    private string ResolveContentIconFile(string contentName)
    {
        if (contentIconFileCache.TryGetValue(contentName, out var cached))
            return cached;

        string resolved = null;
        if (ContentIconAliases.TryGetValue(contentName, out var alias)) {
            var aliased = $"icon-{alias}.png";
            if (loadedContentIcons.Contains(aliased))
                resolved = aliased;
        }
        if (resolved == null) {
            var literal = $"icon-{contentName.ToLower().Replace(" ", "")}.png";
            if (loadedContentIcons.Contains(literal))
                resolved = literal;
        }

        contentIconFileCache[contentName] = resolved;
        return resolved;
    }

    private static readonly System.Collections.Generic.Dictionary<string, string> BiomeIconAliases =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        { "EzomyteCity", "ezomyte" },
        { "FaridunCity", "faridun" },
        { "VaalCity", "vaal" },
        { "BreachCity", "breach" },
        { "OriathCity", "oriath" },
    };

    private string ResolveBiomeIconFile(string biomeName)
    {
        if (string.IsNullOrEmpty(biomeName))
            return null;
        if (biomeIconFileCache.TryGetValue(biomeName, out var cached))
            return cached;

        string resolved = null;
        if (BiomeIconAliases.TryGetValue(biomeName, out var alias)) {
            var aliased = $"biome-{alias}.png";
            if (loadedBiomeIcons.Contains(aliased))
                resolved = aliased;
        }
        if (resolved == null) {
            var literal = $"biome-{biomeName.ToLower().Replace(" ", "")}.png";
            if (loadedBiomeIcons.Contains(literal))
                resolved = literal;
        }

        biomeIconFileCache[biomeName] = resolved;
        return resolved;
    }

    private void DrawContentRow(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowContentRow || loadedContentIcons.Count == 0)
                return;
            if (cachedNode.IsDone && !cachedNode.IsAttempted)
                return;
            if (!MapTune(cachedNode).Highlight)
                return;

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);

            float size = Settings.Graphics.IconSize * zoom;
            float spacing = 2f * zoom;
            Color tint = Color.White;
            var fullUV = new RectangleF(0, 0, 1, 1);

            var icons = contentIconScratch;
            icons.Clear();

            void AddIcon(string contentName, string title = null) {
                var iconName = ResolveContentIconFile(contentName);
                if (iconName == null)
                    return;
                for (int j = 0; j < icons.Count; j++)
                    if (icons[j].file == iconName)
                        return;
                icons.Add((iconName, title ?? contentName, tint));
            }

            foreach (var (contentName, content) in cachedNode.Content) {
                if (!Settings.ReadContent(content?.Id).Highlight)
                    continue;
                if (Settings.Graphics.ContentIconsSkipGameDrawn && cachedNode.IsVisible
                    && !string.IsNullOrEmpty(content.AtlasIcon))
                    continue;
                AddIcon(contentName);
            }

            if (!string.IsNullOrEmpty(cachedNode.AtlasPointType))
                AddIcon(cachedNode.AtlasPointType);

            if (cachedNode.GivesAtlasPoint && Settings.ContentDisplay.ShowAtlasPoint(cachedNode.AtlasPointType))
                AddIcon("Atlas Point", string.IsNullOrEmpty(cachedNode.AtlasPointType)
                    ? "Awards an atlas point"
                    : $"Awards an atlas point ({cachedNode.AtlasPointType})");

            if (icons.Count == 0)
                return;

            float totalWidth = icons.Count * size + (icons.Count - 1) * spacing;
            float startX = nodeCurrentPosition.Center.X - totalWidth / 2f;
            float centerY = nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY
                          + ContentRowOffsetY * zoom;

            contentRowTopByCoord[cachedNode.Coordinates] = centerY - size / 2f;

            for (int i = 0; i < icons.Count; i++) {
                float x = startX + i * (size + spacing);
                var iconRect = new RectangleF(x, centerY - size / 2f, size, size);
                Graphics.DrawImage(icons[i].file, iconRect, fullUV, icons[i].tint);
                contentIconRects.Add((iconRect, icons[i].content));
            }
        } catch (Exception e) {
            LogError("Error drawing content row: " + e.Message);
        }
    }

    private float NodeZoom(Node node, RectangleF nodePos)
    {
        float artW = node.ArtWidth > 1f ? node.ArtWidth : 40f;
        float mag = nodePos.Width / artW;
        if (mag > maxNodeZoomMagnification) maxNodeZoomMagnification = mag;
        float zoom = maxNodeZoomMagnification > 0.0001f ? mag / maxNodeZoomMagnification : 1f;
        return Math.Clamp(zoom / Settings.Graphics.ZoomScaleStart, Settings.Graphics.MinZoomScale, 1f);
    }

    private float NameHalf(Node node)
    {
        if (nameHalfByCoord.TryGetValue(node.Coordinates, out var half))
            return half;
        var style = ResolveLabelStyle(node);
        half = Settings.Graphics.DrawNames.HasFlag(StateOf(node))
            ? (MeasureCached(MapLabelText(node), style.Text.Scale).X + 10f) / 2f
            : 20f;
        nameHalfByCoord[node.Coordinates] = half;
        return half;
    }

    private RectangleF BiomeSlotRect(Node node, RectangleF nodePos, float size, float zoom)
    {
        float gap = size * 0.28f;
        float boxLeft = nodePos.Center.X + MapNameOffsetX - NameHalf(node);
        float x = boxLeft - gap - size;
        float centerY = nodePos.Center.Y + Settings.Graphics.MapNameOffsetY
                      + 0f * zoom;
        return new RectangleF(x, centerY - size / 2f, size, size);
    }

    private void DrawBiomeIcon(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowBiomeIcon || loadedBiomeIcons.Count == 0)
                return;
            if (cachedNode.IsDone && !cachedNode.IsAttempted)
                return;
            if (!MapTune(cachedNode).Highlight || cachedNode.Biomes.Count == 0)
                return;

            string bestName = null, bestFile = null;
            float bestW = float.NegativeInfinity;
            foreach (var b in cachedNode.Biomes.Values) {
                if (b?.Name == null)
                    continue;
                float w = Settings.BiomeWeight(b.Name);
                if (w <= bestW)
                    continue;
                var file = ResolveBiomeIconFile(b.Name);
                if (file == null)
                    continue;
                bestW = w;
                bestName = b.Name;
                bestFile = file;
            }
            if (bestFile == null)
                return;

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);
            float size = Settings.Graphics.IconSize * zoom;
            var rect = BiomeSlotRect(cachedNode, nodeCurrentPosition, size, zoom);
            Graphics.DrawImage(bestFile, rect, new RectangleF(0, 0, 1, 1), Color.White);

            biomeIconRectByCoord[cachedNode.Coordinates] = rect;
            contentIconRects.Add((rect, bestName));
        } catch (Exception e) {
            LogError("Error drawing biome icon: " + e.Message);
        }
    }

    private float LeftIconClusterEdge(Node node, RectangleF nodePos, float zoom)
    {
        float edge = nodePos.Center.X + MapNameOffsetX - NameHalf(node);
        bool hasBiome = biomeIconRectByCoord.TryGetValue(node.Coordinates, out var biomeRect);
        if (hasBiome)
            edge = biomeRect.Left;
        if (node.IsFavorited && !node.IsDone) {
            float favSize = Settings.Labels.Favorite.IconSize * zoom;
            if (hasBiome)
                edge = biomeRect.X - biomeRect.Height * 0.12f - favSize;
            else
                edge = BiomeSlotRect(node, nodePos, favSize, zoom).Left;
        }
        return edge;
    }

    private float RightIconClusterEdge(Node node, RectangleF nodePos)
    {
        float edge = nodePos.Center.X + MapNameOffsetX + NameHalf(node);
        if (ShowsWeightValue(node)) {
            float zoom = LabelZoom(node);
            float offsetX = Settings.Graphics.DrawNames.HasFlag(StateOf(node))
                ? MeasureCached(MapLabelText(node), zoom).X / 2f + 20f : 40f;
            float weightRight = nodePos.Center.X + MapNameOffsetX + offsetX
                              + MeasureCached(node.WeightText, zoom).X + 8f;
            if (weightRight > edge)
                edge = weightRight;
        }
        return edge;
    }

    private void DrawOverrideIcon(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (cachedNode.IsDone && !cachedNode.IsAttempted)
                return;
            if (!MapTune(cachedNode).Highlight)
                return;

            var ov = ResolveIconOverride(cachedNode);
            if (ov == null)
                return;

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);
            float size = ov.IconSize * zoom;

            float gap = size * 0.28f;
            float rowY = nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY;
            Vector2 center;
            switch (ov.IconPosition) {
                case IconPosition.AboveIcon:
                    center = new Vector2(nodeCurrentPosition.Center.X, nodeCurrentPosition.Top - size * 0.5f);
                    break;
                case IconPosition.BelowLabel:
                    center = new Vector2(nodeCurrentPosition.Center.X,
                        rowY + (ContentRowOffsetY + Settings.Graphics.IconSize) * zoom);
                    break;
                case IconPosition.RightOfLabel:
                    center = new Vector2(RightIconClusterEdge(cachedNode, nodeCurrentPosition) + gap + size * 0.5f, rowY);
                    break;
                default:
                    center = new Vector2(LeftIconClusterEdge(cachedNode, nodeCurrentPosition, zoom) - gap - size * 0.5f, rowY);
                    break;
            }
            DrawNodeSprite(center, size, size, ov.Icon, ov.IconTint, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing override icon: " + e.Message);
        }
    }

    private static readonly Color IconTooltipBg = Color.FromArgb(230, 20, 20, 20);
    private static readonly Color IconTooltipBorder = Color.FromArgb(255, 90, 90, 90);
    private static readonly Color OverlayText = Color.White;
    private static readonly Color OverlayBg = Color.FromArgb(180, 0, 0, 0);

    private void DrawIconTooltips()
    {
        if (contentIconRects.Count == 0)
            return;

        try {
            Vector2 cursor = ImGuiNET.ImGui.GetMousePos();
            string title = null;
            for (int i = contentIconRects.Count - 1; i >= 0; i--) {
                var r = contentIconRects[i].rect;
                if (cursor.X >= r.Left && cursor.X <= r.Right && cursor.Y >= r.Top && cursor.Y <= r.Bottom) {
                    title = contentIconRects[i].title;
                    break;
                }
            }
            if (title == null)
                return;

            Vector2 pos = cursor + new Vector2(16, 16);
            using (Graphics.SetTextScale(1.0f)) {
                DrawCenteredTextWithBorder(title, pos + new Vector2(Graphics.MeasureText(title).X / 2f, 0),
                    Color.White, IconTooltipBg, IconTooltipBorder, 10, 6);
            }
        } catch (Exception e) {
            DebugSwallow("DrawIconTooltips", e);
        }
    }

    #endregion

    #region Text + Image Primitives

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);

        boxSize += new Vector2(xPadding, yPadding);

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);
        position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
    }

    private void DrawCenteredTextWithBorder(string text, Vector2 position, Color color, Color backgroundColor, Color borderColor, int xPadding, int yPadding)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text) + new Vector2(xPadding, yPadding);
        var topLeft = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);
        topLeft = new Vector2(MathF.Round(topLeft.X), MathF.Round(topLeft.Y));

        Graphics.DrawBox(topLeft, topLeft + boxSize, borderColor, 5.0f);
        Graphics.DrawBox(topLeft + new Vector2(2, 2), topLeft + boxSize - new Vector2(2, 2), backgroundColor, 4.0f);
        Graphics.DrawText(text, topLeft + new Vector2(xPadding / 2f, yPadding / 2f), color);
    }

    private void DrawStyledLabel(string text, Vector2 position, LabelStyle style)
    {
        if (string.IsNullOrEmpty(text) || !IsOnScreen(position))
            return;

        var t = style.Text;
        using (Graphics.SetTextScale(t.Scale)) {
            var boxSize = MeasureCached(text, t.Scale) + new Vector2(10, 4);
            var topLeft = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);
            topLeft = new Vector2(MathF.Round(topLeft.X), MathF.Round(topLeft.Y));

            if (t.BgEnabled) {
                Graphics.DrawBox(topLeft, topLeft + boxSize, t.BgColor, t.BorderRounding);
            }
            if (t.BorderEnabled) {
                Graphics.DrawFrame(topLeft, topLeft + boxSize, t.BorderColor, t.BorderRounding, (int)t.BorderThickness, 0);
            }

            var textPos = topLeft + new Vector2(5f, 2f);
            if (t.StrokeEnabled) {
                var sc = Color.FromArgb(255, t.StrokeColor.R, t.StrokeColor.G, t.StrokeColor.B);
                float spread = MathF.Max(1f, MathF.Round(t.Scale));
                foreach (var o in StrokeOffsets)
                    Graphics.DrawText(text, textPos + o * spread, sc);
            }
            var textCol = Color.FromArgb(255, t.Color.R, t.Color.G, t.Color.B);
            Graphics.DrawText(text, textPos, textCol);
        }
    }

    private void DrawRotatedImage(IntPtr textureId, Vector2 position, Vector2 size, float angle, Color color)
    {
        Vector2 center = position + size / 2;

        float cosTheta = (float)Math.Cos(angle);
        float sinTheta = (float)Math.Sin(angle);

        Vector2 RotatePoint(Vector2 point)
        {
            Vector2 translatedPoint = point - center;
            Vector2 rotatedPoint = new Vector2(
                translatedPoint.X * cosTheta - translatedPoint.Y * sinTheta,
                translatedPoint.X * sinTheta + translatedPoint.Y * cosTheta
            );
            return rotatedPoint + center;
        }

        Vector2 topLeft = RotatePoint(position);
        Vector2 topRight = RotatePoint(position + new Vector2(size.X, 0));
        Vector2 bottomRight = RotatePoint(position + size);
        Vector2 bottomLeft = RotatePoint(position + new Vector2(0, size.Y));

        Graphics.DrawQuad(textureId, topLeft, topRight, bottomRight, bottomLeft, color);
        }

    private const int PageOwner = 0x310;
    private const int OwnerConnections = 0x17E8;
    private const int ConnRecords = 984;
    private const int ConnCount = 1000;
    private const int RecordStride = 184;
    private const int RecP0 = 56;
    private const int RecSegments = 176;

    #endregion

    #region Connection Curves

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Vector2i, Vector2i), Vector3[]> connectionCurves = new();
    private long lastCurveRecordCount = -1;

    private static (Vector2i, Vector2i) EdgeKey(Vector2i a, Vector2i b) =>
        (a.X < b.X || (a.X == b.X && a.Y <= b.Y)) ? (a, b) : (b, a);

    private static long PosKey(Vector3 v) =>
        ((long)MathF.Round(v.X * 8f) * 73856093) ^
        ((long)MathF.Round(v.Y * 8f) * 19349663) ^
        ((long)MathF.Round(v.Z * 8f) * 83492791);

    private void RefreshConnectionCurves(IReadOnlyCollection<Node> nodes)
    {
        if (!Settings.Graphics.UseGameConnectionCurves)
        {
            connectionCurves.Clear();
            return;
        }

        try
        {
            long atlas = AtlasPanel?.Address ?? 0;
            if (atlas == 0)
                return;

            var mem = GameController.Memory;
            long owner = mem.Read<long>(atlas + PageOwner);
            if (owner == 0)
                return;

            long wmc = owner + OwnerConnections;
            long records = mem.Read<long>(wmc + ConnRecords);
            long count = mem.Read<long>(wmc + ConnCount);

            if (records == 0 || count <= 0 || count > 200000)
            {
                if (Settings.Features.DebugMode)
                    LogMessage($"ConnectionCurves: bad read (records={records:X} count={count}), offsets likely stale after a patch");
                return;
            }

            if (count == lastCurveRecordCount && !connectionCurves.IsEmpty)
                return;
            lastCurveRecordCount = count;

            var byPos = new Dictionary<long, Vector2i>(nodes.Count);
            foreach (var n in nodes)
            {
                var d3d = n.MapNode?.Description3D;
                if (d3d == null)
                    continue;
                byPos[PosKey(d3d.Position)] = n.Coordinates;
            }
            if (byPos.Count == 0)
                return;

            byte[] blob = mem.ReadBytes(records, (int)count * RecordStride);
            if (blob == null || blob.Length < count * RecordStride)
                return;

            const int extra = 1;

            var built = connectionCurves;
            int addedThisPass = 0;

            for (int i = 0; i < count; i++)
            {
                int o = i * RecordStride;
                Vector3 p0 = ReadVec3(blob, o + RecP0);
                Vector3 p1 = ReadVec3(blob, o + RecP0 + 16);
                Vector3 p2 = ReadVec3(blob, o + RecP0 + 32);
                Vector3 p3 = ReadVec3(blob, o + RecP0 + 48);
                int segments = BitConverter.ToInt32(blob, o + RecSegments);

                if (segments < 2)
                    continue;

                if (!byPos.TryGetValue(PosKey(p1), out var ca) || !byPos.TryGetValue(PosKey(p2), out var cb))
                    continue;

                var key = EdgeKey(ca, cb);
                if (built.ContainsKey(key))
                    continue;
                addedThisPass++;

                int steps = Math.Min((segments - 1) * extra, 512);
                var pts = new Vector3[steps + 1];
                HermiteCoefficients(p0, p1, p2, p3, out var a, out var b, out var c, out var d);
                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    pts[s] = ((a * t + b) * t + c) * t + d;
                }

                pts[0] = p1;
                pts[steps] = p2;

                if (key.Item1 != ca)
                    Array.Reverse(pts);
                built[key] = pts;
            }

            if (addedThisPass > 0 && Settings.Features.DebugMode)
                LogMessage($"ConnectionCurves: built {addedThisPass} new curves ({built.Count} total)");
        }
        catch (Exception e)
        {
            DebugSwallow("ConnectionCurves: refresh", e);
        }
    }

    private static Vector3 ReadVec3(byte[] b, int o) => new(
        BitConverter.ToSingle(b, o),
        BitConverter.ToSingle(b, o + 4),
        BitConverter.ToSingle(b, o + 8));

    private static void HermiteCoefficients(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                                            out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        const float eps = 5e-2f;

        Vector3 dp0 = p1 - p0, dp1 = p2 - p1, dp2 = p3 - p2;
        float dt0 = dp0.Length(), dt1 = dp1.Length(), dt2 = dp2.Length();

        d = p1;

        if (dt1 < eps)
        {
            a = b = c = Vector3.Zero;
            return;
        }

        Vector3 m1 = (dt0 < eps) ? Vector3.Zero : Vector3.Lerp(dp0 * (dt1 / dt0), dp1, dt0 / (dt0 + dt1));
        Vector3 m2 = (dt2 < eps) ? Vector3.Zero : Vector3.Lerp(dp1, dp2 * (dt1 / dt2), dt1 / (dt1 + dt2));

        if (dt0 < eps && dt2 < eps)
        {
            a = b = Vector3.Zero;
            c = dp1;
            return;
        }

        if (dt0 < eps)
        {
            a = Vector3.Zero;
            b = p1 - p2 + m2;
            c = -2f * p1 + 2f * p2 - m2;
            return;
        }

        if (dt2 < eps)
        {
            a = Vector3.Zero;
            b = -p1 + p2 - m1;
            c = m1;
            return;
        }

        a = 2f * p1 - 2f * p2 + m1 + m2;
        b = -3f * p1 + 3f * p2 - 2f * m1 - m2;
        c = m1;
    }

    private const float CurvePixelsPerSample = 16f;

    private Vector2[] GetCurveScreenPoints(Vector2i from, Vector2i to, out bool reversed)
    {
        var key = EdgeKey(from, to);
        reversed = key.Item1 != from;

        if (frameCurveCache.TryGetValue(key, out var cached))
            return cached;

        if (!connectionCurves.TryGetValue(key, out var world))
            return null;

        var project = frameWorldToScreen;
        if (project == null || world == null || world.Length < 2)
            return null;

        int n = world.Length;
        Vector2 a = project(world[0]);
        Vector2 b = project(world[n - 1]);

        int want = (int)(Vector2.Distance(a, b) / CurvePixelsPerSample) + 2;
        if (want > n) want = n;
        if (want < 2) want = 2;

        var screen = new Vector2[want];
        screen[0] = a;
        screen[want - 1] = b;
        for (int i = 1; i < want - 1; i++)
            screen[i] = project(world[i * (n - 1) / (want - 1)]);

        frameCurveCache[key] = screen;
        return screen;
    }

    #endregion

}
