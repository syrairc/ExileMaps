using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json.Serialization;

namespace ExileMaps.Classes
{
    // One fully-resolved map-label look: text, background box, border. Concrete values, no toggles.
    // Color attributes mirror MapProfileEntry so Labels round-trips in the profile JSON the same way.
    public class LabelStyle
    {
        public float TextScale { get; set; } = 1.0f;

        [JsonConverter(typeof(JsonColorConverter))]
        public Color TextColor { get; set; } = Color.FromArgb(255, 255, 255, 255);
        public bool TextColorByWeight { get; set; } = false;

        public bool StrokeEnabled { get; set; } = false;
        [JsonConverter(typeof(JsonColorConverter))]
        public Color StrokeColor { get; set; } = Color.FromArgb(255, 0, 0, 0);

        public bool BoxVisible { get; set; } = true;
        [JsonConverter(typeof(JsonColorConverter))]
        public Color BoxColor { get; set; } = Color.FromArgb(177, 0, 0, 0);
        // legacy: opacity is the color alpha now. Kept for JSON load + the one-time fold migration.
        public int BoxOpacity { get; set; } = 177;
        public bool BoxColorByWeight { get; set; } = false;

        public bool BorderVisible { get; set; } = true;
        [JsonConverter(typeof(JsonColorConverter))]
        public Color BorderColor { get; set; } = Color.FromArgb(255, 120, 120, 130);
        // legacy: see BoxOpacity.
        public int BorderOpacity { get; set; } = 255;
        public bool BorderColorByWeight { get; set; } = false;
        public int BorderThickness { get; set; } = 2;

        // Color is a struct and every other field is a value type, so a memberwise copy is a full clone.
        public LabelStyle Clone() => (LabelStyle)MemberwiseClone();

        // Copy all fields from another style into this one (no allocation). All fields are value types.
        public void CopyFrom(LabelStyle o)
        {
            TextScale = o.TextScale;
            TextColor = o.TextColor; TextColorByWeight = o.TextColorByWeight;
            StrokeEnabled = o.StrokeEnabled; StrokeColor = o.StrokeColor;
            BoxVisible = o.BoxVisible; BoxColor = o.BoxColor; BoxOpacity = o.BoxOpacity; BoxColorByWeight = o.BoxColorByWeight;
            BorderVisible = o.BorderVisible; BorderColor = o.BorderColor; BorderOpacity = o.BorderOpacity;
            BorderColorByWeight = o.BorderColorByWeight; BorderThickness = o.BorderThickness;
        }
    }

    // Where an override's icon draws relative to the node. Ignored when the icon replaces the node icon.
    public enum IconPosition
    {
        AboveIcon,
        BelowLabel,
        LeftOfLabel,
        RightOfLabel,
    }

    // One override layer. Only fields whose Override* is true are applied; the rest fall through.
    // Stroke enabled+color share one toggle (OverrideStroke) to match the "toggle + color" design.
    public class LabelStyleOverride
    {
        public bool OverrideTextScale { get; set; }
        public float TextScale { get; set; } = 1.0f;

        public bool OverrideTextColor { get; set; }
        [JsonConverter(typeof(JsonColorConverter))]
        public Color TextColor { get; set; } = Color.FromArgb(255, 255, 255, 255);

        public bool OverrideTextColorByWeight { get; set; }
        public bool TextColorByWeight { get; set; }

        public bool OverrideStroke { get; set; }
        public bool StrokeEnabled { get; set; }
        [JsonConverter(typeof(JsonColorConverter))]
        public Color StrokeColor { get; set; } = Color.FromArgb(255, 0, 0, 0);

        public bool OverrideBoxVisible { get; set; }
        public bool BoxVisible { get; set; } = true;
        public bool OverrideBoxColor { get; set; }
        [JsonConverter(typeof(JsonColorConverter))]
        public Color BoxColor { get; set; } = Color.FromArgb(255, 0, 0, 0);
        public bool OverrideBoxOpacity { get; set; }
        public int BoxOpacity { get; set; } = 177;
        public bool OverrideBoxColorByWeight { get; set; }
        public bool BoxColorByWeight { get; set; }

        public bool OverrideBorderVisible { get; set; }
        public bool BorderVisible { get; set; } = true;
        public bool OverrideBorderColor { get; set; }
        [JsonConverter(typeof(JsonColorConverter))]
        public Color BorderColor { get; set; } = Color.FromArgb(255, 120, 120, 130);
        public bool OverrideBorderOpacity { get; set; }
        public int BorderOpacity { get; set; } = 255;
        public bool OverrideBorderColorByWeight { get; set; }
        public bool BorderColorByWeight { get; set; }
        public bool OverrideBorderThickness { get; set; }
        public int BorderThickness { get; set; } = 2;

        // Optional per-override icon (a SpriteIcon glyph). Icons are override-only; the base style has none.
        // Single winner across layers - see ResolveIconOverride. No Override* toggle: IconEnabled is the gate.
        public bool IconEnabled { get; set; }
        public SpriteIcon Icon { get; set; } = SpriteIcon.Circle;
        public bool IconReplacesNode { get; set; }
        public IconPosition IconPosition { get; set; } = IconPosition.LeftOfLabel;
        public float IconOffsetX { get; set; }
        public float IconOffsetY { get; set; }
        [JsonConverter(typeof(JsonColorConverter))]
        public Color IconTint { get; set; } = Color.FromArgb(255, 255, 255, 255);
        public float IconSize { get; set; } = 24f;

