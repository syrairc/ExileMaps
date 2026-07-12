// All ISettings classes and ImGui UI widgets for the plugin.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Linq;
using System.Numerics;
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;
using ExileMaps.Classes;
using ImGuiNET;
using Newtonsoft.Json;
using GameOffsets2.Native;

using static ExileMaps.ExileMapsCore;

namespace ExileMaps;

// Selectable sprite for the animated waypoint path. Each maps to a textures/path-*.png file loaded in
// Initialise; Comet = path-energy-stripe.png (the default).
public enum PathTextureStyle
{
    Comet,
    Chevron,
    DoubleChevron,
    Capsule,
}

// Atlas corner for the always-on progress readout HUD.
public enum ReadoutCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

// Source for a map-label color (name text or border) in the new bordered style.
public enum LabelColorSource
{
    Static,
    Weight,
    MapColor,
}

public class ExileMapsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Submenu(CollapsedByDefault = false)]
    public class ProfileSettings
    {
        public string ActiveProfile { get; set; } = "Default";
        public Dictionary<string, WeightProfile> Profiles { get; set; } = new() { { "Default", new() } };

        // Set once the pre-rework ("v1") settings auto-detect has been handled (imported or dismissed) so
        // the one-time import banner never reappears. Persisted, but not auto-rendered as a widget (plain
        // properties in this class, like ActiveProfile/Profiles, are driven by code, not the settings menu).
        public bool OldSettingsMigrationHandled { get; set; } = false;

        // Set once the label-style model has been migrated from the old scattered color settings. Defaults
        // true now: the shipped LabelStyleSettings.Defaults() already IS the intended look, so a fresh
        // install must not re-derive labels from the legacy Graphics color fields.
        public bool LabelStyleMigrated { get; set; } = true;

        // Set once box/border opacity has been folded into the color alpha (separate opacity sliders removed).
        // Defaults true: the default label colors already carry their alpha, nothing to fold on a fresh install.
        public bool LabelOpacityFolded { get; set; } = true;

        // Set once the Phase 3 connection-line settings have been seeded from the old Features toggles.
        public bool ConnectionSettingsMigrated { get; set; } = false;

        // Set once the 3-way (completed/locked/visible) toggles were split into the 4-way categories.
        public bool ConnectionCategoriesMigrated { get; set; } = false;

        [JsonIgnore]
        private string _profileEditName = "";
        [JsonIgnore]
        private bool _showNewProfileInput = false;
        [JsonIgnore]
        private bool _showRenameProfileInput = false;
        [JsonIgnore]
        private string _deleteProfileConfirm = "";

        public void SwitchProfile(string name)
        {
            if (Profiles.TryGetValue(name, out _))
            {
                SaveCurrentProfile();
                ActiveProfile = name;
                LoadProfile(name);
            }
        }

        public void SaveCurrentProfile()
        {
            if (!Profiles.ContainsKey(ActiveProfile))
                Profiles[ActiveProfile] = new();

            var profile = Profiles[ActiveProfile];
            if (Main != null)
            {
                profile.Maps.Clear();
                foreach (var (k, map) in Main.Settings.Maps.Maps)
                    profile.Maps[k] = new MapProfileEntry
                    {
                        Weight = map.Weight,
                        NodeColor = map.NodeColor,
                        NameColor = map.NameColor,
                        BackgroundColor = map.BackgroundColor,
                        Highlight = map.Highlight,
                        ColorNodesByWeight = map.ColorNodesByWeight,
                        UseWeightColorForName = map.UseWeightColorForName,
                        Favorite = map.Favorite,
                        Icon = map.Icon
                    };

                profile.Content.Clear();
                foreach (var (k, content) in Main.Settings.Maps.Content.ContentTypes)
                    profile.Content[k] = new ContentProfileEntry
                    {
                        Weight = content.Weight,
                        Color = content.Color,
                        Highlight = content.Highlight,
                        Favorite = content.Favorite
                    };

                profile.Biomes.Clear();
                foreach (var (k, biome) in Main.Settings.Maps.Biomes.Biomes)
                    profile.Biomes[k] = new BiomeProfileEntry
                    {
                        Weight = biome.Weight
                    };

                profile.Labels = Main.Settings.Labels.Clone();
            }
        }

        // Overlays a stored profile's values onto the live Map/Content/Biome objects. The live
        // dictionaries hold the map *definitions* (name/id/icon scraped from the game) and are the
        // working state that rendering reads. We must NOT clear them, only update matching entries.
        public void LoadProfile(string name)
        {
            if (!Profiles.TryGetValue(name, out var profile))
                return;

            if (Main != null)
            {
                foreach (var (k, entry) in profile.Maps)
                {
                    if (Main.Settings.Maps.Maps.TryGetValue(k, out var map))
                    {
                        map.Weight = entry.Weight;
                        map.NodeColor = entry.NodeColor;
                        map.NameColor = entry.NameColor;
                        map.BackgroundColor = entry.BackgroundColor;
                        map.Highlight = entry.Highlight;
                        map.ColorNodesByWeight = entry.ColorNodesByWeight;
                        map.UseWeightColorForName = entry.UseWeightColorForName;
                        map.Favorite = entry.Favorite;
                        map.Icon = entry.Icon;
                    }
                }

                foreach (var (k, entry) in profile.Content)
                {
                    if (Main.Settings.Maps.Content.ContentTypes.TryGetValue(k, out var content))
                    {
                        content.Weight = entry.Weight;
                        content.Color = entry.Color;
                        content.Highlight = entry.Highlight;
                        content.Favorite = entry.Favorite;
                    }
                }

                foreach (var (k, entry) in profile.Biomes)
                {
                    if (Main.Settings.Maps.Biomes.Biomes.TryGetValue(k, out var biome))
                        biome.Weight = entry.Weight;
                }

                // Labels is pure user config (no game-scraped definitions), so replace wholesale.
                if (profile.Labels != null)
                    Main.Settings.Labels = profile.Labels.Clone();

                // Live weights just changed; recompute cached node weights/colors and waypoints so the
                // atlas reflects the new profile immediately (a refresh re-runs each Node.RecalculateWeight).
                Main.OnProfileApplied();
            }
        }

        // Creates a profile seeded with the plugin's default weights and colors, and switches to it.
        // Current edits to the active profile are preserved first.
        public void NewProfile(string name)
        {
            if (Profiles.ContainsKey(name) || Main == null)
                return;

            SaveCurrentProfile();

            // Start from a blank profile: map colors/icons/flags reset to their constructor (plugin)
            // defaults, clearing any carry-over from the previous profile's live state.
            var profile = new WeightProfile();
            foreach (var k in Main.Settings.Maps.Maps.Keys)
                profile.Maps[k] = new MapProfileEntry();
            foreach (var k in Main.Settings.Maps.Content.ContentTypes.Keys)
                profile.Content[k] = new ContentProfileEntry();
            foreach (var k in Main.Settings.Maps.Biomes.Biomes.Keys)
                profile.Biomes[k] = new BiomeProfileEntry();

            profile.Labels = LabelStyleSettings.Defaults();

            Profiles[name] = profile;
            ActiveProfile = name;
            LoadProfile(name);

            // Overlay the bundled default weights + per-mechanic content colors onto live state and
            // snapshot them into the now-active new profile (a blank profile alone uses neutral
            // weight=1.0 and white content colors, ignoring the plugin defaults).
            Main.ResetWeightsToDefaults();
        }

        // Duplicates the active profile (capturing any unsaved live edits first) under a new name and
        // makes it active. The live working state already matches the source, so no reload is needed.
        public void CopyProfile(string newName)
        {
            if (Profiles.ContainsKey(newName))
                return;

            SaveCurrentProfile();
            if (Profiles.TryGetValue(ActiveProfile, out var source))
            {
                Profiles[newName] = new()
                {
                    Maps = new Dictionary<string, MapProfileEntry>(source.Maps),
                    Content = new Dictionary<string, ContentProfileEntry>(source.Content),
                    Biomes = new Dictionary<string, BiomeProfileEntry>(source.Biomes),
                    Labels = source.Labels?.Clone() ?? LabelStyleSettings.Defaults()
                };
                ActiveProfile = newName;
            }
        }

        public void DeleteProfile(string name)
        {
            if (name != "Default" && Profiles.ContainsKey(name))
            {
                Profiles.Remove(name);
                if (ActiveProfile == name)
                {
                    // Don't route through SwitchProfile: it calls SaveCurrentProfile first, which
                    // re-creates the just-removed profile (ActiveProfile still points at it) and the
                    // deleted profile reappears in the list. Point ActiveProfile at the fallback first,
                    // then overlay it onto the live data.
                    ActiveProfile = Profiles.ContainsKey("Default") ? "Default" : Profiles.Keys.First();
                    LoadProfile(ActiveProfile);
                }
            }
        }

        public void RenameProfile(string oldName, string newName)
        {
            if (Profiles.TryGetValue(oldName, out var profile) && !Profiles.ContainsKey(newName))
            {
                Profiles[newName] = profile;
                Profiles.Remove(oldName);
                if (ActiveProfile == oldName)
                    ActiveProfile = newName;
            }
        }

        public void EnsureDefaultProfile()
        {
            if (Profiles.Count == 0)
                Profiles["Default"] = new();
            if (string.IsNullOrEmpty(ActiveProfile) || !Profiles.ContainsKey(ActiveProfile))
                ActiveProfile = Profiles.Keys.First();
        }

        // Profile picker + management buttons. Switching profiles auto-saves the current one first
        // (see SwitchProfile). New = blank/defaults, Copy = duplicate active, Rename/Delete act on active.
        [JsonIgnore]
        public CustomNode ProfileSelector { get; set; } = new CustomNode
        {
            DrawDelegate = () =>
            {
                var p = Main.Settings.Profiles;

                ImGui.Text("Active Profile:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(250);
                if (ImGui.BeginCombo("##profile_combo", p.ActiveProfile))
                {
                    foreach (var key in p.Profiles.Keys.OrderBy(x => x).ToList())
                    {
                        bool isSelected = key == p.ActiveProfile;
                        if (ImGui.Selectable(key, isSelected) && key != p.ActiveProfile)
                            p.SwitchProfile(key);
                        if (isSelected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                if (p._showNewProfileInput)
                {
                    ImGui.SetNextItemWidth(200);
                    ImGui.InputTextWithHint("##newprofilename", "Profile name", ref p._profileEditName, 64);
                    ImGui.SameLine();
                    if (ImGui.Button("Create##profile") && !string.IsNullOrWhiteSpace(p._profileEditName))
                    {
                        p.NewProfile(p._profileEditName.Trim());
                        p._showNewProfileInput = false;
                        p._profileEditName = "";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel##profilenew"))
                    {
                        p._showNewProfileInput = false;
                        p._profileEditName = "";
                    }
                }
                else if (p._showRenameProfileInput)
                {
                    ImGui.SetNextItemWidth(200);
                    ImGui.InputTextWithHint("##renameprofilename", "New name", ref p._profileEditName, 64);
                    ImGui.SameLine();
                    if (ImGui.Button("Rename##profileconfirm") && !string.IsNullOrWhiteSpace(p._profileEditName))
                    {
                        p.RenameProfile(p.ActiveProfile, p._profileEditName.Trim());
                        p._showRenameProfileInput = false;
                        p._profileEditName = "";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel##profilerename"))
                    {
                        p._showRenameProfileInput = false;
                        p._profileEditName = "";
                    }
                }
                else if (!string.IsNullOrEmpty(p._deleteProfileConfirm))
                {
                    ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Delete \"{p._deleteProfileConfirm}\"?");
                    ImGui.SameLine();
                    if (ImGui.Button("Yes##profiledelete"))
                    {
                        p.DeleteProfile(p._deleteProfileConfirm);
                        p._deleteProfileConfirm = "";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("No##profiledelete"))
                        p._deleteProfileConfirm = "";
                }
                else
                {
                    if (ImGui.Button("New##profile"))
                    {
                        p._showNewProfileInput = true;
                        p._profileEditName = "";
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Copy##profile"))
                    {
                        string baseName = p.ActiveProfile + " (copy)";
                        string newName = baseName;
                        int i = 2;
                        while (p.Profiles.ContainsKey(newName))
                            newName = $"{baseName} {i++}";
                        p.CopyProfile(newName);
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Rename##profile"))
                    {
                        p._showRenameProfileInput = true;
                        p._profileEditName = p.ActiveProfile;
                    }
                    ImGui.SameLine();
                    bool canDelete = p.Profiles.Count > 1 && p.ActiveProfile != "Default";
                    if (!canDelete)
                        ImGui.BeginDisabled();
                    if (ImGui.Button("Delete##profile"))
                        p._deleteProfileConfirm = p.ActiveProfile;
                    if (!canDelete)
                        ImGui.EndDisabled();
                }

                // Import/export the active profile as a standalone JSON file (share or back up presets).
                if (ImGui.Button("Export Profile##profile"))
                    Main.ExportProfile();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Save the active profile's weights and display settings to a JSON file.");
                ImGui.SameLine();
                if (ImGui.Button("Import Profile##profile"))
                    Main.ImportProfile();
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Load a profile JSON file as a new profile and switch to it.");
            }
        };
    }

    // Top-level import/export: full settings. Profile import/export (Profiles section) covers weight-only presets.
    [JsonIgnore]
    public CustomNode WeightImportExport { get; set; } = new CustomNode
    {
        DrawDelegate = () =>
        {
            SettingsHelpers.DrawImportExportSettingsButtons();
        }
    };

    public ProfileSettings Profiles { get; set; } = new();
    public FeatureSettings Features { get; set; } = new FeatureSettings();
    public ExpeditionSettings Expeditions { get; set; } = new ExpeditionSettings();
    public HotkeySettings Keybinds { get; set; } = new HotkeySettings();
    public GraphicSettings Graphics { get; set; } = new GraphicSettings();

    // Live label look rendering reads. Snapshotted into the active WeightProfile (see ProfileSettings).
    public LabelStyleSettings Labels { get; set; } = LabelStyleSettings.Defaults();

    [Menu("Atlas Overview")]
    public AtlasOverviewSettings AtlasOverview { get; set; } = new AtlasOverviewSettings();

    [Menu("Tours")]
    public TourSettings Tours { get; set; } = new TourSettings();

    [Menu("Maps")]
    public MapSettings Maps { get; set; } = new MapSettings();
    public WaypointSettings Waypoints { get; set; } = new WaypointSettings();

}

[Submenu(CollapsedByDefault = false)]
public class FeatureSettings
{
    [Menu("Enable Atlas Drawing", "Master switch for all atlas overlay drawing (nodes, lines, rings, labels, waypoints). Toggle with the Atlas Drawing hotkey (default Scroll Lock).")]
    public ToggleNode EnableDrawing { get; set; } = new ToggleNode(true);

    [Menu("Atlas Range", "Range (from your current viewpoint) to process atlas nodes.")]
    public RangeNode<int> AtlasRange { get; set; } = new(100, 100, 20000);
    [Menu("Use Atlas Range for Node Connections", "Drawing node connections is performance intensive. By default it uses a range of 1000, but you can change it to use the Atlas range.")]
    public ToggleNode UseAtlasRange { get; set; } = new ToggleNode(true);

    [Menu("Process Visited Map Nodes")]
    public ToggleNode ProcessVisitedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Unlocked Map Nodes")]
    public ToggleNode ProcessUnlockedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Locked Map Nodes")]
    public ToggleNode ProcessLockedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Hidden Map Nodes")]
    public ToggleNode ProcessHiddenNodes { get; set; } = new ToggleNode(true);

    [Menu("Draw Connections for Visited Map Nodes")]
    public ToggleNode DrawVisitedNodeConnections { get; set; } = new ToggleNode(false);

    [ConditionalDisplay(nameof(ProcessHiddenNodes), true)]
    [Menu("Draw Connections for Hidden Map Nodes")]
    public ToggleNode DrawHiddenNodeConnections { get; set; } = new ToggleNode(true);

    [Menu("Recalculate Node Weights on Refresh", "Recompute each node's weight when the map cache refreshes so weight (and weight-based colors/sorting) reflect live content and tablet mods. Disable to save performance if weights don't need to update after the first scan.")]
    public ToggleNode RecalculateNodeWeightsOnRefresh { get; set; } = new ToggleNode(false);

    [Menu("Debug Mode")]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [Menu("Show Performance Monitor", "Display a real-time overlay showing 60-frame average CPU time for each rendering and cache section.")]
    public ToggleNode ShowPerfMonitor { get; set; } = new ToggleNode(false);

    [Menu("Show Panel Buttons", "Show a floating button bar at the bottom-center of the atlas to open the Waypoints, Atlas Overview and Tours panels.")]
    public ToggleNode ShowPanelButtons { get; set; } = new ToggleNode(true);

    [Menu("Show Atlas Search Box", "Show a floating search box at the top-center of the atlas that pings matching nodes (same as the Atlas Overview search).")]
    public ToggleNode ShowAtlasSearchBox { get; set; } = new ToggleNode(true);

    [Menu("Show Expedition Markers", "Draw a marker + label on the atlas at each expedition's nearest map node.")]
    public ToggleNode ShowExpeditionMarkers { get; set; } = new ToggleNode(true);

    [Menu("Draw All Hidden Expedition Indicators", "On: draw a marker at every hidden button location per expedition. Off: draw only the nearest one (fewest steps from your explored frontier).")]
    public ToggleNode DrawAllExpeditionMarkers { get; set; } = new ToggleNode(true);

}
[Submenu(CollapsedByDefault = true)]
public class HotkeySettings
{
    // HotkeyNodeV2 renders as a uniform label + picker button (unlike old HotkeyNode which sized to its
    // label, producing inconsistent widths). [JsonIgnore] CustomNodes group related binds. Most default to F13.

    [JsonIgnore]
    public CustomNode SepGeneral { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("General") };

    [Menu("Toggle Atlas Drawing Hotkey", "Show/hide all atlas overlay drawing. Default: Scroll Lock")]
    public HotkeyNodeV2 ToggleDrawingHotkey { get; set; } = new HotkeyNodeV2(Keys.Scroll);

    [Menu("Map Cache Refresh Hotkey", "Default: Home")]
    public HotkeyNodeV2 RefreshMapCacheHotkey { get; set; } = new HotkeyNodeV2(Keys.Home);

    [Menu("Quick Edit Node Hotkey", "Hover a node on the Atlas and press to open a popup that edits that map type and its content without opening settings. Unbound by default - set a key.")]
    public HotkeyNodeV2 QuickEditNodeHotkey { get; set; } = new HotkeyNodeV2(Keys.Multiply);

    [JsonIgnore]
    public CustomNode SepPanels { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Panels") };

    [Menu("Toggle Atlas Overview Panel", "Show/hide the Atlas Overview panel (reachable-content tally + node search). Default: Pause")]
    public HotkeyNodeV2 ToggleAtlasOverviewHotkey { get; set; } = new HotkeyNodeV2(Keys.Pause);

    [Menu("Waypoint Panel Hotkey", "Show/hide the Waypoints panel. Default: End")]
    public HotkeyNodeV2 ToggleWaypointPanelHotkey { get; set; } = new HotkeyNodeV2(Keys.End);

    [JsonIgnore]
    public CustomNode SepTours { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Tours") };

    [Menu("Toggle Tours Panel", "Show/hide the Tours panel (create/manage named tour routes). Unbound by default - set a key.")]
    public HotkeyNodeV2 ToggleToursPanelHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Add Tour Stop Hotkey", "Hover a node on the Atlas and press to add/remove it from the active tour. Unbound by default - set a key.")]
    public HotkeyNodeV2 AddTourStopHotkey { get; set; } = new HotkeyNodeV2(Keys.NumPad7);

    [Menu("Build Tour Mode Hotkey", "Toggle interactive Build Mode: left-click atlas nodes to add them to the active tour, right-click to remove. Unbound by default - set a key.")]
    public HotkeyNodeV2 BuildModeHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Build Tour Mode Exit Key", "Key that exits Build Mode. Default: Tab (avoid Escape - it also closes the Atlas).")]
    public HotkeyNodeV2 BuildModeExitHotkey { get; set; } = new HotkeyNodeV2(Keys.Tab);

    [JsonIgnore]
    public CustomNode SepWaypoints { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Waypoints") };

    [Menu("Add Waypoint Hotkey", "Default: Insert")]
    public HotkeyNodeV2 AddWaypointHotkey { get; set; } = new HotkeyNodeV2(Keys.Insert);

    [Menu("Remove Waypoint Hotkey", "Default: Delete")]
    public HotkeyNodeV2 DeleteWaypointHotkey { get; set; } = new HotkeyNodeV2(Keys.Delete);

    [Menu("Toggle Waypoints Hotkey", "Show/hide waypoints and their arrows. Unbound by default - set a key.")]
    public HotkeyNodeV2 ToggleWaypointsHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [JsonIgnore]
    public CustomNode SepNodeProcessing { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Node Processing") };

    [Menu("Toggle Processing Visited Nodes", "Default: unbound - set this")]
    public HotkeyNodeV2 ToggleVisitedNodesHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Toggle Processing Unlocked Nodes", "Default: unbound - set this")]
    public HotkeyNodeV2 ToggleUnlockedNodesHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Toggle Processing Locked Nodes", "Default: unbound - set this")]
    public HotkeyNodeV2 ToggleLockedNodesHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Toggle Processing Hidden Nodes", "Default: unbound - set this")]
    public HotkeyNodeV2 ToggleHiddenNodesHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [JsonIgnore]
    public CustomNode SepDataUpdates { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Game Data Updates") };

    [Menu("Update Map Type Data", "Re-scrape map types from game files. Unbound by default - set a key.")]
    public HotkeyNodeV2 UpdateMapsKey { get; set; } = new HotkeyNodeV2(Keys.F9);

    [Menu("Update Content Type Data", "Re-scrape content types from game files. Unbound by default - set a key.")]
    public HotkeyNodeV2 UpdateContentKey { get; set; } = new HotkeyNodeV2(Keys.F10);

    [Menu("Update Biome Data", "Re-scrape biomes from game files. Unbound by default - set a key.")]
    public HotkeyNodeV2 UpdateBiomesKey { get; set; } = new HotkeyNodeV2(Keys.F11);

    [JsonIgnore]
    public CustomNode SepDebug { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Debug") };

    [Menu("Debug Node Hotkey", "Hover a node on the Atlas and press to open a popup showing that node's debug info and element flags. Unbound by default - set a key.")]
    public HotkeyNodeV2 DebugNodeHotkey { get; set; } = new HotkeyNodeV2(Keys.Divide);

    [Menu("Print Node Debug Data", "Unbound by default - set a key.")]
    public HotkeyNodeV2 DebugKey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Toggle Debug Mode", "Unbound by default - set a key.")]
    public HotkeyNodeV2 ToggleDebugModeHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);
}

[Submenu(CollapsedByDefault = true)]
public class GraphicSettings
{
    // Properties are auto-rendered by ExileCore2 in declaration order. The [JsonIgnore] separator
    // CustomNodes below are interleaved between groups so the section reads as labeled sub-groups.

    [JsonIgnore]
    public CustomNode SepPerformance { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Performance") };

    [Menu("Map Cache Refresh Rate", "Throttle the map cache refresh rate. Default is 5 seconds.")]
    public RangeNode<int> MapCacheRefreshRate { get; set; } = new RangeNode<int>(1, 1, 60);

    [JsonIgnore]
    public CustomNode SepNodes { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Nodes") };

    [Menu("Node Radius", "Radius of the circles used to highlight map nodes")]
    public RangeNode<float> NodeRadius { get; set; } = new RangeNode<float>(1.856f, 0, 10);

    [Menu("Use Icons for Nodes", "Draw the per-map sprite icon for map nodes. Disable to use plain filled circles (the original look).")]
    public ToggleNode UseNodeIcons { get; set; } = new ToggleNode(true);

    [Menu("Lay Icons Flat", "Vertically squash node icons so round sprites read as flat discs lying on the tilted atlas plane. 0 = upright. Tune by eye.")]
    public RangeNode<float> IconFlatten { get; set; } = new RangeNode<float>(0.180f, 0f, 0.9f);

    [JsonIgnore]
    public CustomNode SepContentIcons { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Content Indicators") };

    [Menu("Skip Game-drawn Content", "On visible nodes the game draws its own icon for any content that has one, so skip our duplicate there. Fogged (non-visible) nodes show no in-game content, so our icons always draw on them. Turn off to always draw our icons.")]
    public ToggleNode ContentIconsSkipGameDrawn { get; set; } = new ToggleNode(true);

    [Menu("Content Icon Size", "Width (and height) of each content icon in the row, in pixels at full zoom. Scales down as you zoom out.")]
    public RangeNode<float> ContentIconSize { get; set; } = new RangeNode<float>(32f, 8f, 48f);

    [Menu("Content Icon Spacing", "Horizontal gap in pixels between icons when a node has multiple content types.")]
    public RangeNode<float> ContentIconSpacing { get; set; } = new RangeNode<float>(2f, 0f, 32f);

    // ---- Phase 2 display (global, not per-profile) ----

    [JsonIgnore]
    public CustomNode SepPhase2 { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Content Row / Biome") };

    [Menu("Show Content Row", "Draw a row of content-type icons centered below the map name.")]
    public ToggleNode ShowContentRow { get; set; } = new ToggleNode(true);

    [Menu("Content Row Offset Y", "Vertical pixel offset (at full zoom) of the content row below the map name. Scales with zoom.")]
    public RangeNode<float> ContentRowOffsetY { get; set; } = new RangeNode<float>(32f, -200f, 200f);

    [Menu("Show Biome Icon", "Draw one biome icon (highest-weight biome) centered above the map name.")]
    public ToggleNode ShowBiomeIcon { get; set; } = new ToggleNode(true);

    [Menu("Biome Icon Size", "Width/height of the biome icon in pixels at full zoom.")]
    public RangeNode<float> BiomeIconSize { get; set; } = new RangeNode<float>(24f, 8f, 48f);

    [Menu("Biome Icon Offset Y", "Vertical nudge (at full zoom) of the biome icon relative to the map name row. Negative = upward. Scales with zoom.")]
    public RangeNode<float> BiomeIconOffsetY { get; set; } = new RangeNode<float>(0f, -200f, 200f);

    // Show the numeric weight value next to the map name. Reverted from the old icon/value enum.
    [Menu("Show Weight Value", "Draw each highlighted node's numeric weight next to its map name.")]
    public ToggleNode DrawWeightOnMap { get; set; } = new ToggleNode(false);

    [Menu("Show Atlas Point Badge", "Badge content that awards an atlas point with a small gold star at the bottom of its icon.")]
    public ToggleNode ShowAtlasPointBadge { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public CustomNode SepColors { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Colors") };

    [Menu("Font Color", "Color of the text on the Atlas")]
    public ColorNode FontColor { get; set; } = new ColorNode(Color.White);

    [Menu("Background Color", "Color of the background on the Atlas")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(Color.FromArgb(177, 0, 0, 0));

    [JsonIgnore]
    public CustomNode SepLines { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Connection Lines") };

    [Menu("Line Color", "Color of the map connection lines and waypoint lines when no map specific color is set")]
    public ColorNode LineColor { get; set; } = new ColorNode(Color.FromArgb(200, 255, 222, 222));

    [Menu("Line Width", "Width of the map connection lines")]
    public RangeNode<float> MapLineWidth { get; set; } = new RangeNode<float>(3.0f, 0, 10);

    [Menu("Draw Lines as Gradients", "Draws lines as a gradient between the two colors. Performance intensive.")]
    public ToggleNode DrawGradientLines { get; set; } = new ToggleNode(true);

    [Menu("Visited Line Color", "Color of the map connection lines when an both nodes are visited.")]
    public ColorNode VisitedLineColor { get; set; } = new ColorNode(Color.FromArgb(255, 152, 152, 152));

    [Menu("Unlocked Line Color", "Color of the map connection lines when an adjacent node is unlocked.")]
    public ColorNode UnlockedLineColor { get; set; } = new ColorNode(Color.FromArgb(255, 18, 228, 18));

    [Menu("Locked Line Color", "Color of the map connection lines when no adjacent nodes are unlocked.")]
    public ColorNode LockedLineColor { get; set; } = new ColorNode(Color.FromArgb(255, 149, 20, 20));

    [Menu("Show Connection Lines", "Master toggle for the lines drawn between adjacent atlas nodes.")]
    public ToggleNode ShowConnectionLines { get; set; } = new ToggleNode(true);

    [JsonIgnore]
    public CustomNode SepConnectionConditions { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Show lines for") };

    [Menu("Connections from Completed Maps", "Draw lines touching a completed/visited map.")]
    public ToggleNode ShowConnectionsForCompleted { get; set; } = new ToggleNode(false);

    [Menu("Connections from Accessible Maps", "Draw lines touching an accessible map (unlocked, not yet run).")]
    public ToggleNode ShowConnectionsForAccessible { get; set; } = new ToggleNode(false);

    [Menu("Connections from Inaccessible Maps", "Draw lines touching a revealed but locked map (not unlocked yet).")]
    public ToggleNode ShowConnectionsForInaccessible { get; set; } = new ToggleNode(true);

    [Menu("Connections from Hidden Maps", "Draw lines touching a hidden map (not revealed yet).")]
    public ToggleNode ShowConnectionsForHidden { get; set; } = new ToggleNode(true);

    // old 3-way toggles, kept for JSON compat + one-time split into the 4-way categories above
    [JsonProperty] internal ToggleNode ShowConnectionsForLocked { get; set; } = new ToggleNode(true);
    [JsonProperty] internal ToggleNode ShowConnectionsForVisible { get; set; } = new ToggleNode(false);

    [Menu("Connection Line Opacity", "Global dimmer for all connection lines. Scales each line color's alpha; keeps the per-state color differences.")]
    public RangeNode<int> ConnectionLineOpacity { get; set; } = new RangeNode<int>(106, 0, 255);

    [Menu("Distance Marker Scale", "Interpolation factor for distance markers on lines")]
    public RangeNode<float> LabelInterpolationScale { get; set; } = new RangeNode<float>(0.746f, 0, 1);

    [JsonIgnore]
    public CustomNode SepWaypoints { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Waypoints") };

    [Menu("Draw paths to waypoints", "Shows the shortest path from the nearest visited node to waypoints")]
    public ToggleNode ShowPaths { get; set; } = new ToggleNode(true);

    [Menu("Waypoint Line Width", "Width of the map waypoint path (px).")]
    public RangeNode<float> WaypointLineWidth { get; set; } = new RangeNode<float>(5.051f, 0, 24);

    // Persisted enum (no [Menu]; edited via WaypointPathTexturePicker below, like SpecialMapIcon).
    public PathTextureStyle WaypointPathTexture { get; set; } = PathTextureStyle.DoubleChevron;

    [JsonIgnore]
    public CustomNode WaypointPathTexturePicker { get; set; } = new CustomNode
    {
        DrawDelegate = () =>
        {
            if (Main == null) return;
            string[] labels = { "Comet", "Chevron", "Double Chevron", "Capsule" };
            int idx = (int)Main.Settings.Graphics.WaypointPathTexture;
            if (idx < 0 || idx >= labels.Length) idx = 0;
            ImGui.Text("Waypoint Path Texture");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            if (ImGui.Combo("##waypointpathtexture", ref idx, labels, labels.Length))
                Main.Settings.Graphics.WaypointPathTexture = (PathTextureStyle)idx;
            ImGui.SameLine();
            ImGui.TextDisabled("(needs 'Use Icons for Nodes' on; off = flat line)");
        }
    };

    [Menu("Animate Waypoint Path", "Scroll dashes along the path toward the waypoint. Off = static dashed line.")]
    public ToggleNode WaypointPathAnimated { get; set; } = new ToggleNode(true);

    [Menu("Waypoint Dash Length", "Length (px) of each dash on the waypoint path. 0 = solid line.")]
    public RangeNode<float> WaypointDashLength { get; set; } = new RangeNode<float>(20.0f, 0, 80);

    [Menu("Waypoint Dash Gap", "Gap (px) between dashes on the waypoint path.")]
    public RangeNode<float> WaypointDashGap { get; set; } = new RangeNode<float>(10.0f, 0, 60);

    [Menu("Waypoint Dash Speed", "How fast dashes scroll toward the waypoint (px per tick). 0 = static.")]
    public RangeNode<float> WaypointDashSpeed { get; set; } = new RangeNode<float>(0.792f, 0, 8);

    [Menu("Waypoint Texture Scale", "Thickness multiplier applied to the path width when using a textured path. Plain-line mode ignores this.")]
    public RangeNode<float> WaypointPathTextureScale { get; set; } = new RangeNode<float>(2.0f, 0.5f, 6);

    [Menu("Waypoint Arrow Min Distance", "Only show the off-screen waypoint arrow when the waypoint is farther than this (px) from screen center. Lower = arrow shows for nearer waypoints.")]
    public RangeNode<float> WaypointArrowMinDistance { get; set; } = new RangeNode<float>(641.454f, 50, 1500);

    [Menu("Favorite Marker Color", "Color of the star marker drawn above favorite map nodes")]
    public ColorNode FavoriteColor { get; set; } = new ColorNode(Color.FromArgb(255, 255, 215, 0));

    [Menu("Favorite Marker Scale", "Size of the star marker drawn above favorite map nodes")]
    public RangeNode<float> FavoriteIconScale { get; set; } = new RangeNode<float>(1.038f, 0, 3);

    [Menu("Favorite Icon Size", "Width/height of the favorite star in pixels at full zoom. Sits beside the biome icon; defaults to the biome icon size.")]
    public RangeNode<float> FavoriteIconSize { get; set; } = new RangeNode<float>(24f, 8f, 48f);

    [JsonIgnore]
    public CustomNode SepSpecialMaps { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Special Maps") };

    [Menu("Show Atlas Quest Marker", "Draw a small golden exclamation above maps that have atlas quest content.")]
    public ToggleNode ShowAtlasQuestIndicator { get; set; } = new ToggleNode(false);

    [Menu("Atlas Quest Marker Size", "Pixel size of the atlas-quest exclamation marker.")]
    public RangeNode<float> AtlasIndicatorSize { get; set; } = new RangeNode<float>(18.918f, 8, 48);

    [Menu("Special Map Indicator", "Draw an icon above 'special' map nodes (wider node art) instead of a solid circle, so the map art isn't covered")]
    public ToggleNode ShowSpecialMapIndicator { get; set; } = new ToggleNode(false);

    [Menu("Hide Completed Special Maps", "Once a repeatable special map (e.g. Precursor Tower) is completed, drop its marker + name entirely instead of fading them. Hub specials (Gateways, Sealed Vault) always stay shown.")]
    public ToggleNode HideCompletedSpecialMaps { get; set; } = new ToggleNode(true);

    [Menu("Special Map Marker Color", "Color of the icon drawn above special map nodes")]
    public ColorNode SpecialMapColor { get; set; } = new ColorNode(Color.FromArgb(255, 200, 80, 255));

    [Menu("Special Map Marker Scale", "Size of the icon drawn above special map nodes")]
    public RangeNode<float> SpecialMapIconScale { get; set; } = new RangeNode<float>(1.501f, 0, 3);

    [Menu("Special Map Marker Offset", "Pixels above the node center to place the special-map icon.")]
    public RangeNode<float> SpecialMapIconOffset { get; set; } = new RangeNode<float>(40.0f, 0, 120);

    // Sprite drawn above special map nodes. Persisted (Newtonsoft); edited via SpecialMapIconPicker.
    public SpriteIcon SpecialMapIcon { get; set; } = SpriteIcon.Exclamation;

    [JsonIgnore]
    public CustomNode SpecialMapIconPicker { get; set; } = new CustomNode
    {
        DrawDelegate = () =>
        {
            ImGui.Text("Special Map Marker Icon");
            ImGui.SameLine();
            SettingsHelpers.IconPicker("specialmapicon", Main.Settings.Graphics.SpecialMapIcon, i => Main.Settings.Graphics.SpecialMapIcon = i);
        }
    };

    [Menu("Special Map Name Color", "Map name text color for special maps (overrides weight-based coloring)")]
    public ColorNode SpecialMapNameColor { get; set; } = new ColorNode(Color.FromArgb(255, 200, 80, 255));

    [JsonIgnore]
    public CustomNode SepMapLabels { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Map Labels") };

    // Pixel offset applied to map name and weight text relative to the node center.
    [Menu("Map Name Offset X", "Horizontal pixel offset of the map name/weight text relative to the node center.")]
    public RangeNode<int> MapNameOffsetX { get; set; } = new RangeNode<int>(0, -200, 200);
    [Menu("Map Name Offset Y", "Vertical pixel offset of the map name/weight text relative to the node center.")]
    public RangeNode<int> MapNameOffsetY { get; set; } = new RangeNode<int>(30, -200, 200);

    [Menu("Uppercase Map Names", "Draw map names in ALL CAPS. Off = use the map's normal casing.")]
    public ToggleNode UppercaseMapNames { get; set; } = new ToggleNode(false);

    [Menu("Legacy Map Name Styling", "Use the old plain-background label for normal map names instead of the new bordered box.")]
    public ToggleNode LegacyMapNameStyling { get; set; } = new ToggleNode(false);

    // Old-style label colors, superseded by the LabelStyle system (see Classes/LabelStyle.cs and
    // LabelStyleUI). Kept only so MigrateLabelStyles can read them once on old configs; no UI left.
    public LabelColorSource MapNameTextColorSource { get; set; } = LabelColorSource.Static;
    public LabelColorSource MapNameBorderColorSource { get; set; } = LabelColorSource.Weight;
    public Color MapNameTextStaticColor { get; set; } = Color.White;
    public Color MapNameBorderStaticColor { get; set; } = Color.FromArgb(255, 120, 120, 130);
}

[Submenu(CollapsedByDefault = true)]
public class AtlasOverviewSettings
{
    [Menu("Reachable Step Window", "How many steps from your explored frontier count as 'reachable' for the content tally and atlas-point counter.")]
    public RangeNode<int> ReachableSteps { get; set; } = new RangeNode<int>(5, 1, 50);

    [Menu("Show Progress Readout", "Draw an always-on counter (maps run/completed, atlas points reachable) in an atlas corner.")]
    public ToggleNode ShowProgressReadout { get; set; } = new ToggleNode(false);

    // Edited via the combo below; no [Menu] so it isn't auto-rendered as a raw enum.
    public ReadoutCorner ReadoutCorner { get; set; } = ReadoutCorner.TopLeft;

    [JsonIgnore]
    public CustomNode ReadoutCornerPicker { get; set; } = new CustomNode
    {
        DrawDelegate = () =>
        {
            if (Main == null) return;
            string[] labels = { "Top Left", "Top Right", "Bottom Left", "Bottom Right" };
            int idx = (int)Main.Settings.AtlasOverview.ReadoutCorner;
            if (idx < 0 || idx >= labels.Length) idx = 0;
            ImGui.Text("Readout Corner");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(160);
            if (ImGui.Combo("##readoutcorner", ref idx, labels, labels.Length))
                Main.Settings.AtlasOverview.ReadoutCorner = (ReadoutCorner)idx;
        }
    };

    [Menu("Search Ping Color", "Color of the pulsing ring drawn on atlas nodes matching the Atlas Overview search box.")]
    public ColorNode SearchPingColor { get; set; } = new ColorNode(Color.FromArgb(255, 80, 230, 255));

    public PanelRect PanelRect { get; set; } = new PanelRect();
}

[Submenu(CollapsedByDefault = true)]
public class TourSettings
{
    [Menu("Show Tours", "Master switch for drawing all enabled tour routes on the atlas.")]
    public ToggleNode ShowTours { get; set; } = new ToggleNode(true);

    [Menu("Show Next-Stop Target", "Draw a pulsing Target icon above the next uncompleted stop in each visible tour.")]
    public ToggleNode ShowNextStopTarget { get; set; } = new ToggleNode(true);

    [Menu("Auto-clear Completed Stops", "When rebuilding a tour, automatically drop stops whose map you've already completed.")]
    public ToggleNode AutoClearCompleted { get; set; } = new ToggleNode(true);

    // Id of the tour the Add-Tour-Stop hotkey targets. Empty = none active.
    public string ActiveTourId { get; set; } = "";

    public ObservableDictionary<string, Tour> Tours { get; set; } = [];

    // ---- Auto Create Tour ----
    // Content names (matching the Atlas Overview content list) the auto-tour builder targets.
    public List<string> AutoTourContent { get; set; } = new();

    [Menu("Auto Tour Max Steps", "When auto-creating a tour, the most steps allowed between consecutive matching stops before the route stops growing.")]
    public RangeNode<int> AutoTourStepRange { get; set; } = new RangeNode<int>(5, 1, 10);

    [Menu("Auto Tour Off-screen Margin %", "How far beyond the visible screen (as a percent of screen size) a matching node may sit and still be included, so auto-tours don't sprawl across the whole atlas.")]
    public RangeNode<int> AutoTourScreenMarginPct { get; set; } = new RangeNode<int>(11, 0, 200);

    [Menu("Auto Tour Only Atlas-point Content", "When auto-creating a tour, only include nodes whose content awards an atlas point (league content points), skipping plain content maps.")]
    public ToggleNode AutoTourOnlyAtlasPoints { get; set; } = new ToggleNode(false);

    public PanelRect PanelRect { get; set; } = new PanelRect();
}

[Submenu(CollapsedByDefault = true)]
// MARK: MapSettings
public class MapSettings
{

    public MapSettings() {    
        
        CustomMapSettings = new CustomNode
        {
            
            DrawDelegate = () =>
            {
                bool highlightNodes = HighlightMapNodes;
                if (ImGui.Checkbox("Draw Map Nodes", ref highlightNodes)) HighlightMapNodes = highlightNodes;
            }
        };

        MapTable = new CustomNode
        {
            
            DrawDelegate = () =>
            {

            ImGui.Separator();
            ImGui.TextWrapped("CTRL+Click on a slider to manually enter a value.");

            // How weights work: quick reference for the user.
            bool weightsHelpOpen = SettingsHelpers.SubHeader("How do map weights work?");
            if (weightsHelpOpen)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.75f, 0.75f, 0.75f, 1.0f));
                ImGui.TextWrapped(
                    "Each node's weight decides how desirable it is (higher = more highlighted). It is the map's base " +
                    "weight, scaled by the content on the node, plus a flat biome bonus. Three different scales feed " +
                    "one formula:");
                ImGui.Spacing();
                ImGui.TextWrapped("    Weight = MapWeight + |MapWeight| x (sum of content weights) / 100 + (sum of biome weights)");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Map weight (-50 to 50): 50 = amazing, 25 = good, 0 = neutral, negative = actively avoid.");
                ImGui.TextWrapped(
                    "Content weight (-100 to 100) is a whole-number percentage bonus (default 25). Content adds together - " +
                    "it does NOT multiply. Positive content always RAISES the value (even for negative-weight maps); " +
                    "negative content lowers it. 0 = ignored.");
                ImGui.TextWrapped(
                    "Biome weight (-5 to 5) is a small flat amount added after the content scaling (most biomes 0). Set a " +
                    "few you care about; it nudges otherwise-similar maps.");
                ImGui.Spacing();
                ImGui.TextWrapped(
                    "Example: a map (10) with Breach (+25) and Delirium (-10), on a +2 biome = " +
                    "10 + 10 x (25 - 10)/100 + 2 = 10 + 1.5 + 2 = 13.5.");
                ImGui.PopStyleColor();
            }
            SettingsHelpers.SubHeaderEnd();

            // Profile context
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
            ImGui.Text($"Profile: {(Main != null && Main.Settings.Profiles != null ? Main.Settings.Profiles.ActiveProfile : "Default")}");
            ImGui.PopStyleColor();

            // Search filter for the map list.
            string filter = mapSearchFilter;
            ImGui.SetNextItemWidth(250);
            if (ImGui.InputTextWithHint("##mapsearch", "Search maps...", ref filter, 100))
                mapSearchFilter = filter;
            ImGui.SameLine();
            if (ImGui.Button("Clear##mapsearchclear"))
                mapSearchFilter = "";
            // Quick-filter chips - narrow the list to highlighted / weighted maps.
            ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
            ImGui.Checkbox("Highlighted##mapchip", ref mapFilterHighlight);
            ImGui.SameLine();
            ImGui.Checkbox("Weight > 0##mapchip", ref mapFilterWeighted);

            if (ImGui.BeginTable("maps_table", 3, ImGuiTableFlags.SizingFixedFit|ImGuiTableFlags.Borders|ImGuiTableFlags.PadOuterX))
            {

                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 250);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 200);
                ImGui.TableHeadersRow();

                var visibleMaps = Maps
                    .Where(x => !MapIsUnused(x.Key, x.Value))
                    .Where(x => string.IsNullOrEmpty(mapSearchFilter)
                        || (x.Value.Name?.Contains(mapSearchFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Where(x => !mapFilterHighlight || x.Value.Highlight)
                    .Where(x => !mapFilterWeighted || x.Value.Weight > 0)
                    .OrderBy(x => x.Value.Name)
                    .ToList();
                var visibleMapValues = visibleMaps.Select(x => x.Value).ToList();

                // "Set/Toggle all" row: applies to every map currently shown (respects the search filter).
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text("Set all");
                SettingsHelpers.ToggleAll("highlight", visibleMapValues, m => m.Highlight, (m, v) => m.Highlight = v);
                ImGui.TableNextColumn();
                SettingsHelpers.SetAllWeight("mapweight", ref bulkMapWeight, -50f, 50f, "%.1f", visibleMapValues, (m, w) => m.Weight = w);
                ImGui.TableNextColumn(); // spacer

                foreach (var (key,map) in visibleMaps)
                {
                    ImGui.PushID($"Map_{key}");
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool isMapHighlighted = map.Highlight;

                    // Highlight
                    if(ImGui.Checkbox($"##{key}_highlight", ref isMapHighlighted))
                        map.Highlight = isMapHighlighted;

                    // Name
                    ImGui.SameLine();
                    ImGui.Text(map.Name);

                    // Weight
                    ImGui.TableNextColumn();
                    float weight = map.Weight;
                    ImGui.SetNextItemWidth(100);
                    if(ImGui.SliderFloat($"##{key}_weight", ref weight, -50.0f, 50.0f, "%.1f"))
                        map.Weight = weight;

                    // spacer
                    ImGui.TableNextColumn();

                    ImGui.PopID();
                }
            }
            ImGui.EndTable();

            SettingsHelpers.DrawResetWeightsButton("maps", ref mapResetConfirm, () => {
                Main?.ResetMapWeightsToDefaults();
            });
            ImGui.SameLine();
            SettingsHelpers.DrawResetToDefaultsButton("maps", ref defaultWeightsConfirm);
            }
        };

    }

    [JsonIgnore]
    private string mapSearchFilter = "";
    // Quick-filter chips for the map table (combine with the search box).
    private bool mapFilterHighlight = false;
    private bool mapFilterWeighted = false;
    [JsonIgnore]
    private bool mapResetConfirm = false;
    [JsonIgnore]
    private bool defaultWeightsConfirm = false;
    // Backing value for the "set all" weight slider in the map table header.
    [JsonIgnore]
    // Starts neutral (0) so nudging the "set all" slider doesn't silently jump every map to a non-zero
    // weight. The user drags it to the value they actually want.
    private float bulkMapWeight = 0f;

    // Maps flagged DNT-UNUSED (in the name, key or any id) are dev/unused placeholders and are hidden.
    private static bool MapIsUnused(string key, Map map) {
        const string marker = "DNT-UNUSED";
        if (key != null && key.Contains(marker))
            return true;
        if (map.Name != null && map.Name.Contains(marker))
            return true;
        return map.IDs != null && map.IDs.Any(id => id != null && id.Contains(marker));
    }

    [JsonIgnore]
    public CustomNode CustomMapSettings { get; set; }

    [Menu("Map Types", 1032, CollapsedByDefault = false)]
    [JsonIgnore]
    public EmptyNode MapTypesHeader { get; set; }
    [JsonIgnore]    
    [Menu(null, parentIndex = 1032)]
    public CustomNode MapTable { get; set; }

    public bool HighlightMapNodes { get; set; } = true;
    public bool ShowMapNames { get; set; } = true;
    public bool ShowMapNamesOnUnlockedNodes { get; set; } = true;
    public bool ShowMapNamesOnLockedNodes { get; set; } = true;
    public bool ShowMapNamesOnHiddenNodes { get; set; } = true;
    public Color GoodNodeColor { get; set; } = Color.FromArgb(200, 50, 255, 50);
    public Color BadNodeColor { get; set; } = Color.FromArgb(200, 255, 50, 50);

    public ObservableDictionary<string, Map> Maps { get; set; } = [];

    // Nested under Maps because biome and content weights/colors are map-related.
    public BiomeSettings Biomes { get; set; } = new BiomeSettings();
    public ContentSettings Content { get; set; } = new ContentSettings();

    [Menu("Special Maps")]
    public SpecialMapSettings SpecialMaps { get; set; } = new SpecialMapSettings();
}

// A user-defined special map: matched by exact in-game name, with its own marker style. Persisted
// (Newtonsoft): System.Drawing.Color and the SpriteIcon enum both round-trip like other settings.
public class SpecialMapEntry
{
    public string Name { get; set; } = "";
    public Color Color { get; set; } = Color.FromArgb(255, 200, 80, 255);
    public float Scale { get; set; } = 1.0f;
    public SpriteIcon Icon { get; set; } = SpriteIcon.Exclamation;
}

/// MARK: SpecialMapSettings
[Submenu(CollapsedByDefault = true)]
public class SpecialMapSettings
{
    // User-defined special maps (in addition to auto-detected ones). Each carries its own marker style.
    [Newtonsoft.Json.JsonProperty(ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace)]
    public List<SpecialMapEntry> Maps { get; set; } = new() {
        new SpecialMapEntry { Name = "The Jade Isles" },
        new SpecialMapEntry { Name = "The Matriarchal Halls" },
    };

    // When set, every special map (auto-detected or user-added) is weighted at MaxWeight so it always
    // ranks highest. Applied in Node.RecalculateWeight via the mirrored static config.
    public bool UseMaxWeight { get; set; } = true;
    public float MaxWeight { get; set; } = 50f;

    [JsonIgnore]
    public string _input = "";

    [JsonIgnore]
    public CustomNode CustomSpecialMapSettings { get; set; }

    public SpecialMapSettings()
    {
        CustomSpecialMapSettings = new CustomNode { DrawDelegate = DrawEditor };
    }

    // Case-insensitive lookup of a user entry by map name (null if not user-defined).
    public SpecialMapEntry Find(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var m in Maps)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                return m;
        return null;
    }

    public IEnumerable<string> Names() => Maps.Select(m => m.Name);

    private void DrawEditor()
    {
        ImGui.Spacing();
        ImGui.TextWrapped("Maps listed here are always treated as special (own marker icon, no covering fill), " +
                          "in addition to the auto-detected ones. Use the exact in-game name, e.g. \"Lost Towers\".");
        ImGui.Spacing();

        // Structural changes (name list, weight option) need the cache resynced + recalculated.
        // Per-map color/size/icon are read live by the renderer, so they need no refresh.
        bool structuralChanged = false;

        bool useMax = UseMaxWeight;
        if (ImGui.Checkbox("Calculate special maps at max weight", ref useMax)) { UseMaxWeight = useMax; structuralChanged = true; }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("When checked, every special map is weighted at the value to the right so it always ranks highest.");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        float maxW = MaxWeight;
        if (ImGui.DragFloat("##specialmaxweight", ref maxW, 1f, 0f, 500f, "%.0f")) { MaxWeight = maxW; structuralChanged = true; }

        ImGui.Separator();

        ImGui.SetNextItemWidth(220);
        bool submit = ImGui.InputTextWithHint("##specialadd", "Map name to add...", ref _input, 64, ImGuiInputTextFlags.EnterReturnsTrue);
        ImGui.SameLine();
        if ((ImGui.Button("Add##special") || submit) && !string.IsNullOrWhiteSpace(_input))
        {
            var name = _input.Trim();
            if (Find(name) == null) { Maps.Add(new SpecialMapEntry { Name = name }); structuralChanged = true; }
            _input = "";
        }

        if (Maps.Count > 0 && ImGui.BeginTable("special_maps_table", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthStretch, 200);
            ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 45);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 40);
            ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthFixed, 30);
            ImGui.TableHeadersRow();

            int removeAt = -1;
            for (int i = 0; i < Maps.Count; i++)
            {
                var e = Maps[i];
                ImGui.TableNextRow();

                ImGui.TableNextColumn();
                ImGui.AlignTextToFramePadding();
                ImGui.Text(e.Name);

                ImGui.TableNextColumn();
                Vector4 cv = new(e.Color.R / 255f, e.Color.G / 255f, e.Color.B / 255f, e.Color.A / 255f);
                if (ImGui.ColorEdit4($"##col{i}", ref cv, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
                    e.Color = Color.FromArgb((int)(cv.W * 255), (int)(cv.X * 255), (int)(cv.Y * 255), (int)(cv.Z * 255));

                ImGui.TableNextColumn();
                ImGui.SetNextItemWidth(110);
                float s = e.Scale;
                if (ImGui.SliderFloat($"##size{i}", ref s, 0f, 3f, "%.2f")) e.Scale = s;

                ImGui.TableNextColumn();
                SettingsHelpers.IconPicker($"specialicon{i}", e.Icon, ic => e.Icon = ic);

                ImGui.TableNextColumn();
                if (ImGui.Button($"x##rm{i}")) removeAt = i;
            }
            ImGui.EndTable();

            if (removeAt >= 0) { Maps.RemoveAt(removeAt); structuralChanged = true; }
        }

        if (structuralChanged)
            Main?.RequestSpecialMapsRefresh();
    }
}

/// MARK: BiomeSettings
[Submenu(CollapsedByDefault = true)]
public class BiomeSettings
{
    [JsonIgnore]
    public CustomNode CustomBiomeSettings { get; set; }
    public ObservableDictionary<string, Biome> Biomes { get; set; } = [];
    public BiomeSettings() {

        CustomBiomeSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.Spacing();
                ImGui.TextWrapped("CTRL+Click on a slider to manually enter a value.");
                ImGui.Spacing();

                // Profile context
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
                ImGui.Text($"Profile: {(Main != null && Main.Settings.Profiles != null ? Main.Settings.Profiles.ActiveProfile : "Default")}");
                ImGui.PopStyleColor();

                if (ImGui.BeginTable("biomes_table", 3, ImGuiTableFlags.Borders|ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("Biome", ImGuiTableColumnFlags.WidthFixed, 250);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                    ImGui.TableHeadersRow();

                    foreach (var (key,biome) in Biomes.OrderBy(x => x.Value.Name))
                    {
                        ImGui.PushID($"Biome_{key}");
                        ImGui.TableNextRow();

                        // Name
                        ImGui.TableNextColumn();
                        ImGui.Text(key);

                        // Weight
                        ImGui.TableNextColumn();
                        float weight = biome.Weight;                        
                        ImGui.SetNextItemWidth(100);
                        if(ImGui.SliderFloat($"##{biome}_weight", ref weight, -5.0f, 5.0f, "%.2f"))
                            biome.Weight = weight;

                        ImGui.PopID();
                    }
                }
                ImGui.EndTable();

                SettingsHelpers.DrawResetWeightsButton("biomes", ref biomeResetConfirm, () => {
                    Main?.ResetBiomeWeightsToDefaults();
                });
            }
        };
    }

    [JsonIgnore]
    private bool biomeResetConfirm = false;
}

// MARK: ContentSettings
[Submenu(CollapsedByDefault = true)]
public class ContentSettings
{
    [JsonIgnore]
    public CustomNode CustomContentSettings { get; set; }
    public ObservableDictionary<string, Content> ContentTypes { get; set; } = [];

    public ContentSettings() {

        CustomContentSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.Spacing();
                ImGui.TextWrapped("CTRL+Click on a slider to manually enter a value.");
                ImGui.Spacing();

                // Profile context
                ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.6f, 0.6f, 0.6f, 1.0f));
                ImGui.Text($"Profile: {(Main != null && Main.Settings.Profiles != null ? Main.Settings.Profiles.ActiveProfile : "Default")}");
                ImGui.PopStyleColor();

                // Search filter for the content list.
                string contentFilter = contentSearchFilter;
                ImGui.SetNextItemWidth(250);
                if (ImGui.InputTextWithHint("##contentsearch", "Search content...", ref contentFilter, 100))
                    contentSearchFilter = contentFilter;
                ImGui.SameLine();
                if (ImGui.Button("Clear##contentsearchclear"))
                    contentSearchFilter = "";
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                ImGui.Checkbox("Favorites##contentchip", ref contentFilterFav);

                if (ImGui.BeginTable("content_table", 4, ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Content Type", ImGuiTableColumnFlags.WidthFixed, 250);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Fav", ImGuiTableColumnFlags.WidthFixed, 30);
                    ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                    ImGui.TableHeadersRow();

                    var visibleContent = ContentTypes
                        .Where(x => string.IsNullOrEmpty(contentSearchFilter)
                            || (x.Value.Name?.Contains(contentSearchFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        .Where(x => !contentFilterFav || x.Value.Favorite)
                        .OrderBy(x => x.Value.Name)
                        .ToList();
                    var contentValues = visibleContent.Select(x => x.Value).ToList();

                    // "Set/Toggle all" row applies to every content type currently shown (respects search).
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text("Set all");
                    ImGui.TableNextColumn();
                    SettingsHelpers.SetAllWeight("contentweight", ref bulkContentWeight, -100f, 100f, "%.0f", contentValues, (c, w) => c.Weight = w);
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    SettingsHelpers.ToggleAll("contentfavorite", contentValues, c => c.Favorite, (c, v) => c.Favorite = v);
                    ImGui.TableNextColumn(); // spacer

                    foreach (var (key,content) in visibleContent)
                    {
                        ImGui.PushID($"Content_{key}");
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.Text(content.Name);

                        ImGui.TableNextColumn();
                        float weight = content.Weight;
                        ImGui.SetNextItemWidth(100);
                        if(ImGui.SliderFloat($"##{key}_weight", ref weight, -100.0f, 100.0f, "%.0f"))
                            content.Weight = weight;

                        // Favorite
                        ImGui.TableNextColumn();
                        SettingsHelpers.CenterControl(30f);
                        bool contentFavorite = content.Favorite;
                        if(ImGui.Checkbox($"##{key}_favorite", ref contentFavorite))
                            content.Favorite = contentFavorite;

                        ImGui.TableNextColumn();

                        ImGui.PopID();
                    }
                }
                ImGui.EndTable();

                SettingsHelpers.DrawResetWeightsButton("content", ref contentResetConfirm, () => {
                    Main?.ResetContentWeightsToDefaults();
                });
                ImGui.SameLine();
                SettingsHelpers.DrawResetToDefaultsButton("content", ref defaultWeightsConfirm);
            }
        };
    }

    [JsonIgnore]
    private bool defaultWeightsConfirm = false;
    [JsonIgnore]
    private bool contentResetConfirm = false;
    [JsonIgnore]
    private string contentSearchFilter = "";
    // Quick-filter chip for the content table (favorites only).
    private bool contentFilterFav = false;
    // Backing value for the "set all" weight slider in the content table header.
    [JsonIgnore]
    // Neutral default - see bulkMapWeight.
    private float bulkContentWeight = 0f;
}

// MARK: WaypointSettings
[Submenu(CollapsedByDefault = true)]
public class WaypointSettings
{
    [JsonIgnore]
    public CustomNode CustomWaypointSettings { get; set; }
    public bool PanelIsOpen { get; set; } = false;
    public bool ShowWaypoints { get; set; } = true;
    public bool ShowWaypointArrows { get; set; } = true;
    public bool InverWaypointArrowsColors { get; set; } = true;
    public bool AutoWaypointFavorites { get; set; } = false;
    public bool AutoRemoveCompletedWaypoints { get; set; } = true;

    public int WaypointPanelMaxItems { get; set; } = 160;
    public int WaypointPanelMaxSteps { get; set; } = 0; // 0 = unlimited
    public string WaypointPanelSortBy { get; set; } = "Name";
    public string WaypointPanelSortBy2 { get; set; } = "Steps";
    public bool WaypointsUseRegex { get; set; } = false;
    public bool ShowUnlockedOnly { get; set; } = false;
    
    public string WaypointPanelFilter { get; set; } = "";
    public PanelRect PanelRect { get; set; } = new PanelRect();
    public ObservableDictionary<string, Waypoint> Waypoints { get; set; } = [];
    public WaypointSettings() {    
        CustomWaypointSettings = new CustomNode
        {
            DrawDelegate = () =>
            {

                if (ImGui.BeginTable("waypoint_options_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 60);                                                               
                    ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     
        

                    // show waypoints
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    bool _show = ShowWaypoints;
                    if(ImGui.Checkbox($"##show_waypoints", ref _show))                        
                        ShowWaypoints = _show;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Waypoints on Atlas");


                    // show waypoints arrows
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool _showArrows = ShowWaypointArrows;
                    if(ImGui.Checkbox($"##show_arrows", ref _showArrows))                        
                        ShowWaypointArrows = _showArrows;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Waypoint Arrows on Atlas");

                    ImGui.TableNextRow();
                    
                    // invert waypoints arrows colors
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool _invertWaypointArrowColors = InverWaypointArrowsColors;
                    if(ImGui.Checkbox($"##invert_waypoint_arrow_colors", ref _invertWaypointArrowColors))
                        InverWaypointArrowsColors = _invertWaypointArrowColors;

                    ImGui.TableNextColumn();
                    ImGui.Text("Invert Waypoint Arrows Colors on Atlas");

                    ImGui.TableNextRow();

                    // auto-create waypoints for favorite maps
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool _autoFavorites = AutoWaypointFavorites;
                    if(ImGui.Checkbox($"##auto_waypoint_favorites", ref _autoFavorites))
                        AutoWaypointFavorites = _autoFavorites;

                    ImGui.TableNextColumn();
                    ImGui.Text("Auto Create Waypoints for Favorite Maps");

                    ImGui.TableNextRow();

                    // auto-remove waypoints whose map is completed/visited
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();

                    bool _autoRemoveCompleted = AutoRemoveCompletedWaypoints;
                    if(ImGui.Checkbox($"##auto_remove_completed_waypoints", ref _autoRemoveCompleted))
                        AutoRemoveCompletedWaypoints = _autoRemoveCompleted;

                    ImGui.TableNextColumn();
                    ImGui.Text("Auto Remove Completed Waypoints");

                    ImGui.TableNextRow();

                }
                ImGui.EndTable();
            }
        };
    }
}

public static class SettingsHelpers {
    // "Toggle all" checkbox: reflects whether every item is on; clicking sets all to new value.
    public static void ToggleAll<T>(string id, System.Collections.Generic.IEnumerable<T> items, Func<T,bool> get, Action<T,bool> set) {
        var list = items as System.Collections.Generic.ICollection<T> ?? items.ToList();
        bool all = list.Count > 0 && list.All(get);
        if (ImGui.Checkbox($"##all_{id}", ref all))
            foreach (var i in list) set(i, all);
    }

    // "Set all" color picker: editing applies the chosen color to every item. Swatch is caller-owned.
    public static void SetAllColor<T>(string id, ref Vector4 value, System.Collections.Generic.IEnumerable<T> items, Action<T,Color> set) {
        if (ImGui.ColorEdit4($"##setall_{id}", ref value, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs)) {
            Color c = Color.FromArgb((int)(value.W * 255), (int)(value.X * 255), (int)(value.Y * 255), (int)(value.Z * 255));
            foreach (var i in items) set(i, c);
        }
    }

    // "Set all" weight slider: dragging applies the value to every item. Slider is caller-owned.
    public static void SetAllWeight<T>(string id, ref float value, float min, float max, string fmt, System.Collections.Generic.IEnumerable<T> items, Action<T,float> set) {
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderFloat($"##setallw_{id}", ref value, min, max, fmt))
            foreach (var i in items) set(i, value);
    }

    public static void CenterControl(float width) {
        float availableWidth = ImGui.GetContentRegionAvail().X;
        float cursorPosX = ImGui.GetCursorPosX() + (availableWidth - width) / 2.0f;
        ImGui.SetCursorPosX(cursorPosX);
    }

    // Nested (child) collapsing header, visually distinct from a top-level section: the bar is dimmer
    // and inset, and the body draws indented, so hierarchy reads at a glance. Pair every call with
    // SubHeaderEnd() (unconditionally - the indent must be popped whether the header is open or not).
    public static bool SubHeader(string label, bool defaultOpen = false) {
        ImGui.Indent(14f);
        ImGui.PushStyleColor(ImGuiCol.Header,        new Vector4(0.17f, 0.21f, 0.27f, 0.60f));
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, new Vector4(0.25f, 0.31f, 0.40f, 0.80f));
        ImGui.PushStyleColor(ImGuiCol.HeaderActive,  new Vector4(0.31f, 0.39f, 0.49f, 0.95f));
        bool open = ImGui.CollapsingHeader(label, defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None);
        ImGui.PopStyleColor(3);
        return open;
    }

    public static void SubHeaderEnd() => ImGui.Unindent(14f);

    public static void IconPicker(string id, SpriteIcon current, Action<SpriteIcon> set) {
        IntPtr tex = Main.customIconsId;
        string popupId = $"iconpick_{id}";
        var white = new Vector4(1, 1, 1, 1);
        var (cu0, cu1) = SpriteAtlas.GetUVPair(current);
        if (tex != IntPtr.Zero) {
            if (ImGui.ImageButton($"##btn_{id}", tex, new Vector2(20, 20), cu0, cu1, Vector4.Zero, white))
                ImGui.OpenPopup(popupId);
        } else {
            if (ImGui.Button($"{(int)current}##{id}", new Vector2(24, 20)))
                ImGui.OpenPopup(popupId);
        }
        if (ImGui.BeginPopup(popupId)) {
            for (int i = 0; i < SpriteAtlas.Count; i++) {
                var icon = (SpriteIcon)i;
                var (u0, u1) = SpriteAtlas.GetUVPair(icon);
                // Tint the current selection so it stands out in the grid.
                bool isCurrent = icon == current;
                if (isCurrent) ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(1, 0, 1, 1));
                bool clicked = tex != IntPtr.Zero
                    ? ImGui.ImageButton($"##pick_{id}_{i}", tex, new Vector2(32, 32), u0, u1, Vector4.Zero, white)
                    : ImGui.Button($"{i}##pick_{id}_{i}", new Vector2(36, 36));
                if (isCurrent) ImGui.PopStyleColor();
                if (clicked) { set(icon); ImGui.CloseCurrentPopup(); }
                if (i % SpriteAtlas.Columns != SpriteAtlas.Columns - 1) ImGui.SameLine();
            }
            ImGui.EndPopup();
        }
    }

    // "Set all" icon picker: picking applies the icon to every item. Selection is caller-owned.
    public static void SetAllIcon<T>(string id, ref SpriteIcon value, System.Collections.Generic.IEnumerable<T> items, Action<T,SpriteIcon> set) {
        var captured = value;        // ref params can't be captured in the lambda
        SpriteIcon chosen = captured;
        IconPicker(id, captured, i => chosen = i);
        if (chosen != captured) {
            value = chosen;
            foreach (var item in items) set(item, chosen);
        }
    }

    // Two-step "Reset All Weights": first click arms, second (red) click runs reset, Cancel disarms.
    public static void DrawResetWeightsButton(string id, ref bool confirming, Action reset) {
        ImGui.Spacing();
        if (!confirming) {
            if (ImGui.Button($"Reset All Weights##{id}_reset"))
                confirming = true;
        } else {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.1f, 0.1f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button($"Are you sure? Click to confirm##{id}_confirm")) {
                reset();
                confirming = false;
            }
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            if (ImGui.Button($"Cancel##{id}_cancel"))
                confirming = false;
        }
    }

    // "Reset to Plugin Defaults" button with confirm; reloads bundled weights from exilemaps_weights.json.
    public static void DrawResetToDefaultsButton(string id, ref bool confirming) {
        ImGui.Spacing();
        if (!confirming) {
            if (ImGui.Button($"Reset to Plugin Defaults##{id}_default"))
                confirming = true;
            if (ImGui.IsItemHovered()) {
                ImGui.BeginTooltip();
                ImGui.Text("Restore the map and content weights that ship with the plugin.");
                ImGui.EndTooltip();
            }
        } else {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.7f, 0.1f, 0.1f, 1.0f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.85f, 0.15f, 0.15f, 1.0f));
            if (ImGui.Button($"Reset to plugin defaults? Click to confirm##{id}_default_confirm")) {
                Main?.ResetWeightsToDefaults();
                confirming = false;
            }
            ImGui.PopStyleColor(2);
            ImGui.SameLine();
            if (ImGui.Button($"Cancel##{id}_default_cancel"))
                confirming = false;
        }
    }

    // Draws "Import Old Settings" button and the one-time auto-detect banner.
    public static void DrawImportExportSettingsButtons() {
        DrawOldSettingsImportBanner();

        if (ImGui.Button("Import Old Settings##import_old_settings"))
            Main.ImportOldSettings();
        if (ImGui.IsItemHovered()) {
            ImGui.BeginTooltip();
            ImGui.Text("Convert settings from the previous (pre-rework) version of the plugin.\nMap/content/biome weights load into a NEW profile (leaving existing\nprofiles intact); all other settings apply directly.\n\nSettings files are normally stored in your ExileCore folder under\nconfig\\global\\ (e.g. config\\global\\ExileMaps_settings.json).");
            ImGui.EndTooltip();
        }
    }

    // One-time prompt shown when pre-rework ("v1") settings were detected on disk at load (see
    // ExileMapsCore.DetectOldSettings). Import converts the backed-up v1 file into an "Original Settings"
    // profile and restores keybinds/other categories; Dismiss hides it for good. Both stop the prompt
    // permanently via OldSettingsMigrationHandled.
    private static void DrawOldSettingsImportBanner() {
        if (Main == null || !Main.OldSettingsPendingImport)
            return;

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.85f, 0.3f, 1f));
        ImGui.TextWrapped("Settings from the previous version were detected. Import them into an 'Original Settings' profile? This also restores your old keybinds and other settings.");
        ImGui.PopStyleColor();

        if (ImGui.Button("Import##old_settings_banner"))
            Main.ImportDetectedOldSettings();
        ImGui.SameLine();
        if (ImGui.Button("Dismiss##old_settings_banner"))
            Main.DismissDetectedOldSettings();
        ImGui.Separator();
    }
}

// MARK: ExpeditionSettings
public class ExpeditionSettings
{
    [JsonIgnore]
    public CustomNode RumorWeightsEditor { get; set; }

    // Rumour text -> editable weight row. Seeded from json/rumors.json, persisted per profile.
    public ObservableDictionary<string, Rumor> RumorWeights { get; set; } = [];

    public ColorNode HighlightColor { get; set; } = new ColorNode(Color.FromArgb(220, 90, 200, 255));

    [JsonIgnore]
    public PanelRect PanelRect { get; set; } = new PanelRect();

    // editor-only ui state
    private string rumorSearchFilter = "";
    private float bulkRumorWeight = 25f;

    public ExpeditionSettings()
    {
        RumorWeightsEditor = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.Spacing();
                ImGui.TextWrapped("CTRL+Click a slider to type a value. Higher weight ranks an expedition higher in the panel.");
                ImGui.Spacing();

                string filter = rumorSearchFilter;
                ImGui.SetNextItemWidth(250);
                if (ImGui.InputTextWithHint("##rumorsearch", "Search rumors/content...", ref filter, 100))
                    rumorSearchFilter = filter;
                ImGui.SameLine();
                if (ImGui.Button("Clear##rumorsearchclear"))
                    rumorSearchFilter = "";

                var visible = RumorWeights
                    .Where(x => string.IsNullOrEmpty(rumorSearchFilter)
                        || (x.Value.Content?.Contains(rumorSearchFilter, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (x.Value.Text?.Contains(rumorSearchFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .OrderByDescending(x => x.Value.Weight)
                    .ToList();
                var rumorValues = visible.Select(x => x.Value).ToList();

                if (ImGui.BeginTable("rumor_table", 3, ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Rumor", ImGuiTableColumnFlags.WidthFixed, 200);
                    ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 250);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 160);
                    ImGui.TableHeadersRow();

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Text("Set all");
                    ImGui.TableNextColumn();
                    ImGui.TableNextColumn();
                    SettingsHelpers.SetAllWeight("rumorweight", ref bulkRumorWeight, 0f, 100f, "%.0f", rumorValues, (r, w) => r.Weight = w);

                    foreach (var (key, rumor) in visible)
                    {
                        ImGui.PushID($"Rumor_{key}");
                        ImGui.TableNextRow();

                        ImGui.TableNextColumn();
                        ImGui.Text(rumor.Text);
                        if (ImGui.IsItemHovered() && !string.IsNullOrEmpty(rumor.Description))
                            ImGui.SetTooltip(rumor.Description);

                        ImGui.TableNextColumn();
                        ImGui.Text(rumor.Content);

                        ImGui.TableNextColumn();
                        float w = rumor.Weight;
                        ImGui.SetNextItemWidth(150);
                        if (ImGui.SliderFloat("##rw", ref w, 0f, 100f, "%.0f"))
                            rumor.Weight = w;

                        ImGui.PopID();
                    }
                    ImGui.EndTable();
                }
            }
        };
    }
}