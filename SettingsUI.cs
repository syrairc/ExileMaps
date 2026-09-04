
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.Shared.Nodes;
using ExileImGui2;
using ExileMaps.Classes;
using ImGuiNET;
using Newtonsoft.Json;
using SColor = System.Drawing.Color;

namespace ExileMaps;

public partial class ExileMapsCore
{
    #region Settings UI

    public override void DrawSettings()
    {
        if (iconSheets == null) { ImGui.TextDisabled("Sprite atlas failed to load."); return; }
        DrawLegacyImportBanner();

        ExileImGui2.Windows.TabBar("exilemaps_settings",
            ("General",    Safe("General",    DrawGeneralTab)),
            ("Appearance", Safe("Appearance", DrawAppearanceTab)),
            ("Content",    Safe("Content",    DrawContentTab)),
            ("Tuning",     Safe("Tuning",     DrawWeightsTab)),
            ("Waypoints",  Safe("Waypoints",  DrawWaypointsTab)),
            ("Keybinds",   Safe("Keybinds",   DrawKeybindsTab)),
            ("Hacks",      Safe("Hacks",      DrawHacksTab)),
            ("Debug",      Safe("Debug",      DrawDebugTab)),
            ("Profiles",   Safe("Profiles",   DrawProfilesTab)));
    }

