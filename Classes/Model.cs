
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using System.Text;
using GameOffsets2.Native;
using System.Linq;
using System.Drawing;
using ExileImGui2;
using static ExileMaps.ExileMapsCore;

namespace ExileMaps.Classes;

#region Atlas Nodes

public class Node
{
    public string Name { get; set; }
    public string Id { get; set; }

    private string _upperName;
    private string _upperNameSource;
    [JsonIgnore]
    public string UppercaseName
    {
        get
        {
            if (!ReferenceEquals(_upperNameSource, Name))
            {
                _upperNameSource = Name;
                _upperName = Name?.ToUpper();
            }
            return _upperName;
        }
    }

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
    public bool IsAttempted => !IsUnlocked && IsVisited;
    [JsonIgnore]
    public bool IsDone => IsVisited || IsCompleted;
    [JsonIgnore]
    public bool IsNavigable => IsVisible || !Biomes.ContainsKey("BreachCity");
    [JsonIgnore]
    public System.Numerics.Vector3 WorldPos;
    [JsonIgnore]
    public bool HasWorldPos;
    private int favFrame = -1;
    private bool favCached;

    [JsonIgnore]
    public bool IsFavorited
    {
        get
        {
            var main = Main;
            var settings = main?.Settings;
            if (settings == null)
                return false;

            int frame = main.TickCount;
            if (favFrame == frame)
                return favCached;

            bool fav = settings.ReadMap(MapType?.ShortestId).Favorite;
            if (!fav)
                foreach (var c in Content.Values)
                    if (settings.ReadContent(c?.Id).Favorite) { fav = true; break; }

            favFrame = frame;
            favCached = fav;
            return fav;
        }
    }
    [JsonIgnore]
    public bool ConnectionsResolved { get; set; }
    [JsonIgnore]
    public List<Vector2i> NeighborCoordinates { get; set; } = [];
    [JsonIgnore]
    public Vector2i Coordinates { get; set; }
    [JsonIgnore]
    public Dictionary<Vector2i, Node> Neighbors { get; set; } = [];
    public Dictionary<string, BiomeInfo> Biomes { get; set; } = [];
    [JsonIgnore]
    public Dictionary<string, ContentInfo> Content { get; set; } = [];
    [JsonIgnore]
    public List<string> SpecialModifiers { get; set; } = [];
    [JsonIgnore]
    public List<string> ModifierDetails { get; set; } = [];
    [JsonIgnore]
    public int ModifierChildCount { get; set; } = -1;
    [JsonIgnore]
    public MapInfo MapType { get; set; }
    [JsonIgnore]
    public float Weight { get; set; }

    private float weightTextFor = float.NaN;
    private string weightText;
    [JsonIgnore]
    public string WeightText
    {
        get
        {
            if (weightText == null || weightTextFor != Weight) {
                weightTextFor = Weight;
                weightText = Weight.ToString("0");
            }
            return weightText;
        }
    }
    [JsonIgnore]
    public bool GivesAtlasPoint { get; set; }
    [JsonIgnore]
    public string AtlasPointType { get; set; }
    [JsonIgnore]
    public bool HasAtlasQuest { get; set; }

    [JsonIgnore]
    public float ArtWidth { get; set; }

    private static readonly float[] NormalArtWidths = { 40f, 65f };

    private static readonly HashSet<string> SameSizeSpecialNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Sealed Vault",
        "Western Gateway",
        "Ancient Gateway",
        "Eastern Gateway",
        "Precursor Tower",
    };

    [JsonIgnore]
    public bool IsSpecial { get; private set; }

    public void ResolveSpecial()
    {
        var settings = Main?.Settings;
        bool forced = settings != null && settings.ReadMap(MapType?.ShortestId).Special;
        IsSpecial = forced
            || (ArtWidth > 0f && !IsNormalArtWidth(ArtWidth))
            || (!string.IsNullOrEmpty(Name) && SameSizeSpecialNames.Contains(Name));
    }

    private static bool IsNormalArtWidth(float w)
    {
        foreach (var n in NormalArtWidths)
            if (Math.Abs(w - n) < 0.5f)
                return true;
        return false;
    }

    public long ParentAddress { get; set; }

    [JsonIgnore]
    public AtlasNodeDescription MapNode { get; set; }

    [JsonIgnore]
    public bool StaticResolved { get; set; }

    public Waypoint ToWaypoint() {
        return new Waypoint {
            Name = Name,
            Coordinates = Coordinates,
            Show = true
        };
    }

    public void RecalculateWeight() {
        if (IsDone) {
            Weight = 500;
            return;
        }

        var settings = Main?.Settings;
        if (settings == null) {
            Weight = 0f;
            return;
        }

        var special = settings.Maps.SpecialMaps;
        if (special.UseMaxWeight && IsSpecial) {
            Weight = special.MaxWeight;
            return;
        }

        float contentSum = 0f;
        foreach (var content in Content)
            contentSum += settings.ReadContent(content.Value?.Id).Weight;

        Weight = settings.ReadMap(MapType?.ShortestId).Weight + contentSum;

        foreach (var biome in Biomes)
            Weight += settings.BiomeWeight(biome.Value?.Name);

    }

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Name: {Name}");
        sb.AppendLine($"Id: {Id}");
        sb.AppendLine($"IsVisited: {IsVisited}");
        sb.AppendLine($"IsUnlocked: {IsUnlocked}");
        sb.AppendLine($"IsVisible: {IsVisible}");
        sb.AppendLine($"IsActive: {IsActive}");
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

}

