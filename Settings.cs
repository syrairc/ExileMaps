using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ExileMaps.Classes;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using ExileCore2;

namespace ExileMaps;

public partial class ExileMapsCore
{
    #region Atlas Stats

    private struct AtlasStats
    {
        public int MapsRun;
        public int MapsCompleted;
        public int AtlasPointsTotal;
        public int AtlasPointsReachable;
        public int AtlasQuestsReachable;
        public int ReachableMaps;
        public Dictionary<string, (int count, Color color)> ContentCounts;
    }

    private static readonly Color ModifierTallyColor = Color.FromArgb(255, 230, 200, 110);

    private AtlasStats? cachedAtlasStats;

    #endregion

    #region Import / Export

    // everything that has to re-run when the active profile's VALUES change. hung off the store's
    // OnSwitch, so the profile bar's own switch/delete/copy-into gets it too.
    public void OnProfileApplied() {
        RebuildWeightEditorIds();
        RegisterHotkeys();
        RequestSpecialMapsRefresh();
        refreshCache = true;
        lastRefreshMs = long.MinValue / 2;
        weightsDirty = true;
    }

    // the store's seams. none of them serialize, so this runs again after an import swaps the
    // whole store out from under us.
    public void WireProfiles() {
        Settings.Profiles.Json = SettingsContainer.jsonSettings;
        Settings.Profiles.NameStem = () => GameController?.Game?.IngameState?.ServerData?.League;
        Settings.Profiles.OnSwitch = _ => OnProfileApplied();
        Settings.Profiles.Ensure();
    }

    // switch by name from an import path. Switch is a no-op when that name is already active, so
    // run the side effects here in that case - the values moved even though the name didn't.
    public void ApplyProfileSwitch(string name)
    {
        if (!Settings.Profiles.Switch(name)) OnProfileApplied();
    }

    public void ResetMapWeightsToDefaults() {
        foreach (var m in Settings.Active.Maps.Values) m.Weight = 0f;
        weightsDirty = true;
    }
    public void ResetContentWeightsToDefaults() {
        foreach (var c in Settings.Active.Content.Values) c.Weight = 0f;
        weightsDirty = true;
    }
    public void ResetBiomeWeightsToDefaults() { Settings.Active.Biomes.Clear(); weightsDirty = true; }

    private volatile Action pendingFileAction;
    private volatile bool fileDialogBusy;

    public void ExportSettings() => OpenFileDialogAsync("Export Settings", "exilemaps_settings.json", true, WriteSettings);
    public void ImportSettings() => OpenFileDialogAsync("Import Settings", null, false, ReadSettings);
    public void ExportProfile() => OpenFileDialogAsync("Export Profile", $"{Settings.Profiles.Active}.json", true, WriteProfile);
    public void ImportProfile() => OpenFileDialogAsync("Import Profile", null, false, ReadProfile);
    public void ExportWeights() => OpenFileDialogAsync("Export Weights", "exilemaps_weights.json", true, WriteWeights);
    public void ImportWeights() => OpenFileDialogAsync("Import Weights", null, false, ReadWeights);
    public void ImportLegacySettings() => OpenFileDialogAsync("Import Previous Version Settings", null, false, ConvertLegacySettings);

    public void ProcessPendingFileAction() {
        var action = pendingFileAction;
        if (action == null) return;
        pendingFileAction = null;
        action();
    }

    private void WriteSettings(string path) {
        try {
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented, SettingsContainer.jsonSettings);
            File.WriteAllText(path, json);
            LogMessage($"Exported settings to {path}");
        } catch (Exception e) {
            LogError("Error exporting settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void ReadSettings(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing settings: file not found ({path}).");
                return;
            }

            var json = File.ReadAllText(path);
            var imported = string.IsNullOrWhiteSpace(json) ? null
                : JsonConvert.DeserializeObject<ExileMapsSettings>(json, SettingsContainer.jsonSettings);
            if (imported?.Profiles?.Items == null || imported.Profiles.Items.Count == 0) {
                LogError("Error importing settings: no profiles in file.");
                return;
            }

