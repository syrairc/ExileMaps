using System.Collections.Generic;

namespace ExileMaps.Classes
{
    // Serializable snapshot of weights keyed by settings dictionary key (map id, content key, biome key).
    // Used to (de)serialize exilemaps_weights.json default weights. Only weights stored, not colors/toggles.
    public class WeightExport
    {
        public Dictionary<string, float> Maps { get; set; } = [];
        public Dictionary<string, float> Content { get; set; } = [];
        public Dictionary<string, float> Biomes { get; set; } = [];
    }
}
