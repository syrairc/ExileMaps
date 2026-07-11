using System.Collections.Generic;
using GameOffsets2.Native;

namespace ExileMaps.Classes;

// One expedition region, snapshotted from AtlasPanel.Buttons each cache refresh. Transient - not persisted.
// Score/Label are derived at draw time from the live rumour weights, so they are not stored here.
public class Expedition
{
    public Vector2i ButtonCoord;
    public Vector2i RegionCoord;
    public List<Vector2i> MapCoords = new();
    public Dictionary<string, int> Rumors = new();
}
