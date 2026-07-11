using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text.Json.Serialization;

namespace ExileMaps.Classes
{
    public class MapProfileEntry
    {
        public float Weight { get; set; } = 1.0f;

        [JsonConverter(typeof(JsonColorConverter))]
        public Color NodeColor { get; set; } = Color.FromArgb(200, 155, 155, 155);

        [JsonConverter(typeof(JsonColorConverter))]
        public Color NameColor { get; set; } = Color.FromArgb(255, 255, 255, 255);

        [JsonConverter(typeof(JsonColorConverter))]
        public Color BackgroundColor { get; set; } = Color.FromArgb(220, 0, 0, 0);

        public bool Highlight { get; set; } = true;
        public bool ColorNodesByWeight { get; set; } = true;
        public bool UseWeightColorForName { get; set; } = true;
        public bool Favorite { get; set; } = false;
        public SpriteIcon Icon { get; set; } = SpriteIcon.Circle;
    }

    public class ContentProfileEntry
    {
        public float Weight { get; set; } = 1.0f;

        [JsonConverter(typeof(JsonColorConverter))]
        public Color Color { get; set; } = Color.FromArgb(255, 255, 255, 255);

        public bool Highlight { get; set; } = true;
        public bool Favorite { get; set; } = false;
    }

    public class BiomeProfileEntry
    {
        public float Weight { get; set; } = 1.0f;
    }

    public class WeightProfile
    {
        public Dictionary<string, MapProfileEntry> Maps { get; set; } = new();
        public Dictionary<string, ContentProfileEntry> Content { get; set; } = new();
        public Dictionary<string, BiomeProfileEntry> Biomes { get; set; } = new();

        // Per-profile label look (base + Favorite/Content/Biome/Special overrides). Null on profiles
        // saved before this feature; migration/LoadProfile backfill it.
        public LabelStyleSettings Labels { get; set; }
    }
}
