using System.Collections.Generic;

namespace ExileMaps.Classes
{
    /// <summary>
    /// Serializable snapshot of weights keyed by the settings dictionary key (map id, content key,
    /// biome key). Used to (de)serialize the bundled json/exilemaps_weights.json default weights
    /// (see LoadDefaultWeights). Only weights are stored — colors, toggles, etc. are not.
    /// </summary>
    public class WeightExport
    {
        public Dictionary<string, float> Maps { get; set; } = [];
        public Dictionary<string, float> Content { get; set; } = [];
        public Dictionary<string, float> Biomes { get; set; } = [];
    }
}
