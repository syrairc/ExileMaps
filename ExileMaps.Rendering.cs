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
    //   3. DrawNodeLabels - name, weight
    // Waypoints draw on top. Global z-order prevents lines drawing over fills/labels.
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
            DrawContentRow(node, rect);          // content-type icon row
            DrawSpecialIndicator(node, rect);   // special-map icon
            using (Graphics.SetTextScale(1.0f)) // 1.0 for the normal name; special name self-scales
                DrawMapName(node, rect);        // normal map name (skips special maps)
            DrawSpecialMapName(node, rect);     // special-map name
        } catch (Exception e) {
            DebugSwallow("Render: hovered node over tooltip", e);
        }
    }

    // one node -> one category, its toggle decides. completed > accessible > inaccessible > hidden.
    private bool ConnectionCategoryEnabled(Node node)
    {
        if (node.IsVisited || node.IsCompleted)
            return Settings.Graphics.ShowConnectionsForCompleted;
        if (node.IsUnlocked)
            return Settings.Graphics.ShowConnectionsForAccessible;
        if (node.IsVisible)
            return Settings.Graphics.ShowConnectionsForInaccessible;
        return Settings.Graphics.ShowConnectionsForHidden;
    }

    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Graphics.ShowConnectionLines)
            return;

        // Source category is the same for every neighbor, so resolve it once.
        bool srcEnabled = ConnectionCategoryEnabled(cachedNode);

        foreach (Vector2i coordinates in cachedNode.GetNeighborCoordinates())
        {
            if (coordinates == default)
                continue;

            if (!mapCache.TryGetValue(coordinates, out Node destinationNode))
                continue;

            // OR-any draw rule: an edge draws if either endpoint falls in an enabled category.
            // Each node lands in exactly one: completed > accessible > inaccessible > hidden.
            if (!srcEnabled && !ConnectionCategoryEnabled(destinationNode))
                continue;

            // Reuse the neighbor's rect from the per-frame memo if it was already read this frame
            // (e.g. it's an on-screen selected node); otherwise this reads + caches it once.
            var destinationPos = GetNodeRect(destinationNode);

            if (!IsLineVisible(nodeCurrentPosition.Center, destinationPos.Center))
                continue;

            if (Settings.Graphics.DrawGradientLines) {
                Color sourceColor = cachedNode.IsVisited ? Settings.Graphics.VisitedLineColor : cachedNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                Color destinationColor = destinationNode.IsVisited ? Settings.Graphics.VisitedLineColor : destinationNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;

                sourceColor = ApplyLineOpacity(sourceColor);
                destinationColor = ApplyLineOpacity(destinationColor);

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

                color = ApplyLineOpacity(color);
                Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, color);
            }
        }
    }

    // Scales a line color's alpha by the global ConnectionLineOpacity (0-255). Preserves the
    // per-state alpha differences (visited 196 / unlocked 170 / locked 51) while dimming everything.
    private Color ApplyLineOpacity(Color c)
    {
        int a = (int)Math.Round(c.A * Settings.Graphics.ConnectionLineOpacity.Value / 255f);
        return Color.FromArgb(Math.Clamp(a, 0, 255), c.R, c.G, c.B);
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

    // Fixed gold + size for the atlas-point badge drawn at the bottom of a content icon.
    private static readonly Color AtlasBadgeColor = Color.FromArgb(255, 255, 200, 40);
    private const float AtlasBadgeSize = 10f;

    // The Y above which the atlas-point/quest indicators should sit so they clear any content icons on
    // the node: our drawn content row (recorded this frame) or, on visible nodes, the game's own content
    // icon cluster. Defaults to the node's top edge when there are no content icons.
    private float IndicatorBaseTop(Node node, RectangleF rect)
    {
        float top = rect.Center.Y - rect.Height / 2f;

        if (contentRowTopByCoord.TryGetValue(node.Coordinates, out var rowTop)) {
            top = Math.Min(top, rowTop);
        } else if (node.IsVisible && NodeHasGameContentIcon(node)) {
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

    // True if any content on the node carries a game-drawn atlas icon. Plain loop, no LINQ closure.
    private static bool NodeHasGameContentIcon(Node node)
    {
        foreach (var c in node.Content.Values)
            if (!string.IsNullOrEmpty(c.AtlasIcon))
                return true;
        return false;
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

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);
            float favSize = Settings.Graphics.FavoriteIconSize * zoom;

            RectangleF iconRect;
            if (biomeIconRectByCoord.TryGetValue(cachedNode.Coordinates, out var biomeRect)) {
                // sit left of the biome icon, centered on it
                float gap = biomeRect.Height * 0.12f;
                iconRect = new RectangleF(biomeRect.X - gap - favSize, biomeRect.Center.Y - favSize / 2f, favSize, favSize);
            } else {
                // no biome icon: take the biome slot (left of the name box)
                iconRect = BiomeSlotRect(cachedNode, nodeCurrentPosition, favSize, zoom);
            }
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
        if (!Settings.Graphics.DrawWeightOnMap ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        float norm = (maxMapWeight - minMapWeight) > 0
            ? (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight)
            : 0.5f;
        norm = Math.Clamp(norm, 0f, 1f);
        Color wc = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, norm);

        // Sit the value just right of the name (or the node if names are off).
        float offsetX = Settings.Maps.ShowMapNames ? (Graphics.MeasureText(MapLabelText(cachedNode)).X / 2) + 20 : 40;
        var text = $"{cachedNode.Weight:0}";
        Vector2 pos = new(nodeCurrentPosition.Center.X + offsetX + Settings.Graphics.MapNameOffsetX,
                          nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY);
        DrawCenteredTextWithBackground(text, new Vector2(pos.X + Graphics.MeasureText(text).X / 2f, pos.Y),
            wc, Settings.Graphics.BackgroundColor, true, 8, 3);
    }
    // 8 neighbor offsets for a 1px text stroke (ported from ExileNameplates DataTextRenderer).
    private static readonly Vector2[] LabelStrokeOffsets =
    {
        new(-1, -1), new(0, -1), new(1, -1),
        new(-1,  0),             new(1,  0),
        new(-1,  1), new(0,  1), new(1,  1),
    };

    // Map name as drawn: uppercased or the map's own casing, per the Uppercase Map Names setting.
    private string MapLabelText(Node node)
        => Settings.Graphics.UppercaseMapNames ? node.UppercaseName : node.Name;

    // Reused by ResolveLabelStyle so the per-node-per-frame label pass allocates nothing. Render-thread only.
    private readonly LabelStyle _scratchLabelStyle = new();

    // Composes the final label look for a node: Base -> Biome -> Favorite -> Content -> Special,
    // higher layer wins each contested property. Multi-content/biome picks the highest-weight entry.
    // (Map layer between Favorite and Biome is future - no per-map override yet.)
    private LabelStyle ResolveLabelStyle(Node node)
    {
        var s = _scratchLabelStyle;
        s.CopyFrom(Settings.Labels.Base);

        var biomeOv = HighestWeightBiomeOverride(node);
        biomeOv?.ApplyTo(s);

        if (node.IsFavorited)
            Settings.Labels.Favorite.ApplyTo(s);

        var contentOv = HighestWeightContentOverride(node);
        contentOv?.ApplyTo(s);

        if (node.IsSpecial)
            Settings.Labels.Special.ApplyTo(s);

        ApplyWeightTints(s, node);
        return s;
    }

    // Highest-weight content type on the node that has an override entry, or null.
    private LabelStyleOverride HighestWeightContentOverride(Node node)
    {
        var overrides = Settings.Labels.Content;
        if (overrides.Count == 0 || node.Content.Count == 0)
            return null;
        LabelStyleOverride best = null;
        float bestW = float.NegativeInfinity;
        foreach (var c in node.Content.Values) {
            if (c?.Id != null && overrides.TryGetValue(c.Id, out var ov) && c.Weight > bestW) {
                bestW = c.Weight;
                best = ov;
            }
        }
        return best;
    }

    // Highest-weight biome on the node that has an override entry, or null.
    private LabelStyleOverride HighestWeightBiomeOverride(Node node)
    {
        var overrides = Settings.Labels.Biome;
        if (overrides.Count == 0 || node.Biomes.Count == 0)
            return null;
        LabelStyleOverride best = null;
        float bestW = float.NegativeInfinity;
        foreach (var b in node.Biomes.Values) {
            if (b?.Name != null && overrides.TryGetValue(b.Name, out var ov) && b.Weight > bestW) {
                bestW = b.Weight;
                best = ov;
            }
        }
        return best;
    }

    // Resolves any by-weight colors on the style against the node's weight (Bad->Good gradient),
    // leaving opacity untouched so a weight-tinted plate can still be semi-transparent.
    private void ApplyWeightTints(LabelStyle s, Node node)
    {
        if (!s.TextColorByWeight && !s.BoxColorByWeight && !s.BorderColorByWeight)
            return;
        float denom = maxMapWeight - minMapWeight;
        float w = denom > 0.0001f ? Math.Clamp((node.Weight - minMapWeight) / denom, 0f, 1f) : 0.5f;
        var wc = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, w);
        // keep each channel's own alpha - box/border opacity lives in the color alpha now.
        if (s.TextColorByWeight) s.TextColor = Color.FromArgb(s.TextColor.A, wc.R, wc.G, wc.B);
        if (s.BoxColorByWeight) s.BoxColor = Color.FromArgb(s.BoxColor.A, wc.R, wc.G, wc.B);
        if (s.BorderColorByWeight) s.BorderColor = Color.FromArgb(s.BorderColor.A, wc.R, wc.G, wc.B);
    }

    // Draws the map name above the node on the atlas.
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        // Special maps are named by DrawSpecialMapName (own pass, drawn in any state). Skip here.
        if (cachedNode.IsSpecial)
            return;

        if (!Settings.Maps.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);
        var style = ResolveLabelStyle(cachedNode);
        DrawStyledLabel(MapLabelText(cachedNode), namePosition, style);
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

        // Special layer already applied by ResolveLabelStyle (node.IsSpecial).
        var style = ResolveLabelStyle(cachedNode);

        // User-added specials key off their configured color; fade/user tweak the resolved look.
        Color baseCol = entry?.Color ?? style.BorderColor;
        if (fade) {
            Color washed = Desaturate(baseCol, 0.85f);
            style.TextColor = Color.FromArgb(255, washed.R, washed.G, washed.B);
            style.BorderColor = washed;
            style.BorderOpacity = 115;
            style.BoxColor = Color.FromArgb(0, 0, 0);
            style.BoxOpacity = 255;
            style.BoxVisible = true;
            style.TextScale = 1.15f;
        } else if (userAdded) {
            Color washed = Desaturate(baseCol, 0.55f);
            style.TextColor = Color.FromArgb(255, washed.R, washed.G, washed.B);
            style.BorderColor = washed;
        }

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);
        DrawStyledLabel(MapLabelText(cachedNode), namePosition, style);
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
    // table first, then the literal lowercase+space-stripped form. Memoized (contentIconFileCache):
    // loadedContentIcons is init-only, so a name always resolves to the same file. Render-thread only.
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

    // Biome-name -> biome-*.png alias table (for names whose file base isn't just lowercase+stripped).
    private static readonly System.Collections.Generic.Dictionary<string, string> BiomeIconAliases =
        new(System.StringComparer.OrdinalIgnoreCase)
    {
        // city biome ids don't match their file base 1:1
        { "EzomyteCity", "ezomyte" },
        { "FaridunCity", "faridun" },
        { "VaalCity", "vaal" },
        { "BreachCity", "breach" },
        { "OriathCity", "oriath" },
    };

    // Resolves a biome name to a loaded biome-<x>.png, or null. Alias table first, then literal
    // lowercase+space-stripped form. Memoized; loadedBiomeIcons is init-only.
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


    // Draws the node's content-type icons in a horizontal row centered below the map name, expanding
    // symmetrically. Draws ALL highlighted content types (no game-drawn dedup, no blank fallback);
    // unknown content with no icon is skipped. Badges content that awards an atlas point.
    private void DrawContentRow(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowContentRow || loadedContentIcons.Count == 0)
                return;
            if (cachedNode.IsVisited && !cachedNode.IsAttempted)
                return;
            if (!cachedNode.MapType.Highlight)
                return;

            // Zoom factor: screen-px-per-art-unit normalized to 1.0 at full zoom (see the old row).
            float artW = cachedNode.ArtWidth > 1f ? cachedNode.ArtWidth : 40f;
            float mag = nodeCurrentPosition.Width / artW;
            if (mag > maxNodeZoomMagnification) maxNodeZoomMagnification = mag;
            float zoom = maxNodeZoomMagnification > 0.0001f ? mag / maxNodeZoomMagnification : 1f;

            float size = Settings.Graphics.ContentIconSize * zoom;
            float spacing = Settings.Graphics.ContentIconSpacing * zoom;
            Color tint = Color.White;
            var fullUV = new RectangleF(0, 0, 1, 1);

            // Collect distinct icon files (dedup by file so boss variants share one). content name kept
            // for tooltip + atlas-point badge match.
            var icons = contentIconScratch;
            icons.Clear();

            void AddIcon(string contentName) {
                var iconName = ResolveContentIconFile(contentName);
                if (iconName == null)
                    return;
                for (int j = 0; j < icons.Count; j++)
                    if (icons[j].file == iconName)
                        return;
                icons.Add((iconName, contentName, tint));
            }

            foreach (var (contentName, content) in cachedNode.Content) {
                // game draws its own icon on visible nodes for content with an atlas icon (map boss,
                // expedition, unique map...). skip ours to avoid a dup. fogged nodes show no in-game
                // content, so we still draw there.
                if (Settings.Graphics.ContentIconsSkipGameDrawn && cachedNode.IsVisible
                    && !string.IsNullOrEmpty(content.AtlasIcon))
                    continue;
                AddIcon(contentName);
            }

            // League atlas-tree content (Breach/Abyss/Incursion/Delirium/Ritual) isn't in node.Content;
            // it comes off AtlasPointType. The game never draws an icon for it, so always add it.
            if (!string.IsNullOrEmpty(cachedNode.AtlasPointType))
                AddIcon(cachedNode.AtlasPointType);

            if (icons.Count == 0)
                return;

            // Row centered on the node, anchored below the name (tracks MapNameOffsetY so it follows the
            // name). ContentRowOffsetY is zoom-scaled; MapNameOffsetY is not, matching the name itself.
            float totalWidth = icons.Count * size + (icons.Count - 1) * spacing;
            float startX = nodeCurrentPosition.Center.X - totalWidth / 2f;
            float centerY = nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY
                          + Settings.Graphics.ContentRowOffsetY * zoom;

            // Record the row's top Y so the atlas-point/quest indicators clear it vertically.
            contentRowTopByCoord[cachedNode.Coordinates] = centerY - size / 2f;

            bool badge = Settings.Graphics.ShowAtlasPointBadge && cachedNode.GivesAtlasPoint
                         && !string.IsNullOrEmpty(cachedNode.AtlasPointType);
            float badgeSize = AtlasBadgeSize * zoom;

            for (int i = 0; i < icons.Count; i++) {
                float x = startX + i * (size + spacing);
                var iconRect = new RectangleF(x, centerY - size / 2f, size, size);
                Graphics.DrawImage(icons[i].file, iconRect, fullUV, icons[i].tint);

                string note = (cachedNode.GivesAtlasPoint
                    && string.Equals(icons[i].content, cachedNode.AtlasPointType, StringComparison.OrdinalIgnoreCase))
                    ? "Awards an atlas point" : null;
                contentIconRects.Add((iconRect, icons[i].content, note));

                // Gold star badge centered on the icon's bottom edge for the content that grants the point.
                if (badge && customIconsLoaded
                    && string.Equals(icons[i].content, cachedNode.AtlasPointType, StringComparison.OrdinalIgnoreCase)) {
                    var badgeCenter = new Vector2(iconRect.Center.X, iconRect.Bottom);
                    DrawNodeSprite(badgeCenter, badgeSize, badgeSize, SpriteIcon.Star8,
                        AtlasBadgeColor, allowFlatten: false);
                }
            }
        } catch (Exception e) {
            LogError("Error drawing content row: " + e.Message);
        }
    }

    // Draws one biome icon (highest-weight biome on the node that has a loaded icon) to the left of the
    // map name, with a small gap. Gated the same way as the content row.
    // Per-node zoom factor (also feeds the running maxNodeZoomMagnification used to normalize sizes).
    private float NodeZoom(Node node, RectangleF nodePos)
    {
        float artW = node.ArtWidth > 1f ? node.ArtWidth : 40f;
        float mag = nodePos.Width / artW;
        if (mag > maxNodeZoomMagnification) maxNodeZoomMagnification = mag;
        return maxNodeZoomMagnification > 0.0001f ? mag / maxNodeZoomMagnification : 1f;
    }

    // Rect for an icon in the "biome slot": left of the name box, centered on the name row. Measure the
    // name at its own label scale so we line up with the rendered box (MeasureText + 10 matches
    // DrawStyledLabel's box width), then a gap so it isn't flush against the border.
    private RectangleF BiomeSlotRect(Node node, RectangleF nodePos, float size, float zoom)
    {
        var style = ResolveLabelStyle(node);
        float nameHalf;
        using (Graphics.SetTextScale(style.TextScale))
            nameHalf = Settings.Maps.ShowMapNames
                ? (Graphics.MeasureText(MapLabelText(node)).X + 10f) / 2f
                : 20f;
        float gap = size * 0.28f;
        float boxLeft = nodePos.Center.X + Settings.Graphics.MapNameOffsetX - nameHalf;
        float x = boxLeft - gap - size;
        float centerY = nodePos.Center.Y + Settings.Graphics.MapNameOffsetY
                      + Settings.Graphics.BiomeIconOffsetY * zoom;
        return new RectangleF(x, centerY - size / 2f, size, size);
    }

    private void DrawBiomeIcon(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowBiomeIcon || loadedBiomeIcons.Count == 0)
                return;
            if (cachedNode.IsVisited && !cachedNode.IsAttempted)
                return;
            if (!cachedNode.MapType.Highlight || cachedNode.Biomes.Count == 0)
                return;

            // Highest-weight biome that resolves to a loaded icon.
            string bestName = null, bestFile = null;
            float bestW = float.NegativeInfinity;
            foreach (var b in cachedNode.Biomes.Values) {
                if (b?.Name == null || b.Weight <= bestW)
                    continue;
                var file = ResolveBiomeIconFile(b.Name);
                if (file == null)
                    continue;
                bestW = b.Weight;
                bestName = b.Name;
                bestFile = file;
            }
            if (bestFile == null)
                return;

            float zoom = NodeZoom(cachedNode, nodeCurrentPosition);
            float size = Settings.Graphics.BiomeIconSize * zoom;
            var rect = BiomeSlotRect(cachedNode, nodeCurrentPosition, size, zoom);
            Graphics.DrawImage(bestFile, rect, new RectangleF(0, 0, 1, 1), Color.White);

            biomeIconRectByCoord[cachedNode.Coordinates] = rect;
            contentIconRects.Add((rect, bestName, null));
        } catch (Exception e) {
            LogError("Error drawing biome icon: " + e.Message);
        }
    }

    // Shows a tooltip for the content/biome icon under the cursor (topmost-last so stacked icons resolve
    // to the last drawn). contentIconRects is populated by DrawContentRow / DrawBiomeIcon. Drawn last.
    private void DrawIconTooltips()
    {
        if (contentIconRects.Count == 0)
            return;

        try {
            Vector2 cursor = ImGuiNET.ImGui.GetMousePos();
            string title = null, note = null;
            for (int i = contentIconRects.Count - 1; i >= 0; i--) {
                var r = contentIconRects[i].rect;
                if (cursor.X >= r.Left && cursor.X <= r.Right && cursor.Y >= r.Top && cursor.Y <= r.Bottom) {
                    title = contentIconRects[i].title;
                    note = contentIconRects[i].note;
                    break;
                }
            }
            if (title == null)
                return;

            Vector2 pos = cursor + new Vector2(16, 16);
            using (Graphics.SetTextScale(1.0f)) {
                DrawCenteredTextWithBorder(title, pos + new Vector2(Graphics.MeasureText(title).X / 2f, 0),
                    Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor,
                    Color.White, 10, 6);
                if (!string.IsNullOrEmpty(note)) {
                    Vector2 notePos = pos + new Vector2(0, Graphics.MeasureText(title).Y + 4);
                    DrawCenteredTextWithBorder(note, notePos + new Vector2(Graphics.MeasureText(note).X / 2f, 0),
                        AtlasBadgeColor, Settings.Graphics.BackgroundColor,
                        Color.White, 10, 6);
                }
            }
        } catch (Exception e) {
            DebugSwallow("DrawIconTooltips", e);
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

    // Draws a centered map label from a resolved style: optional border box, optional background box
    // (inset by border thickness), optional 1px stroke, then the text. Padding matches the old label.
    private void DrawStyledLabel(string text, Vector2 position, LabelStyle style)
    {
        if (string.IsNullOrEmpty(text) || !IsOnScreen(position))
            return;

        using (Graphics.SetTextScale(style.TextScale)) {
            var boxSize = Graphics.MeasureText(text) + new Vector2(10, 4);
            var topLeft = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

            // Border sits on the box: no box, no border. Fill the box first, then draw the border as a
            // frame outline on top - filling the whole border rect would show through a translucent box.
            bool drawBorder = style.BorderVisible && style.BoxVisible;
            if (style.BoxVisible) {
                Graphics.DrawBox(topLeft, topLeft + boxSize, style.BoxColor, 5.0f);
            }
            if (drawBorder) {
                Graphics.DrawFrame(topLeft, topLeft + boxSize, style.BorderColor, 5.0f, style.BorderThickness, 0);
            }

            var textPos = topLeft + new Vector2(5f, 2f);
            if (style.StrokeEnabled) {
                var sc = Color.FromArgb(255, style.StrokeColor.R, style.StrokeColor.G, style.StrokeColor.B);
                foreach (var o in LabelStrokeOffsets)
                    Graphics.DrawText(text, textPos + o, sc);
            }
            var textCol = Color.FromArgb(255, style.TextColor.R, style.TextColor.G, style.TextColor.B);
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
