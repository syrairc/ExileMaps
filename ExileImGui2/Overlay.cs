using System;
using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Drag and resize for foreground-drawlist panels - overlays drawn outside any ImGui window.
/// <para>
/// One Overlay per plugin tracks which panel is being dragged or resized, so several panels coexist
/// without stealing each other's drag. Set <see cref="Locked"/> (and optionally <see cref="MoveHeld"/>
/// and <see cref="InteractHeld"/> from your own held keys) each frame; while <see cref="Movable"/>,
/// <see cref="Handle"/> moves the panel, resizes the enabled edges, and writes back into your
/// position and size settings fields.
/// </para>
/// </summary>
public sealed class Overlay
{
    /// <summary>Set each frame. A locked overlay ignores drags unless <see cref="MoveHeld"/> is set.</summary>
    public bool Locked;

    /// <summary>
    /// Set each frame from a held move-key (Alt, say) to make a locked overlay grabbable. The caller
    /// reads the key so the binding is theirs - it is usually a settings field, and need not be a
    /// modifier at all.
    /// </summary>
    public bool MoveHeld;

    /// <summary>
    /// Set each frame from a held interact-key (Ctrl, say) to arm row clicks. Same caller-owned
    /// binding as <see cref="MoveHeld"/>. Read by the caller's own hit-testing; this class only
    /// stores it.
    /// </summary>
    public bool InteractHeld;

    /// <summary>Whether a drag would be accepted right now.</summary>
    public bool Movable => !Locked || MoveHeld;

    string _dragId, _resizeId;
    ResizeEdge _resizeEdge;     // which edge the active resize latched onto
    Vector2 _dragOffset;
    const float Edge = 6f;   // right-edge grab band for resize

    /// <summary>Which edges of a panel can be dragged to resize it.</summary>
    [Flags]
    public enum ResizeEdge
    {
        /// <summary>No resize.</summary>
        None = 0,
        /// <summary>Right edge, driving width.</summary>
        Right = 1,
        /// <summary>Bottom edge, driving height.</summary>
        Bottom = 2,
        /// <summary>Both at once.</summary>
        Corner = Right | Bottom,
    }

    /// <summary>
    /// Pure hit-test for the resize grips. Corner wins when the mouse is near both right and bottom
    /// AND both are enabled; otherwise whichever single edge is both near and enabled.
    /// </summary>
    /// <param name="enabled">Mask of edges the caller allows.</param>
    /// <param name="edge">Width of the grab band, in pixels.</param>
    public static ResizeEdge HitEdge(Vector2 mouse, Vector2 origin, Vector2 size, ResizeEdge enabled, float edge = Edge)
    {
        bool inside = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                   && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        if (!inside) return ResizeEdge.None;
        bool nearR = mouse.X >= origin.X + size.X - edge;
        bool nearB = mouse.Y >= origin.Y + size.Y - edge;
        bool canR = (enabled & ResizeEdge.Right) != 0;
        bool canB = (enabled & ResizeEdge.Bottom) != 0;
        if (nearR && nearB && canR && canB) return ResizeEdge.Corner;
        if (nearR && canR) return ResizeEdge.Right;
        if (nearB && canB) return ResizeEdge.Bottom;
        return ResizeEdge.None;
    }

    /// <summary>
    /// Pure hit-test: is the mouse over the rect, and is it in the right-edge resize band.
    /// </summary>
    public static (bool hovered, bool onEdge) HitTest(Vector2 mouse, Vector2 origin, Vector2 size, float edge = Edge)
    {
        bool hovered = mouse.X >= origin.X && mouse.X <= origin.X + size.X
                    && mouse.Y >= origin.Y && mouse.Y <= origin.Y + size.Y;
        bool onEdge = hovered && mouse.X >= origin.X + size.X - edge;
        return (hovered, onEdge);
    }