        public void ApplyTo(LabelStyle s)
        {
            if (OverrideTextScale) s.TextScale = TextScale;
            if (OverrideTextColor) s.TextColor = TextColor;
            if (OverrideTextColorByWeight) s.TextColorByWeight = TextColorByWeight;
            if (OverrideStroke) { s.StrokeEnabled = StrokeEnabled; s.StrokeColor = StrokeColor; }
            if (OverrideBoxVisible) s.BoxVisible = BoxVisible;
            if (OverrideBoxColor) s.BoxColor = BoxColor;
            if (OverrideBoxOpacity) s.BoxOpacity = BoxOpacity;
            if (OverrideBoxColorByWeight) s.BoxColorByWeight = BoxColorByWeight;
            if (OverrideBorderVisible) s.BorderVisible = BorderVisible;
            if (OverrideBorderColor) s.BorderColor = BorderColor;
            if (OverrideBorderOpacity) s.BorderOpacity = BorderOpacity;
            if (OverrideBorderColorByWeight) s.BorderColorByWeight = BorderColorByWeight;
            if (OverrideBorderThickness) s.BorderThickness = BorderThickness;
        }

        public LabelStyleOverride Clone() => (LabelStyleOverride)MemberwiseClone();

        // Seed an override from a concrete style: copy values, turn every toggle on.
        public static LabelStyleOverride FromStyle(LabelStyle s) => new LabelStyleOverride
        {
            OverrideTextScale = true, TextScale = s.TextScale,
            OverrideTextColor = true, TextColor = s.TextColor,
            OverrideTextColorByWeight = true, TextColorByWeight = s.TextColorByWeight,
            OverrideStroke = true, StrokeEnabled = s.StrokeEnabled, StrokeColor = s.StrokeColor,
            OverrideBoxVisible = true, BoxVisible = s.BoxVisible,
            OverrideBoxColor = true, BoxColor = s.BoxColor,
            OverrideBoxOpacity = true, BoxOpacity = s.BoxOpacity,
            OverrideBoxColorByWeight = true, BoxColorByWeight = s.BoxColorByWeight,
            OverrideBorderVisible = true, BorderVisible = s.BorderVisible,
            OverrideBorderColor = true, BorderColor = s.BorderColor,
            OverrideBorderOpacity = true, BorderOpacity = s.BorderOpacity,
            OverrideBorderColorByWeight = true, BorderColorByWeight = s.BorderColorByWeight,
            OverrideBorderThickness = true, BorderThickness = s.BorderThickness,
        };
    }

    // The whole per-profile label look: base + four override layers. Content/Biome keyed by type name.
    public class LabelStyleSettings
    {
        public LabelStyle Base { get; set; } = new LabelStyle();
        public LabelStyleOverride Favorite { get; set; } = new LabelStyleOverride();
        public LabelStyleOverride Special { get; set; } = new LabelStyleOverride();
        public Dictionary<string, LabelStyleOverride> Content { get; set; } = new();
        public Dictionary<string, LabelStyleOverride> Biome { get; set; } = new();
        public Dictionary<string, LabelStyleOverride> Map { get; set; } = new();

        public LabelStyleSettings Clone()
        {
            var c = new LabelStyleSettings
            {
                Base = Base.Clone(),
                Favorite = Favorite.Clone(),
                Special = Special.Clone(),
            };
            foreach (var kv in Content) c.Content[kv.Key] = kv.Value.Clone();
            foreach (var kv in Biome) c.Biome[kv.Key] = kv.Value.Clone();
            foreach (var kv in Map) c.Map[kv.Key] = kv.Value.Clone();
            return c;
        }

        // Out-of-box look: base = the normal bordered-box name, special = larger purple text + border.
        // Reproduces the pre-rework defaults so a fresh install and a new profile look unchanged.
        public static LabelStyleSettings Defaults()
        {
            var s = new LabelStyleSettings();
            s.Base = new LabelStyle
            {
                TextScale = 1.0f,
                TextColor = Color.FromArgb(255, 255, 255, 255),
                TextColorByWeight = false,
                StrokeEnabled = true,
                StrokeColor = Color.FromArgb(255, 0, 0, 0),
                BoxVisible = true,
                BoxColor = Color.FromArgb(69, 0, 0, 0),
                BoxOpacity = 69,
                BorderVisible = false,
                BorderColor = Color.FromArgb(255, 0, 0, 0),
                BorderOpacity = 255,
                BorderColorByWeight = true,
                BorderThickness = 1,
            };
            s.Special = new LabelStyleOverride
            {
                OverrideTextScale = true, TextScale = 1.4f,
                OverrideTextColor = true, TextColor = Color.FromArgb(255, 200, 80, 255),
                OverrideBoxVisible = true, BoxVisible = true,
                OverrideBoxColor = true, BoxColor = Color.FromArgb(177, 0, 0, 0),
                OverrideBorderColor = true, BorderColor = Color.FromArgb(255, 200, 80, 255),
                OverrideBorderColorByWeight = true, BorderColorByWeight = false,
                OverrideTextColorByWeight = true, TextColorByWeight = false,
            };
            return s;
        }
    }
}
