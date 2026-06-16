// Settings I/O: profile management, import/export, v1 migration, file dialogs.
using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExileCore2;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Forces a cache refresh so node weights/colors update immediately after a profile is loaded.
    public void OnProfileApplied() {
        refreshCache = true;
        lastRefresh = DateTime.MinValue;
    }

    // Full plugin-defaults reset: weights AND content colors, persisted so it survives reload.
    public void ResetWeightsToDefaults() {
        LoadDefaultWeights();
        ResetContentColorsToDefaults();
        Settings.Profiles.SaveCurrentProfile();
        weightsDirty = true;
        refreshCache = true;
    }

    // Per-section "Reset All Weights": restores bundled default weights for that section only.
    public void ResetMapWeightsToDefaults()     { ApplyDefaultWeights(true, false, false); Settings.Profiles.SaveCurrentProfile(); weightsDirty = true; }
    public void ResetContentWeightsToDefaults() { ApplyDefaultWeights(false, true, false); Settings.Profiles.SaveCurrentProfile(); weightsDirty = true; }
    public void ResetBiomeWeightsToDefaults()   { ApplyDefaultWeights(false, false, true); Settings.Profiles.SaveCurrentProfile(); weightsDirty = true; }

    // Restores every content type's color to its per-mechanic plugin default. Colors persist via the
    // ExileCore settings store (ContentTypes), so no separate save is needed here.
    public void ResetContentColorsToDefaults() {
        foreach (var (id, content) in Settings.Maps.Content.ContentTypes)
            content.Color = ContentDefaultColor(id);
    }

    public void LoadDefaultWeights() => ApplyDefaultWeights(true, true, true);

    // Loads bundled default weights from exilemaps_weights.json, limited to the requested sections.
    private void ApplyDefaultWeights(bool maps, bool content, bool biomes) {
        try {
            var weightsPath = Path.Combine(DirectoryFullName, "json\\exilemaps_weights.json");
            if (!File.Exists(weightsPath))
                return;

            var export = JsonSerializer.Deserialize<WeightExport>(File.ReadAllText(weightsPath));

            if (maps && export?.Maps != null)
                foreach (var kvp in export.Maps)
                    if (Settings.Maps.Maps.TryGetValue(kvp.Key, out var map))
                        map.Weight = kvp.Value;

            if (content && export?.Content != null)
                foreach (var kvp in export.Content)
                    if (Settings.Maps.Content.ContentTypes.TryGetValue(kvp.Key, out var c))
                        c.Weight = kvp.Value;

            if (biomes && export?.Biomes != null)
                foreach (var kvp in export.Biomes)
                    if (Settings.Maps.Biomes.Biomes.TryGetValue(kvp.Key, out var biome))
                        biome.Weight = kvp.Value;
        } catch (Exception e) {
            LogError("Error loading default weights: " + e.Message);
        }
    }

    // First run: seeds from bundled defaults and captures into the active profile.
    // Subsequent runs: overlays the active profile onto live data.
    private void SeedProfiles() {
        try {
            var profiles = Settings.Profiles;
            profiles.EnsureDefaultProfile();

            if (!profiles.Profiles.TryGetValue(profiles.ActiveProfile, out var active))
                return;

            if (active.Maps.Count == 0 && active.Content.Count == 0 && active.Biomes.Count == 0) {
                LoadDefaultWeights();
                profiles.SaveCurrentProfile();
            } else {
                profiles.LoadProfile(profiles.ActiveProfile);
            }

            weightsDirty = true;
        } catch (Exception e) {
            LogError("Error seeding profiles: " + e.Message);
        }
    }

    private volatile string pendingExportSettingsPath;
    private volatile string pendingImportSettingsPath;
    private volatile string pendingExportProfilePath;
    private volatile string pendingImportProfilePath;
    private volatile string pendingImportOldSettingsPath;
    // Set by the auto-detect banner; null means name after the file (manual import).
    private volatile string pendingImportOldSettingsName;
    private volatile bool weightDialogBusy;

    // Set when v1 settings detected at startup; cleared when the banner is dismissed or acted on.
    private bool oldSettingsPendingImport = false;
    private string oldSettingsBackupPath;
    public bool OldSettingsPendingImport => oldSettingsPendingImport;

    public void ExportSettings() => OpenSettingsFileDialogAsync(save: true);
    public void ImportSettings() => OpenSettingsFileDialogAsync(save: false);

    public void ExportProfile() => OpenProfileFileDialogAsync(save: true);
    public void ImportProfile() => OpenProfileFileDialogAsync(save: false);

    public void ImportOldSettings() => OpenOldSettingsFileDialogAsync();

    // Detects v1 settings at load. Backs up before ExileCore rewrites on next save, then raises the import banner.
    private void DetectOldSettings() {
        try {
            if (Settings.Profiles.OldSettingsMigrationHandled)
                return;

            var path = FindSettingsFilePath();
            if (path == null || !File.Exists(path))
                return;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text) || !LooksLikeOldSettings(text))
                return;

            // Preserve the v1 data before ExileCore overwrites the live file on the next save.
            var dir = Path.GetDirectoryName(path);
            var backup = Path.Combine(dir, "ExileMaps_settings.v1.bak.json");
            File.Copy(path, backup, overwrite: true);
            oldSettingsBackupPath = backup;
            oldSettingsPendingImport = true;
            LogMessage($"Detected pre-rework settings; backed up to {backup}. Import available in plugin settings.");
        } catch (Exception e) {
            LogError("Error detecting old settings: " + e.Message);
        }
    }

    // Locates this plugin's settings file: <ConfigDirectory>/<InternalName>_settings.json, falling back to any *_settings.json match.
    private string FindSettingsFilePath() {
        try {
            var dir = ConfigDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            var primary = Path.Combine(dir, InternalName + "_settings.json");
            if (File.Exists(primary))
                return primary;
            var matches = Directory.GetFiles(dir, "*_settings.json");
            return matches.FirstOrDefault(f => Path.GetFileName(f).Contains("ExileMaps", StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
        } catch {
            return null;
        }
    }

    // v1 settings have top-level "MapTypes", "MapContent", or "MapMods" keys (wrapped in "data" on disk).
    private static bool LooksLikeOldSettings(string json) {
        try {
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var data = root["data"] as Newtonsoft.Json.Linq.JObject ?? root;
            return data["MapTypes"] != null || data["MapContent"] != null || data["MapMods"] != null;
        } catch {
            return false;
        }
    }

    // Queues the auto-detected v1 import for the next render frame, then marks the migration done.
    public void ImportDetectedOldSettings() {
        if (oldSettingsBackupPath != null) {
            pendingImportOldSettingsName = "Original Settings";
            pendingImportOldSettingsPath = oldSettingsBackupPath;
        }
        oldSettingsPendingImport = false;
        Settings.Profiles.OldSettingsMigrationHandled = true;
    }

    // Dismisses the banner. The .v1.bak file remains so the manual import button still works.
    public void DismissDetectedOldSettings() {
        oldSettingsPendingImport = false;
        Settings.Profiles.OldSettingsMigrationHandled = true;
    }

    // Applies any path the dialog thread produced. Called from Render so settings access stays single-threaded.
    public void ProcessPendingWeightFile() {
        var exportSettings = pendingExportSettingsPath;
        if (exportSettings != null) {
            pendingExportSettingsPath = null;
            WriteSettings(exportSettings);
        }

        var importSettings = pendingImportSettingsPath;
        if (importSettings != null) {
            pendingImportSettingsPath = null;
            ReadSettings(importSettings);
        }

        var exportProfile = pendingExportProfilePath;
        if (exportProfile != null) {
            pendingExportProfilePath = null;
            WriteProfile(exportProfile);
        }

        var importProfile = pendingImportProfilePath;
        if (importProfile != null) {
            pendingImportProfilePath = null;
            ReadProfile(importProfile);
        }

        var importOld = pendingImportOldSettingsPath;
        if (importOld != null) {
            pendingImportOldSettingsPath = null;
            var importOldName = pendingImportOldSettingsName;
            pendingImportOldSettingsName = null;
            ConvertOldSettings(importOld, importOldName);
        }
    }

    // Serializes entire settings with Newtonsoft (matching ExileCore2's serializer). UI-only fields are [JsonIgnore].
    private void WriteSettings(string path) {
        try {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
            LogMessage($"Exported settings to {path}");
        } catch (Exception e) {
            LogError("Error exporting settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports via PopulateObject to mutate the live Settings instance in place; dictionary entries merge by key.
    private void ReadSettings(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing settings: file not found ({path}).");
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) {
                LogError("Error importing settings: file was empty or invalid.");
                return;
            }

            Newtonsoft.Json.JsonConvert.PopulateObject(json, Settings);

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported settings from {path}");
        } catch (Exception e) {
            LogError("Error importing settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Exports the active profile as JSON. Calls SaveCurrentProfile first so unsaved live edits are captured.
    private void WriteProfile(string path) {
        try {
            Settings.Profiles.SaveCurrentProfile();
            if (!Settings.Profiles.Profiles.TryGetValue(Settings.Profiles.ActiveProfile, out var profile)) {
                LogError("Error exporting profile: active profile not found.");
                return;
            }

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            LogMessage($"Exported profile '{Settings.Profiles.ActiveProfile}' to {path}");
        } catch (Exception e) {
            LogError("Error exporting profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports a profile JSON, names it after the file (deduped), adds it, and switches to it.
    private void ReadProfile(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing profile: file not found ({path}).");
                return;
            }

            var profile = JsonSerializer.Deserialize<WeightProfile>(File.ReadAllText(path));
            if (profile == null) {
                LogError("Error importing profile: file was empty or invalid.");
                return;
            }

            // Name the profile after the file, deduped against existing names.
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported Profile";
            string name = baseName;
            int i = 2;
            while (Settings.Profiles.Profiles.ContainsKey(name))
                name = $"{baseName} {i++}";

            Settings.Profiles.SaveCurrentProfile();
            Settings.Profiles.Profiles[name] = profile;
            Settings.Profiles.ActiveProfile = name;
            Settings.Profiles.LoadProfile(name);

            weightsDirty = true;
            LogMessage($"Imported profile '{name}' from {path} ({profile.Maps.Count} maps, {profile.Content.Count} content, {profile.Biomes.Count} biomes)");
        } catch (Exception e) {
            LogError("Error importing profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Converts a v1 settings file. Non-weight categories apply directly; map/content/biome weights are captured into a new profile.
    private void ConvertOldSettings(string path, string profileName = null) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing old settings: file not found ({path}).");
                return;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) {
                LogError("Error importing old settings: file was empty or invalid.");
                return;
            }

            var root = Newtonsoft.Json.Linq.JObject.Parse(text);
            // On-disk settings wrap everything in "data"; a hand-made/exported file may not.
            var data = root["data"] as Newtonsoft.Json.Linq.JObject ?? root;

            // Preserve any unsaved live edits to the current active profile before we overwrite live state.
            Settings.Profiles.SaveCurrentProfile();

            // Non-weight categories apply directly to the live settings objects.
            PopulateOldSection(data["Enable"], Settings.Enable, "Enable");
            PopulateOldSection(data["Features"], Settings.Features, "Features");
            PopulateOldSection(data["Graphics"], Settings.Graphics, "Graphics");
            PopulateOldSection(data["Waypoints"], Settings.Waypoints, "Waypoints");
            ConvertOldKeybinds(data["Keybinds"] as Newtonsoft.Json.Linq.JObject);

            // Reshape MapTypes + the formerly top-level Biomes/MapContent into the current Maps object.
            if (data["MapTypes"] is Newtonsoft.Json.Linq.JObject mapTypes) {
                var mapsObj = (Newtonsoft.Json.Linq.JObject)mapTypes.DeepClone();
                if (data["Biomes"] is Newtonsoft.Json.Linq.JObject oldBiomes)
                    mapsObj["Biomes"] = oldBiomes;
                if (data["MapContent"] is Newtonsoft.Json.Linq.JObject oldContent)
                    mapsObj["Content"] = oldContent;
                PopulateOldSection(mapsObj, Settings.Maps, "Maps");
            }

            // Capture the just-imported map/content/biome state into a new profile and switch to it.
            // Auto-detect import passes an explicit name ("Original Settings"); the manual file-picker
            // leaves it null, so the profile is named after the chosen file.
            string baseName = profileName;
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported";
            string name = baseName;
            int i = 2;
            while (Settings.Profiles.Profiles.ContainsKey(name))
                name = $"{baseName} {i++}";

            Settings.Profiles.Profiles[name] = new WeightProfile();
            Settings.Profiles.ActiveProfile = name;
            Settings.Profiles.SaveCurrentProfile(); // snapshot live (imported) -> the new profile

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported old settings from {path} into new profile '{name}'.");
        } catch (Exception e) {
            LogError("Error importing old settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Populates one old-settings section in isolation using ExileCore2's serializer (handles ColorNode/ToggleNode).
    private void PopulateOldSection(Newtonsoft.Json.Linq.JToken token, object target, string label) {
        if (token == null)
            return;
        try {
            Newtonsoft.Json.JsonConvert.PopulateObject(token.ToString(), target, SettingsContainer.jsonSettings);
        } catch (Exception e) {
            LogError($"Error importing old settings section '{label}': {e.Message}");
        }
    }

    // Translates old int-keyed binds (WinForms Keys values) to HotkeyNodeV2 shape.
    private void ConvertOldKeybinds(Newtonsoft.Json.Linq.JObject oldKeybinds) {
        if (oldKeybinds == null)
            return;
        try {
            var converted = new Newtonsoft.Json.Linq.JObject();
            foreach (var prop in oldKeybinds.Properties()) {
                var valTok = prop.Value?["Value"];
                if (valTok == null)
                    continue;
                int vk = valTok.ToObject<int>();
                string keyName = ((System.Windows.Forms.Keys)vk).ToString();
                converted[prop.Name] = new Newtonsoft.Json.Linq.JObject {
                    ["ValueV2"] = new Newtonsoft.Json.Linq.JObject {
                        ["Key"] = keyName,
                        ["ControllerKey"] = "None",
                        ["ControllerModifierKey"] = "None",
                        ["Win"] = false
                    }
                };
            }
            Newtonsoft.Json.JsonConvert.PopulateObject(converted.ToString(), Settings.Keybinds, SettingsContainer.jsonSettings);
        } catch (Exception e) {
            LogError($"Error importing old settings section 'Keybinds': {e.Message}");
        }
    }

    // Opens a file-open dialog on a background STA thread for a v1 settings file.
    private void OpenOldSettingsFileDialogAsync() {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string chosen = NativeFileDialog.ShowOpen("Import Old Settings", DirectoryFullName);
                if (!string.IsNullOrEmpty(chosen))
                    pendingImportOldSettingsPath = chosen;
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    // Opens a native save/open dialog on a background STA thread. Path picked up by ProcessPendingWeightFile on the next frame.
    private void OpenSettingsFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Settings", "exilemaps_settings.json", initialDir)
                    : NativeFileDialog.ShowOpen("Import Settings", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportSettingsPath = chosen;
                    else pendingImportSettingsPath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    private void OpenProfileFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string defaultName = $"{Settings.Profiles.ActiveProfile}.json";
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Profile", defaultName, initialDir)
                    : NativeFileDialog.ShowOpen("Import Profile", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportProfilePath = chosen;
                    else pendingImportProfilePath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }
}
