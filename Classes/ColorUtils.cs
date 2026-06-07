using System;
using System.Drawing;
using ExileCore2.PoEMemory.Components;

namespace ExileMaps.Classes
{
    public static class ColorUtils
    {
        public static Color InterpolateColor(Color color1, Color color2, float fraction)
        {
            float r = color1.R + (color2.R - color1.R) * fraction;
            float g = color1.G + (color2.G - color1.G) * fraction;
            float b = color1.B + (color2.B - color1.B) * fraction;
            float a = color1.A + (color2.A - color1.A) * fraction;

            // Restrict RGBA values to 0-255
            int iR = Math.Max(Math.Min((int)r, 255), 0);
            int iG = Math.Max(Math.Min((int)g, 255), 0);
            int iB = Math.Max(Math.Min((int)b, 255), 0);
            int iA = Math.Max(Math.Min((int)a, 255), 0);
            
            return Color.FromArgb(iA, iR, iG, iB);
        }

        /// <summary>Builds an opaque RGB color from HSV. hue 0-360, sat/val 0-1.</summary>
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
}