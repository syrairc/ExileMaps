
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2.PoEMemory;
using ImGuiNET;
using ExileImGui2;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{

    #region Grouped Panel

    private void DrawGroupedPanel()
    {
        if (navItems == null) return;

        try {
            ImGui.SetNextWindowSizeConstraints(new Vector2(460, 320), new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowSize(new Vector2(920, 640), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowBgAlpha(0.9f);

            if (ImGui.Begin("ExileMaps###exilemapspanel", ref panelOpen, ImGuiWindowFlags.NoCollapse))
                ExileImGui2.Nav.Rail("sections", navItems, navState);
            ImGui.End();
        } catch (Exception e) {
            LogError("Error drawing grouped panel: " + e.Message);
        }
    }

    private void OpenPanelSection(string key)
    {
        panelOpen = true;
        navState.Active = key;
    }

    private void TogglePanelSection(string key)
    {
        if (panelOpen && navState.Active == key) panelOpen = false;
        else OpenPanelSection(key);
    }

    #endregion

    #region Atlas Button

    private const string SearchBoxTexture = "AtlasSearchBg.dds";

    private Element cachedSearchBox;

    private Element AtlasSearchBox()
    {
        try {
            if (cachedSearchBox is { IsValid: true }) return cachedSearchBox;
            cachedSearchBox = null;

            var kids = UI?.WorldMap?.Children;
            if (kids == null) return null;
            foreach (var c in kids)
                if (c?.TextureName?.EndsWith(SearchBoxTexture, StringComparison.OrdinalIgnoreCase) == true) {
                    cachedSearchBox = c;
                    break;
                }
            return cachedSearchBox;
        } catch (Exception e) {
            LogError("Error resolving atlas search box: " + e.Message);
            return null;
        }
    }

    private void DrawExileMapsButton()
    {
        if (!Settings.Features.ShowAtlasButton) return;

        try {
            var size = ImGui.CalcTextSize("ExileMaps") + new Vector2(14f, 8f) * 2f;
            var pos = Settings.Features.AtlasButtonPos;

            if (pos == Vector2.Zero) {
                var box = AtlasSearchBox();
                if (box == null) return;
                var r = box.GetClientRect();
                pos = new Vector2(r.Left - size.X - 8f, r.Top + (r.Height - size.Y) / 2f);
            }

            if (Floating.Button("exilemaps", "ExileMaps", ref pos, size))
                TogglePanelSection(navState.Active ?? "wp");

            Settings.Features.AtlasButtonPos = pos;
        } catch (Exception e) {
            LogError("Error drawing atlas button: " + e.Message);
        }
    }

    #endregion

    #region Debug Panels

    private Node quickEditNode;
    private bool quickEditOpen;

    private void OpenQuickEditForCurrentArea()
    {
        try {
            string areaId = GameController?.IngameState?.Data?.CurrentArea?.Id;
            LogMessage($"QuickEdit(in-map): CurrentArea.Area.Id = '{areaId ?? "<null>"}'");
            if (string.IsNullOrWhiteSpace(areaId)) { LogMessage("QuickEdit(in-map): area id null/empty, aborting."); return; }

            MapInfo map = ResolveMapForId(areaId.Trim());
            if (map == null) {
                LogMessage($"QuickEdit(in-map): '{areaId.Trim()}' not found in map dictionary ({Settings.GameData.Maps.Count} maps). Failing silently.");
                return;
            }

            LogMessage($"QuickEdit(in-map): resolved map '{map.Name}', opening panel.");
            quickEditNode = new Node { Name = map.Name, MapType = map };
            quickEditOpen = true;
        } catch (Exception e) {
            LogError("OpenQuickEditForCurrentArea: " + e.Message + "\n" + e.StackTrace);
        }
    }

    private MapInfo ResolveMapForId(string fullId)
    {
        string shortId = fullId.Replace("_NoBoss", "");
        if (Settings.GameData.Maps.TryGetValue(shortId, out MapInfo m)) return m;
        EnsureMapIdIndex();
        return mapIdIndex.TryGetValue(fullId, out MapInfo byId) ? byId : null;
    }

    private WeightOpts WeightSliderLook => new()
    {
        Min = minMapWeight, Max = maxMapWeight, Decimals = 1,
        Scale = WeightRamp(),
        Rail = true,
    };

    private readonly TextStyle _quickEditStyleScratch = new();

    private void DrawQuickEditPanel() {
        try {
            var map = quickEditNode?.MapType;
            if (quickEditNode == null || map == null || map.ShortestId == null) { quickEditOpen = false; quickEditNode = null; return; }

            Vector2 pos;
            try { pos = quickEditNode.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch (Exception e) { pos = screenCenter; DebugSwallow("waypoint position read", e); }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin($"Quick Edit###quickedit", ref quickEditOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextDisabled($"{quickEditNode.Name}  ({map.Name})");
                ImGui.Separator();

                var tune = Settings.TuneMap(map.ShortestId);

                bool highlight = tune.Highlight;
                if (ImGui.Checkbox("Draw##qe", ref highlight)) tune.Highlight = highlight;
                ImGui.SameLine();
                bool special = tune.Special;
                if (ImGui.Checkbox("Special##qe", ref special)) { tune.Special = special; RequestSpecialMapsRefresh(); }
                Controls.Tip("Force this map to count as special, overriding the heuristic.");
                ImGui.SameLine();
                bool fav = tune.Favorite;
                if (ImGui.Checkbox("Favorite##qe", ref fav)) tune.Favorite = fav;

                if (Weight.Slider("Weight", () => tune.Weight, v => tune.Weight = v, WeightSliderLook, "qe_w"))
                    MarkWeightsDirty();

                var mapOverrides = Settings.Labels.Maps;
                bool hasOverride = mapOverrides.ContainsKey(map.ShortestId);
                bool wantOverride = hasOverride;
                if (ImGui.Checkbox("Override##qe_ov", ref wantOverride)) {
                    if (wantOverride) mapOverrides[map.ShortestId] = new LabelStyleOverride();
                    else mapOverrides.Remove(map.ShortestId);
                }
                Controls.Tip("Give this one map its own label style and icon.");

                if (mapOverrides.TryGetValue(map.ShortestId, out var ov)) {
                    bool iconOn = ov.IconEnabled;
                    if (ImGui.Checkbox("##qe_icon_on", ref iconOn)) ov.IconEnabled = iconOn;
                    Controls.Tip("Draw a sprite for this map.");
                    ImGui.SameLine();
                    DrawIconPickTinted("qe_icon", "Icon", () => ov.Icon, v => ov.Icon = v,
                        () => ov.IconTint, v => ov.IconTint = v, ov.IconEnabled);

                    DrawIconPickTinted("qe_mapicon", "Map Icon", () => ov.MapIcon, v => ov.MapIcon = v,
                        () => ov.MapIconTint, v => ov.MapIconTint = v, ov.OverrideMapIconTint);
                    Controls.Tip("Node sprite for this map. Circle means unset.");
                    ImGui.SameLine();
                    bool tintOn = ov.OverrideMapIconTint;
                    if (ImGui.Checkbox("Tint##qe_maptint", ref tintOn)) ov.OverrideMapIconTint = tintOn;
                    Controls.Tip("Use this colour instead of the node colour mode.");

                    TextStyle.EditOverride("Label Style", ov.Text, Settings.Labels.Base.Text,
                        quickEditNode.Name, "qe_ls", 150f, _quickEditStyleScratch);
                    Controls.Tip("Applies to this map only.");
                } else if (tune.Special) {
                    TextStyle.EditOverride("Special Style", Settings.Labels.Special.Text,
                        Settings.Labels.Base.Text, quickEditNode.Name, "qe_ls", 150f,
                        _quickEditStyleScratch);
                    Controls.Tip("The Special Maps layer, shared by every special map.");
                }

                if (quickEditNode.Content.Count > 0) {
                    ImGui.Separator();
                    ImGui.Text("Content");
                    foreach (var (cname, content) in quickEditNode.Content) {
                        ImGui.PushID($"qe_c_{cname}");
                        var ctune = Settings.TuneContent(content.Id ?? cname);
                        bool cfav = ctune.Favorite;
                        if (ImGui.Checkbox("##cfav", ref cfav)) ctune.Favorite = cfav;
                        Controls.Tip("Favorite this content type.");
                        ImGui.SameLine();
                        if (Weight.Slider(content.Name, () => ctune.Weight, v => ctune.Weight = v,
                                WeightSliderLook, "cw"))
                            MarkWeightsDirty();
                        ImGui.PopID();
                    }
                }

                ImGui.Separator();
                if (ImGui.Button("Close##qe")) quickEditOpen = false;
            }
            ImGui.End();

            if (!quickEditOpen) quickEditNode = null;
        } catch (Exception e) {
            LogError("Error drawing quick edit panel: " + e.Message);
            quickEditOpen = false;
            quickEditNode = null;
        }
    }

    private Node debugNode;
    private bool debugNodeOpen;

    private void DrawNodeDebugPanel() {
        try {
            if (debugNode == null) { debugNodeOpen = false; return; }
            var node = debugNode;
            var el = node.MapNode?.Element;

            Vector2 pos;
            try { pos = node.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch (Exception e) { pos = screenCenter; DebugSwallow("waypoint position read", e); }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin("Node Debug###nodedebug", ref debugNodeOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextUnformatted(node.DebugText(false));

                string parentAddr = $"{node.ParentAddress:X}";
                if (ImGui.SmallButton($"Copy Parent Address##nd")) ImGui.SetClipboardText(parentAddr);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Copy {parentAddr} to clipboard");

                ImGui.Separator();

                var status = new List<bool>();
                void Add(Func<bool> get) { try { status.Add(get()); } catch { status.Add(false); } }
                Add(() => el.CanTraverse);
                Add(() => el.IsActive);
                Add(() => el.IsCompleted);
                Add(() => el.IsSaturated);
                Add(() => el.IsScrollable);
                Add(() => el.IsUnlocked);
                Add(() => el.IsValid);
                Add(() => el.IsVisible);
                Add(() => el.IsVisibleLocal);
                Add(() => el.IsVisited);
                Add(() => el.HasShinyHighlight);
                ImGui.Text($"Status: {string.Concat(status.Select(b => b ? "1" : "0"))}");

                string bits = "";
                try { bits = string.Concat(el.Flags.Select(b => b ? "1" : "0")); } catch { }
                ImGui.Text($"Flags: {bits}");

                string passiveId = null;
                try { passiveId = el?.AtlasEntry?.PassiveSkill?.Id; } catch { }
                ImGui.Text($"AtlasEntry.PassiveSkill: {(passiveId != null ? "1" : "0")}");
                ImGui.Text($"  PassiveSkill.Id: {passiveId ?? "(none)"}");
                ImGui.Text($"  AtlasPointType: {(string.IsNullOrEmpty(node.AtlasPointType) ? "(generic)" : node.AtlasPointType)}");

                string biomeId = "";
                try { biomeId = node.MapNode.Element.Biome?.Id ?? ""; } catch { }
                ImGui.Text($"Biome.Id: {biomeId}");

                ImGui.Separator();
                ImGui.Text("Content:");
                if (node.Content.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var (_, c) in node.Content)
                        ImGui.TextUnformatted($"  {c.Name}{(string.IsNullOrEmpty(c.AtlasIcon) ? "" : "  [game icon]")}");

                ImGui.Text("Special modifiers:");
                if (node.SpecialModifiers.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var m in node.SpecialModifiers)
                        ImGui.TextUnformatted($"  {m}");

                ImGui.Text("Biomes:");
                var biomeNames = node.Biomes.Where(x => x.Value != null).Select(x => x.Value.Name).ToList();
                if (biomeNames.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var b in biomeNames)
                        ImGui.TextUnformatted($"  {b}");

                ImGui.Separator();
                if (ImGui.Button("Close##nd")) debugNodeOpen = false;
            }
            ImGui.End();

            if (!debugNodeOpen) debugNode = null;
        } catch (Exception e) {
            LogError("Error drawing node debug panel: " + e.Message);
            debugNodeOpen = false;
            debugNode = null;
        }
    }

    private void DrawPerfMonitorOverlay()
    {
        if (!Settings.Features.ShowPerfMonitor) return;

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.SetNextWindowPos(new Vector2(10, 10), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.75f);

        if (!ImGui.Begin("##perfmon", flags)) { ImGui.End(); return; }

        ImGui.TextColored(new Vector4(0.8f, 0.8f, 1f, 1f), "Perf Monitor (60f avg)");
        ImGui.Separator();

        foreach (var (key, ms) in PerfMonitor.Snapshot())
        {
            Vector4 col = ms < 1.0  ? new Vector4(0.4f, 1f, 0.4f, 1f)
                        : ms < 5.0  ? new Vector4(1f, 0.85f, 0.2f, 1f)
                                    : new Vector4(1f, 0.3f, 0.3f, 1f);
            ImGui.TextColored(col, $"{key,-28} {ms,6:F2} ms");
        }

        ImGui.End();
    }

    #endregion

    #region Atlas Overview

    private (int ver, int wver, int n) cachedAtlasStatsSig = (-1, -1, -1);

    private AtlasStats ComputeAtlasStats()
    {
        int n = Settings.AtlasOverview.ReachableSteps;
        var sig = (mapCacheVersion, weightsRecalcVersion, n);
        if (cachedAtlasStats.HasValue && cachedAtlasStatsSig.Equals(sig))
            return cachedAtlasStats.Value;

        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        var stepCounts = ComputeStepCounts();
        int Steps(Node node) => stepCounts.TryGetValue(node.Coordinates, out var s) ? s : int.MaxValue;

        var stats = new AtlasStats { ContentCounts = new Dictionary<string, (int, Color)>() };

        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values)
            {
                if (node.IsVisited) stats.MapsRun++;
                if (node.IsCompleted) stats.MapsCompleted++;
                if (node.GivesAtlasPoint) stats.AtlasPointsTotal++;

                if (node.IsDone) continue;
                if (Steps(node) > n) continue;

                stats.ReachableMaps++;
                if (node.GivesAtlasPoint) stats.AtlasPointsReachable++;
                if (node.HasAtlasQuest) stats.AtlasQuestsReachable++;

                foreach (var (_, content) in node.Content)
                {
                    var key = content.Name ?? "";
                    if (key.Length == 0) continue;
                    if (stats.ContentCounts.TryGetValue(key, out var cur))
                        stats.ContentCounts[key] = (cur.count + 1, cur.color);
                    else
                        stats.ContentCounts[key] = (1, ContentDefaultColor(content.Id));
                }

                foreach (var mod in node.SpecialModifiers)
                {
                    if (string.IsNullOrEmpty(mod)) continue;
                    if (stats.ContentCounts.TryGetValue(mod, out var cur))
                        stats.ContentCounts[mod] = (cur.count + 1, cur.color);
                    else
                        stats.ContentCounts[mod] = (1, ModifierTallyColor);
                }
            }
        }

        cachedAtlasStats = stats;
        cachedAtlasStatsSig = sig;
        PerfMonitor.Record("Memo.AtlasStats", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return stats;
    }

    private void DrawAtlasOverviewBody()
    {
        try
        {
            {

                int n = Settings.AtlasOverview.ReachableSteps;
                ImGui.SetNextItemWidth(160);
                if (ImGui.SliderInt("Reachable within (steps)", ref n, 1, 50))
                    Settings.AtlasOverview.ReachableSteps = n;

                var s = ComputeAtlasStats();
                if (s.ReachableMaps == 0)
                {
                    ImGui.TextDisabled("No reachable maps in range (open the Atlas / widen the step window).");
                }
                else if (ImGui.BeginTable("atlas_overview_content", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit))
                {
                    ImGui.TableSetupColumn("##swatch", ImGuiTableColumnFlags.WidthFixed, 18);
                    ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthStretch, 200);
                    ImGui.TableSetupColumn("Count", ImGuiTableColumnFlags.WidthFixed, 50);
                    foreach (var kv in s.ContentCounts.OrderByDescending(x => x.Value.count))
                    {
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        var c = kv.Value.color;
                        ImGui.ColorButton($"##c_{kv.Key}", new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f),
                            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, new Vector2(14, 14));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(kv.Key);
                        ImGui.TableNextColumn();
                        ImGui.Text(kv.Value.count.ToString());
                    }
                    ImGui.EndTable();
                }

                ImGui.Separator();
                ImGui.Text($"Atlas points: {s.AtlasPointsReachable}");
                ImGui.Text($"Atlas quests: {s.AtlasQuestsReachable}");
                ImGui.Text($"Reachable maps: {s.ReachableMaps}");

                var visibleTours = Settings.Tours.Tours.Values
                    .Where(t => Settings.Tours.ShowTours && t.Show && t.Stops.Count > 0).ToList();
                if (visibleTours.Count > 0)
                {
                    ImGui.Separator();
                    ImGui.Text("Visible tours:");
                    foreach (var t in visibleTours)
                    {
                        var c = t.Color;
                        ImGui.ColorButton($"##tc_{t.Id}", new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f),
                            ImGuiColorEditFlags.NoTooltip | ImGuiColorEditFlags.NoInputs, new Vector2(14, 14));
                        ImGui.SameLine();
                        ImGui.TextUnformatted($"{t.Name}  ({t.Stops.Count} stops)");
                        if (t.Skipped.Count > 0)
                            ImGui.TextDisabled($"   skipped: {string.Join(", ", t.Skipped)}");
                    }
                }

            }
        }
        catch (Exception e)
        {
            LogError("Error drawing atlas overview panel: " + e.Message);
        }
    }

    #endregion

}