    private void DrawLegacyImportBanner()
    {
        if (!legacyImportPending) return;

        ImGui.TextColored(new Vector4(1f, 0.85f, 0.3f, 1f),
            "Settings from the previous plugin version were detected and backed up.");
        if (ImGui.Button("Import now"))
        {
            ConvertLegacySettings(legacyBackupPath);
            Settings.LegacyImportHandled = true;
            legacyImportPending = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Dismiss"))
        {
            Settings.LegacyImportHandled = true;
            legacyImportPending = false;
        }
        ImGui.Separator();
    }

    private Func<bool> Safe(string label, Func<bool> body) => () => {
        try { return body(); }
        catch (Exception ex) { LogError($"[Settings] {label} tab failed: {ex.Message}"); return false; }
    };

    private static bool Check(string label, Func<bool> get, Action<bool> set)
    {
        bool v = get();
        if (!ImGui.Checkbox(label, ref v)) return false;
        set(v);
        return true;
    }

    private static Controls.ToggleItem Tog(string label, Func<bool> get, Action<bool> set, string tip = null) => new(label, get, set, tip);
    private static Controls.ColorItem Col(string label, Func<SColor> get, Action<SColor> set) => new(label, get, set);

    #endregion

    #region General Tab

    private bool DrawGeneralTab()
    {
        bool d = false;
        var f = Settings.Features;
        var g = Settings.Graphics;
        var a = Settings.AtlasOverview;

        if (Controls.Category("Drawing"))
        {
            d |= Controls.ToggleGrid("gen_draw", new[] {
                Tog("Enable Plugin", () => Settings.Enable, v => Settings.Enable.Value = v),
                Tog("Enable Atlas Drawing", () => f.EnableDrawing, v => f.EnableDrawing = v,
                    "Master switch for atlas overlay drawing. Default: Scroll Lock."),
            }, 0);

        }

        if (Controls.Category("Node States"))
            d |= DrawNodeStateGrid();

        if (Controls.Category("Performance"))
        {
            d |= Controls.SliderInt("Map Cache Refresh Rate", () => g.MapCacheRefreshRate, v => g.MapCacheRefreshRate = v, 1, 60);
            Controls.Tip("Throttle the map cache refresh rate. Default is 1 second.");
        }

        if (Controls.Category("Panels"))
        {
            d |= Controls.ToggleGrid("gen_panels", new[] {
                Tog("Show Atlas Button", () => f.ShowAtlasButton, v => f.ShowAtlasButton = v,
                    "Draggable ExileMaps button that opens the panel."),
            }, 0);

            d |= Controls.SliderInt("Reachable Step Window", () => a.ReachableSteps, v => a.ReachableSteps = v, 1, 50);
            Controls.Tip("Steps from your frontier counted as 'reachable' for tallies.");
        }

        if (Controls.Category("Search"))
        {
            var s = Settings.Search;
            d |= Controls.ToggleGrid("gen_search", new[] {
                Tog("Hook Ingame Search Box", () => s.HookIngameSearch, v => s.HookIngameSearch = v,
                    "Read the game's atlas search box, list matches under it."),
                Tog("Show Own Search Box", () => s.ShowSearchBox, v => s.ShowSearchBox = v,
                    "A separate draggable search box; its text wins over the game's."),
                Tog("Highlight Matches on Atlas", () => s.HighlightMatches, v => s.HighlightMatches = v,
                    "Pulsing ring on each result. The game's own box already glows."),
            }, 0);

            d |= Controls.SliderFloat("Match Ring Size", () => s.HighlightSize, v => s.HighlightSize = v, 0.1f, 1.5f, null, "%.2f");
            Controls.Tip("Ring radius, fraction of node spacing. Stays sized as you zoom.");

            d |= Controls.ColorGrid("gen_search_col", new[] {
                Col("Match Ring", () => s.HighlightColor, v => s.HighlightColor.Value = v),
            }, 0);

            d |= Controls.SliderInt("Max Results", () => s.MaxResults, v => s.MaxResults = v, 1, 50);
            Controls.Tip("Matches shown before '+ N more'. Closest unrun maps first.");
        }
        return d;
    }

    private static readonly string[] NodeStateLabels = { "Visited", "Unlocked", "Locked", "Hidden" };

    private static bool DrawNodeStateRow(string label, ref NodeStates value)
    {
        bool[] v = {
            value.HasFlag(NodeStates.Visited),
            value.HasFlag(NodeStates.Unlocked),
            value.HasFlag(NodeStates.Locked),
            value.HasFlag(NodeStates.Hidden) };

        ImGui.TextUnformatted(label);
        if (!Controls.ToggleGrid(label, NodeStateLabels, v, 4)) return false;

        value = (v[0] ? NodeStates.Visited  : 0)
              | (v[1] ? NodeStates.Unlocked : 0)
              | (v[2] ? NodeStates.Locked   : 0)
              | (v[3] ? NodeStates.Hidden   : 0);
        return true;
    }

    private bool DrawNodeStateGrid()
    {
        var g = Settings.Graphics;
        bool d = false;

        var nodes = g.DrawNodes;
        if (DrawNodeStateRow("Highlight Nodes", ref nodes)) { g.DrawNodes = nodes; d = true; }

        var names = g.DrawNames;
        if (DrawNodeStateRow("Show Map Names", ref names)) { g.DrawNames = names; d = true; }

        var lines = g.DrawLines;
        if (DrawNodeStateRow("Connection Lines", ref lines)) { g.DrawLines = lines; d = true; }

        return d;
    }

    #endregion

    #region Appearance Tab

    private bool DrawAppearanceTab()
    {
        bool d = false;
        var g = Settings.Graphics;
        var m = Settings.Maps;

        d |= DrawGraphicsPresetRow();

        if (Controls.Category("Nodes"))
        {
            d |= Controls.SliderFloat("Node Radius", () => g.NodeRadius, v => g.NodeRadius = v, 0f, 10f, null, "%.2f");
            Controls.Tip("Radius of the circles used to highlight map nodes.");

            d |= ImGui.Checkbox("Use Icons for Nodes", ref g.UseNodeIcons);
            Controls.Tip("Draw per-map sprite icons. Off = plain filled circles.");

            int mode = (int)m.NodeColorMode;
            bool modeChanged = ImGui.RadioButton("By Weight", ref mode, (int)NodeColorMode.Weight);
            Controls.Tip("Colour nodes along the weight ramp.");
            ImGui.SameLine();
            modeChanged |= ImGui.RadioButton("By Status", ref mode, (int)NodeColorMode.Status);
            Controls.Tip("Colour nodes by visited / unlocked / locked / fogged.");
            ImGui.SameLine();
            modeChanged |= ImGui.RadioButton("Static", ref mode, (int)NodeColorMode.Static);
            Controls.Tip("One colour for every node.");
            if (modeChanged) { m.NodeColorMode = (NodeColorMode)mode; d = true; }

            d |= m.NodeColorMode switch {
                NodeColorMode.Static => Controls.ColorGrid("node_colors_static", new[] {
                    Col("Node Color", () => m.StaticNodeColor, v => m.StaticNodeColor = v),
                }),
                NodeColorMode.Status => Controls.ColorGrid("node_colors_status", new[] {
                    Col("Visited", () => m.VisitedNodeColor, v => m.VisitedNodeColor = v),
                    Col("Unlocked", () => m.UnlockedNodeColor, v => m.UnlockedNodeColor = v),
                    Col("Locked", () => m.LockedNodeColor, v => m.LockedNodeColor = v),
                    Col("Fogged", () => m.HiddenNodeColor, v => m.HiddenNodeColor = v),
                }),
                _ => Controls.ColorGrid("node_colors_weight", new[] {
                    Col("Good Weight", () => m.GoodNodeColor, v => m.GoodNodeColor = v),
                    Col("Neutral Weight", () => m.NeutralNodeColor, v => m.NeutralNodeColor = v),
                    Col("Bad Weight", () => m.BadNodeColor, v => m.BadNodeColor = v),
                }),
            };
            if (m.NodeColorMode == NodeColorMode.Weight)
                Controls.Tip("The weight sliders use these three colours too.");
        }

        if (Controls.Category("Content and Biome Icons"))
        {
            var iconTogs = new List<Controls.ToggleItem> {
                Tog("Show Content Row", () => g.ShowContentRow, v => g.ShowContentRow = v,
                    "Draw a row of content-type icons centered below the map name."),
            };
            if (g.ShowContentRow)
            {
                iconTogs.Add(Tog("Skip Game-drawn Content", () => g.ContentIconsSkipGameDrawn, v => g.ContentIconsSkipGameDrawn = v,
                    "Visible nodes show the game's icon; skip drawing our duplicate."));
            }
            iconTogs.Add(Tog("Show Biome Icon", () => g.ShowBiomeIcon, v => g.ShowBiomeIcon = v,
                "Draw one biome icon (highest-weight biome) above the map name."));
            d |= Controls.ToggleGrid("icon_togs", iconTogs.ToArray(), 0);

            d |= Controls.SliderFloat("Icon Size", () => g.IconSize, v => g.IconSize = v, 8f, 64f);
            Controls.Tip("Size (px, at full zoom) of content row and biome icons.");

            d |= Controls.SliderFloat("Minimum Zoom Scale", () => g.MinZoomScale, v => g.MinZoomScale = v, 0.2f, 1f);
            Controls.Tip("Floor for icon and label shrinking when zoomed out.");

            d |= Controls.SliderFloat("Shrink Starts At", () => g.ZoomScaleStart, v => g.ZoomScaleStart = v, 0.1f, 1f);
            Controls.Tip("Zoom level shrinking begins. Lower stays full size longer.");
        }

        if (Controls.Category("Map Labels"))
        {
            d |= ImGui.Checkbox("Uppercase Map Names", ref g.UppercaseMapNames);
            Controls.Tip("Draw map names in ALL CAPS. Off = use the map's normal casing.");

            d |= Controls.SliderInt("Map Name Offset Y", () => g.MapNameOffsetY, v => g.MapNameOffsetY = v, -200, 200);
            Controls.Tip("Vertical offset of the map name/weight text from node center.");

            d |= ImGui.Checkbox("Show Weight Value", ref g.DrawWeightOnMap);
            Controls.Tip("Numeric weight beside the name. Zeroes are skipped.");

            d |= ImGui.Checkbox("Scale Names With Zoom", ref g.ScaleLabelsWithZoom);
            Controls.Tip("Shrink names as you zoom out, down to Minimum Zoom Scale.");

            var lb = Settings.Labels.Base;
            ImGui.Spacing();
            ImGui.TextDisabled("Colour by weight");
            Controls.Tip("Styles below override whichever of these they set.");
            d |= Controls.ToggleGrid("base_byweight", new[] {
                Tog("Text", () => lb.TextColorByWeight, v => lb.TextColorByWeight = v,
                    "Tint the map name along the weight ramp."),
                Tog("Background", () => lb.BoxColorByWeight, v => lb.BoxColorByWeight = v,
                    "Tint the label's box along the weight ramp."),
                Tog("Border", () => lb.BorderColorByWeight, v => lb.BorderColorByWeight = v,
                    "Tint the label's border along the weight ramp."),
            }, 0);
        }

        if (Controls.Category("Connection Lines"))
        {
            d |= ImGui.Checkbox("Show Connection Lines", ref g.ShowConnectionLines);
            Controls.Tip("Master toggle for lines drawn between adjacent atlas nodes.");

            if (g.ShowConnectionLines)
            {
                d |= Controls.SliderFloat("Line Width", () => g.MapLineWidth, v => g.MapLineWidth = v, 0f, 10f);
                Controls.Tip("Width of the map connection lines.");

                d |= ImGui.Checkbox("Draw Lines as Gradients", ref g.DrawGradientLines);
                Controls.Tip("Gradient between the two colors. Performance intensive.");

                d |= Controls.ColorGrid("line_colors", new[] {
                    Col("Visited", () => g.VisitedLineColor.Value, v => g.VisitedLineColor.Value = v),
                    Col("Unlocked", () => g.UnlockedLineColor.Value, v => g.UnlockedLineColor.Value = v),
                    Col("Locked", () => g.LockedLineColor.Value, v => g.LockedLineColor.Value = v),
                }, alpha: true);

                if (ImGui.Checkbox("Follow the Game's Curves", ref g.UseGameConnectionCurves)) { refreshCache = true; d = true; }
                Controls.Tip("Use the game's curved lines between map nodes.");
            }
        }

        if (Controls.Category("Special Maps"))
        {
            var specTogs = new List<Controls.ToggleItem> {
            };
            if (g.ShowSpecialMapIndicator)
                specTogs.Add(Tog("Hide Completed Special Maps", () => g.HideCompletedSpecialMaps, v => g.HideCompletedSpecialMaps = v,
                    "Drop a completed repeatable special's marker/name. Hubs stay."));
            specTogs.Add(Tog("Show Atlas Quest Marker", () => g.ShowAtlasQuestIndicator, v => g.ShowAtlasQuestIndicator = v,
                "Golden exclamation above maps with atlas quest content."));
            d |= Controls.ToggleGrid("spec_togs", specTogs.ToArray(), 0);

            var S = Settings.Maps.SpecialMaps;
            bool maxWeightChanged = Check("Calculate special maps at specific weight", () => S.UseMaxWeight, v => S.UseMaxWeight = v);
            Controls.Tip("Every special map weighs the value below, not its own.");

            if (S.UseMaxWeight)
            {
                if (Weight.Slider("Weight", () => S.MaxWeight, v => S.MaxWeight = v, WeightSliderLook, "spec_w"))
                    { specialWeightDragging = true; d = true; }
            }

            if (specialWeightDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left))
            {
                specialWeightDragging = false;
                maxWeightChanged = true;
            }

            if (maxWeightChanged) { RequestSpecialMapsRefresh(); d = true; }

            ImGui.Spacing();
            ImGui.TextWrapped("Most specials are detected from their node art. Tick Special on a map's row under " +
                              "Weights to force one the detection misses. The marker itself is the icon on the " +
                              "Special Maps row of the label style table below.");
        }

        if (Controls.Category("Styles"))
            d |= DrawLabelStyleSection();

        return d;
    }

