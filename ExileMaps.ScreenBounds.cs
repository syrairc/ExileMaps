using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore2;
using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Cohen–Sutherland clip of a segment to the cached on-screen rect. Returns false if the segment
    // lies entirely outside the screen; on true, clippedStart/clippedEnd are the visible portion.
    private bool ClipLineToScreen(Vector2 a, Vector2 b, out Vector2 clippedStart, out Vector2 clippedEnd)
    {
        RectangleF r = cachedScreenRect;
        float xMin = r.Left, xMax = r.Right, yMin = r.Top, yMax = r.Bottom;

        int Code(Vector2 p) {
            int c = 0;
            if (p.X < xMin) c |= 1; else if (p.X > xMax) c |= 2;
            if (p.Y < yMin) c |= 4; else if (p.Y > yMax) c |= 8;
            return c;
        }

        Vector2 p0 = a, p1 = b;
        int c0 = Code(p0), c1 = Code(p1);
        clippedStart = a;
        clippedEnd = b;

        while (true) {
            if ((c0 | c1) == 0) { clippedStart = p0; clippedEnd = p1; return true; } // both inside
            if ((c0 & c1) != 0) return false;                                        // both off the same side

            int co = c0 != 0 ? c0 : c1;
            Vector2 p;
            if ((co & 8) != 0)      p = new Vector2(p0.X + (p1.X - p0.X) * (yMax - p0.Y) / (p1.Y - p0.Y), yMax);
            else if ((co & 4) != 0) p = new Vector2(p0.X + (p1.X - p0.X) * (yMin - p0.Y) / (p1.Y - p0.Y), yMin);
            else if ((co & 2) != 0) p = new Vector2(xMax, p0.Y + (p1.Y - p0.Y) * (xMax - p0.X) / (p1.X - p0.X));
            else                    p = new Vector2(xMin, p0.Y + (p1.Y - p0.Y) * (xMin - p0.X) / (p1.X - p0.X));

            if (co == c0) { p0 = p; c0 = Code(p0); }
            else          { p1 = p; c1 = Code(p1); }
        }
    }

    // Map tooltip is a WorldMap child identified by its popup texture; checked in IsOnScreen.
    // Matches any AtlasScreen "*Popup*" texture (e.g. AtlasMapNodePopup / AtlasMapNodePopupSelected).
    private const string TooltipTexturePrefix = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/";
    private static bool IsTooltipTexture(string textureName) =>
        textureName != null && textureName.StartsWith(TooltipTexturePrefix) && textureName.Contains("Popup");

    // Static atlas UI textures we never want to draw the overlay over (title bar, search box bg).
    // Matched by exact texture name anywhere under the WorldMap element tree.
    private static readonly HashSet<string> ExcludeTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/WorldMap/WorldmapTitleBar.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/Common/AtlasSearchBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapPinsWindow/MapPinLegendBG.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapLegend/LegendBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/KeystonesUI/MainBgExpanded.dds",
    };

    // Recursively adds the client rect of any visible descendant whose texture is in
    // ExcludeTextureNames. Depth-limited so a malformed/deep tree can't stall the frame.
    private void AddExcludeRectsByTexture(ExileCore2.PoEMemory.Element element, int depth)
    {
        if (element == null || depth > 8 || !element.IsVisible)
            return;

        if (element.TextureName != null && ExcludeTextureNames.Contains(element.TextureName))
            cachedExcludeRects.Add(element.GetClientRect());

        foreach (var child in element.Children)
            AddExcludeRectsByTexture(child, depth + 1);
    }

    // Recomputes the on-screen rect and any visible map-tooltip rects once per render frame.
    // IsOnScreen then reads these cached values instead of re-reading panel/tooltip game memory
    // on every call (it's called dozens of times per node per frame).
    private void UpdateScreenBounds()
    {
        try {
            var size = GameController.Window.GetWindowRectangleTimeCache.Size;
            float left = 0;
            float right = size.X;

            if (UI.OpenRightPanel.IsVisible)
                right -= UI.OpenRightPanel.GetClientRect().Width;

            if (UI.OpenLeftPanel.IsVisible || WaypointPanelIsOpen)
                left += Math.Max(UI.OpenLeftPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Width);

            cachedScreenRect = new RectangleF(left, 0, right - left, size.Y);

            // Don't render over the map tooltip. Its child index varies, so identify it
            // by its popup texture (selected/unselected) instead of a fixed position.
            cachedExcludeRects.Clear();
            foreach (var tooltip in UI.WorldMap.Children) {
                if (tooltip == null || !tooltip.IsVisible)
                    continue;
                if (!IsTooltipTexture(tooltip.TextureName))
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedExcludeRects.Add(mapTooltip);
            }

            // Don't render over the atlas title bar / search box background.
            AddExcludeRectsByTexture(UI.WorldMap, 0);

            // Don't render over the fixed HUD elements (life/mana orbs, flask panel, skill bar).
            AddExcludeRect(UI.GameUI?.LifeOrb);
            AddExcludeRect(UI.GameUI?.ManaOrb);
            AddExcludeRect(UI.GameUI?.FlaskPanel?.Parent);
            AddExcludeRect(UI.SkillBar?.Parent);
        } catch (Exception e) {
            // Keep last good bounds on a failed memory read rather than blanking the overlay.
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

    // Adds a visible element's client rect to the no-draw set. Null/invisible elements are skipped.
    private void AddExcludeRect(ExileCore2.PoEMemory.Element element)
    {
        if (element == null || !element.IsVisible)
            return;
        cachedExcludeRects.Add(element.GetClientRect());
    }

    private bool IsOnScreen(Vector2 position)
    {
        foreach (var tooltip in cachedExcludeRects)
            if (tooltip.Contains(position))
                return false;

        return cachedScreenRect.Contains(position);
    }

    // A line is drawable only if both endpoints are on screen AND the segment doesn't
    // pass through a tooltip rect (a line can clear both endpoint checks yet still cross
    // the tooltip between them).
    private bool IsLineVisible(Vector2 start, Vector2 end)
    {
        if (!IsOnScreen(start) || !IsOnScreen(end))
            return false;

        foreach (var tooltip in cachedExcludeRects)
            if (SegmentIntersectsRect(start, end, tooltip))
                return false;

        return true;
    }

    // Segment vs axis-aligned rect. Endpoints are already known outside the rect (IsOnScreen
    // rejects points inside a tooltip), so test the segment against the four edges.
    private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, RectangleF rect)
    {
        Vector2 tl = new(rect.Left, rect.Top);
        Vector2 tr = new(rect.Right, rect.Top);
        Vector2 br = new(rect.Right, rect.Bottom);
        Vector2 bl = new(rect.Left, rect.Bottom);

        return SegmentsIntersect(a, b, tl, tr) ||
               SegmentsIntersect(a, b, tr, br) ||
               SegmentsIntersect(a, b, br, bl) ||
               SegmentsIntersect(a, b, bl, tl);
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        float d1 = Cross(p3, p4, p1);
        float d2 = Cross(p3, p4, p2);
        float d3 = Cross(p1, p2, p3);
        float d4 = Cross(p1, p2, p4);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    // Cross product of (b - a) x (c - a); sign tells which side of line ab point c is on.
    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    public float GetDistanceToNode(Node cachedNode)
    {
        return Vector2.Distance(screenCenter, cachedNode.MapNode.Element.GetClientRect().Center);
    }
}
