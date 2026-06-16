using System.Collections.Generic;
using System.Drawing;
using Newtonsoft.Json;

namespace ExileMaps.Classes
{
    // A named, self-contained route over the atlas. Owns its own ordered list of map stops
    // (bare coordinates), independent of the waypoint system. Adding a stop never creates a waypoint.
    public class Tour
    {
        public string Id { get; set; }                          // stable key (guid string)
        public string Name { get; set; }                        // e.g. "Tour 1"
        public bool Show { get; set; } = true;                  // per-tour render toggle
        public Color Color { get; set; }                        // route + order-number color
        public List<TourStop> Stops { get; set; } = new();      // ordered, manual order

        // Runtime only; rebuilt by BuildTour against the current map cache, never persisted.
        [JsonIgnore] public List<List<Node>> Segments { get; set; } = new();
        [JsonIgnore] public List<Node> ResolvedStops { get; set; } = new();
        [JsonIgnore] public List<string> Skipped { get; set; } = new();
        [JsonIgnore] public int BuiltVersion { get; set; } = -1;
    }

    // A single tour stop, stored as a bare coordinate. Plain int props serialize cleanly
    // (no custom Vector2i converter needed). Resolved to a cache node via Vector2i(X, Y).
    public struct TourStop
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