    private GraphicsPreset pendingPreset = GraphicsPreset.Normal;

    private bool DrawGraphicsPresetRow()
    {
        bool applied = false;

        ImGui.TextDisabled("Preset");
        Controls.Tip("Overwrites this profile's visual toggles. Weights are untouched.");
        ImGui.SameLine();

        for (int i = 0; i < GraphicsPresets.Names.Length; i++)
        {
            if (i > 0) ImGui.SameLine();
            if (ImGui.Button(GraphicsPresets.Names[i]))
            {
                pendingPreset = (GraphicsPreset)i;
                ImGui.OpenPopup("apply_gfx_preset");
            }
            Controls.Tip(GraphicsPresets.Describe((GraphicsPreset)i));
        }

        if (ExileImGui2.Windows.ConfirmModal("apply_gfx_preset",
            "Apply the " + GraphicsPresets.Names[(int)pendingPreset] + " preset to this profile?", out bool ok) && ok)
        {
            GraphicsPresets.Apply(Settings.Active, pendingPreset);
            refreshCache = true;
            applied = true;
        }

        ImGui.Separator();
        return applied;
    }

    #endregion

    #region Styles

    private sealed record LabelRow(string Key, string Name, LabelStyleOverride Ov, bool IsMap = false);

    private readonly List<LabelRow> labelRows = [];

    private bool GameDataPending()
    {
        if (gameFilesScraped) return false;
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled("Open the atlas to load content.");
        return true;
    }

    private bool DrawLabelStyleSection()
    {
        bool d = false;
        var L = Settings.Labels;
        var B = L.Base;

        labelRows.Clear();
        labelRows.Add(new LabelRow(null, "Base", null));
        labelRows.Add(new LabelRow("__favorite", "Favorite Maps", L.Favorite));
        labelRows.Add(new LabelRow("__special", "Special Maps", L.Special));
        foreach (var kv in L.Content.OrderBy(kv => ContentOverrideName(kv.Key)))
            labelRows.Add(new LabelRow(kv.Key, ContentOverrideName(kv.Key), kv.Value));
        foreach (var kv in L.Maps.OrderBy(kv => MapOverrideName(kv.Key)))
            labelRows.Add(new LabelRow(kv.Key, MapOverrideName(kv.Key), kv.Value, true));

        if (!GameDataPending())
        {
            if (Combo.SearchCombo("add_ov", "Add content override...",
                    Settings.GameData.Content
                        .Where(kv => !L.Content.ContainsKey(kv.Key))
                        .OrderBy(kv => kv.Value.Name)
                        .Select(kv => (kv.Key, kv.Value.Name)),
                    ref contentOverrideAddFilter, out string added))
            {
                L.Content[added] = new LabelStyleOverride();
                d = true;
            }

            ImGui.SameLine();
            if (Combo.SearchCombo("add_map_ov", "Add map override...",
                    Settings.GameData.Maps
                        .Where(kv => !L.Maps.ContainsKey(kv.Key))
                        .OrderBy(kv => kv.Value.Name)
                        .Select(kv => (kv.Key, kv.Value.Name)),
                    ref mapOverrideAddFilter, out string addedMap))
            {
                L.Maps[addedMap] = new LabelStyleOverride();
                d = true;
            }
        }

        string removeKey = null;
        bool removeIsMap = false;

        var cols = new[] {
            new TableColumn<LabelRow> {
                Header = "", Width = 22f, SortKey = null,
                Draw = (r, _) => {
                    if (r.Key is null or "__favorite" or "__special") return;
                    d |= Check("##ovon", () => r.Ov.Enabled, v => r.Ov.Enabled = v);
                    Controls.Tip("Apply this override.");
                },
            },
            new TableColumn<LabelRow> {
                Header = "Layer", Width = 0f, SortKey = null,
                Draw = (r, _) => { ImGui.AlignTextToFramePadding(); ImGui.TextUnformatted(r.Name); },
            },
            new TableColumn<LabelRow> {
                Header = "Style (click to edit)", Width = 0f, SortKey = null,
                Draw = (r, _) => {
                    if (r.Ov != null) {
                        d |= DrawIconPickTinted("mi", null, () => r.Ov.MapIcon, v => r.Ov.MapIcon = v,
                            () => r.Ov.MapIconTint, v => r.Ov.MapIconTint = v, r.Ov.OverrideMapIconTint);
                        Controls.Tip("Node sprite for this layer. Circle means unset.");
                        ImGui.SameLine();
                        d |= Check("##mitint", () => r.Ov.OverrideMapIconTint, v => r.Ov.OverrideMapIconTint = v);
                        Controls.Tip("Tint the sprite with this colour, not the node colour.");
                        ImGui.SameLine();
                    }

                    if (r.Ov == null)
                        d |= TextStyle.Edit(null, B.Text, r.Name, "st", 170f);
                    else
                        d |= TextStyle.EditOverride(null, r.Ov.Text, B.Text, r.Name, "st", 170f, _labelSwatchScratch);
                },
            },
            new TableColumn<LabelRow> {
                Header = "Extra Icon", Width = 175f, SortKey = null,
                Draw = (r, _) => {
                    if (r.Ov == null) { ImGui.AlignTextToFramePadding(); ImGui.TextDisabled("-"); return; }

                    bool isSpecial = r.Key == "__special";
                    d |= DrawOverrideIconCell(r.Ov,
                        showPlacement: r.Key is not ("__favorite" or "__special"),
                        tip: r.Key == "__favorite"
                            ? "Draw a star beside favorited maps that haven't been run yet."
                            : isSpecial
                                ? "Draw the marker above special map nodes."
                                : "Draw a sprite beside the label. One winner: Content beats Map.",
                        enableGet: isSpecial ? () => Settings.Graphics.ShowSpecialMapIndicator : null,
                        enableSet: isSpecial ? v => Settings.Graphics.ShowSpecialMapIndicator = v : null);
                },
            },
            new TableColumn<LabelRow> {
                Header = "", Width = 18f, SortKey = null,
                Draw = (r, _) => {
                    if (r.Key is null or "__favorite" or "__special") return;
                    ImGui.AlignTextToFramePadding();
                    ImGui.TextDisabled("x");
                    Controls.Tip("Remove this override.");
                    if (ImGui.IsItemClicked()) { removeKey = r.Key; removeIsMap = r.IsMap; }
                },
            },
        };

        string noFilter = null;
        SortableTable.Draw("label_styles", labelRows, cols, ref noFilter);

        if (removeKey != null) {
            if (removeIsMap) L.Maps.Remove(removeKey); else L.Content.Remove(removeKey);
            d = true;
        }
        return d;
    }

