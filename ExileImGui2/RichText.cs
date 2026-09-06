using System.Numerics;
using ImGuiNET;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Text drawn onto a draw list with a background pill or a black outline, plus a bare highlight box.
/// These take an explicit <see cref="ImDrawListPtr"/> so they work on the foreground list (overlays)
/// or on a window's own list.
/// </summary>
public static class RichText
{
    // 4-corner black outline the game overlays use, so light text stays readable over anything.
    static readonly Vector2[] Outline = { new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };

    /// <summary>
    /// Text with a 1px black outline, readable over any game background.
    /// </summary>
    /// <param name="font">Font to draw with, and <paramref name="size"/> its pixel size - pass
    /// ImGui.GetFont()/GetFontSize() to match the game font scale.</param>
    public static void Outlined(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text, SColor color)
    {
        uint black = ImGui.GetColorU32(new Vector4(0, 0, 0, 1f));
        foreach (var o in Outline) dl.AddText(font, size, pos + o, black, text);
        dl.AddText(font, size, pos, EColor.U32(color), text);
    }

    /// <summary>
    /// Text on a rounded background box.
    /// </summary>
    /// <param name="pos">Top-left of the text itself; the pill extends outward by the padding.</param>
    /// <param name="bg">Box fill. A zero alpha draws the text alone.</param>
    /// <returns>The outer pill size, so callers can advance or lay out around it.</returns>
    public static Vector2 Pill(ImDrawListPtr dl, ImFontPtr font, float size, Vector2 pos, string text,
        SColor textColor, SColor bg, float padX = 4f, float padY = 2f, float rounding = 2f)
    {
        var ts = font.CalcTextSizeA(size, float.MaxValue, 0f, text);
        var min = new Vector2(pos.X - padX, pos.Y - padY);
        var max = new Vector2(pos.X + ts.X + padX, pos.Y + ts.Y + padY);
        if (bg.A > 0) dl.AddRectFilled(min, max, EColor.U32(bg), rounding);
        dl.AddText(font, size, pos, EColor.U32(textColor), text);
        return max - min;
    }

    /// <summary>
    /// Outline box around a screen rect, for example to ring an item slot. No fill.
    /// </summary>
    /// <param name="expand">Pixels to grow the rect by on every side before drawing.</param>
    public static void Highlight(ImDrawListPtr dl, Vector2 min, Vector2 max, SColor color,
        float thickness = 3f, float rounding = 0f, float expand = 2f)
    {
        var e = new Vector2(expand, expand);
        dl.AddRect(min - e, max + e, EColor.U32(color), rounding, ImDrawFlags.None, thickness);
    }
}