    /// <summary>
    /// Top-left anchored move plus right-edge resize, for a panel that auto-sizes vertically and so
    /// has no height to drag into. Use the longer overload when it does.
    /// </summary>
    public (bool hovered, ResizeEdge edge, bool active) Handle(string id, ref Vector2 origin, Vector2 size,
        ref int posX, ref int posY, ref int width, int minWidth = 60, int maxWidth = 4000)
    {
        int noHeight = 0;
        return Handle(id, ref origin, size, ref posX, ref posY, ref width, minWidth, maxWidth,
            ref noHeight, 0, 0, ResizeEdge.Right);
    }

    /// <summary>
    /// Top-left anchored. Left-drag the body to move; drag an enabled edge to resize - right drives
    /// width, bottom drives height, corner does both, each clamped to the min/max you pass.
    /// </summary>
    /// <param name="id">Names this panel so two panels on one Overlay don't share a drag.</param>
    /// <param name="origin">Panel top-left. Mutated to follow a move.</param>
    /// <param name="size">Current panel size, used for the hit-test.</param>
    /// <param name="posX">Your settings field for X. Written on a move.</param>
    /// <param name="posY">Your settings field for Y. Written on a move.</param>
    /// <param name="width">Your settings field for width. Written on a right/corner resize.</param>
    /// <param name="height">Your settings field for height. Written on a bottom/corner resize.</param>
    /// <param name="resizable">Which edges may be dragged. Mask an edge off and it is inert.</param>
    /// <returns>hovered: mouse is over the panel. edge: which grip to show. active: this panel owns
    /// the live drag or resize.</returns>
    public (bool hovered, ResizeEdge edge, bool active) Handle(string id, ref Vector2 origin, Vector2 size,
        ref int posX, ref int posY, ref int width, int minWidth, int maxWidth,
        ref int height, int minHeight, int maxHeight, ResizeEdge resizable = ResizeEdge.Corner)
    {
        if (!Movable && _dragId != id && _resizeId != id) return (false, ResizeEdge.None, false);

        var enabled = resizable;
        var mouse = ImGui.GetMousePos();
        var (hovered, _) = HitTest(mouse, origin, size);
        var edge = HitEdge(mouse, origin, size, enabled);
        var shown = _resizeId == id ? _resizeEdge : edge;
        if (shown == ResizeEdge.Corner) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNWSE);
        else if (shown == ResizeEdge.Right) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);
        else if (shown == ResizeEdge.Bottom) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);

        if (_dragId == null && _resizeId == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (edge != ResizeEdge.None) { _resizeId = id; _resizeEdge = edge; }
            else { _dragId = id; _dragOffset = mouse - origin; }
        }
        if (_dragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                origin = mouse - _dragOffset;
                posX = (int)Math.Round(origin.X);
                posY = (int)Math.Round(origin.Y);
            }
            else _dragId = null;
        }
        if (_resizeId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                if ((_resizeEdge & ResizeEdge.Right) != 0)
                    width = Math.Clamp((int)Math.Round(mouse.X - origin.X), minWidth, maxWidth);
                if ((_resizeEdge & ResizeEdge.Bottom) != 0)
                    height = Math.Clamp((int)Math.Round(mouse.Y - origin.Y), minHeight, maxHeight);
            }
            else _resizeId = null;
        }
        return (hovered, shown, _dragId == id || _resizeId == id);
    }

    /// <summary>
    /// Centre-anchored variant, for banners and toast stacks whose block is centred. Resize keeps the
    /// width symmetric about the centre.
    /// <para>
    /// Unlike <see cref="Handle"/> this honours <see cref="Locked"/> alone and ignores
    /// <see cref="MoveHeld"/> - a locked centred panel is not grabbable by holding the move key.
    /// </para>
    /// </summary>
    /// <param name="min">Panel top-left. Mutated to follow a move.</param>
    /// <param name="posX">Horizontal CENTRE of the panel, not its left edge.</param>
    /// <param name="posY">Top edge.</param>
    public (bool hovered, bool onEdge, bool active) HandleCenter(string id, ref Vector2 min, Vector2 size,
        ref int posX, ref int posY, ref int width, int minWidth = 60, int maxWidth = 4000)
    {
        if (Locked) { Release(id); return (false, false, false); }

        var mouse = ImGui.GetMousePos();
        var (hovered, onEdge) = HitTest(mouse, min, size);
        if (onEdge || _resizeId == id) ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEW);

        if (_dragId == null && _resizeId == null && hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            if (onEdge) _resizeId = id;
            else { _dragId = id; _dragOffset = mouse - new Vector2(posX, posY); }
        }
        if (_dragId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                var c = mouse - _dragOffset;
                posX = (int)Math.Round(c.X);
                posY = (int)Math.Round(c.Y);
                min = new Vector2(posX - size.X / 2f, posY);
            }
            else _dragId = null;
        }
        if (_resizeId == id)
        {
            if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                width = Math.Clamp((int)Math.Round(2 * (mouse.X - posX)), minWidth, maxWidth);
            else _resizeId = null;
        }
        return (hovered, onEdge, _dragId == id || _resizeId == id);
    }

    /// <summary>
    /// Clears any drag or resize latch held by this panel. Call it for a panel that early-returned
    /// without reaching its <see cref="Handle"/> call, so the latch doesn't outlive the panel.
    /// </summary>
    public void Release(string id)
    {
        if (_dragId == id) _dragId = null;
        if (_resizeId == id) _resizeId = null;
    }

    /// <summary>
    /// Panel chrome: filled background plus outline, in one call. Draw before your content.
    /// </summary>
    /// <param name="background">Fill. A zero alpha skips it.</param>
    /// <param name="thickness">Outline thickness. 0 skips the outline.</param>
    /// <param name="radius">Corner rounding for the outer rect only.</param>
    public static void Chrome(ImDrawListPtr dl, Vector2 min, Vector2 max, SColor background, SColor border,
        float thickness = 1f, float radius = 0f)
    {
        if (background.A > 0) dl.AddRectFilled(min, max, EColor.U32(background), radius);
        if (thickness > 0) dl.AddRect(min, max, EColor.U32(border), radius, ImDrawFlags.None, thickness);
    }

    /// <summary>
    /// An invisible ImGui window over the panel. Being hovered is its only job: that makes ImGui set
    /// WantCaptureMouse, which ExileCore2 honours, so a drag click is swallowed before the game sees
    /// it. Draw before your panel content, while hovered or active.
    /// </summary>
    public static void ClickBlocker(string id, Vector2 pos, Vector2 size)
    {
        ImGui.SetNextWindowPos(pos);
        ImGui.SetNextWindowSize(size);
        ImGui.SetNextWindowBgAlpha(0f);
        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav
            | ImGuiWindowFlags.NoScrollWithMouse;
        ImGui.Begin($"##eidrag_{id}", flags);
        ImGui.End();
    }

    /// <summary>
    /// Outline plus edge grips drawn around a movable panel. Draw after your content.
    /// </summary>
    /// <param name="edge">The edge <see cref="Handle"/> returned, which decides which grips light up.</param>
    public static void DragHint(Vector2 min, Vector2 max, bool active, bool hovered, ResizeEdge edge)
    {
        var hint = (active || hovered) ? SColor.FromArgb(220, 120, 220, 255) : SColor.FromArgb(120, 120, 220, 255);
        var dl = ImGui.GetForegroundDrawList();
        dl.AddRect(min, max, EColor.U32(hint), 0f, ImDrawFlags.None, 1.5f);
        var grip = EColor.U32(SColor.FromArgb(255, 120, 220, 255));
        if ((edge & ResizeEdge.Right) != 0)
            dl.AddLine(new Vector2(max.X, min.Y), new Vector2(max.X, max.Y), grip, 3f);
        if ((edge & ResizeEdge.Bottom) != 0)
            dl.AddLine(new Vector2(min.X, max.Y), new Vector2(max.X, max.Y), grip, 3f);
    }
}