    private string contentOverrideAddFilter = "";
    private string mapOverrideAddFilter = "";

    private readonly TextStyle _labelSwatchScratch = new();

    private string ContentOverrideName(string key) =>
        Settings.GameData.Content.TryGetValue(key, out var i) ? i.Name : key;

    private string MapOverrideName(string key) =>
        Settings.GameData.Maps.TryGetValue(key, out var i) ? i.Name : key;

    private bool DrawOverrideIconCell(LabelStyleOverride o, bool showPlacement, string tip,
        Func<bool> enableGet = null, Action<bool> enableSet = null)
    {
        Func<bool> get = enableGet ?? (() => o.IconEnabled);
        Action<bool> set = enableSet ?? (v => o.IconEnabled = v);

        bool d = Check("##icon", get, set);
        Controls.Tip(tip);

        if (!get()) return d;

        ImGui.SameLine();
        d |= DrawIconPickWysiwyg("glyph", null,
            () => o.Icon, v => o.Icon = v,
            () => o.IconTint, v => o.IconTint = v,
            () => o.IconSize, v => o.IconSize = v, 8, 96);

        if (!showPlacement) return d;

        {
            ImGui.SameLine();
            int pos = (int)o.IconPosition;
            if (Combo.Option(ref pos, IconPositionLabels, "ip", 110f)) { o.IconPosition = (IconPosition)pos; d = true; }
            Controls.Tip("Where the glyph sits relative to the label.");
        }
        return d;
    }

    private static readonly string[] IconPositionLabels = { "Above Icon", "Below Label", "Left of Label", "Right of Label" };

    #endregion

    #region Weights Tab

    private List<string> mapIds, contentIds, biomeIds, rumorIds;

    private void RebuildWeightEditorIds()
    {
        mapIds     = [.. Settings.GameData.Maps.Keys.OrderBy(k => Settings.GameData.Maps[k].Name)];
        contentIds = [.. Settings.GameData.Content.Keys.OrderBy(k => Settings.GameData.Content[k].Name)];
        biomeIds   = [.. Settings.GameData.Biomes.Keys.OrderBy(k => Settings.GameData.Biomes[k].Name)];
        rumorIds   = [.. Settings.GameData.Rumors.Keys.OrderBy(k => k)];
    }

    private WeightListOpts WeightLook => new()
    {
        Min = minMapWeight, Max = maxMapWeight, Decimals = 1,
        Scale = WeightRamp(),
        Rail = true,
        Search = true, Sortable = true, MultiSelect = true, Resizable = false,
        NameHeader = "Name", ValueHeader = "Weight",
        MaxHeight = 420f,
    };

    public SColor[] WeightRamp()
    {
        var m = Settings.Maps;
        return [m.BadNodeColor, m.NeutralNodeColor, m.GoodNodeColor];
    }

    private string profileNewName = "";
    private bool profileRenaming = false;
    private string profileFilter = "";
    private string profileCopySource;
    private string profileCopyFilter = "";

    private static readonly (string Key, string Label)[] CopyGroups =
    {
        ("weights",    "Tuning"),
        ("appearance", "Appearance"),
        ("keybinds",   "Keybinds"),
        ("hacks",      "Hacks"),
        ("behaviour",  "Other behaviour"),
        ("waypoints",  "Waypoints"),
        ("tours",      "Tours"),
    };

    private readonly bool[] profileCopyGroups = { true, true, true, true, true, false, false };

