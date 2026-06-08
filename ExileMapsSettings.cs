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
            }
        }

        // Overlays a stored profile's values onto the live Map/Content/Biome objects. The live
        // dictionaries hold the map *definitions* (name/id/icon scraped from the game) and are the
        // working state that rendering reads — we must NOT clear them, only update matching entries.
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
                    Biomes = new Dictionary<string, BiomeProfileEntry>(source.Biomes)
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
    public HotkeySettings Keybinds { get; set; } = new HotkeySettings();
    public GraphicSettings Graphics { get; set; } = new GraphicSettings();
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
    public RangeNode<int> AtlasRange { get; set; } = new(1500, 100, 20000);
    [Menu("Use Atlas Range for Node Connections", "Drawing node connections is performance intensive. By default it uses a range of 1000, but you can change it to use the Atlas range.")]
    public ToggleNode UseAtlasRange { get; set; } = new ToggleNode(false);

    [Menu("Process Visited Map Nodes")]
    public ToggleNode ProcessVisitedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Unlocked Map Nodes")]
    public ToggleNode ProcessUnlockedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Locked Map Nodes")]
    public ToggleNode ProcessLockedNodes { get; set; } = new ToggleNode(true);

    [Menu("Process Hidden Map Nodes")]
    public ToggleNode ProcessHiddenNodes { get; set; } = new ToggleNode(true);

    [Menu("Draw Connections for Visited Map Nodes")]
    public ToggleNode DrawVisitedNodeConnections { get; set; } = new ToggleNode(true);

    [ConditionalDisplay(nameof(ProcessHiddenNodes), true)]
    [Menu("Draw Connections for Hidden Map Nodes")]
    public ToggleNode DrawHiddenNodeConnections { get; set; } = new ToggleNode(true);

    [Menu("Recalculate Node Weights on Refresh", "Recompute each node's weight when the map cache refreshes so weight (and weight-based colors/sorting) reflect live content and tablet mods. Disable to save performance if weights don't need to update after the first scan.")]
    public ToggleNode RecalculateNodeWeightsOnRefresh { get; set; } = new ToggleNode(true);

    [Menu("Debug Mode")]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);

    [Menu("Show Panel Buttons", "Show a floating button bar at the bottom-center of the atlas to open the Waypoints, Atlas Overview and Tours panels.")]
    public ToggleNode ShowPanelButtons { get; set; } = new ToggleNode(true);

    [Menu("Show Atlas Search Box", "Show a floating search box at the top-center of the atlas that pings matching nodes (same as the Atlas Overview search).")]
    public ToggleNode ShowAtlasSearchBox { get; set; } = new ToggleNode(true);

}
[Submenu(CollapsedByDefault = true)]
public class HotkeySettings
{
    // HotkeyNodeV2 renders as a uniform label + picker button (the deprecated HotkeyNode rendered as a
    // single button sized to its label, producing the inconsistent widths). [JsonIgnore] separator
    // CustomNodes group related binds. Most default to F13 (effectively unbound) — the user sets them.

    [JsonIgnore]
    public CustomNode SepGeneral { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("General") };

    [Menu("Toggle Atlas Drawing Hotkey", "Show/hide all atlas overlay drawing. Default: Scroll Lock")]
    public HotkeyNodeV2 ToggleDrawingHotkey { get; set; } = new HotkeyNodeV2(Keys.Scroll);

    [Menu("Map Cache Refresh Hotkey", "Default: Home")]
    public HotkeyNodeV2 RefreshMapCacheHotkey { get; set; } = new HotkeyNodeV2(Keys.Home);

    [Menu("Quick Edit Node Hotkey", "Hover a node on the Atlas and press to open a popup that edits that map type and its content without opening settings. Unbound by default - set a key.")]
    public HotkeyNodeV2 QuickEditNodeHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

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
    public HotkeyNodeV2 AddTourStopHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

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
    public HotkeyNodeV2 UpdateMapsKey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Update Content Type Data", "Re-scrape content types from game files. Unbound by default - set a key.")]
    public HotkeyNodeV2 UpdateContentKey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [Menu("Update Biome Data", "Re-scrape biomes from game files. Unbound by default - set a key.")]
    public HotkeyNodeV2 UpdateBiomesKey { get; set; } = new HotkeyNodeV2(Keys.F13);

