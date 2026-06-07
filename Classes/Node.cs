using System.Collections.Generic;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using System.Text;
using GameOffsets2.Native;
using System.Linq;
using System.Text.Json.Serialization;

namespace ExileMaps.Classes;

public class Node
{
    public string Name { get; set; }
    public string Id { get; set; }

    [JsonIgnore]
    public bool IsUnlocked;
    
    [JsonIgnore]
    public bool IsVisible;
    [JsonIgnore]
    public bool IsActive;
    [JsonIgnore]
    public bool IsVisited;
    [JsonIgnore]
    public bool IsWaypoint;
    [JsonIgnore]
    public bool IsFailed => !IsUnlocked && IsVisited;
    [JsonIgnore]
    public bool IsAttempted => !IsUnlocked && IsVisited;
    // Favorited if the map type is a favorite, or any content present on the node is favorited.
    [JsonIgnore]
    public bool IsFavorited => (MapType?.Favorite ?? false) || Content.Values.Any(c => c.Favorite);
    [JsonIgnore]
    public List<Vector2i> NeighborCoordinates { get; set; } = [];
    [JsonIgnore]
    public Vector2i Coordinates { get; set; }
    [JsonIgnore]
    public Dictionary<Vector2i, Node> Neighbors { get; set; } = [];
    public Dictionary<string, Biome> Biomes { get; set; } = [];
    [JsonIgnore]
    public Dictionary<string, Content> Content { get; set; } = [];
    [JsonIgnore]
    public Map MapType { get; set; }
    [JsonIgnore]
    public float Weight { get; set; }
    // True when the map grants an atlas passive point (AtlasEntry.PassiveSkill present).
    [JsonIgnore]
    public bool GivesAtlasPoint { get; set; }
    // True when the map has atlas quest content (PassiveSkill.Id contains "AtlasQuest").
    [JsonIgnore]
    public bool HasAtlasQuest { get; set; }

    public long Address { get; set; }
    public long ParentAddress { get; set; }

    [JsonIgnore]
    public AtlasNodeDescription MapNode { get; set; }

    public bool MatchID(string id) {
        return MapType.MatchID(id);
    }
    public Waypoint ToWaypoint() {
        return new Waypoint {
            Name = Name,
            ID = Id,
            Address = Address,
            Coordinates = Coordinates,
            Show = true
        };
    }

    public void RecalculateWeight() {
        if (IsVisited || (IsVisited && !IsUnlocked)) {
            Weight = 500;
            return;
        }
        
        // Content weights are whole-number percentage bonuses (default 25, may be negative) that add
        // together and scale by the MAGNITUDE of the base map weight, so positive content always raises
        // the value even when the base weight is negative (and negative content always lowers it):
        //   Weight = MapWeight + |MapWeight| * (cw1 + cw2 + ...) / 100 + (biome weights).
        // For a positive base this is identical to the old MapWeight * (1 + sum/100). Base 0 -> content
        // contributes 0 (nothing to scale).
        float contentSum = 0f;
        foreach (var content in Content)
            contentSum += content.Value.Weight;

        Weight = MapType.Weight + System.Math.Abs(MapType.Weight) * (contentSum / 100f);

        foreach (var biome in Biomes)
            Weight += biome.Value.Weight;

    }
        
    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Name: {Name}");
        sb.AppendLine($"Id: {Id}");
        sb.AppendLine($"Address: {Address}");
        sb.AppendLine($"IsVisited: {IsVisited}");
        sb.AppendLine($"IsUnlocked: {IsUnlocked}");
        sb.AppendLine($"IsVisible: {IsVisible}");
        sb.AppendLine($"IsActive: {IsActive}");
        sb.AppendLine($"IsWaypoint: {IsWaypoint}");
        sb.AppendLine($"Coordinate: {Coordinates}");
        sb.AppendLine($"Weight: {Weight}");
        sb.AppendLine($"Neighbors: {string.Join(", ", NeighborCoordinates)}");
        sb.AppendLine($"Biomes: {string.Join(", ", Biomes.Where(x => x.Value != null).Select(x => x.Value.Name))}");
        sb.AppendLine($"Content: {string.Join(", ", Content.Select(x => x.Value.Name))}");

        return sb.ToString();
    }

    public string DebugText(bool includeContentBiomes = true) {

        StringBuilder sb = new();
        sb.AppendLine($"Id: {Id}");
        sb.AppendLine($"ParentAddress: {ParentAddress:X}");
        sb.AppendLine($"Weight: {Weight}");
        sb.AppendLine($"Coordinates: {Coordinates}");
        if (includeContentBiomes) {
            sb.AppendLine($"Biomes: {string.Join(", ", Biomes.Where(x => x.Value != null).Select(x => x.Value.Name))}");
            sb.AppendLine($"Content: {string.Join(", ", Content.Select(x => x.Value.Name))}");
        }

        return sb.ToString();
    }

    public List<Vector2i> GetNeighborCoordinates() {
        return NeighborCoordinates;
    }
}