    private bool DrawProfileBar()
    {
        bool d = false;
        var P = Settings.Profiles;

        bool picked = Combo.SearchCombo("profile", P.ActiveProfile,
            P.Profiles.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => (k, k)),
            ref profileFilter, out string pick, 200f);
        if (picked)
        {
            ApplyProfileSwitch(pick);
            d = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("New"))
        {
            CreateProfile(NewProfileName(), null);
            d = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Duplicate"))
        {
            CreateProfile(UniqueProfileName(P, P.ActiveProfile + " Copy"), P.ActiveProfile);
            d = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Copy from..."))
        {
            profileCopySource = null;
            profileCopyFilter = "";
            ImGui.OpenPopup("copy_from");
        }
        d |= DrawCopyFromPopup();

        ImGui.SameLine();
        if (ImGui.Button("Rename")) { profileRenaming = true; profileNewName = P.ActiveProfile; }
        if (profileRenaming)
        {
            ImGui.SetNextItemWidth(200f);
            if (ImGui.InputText("##rename", ref profileNewName, 64, ImGuiInputTextFlags.EnterReturnsTrue))
            {
                if (!string.IsNullOrWhiteSpace(profileNewName) && !P.Profiles.ContainsKey(profileNewName))
                {
                    P.Profiles[profileNewName] = P.Profiles[P.ActiveProfile];
                    P.Profiles.Remove(P.ActiveProfile);
                    P.ActiveProfile = profileNewName;
                    d = true;
                }
                profileRenaming = false;
            }
        }

        if (P.Profiles.Count > 1)
        {
            ImGui.SameLine();
            if (ImGui.Button("Delete")) ImGui.OpenPopup("del_profile");
        }
        if (ExileImGui2.Windows.ConfirmModal("del_profile", "Delete profile \"" + P.ActiveProfile + "\"?", out bool ok) && ok)
        {
            string next = P.Profiles.Keys.First(k => k != P.ActiveProfile);
            P.Profiles.Remove(P.ActiveProfile);
            ApplyProfileSwitch(next);
            d = true;
        }
        return d;
    }

    private bool DrawCopyFromPopup()
    {
        if (!ImGui.BeginPopup("copy_from")) return false;

        bool copied = false;
        var P = Settings.Profiles;

        ImGui.TextDisabled("Copy into \"" + P.ActiveProfile + "\" from:");

        var options = P.Profiles.Keys.Where(k => k != P.ActiveProfile)
            .OrderBy(k => k, StringComparer.Ordinal).Select(k => (k, k)).ToList();

        if (options.Count == 0)
            ImGui.TextDisabled("No other profile to copy from.");
        else if (Combo.SearchCombo("copy_src", profileCopySource ?? "", options, ref profileCopyFilter, out string pick, 220f))
            profileCopySource = pick;

        ImGui.Separator();
        for (int i = 0; i < CopyGroups.Length; i++)
            ImGui.Checkbox(CopyGroups[i].Label, ref profileCopyGroups[i]);
        Controls.Tip("Ticked groups replace the same group in this profile.");

        ImGui.Separator();
        ImGui.BeginDisabled(profileCopySource == null);
        if (ImGui.Button("Copy"))
        {
            CopyIntoActive(profileCopySource);
            copied = true;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        if (ImGui.Button("Cancel")) ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
        return copied;
    }

    private void CopyIntoActive(string sourceName)
    {
        if (!Settings.Profiles.Profiles.TryGetValue(sourceName, out var source)) return;

        var copy = CloneProfile(source);
        var target = Settings.Active;

        for (int i = 0; i < CopyGroups.Length; i++)
        {
            if (!profileCopyGroups[i]) continue;
            switch (CopyGroups[i].Key)
            {
                case "weights":
                    target.Maps = copy.Maps;
                    target.Content = copy.Content;
                    target.Biomes = copy.Biomes;
                    target.Rumors = copy.Rumors;
                    break;
                case "appearance":
                    target.Labels = copy.Labels;
                    target.MapAppearance = copy.MapAppearance;
                    target.Graphics = copy.Graphics;
                    break;
                case "keybinds":
                    target.Keybinds = copy.Keybinds;
                    break;
                case "hacks":
                    target.Hacks = copy.Hacks;
                    break;
                case "behaviour":
                    target.Features = copy.Features;
                    target.Search = copy.Search;
                    target.AtlasOverview = copy.AtlasOverview;
                    target.Expeditions = copy.Expeditions;
                    break;
                case "waypoints":
                    target.Waypoints = copy.Waypoints;
                    break;
                case "tours":
                    target.Tours = copy.Tours;
                    break;
            }
        }

        ApplyProfileSwitch(Settings.Profiles.ActiveProfile);
    }

    private void CreateProfile(string name, string sourceName)
    {
        var profile = sourceName != null && Settings.Profiles.Profiles.TryGetValue(sourceName, out var source)
            ? CloneProfile(source)
            : new Profile();

        Settings.Profiles.Profiles[name] = profile;
        ApplyProfileSwitch(name);
    }

    private static Profile CloneProfile(Profile p) =>
        JsonConvert.DeserializeObject<Profile>(JsonConvert.SerializeObject(p, SettingsContainer.jsonSettings), SettingsContainer.jsonSettings);

    private string NewProfileName()
    {
        string league = null;
        try { league = GameController?.Game?.IngameState?.ServerData?.League; } catch { }

        return UniqueProfileName(Settings.Profiles, string.IsNullOrWhiteSpace(league) ? "Profile" : league);
    }

    private static string UniqueProfileName(ExileMapsSettings.ProfileSettings p, string stem)
    {
        if (!p.Profiles.ContainsKey(stem)) return stem;
        for (int i = 2; ; i++)
            if (!p.Profiles.ContainsKey(stem + " " + i)) return stem + " " + i;
    }

    private bool DrawProfilesTab()
    {
        bool d = DrawProfileBar();

        ImGui.Separator();
        DrawImportExportRow();

        ImGui.Separator();
        if (ImGui.Button("Import from previous version...")) ImportLegacySettings();
        Controls.Tip("Import weights, tuning, waypoints and tours from the previously published plugin.");

        return d;
    }

    private void DrawImportExportRow()
    {
        if (ImGui.Button("Export settings")) ExportSettings();
        ImGui.SameLine();
        if (ImGui.Button("Import settings")) ImportSettings();
        ImGui.SameLine();
        if (ImGui.Button("Export weights")) ExportWeights();
        ImGui.SameLine();
        if (ImGui.Button("Import weights")) ImportWeights();
        ImGui.SameLine();
        if (ImGui.Button("Reset weights")) ImGui.OpenPopup("reset_weights");

        if (ExileImGui2.Windows.ConfirmModal("reset_weights", "Zero every weight in this profile?", out bool ok) && ok)
        {
            ResetMapWeightsToDefaults();
            ResetContentWeightsToDefaults();
            ResetBiomeWeightsToDefaults();
            Settings.Active.Rumors.Clear();
            weightsDirty = true;
        }
    }

    private bool DrawContentTab()
    {
        bool d = false;
        var c = Settings.ContentDisplay;

        if (Controls.Category("Atlas Points"))
        {
            ImGui.TextWrapped("Badge on the content row for maps that award an atlas passive point. Turn a type off once you have every point for that tree.");
            ImGui.Spacing();

            var togs = new List<Controls.ToggleItem> {
                Tog("Generic", () => c.ShowGenericAtlasPoint, v => c.ShowGenericAtlasPoint = v,
                    "Points not tied to a league content type."),
            };
            foreach (var t in ContentDisplaySettings.AtlasPointTypes) {
                var key = t;
                togs.Add(Tog(key, () => c.ShowAtlasPoint(key), v => c.AtlasPointIcons[key] = v,
                    "Points awarded by " + key + " maps."));
            }
            d |= Controls.ToggleGrid("atlas_points", togs.ToArray(), 0);
        }

        if (Controls.Category("Expedition"))
        {
            var f = Settings.Features;
            var expMarkers = f.ExpeditionMarkers;
            if (Controls.EnumCombo(ref expMarkers, "exp_markers", 160f)) { f.ExpeditionMarkers = expMarkers; d = true; }
            ImGui.SameLine();
            ImGui.TextUnformatted("Expedition Markers");
            Controls.Tip("Mark possible expedition spawns. Nearest = closest one only.");

            d |= Check("Color rumours by weight", () => c.ColorRumorsByWeight, v => c.ColorRumorsByWeight = v);
            Controls.Tip("Tints rumour names in the marker and popup lists. 0 stays plain.");
        }

        return d;
    }

    private bool DrawWeightsTab()
    {
        bool d = false;
        specialMapsChanged = false;

        if (Controls.Category("Maps") && !GameDataPending())
            d |= Weight.List("w_maps", mapIds,
                MapName,
                id => Settings.ReadMap(id).Weight,
                (id, v) => Settings.TuneMap(id).Weight = v,
                WeightLook, null, MapWeightColumns());

        if (Controls.Category("Content") && !GameDataPending())
            d |= Weight.List("w_content", contentIds,
                ContentName,
                id => Settings.ReadContent(id).Weight,
                (id, v) => Settings.TuneContent(id).Weight = v,
                WeightLook, null, ContentWeightColumns());

        if (Controls.Category("Biomes") && !GameDataPending())
            d |= Weight.List("w_biomes", biomeIds,
                id => Settings.GameData.Biomes[id].Name,
                id => Settings.BiomeWeight(id),
                (id, v) => Settings.Active.Biomes[id] = v,
                WeightLook);

        if (Controls.Category("Expedition rumours"))
            d |= Weight.List("w_rumors2", rumorIds,
                id => Settings.GameData.Rumors[id].Content,
                id => Settings.RumorWeight(id),
                (id, v) => Settings.Active.Rumors[id] = v,
                WeightLook,
                id => Settings.GameData.Rumors[id].Description,
                RumorWeightColumns());

        if (specialMapsChanged) { RequestSpecialMapsRefresh(); d = true; }
        if (d) weightsDirty = true;
        return d;
    }

    private string iconPickerSheet = SpriteAtlas.FileName;

    private bool DrawIconPickWysiwyg(string id, string label,
        Func<SpriteIcon> getIcon, Action<SpriteIcon> setIcon,
        Func<SColor> getTint, Action<SColor> setTint,
        Func<float> getSize, Action<float> setSize, int minSize, int maxSize)
    {
        int index = (int)getIcon();
        var tint = getTint();
        int size = (int)MathF.Round(getSize());
        string sheet = iconPickerSheet;

        bool changed = Controls.IconPicker(id, iconSheets, ref sheet, ref index, ref tint, ref size, minSize, maxSize);
        iconPickerSheet = sheet;
        if (changed)
        {
            setIcon((SpriteIcon)index);
            setTint(tint);
            setSize(size);
        }

        if (label != null) { ImGui.SameLine(); ImGui.TextUnformatted(label); }
        return changed;
    }

    private bool DrawIconPickTinted(string id, string label,
        Func<SpriteIcon> getIcon, Action<SpriteIcon> setIcon,
        Func<SColor> getTint, Action<SColor> setTint, bool tintEnabled = true)
    {
        int index = (int)getIcon();
        var tint = getTint();
        string sheet = iconPickerSheet;

        bool changed = Controls.IconPicker(id, iconSheets, ref sheet, ref index, ref tint, tintEnabled);
        iconPickerSheet = sheet;
        if (changed) { setIcon((SpriteIcon)index); setTint(tint); }

        if (label != null) { ImGui.SameLine(); ImGui.TextUnformatted(label); }
        return changed;
    }

    private string MapName(string id) => Settings.GameData.Maps[id].Name;
    private string ContentName(string id) => Settings.GameData.Content[id].Name;

    private bool specialMapsChanged;
    private bool specialWeightDragging;

    private static float CheckColWidth => ImGui.GetFrameHeight() + ImGui.GetStyle().CellPadding.X * 2f;


    private static float CheckHeaderWidth(string header) =>
        MathF.Max(CheckColWidth, ImGui.CalcTextSize(header).X + ImGui.GetStyle().CellPadding.X * 2f);

    private static bool CenteredCheck(string label, Func<bool> get, Action<bool> set)
    {
        float avail = ImGui.GetContentRegionAvail().X;
        float w = ImGui.GetFrameHeight();
        if (avail > w) ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (avail - w) * 0.5f);
        return Check(label, get, set);
    }

    private TableColumn<string>[] MapWeightColumns() =>
    [
        new TableColumn<string> {
            Header = "Special", Width = CheckHeaderWidth("Special"), SortKey = id => Settings.ReadMap(id).Special,
            Draw = (id, _) => {
                if (CenteredCheck("##special", () => Settings.ReadMap(id).Special, v => Settings.TuneMap(id).Special = v)) specialMapsChanged = true;
                Controls.Tip("Force this map to count as special, overriding the heuristic.");
            },
        },
        new TableColumn<string> {
            Header = "Show", Width = CheckHeaderWidth("Show"), SortKey = id => Settings.ReadMap(id).Highlight,
            Draw = (id, _) => {
                CenteredCheck("##highlight", () => Settings.ReadMap(id).Highlight, v => Settings.TuneMap(id).Highlight = v);
                Controls.Tip("Draw this map's node, name and icons on the atlas.");
            },
        },
        new TableColumn<string> {
            Header = "Fav", Width = CheckHeaderWidth("Fav"), SortKey = id => Settings.ReadMap(id).Favorite,
            Draw = (id, _) => {
                CenteredCheck("##favorite", () => Settings.ReadMap(id).Favorite, v => Settings.TuneMap(id).Favorite = v);
                Controls.Tip("Favorite this map - draws the star, auto-creates a waypoint.");
            },
        },
    ];

    private TableColumn<string>[] ContentWeightColumns() =>
    [
        new TableColumn<string> {
            Header = "Icon", Width = CheckHeaderWidth("Icon"), SortKey = id => Settings.ReadContent(id).Highlight,
            Draw = (id, _) => {
                CenteredCheck("##highlight", () => Settings.ReadContent(id).Highlight, v => Settings.TuneContent(id).Highlight = v);
                Controls.Tip("Draw this content type's icon under the map name.");
            },
        },
        new TableColumn<string> {
            Header = "Fav", Width = CheckHeaderWidth("Fav"), SortKey = id => Settings.ReadContent(id).Favorite,
            Draw = (id, _) => {
                CenteredCheck("##favorite", () => Settings.ReadContent(id).Favorite, v => Settings.TuneContent(id).Favorite = v);
                Controls.Tip("Any map carrying this content counts as favorited.");
            },
        },
    ];

    private TableColumn<string>[] RumorWeightColumns() =>
    [
        new TableColumn<string> {
            Header = "Hint", Width = 220f, SortKey = id => Settings.GameData.Rumors[id].Text,
            Draw = (id, _) => {
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Settings.GameData.Rumors[id].Text);
            },
        },
    ];

