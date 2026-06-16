using System;
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
    public bool IsCompleted;
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
    // Content type of a content-specific atlas point (Breach/Abyss/Incursion/Delirium/Ritual),
    // parsed from the passive id. Null/empty = generic atlas point. Used to tint the marker.
    [JsonIgnore]
    public string AtlasPointType { get; set; }
    // True when the map has atlas quest content (PassiveSkill.Id contains "AtlasQuest").
    [JsonIgnore]
    public bool HasAtlasQuest { get; set; }

    // Raw, zoom-independent node art width (Element.Width, captured in the map cache). Ordinary maps
    // render at one of NormalArtWidths; special landmark/boss/tower maps are a different size. Used by
    // IsSpecial. (The screen rect width scales with atlas zoom and is NOT reliable for this — that was
    // the old detection bug.)
    [JsonIgnore]
    public float ArtWidth { get; set; }

    // Ordinary map nodes use one of these raw art widths (40 = most maps, 65 = Bluff/Alpine Ridge/Mesa
    // and other larger-tile maps). Anything else is a wide landmark. Width is non-monotonic with
    // "special": some specials are 60 wide (smaller than 65-wide normal maps), so a single threshold
    // can't separate them — we exclude the known normal sizes instead.
    private static readonly float[] NormalArtWidths = { 40f, 65f };

    // Known special maps whose art is the SAME size as ordinary maps (40 wide: Sealed Vault + the three
    // Gateways; 65 wide: the Precursor Tower "initial tower" variant), so the size test can't see them.
    // These are the only specials that must be hard-coded — every special with distinctive (60 or 70+)
    // art is detected by width, so new wide-art specials need no code change. Names collapse biome/quest
    // variants (all "Precursor Tower"s share a name). Keep in sync with SpecialMapIds in
    // ExileMaps.HtmlExport.cs.
    private static readonly HashSet<string> SameSizeSpecialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sealed Vault",
        "Western Gateway",
        "Ancient Gateway",
        "Eastern Gateway",
        "Precursor Tower",
    };

    // User-added special map names (from Settings.Maps.SpecialMaps). Lets the user flag any map the
    // size/name heuristics miss, without a code change. Reference-swapped (not mutated in place) so the
    // render/cache threads never see a torn set. Synced via SetSpecialConfig. Case-insensitive.
    private static HashSet<string> UserSpecialNames = new(StringComparer.OrdinalIgnoreCase);

    // When set, non-visited special maps are forced to SpecialMaxWeight (so they always rank highest).
    private static bool SpecialUseMaxWeight;
    private static float SpecialMaxWeight = 50f;

    // Replaces the user special-map config. Call on settings load and whenever the list/options change.
    public static void SetSpecialConfig(IEnumerable<string> names, bool useMaxWeight, float maxWeight)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names != null)
            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n))
                    set.Add(n.Trim());
        UserSpecialNames = set;          // atomic reference swap
        SpecialUseMaxWeight = useMaxWeight;
        SpecialMaxWeight = maxWeight;
    }

    // Two-prong "special map" test: distinctive art width (auto-covers current and future wide-art
    // specials) OR a known/user-added special that reuses a normal art size (caught by name).
    [JsonIgnore]
    public bool IsSpecial =>
        (ArtWidth > 0f && !IsNormalArtWidth(ArtWidth))
        || (!string.IsNullOrEmpty(Name)
            && (SameSizeSpecialNames.Contains(Name) || UserSpecialNames.Contains(Name)));

    private static bool IsNormalArtWidth(float w)
    {
        foreach (var n in NormalArtWidths)
            if (Math.Abs(w - n) < 0.5f)
                return true;
        return false;
    }

    public long Address { get; set; }
    public long ParentAddress { get; set; }

    [JsonIgnore]
    public AtlasNodeDescription MapNode { get; set; }

    // True once immutable per-coordinate data (MapType, Content, atlas passive) has been resolved, so
    // periodic refreshes skip the expensive re-read. A full cache rebuild creates a fresh node (false).
    [JsonIgnore]
    public bool StaticResolved { get; set; }

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

        // Optionally pin special maps to a fixed max weight so they always rank highest.
        if (SpecialUseMaxWeight && IsSpecial) {
            Weight = SpecialMaxWeight;
            return;
        }

        // Weight = MapWeight + |MapWeight| * sum(contentWeights) / 100 + sum(biomeWeights).
        // Scaling by magnitude means positive content raises and negative content lowers regardless of sign.
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
