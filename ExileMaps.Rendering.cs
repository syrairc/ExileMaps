using System;
using System.Drawing;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Enums;
using GameOffsets2.Native;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Drawing runs in fixed z-layer passes over the whole set:
    //   1. DrawNodeLines  - connections
    //   2. DrawMapNode    - node fill
    //   3. DrawNodeRings  - content rings
    //   4. DrawNodeLabels - name, weight
    // Waypoints draw on top. Global z-order prevents lines drawing over fills/labels.
    private void DrawNodeLines(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawConnections(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node lines: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeRings(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            // Special maps (wider node art) get an icon above the node, not a covering fill —
            // don't ring them either, the rings would overlap the map art.
            if (Settings.Graphics.ShowSpecialMapIndicator && nodeCurrentPosition.Width > Settings.Graphics.SpecialMapWidthThreshold)
                return;

            var ringCount = 0;
            foreach (var contentName in cachedNode.Content.Keys)
                ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, contentName);
        } catch (Exception e) {
            LogError("Error drawing node rings: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeLabels(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawMapName(cachedNode, nodeCurrentPosition);
            DrawWeight(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node labels: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {       
         foreach (Vector2i coordinates in cachedNode.GetNeighborCoordinates())
        {
            if (coordinates == default)
                continue;
            
            if (!mapCache.TryGetValue(coordinates, out Node destinationNode))
                continue;
                
            if (!Settings.Features.DrawVisitedNodeConnections && (destinationNode.IsVisited || cachedNode.IsVisited))
                continue;

            if ((!Settings.Features.DrawHiddenNodeConnections || !Settings.Features.ProcessHiddenNodes) && (!destinationNode.IsVisible || !cachedNode.IsVisible))
                continue;
            
            var destinationPos = destinationNode.MapNode.Element.GetClientRect();

            if (!IsLineVisible(nodeCurrentPosition.Center, destinationPos.Center))
                continue;

            if (Settings.Graphics.DrawGradientLines) {
                Color sourceColor = cachedNode.IsVisited ? Settings.Graphics.VisitedLineColor : cachedNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                Color destinationColor = destinationNode.IsVisited ? Settings.Graphics.VisitedLineColor : destinationNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                
                if (sourceColor == destinationColor)
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor);
                else
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor, destinationColor);

            } else {
                var color = Settings.Graphics.LockedLineColor;

                if (destinationNode.IsUnlocked || cachedNode.IsUnlocked)
                    color = Settings.Graphics.UnlockedLineColor;
                
                if (destinationNode.IsVisited && cachedNode.IsVisited)
                    color = Settings.Graphics.VisitedLineColor;

                Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, color);
            }
            
        }
    }

    // Draws a content ring at the given ring-count offset. Returns 1 if drawn, 0 if skipped.
    private int DrawContentRings(Node cachedNode, RectangleF nodeCurrentPosition, int Count, string Content)
    {
        if ((cachedNode.IsVisited && !cachedNode.IsAttempted) || 
            (!Settings.Maps.Content.ShowRingsOnLockedNodes && !cachedNode.IsUnlocked) || 
            (!Settings.Maps.Content.ShowRingsOnUnlockedNodes && cachedNode.IsUnlocked) || 
            (!Settings.Maps.Content.ShowRingsOnHiddenNodes && !cachedNode.IsVisible) ||         
            !cachedNode.Content.TryGetValue(Content, out Content cachedContent) || 
            !cachedNode.MapType.Highlight || !cachedContent.Highlight)            
            return 0;

        float baseRadius = 1 + ((nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 2 * Settings.Graphics.RingRadius);
        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            // Icons are scaled up by ContentRingIconScale, so the per-ring step must widen too or
            // stacked icons overlap. ContentRingIconSpacing adds the extra gap.
            float step = Settings.Graphics.RingWidth * Settings.Graphics.ContentRingIconSpacing;
            float radius = (Count * step) + baseRadius;
            float d = radius * 2f * Settings.Graphics.ContentRingIconScale;
            DrawNodeSprite(nodeCurrentPosition.Center, d, d, SpriteIcon.CircleOutline, cachedContent.Color);
        }
        else {
            float radius = (Count * Settings.Graphics.RingWidth) + baseRadius;
            Graphics.DrawCircle(nodeCurrentPosition.Center, radius, cachedContent.Color, Settings.Graphics.RingWidth, 32);
        }

        return 1;
    }
    

    private void DrawMapNode(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.HighlightMapNodes || cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;
            
        // "Special" maps have wider node art. Skip the solid circle (it would cover the map art) —
        // they get an icon drawn above the node by DrawSpecialIndicator instead.
        if (Settings.Graphics.ShowSpecialMapIndicator && nodeCurrentPosition.Width > Settings.Graphics.SpecialMapWidthThreshold)
            return;

        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        var weight = cachedNode.Weight;
        Color color = cachedNode.MapType.ColorNodesByWeight ? ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, (weight - minMapWeight) / (maxMapWeight - minMapWeight)) : cachedNode.MapType.NodeColor;

        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            DrawNodeSprite(nodeCurrentPosition.Center, radius * 2f, radius * 2f, cachedNode.MapType.Icon, color);
        } else {
            Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
        }
    }

    // Converts SpriteAtlas UV pair to RectangleF (x, y, w, h) for Graphics.DrawImage.
    private static RectangleF GetSpriteUV(SpriteIcon icon)
    {
        var (uv0, uv1) = SpriteAtlas.GetUVPair(icon);
        return new RectangleF(uv0.X, uv0.Y, uv1.X - uv0.X, uv1.Y - uv0.Y);
    }

    private bool CustomIconsAvailable => customIconsLoaded;

    // Draws a custom-atlas sprite centered at center. IconFlatten squashes height so round sprites
    // read as flat discs on the tilted atlas plane.
    private void DrawNodeSprite(Vector2 center, float width, float height, SpriteIcon icon, Color color, bool allowFlatten = true)
    {
        float h = allowFlatten ? height * (1f - Settings.Graphics.IconFlatten) : height;
        Graphics.DrawImage(CustomIconsName, new RectangleF(center.X - width / 2f, center.Y - h / 2f, width, h), GetSpriteUV(icon), color);
    }

    // Draws an icon above special map nodes (wider than threshold) instead of covering them with a fill.
    private void DrawSpecialIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowSpecialMapIndicator || !Settings.Maps.HighlightMapNodes ||
                cachedNode.IsVisited || cachedNode.MapType == null || !cachedNode.MapType.Highlight ||
                nodeCurrentPosition.Width <= Settings.Graphics.SpecialMapWidthThreshold)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.SpecialMapIconScale;

            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(iconSize.X / 2, Settings.Graphics.SpecialMapIconOffset + iconSize.Y / 2);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, Settings.Graphics.SpecialMapIcon, Settings.Graphics.SpecialMapColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteHexagon), Settings.Graphics.SpecialMapColor);
        } catch (Exception e) {
            LogError("Error drawing special map indicator: " + e.Message);
        }
    }

    // Fallback tint when a style key is missing from settings (silver).
    private static readonly Color AtlasPointColor = Color.FromArgb(255, 200, 200, 205);

    // Draws a marker above nodes that grant an atlas passive point. Color and icon are user-configurable
    // per type (Settings.Graphics.AtlasPointColors/AtlasPointIcons); "Generic" = non-content points.
    private void DrawAtlasPointIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasPointIndicator || !cachedNode.GivesAtlasPoint || cachedNode.IsVisited || !customIconsLoaded)
                return;

            string key = string.IsNullOrEmpty(cachedNode.AtlasPointType) ? "Generic" : cachedNode.AtlasPointType;
            Color color = Settings.Graphics.AtlasPointColors.TryGetValue(key, out var cn) ? cn : AtlasPointColor;
            SpriteIcon icon = Settings.Graphics.AtlasPointIcons.TryGetValue(key, out var ic) ? ic : SpriteIcon.Star8;

            float size = Settings.Graphics.AtlasIndicatorSize;
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, icon, color, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas point indicator: " + e.Message);
        }
    }

    // Golden tint for the atlas-quest marker.
    private static readonly Color AtlasQuestColor = Color.FromArgb(255, 255, 200, 40);

    // Draws a golden exclamation above nodes that have atlas quest content.
    private void DrawAtlasQuestIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasQuestIndicator || !cachedNode.HasAtlasQuest || cachedNode.IsVisited || !customIconsLoaded)
                return;

            float size = Settings.Graphics.AtlasIndicatorSize;
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X + size, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Exclamation, AtlasQuestColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas quest indicator: " + e.Message);
        }
    }

    // Draws a star above favorited nodes.
    private void DrawFavoriteIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!cachedNode.IsFavorited || cachedNode.IsVisited)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.FavoriteIconScale;

            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(0, nodeCurrentPosition.Height / 2 + 20);
            iconPosition -= new Vector2(iconSize.X / 2, iconSize.Y);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, SpriteIcon.Star5, Settings.Graphics.FavoriteColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteStar), Settings.Graphics.FavoriteColor);
        } catch (Exception e) {
            LogError("Error drawing favorite indicator: " + e.Message);
        }
    }

    private void DrawWeight(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.DrawWeightOnMap ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;  

        float norm = (maxMapWeight - minMapWeight) > 0
            ? (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight)
            : 0.5f;
        norm = Math.Clamp(norm, 0f, 1f);

        float offsetX = Settings.Maps.ShowMapNames ? (Graphics.MeasureText(cachedNode.Name.ToUpper()).X / 2) + 30 : 50;
        Vector2 position = new(nodeCurrentPosition.Center.X + offsetX + Settings.Graphics.MapNameOffsetX, nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY);

        DrawCenteredTextWithBackground($"{cachedNode.Weight:0}", position, ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, norm), Settings.Graphics.BackgroundColor, true, 10, 3);
    }
    /// <summary>
    /// Draws the name of the map on the atlas.
    /// </summary>
    /// <param name="cachedNode">The atlas node description containing information about the map.</param>
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Color fontColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.NameColor : Settings.Graphics.FontColor;
        Color backgroundColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.BackgroundColor : Settings.Graphics.BackgroundColor;

        bool isSpecial = nodeCurrentPosition.Width > Settings.Graphics.SpecialMapWidthThreshold;

        if (isSpecial) {
            fontColor = Settings.Graphics.SpecialMapNameColor;
        } else if (cachedNode.MapType.UseWeightColorForName) {
            float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
            fontColor = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, weight);
        }

        fontColor = Color.FromArgb(255, fontColor.R, fontColor.G, fontColor.B);

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);

        // Special maps render their name 20% larger.
        using (Graphics.SetTextScale(isSpecial ? 1.2f : 1.0f))
            DrawCenteredTextWithBackground(cachedNode.Name.ToUpper(), namePosition, fontColor, backgroundColor, true, 10, 3);
    }

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);

        boxSize += new Vector2(xPadding, yPadding);    

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);       

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
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
        private void DrawGradientLine(Vector2 start, Vector2 end, Color startColor, Color endColor, float lineWidth)
    {
        if (startColor == endColor)
        {
            Graphics.DrawLine(start, end, Settings.Graphics.MapLineWidth, startColor);
            return;
        }

        int segments = 10; // Number of segments to create the gradient effect
        Vector2 direction = (end - start) / segments;

        for (int i = 0; i < segments; i++)
        {
            Vector2 segmentStart = start + direction * i;
            Vector2 segmentEnd = start + direction * (i + 1);

            float t = (float)i / segments;
            Color segmentColor = ColorUtils.InterpolateColor(startColor, endColor, t);

            Graphics.DrawLine(segmentStart, segmentEnd, lineWidth, segmentColor);

        }
    }
}