    #endregion

    #region Waypoints Tab

    private bool DrawWaypointsTab()
    {
        bool d = false;
        var g = Settings.Graphics;
        var w = Settings.Waypoints;
        var t = Settings.Tours;

        if (Controls.Category("Waypoints"))
        {
            d |= Controls.ToggleGrid("wp_togs", new[] {
                Tog("Show Waypoints on Atlas", () => w.ShowWaypoints, v => w.ShowWaypoints = v),
                Tog("Show Waypoint Arrows", () => w.ShowWaypointArrows, v => w.ShowWaypointArrows = v),
                Tog("Invert Waypoint Arrow Colors", () => w.InverWaypointArrowsColors, v => w.InverWaypointArrowsColors = v),
                Tog("Auto Create Waypoints for Favorite Maps", () => w.AutoWaypointFavorites, v => w.AutoWaypointFavorites = v),
                Tog("Auto Remove Completed Waypoints", () => w.AutoRemoveCompletedWaypoints, v => w.AutoRemoveCompletedWaypoints = v),
            }, 0);
        }

        if (Controls.Category("Paths"))
        {
            d |= ImGui.Checkbox("Draw paths to waypoints", ref g.ShowPaths);
            Controls.Tip("Shows the shortest path from the nearest visited node.");

            if (g.ShowPaths)
            {
                d |= Controls.SliderFloat("Waypoint Line Width", () => g.WaypointLineWidth, v => g.WaypointLineWidth = v, 0f, 24f);
                Controls.Tip("Width of the map waypoint path (px).");

                d |= Controls.SliderFloat("Waypoint Dash Length", () => g.WaypointDashLength, v => g.WaypointDashLength = v, 0f, 80f);
                Controls.Tip("Dash length (px) on the waypoint path. 0 = solid line.");

                d |= Controls.SliderFloat("Waypoint Dash Gap", () => g.WaypointDashGap, v => g.WaypointDashGap = v, 0f, 60f);
                Controls.Tip("Gap (px) between dashes on the waypoint path.");
            }

            d |= Controls.SliderFloat("Waypoint Arrow Min Distance", () => g.WaypointArrowMinDistance, v => g.WaypointArrowMinDistance = v, 50f, 1500f);
            Controls.Tip("Off-screen arrow shows past this distance (px). Lower = sooner.");
        }

        if (Controls.Category("Tours"))
        {
            d |= Controls.ToggleGrid("tour_togs", new[] {
                Tog("Show Tours", () => t.ShowTours, v => t.ShowTours = v,
                    "Master switch for drawing all enabled tour routes on the atlas."),
                Tog("Show Next-Stop Target", () => t.ShowNextStopTarget, v => t.ShowNextStopTarget = v,
                    "Pulsing Target icon above each tour's next stop."),
            }, 0);

            d |= Controls.SliderInt("Auto Tour Reach", () => t.AutoTourReach, v => t.AutoTourReach = v, 1, 10);
            Controls.Tip("Max steps between stops when auto-tour chains a route.");
        }
        return d;
    }

