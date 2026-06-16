using System;
using System.Drawing;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GameOffsets2.Native;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.Shared.Enums;
using static ExileMaps.ExileMapsCore;
using Newtonsoft.Json;

namespace ExileMaps.Classes
{
    public class Waypoint
    {
        
        public string ID { get; set; }
        public string Name { get; set; }
        public string Text { get; set; }
        public bool Show { get; set; }
        public bool Line { get; set; }
        public bool Arrow { get; set; }
        public bool AutoCreated { get; set; } = false;
        public float Scale { get; set; } = 1;
        [JsonIgnore]
        public List<Node> PathFromStart { get; set; } = new List<Node>();
        [JsonIgnore]
        public int StepCount => PathFromStart?.Count > 0 ? PathFromStart.Count - 1 : -1;
        // Summed Weight of every map on PathFromStart (completed anchor + incomplete maps + destination).
        // Set alongside PathFromStart by UpdateWaypointPaths; used to break ties between equal-length paths.
        [JsonIgnore]
        public float PathWeight { get; set; }

        [JsonConverter(typeof(Vector2iConverter))]
        public Vector2i Coordinates;
        // Custom sprite-atlas icon (Icons_Desaturated.png). Defaults to the downward triangle marker.
        public SpriteIcon Icon { get; set; } = SpriteIcon.TriangleDown;
        public Color Color { get; set; }

        [JsonIgnore]
        public string CoordinatesString
        {
            get => $"{Coordinates.X},{Coordinates.Y}";

        }
        
        public long Address { get; set; }

        
        public AtlasNodeDescription MapNode () {
            // O(1) lookup via the live map cache instead of scanning AtlasPanel.Descriptions
            // (~1000 entries) with string allocations. Null on cache miss / mid-refresh.
            return Main?.GetAtlasNodeAt(Coordinates);
        }


    
    }
}