    [JsonIgnore]
    public CustomNode SepDebug { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Debug") };

    [Menu("Debug Node Hotkey", "Hover a node on the Atlas and press to open a popup showing that node's debug info and element flags. Unbound by default - set a key.")]
    public HotkeyNodeV2 DebugNodeHotkey { get; set; } = new HotkeyNodeV2(Keys.F13);

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
    public RangeNode<int> MapCacheRefreshRate { get; set; } = new RangeNode<int>(5, 1, 60);

    [JsonIgnore]
    public CustomNode SepNodes { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Nodes") };

    [Menu("Node Radius", "Radius of the circles used to highlight map nodes")]
    public RangeNode<float> NodeRadius { get; set; } = new RangeNode<float>(1.5f, 0, 10);

    [Menu("Use Icons for Nodes", "Draw the per-map sprite icon for map nodes. Disable to use plain filled circles (the original look).")]
    public ToggleNode UseNodeIcons { get; set; } = new ToggleNode(true);

    [Menu("Lay Icons Flat", "Vertically squash node icons so round sprites read as flat discs lying on the tilted atlas plane. 0 = upright. Tune by eye.")]
    public RangeNode<float> IconFlatten { get; set; } = new RangeNode<float>(0.180f, 0f, 0.9f);

    [Menu("Content Ring Icon Size", "Scale of the outline-circle icon used for content rings (icon mode only).")]
    public RangeNode<float> ContentRingIconScale { get; set; } = new RangeNode<float>(1.0f, 0.3f, 3f);

    [Menu("Content Ring Icon Spacing", "Extra gap between stacked content ring icons when a node has multiple content types (icon mode only).")]
    public RangeNode<float> ContentRingIconSpacing { get; set; } = new RangeNode<float>(1.5f, 0.5f, 4f);

    [Menu("Content Ring Width", "Width of the rings used to indicate map content")]
    public RangeNode<float> RingWidth { get; set; } = new RangeNode<float>(5.0f, 0, 10);

    [Menu("Content Radius", "Radius of the rings used to indicate map content")]
    public RangeNode<float> RingRadius { get; set; } = new RangeNode<float>(1f, 0, 10);

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
    public ColorNode VisitedLineColor { get; set; } = new ColorNode(Color.FromArgb(80, 255, 255, 255));

    [Menu("Unlocked Line Color", "Color of the map connection lines when an adjacent node is unlocked.")]
    public ColorNode UnlockedLineColor { get; set; } = new ColorNode(Color.FromArgb(170, 90, 255, 90));

    [Menu("Locked Line Color", "Color of the map connection lines when no adjacent nodes are unlocked.")]
    public ColorNode LockedLineColor { get; set; } = new ColorNode(Color.FromArgb(170, 255, 90, 90));

    [Menu("Distance Marker Scale", "Interpolation factor for distance markers on lines")]
    public RangeNode<float> LabelInterpolationScale { get; set; } = new RangeNode<float>(0.5f, 0, 1);

    [JsonIgnore]
    public CustomNode SepWaypoints { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Waypoints") };

    [Menu("Draw paths to waypoints", "Shows the shortest path from the nearest visited node to waypoints")]
    public ToggleNode ShowPaths { get; set; } = new ToggleNode(true);

    [Menu("Waypoint Line Width", "Width of the map waypoint path (px).")]
    public RangeNode<float> WaypointLineWidth { get; set; } = new RangeNode<float>(9.0f, 0, 24);

    // Persisted enum (no [Menu]; edited via WaypointPathTexturePicker below, like SpecialMapIcon).
    public PathTextureStyle WaypointPathTexture { get; set; } = PathTextureStyle.Comet;

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
    public RangeNode<float> WaypointDashSpeed { get; set; } = new RangeNode<float>(1.5f, 0, 8);

    [Menu("Waypoint Texture Scale", "Thickness multiplier applied to the path width when using a textured path. Plain-line mode ignores this.")]
    public RangeNode<float> WaypointPathTextureScale { get; set; } = new RangeNode<float>(2.0f, 0.5f, 6);

    [Menu("Waypoint Arrow Min Distance", "Only show the off-screen waypoint arrow when the waypoint is farther than this (px) from screen center. Lower = arrow shows for nearer waypoints.")]
    public RangeNode<float> WaypointArrowMinDistance { get; set; } = new RangeNode<float>(1200.0f, 50, 1500);

    [Menu("Favorite Marker Color", "Color of the star marker drawn above favorite map nodes")]
    public ColorNode FavoriteColor { get; set; } = new ColorNode(Color.FromArgb(255, 255, 215, 0));

    [Menu("Favorite Marker Scale", "Size of the star marker drawn above favorite map nodes")]
    public RangeNode<float> FavoriteIconScale { get; set; } = new RangeNode<float>(1.0f, 0, 3);

    [JsonIgnore]
    public CustomNode SepSpecialMaps { get; set; } = new CustomNode { DrawDelegate = () => ImGui.SeparatorText("Special Maps") };

    [Menu("Show Atlas Point Marker", "Draw a small silver star above maps that grant an atlas passive point.")]
    public ToggleNode ShowAtlasPointIndicator { get; set; } = new ToggleNode(true);

    [Menu("Show Atlas Quest Marker", "Draw a small golden exclamation above maps that have atlas quest content.")]
    public ToggleNode ShowAtlasQuestIndicator { get; set; } = new ToggleNode(true);

    [Menu("Atlas Marker Size", "Pixel size of the atlas-point star and atlas-quest exclamation markers.")]
    public RangeNode<float> AtlasIndicatorSize { get; set; } = new RangeNode<float>(20.0f, 8, 48);

    // Per-atlas-point-type marker color. Key: "Generic" plus each content type. Persisted (Newtonsoft);
    // edited via AtlasPointStylePicker. Rendering falls back to a default if a key is missing.
    public Dictionary<string, ColorNode> AtlasPointColors { get; set; } = new() {
        ["Generic"]   = new ColorNode(Color.FromArgb(255, 200, 200, 205)), // silver
        ["Breach"]    = new ColorNode(Color.FromArgb(255, 180, 70, 255)),  // purple
        ["Abyss"]     = new ColorNode(Color.FromArgb(255, 60, 200, 90)),   // green
        ["Incursion"] = new ColorNode(Color.FromArgb(255, 255, 150, 40)),  // orange
        ["Delirium"]  = new ColorNode(Color.FromArgb(255, 240, 240, 245)), // white
        ["Ritual"]    = new ColorNode(Color.FromArgb(255, 220, 50, 50)),   // red
    };

    // Per-atlas-point-type marker icon. Same keys as AtlasPointColors.
    public Dictionary<string, SpriteIcon> AtlasPointIcons { get; set; } = new() {
        ["Generic"]   = SpriteIcon.Star8,
        ["Breach"]    = SpriteIcon.Star8,
        ["Abyss"]     = SpriteIcon.Star8,
        ["Incursion"] = SpriteIcon.Star8,
        ["Delirium"]  = SpriteIcon.Star8,
        ["Ritual"]    = SpriteIcon.Star8,
    };

    [JsonIgnore]
    public CustomNode AtlasPointStylePicker { get; set; } = new CustomNode
    {
        DrawDelegate = () => SettingsHelpers.AtlasPointStyleTable()
    };

    [Menu("Special Map Indicator", "Draw an icon above 'special' map nodes (wider node art) instead of a solid circle, so the map art isn't covered")]
    public ToggleNode ShowSpecialMapIndicator { get; set; } = new ToggleNode(true);

    [Menu("Special Map Marker Color", "Color of the icon drawn above special map nodes")]
    public ColorNode SpecialMapColor { get; set; } = new ColorNode(Color.FromArgb(255, 200, 80, 255));

    [Menu("Special Map Marker Scale", "Size of the icon drawn above special map nodes")]
    public RangeNode<float> SpecialMapIconScale { get; set; } = new RangeNode<float>(1.0f, 0, 3);

    [Menu("Special Map Width Threshold", "Node art wider than this (px) counts as a 'special' map: skips the covering fill and gets an icon above instead. Raise if normal maps are wrongly flagged, lower if specials are missed (may vary with resolution/UI scale).")]
    public RangeNode<float> SpecialMapWidthThreshold { get; set; } = new RangeNode<float>(90.0f, 40, 200);

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
    public RangeNode<int> MapNameOffsetY { get; set; } = new RangeNode<int>(25, -200, 200);
}

[Submenu(CollapsedByDefault = true)]
public class AtlasOverviewSettings
{
    [Menu("Reachable Step Window", "How many steps from your explored frontier count as 'reachable' for the content tally and atlas-point counter.")]
    public RangeNode<int> ReachableSteps { get; set; } = new RangeNode<int>(10, 1, 50);

    [Menu("Show Progress Readout", "Draw an always-on counter (maps run/completed, atlas points reachable) in an atlas corner.")]
    public ToggleNode ShowProgressReadout { get; set; } = new ToggleNode(true);

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

    // Id of the tour the Add-Tour-Stop hotkey targets. Empty = none active.
    public string ActiveTourId { get; set; } = "";

    public ObservableDictionary<string, Tour> Tours { get; set; } = [];

    // ---- Auto Create Tour ----
    // Content names (matching the Atlas Overview content list) the auto-tour builder targets.
    public List<string> AutoTourContent { get; set; } = new();

    [Menu("Auto Tour Max Steps", "When auto-creating a tour, the most steps allowed between consecutive matching stops before the route stops growing.")]
    public RangeNode<int> AutoTourStepRange { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Auto Tour Off-screen Margin %", "How far beyond the visible screen (as a percent of screen size) a matching node may sit and still be included, so auto-tours don't sprawl across the whole atlas.")]
    public RangeNode<int> AutoTourScreenMarginPct { get; set; } = new RangeNode<int>(25, 0, 200);

    public PanelRect PanelRect { get; set; } = new PanelRect();
}

[Submenu(CollapsedByDefault = true)]
/// <summary>
/// Settings for Map types
/// </summary>
/// MARK: MapSettings
public class MapSettings
{

    public MapSettings() {    
        
        CustomMapSettings = new CustomNode
        {
            
            DrawDelegate = () =>
            {
                
                if (ImGui.BeginTable("map_options_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 40);                                                               
                    ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     
                
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool showMapNames = ShowMapNames;
                    if(ImGui.Checkbox($"##showmapnames", ref showMapNames))                        
                        ShowMapNames = showMapNames;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Map Names");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool showUnlocked = ShowMapNamesOnUnlockedNodes;
                    if(ImGui.Checkbox($"##showunlocked", ref showUnlocked))                        
                        ShowMapNamesOnUnlockedNodes = showUnlocked;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Map Names on Unlocked Nodes");                           
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool showLocked = ShowMapNamesOnLockedNodes;
                    if(ImGui.Checkbox($"##showlocked", ref showLocked))                        
                        ShowMapNamesOnLockedNodes = showLocked;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Map Names on Locked Nodes");                                
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool showHidden = ShowMapNamesOnHiddenNodes;
                    if(ImGui.Checkbox($"##showhidden", ref showHidden))                        
                        ShowMapNamesOnHiddenNodes = showHidden;

                    ImGui.TableNextColumn();
                    ImGui.Text("Show Map Names on Hidden Nodes");    
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool highlightNodes = HighlightMapNodes;
                    if(ImGui.Checkbox($"##map_nodes_highlight", ref highlightNodes))                        
                        HighlightMapNodes = highlightNodes;

                    ImGui.TableNextColumn();
                    ImGui.Text("Draw Map Nodes");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool useNameColors = UseColorsForMapNames;
                    if(ImGui.Checkbox($"##usenamecolors", ref useNameColors))                        
                        UseColorsForMapNames = useNameColors;

                    ImGui.TableNextColumn();
                    ImGui.Text("Use Map Colors for Map Names");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool drawWeight = DrawWeightOnMap;
                    if(ImGui.Checkbox($"##draw_weight", ref drawWeight))                        
                        DrawWeightOnMap = drawWeight;

                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted("Draw Weight on Map");

                    ImGui.TableNextColumn();
                    Color goodColor = GoodNodeColor;
                    Vector4 colorVector = new(goodColor.R / 255.0f, goodColor.G / 255.0f, goodColor.B / 255.0f, goodColor.A / 255.0f);
                    if(ImGui.ColorEdit4($"##goodgoodcolor", ref colorVector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        GoodNodeColor = Color.FromArgb((int)(colorVector.W * 255), (int)(colorVector.X * 255), (int)(colorVector.Y * 255), (int)(colorVector.Z * 255));

                    ImGui.TableNextColumn();
                    ImGui.Text("Good Node Color");    
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    Vector4 badColor = new(BadNodeColor.R / 255.0f, BadNodeColor.G / 255.0f, BadNodeColor.B / 255.0f, BadNodeColor.A / 255.0f);
                    if(ImGui.ColorEdit4($"##goodbadcolor", ref badColor, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        BadNodeColor = Color.FromArgb((int)(badColor.W * 255), (int)(badColor.X * 255), (int)(badColor.Y * 255), (int)(badColor.Z * 255));

                    ImGui.TableNextColumn();
                    ImGui.Text("Bad Node Color"); 
                }

                ImGui.EndTable();       

   

                
      

            }
        };

        MapTable = new CustomNode
        {
            
            DrawDelegate = () =>
            {

            ImGui.Separator();
            ImGui.TextWrapped("CTRL+Click on a slider to manually enter a value.");

            // How weights work — quick reference for the user.
            if (ImGui.CollapsingHeader("How do map weights work?"))
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
            // Quick-filter chips - narrow the list to favorites / highlighted / weighted maps.
            ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
            ImGui.Checkbox("Favorites##mapchip", ref mapFilterFav);
            ImGui.SameLine();
            ImGui.Checkbox("Highlighted##mapchip", ref mapFilterHighlight);
            ImGui.SameLine();
            ImGui.Checkbox("Weight > 0##mapchip", ref mapFilterWeighted);

            if (ImGui.BeginTable("maps_table", 10, ImGuiTableFlags.SizingFixedFit|ImGuiTableFlags.Borders|ImGuiTableFlags.PadOuterX))
            {

                ImGui.TableSetupColumn("Map", ImGuiTableColumnFlags.WidthFixed, 250);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 100);
                ImGui.TableSetupColumn("NClr", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("LClr", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("BGClr", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("Fav", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("WClr", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("WLbl", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("Icon", ImGuiTableColumnFlags.WidthFixed, 30);
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 200);
                ImGui.TableHeadersRow();

                var visibleMaps = Maps
                    .Where(x => !MapIsUnused(x.Key, x.Value))
                    .Where(x => string.IsNullOrEmpty(mapSearchFilter)
                        || (x.Value.Name?.Contains(mapSearchFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Where(x => !mapFilterFav || x.Value.Favorite)
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
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.SetAllColor("nodecolor", ref bulkNodeColor, visibleMapValues, (m, c) => m.NodeColor = c);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.SetAllColor("namecolor", ref bulkNameColor, visibleMapValues, (m, c) => m.NameColor = c);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.SetAllColor("bgcolor", ref bulkBgColor, visibleMapValues, (m, c) => m.BackgroundColor = c);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.ToggleAll("favorite", visibleMapValues, m => m.Favorite, (m, v) => m.Favorite = v);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.ToggleAll("colorbyweight", visibleMapValues, m => m.ColorNodesByWeight, (m, v) => m.ColorNodesByWeight = v);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.ToggleAll("namebyweight", visibleMapValues, m => m.UseWeightColorForName, (m, v) => m.UseWeightColorForName = v);
                ImGui.TableNextColumn();
                SettingsHelpers.CenterControl(30f);
                SettingsHelpers.SetAllIcon("setallicon", ref bulkIcon, visibleMapValues, (m, i) => m.Icon = i);
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

                    ImGui.TableNextColumn();

                    // Node Color
                    SettingsHelpers.CenterControl(30f);
                    Vector4 colorVector = new(map.NodeColor.R / 255.0f, map.NodeColor.G / 255.0f, map.NodeColor.B / 255.0f, map.NodeColor.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{key}_nodecolor", ref colorVector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        map.NodeColor = Color.FromArgb((int)(colorVector.W * 255), (int)(colorVector.X * 255), (int)(colorVector.Y * 255), (int)(colorVector.Z * 255));
                    
                    // Text Color
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    Vector4 nameColorVector = new(map.NameColor.R / 255.0f, map.NameColor.G / 255.0f, map.NameColor.B / 255.0f, map.NameColor.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{key}_namecolor", ref nameColorVector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        map.NameColor = Color.FromArgb((int)(nameColorVector.W * 255), (int)(nameColorVector.X * 255), (int)(nameColorVector.Y * 255), (int)(nameColorVector.Z * 255));
                    
                    // Text BG Color
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    Vector4 bgColorVector = new(map.BackgroundColor.R / 255.0f, map.BackgroundColor.G / 255.0f, map.BackgroundColor.B / 255.0f, map.BackgroundColor.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{key}_bgcolor", ref bgColorVector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
                        map.BackgroundColor = Color.FromArgb((int)(bgColorVector.W * 255), (int)(bgColorVector.X * 255), (int)(bgColorVector.Y * 255), (int)(bgColorVector.Z * 255));

                    // Favorite
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    bool favorite = map.Favorite;
                    if(ImGui.Checkbox($"##{key}_favorite", ref favorite))
                        map.Favorite = favorite;

                    // Color Nodes by Weight
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    bool colorByWeight = map.ColorNodesByWeight;
                    if(ImGui.Checkbox($"##{key}_colorbyweight", ref colorByWeight))
                        map.ColorNodesByWeight = colorByWeight;

                    // Use Weight Color for Name
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    bool nameByWeight = map.UseWeightColorForName;
                    if(ImGui.Checkbox($"##{key}_namebyweight", ref nameByWeight))
                        map.UseWeightColorForName = nameByWeight;

                    // Icon picker
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    SettingsHelpers.IconPicker($"{key}_icon", map.Icon, i => map.Icon = i);

                    // Blank spacer column (was Biomes) so the fixed-width columns stay aligned.
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
    private bool mapFilterFav = false;
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
    // Backing values for the "set all" color pickers in the map table header.
    [JsonIgnore]
    private Vector4 bulkNodeColor = new(1, 1, 1, 1);
    [JsonIgnore]
    private Vector4 bulkNameColor = new(1, 1, 1, 1);
    [JsonIgnore]
    private Vector4 bulkBgColor = new(0, 0, 0, 1);
    [JsonIgnore]
    private SpriteIcon bulkIcon = SpriteIcon.Circle;

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
    public bool DrawWeightOnMap { get; set; } = false;
    public bool ShowMapNames { get; set; } = true;
    public bool UseColorsForMapNames { get; set; } = true;
    public bool ShowMapNamesOnUnlockedNodes { get; set; } = true;
    public bool ShowMapNamesOnLockedNodes { get; set; } = true;
    public bool ShowMapNamesOnHiddenNodes { get; set; } = true;
    public Color GoodNodeColor { get; set; } = Color.FromArgb(200, 50, 255, 50);
    public Color BadNodeColor { get; set; } = Color.FromArgb(200, 255, 50, 50);

    public ObservableDictionary<string, Map> Maps { get; set; } = [];

    // Nested under Maps — biome and content weights/colors are map-related, so they live as
    // sub-sections of the Maps category rather than as separate top-level menus.
    public BiomeSettings Biomes { get; set; } = new BiomeSettings();
    public ContentSettings Content { get; set; } = new ContentSettings();
}

/// <summary>
/// Settings for Biomes
/// </summary>
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

/// <summary>
/// Settings for Map Content
/// </summary>
/// MARK: ContentSettings
[Submenu(CollapsedByDefault = true)]
public class ContentSettings
{
    [JsonIgnore]
    public CustomNode CustomContentSettings { get; set; }
    public ObservableDictionary<string, Content> ContentTypes { get; set; } = [];

    public bool ShowRingsOnUnlockedNodes { get; set; } = true;
    public bool ShowRingsOnLockedNodes { get; set; } = true;
    public bool ShowRingsOnHiddenNodes { get; set; } = true;


    public ContentSettings() {    

        CustomContentSettings = new CustomNode
        {
            DrawDelegate = () =>
            {
  
                if (ImGui.BeginTable("content_options_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
                {
                    ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 40);                                                               
                    ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     
        
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool highlightUnlocked = ShowRingsOnUnlockedNodes;
                    if(ImGui.Checkbox($"##unlocked_nodes_highlight", ref highlightUnlocked))                        
                        ShowRingsOnUnlockedNodes = highlightUnlocked;

                    ImGui.TableNextColumn();
                    ImGui.Text("Highlight Content in Unlocked Map Nodes");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool highlightLocked = ShowRingsOnLockedNodes;
                    if(ImGui.Checkbox($"##locked_nodes_highlight", ref highlightLocked))                        
                        ShowRingsOnLockedNodes = highlightLocked;

                    ImGui.TableNextColumn();
                    ImGui.Text("Highlight Content in Locked Map Nodes");
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    bool highlightHidden = ShowRingsOnHiddenNodes;
                    if(ImGui.Checkbox($"##hidden_nodes_highlight", ref highlightHidden))                        
                        ShowRingsOnHiddenNodes = highlightHidden;

                    ImGui.TableNextColumn();
                    ImGui.Text("Highlight Content in Hidden Map Nodes");                    
                }

                ImGui.EndTable();

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

                if (ImGui.BeginTable("content_table", 6, ImGuiTableFlags.Borders))
                {
                    ImGui.TableSetupColumn("Content Type", ImGuiTableColumnFlags.WidthFixed, 250);
                    ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 50);
                    ImGui.TableSetupColumn("Ring", ImGuiTableColumnFlags.WidthFixed, 70);
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
                    SettingsHelpers.SetAllColor("contentcolor", ref bulkContentColor, contentValues, (c, col) => c.Color = col);
                    ImGui.TableNextColumn();
                    SettingsHelpers.CenterControl(30f);
                    SettingsHelpers.ToggleAll("contenthighlight", contentValues, c => c.Highlight, (c, v) => c.Highlight = v);
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
                        
                        ImGui.TableNextColumn();
                        SettingsHelpers.CenterControl(30f);
                        Vector4 contentColorVector = new Vector4(content.Color.R / 255.0f, content.Color.G / 255.0f, content.Color.B / 255.0f, content.Color.A / 255.0f);
                        if(ImGui.ColorEdit4($"##{key}_color", ref contentColorVector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                            content.Color = Color.FromArgb((int)(contentColorVector.W * 255), (int)(contentColorVector.X * 255), (int)(contentColorVector.Y * 255), (int)(contentColorVector.Z * 255));
                        
                        ImGui.TableNextColumn();
                        SettingsHelpers.CenterControl(30f);
                        bool highlight = content.Highlight;
                        if(ImGui.Checkbox($"##{key}_highlight", ref highlight))
                            content.Highlight = highlight;

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
    // Backing value for the "set all" color picker in the content table header.
    [JsonIgnore]
    private Vector4 bulkContentColor = new(1, 1, 1, 1);
    // Backing value for the "set all" weight slider in the content table header.
    [JsonIgnore]
    // Neutral default - see bulkMapWeight.
    private float bulkContentWeight = 0f;
}

/// <summary>
/// Settings for Waypoints
/// </summary>
/// MARK: WaypointSettings
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

    public int WaypointPanelMaxItems { get; set; } = 30;
    public int WaypointPanelMaxSteps { get; set; } = 50; // 0 = unlimited
    public string WaypointPanelSortBy { get; set; } = "Weight";
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

                }
                ImGui.EndTable();
            }
        };
    }
}

public static class SettingsHelpers {
    /// <summary>
    /// "Toggle all" checkbox for a table column. Reflects whether every item is currently on, and
    /// clicking it sets the bound bool on every item to the new value.
    /// </summary>
    public static void ToggleAll<T>(string id, System.Collections.Generic.IEnumerable<T> items, Func<T,bool> get, Action<T,bool> set) {
        var list = items as System.Collections.Generic.ICollection<T> ?? items.ToList();
        bool all = list.Count > 0 && list.All(get);
        if (ImGui.Checkbox($"##all_{id}", ref all))
            foreach (var i in list) set(i, all);
    }

    /// <summary>
    /// "Set all" color picker for a table column. Editing it applies the chosen color to every item.
    /// The swatch value is caller-owned so it persists between frames.
    /// </summary>
    public static void SetAllColor<T>(string id, ref Vector4 value, System.Collections.Generic.IEnumerable<T> items, Action<T,Color> set) {
        if (ImGui.ColorEdit4($"##setall_{id}", ref value, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs)) {
            Color c = Color.FromArgb((int)(value.W * 255), (int)(value.X * 255), (int)(value.Y * 255), (int)(value.Z * 255));
            foreach (var i in items) set(i, c);
        }
    }

    /// <summary>
    /// "Set all" weight slider for a table column. Dragging it applies the value to every item.
    /// The slider value is caller-owned so it persists between frames.
    /// </summary>
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

    /// <summary>
    /// Sprite icon picker. Shows the current icon as a small image button; clicking opens a popup
    /// grid of all atlas sprites (laid out to match the atlas columns). Selecting one calls
    /// <paramref name="set"/>. Falls back to plain numbered buttons if the atlas texture isn't loaded.
    /// </summary>
    // Stable display order for the atlas-point style editor (Dictionary order isn't guaranteed).
    public static readonly string[] AtlasPointStyleKeys = { "Generic", "Breach", "Abyss", "Incursion", "Delirium", "Ritual" };

    // Editable table of per-atlas-point-type marker color + icon. Backs the AtlasPointStylePicker CustomNode.
    public static void AtlasPointStyleTable() {
        var g = Main.Settings.Graphics;
        ImGui.TextDisabled("Atlas point markers — color and icon per content type ('Generic' = non-content points).");
        if (!ImGui.BeginTable("atlaspointstyles", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
            return;
        ImGui.TableSetupColumn("Type");
        ImGui.TableSetupColumn("Color");
        ImGui.TableSetupColumn("Icon");
        ImGui.TableHeadersRow();
        foreach (var key in AtlasPointStyleKeys) {
            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.Text(key);

            ImGui.TableNextColumn();
            if (!g.AtlasPointColors.TryGetValue(key, out var cn)) { cn = new ColorNode(Color.White); g.AtlasPointColors[key] = cn; }
            Color col = cn;
            Vector4 cv = new(col.R / 255.0f, col.G / 255.0f, col.B / 255.0f, col.A / 255.0f);
            if (ImGui.ColorEdit4($"##apc_{key}", ref cv, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
                g.AtlasPointColors[key] = Color.FromArgb((int)(cv.W * 255), (int)(cv.X * 255), (int)(cv.Y * 255), (int)(cv.Z * 255));

            ImGui.TableNextColumn();
            SpriteIcon cur = g.AtlasPointIcons.TryGetValue(key, out var ic) ? ic : SpriteIcon.Star8;
            IconPicker($"apicon_{key}", cur, i => g.AtlasPointIcons[key] = i);
        }
        ImGui.EndTable();
    }

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

    /// <summary>
    /// "Set all" icon picker for a table column. Picking an icon applies it to every item.
    /// The selection is caller-owned so it persists between frames.
    /// </summary>
    public static void SetAllIcon<T>(string id, ref SpriteIcon value, System.Collections.Generic.IEnumerable<T> items, Action<T,SpriteIcon> set) {
        var captured = value;        // ref params can't be captured in the lambda
        SpriteIcon chosen = captured;
        IconPicker(id, captured, i => chosen = i);
        if (chosen != captured) {
            value = chosen;
            foreach (var item in items) set(item, chosen);
        }
    }

    /// <summary>
    /// Draws a two-step "Reset All Weights" button. First click arms the confirm state; the second
    /// (red) click runs <paramref name="reset"/>. A Cancel button disarms. State is held in the
    /// caller-owned <paramref name="confirming"/> flag.
    /// </summary>
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

    /// <summary>
    /// Draws a "Reset to Plugin Defaults" button (with confirm) that reloads the bundled
    /// exilemaps_weights.json, restoring the shipped default map and content weights.
    /// </summary>
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

    /// <summary>
    /// Draws the "Import Old Settings" button (convert a pre-rework settings file into a new profile) plus
    /// the one-time auto-detect banner. Full settings export/import was removed - profiles cover sharing.
    /// </summary>
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