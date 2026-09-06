using System.Numerics;
using ImGuiNET;
using Newtonsoft.Json;
using SColor = System.Drawing.Color;

namespace ExileImGui2;

/// <summary>
/// A text style setting: scale, color, optional background, optional border (with width and
/// rounding), optional stroke. Plain fields, so it persists on a settings class like any other POCO
/// member.
/// </summary>
public sealed class TextStyle
{
    /// <summary>Draw size over the current font size.</summary>
    public float Scale = 1f;

    [JsonConverter(typeof(EColorConverter))]
    public SColor Color = SColor.FromArgb(255, 255, 255, 255);

    public bool BgEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor BgColor = SColor.FromArgb(180, 0, 0, 0);

    public bool BorderEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor BorderColor = SColor.FromArgb(255, 255, 255, 255);
    public float BorderThickness = 1f;
    /// <summary>Shared with the background fill too, so the two corners always match.</summary>
    public float BorderRounding = 2f;

    public bool StrokeEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor StrokeColor = SColor.FromArgb(255, 0, 0, 0);

    // same 4-corner trick RichText.Outlined uses. not reusing that file here on purpose - pulling in
    // a whole extra module for four AddText calls isn't worth the edge.
    static readonly Vector2[] StrokeOffsets = { new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };

    /// <summary>
    /// One row: a swatch button showing the style live - background, border and stroke all render on
    /// the sample text itself, so the swatch doubles as its own indicator - then the label. Click opens
    /// a popup with the full editor and a bigger preview.
    /// </summary>
    /// <param name="sampleText">What the swatch and popup preview show. Falls back to "Sample Text"
    /// when empty.</param>
    public static bool Edit(string label, TextStyle style, string sampleText = "Sample Text",
        string id = null, float swatchWidth = 140f)
    {
        if (style == null) return false;
        bool changed = false;
        float h = ImGui.GetFrameHeight();

        ImGui.PushID(id ?? label);
        ImGui.InvisibleButton("##swatch", new Vector2(swatchWidth, h));
        bool clicked = ImGui.IsItemClicked();
        DrawText(ImGui.GetWindowDrawList(), style, sampleText, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (clicked) ImGui.OpenPopup("##edit");

        if (!string.IsNullOrEmpty(label))
        {
            ImGui.SameLine();
            Controls.AlignMid(ImGui.GetTextLineHeight());
            ImGui.TextUnformatted(label);
        }

        if (ImGui.BeginPopup("##edit"))
        {
            var pos = ImGui.GetCursorScreenPos();
            var boxSize = new Vector2(220f, h * 2f);
            ImGui.Dummy(boxSize);
            DrawText(ImGui.GetWindowDrawList(), style, sampleText, pos, pos + boxSize);
            ImGui.Separator();

            changed |= Controls.SliderFloat("Scale", ref style.Scale, 0.5f, 3f);
            changed |= Controls.Color("Color", ref style.Color);
            changed |= Controls.OverrideRow("bg", "Background", ref style.BgEnabled,
                () => Controls.Color("##bgcolor", ref style.BgColor, "bgcolor"),
                "Draw a filled box behind the text.");
            changed |= Controls.OverrideRow("border", "Border", ref style.BorderEnabled,
                () => Controls.Color("##bordercolor", ref style.BorderColor, "bordercolor"),
                "Draw a frame around the text.");
            changed |= Controls.SliderFloat("Border width", ref style.BorderThickness, 1f, 6f, fmt: "%.0f");
            changed |= Controls.SliderFloat("Border rounding", ref style.BorderRounding, 0f, 8f, fmt: "%.0f");
            changed |= Controls.OverrideRow("stroke", "Stroke", ref style.StrokeEnabled,
                () => Controls.Color("##strokecolor", ref style.StrokeColor, "strokecolor"),
                "Outline each glyph so it reads over any background.");
            ImGui.EndPopup();
        }

        ImGui.PopID();
        return changed;
    }


    /// <summary>Every field is a value type, so memberwise is a full clone.</summary>
    public TextStyle Clone() => (TextStyle)MemberwiseClone();

    /// <summary>Copies every field from another style. No allocation, for a reused scratch instance.</summary>
    public void CopyFrom(TextStyle o)
    {
        if (o == null) return;
        Scale = o.Scale; Color = o.Color;
        BgEnabled = o.BgEnabled; BgColor = o.BgColor;
        BorderEnabled = o.BorderEnabled; BorderColor = o.BorderColor;
        BorderThickness = o.BorderThickness; BorderRounding = o.BorderRounding;
        StrokeEnabled = o.StrokeEnabled; StrokeColor = o.StrokeColor;
    }

    /// <summary>
    /// The <see cref="TextStyleOverride"/> editor. Same swatch-plus-popup shape as <see cref="Edit"/>,
    /// except the swatch previews the RESOLVED style - base with the override laid over it - so you see
    /// what the thing will actually look like rather than the half of it you happen to be editing.
    /// <para>
    /// Each group in the popup is a <see cref="Controls.OverrideRow"/>: untick and that group falls back
    /// to base, which is greyed out rather than hidden so it stays obvious what is inheriting.
    /// </para>
    /// </summary>
    /// <param name="baseStyle">What unticked groups inherit. The preview needs it, so it is required.</param>
    /// <param name="scratch">Optional reusable instance for the resolve, so a table of these does not
    /// allocate a style per row per frame. One per call site is enough; it is only read within the call.</param>
    public static bool EditOverride(string label, TextStyleOverride ov, TextStyle baseStyle,
        string sampleText = "Sample Text", string id = null, float swatchWidth = 140f,
        TextStyle scratch = null)
    {
        if (ov == null || baseStyle == null) return false;
        bool changed = false;
        float h = ImGui.GetFrameHeight();

        var resolved = scratch ?? new TextStyle();
        resolved.CopyFrom(baseStyle);
        ov.ApplyTo(resolved);

        ImGui.PushID(id ?? label);
        ImGui.InvisibleButton("##swatch", new Vector2(swatchWidth, h));
        bool clicked = ImGui.IsItemClicked();
        DrawText(ImGui.GetWindowDrawList(), resolved, sampleText, ImGui.GetItemRectMin(), ImGui.GetItemRectMax());
        if (clicked) ImGui.OpenPopup("##editov");

        if (!string.IsNullOrEmpty(label))
        {
            ImGui.SameLine();
            Controls.AlignMid(ImGui.GetTextLineHeight());
            ImGui.TextUnformatted(label);
        }

        if (ImGui.BeginPopup("##editov"))
        {
            var pos = ImGui.GetCursorScreenPos();
            var boxSize = new Vector2(220f, h * 2f);
            ImGui.Dummy(boxSize);
            DrawText(ImGui.GetWindowDrawList(), resolved, sampleText, pos, pos + boxSize);
            ImGui.Separator();

            changed |= Controls.OverrideRow("sc", "Scale", ref ov.OverrideScale,
                () => Controls.SliderFloat("##scale", ref ov.Scale, 0.5f, 3f, "scale"));
            changed |= Controls.OverrideRow("col", "Color", ref ov.OverrideColor,
                () => Controls.Color("##color", ref ov.Color, "color"));

            changed |= Controls.OverrideRow("bg", "Background", ref ov.OverrideBg, () => {
                bool c = ImGui.Checkbox("##bgon", ref ov.BgEnabled);
                Controls.Tip("Draw a filled box behind the text.");
                ImGui.SameLine();
                return c | Controls.Color("##bgcolor", ref ov.BgColor, "bgcolor");
            });

            changed |= Controls.OverrideRow("bd", "Border", ref ov.OverrideBorder, () => {
                bool c = ImGui.Checkbox("##bdon", ref ov.BorderEnabled);
                Controls.Tip("Draw a frame around the text.");
                ImGui.SameLine();
                c |= Controls.Color("##bdcolor", ref ov.BorderColor, "bdcolor");
                c |= Controls.SliderFloat("Width", ref ov.BorderThickness, 1f, 6f, "bdw", "%.0f");
                c |= Controls.SliderFloat("Rounding", ref ov.BorderRounding, 0f, 8f, "bdr", "%.0f");
                return c;
            });

            changed |= Controls.OverrideRow("st", "Stroke", ref ov.OverrideStroke, () => {
                bool c = ImGui.Checkbox("##ston", ref ov.StrokeEnabled);
                Controls.Tip("Outline each glyph so it reads over any background.");
                ImGui.SameLine();
                return c | Controls.Color("##stcolor", ref ov.StrokeColor, "stcolor");
            });

            ImGui.EndPopup();
        }

        ImGui.PopID();
        return changed;
    }

    // the sample text itself, styled and centred within [min, max], clipped so an oversized scale
    // doesn't spill out of its box. no chrome behind it on purpose - a neutral frame here would read
    // as background whether or not BgEnabled is on.
    static void DrawText(ImDrawListPtr dl, TextStyle style, string sampleText, Vector2 min, Vector2 max)
    {
        string text = string.IsNullOrEmpty(sampleText) ? "Sample Text" : sampleText;
        var font = ImGui.GetFont();
        float size = ImGui.GetFontSize() * style.Scale;
        var ts = font.CalcTextSizeA(size, float.MaxValue, 0f, text);
        var pos = new Vector2(min.X + 4f, min.Y + (max.Y - min.Y - ts.Y) * 0.5f);
        var pad = new Vector2(5f, 2f);

        dl.PushClipRect(min, max, true);
        if (style.BgEnabled)
            dl.AddRectFilled(pos - pad, pos + ts + pad, EColor.U32(style.BgColor), style.BorderRounding);
        if (style.BorderEnabled)
            dl.AddRect(pos - pad, pos + ts + pad, EColor.U32(style.BorderColor), style.BorderRounding,
                ImDrawFlags.None, style.BorderThickness);
        if (style.StrokeEnabled)
        {
            uint stroke = EColor.U32(style.StrokeColor);
            foreach (var o in StrokeOffsets) dl.AddText(font, size, pos + o, stroke, text);
        }
        dl.AddText(font, size, pos, EColor.U32(style.Color), text);
        dl.PopClipRect();
    }
}

/// <summary>
/// A partial <see cref="TextStyle"/>: each visual group carries an Override flag, and only the ticked
/// groups are laid over a base style. Plain fields, so it persists like any other POCO member.
/// <para>
/// Grouped per visual feature rather than per field on purpose - "override the box colour but inherit
/// whether there is a box" is a distinction nobody wants, and it doubles the toggles.
/// </para>
/// </summary>
public sealed class TextStyleOverride
{
    public bool OverrideScale;
    public float Scale = 1f;

