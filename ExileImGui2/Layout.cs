using System;
using System.Collections.Generic;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Row layout plumbing that isn't a widget. Kept out of <c>Controls.cs</c> so the frames that only
/// want right-alignment don't have to copy 1.3k lines of widgets to get it.
/// </summary>
public static class Layout
{
    // ---- trailing group: imgui has no right-align ----
    // measure the group once, place it the next frame. width is cached per id, so a group only jumps on the
    // first frame it exists (or the frame its content changes width). not nestable - one open group at a time.
    static readonly Dictionary<string, float> _trailW = new();
    static string _trailId;

    /// <summary>
    /// Right-aligns a group of widgets. ImGui has no right-align, so the group is measured on one
    /// frame and placed on the next - it only jumps on the first frame it exists, or the frame its
    /// content changes width. Not nestable: one open group at a time.
    /// <para>
    /// Call it after the row's left-hand item, draw the tail widgets, then <see cref="EndTrailing"/>.
    /// Works inside a table cell too, aligning to the cell's right edge rather than the window's.
    /// </para>
    /// </summary>
    /// <param name="pad">Pushes the group off the right edge, to leave room for a scrollbar or a
    /// child border.</param>
    public static void BeginTrailing(string id, float pad = 0f)
    {
        float w = _trailW.TryGetValue(id, out var x) ? x : 0f;
        // cursor + avail is the right edge of whatever we're in, so this also lands correctly inside a
        // table cell (GetContentRegionMax would give the whole window and overshoot).
        float left = ImGui.GetCursorPosX();
        float right = left + ImGui.GetContentRegionAvail().X;
        // don't use SameLine(offset) - it adds the table's column offset to whatever you pass, and our x
        // already carries it, so anything past the first column lands off the right edge and gets clipped.
        // SameLine(0,0) + SetCursorPosX are both plain window-local.
        ImGui.SameLine(0, 0);
        ImGui.SetCursorPosX(Math.Max(left, right - w - pad));
        ImGui.BeginGroup();
        _trailId = id;
    }

    /// <summary>Closes a <see cref="BeginTrailing"/> and caches its measured width for next frame.</summary>
    public static void EndTrailing()
    {
        ImGui.EndGroup();
        if (_trailId != null) _trailW[_trailId] = ImGui.GetItemRectSize().X;   // remembered for next frame
        _trailId = null;
    }
}
