using System;
using System.Numerics;
using RectangleF = ExileCore2.Shared.RectangleF; // Graphics.DrawImage uv overload wants this one

namespace ExileImGui2;

/// <summary>
/// UV / source-rect math for a row-major grid sprite sheet. Index 0 is top-left, counting across
/// then down. Works with ImGui AddImage (a Vector2 uv pair) or ExileCore2 Graphics.DrawImage
/// (a RectangleF uv).
/// </summary>
public readonly struct GridAtlas
{
    /// <summary>Atlas pixel width.</summary>
    public readonly int Width;
    /// <summary>Atlas pixel height.</summary>
    public readonly int Height;
    /// <summary>Side of one square cell, in pixels.</summary>
    public readonly int Cell;
    /// <summary>Cells per row. Rows are inferred from the index.</summary>
    public readonly int Columns;

    /// <summary>
    /// Describes a sheet of square cells laid out row-major.
    /// </summary>
    /// <param name="width">Atlas pixel width.</param>
    /// <param name="height">Atlas pixel height.</param>
    /// <param name="cell">Side of one cell, in pixels.</param>
    /// <param name="columns">Cells per row.</param>
    public GridAtlas(int width, int height, int cell, int columns)
    {
        Width = width; Height = height; Cell = cell; Columns = columns;
    }

    /// <summary>Top-left pixel of cell <paramref name="index"/>.</summary>
    public (int X, int Y) Cell0(int index) => ((index % Columns) * Cell, (index / Columns) * Cell);

    /// <summary>Normalised corner pair for a cell: uv0 top-left, uv1 bottom-right.</summary>
    public (Vector2 Uv0, Vector2 Uv1) UVPair(int index)
    {
        var (x, y) = Cell0(index);
        return (new Vector2((float)x / Width, (float)y / Height),
                new Vector2((float)(x + Cell) / Width, (float)(y + Cell) / Height));
    }

    /// <summary>
    /// Corner pair with V swapped, so a sprite drawn pointing up renders pointing down.
    /// </summary>
    public (Vector2 Uv0, Vector2 Uv1) UVPairFlippedV(int index)
    {
        var (a, b) = UVPair(index);
        return (new Vector2(a.X, b.Y), new Vector2(b.X, a.Y));
    }

    /// <summary>
    /// Normalised uv rect (x, y, w, h) for Graphics.DrawImage(..., RectangleF uv, ...).
    /// </summary>
    public RectangleF UVRect(int index)
    {
        var (a, b) = UVPair(index);
        return new RectangleF(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    /// <summary>
    /// V-flipped uv rect, expressed as a negative height. DrawImage forwards to AddImage, which
    /// flips when uv1.Y is above uv0.Y.
    /// </summary>
    public RectangleF UVRectFlippedV(int index)
    {
        var (a, b) = UVPair(index);
        return new RectangleF(a.X, b.Y, b.X - a.X, -(b.Y - a.Y));
    }

    /// <summary>
    /// Enum name to its int index, so callers can key sprites by a name stored as a string.
    /// </summary>
    /// <param name="key">Enum member name, matched case-insensitively.</param>
    /// <param name="fallback">Returned when <paramref name="key"/> is unknown or empty.</param>
    public static int Parse<T>(string key, T fallback) where T : struct, Enum =>
        System.Enum.TryParse<T>(key, true, out var v) ? Convert.ToInt32(v) : Convert.ToInt32(fallback);
}