    public bool OverrideColor;
    [JsonConverter(typeof(EColorConverter))]
    public SColor Color = SColor.FromArgb(255, 255, 255, 255);

    public bool OverrideBg;
    public bool BgEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor BgColor = SColor.FromArgb(180, 0, 0, 0);

    public bool OverrideBorder;
    public bool BorderEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor BorderColor = SColor.FromArgb(255, 255, 255, 255);
    public float BorderThickness = 1f;
    public float BorderRounding = 2f;

    public bool OverrideStroke;
    public bool StrokeEnabled;
    [JsonConverter(typeof(EColorConverter))]
    public SColor StrokeColor = SColor.FromArgb(255, 0, 0, 0);

    /// <summary>Lays the ticked groups over <paramref name="s"/>, leaving the rest as they were.</summary>
    public void ApplyTo(TextStyle s)
    {
        if (s == null) return;
        if (OverrideScale) s.Scale = Scale;
        if (OverrideColor) s.Color = Color;
        if (OverrideBg) { s.BgEnabled = BgEnabled; s.BgColor = BgColor; }
        if (OverrideBorder) {
            s.BorderEnabled = BorderEnabled; s.BorderColor = BorderColor;
            s.BorderThickness = BorderThickness; s.BorderRounding = BorderRounding;
        }
        if (OverrideStroke) { s.StrokeEnabled = StrokeEnabled; s.StrokeColor = StrokeColor; }
    }

    /// <summary>True when nothing is ticked, i.e. this override changes nothing.</summary>
    public bool IsEmpty => !OverrideScale && !OverrideColor && !OverrideBg && !OverrideBorder && !OverrideStroke;

    public TextStyleOverride Clone() => (TextStyleOverride)MemberwiseClone();

    /// <summary>
    /// Seeds an override from a concrete style: copies the values across and ticks every group, so a
    /// freshly added override starts out looking exactly like what it was cloned from.
    /// </summary>
    public static TextStyleOverride FromStyle(TextStyle s) => new()
    {
        OverrideScale = true, Scale = s.Scale,
        OverrideColor = true, Color = s.Color,
        OverrideBg = true, BgEnabled = s.BgEnabled, BgColor = s.BgColor,
        OverrideBorder = true, BorderEnabled = s.BorderEnabled, BorderColor = s.BorderColor,
        BorderThickness = s.BorderThickness, BorderRounding = s.BorderRounding,
        OverrideStroke = true, StrokeEnabled = s.StrokeEnabled, StrokeColor = s.StrokeColor,
    };
}
