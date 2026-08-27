
using System.Numerics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using ExileCore2.PoEMemory.Components;
using System.Threading.Tasks;
using Newtonsoft.Json;
using GameOffsets2.Native;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ExileMaps.Classes;

#region Sprite Icons

public enum SpriteIcon
{
    Circle          = 0,
    Square          = 1,
    TriangleUp      = 2,
    TriangleDown    = 3,
    Diamond         = 4,
    Hexagon         = 5,
    Pentagon        = 6,
    Octagon         = 7,

    Star5           = 8,
    Star6           = 9,
    Star4           = 10,
    Star8           = 11,
    Crescent        = 12,
    Teardrop        = 13,
    Kite            = 14,
    Shield          = 15,

    Heart           = 16,
    Plus            = 17,
    Cross           = 18,
    Chevron         = 19,
    Arrow           = 20,
    Dot             = 21,
    Flower4         = 22,
    Flower6         = 23,

    Pinwheel        = 24,
    Trapezoid       = 25,
    Dome            = 26,
    Pill            = 27,
    DiamondCluster  = 28,
    Target          = 29,
    Burst12         = 30,
    Exclamation     = 31,

    CircleOutline   = 32,
    SquareOutline   = 33,
    TriangleOutline = 34,
    DiamondOutline  = 35,
    HexagonOutline  = 36,
    PentagonOutline = 37,
    OctagonOutline  = 38,
    Star5Outline    = 39,

    Star6Outline    = 40,
    Star4Outline    = 41,
    CrescentOutline = 42,
    TeardropOutline = 43,
    KiteOutline     = 44,
    ShieldOutline   = 45,
    HeartOutline    = 46,
    RingThin        = 47,
}

public static class SpriteAtlas
{
    public const int AtlasWidth  = 1024;
    public const int AtlasHeight = 768;
    public const int CellSize    = 128;
    public const int Columns     = 8;
    public const int Rows        = 6;
    public const int Count       = Columns * Rows;

    public const string FileName = "Icons_Desaturated.png";

    public static (int X, int Y) GetCell(SpriteIcon icon)
    {
        int i = (int)icon;
        return ((i % Columns) * CellSize, (i / Columns) * CellSize);
    }

    public static (int X, int Y, int W, int H) GetSourceRect(SpriteIcon icon)
    {
        var (x, y) = GetCell(icon);
        return (x, y, CellSize, CellSize);
    }

    public static (Vector2 Uv0, Vector2 Uv1) GetUVPair(SpriteIcon icon)
    {
        var (x, y) = GetCell(icon);
        var uv0 = new Vector2((float)x / AtlasWidth,
                              (float)y / AtlasHeight);
        var uv1 = new Vector2((float)(x + CellSize) / AtlasWidth,
                              (float)(y + CellSize) / AtlasHeight);
        return (uv0, uv1);
    }

}

#endregion

#region Colors

public static class ColorUtils
{
    public static Color InterpolateColor(Color color1, Color color2, float fraction)
    {
        float r = color1.R + (color2.R - color1.R) * fraction;
        float g = color1.G + (color2.G - color1.G) * fraction;
        float b = color1.B + (color2.B - color1.B) * fraction;
        float a = color1.A + (color2.A - color1.A) * fraction;

        int iR = Math.Max(Math.Min((int)r, 255), 0);
        int iG = Math.Max(Math.Min((int)g, 255), 0);
        int iB = Math.Max(Math.Min((int)b, 255), 0);
        int iA = Math.Max(Math.Min((int)a, 255), 0);

        return Color.FromArgb(iA, iR, iG, iB);
    }

    public static Color WithAlphaOf(Color rgb, Color alphaFrom) => Color.FromArgb(alphaFrom.A, rgb.R, rgb.G, rgb.B);

