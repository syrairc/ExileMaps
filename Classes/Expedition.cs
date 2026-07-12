using System.Collections.Generic;
using GameOffsets2.Native;

namespace ExileMaps.Classes;

// One expedition region, snapshotted from AtlasPanel.Buttons each cache refresh. Transient - not persisted.
// Score/Label are derived at draw time from the live rumour weights, so they are not stored here.
public class Expedition
{
    // Stable per-region id (assigned in SnapshotExpeditions, ordered by RegionCoord). Drives the tint so
    // a region's scattered markers read as one expedition.
    public int Id;
    // Coord of the region's IsVisible button - the current spawn location (one per region).
    public Vector2i SpawnCoord;
    public Vector2i RegionCoord;
    // Every candidate button location in the region (one per Ocean button). Multiple can be shown at
    // once, so we place a marker at each hidden one.
    public List<Vector2i> ButtonCoords = new();
    public List<Vector2i> MapCoords = new();
    public Dictionary<string, int> Rumors = new();
}