            Settings.Profiles = imported.Profiles;
            WireProfiles();   // the imported store has no seams, they don't serialize
            ApplyProfileSwitch(Settings.Profiles.Active);
            LogMessage($"Imported settings from {path} ({Settings.Profiles.Items.Count} profiles)");
        } catch (Exception e) {
            LogError("Error importing settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void WriteProfile(string path) {
        try {
            if (!Settings.Profiles.Items.TryGetValue(Settings.Profiles.Active, out var profile)) {
                LogError("Error exporting profile: active profile not found.");
                return;
            }

            var json = JsonConvert.SerializeObject(profile, Formatting.Indented, SettingsContainer.jsonSettings);
            File.WriteAllText(path, json);
            LogMessage($"Exported profile '{Settings.Profiles.Active}' to {path}");
        } catch (Exception e) {
            LogError("Error exporting profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void ReadProfile(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing profile: file not found ({path}).");
                return;
            }

            var profile = JsonConvert.DeserializeObject<Profile>(File.ReadAllText(path), SettingsContainer.jsonSettings);
            if (profile == null) {
                LogError("Error importing profile: file was empty or invalid.");
                return;
            }

            string baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported Profile";
            string name = Settings.Profiles.Unique(baseName);

            Settings.Profiles.Items[name] = profile;
            ApplyProfileSwitch(name);

            LogMessage($"Imported profile '{name}' from {path} ({profile.Maps.Count} maps, {profile.Content.Count} content, {profile.Biomes.Count} biomes)");
        } catch (Exception e) {
            LogError("Error importing profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void WriteWeights(string path) {
        try {
            var active = Settings.Active;
            var export = new WeightExport {
                Maps = active.Maps.ToDictionary(kv => kv.Key, kv => kv.Value.Weight),
                Content = active.Content.ToDictionary(kv => kv.Key, kv => kv.Value.Weight),
                Biomes = new Dictionary<string, float>(active.Biomes),
                Rumors = new Dictionary<string, float>(active.Rumors),
            };
            var json = JsonConvert.SerializeObject(export, Formatting.Indented);
            File.WriteAllText(path, json);
            LogMessage($"Exported weights to {path}");
        } catch (Exception e) {
            LogError("Error exporting weights: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void ReadWeights(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing weights: file not found ({path}).");
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) {
                LogError("Error importing weights: file was empty or invalid.");
                return;
            }

            var import = JsonConvert.DeserializeObject<WeightExport>(json);
            if (import == null) {
                LogError("Error importing weights: file was empty or invalid.");
                return;
            }

            foreach (var kv in import.Maps)
                if (Settings.GameData.Maps.ContainsKey(kv.Key))
                    Settings.TuneMap(kv.Key).Weight = kv.Value;

            foreach (var kv in import.Content)
                if (Settings.GameData.Content.ContainsKey(kv.Key))
                    Settings.TuneContent(kv.Key).Weight = kv.Value;

            foreach (var kv in import.Biomes)
                if (Settings.GameData.Biomes.ContainsKey(kv.Key))
                    Settings.Active.Biomes[kv.Key] = kv.Value;

            foreach (var kv in import.Rumors)
                if (Settings.GameData.Rumors.ContainsKey(kv.Key))
                    Settings.Active.Rumors[kv.Key] = kv.Value;

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported weights from {path} ({import.Maps.Count} maps, {import.Content.Count} content, {import.Biomes.Count} biomes, {import.Rumors.Count} rumours)");
        } catch (Exception e) {
            LogError("Error importing weights: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private class LegacyMapEntry
    {
        public float Weight { get; set; }
        public bool Highlight { get; set; }
        public bool Favorite { get; set; }
        public bool ColorNodesByWeight { get; set; }
        public Color NodeColor { get; set; }
        public SpriteIcon Icon { get; set; }
    }

    private class LegacyContentEntry
    {
        public float Weight { get; set; }
        public bool Highlight { get; set; }
        public bool Favorite { get; set; }
    }

    private class LegacyBiomeEntry
    {
        public float Weight { get; set; }
    }

    private void ConvertLegacySettings(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing legacy settings: file not found ({path}).");
                return;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) {
                LogError("Error importing legacy settings: file was empty or invalid.");
                return;
            }

            var root = JObject.Parse(text);
            var data = root["data"] as JObject ?? root;
            var legacySerializer = JsonSerializer.Create(SettingsContainer.jsonSettings);

            Dictionary<string, Waypoint> legacyWaypoints = null;
            if (data["Waypoints"]?["Waypoints"] is JObject wpToken)
                legacyWaypoints = wpToken.ToObject<Dictionary<string, Waypoint>>(legacySerializer);

            Dictionary<string, Tour> legacyTours = null;
            if (data["Tours"]?["Tours"] is JObject tourToken)
                legacyTours = tourToken.ToObject<Dictionary<string, Tour>>(legacySerializer);

            var legacyProfiles = data["Profiles"]?["Profiles"] as JObject;
            if (legacyProfiles == null || legacyProfiles.Count == 0) {
                LogError("Error importing legacy settings: no profiles found in file.");
                return;
            }

            string lastImported = null;
            bool waypointsAttached = false;
            foreach (var prop in legacyProfiles.Properties()) {
                var entry = prop.Value as JObject;
                if (entry == null) continue;

                var profile = new Profile();

                if (entry["Maps"] is JObject mapsTok) {
                    var legacyMaps = mapsTok.ToObject<Dictionary<string, LegacyMapEntry>>(legacySerializer) ?? new();
                    foreach (var (id, m) in legacyMaps) {
                        profile.Maps[id] = new MapTuning {
                            Weight = m.Weight,
                            Highlight = m.Highlight,
                            Favorite = m.Favorite,
                        };

                        if (m.Icon != SpriteIcon.Circle || !m.ColorNodesByWeight)
                            profile.Labels.Maps[id] = new LabelStyleOverride {
                                MapIcon = m.Icon,
                                OverrideMapIconTint = !m.ColorNodesByWeight,
                                MapIconTint = m.NodeColor,
                            };
                    }
                }

                if (entry["Content"] is JObject contentTok) {
                    var legacyContent = contentTok.ToObject<Dictionary<string, LegacyContentEntry>>(legacySerializer) ?? new();
                    foreach (var (id, c) in legacyContent)
                        profile.Content[id] = new ContentTuning {
                            Weight = c.Weight,
                            Highlight = c.Highlight,
                            Favorite = c.Favorite,
                        };
                }

                if (entry["Biomes"] is JObject biomesTok) {
                    var legacyBiomes = biomesTok.ToObject<Dictionary<string, LegacyBiomeEntry>>(legacySerializer) ?? new();
                    foreach (var (id, b) in legacyBiomes)
                        profile.Biomes[id] = b.Weight;
                }

                if (!waypointsAttached) {
                    if (legacyWaypoints != null)
                        foreach (var kv in legacyWaypoints)
                            profile.Waypoints.Waypoints[kv.Key] = kv.Value;
                    if (legacyTours != null)
                        foreach (var kv in legacyTours)
                            profile.Tours.Tours[kv.Key] = kv.Value;
                    waypointsAttached = true;
                }

                string name = Settings.Profiles.Unique(prop.Name + " (imported)");
                Settings.Profiles.Items[name] = profile;
                lastImported = name;
            }

            if (lastImported == null) {
                LogError("Error importing legacy settings: no usable profiles found.");
                return;
            }

            ApplyProfileSwitch(lastImported);
            LogMessage($"Imported legacy settings from {path} into profile '{lastImported}'" +
                (legacyProfiles.Count > 1 ? $" (+{legacyProfiles.Count - 1} more)" : "") +
                ". Waypoints/tours attached to the first imported profile only. Graphics/Features/" +
                "Keybinds/Hacks/Search/appearance were not carried over - they use this plugin's defaults.");
        } catch (Exception e) {
            LogError("Error importing legacy settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private void BackupLegacySettingsOnce() {
        if (Settings.SettingsBackupDone)
            return;

        try {
            string fileName = Name + "_settings.json";
            string path = Path.Combine(ConfigDirectory, fileName);
            if (!File.Exists(path))
                path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", "global", fileName);

            if (!File.Exists(path)) {
                LogMessage($"No prior settings file found at {path} - nothing to back up.");
                Settings.SettingsBackupDone = true;
                return;
            }

            string json = File.ReadAllText(path);
            string backup = Path.Combine(Path.GetDirectoryName(path), Name + "_settings.pre-rework-backup.json");
            if (!File.Exists(backup))
                File.WriteAllText(backup, json);

            Settings.SettingsBackupDone = true;
            LogMessage($"Backed up settings to {backup} before this version's first run.");

            if (!Settings.LegacyImportHandled && LooksLikeLegacySettings(json)) {
                legacyBackupPath = backup;
                legacyImportPending = true;
            }
        } catch (Exception e) {
            LogError("Error backing up settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private static bool LooksLikeLegacySettings(string json) {
        try {
            var root = JObject.Parse(json);
            var data = root["data"] as JObject ?? root;
            return data["Profiles"] != null
                && (data["Waypoints"] != null || data["Tours"] != null || data["Graphics"] != null);
        } catch {
            return false;
        }
    }

    private void OpenFileDialogAsync(string title, string defaultName, bool save, Action<string> apply) {
        if (fileDialogBusy) return;
        fileDialogBusy = true;
        var thread = new System.Threading.Thread(() => {
            try {
                string chosen = save
                    ? NativeFileDialog.ShowSave(title, defaultName, DirectoryFullName)
                    : NativeFileDialog.ShowOpen(title, DirectoryFullName);
                if (!string.IsNullOrEmpty(chosen))
                    pendingFileAction = () => apply(chosen);
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                fileDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    #endregion

}

#region Settings Root

public class GameData
{
    public Dictionary<string, MapInfo> Maps { get; set; } = new();
    public Dictionary<string, ContentInfo> Content { get; set; } = new();
    public Dictionary<string, BiomeInfo> Biomes { get; set; } = new();
    public Dictionary<string, RumorInfo> Rumors { get; set; } = new();
    public Dictionary<string, string> Foretellings { get; set; } = new();
}

public class ExileMapsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public bool SettingsBackupDone { get; set; } = false;
    public bool LegacyImportHandled { get; set; } = false;

    [JsonIgnore]
    public GameData GameData { get; set; } = new();

    // the store memoizes this, so it's a dictionary hit only when the active profile moves
    [JsonIgnore] public Profile Active => Profiles.Current;

    public MapTuning TuneMap(string id) => Active.Maps.GetOrAdd(id, _ => new MapTuning());

    public ContentTuning TuneContent(string id) => Active.Content.GetOrAdd(id, _ => new ContentTuning());

    public float BiomeWeight(string id) => id != null && Active.Biomes.TryGetValue(id, out float w) ? w : 0f;

    public float RumorWeight(string id) => id != null && Active.Rumors.TryGetValue(id, out float w) ? w : 0f;

    public float ForetellingWeight(string id) => id != null && Active.Foretellings.TryGetValue(id, out float w) ? w : 0f;

    private static readonly MapTuning UntunedMap = new();
    private static readonly ContentTuning UntunedContent = new();
    public MapTuning ReadMap(string id) => id != null && Active.Maps.TryGetValue(id, out var t) ? t : UntunedMap;

    public ContentTuning ReadContent(string id) => id != null && Active.Content.TryGetValue(id, out var t) ? t : UntunedContent;

    // wire names are pinned to ActiveProfile/Profiles inside the store, so this is the same json
    // shape the old hand-rolled ProfileSettings wrote - existing profiles load untouched.
    public ExileImGui2.Profiles<Profile> Profiles { get; set; } = new();

    [JsonIgnore] public FeatureSettings Features => Active.Features;
    [JsonIgnore] public ExpeditionSettings Expeditions => Active.Expeditions;
    [JsonIgnore] public ContentDisplaySettings ContentDisplay => Active.ContentDisplay;
    [JsonIgnore] public HotkeySettings Keybinds => Active.Keybinds;
    [JsonIgnore] public GraphicSettings Graphics => Active.Graphics;

    [JsonIgnore] public LabelStyleSettings Labels => Active.Labels;

    [JsonIgnore] public AtlasOverviewSettings AtlasOverview => Active.AtlasOverview;

    [JsonIgnore] public TourSettings Tours => Active.Tours;

    [JsonIgnore] public MapSettings Maps => Active.MapAppearance;
    [JsonIgnore] public WaypointSettings Waypoints => Active.Waypoints;

    [JsonIgnore] public HackSettings Hacks => Active.Hacks;

    [JsonIgnore] public SearchSettings Search => Active.Search;
}

public class SearchSettings
{
    public bool HookIngameSearch = true;

    public bool ShowSearchBox = false;

    public System.Numerics.Vector2 BoxPos = System.Numerics.Vector2.Zero;

    public System.Numerics.Vector2 ResultsOffset = System.Numerics.Vector2.Zero;

    public int MaxResults = 10;

    public bool HighlightMatches = true;

    public ColorNode HighlightColor { get; set; } = new ColorNode(Color.FromArgb(255, 255, 210, 90));

    public float HighlightSize = 0.45f;
}

public class HackSettings
{
    public bool EnableMemoryWrites { get; set; } = false;

    public bool AtlasZoom { get; set; } = false;
    public bool AtlasFog { get; set; } = false;
    public bool AtlasFogAll { get; set; } = false;
    public bool AtlasCameraPan { get; set; } = false;

    public float ZoomOutFactor { get; set; } = 1.7f;
    public float ZoomInFactor { get; set; } = 2f;

    public float PanSpeed { get; set; } = 10f;

    public int VerifyEveryFrames { get; set; } = 60;
}

#endregion

#region Feature + Hotkey Settings

public class FeatureSettings
{
    public bool EnableDrawing = true;

    public bool DebugMode = false;

    public bool ShowPerfMonitor = false;

    public bool DebugAtlasButtons = false;

    public bool DebugRitualRolls = false;
    public bool ShowRitualForetellings = false;
    public bool RitualPlanner = false;
    public int RitualPlanSteps = 6;
    public bool RitualPlanUnlockedOnly = false;
    public System.Numerics.Vector2 RitualPanelPos = new System.Numerics.Vector2(20f, 180f);

    public bool ShowAtlasButton = true;

    public System.Numerics.Vector2 AtlasButtonPos = System.Numerics.Vector2.Zero;

    public ExpeditionMarkers ExpeditionMarkers { get; set; } = ExpeditionMarkers.Nearest;

}

public enum ExpeditionMarkers { Off, Nearest, All }
public class HotkeySettings
{

    public HotkeyNodeV2 ToggleDrawingHotkey { get; set; } = new HotkeyNodeV2(Keys.Scroll);

    public HotkeyNodeV2 RefreshMapCacheHotkey { get; set; } = new HotkeyNodeV2(Keys.Home);

    public HotkeyNodeV2 QuickEditNodeHotkey { get; set; } = new HotkeyNodeV2(Keys.Multiply);

    public HotkeyNodeV2 ToggleAtlasOverviewHotkey { get; set; } = new HotkeyNodeV2(Keys.Pause);

    public HotkeyNodeV2 ToggleWaypointPanelHotkey { get; set; } = new HotkeyNodeV2(Keys.End);

    public HotkeyNodeV2 ToggleToursPanelHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    public HotkeyNodeV2 AddTourStopHotkey { get; set; } = new HotkeyNodeV2(Keys.NumPad7);

    public HotkeyNodeV2 BuildModeHotkey { get; set; } = new HotkeyNodeV2(Keys.NumPad8);

    public HotkeyNodeV2 AddWaypointHotkey { get; set; } = new HotkeyNodeV2(Keys.Insert);

    public HotkeyNodeV2 DeleteWaypointHotkey { get; set; } = new HotkeyNodeV2(Keys.Delete);
}

[Flags]
public enum NodeStates
{
    None     = 0,
    Visited  = 1,
    Unlocked = 2,
    Locked   = 4,
    Hidden   = 8,
}

#endregion

#region Graphics Settings

public class GraphicSettings
{

    public NodeStates DrawNodes { get; set; } = NodeStates.Visited | NodeStates.Unlocked | NodeStates.Locked | NodeStates.Hidden;
    public NodeStates DrawNames { get; set; } = NodeStates.Visited | NodeStates.Unlocked | NodeStates.Locked | NodeStates.Hidden;
    public NodeStates DrawLines { get; set; } = NodeStates.Visited | NodeStates.Unlocked | NodeStates.Locked | NodeStates.Hidden;

    public int MapCacheRefreshRate = 1;

    public float NodeRadius = 1.856f;

    public bool UseNodeIcons = true;

    public bool ContentIconsSkipGameDrawn = true;

    public float IconSize { get; set; } = 28f;

    public bool ShowContentRow = true;

    public bool ShowBiomeIcon = true;

    public bool DrawWeightOnMap = false;

    public float MapLineWidth = 4.0f;

    public bool DrawGradientLines = true;

    public ColorNode VisitedLineColor { get; set; } = new ColorNode(Color.FromArgb(77, 128, 128, 128));

    public ColorNode UnlockedLineColor { get; set; } = new ColorNode(Color.FromArgb(128, 18, 228, 18));

    public ColorNode LockedLineColor { get; set; } = new ColorNode(Color.FromArgb(128, 149, 20, 20));

    public bool ShowConnectionLines = true;

    public bool UseGameConnectionCurves = true;

    public bool ShowPaths = true;

    public float WaypointLineWidth = 5.051f;

    public float WaypointDashLength = 20.0f;

    public float WaypointDashGap = 10.0f;

    public float WaypointArrowMinDistance = 641.454f;

    public bool ShowAtlasQuestIndicator = false;

    public bool ShowSpecialMapIndicator = true;

    public bool HideCompletedSpecialMaps = true;

    public int MapNameOffsetY = 30;

    public bool UppercaseMapNames = true;

    public bool ScaleLabelsWithZoom = true;

    public float MinZoomScale = 0.8f;

    public float ZoomScaleStart = 0.6f;

}

#endregion

#region Panel Settings

public class AtlasOverviewSettings
{
    public int ReachableSteps = 5;
}

public class TourSettings
{
    public bool ShowTours = true;

    public bool ShowNextStopTarget = true;

    public string ActiveTourId { get; set; } = "";

    public Dictionary<string, Tour> Tours { get; set; } = [];

    public List<string> AutoTourContent { get; set; } = new();

    public int AutoTourReach { get; set; } = 5;

    public bool WeightAwareRouting { get; set; } = false;

    public float ExtraMapCost { get; set; } = 25f;
}

#endregion

#region Map, Biome + Content Settings

public enum NodeColorMode { Weight, Status, Static }

public class ContentDisplaySettings
{
    public static readonly string[] AtlasPointTypes = { "Breach", "Abyss", "Incursion", "Delirium", "Ritual", "Expedition" };

    public bool ShowGenericAtlasPoint { get; set; } = true;

    public bool ColorRumorsByWeight { get; set; } = true;

    public bool ShowRitualMarkers { get; set; } = true;

    public Dictionary<string, bool> AtlasPointIcons { get; set; } = new();

    public bool ShowAtlasPoint(string type) =>
        string.IsNullOrEmpty(type)
            ? ShowGenericAtlasPoint
            : !AtlasPointIcons.TryGetValue(type, out var on) || on;
}

public class MapSettings
{
    public NodeColorMode NodeColorMode { get; set; } = NodeColorMode.Weight;

    public Color GoodNodeColor { get; set; } = Color.FromArgb(80, 111, 207, 122);
    public Color NeutralNodeColor { get; set; } = Color.FromArgb(80, 217, 193, 103);
    public Color BadNodeColor { get; set; } = Color.FromArgb(80, 224, 103, 103);

    public Color VisitedNodeColor { get; set; } = Color.FromArgb(80, 111, 207, 122);
    public Color UnlockedNodeColor { get; set; } = Color.FromArgb(80, 217, 193, 103);
    public Color LockedNodeColor { get; set; } = Color.FromArgb(80, 224, 103, 103);
    public Color HiddenNodeColor { get; set; } = Color.FromArgb(80, 130, 130, 130);

    public Color StaticNodeColor { get; set; } = Color.FromArgb(80, 200, 200, 200);

    public SpecialMapSettings SpecialMaps { get; set; } = new SpecialMapSettings();
}

public class SpecialMapSettings
{
    public bool UseMaxWeight { get; set; } = true;
    public float MaxWeight { get; set; } = 50f;
}

#endregion

#region Waypoint Settings

public class WaypointSettings
{
    public bool ShowWaypoints { get; set; } = true;
    public bool ShowWaypointArrows { get; set; } = true;
    public bool InverWaypointArrowsColors { get; set; } = true;
    public bool AutoWaypointFavorites { get; set; } = false;
    public bool AutoWaypointNearestOnly { get; set; } = true;
    public bool AutoRemoveCompletedWaypoints { get; set; } = true;
    public bool WeightAwareRouting { get; set; } = false;
    public float ExtraMapCost { get; set; } = 25f;

    public Dictionary<string, Waypoint> Waypoints { get; set; } = [];
}

#endregion

#region Expedition Settings

public class ExpeditionSettings
{
    public ColorNode HighlightColor { get; set; } = new ColorNode(Color.FromArgb(220, 90, 200, 255));
}

#endregion

#region Graphics Presets

public enum GraphicsPreset { Minimal, Normal, Maximum }

public static class GraphicsPresets
{
    public const int IconAlpha = 215;

    public static readonly string[] Names = { "Minimal", "Normal", "Maximum" };

    public static string Describe(GraphicsPreset p) => p switch {
        GraphicsPreset.Minimal => "Thin flat lines, no gradients or curves, no extra icons, no style overrides.",
        GraphicsPreset.Maximum => "Everything on. Curves, gradients, every icon, stroked labels.",
        _ => "Defaults minus the heaviest effects.",
    };

    public static void Apply(Profile p, GraphicsPreset preset)
    {
        var g = p.Graphics;
        var t = p.Labels.Base.Text;
        var c = p.ContentDisplay;
        var f = p.Features;
        var s = p.Search;

        g.ShowConnectionLines = true;
        g.ShowPaths = true;
        g.ScaleLabelsWithZoom = true;
        g.ShowSpecialMapIndicator = true;

        switch (preset)
        {
            case GraphicsPreset.Minimal:
                g.MapCacheRefreshRate = 3;
                g.UseNodeIcons = false;
                g.ShowContentRow = false;
                g.ShowBiomeIcon = false;
                g.DrawWeightOnMap = false;
                g.ShowAtlasQuestIndicator = false;
                g.UppercaseMapNames = false;
                g.MapLineWidth = 2f;
                g.DrawGradientLines = false;
                g.UseGameConnectionCurves = false;
                g.IconSize = 22f;
                p.MapAppearance.NodeColorMode = NodeColorMode.Status;
                t.Scale = 1f;
                t.Color = Color.FromArgb(255, 255, 255, 255);
                t.StrokeEnabled = false;
                t.BgEnabled = true;
                t.BgColor = Color.FromArgb(255, 0, 0, 0);
                t.BorderEnabled = false;
                p.Labels.Base.TextColorByWeight = false;
                p.Labels.Base.BoxColorByWeight = false;
                p.Labels.Base.BorderColorByWeight = false;
                SetOverridesEnabled(p.Labels, false);
                SetAtlasPointIcons(c, false);
                c.ShowGenericAtlasPoint = false;
                c.ColorRumorsByWeight = false;
                f.ExpeditionMarkers = ExpeditionMarkers.Nearest;
                c.ShowRitualMarkers = true;
                s.HighlightMatches = false;
                break;

            case GraphicsPreset.Normal:
                g.MapCacheRefreshRate = 1;
                g.UseNodeIcons = true;
                g.ShowContentRow = true;
                g.ContentIconsSkipGameDrawn = true;
                g.ShowBiomeIcon = false;
                g.DrawWeightOnMap = false;
                g.ShowAtlasQuestIndicator = true;
                g.UppercaseMapNames = true;
                g.MapLineWidth = 3f;
                g.DrawGradientLines = false;
                g.UseGameConnectionCurves = true;
                g.IconSize = 28f;
                t.StrokeEnabled = false;
                t.BgEnabled = true;
                t.BorderEnabled = false;
                SetOverridesEnabled(p.Labels, true);
                SetAtlasPointIcons(c, true);
                c.ShowGenericAtlasPoint = true;
                c.ColorRumorsByWeight = true;
                f.ExpeditionMarkers = ExpeditionMarkers.Nearest;
                c.ShowRitualMarkers = true;
                s.HighlightMatches = true;
                break;

            case GraphicsPreset.Maximum:
                g.MapCacheRefreshRate = 1;
                g.UseNodeIcons = true;
                g.ShowContentRow = true;
                g.ContentIconsSkipGameDrawn = true;
                g.ShowBiomeIcon = true;
                g.DrawWeightOnMap = true;
                g.ShowAtlasQuestIndicator = true;
                g.UppercaseMapNames = true;
                g.MapLineWidth = 3f;
                g.DrawGradientLines = true;
                g.UseGameConnectionCurves = true;
                g.IconSize = 32f;
                t.StrokeEnabled = true;
                t.BgEnabled = true;
                t.BorderEnabled = true;
                SetOverridesEnabled(p.Labels, true);
                SetAtlasPointIcons(c, true);
                c.ShowGenericAtlasPoint = true;
                c.ColorRumorsByWeight = true;
                f.ExpeditionMarkers = ExpeditionMarkers.Nearest;
                c.ShowRitualMarkers = true;
                s.HighlightMatches = true;
                break;
        }

        ApplyIconAlpha(p);
    }

    private static void SetOverridesEnabled(LabelStyleSettings labels, bool on)
    {
        foreach (var ov in labels.Content.Values) ov.Enabled = on;
        foreach (var ov in labels.Maps.Values) ov.Enabled = on;
    }

    private static void SetAtlasPointIcons(ContentDisplaySettings c, bool on)
    {
        foreach (var type in ContentDisplaySettings.AtlasPointTypes)
            c.AtlasPointIcons[type] = on;
    }

    private static Color Alpha(Color c) => Color.FromArgb(IconAlpha, c.R, c.G, c.B);

    private static void ApplyIconAlpha(Profile p)
    {
        var m = p.MapAppearance;
        m.GoodNodeColor = Alpha(m.GoodNodeColor);
        m.NeutralNodeColor = Alpha(m.NeutralNodeColor);
        m.BadNodeColor = Alpha(m.BadNodeColor);
        m.VisitedNodeColor = Alpha(m.VisitedNodeColor);
        m.UnlockedNodeColor = Alpha(m.UnlockedNodeColor);
        m.LockedNodeColor = Alpha(m.LockedNodeColor);
        m.HiddenNodeColor = Alpha(m.HiddenNodeColor);
        m.StaticNodeColor = Alpha(m.StaticNodeColor);

        AlphaIcons(p.Labels.Favorite);
        AlphaIcons(p.Labels.Special);
        foreach (var ov in p.Labels.Content.Values) AlphaIcons(ov);
        foreach (var ov in p.Labels.Maps.Values) AlphaIcons(ov);
    }

    private static void AlphaIcons(LabelStyleOverride ov)
    {
        ov.IconTint = Alpha(ov.IconTint);
        ov.MapIconTint = Alpha(ov.MapIconTint);
    }
}

#endregion