    public static Color ColorFromHSV(float hue, float saturation, float value)
    {
        hue = ((hue % 360f) + 360f) % 360f;
        int hi = (int)(hue / 60f) % 6;
        float f = hue / 60f - (int)(hue / 60f);

        value *= 255f;
        int v = Math.Max(0, Math.Min(255, (int)value));
        int p = Math.Max(0, Math.Min(255, (int)(value * (1 - saturation))));
        int q = Math.Max(0, Math.Min(255, (int)(value * (1 - f * saturation))));
        int t = Math.Max(0, Math.Min(255, (int)(value * (1 - (1 - f) * saturation))));

        return hi switch
        {
            0 => Color.FromArgb(255, v, t, p),
            1 => Color.FromArgb(255, q, v, p),
            2 => Color.FromArgb(255, p, v, t),
            3 => Color.FromArgb(255, p, q, v),
            4 => Color.FromArgb(255, t, p, v),
            _ => Color.FromArgb(255, v, p, q),
        };
    }
}

#endregion

#region JSON Converters

public class Vector2iConverter : JsonConverter<Vector2i>
{
    public override void WriteJson(JsonWriter writer, Vector2i value, JsonSerializer serializer)
    {
        writer.WriteValue($"{value.X},{value.Y}");
    }

    public override Vector2i ReadJson(JsonReader reader, Type objectType, Vector2i existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        var value = reader.Value.ToString().Split(',');
        return new Vector2i(int.Parse(value[0]), int.Parse(value[1]));
    }

}

#endregion

#region Perf Monitor

public static class PerfMonitor
{
    private const int Samples = 60;
    private static readonly object Lock = new();
    private static readonly Dictionary<string, long[]> Buffers = new();
    private static readonly Dictionary<string, int> Heads = new();

    public static void Record(string key, long elapsedTicks)
    {
        lock (Lock)
        {
            if (!Buffers.TryGetValue(key, out var buf))
            {
                buf = new long[Samples];
                Buffers[key] = buf;
                Heads[key] = 0;
            }
            int h = Heads[key];
            buf[h] = elapsedTicks;
            Heads[key] = (h + 1) % Samples;
        }
    }

    private static double AvgMs(long[] buf)
    {
        long sum = 0;
        foreach (var t in buf) sum += t;
        return sum / (double)Samples / Stopwatch.Frequency * 1000.0;
    }

    public static List<(string Key, double Ms)> Snapshot()
    {
        lock (Lock)
            return Buffers.OrderBy(kv => kv.Key).Select(kv => (kv.Key, AvgMs(kv.Value))).ToList();
    }
}

#endregion

#region Native File Dialog

internal static class NativeFileDialog
{
    private const string Filter = "JSON files (*.json)\0*.json\0All files (*.*)\0*.*\0";

    public static string ShowOpen(string title, string initialDir) {
        var ofn = NewStruct(title, initialDir, null);
        ofn.Flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR;
        return GetOpenFileNameW(ref ofn) ? ofn.lpstrFile : null;
    }

    public static string ShowSave(string title, string defaultFileName, string initialDir) {
        var ofn = NewStruct(title, initialDir, defaultFileName);
        ofn.Flags = OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR;
        ofn.lpstrDefExt = "json";
        return GetSaveFileNameW(ref ofn) ? ofn.lpstrFile : null;
    }

    private static OPENFILENAME NewStruct(string title, string initialDir, string fileName) {
        var fileBuffer = new string('\0', 1024);
        if (!string.IsNullOrEmpty(fileName))
            fileBuffer = fileName + new string('\0', 1024 - fileName.Length);

        var ofn = new OPENFILENAME {
            lpstrFilter = Filter,
            lpstrFile = fileBuffer,
            nMaxFile = 1024,
            lpstrTitle = title,
            lpstrInitialDir = initialDir,
            hwndOwner = IntPtr.Zero,
        };
        ofn.lStructSize = Marshal.SizeOf(ofn);
        return ofn;
    }

    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_NOCHANGEDIR = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public string lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAME ofn);
}

#endregion
