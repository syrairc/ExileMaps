// a button that lives in its own window and can be dragged anywhere on screen
using System.Collections.Generic;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Free-floating controls: no parent window, positioned by the caller, moved by the user.
/// </summary>
public static class Floating
{
    // ids whose current press has already passed the drag threshold, so releasing is not a click
    private static readonly HashSet<string> _dragging = new();

    /// <summary>
    /// A draggable button in its own borderless window. Drag moves <paramref name="pos"/>; a press
    /// that never passes the drag threshold is a click.
    /// </summary>
    /// <param name="pos">Top-left in screen space. Written back as the user drags, so persist it.</param>
    /// <param name="size">Zero measures the label and pads it.</param>
    /// <returns>True the frame the button is clicked, never on the release that ends a drag.</returns>
    public static bool Button(string id, string label, ref Vector2 pos, Vector2 size = default)
    {
        var style = ImGui.GetStyle();
        if (size.X <= 0f || size.Y <= 0f)
            size = ImGui.CalcTextSize(label) + style.FramePadding * 2f;

        // the window IS the button, no padding around it, so pos means the button's own top-left
        ImGui.SetNextWindowPos(pos, ImGuiCond.Always);
        ImGui.SetNextWindowSize(size, ImGuiCond.Always);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoBackground
                  | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing
                  | ImGuiWindowFlags.NoNav;

        bool clicked = false;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.Begin("##float_" + id, flags))
        {
            ImGui.InvisibleButton(id, size);
            bool hovered = ImGui.IsItemHovered();
            bool held = ImGui.IsItemActive();

            if (held && ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                _dragging.Add(id);
                pos += ImGui.GetIO().MouseDelta;
            }
            // a press that ends without ever dragging is the click
            if (ImGui.IsItemDeactivated()) clicked = !_dragging.Remove(id);

            if (hovered) ImGui.SetMouseCursor(held ? ImGuiMouseCursor.ResizeAll : ImGuiMouseCursor.Hand);

            var col = held ? ImGuiCol.ButtonActive : hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Button;
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var dl = ImGui.GetWindowDrawList();
            dl.AddRectFilled(min, max, ImGui.GetColorU32(col), style.FrameRounding);
            dl.AddRect(min, max, ImGui.GetColorU32(ImGuiCol.Border), style.FrameRounding);

            var text = ImGui.CalcTextSize(label);
            dl.AddText(min + (max - min - text) * 0.5f, ImGui.GetColorU32(ImGuiCol.Text), label);
        }
        ImGui.End();
        ImGui.PopStyleVar();

        // dragged off the edge it would be unrecoverable, so keep a corner reachable
        var vp = ImGui.GetMainViewport();
        pos.X = System.Math.Clamp(pos.X, vp.Pos.X, vp.Pos.X + vp.Size.X - size.X);
        pos.Y = System.Math.Clamp(pos.Y, vp.Pos.Y, vp.Pos.Y + vp.Size.Y - size.Y);

        return clicked;
    }
}