#endregion

#region Game Identity

public class MapInfo
{
    public string Name { get; set; } = "";
    public string[] IDs { get; set; } = [];
    public string ShortestId { get; set; }

    public override string ToString() => Name;
}

public class ContentInfo
{
    public string Name { get; set; }
    public string Id { get; set; }
    public string AtlasIcon { get; set; }

    public override string ToString() => Name;
}

public class BiomeInfo
{
    public string Name { get; set; }
}

public class RumorInfo
{
    public string Text { get; set; } = "";
    public string Content { get; set; } = "";
    public string Description { get; set; } = "";
}

#endregion

#region Label Styles

public class LabelStyle
{
    public TextStyle Text { get; set; } = new();

    public bool TextColorByWeight { get; set; }
    public bool BoxColorByWeight { get; set; }
    public bool BorderColorByWeight { get; set; }

    public LabelStyle Clone()
    {
        var c = (LabelStyle)MemberwiseClone();
        c.Text = Text.Clone();
        return c;
    }

    public void CopyFrom(LabelStyle o)
    {
        Text.CopyFrom(o.Text);
        TextColorByWeight = o.TextColorByWeight;
        BoxColorByWeight = o.BoxColorByWeight;
        BorderColorByWeight = o.BorderColorByWeight;
    }
}

public enum IconPosition
{
    AboveIcon,
    BelowLabel,
    LeftOfLabel,
    RightOfLabel,
}

public class LabelStyleOverride
{
    public bool Enabled { get; set; } = true;

    public TextStyleOverride Text { get; set; } = new();

    public bool TextColorByWeight { get; set; }
    public bool BoxColorByWeight { get; set; }
    public bool BorderColorByWeight { get; set; }

    public bool IconEnabled { get; set; }
    public SpriteIcon Icon { get; set; } = SpriteIcon.Circle;
    public IconPosition IconPosition { get; set; } = IconPosition.LeftOfLabel;

    public SpriteIcon MapIcon { get; set; } = SpriteIcon.Circle;
    public bool OverrideMapIconTint { get; set; }
    public Color MapIconTint { get; set; } = Color.FromArgb(255, 255, 255, 255);
    public Color IconTint { get; set; } = Color.FromArgb(255, 255, 255, 255);
    public float IconSize { get; set; } = 24f;

    public void ApplyTo(LabelStyle s)
    {
        Text.ApplyTo(s.Text);
        if (Text.OverrideColor) s.TextColorByWeight = TextColorByWeight;
        if (Text.OverrideBg) s.BoxColorByWeight = BoxColorByWeight;
        if (Text.OverrideBorder) s.BorderColorByWeight = BorderColorByWeight;
    }

    public LabelStyleOverride Clone()
    {
        var c = (LabelStyleOverride)MemberwiseClone();
        c.Text = Text.Clone();
        return c;
    }
}

public class LabelStyleSettings
{
    public LabelStyle Base { get; set; } = new LabelStyle();
    public LabelStyleOverride Favorite { get; set; } = new LabelStyleOverride();
    public LabelStyleOverride Special { get; set; } = new LabelStyleOverride();
    public Dictionary<string, LabelStyleOverride> Content { get; set; } = new();
    public Dictionary<string, LabelStyleOverride> Maps { get; set; } = new();

    public LabelStyleSettings Clone()
    {
        var c = new LabelStyleSettings
        {
            Base = Base.Clone(),
            Favorite = Favorite.Clone(),
            Special = Special.Clone(),
        };
        foreach (var kv in Content) c.Content[kv.Key] = kv.Value.Clone();
        foreach (var kv in Maps) c.Maps[kv.Key] = kv.Value.Clone();
        return c;
    }

    private static LabelStyleOverride ContentColor(int r, int g, int b) =>
        new() { Text = new TextStyleOverride { OverrideColor = true, Color = Color.FromArgb(255, r, g, b) } };

    public static Dictionary<string, LabelStyleOverride> DefaultContentStyles()
    {
        var d = new Dictionary<string, LabelStyleOverride>
        {
            ["Abyss"]      = ContentColor(0, 255, 0),
            ["Breach"]     = ContentColor(157, 74, 255),
            ["Corrupted"]  = ContentColor(255, 77, 77),
            ["Delirium"]   = ContentColor(149, 149, 149),
            ["Expedition"] = ContentColor(160, 211, 255),
            ["Incursion"]  = ContentColor(255, 209, 113),
            ["Ritual"]     = ContentColor(255, 152, 105),
            ["Sanctified"] = ContentColor(255, 255, 255),
        };

        var boss = ContentColor(227, 197, 155);
        boss.Text.OverrideScale = true;
        boss.Text.Scale = 1.1f;
        boss.Text.OverrideBg = true;
        boss.Text.BgEnabled = true;
        boss.Text.BgColor = Color.FromArgb(180, 0, 0, 0);
        d["PowerfulMapBoss"] = boss;

        return d;
    }

