using System.Collections.Generic;

namespace ExileMaps.Classes
{
    /// <summary>
    /// Serializable snapshot of every weight in the settings, keyed by the settings dictionary key
    /// (map id, content key, biome key, mod key). Written by ExportWeights / read by ImportWeights so
    /// users can save and share weight presets. Only weights are stored — colors, toggles, etc. are not.
    /// </summary>
    public class WeightExport
    {
        public Dictionary<string, float> Maps { get; set; } = [];
        public Dictionary<string, float> Content { get; set; } = [];
        public Dictionary<string, float> Biomes { get; set; } = [];
    }
}
