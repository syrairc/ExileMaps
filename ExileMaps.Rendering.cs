// Draw methods: nodes, lines, rings, labels, special indicators, map names.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
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
    // Per-frame memoized node rect. First call for a coordinate reads Element.GetClientRect()
    // (parent-chain walk); later calls this frame reuse it. frameRectCache is cleared/reseeded
    // once per frame in Render. Returns default on a null element or a failed read.
    private RectangleF GetNodeRect(Node node)
    {
        if (node?.MapNode?.Element == null)
            return default;
        if (frameRectCache.TryGetValue(node.Coordinates, out var cached))
            return cached;
        try {
            var rect = node.MapNode.Element.GetClientRect();
            frameRectCache[node.Coordinates] = rect;
            return rect;
        } catch (Exception e) {
            DebugSwallow("GetNodeRect: rect read", e);
            return default;
        }
    }

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
            if (!ContentRingsEnabled)
                return;

            // Special maps get an icon above the node, not a covering fill.
            // Don't ring them either; rings would overlap the map art.
            if (Settings.Graphics.ShowSpecialMapIndicator && cachedNode.IsSpecial)
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
            // Force scale 1.0 for normal names/weight. Without this the labels measure + center at
            // whatever text scale is ambient (e.g. the special-name pass's 1.4), inflating the
            // measured box so centering shoves the name far above the node. (Restores pre-refactor
            // behavior; the refactor dropped this explicit reset.)
            using (Graphics.SetTextScale(1.0f)) {
                DrawMapName(cachedNode, nodeCurrentPosition);
                DrawWeight(cachedNode, nodeCurrentPosition);
            }
        } catch (Exception e) {
            LogError("Error drawing node labels: " + e.Message + " - " + e.StackTrace);
        }
    }

    // While a map tooltip is up, the hovered node sits inside the tooltip exclude rect and so is
    // dropped from selectedNodes, blanking its icon + name. Redraw just that node on top of the
    // tooltip so it stays visible. Reuses the normal draw methods so the same gates/styling apply.
    private void DrawHoveredNodeOverTooltip()
    {
        if (ShowMinimap || Settings.Features.DebugMode || !mapTooltipVisible)
            return;

        try {
            var node = GetClosestNodeToCursor();
            if (node == null)
                return;

            var rect = node.MapNode.Element.GetClientRect();
            DrawMapNode(node, rect);            // fill icon (skips special maps)
            DrawContentIcons(node, rect);       // per-content-type icon PNGs
            DrawSpecialIndicator(node, rect);   // special-map icon
            using (Graphics.SetTextScale(1.0f)) // 1.0 for the normal name; special name self-scales
                DrawMapName(node, rect);        // normal map name (skips special maps)
            DrawSpecialMapName(node, rect);     // special-map name
        } catch (Exception e) {
            DebugSwallow("Render: hovered node over tooltip", e);
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
            
            // Reuse the neighbor's rect from the per-frame memo if it was already read this frame
            // (e.g. it's an on-screen selected node); otherwise this reads + caches it once.
            var destinationPos = GetNodeRect(destinationNode);

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
            
        // "Special" maps skip the solid circle (it would cover the art).
        // DrawSpecialIndicator draws an icon above the node instead.
        if (Settings.Graphics.ShowSpecialMapIndicator && cachedNode.IsSpecial)
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

    // Content indicator mode helpers: rings, icon PNGs, or both.
    private bool ContentRingsEnabled =>
        Settings.Graphics.ContentIndicators == ContentIndicatorMode.Rings ||
        Settings.Graphics.ContentIndicators == ContentIndicatorMode.Both;
    private bool ContentIconsEnabled =>
        Settings.Graphics.ContentIndicators == ContentIndicatorMode.Icons ||
        Settings.Graphics.ContentIndicators == ContentIndicatorMode.Both;

    // Draws a custom-atlas sprite centered on nodes. IconFlatten squashes height so round sprites
    // read as flat discs on the tilted atlas plane.
    private void DrawNodeSprite(Vector2 center, float width, float height, SpriteIcon icon, Color color, bool allowFlatten = true)
    {
        float h = allowFlatten ? height * (1f - Settings.Graphics.IconFlatten) : height;
        Graphics.DrawImage(CustomIconsName, new RectangleF(center.X - width / 2f, center.Y - h / 2f, width, h), GetSpriteUV(icon), color);
    }

    // Draws an icon above special map nodes instead of covering them with a fill. Drawn for special
    // nodes in any state (visited/locked included) — the caller iterates a dedicated on-screen set.
    private void DrawSpecialIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowSpecialMapIndicator || !cachedNode.IsSpecial)
                return;

            // User-defined specials render washed out vs the auto-detected ones. Common repeatable
            // specials (and any completed user-added) fade + shrink once completed; one-off landmarks
            // stay highlighted at full color/size.
            var entry = Settings.Maps.SpecialMaps.Find(cachedNode.Name);
            bool userAdded = entry != null;
            if (SpecialHiddenWhenCompleted(cachedNode, userAdded))
                return;
            bool fade = SpecialFadesWhenCompleted(cachedNode, userAdded);

            float scale = entry?.Scale ?? Settings.Graphics.SpecialMapIconScale;
            SpriteIcon icon = entry?.Icon ?? Settings.Graphics.SpecialMapIcon;
            Color baseCol = entry?.Color ?? Settings.Graphics.SpecialMapColor;
            Color color = fade        ? ScaleAlpha(Desaturate(baseCol, 0.85f), 0.5f)
                        : userAdded   ? Desaturate(baseCol, 0.55f)
                        :               baseCol;
            if (fade) scale *= 0.7f;

            Vector2 iconSize = new Vector2(48, 48) * scale;

            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(iconSize.X / 2, Settings.Graphics.SpecialMapIconOffset + iconSize.Y / 2);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, icon, color, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteHexagon), color);
        } catch (Exception e) {
            LogError("Error drawing special map indicator: " + e.Message);
        }
    }

    // Reduces saturation toward grey while preserving brightness (HSV value = the max channel), so the
    // color washes out rather than just darkening. amount 0 = unchanged, 1 = fully grey at the original
    // brightness. (Blending toward luminance instead darkens vivid colors — that was the earlier bug.)
    private static Color Desaturate(Color c, float amount)
    {
        int v = System.Math.Max(c.R, System.Math.Max(c.G, c.B));
        int r = (int)(c.R + (v - c.R) * amount);
        int g = (int)(c.G + (v - c.G) * amount);
        int b = (int)(c.B + (v - c.B) * amount);
        return Color.FromArgb(c.A, r, g, b);
    }

    // Scales a color's alpha (factor 1 = unchanged, 0 = transparent). RGB kept.
    private static Color ScaleAlpha(Color c, float factor)
        => Color.FromArgb((int)(c.A * factor), c.R, c.G, c.B);

    // Auto-detected specials that are common/repeatable rather than one-off landmarks: once completed
    // they should fade (wash out, dim, shrink) like a finished map instead of staying highlighted.
    // Names collapse biome/quest variants (all "Precursor Tower"s; both Mothersoul "...Halls").
    private static readonly System.Collections.Generic.HashSet<string> FadeWhenCompletedSpecials =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Precursor Tower",
        "The Matriarch Halls",
        "The Patriarch Halls",
    };

    // True when a special should fade: it's completed and either user-added or in the fade-set above.
    private static bool SpecialFadesWhenCompleted(Node node, bool userAdded)
        => node.IsCompleted && (userAdded || FadeWhenCompletedSpecials.Contains(node.Name));

    // True when a completed non-hub special should be hidden outright (marker + name) rather than faded.
    // Reuses the fade test, so it only ever hits repeatable/user specials, never hub specials.
    private bool SpecialHiddenWhenCompleted(Node node, bool userAdded)
        => Settings.Graphics.HideCompletedSpecialMaps && SpecialFadesWhenCompleted(node, userAdded);

    // Fallback tint when a style key is missing from settings (silver).
    private static readonly Color AtlasPointColor = Color.FromArgb(255, 200, 200, 205);

    // The Y above which the atlas-point/quest indicators should sit so they clear any content icons on
    // the node: our drawn content row (recorded this frame) or, on visible nodes, the game's own content
    // icon cluster. Defaults to the node's top edge when there are no content icons.
    private float IndicatorBaseTop(Node node, RectangleF rect)
    {
        float top = rect.Center.Y - rect.Height / 2f;

        if (contentRowTopByCoord.TryGetValue(node.Coordinates, out var rowTop)) {
            top = Math.Min(top, rowTop);
        } else if (node.IsVisible && node.Content.Values.Any(c => !string.IsNullOrEmpty(c.AtlasIcon))) {
            // Game-drawn content icons live in the node's in-game icon cluster (Element[0][0]).
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
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X, IndicatorBaseTop(cachedNode, nodeCurrentPosition) - size / 2f - 2f);
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
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X + size, IndicatorBaseTop(cachedNode, nodeCurrentPosition) - size / 2f - 2f);
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
    // Draws the map name above the node on the atlas.
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        // Special maps are named by DrawSpecialMapName (dedicated pass, distinct style, drawn in any
        // state). Skip them here so they don't fall foul of the visited/highlight gates below.
        if (cachedNode.IsSpecial)
            return;

        if (!Settings.Maps.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);
        string text = cachedNode.Name.ToUpper();

        if (Settings.Graphics.LegacyMapNameStyling) {
            // Old plain-background label.
            Color fontColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.NameColor : Settings.Graphics.FontColor;
            Color backgroundColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.BackgroundColor : Settings.Graphics.BackgroundColor;
            if (cachedNode.MapType.UseWeightColorForName) {
                float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
                fontColor = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, weight);
            }
            fontColor = Color.FromArgb(255, fontColor.R, fontColor.G, fontColor.B);
            DrawCenteredTextWithBackground(text, namePosition, fontColor, backgroundColor, true, 10, 3);
            return;
        }

        // New bordered-box style (same look as special names, normal font size). Text and border colors
        // each follow their own source: static, by weight, or by map color.
        Color textColor = ResolveLabelColor(Settings.Graphics.MapNameTextColorSource, Settings.Graphics.MapNameTextStaticColor, cachedNode);
        Color borderColor = ResolveLabelColor(Settings.Graphics.MapNameBorderColorSource, Settings.Graphics.MapNameBorderStaticColor, cachedNode);
        DrawCenteredTextWithBorder(text, namePosition, textColor, Settings.Graphics.BackgroundColor, borderColor, 10, 4);
    }

    // Resolves a map-label color from its configured source. Weight maps Bad->Good across the visible
    // weight range; MapColor uses the map type's name color; Static uses the picked color. Alpha forced.
    private Color ResolveLabelColor(LabelColorSource src, Color staticColor, Node node)
    {
        switch (src) {
            case LabelColorSource.Weight: {
                float denom = maxMapWeight - minMapWeight;
                float w = denom > 0.0001f ? (node.Weight - minMapWeight) / denom : 0.5f;
                w = Math.Clamp(w, 0f, 1f);
                var c = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, w);
                return Color.FromArgb(255, c.R, c.G, c.B);
            }
            case LabelColorSource.MapColor: {
                var c = node.MapType.NameColor;
                return Color.FromArgb(255, c.R, c.G, c.B);
            }
            default:
                return Color.FromArgb(255, staticColor.R, staticColor.G, staticColor.B);
        }
    }

    // Names special maps in a deliberately distinct style: larger, special-colored text on a plate with
    // a matching colored border. Drawn for any on-screen special (visited/locked included) via the
    // dedicated specialNodes pass, so it ignores the normal name-visibility gates. User-added specials
    // use their (desaturated) per-map color to match their marker.
    private void DrawSpecialMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!cachedNode.IsSpecial || !Settings.Maps.ShowMapNames || string.IsNullOrEmpty(cachedNode.Name))
            return;

        var entry = Settings.Maps.SpecialMaps.Find(cachedNode.Name);
        bool userAdded = entry != null;
        if (SpecialHiddenWhenCompleted(cachedNode, userAdded))
            return;
        bool fade = SpecialFadesWhenCompleted(cachedNode, userAdded);

        Color baseCol = entry?.Color ?? Settings.Graphics.SpecialMapNameColor;
        Color text, border;
        Color bg = Settings.Graphics.BackgroundColor;
        float nameScale = 1.4f;

        if (fade) {
            // Completed repeatable/user specials recede: washed text + dim border on a solid black
            // plate (readable), and smaller.
            Color washed = Desaturate(baseCol, 0.85f);
            text = Color.FromArgb(255, washed.R, washed.G, washed.B);
            border = ScaleAlpha(washed, 0.45f);
            bg = Color.FromArgb(255, 0, 0, 0);
            nameScale = 1.15f;
        } else if (userAdded) {
            // User-added (not yet completed): washed to set apart from auto-detected specials.
            Color washed = Desaturate(baseCol, 0.55f);
            text = Color.FromArgb(255, washed.R, washed.G, washed.B);
            border = washed;
        } else {
            // Auto-detected landmark: full color.
            text = Color.FromArgb(255, baseCol.R, baseCol.G, baseCol.B);
            border = text;
        }

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);

        // Larger than normal names, on a bordered plate in the name color (smaller when faded).
        using (Graphics.SetTextScale(nameScale))
            DrawCenteredTextWithBorder(cachedNode.Name.ToUpper(), namePosition, text, bg, border, 14, 6);
    }

    // Maps a content name to its icon-*.png base when the literal lowercase+stripped form doesn't
    // match a file (e.g. "Unique Map" -> "unique", any boss variant -> "mapboss").
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

    // Resolves a content name to a loaded icon file name, or null if none matches. Tries the alias
    // table first, then the literal lowercase+space-stripped form.
    private string ResolveContentIconFile(string contentName)
    {
        if (ContentIconAliases.TryGetValue(contentName, out var alias)) {
            var aliased = $"icon-{alias}.png";
            if (loadedContentIcons.Contains(aliased))
                return aliased;
        }
        var literal = $"icon-{contentName.ToLower().Replace(" ", "")}.png";
        return loadedContentIcons.Contains(literal) ? literal : null;
    }

    // Icon file used for content with no matching icon-<content>.png (tinted with UnknownContentColor).
    private const string BlankContentIcon = "icon-blank.png";

    // League mechanics the game does NOT draw its own in-game content icon for, even on visible maps.
    // Our icon always draws for these (exempt from the visible-node game-drawn dedup); the row layout
    // offsets them alongside any other content icons on the node.
    private static readonly System.Collections.Generic.HashSet<string> AlwaysDrawContent =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        "Expedition", "Abyss", "Delirium", "Incursion", "Ritual",
    };

    // Draws per-content-type icon PNGs (icon-breach.png etc.) in a horizontal row near the node.
    private void DrawContentIcons(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!ContentIconsEnabled || loadedContentIcons.Count == 0)
                return;
            if (cachedNode.IsVisited && !cachedNode.IsAttempted)
                return;
            if (!cachedNode.MapType.Highlight)
                return;

            // Zoom factor: screen-px-per-art-unit normalized so it's 1.0 at full zoom (the size/offset
            // the settings are tuned at), shrinking as the atlas zooms out. ArtWidth is zoom-independent,
            // so normal (40) and large (65) maps yield the same factor at a given zoom.
            float artW = cachedNode.ArtWidth > 1f ? cachedNode.ArtWidth : 40f;
            float mag = nodeCurrentPosition.Width / artW;
            if (mag > maxNodeZoomMagnification) maxNodeZoomMagnification = mag;
            float zoom = maxNodeZoomMagnification > 0.0001f ? mag / maxNodeZoomMagnification : 1f;

            float size = Settings.Graphics.ContentIconSize * zoom;
            float h = size * (1f - Settings.Graphics.ContentIconFlatten);
            float spacing = Settings.Graphics.ContentIconSpacing * zoom;
            Color tint = Settings.Graphics.ContentIconTint;
            var fullUV = new RectangleF(0, 0, 1, 1);
            bool blankLoaded = loadedContentIcons.Contains(BlankContentIcon);

            var icons = new List<(string file, string content, Color tint)>();

            // Adds an icon for a content name (dedup-aware): the matching icon-<content>.png, or the
            // blank icon (user-tinted) when there's no file for it.
            void AddIcon(string contentName) {
                var iconName = ResolveContentIconFile(contentName);
                if (iconName != null) {
                    // Known content: dedup by icon file (boss variants share one icon).
                    if (!icons.Any(x => x.file == iconName))
                        icons.Add((iconName, contentName, tint));
                } else if (blankLoaded) {
                    // Unknown content (no matching icon-<content>.png): blank icon in the user color.
                    // Dedup by content name so distinct unknowns each show, but not duplicated.
                    if (!icons.Any(x => x.file == BlankContentIcon && x.content == contentName))
                        icons.Add((BlankContentIcon, contentName, Settings.Graphics.UnknownContentColor));
                }
            }

            bool visGateFails =
                (!Settings.Maps.Content.ShowRingsOnLockedNodes && !cachedNode.IsUnlocked) ||
                (!Settings.Maps.Content.ShowRingsOnUnlockedNodes && cachedNode.IsUnlocked) ||
                (!Settings.Maps.Content.ShowRingsOnHiddenNodes && !cachedNode.IsVisible);

            foreach (var (contentName, content) in cachedNode.Content) {
                if (visGateFails || !content.Highlight)
                    continue;

                // The game draws its own icon for most content with an AtlasIcon, but only on visible
                // nodes - fogged nodes show no in-game content. So skip ours only when the node is visible
                // and the game already shows that content (avoid a duplicate); always draw on fogged nodes.
                // League mechanics (AlwaysDrawContent) are exempt: the game never draws an icon for them.
                if (Settings.Graphics.ContentIconsSkipGameDrawn && cachedNode.IsVisible
                    && !string.IsNullOrEmpty(content.AtlasIcon)
                    && !AlwaysDrawContent.Contains(contentName))
                    continue;

                AddIcon(contentName);
            }

            // League atlas-tree content (Breach/Abyss/Incursion/Delirium/Ritual) is detected via the
            // atlas passive, not ContentIdentity, so it isn't in node.Content. Draw its icon from
            // AtlasPointType - the game never draws an in-game icon for these.
            if (!visGateFails && !string.IsNullOrEmpty(cachedNode.AtlasPointType))
                AddIcon(cachedNode.AtlasPointType);

            if (icons.Count == 0)
                return;

            // Anchor the row above the node's in-game icon cluster (Element[0][0]) so our icons sit above
            // the game's own content icons; fall back to the node center when the child rect isn't readable.
            // Non-visible (fogged) nodes don't show in-game content icons, so don't anchor above them -
            // that would offset our row too high. Anchor to the in-game icons only on visible nodes.
            float anchorTop = nodeCurrentPosition.Center.Y;
            if (Settings.Graphics.ContentIconsAboveGameIcons && cachedNode.IsVisible) {
                try {
                    var host = cachedNode.MapNode?.Element?.GetChildAtIndex(0)?.GetChildAtIndex(0);
                    if (host != null) {
                        var hostRect = host.GetClientRect();
                        if (hostRect.Width > 0 && hostRect.Height > 0)
                            anchorTop = hostRect.Top;
                    }
                } catch (Exception e) {
                    DebugSwallow("DrawContentIcons: anchor rect", e);
                }
            }

            float totalWidth = icons.Count * size + (icons.Count - 1) * spacing;
            float startX = nodeCurrentPosition.Center.X - totalWidth / 2f;
            float centerY = anchorTop + Settings.Graphics.ContentIconOffsetY * zoom;

            // Record the row's top Y so the atlas-point/quest indicators can sit above it.
            contentRowTopByCoord[cachedNode.Coordinates] = centerY - h / 2f;

            for (int i = 0; i < icons.Count; i++) {
                float x = startX + i * (size + spacing);
                var iconRect = new RectangleF(x, centerY - h / 2f, size, h);
                Graphics.DrawImage(icons[i].file, iconRect, fullUV, icons[i].tint);
                contentIconRects.Add((iconRect, icons[i].content));
            }
        } catch (Exception e) {
            LogError("Error drawing content icons: " + e.Message);
        }
    }

    // Shows the content name as a tooltip when the cursor is over a content icon drawn this frame.
    // contentIconRects is populated by DrawContentIcons; checked topmost-last so stacked icons resolve
    // to the one drawn last. Drawn near the cursor, after all other passes.
    private void DrawContentIconTooltip()
    {
        if (!ContentIconsEnabled || contentIconRects.Count == 0)
            return;

        try {
            Vector2 cursor = ImGuiNET.ImGui.GetMousePos();
            string content = null;
            for (int i = contentIconRects.Count - 1; i >= 0; i--) {
                var r = contentIconRects[i].rect;
                if (cursor.X >= r.Left && cursor.X <= r.Right && cursor.Y >= r.Top && cursor.Y <= r.Bottom) {
                    content = contentIconRects[i].content;
                    break;
                }
            }
            if (content == null)
                return;

            Vector2 pos = cursor + new Vector2(16, 16);
            using (Graphics.SetTextScale(1.0f))
                DrawCenteredTextWithBorder(content, pos + new Vector2(Graphics.MeasureText(content).X / 2f, 0),
                    Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor,
                    Settings.Graphics.ContentIconTint, 10, 6);
        } catch (Exception e) {
            DebugSwallow("DrawContentIconTooltip", e);
        }
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

    // Centered text on a background plate with a colored border (border drawn as an outer box, the
    // background inset 2px on top). Measured under the current text scale, like its plain sibling.
    private void DrawCenteredTextWithBorder(string text, Vector2 position, Color color, Color backgroundColor, Color borderColor, int xPadding, int yPadding)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text) + new Vector2(xPadding, yPadding);
        var topLeft = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(topLeft, topLeft + boxSize, borderColor, 5.0f);
        Graphics.DrawBox(topLeft + new Vector2(2, 2), topLeft + boxSize - new Vector2(2, 2), backgroundColor, 4.0f);
        Graphics.DrawText(text, topLeft + new Vector2(xPadding / 2f, yPadding / 2f), color);
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