    public static LabelStyleSettings Defaults()
    {
        var s = new LabelStyleSettings();
        s.Base = new LabelStyle
        {
            Text = new TextStyle
            {
                Scale = 1.0f,
                Color = Color.FromArgb(255, 255, 255, 255),
                StrokeEnabled = true,
                StrokeColor = Color.FromArgb(255, 0, 0, 0),
                BgEnabled = true,
                BgColor = Color.FromArgb(69, 0, 0, 0),
                BorderEnabled = false,
                BorderColor = Color.FromArgb(255, 0, 0, 0),
                BorderThickness = 1f,
                BorderRounding = 5f,
            },
            BorderColorByWeight = true,
        };
        s.Favorite = new LabelStyleOverride
        {
            IconEnabled = true,
            Icon = SpriteIcon.Star5,
            IconTint = Color.FromArgb(255, 255, 215, 0),
            IconSize = 24f,
        };
        s.Special = new LabelStyleOverride
        {
            Text = new TextStyleOverride
            {
                OverrideScale = true, Scale = 1.4f,
                OverrideColor = true, Color = Color.FromArgb(255, 200, 80, 255),
                OverrideBg = true, BgEnabled = true, BgColor = Color.FromArgb(177, 0, 0, 0),
            },
            Icon = SpriteIcon.Exclamation,
            IconTint = Color.FromArgb(255, 200, 80, 255),
            IconSize = 72f,
        };
        foreach (var kv in DefaultContentStyles()) s.Content[kv.Key] = kv.Value;
        return s;
    }
}

#endregion

#region Waypoints + Tours

public class Waypoint
{

    public string Name { get; set; }
    public bool Show { get; set; }
    public bool AutoCreated { get; set; } = false;
    [JsonIgnore]
    public List<Node> PathFromStart { get; set; } = new List<Node>();
    [JsonIgnore]
    public int StepCount => PathFromStart?.Count > 0 ? PathFromStart.Count - 1 : -1;
    [JsonIgnore]
    public float PathWeight { get; set; }

    [JsonConverter(typeof(Vector2iConverter))]
    public Vector2i Coordinates;
    public Color Color { get; set; }

}

public class Tour
{
    public string Id { get; set; }
    public string Name { get; set; }
    public bool Show { get; set; } = true;
    public Color Color { get; set; }
    public List<TourStop> Stops { get; set; } = new();

    [JsonIgnore] public List<List<Node>> Segments { get; set; } = new();
    [JsonIgnore] public List<Node> ResolvedStops { get; set; } = new();
    [JsonIgnore] public List<string> Skipped { get; set; } = new();
    [JsonIgnore] public int BuiltVersion { get; set; } = -1;
}

public struct TourStop
{
    public int X { get; set; }
    public int Y { get; set; }
}

#endregion

#region Expeditions

public class Expedition
{
    public int Id;
    public string Kind = "Ocean";
    public Vector2i SpawnCoord;
    public Vector2i RegionCoord;
    public List<Vector2i> ButtonCoords = new();
    public List<Vector2i> MapCoords = new();
    public Dictionary<string, int> Rumors = new();
}

#endregion

#region Weight Profiles

public class MapTuning
{
    public float Weight { get; set; }
    public bool Highlight { get; set; } = true;
    public bool Special { get; set; }
    public bool Favorite { get; set; }
}

public class ContentTuning
{
    public float Weight { get; set; }
    public bool Highlight { get; set; } = true;
    public bool Favorite { get; set; }
}

public class Profile
{
    public ConcurrentDictionary<string, MapTuning> Maps { get; set; } = new();
    public ConcurrentDictionary<string, ContentTuning> Content { get; set; } = new();
    public ConcurrentDictionary<string, float> Biomes { get; set; } = new();
    public ConcurrentDictionary<string, float> Rumors { get; set; } = new();
    public ConcurrentDictionary<string, float> Foretellings { get; set; } = new();

    public LabelStyleSettings Labels { get; set; } = LabelStyleSettings.Defaults();

    public FeatureSettings Features { get; set; } = new();
    public GraphicSettings Graphics { get; set; } = new();
    public HotkeySettings Keybinds { get; set; } = new();
    public HackSettings Hacks { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public MapSettings MapAppearance { get; set; } = new();
    public WaypointSettings Waypoints { get; set; } = new();
    public TourSettings Tours { get; set; } = new();
    public AtlasOverviewSettings AtlasOverview { get; set; } = new();
    public ExpeditionSettings Expeditions { get; set; } = new();
    public ContentDisplaySettings ContentDisplay { get; set; } = new();
}

public class WeightExport
{
    public Dictionary<string, float> Maps { get; set; } = [];
    public Dictionary<string, float> Content { get; set; } = [];
    public Dictionary<string, float> Biomes { get; set; } = [];
    public Dictionary<string, float> Rumors { get; set; } = [];
}

#endregion
