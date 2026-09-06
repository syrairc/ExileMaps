using System;
using System.Numerics;
using ImGuiNET;

namespace ExileImGui2;

/// <summary>
/// Read-only progress bars: a fraction bar that can also run indeterminate, and a segmented strip
/// for a fixed run of per-item states.
/// <para>
/// Neither is interactive, so neither pushes an ID and neither returns a changed flag - the one
/// place this library's controls break their own convention, and only because there is nothing to
/// change.
/// </para>
/// </summary>
public static class Bars
{
    /// <summary>
    /// Filled bar across the available width, themed with FrameBg behind and PlotHistogram in front.
    /// </summary>
    /// <param name="fraction">0..1, clamped. Negative runs the indeterminate slider instead, for
    /// work with no known end.</param>
    /// <param name="overlay">Centred text drawn over the bar. Skipped when null or empty.</param>
    /// <param name="height">Defaults to one text line.</param>
    public static void Progress(float fraction, string overlay = null, float height = 0f)
    {
        var width = ImGui.GetContentRegionAvail().X;
        if (width <= 0f) return;
        if (height <= 0f) height = ImGui.GetTextLineHeight();

        var min = ImGui.GetCursorScreenPos();
        var max = new Vector2(min.X + width, min.Y + height);
        var draw = ImGui.GetWindowDrawList();
        var rounding = ImGui.GetStyle().FrameRounding;

        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg), rounding);

        if (fraction < 0f)
        {
            var span = width * 0.2f;
            var t = (float)(Math.Sin(ImGui.GetTime() * 1.6) * 0.5 + 0.5);
            var x = min.X + (width - span) * t;
            draw.AddRectFilled(new Vector2(x, min.Y), new Vector2(x + span, max.Y),
                ImGui.GetColorU32(ImGuiCol.PlotHistogram), rounding);
        }
        else
        {
            var filled = width * Math.Clamp(fraction, 0f, 1f);
            if (filled > 0f)
                draw.AddRectFilled(min, new Vector2(min.X + filled, max.Y),
                    ImGui.GetColorU32(ImGuiCol.PlotHistogram), rounding);
        }

        ImGui.Dummy(new Vector2(width, height));

        if (string.IsNullOrEmpty(overlay)) return;

        var size = ImGui.CalcTextSize(overlay);
        draw.AddText(new Vector2(min.X + (width - size.X) * 0.5f, min.Y + (height - size.Y) * 0.5f),
            ImGui.GetColorU32(ImGuiCol.Text), overlay);
    }

    /// <summary>
    /// Equal-width segments across the available width, one colour each, with a 1px gap between
    /// them. Draws nothing when the span is empty.
    /// </summary>
    /// <param name="colors">Packed ImGui colours, one per segment.</param>
    /// <param name="height">Defaults to one text line.</param>
    public static void Segmented(ReadOnlySpan<uint> colors, float height = 0f)
    {
        if (colors.Length == 0) return;

        var width = ImGui.GetContentRegionAvail().X;
        if (width <= 0f) return;
        if (height <= 0f) height = ImGui.GetTextLineHeight();

        var min = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var step = width / colors.Length;

        for (var i = 0; i < colors.Length; i++)
        {
            var x0 = min.X + step * i;
            var x1 = Math.Min(min.X + width, Math.Max(x0 + 1f, x0 + step - 1f));
            draw.AddRectFilled(new Vector2(x0, min.Y), new Vector2(x1, min.Y + height), colors[i]);
        }

        ImGui.Dummy(new Vector2(width, height));
    }
}
