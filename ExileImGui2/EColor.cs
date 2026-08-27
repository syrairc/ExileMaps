using System;
using System.Numerics;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// Colour conversion between System.Drawing and ImGui, tint helpers, and a style-colour scope.
/// </summary>
public static class EColor
{
    /// <summary>Colour to ImGui's normalised rgba vector.</summary>
    public static Vector4 ToVector4(SColor c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    /// <summary>Normalised rgba vector back to a colour. Each channel is clamped.</summary>
    public static SColor FromVector4(Vector4 v) => SColor.FromArgb(
        (byte)Math.Clamp(v.W * 255f, 0, 255),
        (byte)Math.Clamp(v.X * 255f, 0, 255),
        (byte)Math.Clamp(v.Y * 255f, 0, 255),
        (byte)Math.Clamp(v.Z * 255f, 0, 255));

    /// <summary>Colour to the packed uint the ImGui draw-list calls want.</summary>
    public static uint U32(SColor c) => ImGui.GetColorU32(ToVector4(c));

    /// <summary>Replaces the alpha, keeping rgb.</summary>
    /// <param name="a">New alpha as 0..1.</param>
    public static SColor Fade(SColor c, float a) =>
        SColor.FromArgb((byte)Math.Clamp(a * 255f, 0, 255), c.R, c.G, c.B);

    /// <summary>
    /// Multiplies rgb and keeps alpha, for hover and press shades of a given fill. Above 1 lightens,
    /// below 1 darkens.
    /// </summary>
    public static SColor Scale(SColor c, float f) => SColor.FromArgb(
        c.A,
        (byte)Math.Clamp(c.R * f, 0, 255),
        (byte)Math.Clamp(c.G * f, 0, 255),
        (byte)Math.Clamp(c.B * f, 0, 255));

    /// <summary>
    /// Drops all saturation, keeping brightness and alpha. Uses the same perceptual luma
    /// <see cref="Contrast"/> does.
    /// </summary>
    public static SColor Desaturate(SColor c)
    {
        byte l = (byte)Math.Clamp(c.R * 0.299f + c.G * 0.587f + c.B * 0.114f, 0, 255);
        return SColor.FromArgb(c.A, l, l, l);
    }

    /// <summary>
    /// Readable text colour for a fill: black on light, white on dark. Perceptual luma, with the
    /// threshold picked so mid greens go black.
    /// </summary>
    public static SColor Contrast(SColor bg) =>
        bg.R * 0.299f + bg.G * 0.587f + bg.B * 0.114f > 150f
            ? SColor.FromArgb(255, 0, 0, 0)
            : SColor.FromArgb(255, 255, 255, 255);

    /// <summary>
    /// Hashes a string to a stable, readable colour - handy for tagging items by category name.
    /// Stable across processes, unlike string.GetHashCode, which is per-process randomized.
    /// </summary>
    public static SColor CategoryColor(string s)
    {
        uint h = 2166136261u;
        foreach (char ch in s ?? "")
        {
            h ^= ch;
            h *= 16777619u;
        }
        ImGui.ColorConvertHSVtoRGB((h % 360u) / 360f, 0.55f, 0.95f, out float r, out float g, out float b);
        return SColor.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>
    /// Pushes N style colours and pops exactly those on dispose, so there is no pop count to keep in
    /// sync. Use with <c>using</c>.
    /// </summary>
    public readonly struct StyleColorScope : IDisposable
    {
        readonly int _n;

        /// <summary>Pushes each (slot, packed rgba) pair given.</summary>
        public StyleColorScope(params (ImGuiCol col, uint rgba)[] colors)
        {
            _n = colors.Length;
            foreach (var (col, rgba) in colors) ImGui.PushStyleColor(col, rgba);
        }

        /// <summary>Pops the colours this scope pushed.</summary>
        public void Dispose() => ImGui.PopStyleColor(_n);
    }
}

/// <summary>
/// Round-trips a <see cref="System.Drawing.Color"/> through JSON as {R,G,B,A}. Tag a settings field
/// with <c>[JsonConverter(typeof(EColorConverter))]</c> and it persists like anything else.
/// </summary>
public class EColorConverter : JsonConverter<SColor>
{
    /// <summary>Writes the colour as an object with byte R, G, B and A members.</summary>
    public override void WriteJson(JsonWriter w, SColor c, JsonSerializer s)
    {
        w.WriteStartObject();
        w.WritePropertyName("R"); w.WriteValue(c.R);
        w.WritePropertyName("G"); w.WriteValue(c.G);
        w.WritePropertyName("B"); w.WriteValue(c.B);
        w.WritePropertyName("A"); w.WriteValue(c.A);
        w.WriteEndObject();
    }

    /// <summary>Reads back what <see cref="WriteJson"/> produced.</summary>
    public override SColor ReadJson(JsonReader r, Type t, SColor existing, bool has, JsonSerializer s)
    {
        var o = JObject.Load(r);
        return SColor.FromArgb(o.Value<byte>("A"), o.Value<byte>("R"), o.Value<byte>("G"), o.Value<byte>("B"));
    }
}