    #endregion

    #region Keybinds Tab

    private bool DrawKeybindsTab()
    {
        bool d = false;
        var k = Settings.Keybinds;

        if (Controls.Category("General"))
        {
            d |= DrawHotkey("Toggle Atlas Drawing", k.ToggleDrawingHotkey);
            Controls.Tip("Show/hide atlas overlay drawing. Default: Scroll Lock.");
            d |= DrawHotkey("Refresh Map Cache", k.RefreshMapCacheHotkey);
            Controls.Tip("Default: Home");
            d |= DrawHotkey("Quick Edit Node", k.QuickEditNodeHotkey);
            Controls.Tip("Edit the hovered node's map/content, no settings needed.");
        }

        if (Controls.Category("Panels"))
        {
            d |= DrawHotkey("Toggle Atlas Overview Panel", k.ToggleAtlasOverviewHotkey);
            Controls.Tip("Show/hide Atlas Overview (content tally). Default: Pause.");
            d |= DrawHotkey("Toggle Waypoint Panel", k.ToggleWaypointPanelHotkey);
            Controls.Tip("Show/hide the Waypoints panel. Default: End.");
        }

        if (Controls.Category("Tours"))
        {
            d |= DrawHotkey("Toggle Tours Panel", k.ToggleToursPanelHotkey);
            Controls.Tip("Show/hide the Tours panel (manage named routes).");
            d |= DrawHotkey("Add Tour Stop", k.AddTourStopHotkey);
            Controls.Tip("Add/remove the hovered node from the active tour.");
            d |= DrawHotkey("Build Tour Mode", k.BuildModeHotkey);
            Controls.Tip("Build Mode: left-click adds to tour, right-click removes.");
        }

        if (Controls.Category("Waypoints"))
        {
            d |= DrawHotkey("Add Waypoint", k.AddWaypointHotkey);
            Controls.Tip("Default: Insert");
            d |= DrawHotkey("Remove Waypoint", k.DeleteWaypointHotkey);
            Controls.Tip("Default: Delete");
        }
        return d;
    }

    private string capturingHotkey;

    private bool DrawHotkey(string label, HotkeyNodeV2 node)
    {
        if (node == null) return false;
        bool capturing = capturingHotkey == label;
        Keys bound = node.Value?.Key ?? Keys.None;
        string caption = capturing ? "press a key..." : bound == Keys.None ? "unbound" : bound.ToString();

        if (ImGui.Button(caption + "##" + label, new Vector2(160f, 0f)))
            capturingHotkey = capturing ? null : label;

        ImGui.SameLine();
        ImGui.TextUnformatted(label);
        if (!capturing) return false;

        foreach (Keys key in Enum.GetValues<Keys>())
        {
            if (key == Keys.None || key <= Keys.XButton2 || !Input.GetKeyState(key)) continue;
            capturingHotkey = null;
            if (key == Keys.Escape) return false;
            node.Value = new HotkeyNodeV2.HotkeyNodeValue(key);
            return true;
        }
        return false;
    }

    #endregion

    #region Hacks Tab

    private static readonly Vector4 DragonRed = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 HackGreen = new(0.5f, 1f, 0.5f, 1f);
    private static readonly Vector4 HackGrey = new(0.7f, 0.7f, 0.7f, 1f);

    private bool DrawHacksTab()
    {
        bool d = false;
        var h = Settings.Hacks;

        bool on = h.EnableMemoryWrites;
        if (ImGui.Checkbox("Enable Memory Writes", ref on)) { h.EnableMemoryWrites = on; d = true; }

        ImGui.PushStyleColor(ImGuiCol.Text, DragonRed);
        ImGui.TextWrapped("HERE BE DRAGONS: These features write to memory, which may be riskier " +
                          "than typical ExileCore features. Use at your own risk.");
        ImGui.PopStyleColor();

        if (!h.EnableMemoryWrites)
        {
            ImGui.Spacing();
            ImGui.TextColored(HackGrey, "Off. Nothing is patched and no memory is written.");
            return d;
        }

        ImGui.Spacing();
        ImGui.Separator();

        var patcher = hackPatcher;

        if (patcher == null || !patcher.Attached)
        {
            ImGui.TextColored(DragonRed, patcher?.LastError ?? "Not attached to the client yet.");
            ImGui.TextColored(HackGrey, "Open the game and make sure ExileCore2 is running as administrator - "
                                      + "the client refuses write access to an unelevated process.");
            return d;
        }

        foreach (var (label, state) in patcher.Status())
        {
            var color = state switch {
                PatchState.Patched => HackGreen,
                PatchState.Failed  => DragonRed,
                _                  => HackGrey,
            };
            ImGui.TextColored(color, $"{label}: {state}");
        }

        ImGui.Spacing();

        var every = h.VerifyEveryFrames;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderInt("Re-check patches", ref every, 1, 240,
                            every == 1 ? "every frame" : "every %d frames"))
        {
            h.VerifyEveryFrames = every;
            d = true;
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Crash patch reverify rate. Higher = cheaper, slower to catch.");

        ImGui.Spacing();

        if (!patcher.IsApplied(PatchId.BlockCrashLog))
        {
            ImGui.TextColored(DragonRed, "Crash report not patched, so the other hacks stay hidden.");
            if (!string.IsNullOrEmpty(patcher.LastError))
                ImGui.TextWrapped(patcher.LastError);
            return d;
        }

        if (Controls.Category("Atlas"))
        {
            d |= Controls.ToggleGrid("hack_togs", new[] {
                Tog("Atlas zoom", () => h.AtlasZoom, v => h.AtlasZoom = v,
                    "Rewrites the atlas zoom clamps with the limits below."),
                Tog("Atlas fog", () => h.AtlasFog, v => h.AtlasFog = v,
                    "Stops fog-of-war creation. Reopen the atlas to apply."),
                Tog("Fuck your atlas, Jonathan.", () => h.AtlasFogAll, v => h.AtlasFogAll = v,
                    "Fogs the whole atlas. hello darkness my old friend."),
                Tog("Atlas camera pan", () => h.AtlasCameraPan, v => h.AtlasCameraPan = v,
                    "Goto buttons on panels that pan the atlas to a node."),
            }, 0);

            if (h.AtlasFogAll && h.AtlasFog)
                ImGui.TextColored(HackGrey, "Full fog does nothing while Atlas fog is killing the material.");

            if (h.AtlasZoom)
                d |= DrawZoomLimits(h, patcher);

            if (h.AtlasCameraPan)
            {
                var speed = h.PanSpeed;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderFloat("Pan speed", ref speed, 1f, 30f, "%.0f"))
                {
                    h.PanSpeed = speed;
                    d = true;
                }

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Goto glide speed. Low = slow drift, high = near-instant.");
            }
        }

        if (!string.IsNullOrEmpty(patcher.LastError))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, DragonRed);
            ImGui.TextWrapped(patcher.LastError);
            ImGui.PopStyleColor();
        }

        if (!string.IsNullOrEmpty(patcher.LastNote))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(patcher.LastNote);
            ImGui.PopStyleColor();
        }

        if (!string.IsNullOrEmpty(atlasCamera?.LastError))
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, DragonRed);
            ImGui.TextWrapped("Camera pan: " + atlasCamera.LastError);
            ImGui.PopStyleColor();
        }

        return d;
    }

    private bool DrawZoomLimits(HackSettings h, Patcher patcher)
    {
        bool d = false;

        var outF = h.ZoomOutFactor;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Zoom out limit", ref outF, 1f, Patcher.MaxZoomOutFactor, "%.2fx further")) { h.ZoomOutFactor = outF; d = true; }
        Controls.Tip("How much further out than stock. Capped, a near-zero scale breaks fog.");

        var inF = h.ZoomInFactor;
        ImGui.SetNextItemWidth(220);
        if (ImGui.SliderFloat("Zoom in limit", ref inF, 1f, Patcher.MaxZoomInFactor, "%.2fx closer")) { h.ZoomInFactor = inF; d = true; }
        Controls.Tip("Multiplies how far in the atlas goes. 1x is the stock ceiling.");

        foreach (var (note, stock, live) in patcher.ZoomLimits())
            ImGui.TextColored(HackGrey, $"{stock:0.###} -> {live:0.###}   {note}");

        return d;
    }

    #endregion

    #region Debug Tab

    private bool DrawDebugTab()
    {
        bool d = false;
        var f = Settings.Features;

        if (Controls.Category("Diagnostics"))
        {
            d |= Controls.ToggleGrid("dbg_togs", new[] {
                Tog("Debug Mode", () => f.DebugMode, v => f.DebugMode = v),
                Tog("Show Performance Monitor", () => f.ShowPerfMonitor, v => f.ShowPerfMonitor = v,
                    "Overlay: 60-frame avg CPU time per render/cache section."),
            }, 0);
        }

        if (Controls.Category("Rescrape Game Data"))
        {
            if (ImGui.Button("Rescrape map data"))     { UpdateMapData();     RebuildWeightEditorIds(); }
            ImGui.SameLine();
            if (ImGui.Button("Rescrape content data")) { UpdateContentData(); RebuildWeightEditorIds(); }
            ImGui.SameLine();
            if (ImGui.Button("Rescrape biome data"))   { UpdateBiomeData();   RebuildWeightEditorIds(); }
        }
        return d;
    }

    #endregion
}
