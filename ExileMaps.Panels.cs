// Debug, Quick Edit, Node Debug panels, and perf monitor overlay.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using ExileCore2;
using ExileCore2.Shared.Interfaces;
using ImGuiNET;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Live filter text for the settings search box (see DrawSettings).
    private string _settingsFilter = "";

    // Override the engine's flat "draw every top-level Drawer" loop to prepend a search box that
    // hides non-matching submenus. A submenu is shown if its own name/tooltip matches OR any
    // descendant matches, so searching a leaf setting still surfaces its whole parent group.
    public override void DrawSettings()
    {
        try {
            ImGui.SetNextItemWidth(220);
            ImGui.InputTextWithHint("##settingsfilter", "Filter settings...", ref _settingsFilter, 64);
            ImGui.SameLine();
            if (ImGui.Button("Clear##settingsfilter")) _settingsFilter = "";
            ImGui.Separator();
        } catch (Exception ex) {
            LogError($"Settings filter box failed: {ex.Message}");
        }

        var filter = _settingsFilter?.Trim();
        if (string.IsNullOrEmpty(filter)) {
            base.DrawSettings();
            return;
        }

        try {
            foreach (var drawer in Drawers) {
                if (HolderMatchesFilter(drawer, filter))
                    drawer.Draw();
            }
        } catch (Exception ex) {
            LogError($"Filtered settings draw failed: {ex.Message}");
            base.DrawSettings();
        }
    }

    // True if this holder's name/tooltip contains the filter, or any descendant does.
    private static bool HolderMatchesFilter(ISettingsHolder holder, string filter)
    {
        if (holder == null) return false;
        if (!string.IsNullOrEmpty(holder.Name) &&
            holder.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (!string.IsNullOrEmpty(holder.Tooltip) &&
            holder.Tooltip.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (holder.Children != null)
            foreach (var child in holder.Children)
                if (HolderMatchesFilter(child, filter))
                    return true;
        return false;
    }

    private void DoDebugging() {
        mapCache.TryGetValue(GetClosestNodeToCursor().Coordinates, out Node cachedNode);
        LogMessage(cachedNode.ToString());

    }

    private void DrawDebugging(Node cachedNode) {
        var rect = cachedNode.MapNode.Element.GetClientRect();
        string debugText = cachedNode.DebugText() + $"Size: {rect.Width:0} x {rect.Height:0}\n";
        using (Graphics.SetTextScale(1.0f))
            DrawCenteredTextWithBackground(debugText, rect.Center, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

    }

    // Only logs swallowed exceptions when Debug Mode is on. Hot paths swallow per-item so one bad
    // read never aborts a frame; routing through here makes them diagnosable on demand.
    private void DebugSwallow(string context, Exception e)
    {
        if (Settings.Features.DebugMode)
            LogError($"{context}: {e.Message}");
    }

    // Node currently being edited via the hover hotkey, and whether the popup is showing.
    private Node quickEditNode;
    private bool quickEditOpen;

    // Quick edit the map type of the area the player is currently in (atlas closed). The current
    // area's WorldArea.Id shares the id space of atlas node ids, so we resolve it against the map
    // dictionary. Unknown ids fail silently. Builds a MapNode-less Node; DrawQuickEditPanel falls
    // back to screen center for positioning and skips the (empty) content section.
    private void OpenQuickEditForCurrentArea()
    {
        try {
            string areaId = GameController?.IngameState?.Data?.CurrentArea?.Id;
            LogMessage($"QuickEdit(in-map): CurrentArea.Area.Id = '{areaId ?? "<null>"}'");
            if (string.IsNullOrWhiteSpace(areaId)) { LogMessage("QuickEdit(in-map): area id null/empty, aborting."); return; }

            Map map = ResolveMapForId(areaId.Trim());
            if (map == null) {
                LogMessage($"QuickEdit(in-map): '{areaId.Trim()}' not found in map dictionary ({Settings.Maps.Maps.Count} maps). Failing silently.");
                return;
            }

            LogMessage($"QuickEdit(in-map): resolved map '{map.Name}', opening panel.");
            quickEditNode = new Node { Name = map.Name, MapType = map };
            quickEditOpen = true;
        } catch (Exception e) {
            LogError("OpenQuickEditForCurrentArea: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Map for a full area id, or null if unknown (no blank-Map fallback). Mirrors ResolveMapType's
    // lookup order: direct short-id hit, then the id index.
    private Map ResolveMapForId(string fullId)
    {
        string shortId = fullId.Replace("_NoBoss", "");
        if (Settings.Maps.Maps.TryGetValue(shortId, out Map m)) return m;
        EnsureMapIdIndex();
        return mapIdIndex.TryGetValue(fullId, out Map byId) ? byId : null;
    }

    private static void QuickColorEdit(string id, Color color, Action<Color> set) {
        Vector4 v = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (ImGui.ColorEdit4($"##{id}", ref v, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
            set(Color.FromArgb((int)(v.W * 255), (int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255)));
    }

    // Floating popup for inline editing of the hovered node's map type and content types.
    private void DrawQuickEditPanel() {
        try {
            var map = quickEditNode?.MapType;
            if (quickEditNode == null || map == null) { quickEditOpen = false; quickEditNode = null; return; }

            Vector2 pos;
            try { pos = quickEditNode.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch (Exception e) { pos = screenCenter; DebugSwallow("waypoint position read", e); }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin($"Quick Edit###quickedit", ref quickEditOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextDisabled($"{quickEditNode.Name}  ({map.Name})");
                ImGui.Separator();

                bool highlight = map.Highlight;
                if (ImGui.Checkbox("Highlight##qe", ref highlight)) map.Highlight = highlight;
                ImGui.SameLine();
                bool fav = map.Favorite;
                if (ImGui.Checkbox("Favorite##qe", ref fav)) map.Favorite = fav;

                float weight = map.Weight;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderFloat("Weight##qe", ref weight, -50f, 50f, "%.1f")) map.Weight = weight;

                QuickColorEdit("qe_node", map.NodeColor, c => map.NodeColor = c);
                ImGui.SameLine(); ImGui.Text("Node");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_name", map.NameColor, c => map.NameColor = c);
                ImGui.SameLine(); ImGui.Text("Name");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_bg", map.BackgroundColor, c => map.BackgroundColor = c);
                ImGui.SameLine(); ImGui.Text("Text BG");

                bool cbw = map.ColorNodesByWeight;
                if (ImGui.Checkbox("Color node by weight##qe", ref cbw)) map.ColorNodesByWeight = cbw;
                bool nbw = map.UseWeightColorForName;
                if (ImGui.Checkbox("Color name by weight##qe", ref nbw)) map.UseWeightColorForName = nbw;

                ImGui.Text("Icon"); ImGui.SameLine();
                SettingsHelpers.IconPicker("qeicon", map.Icon, i => map.Icon = i);

                if (quickEditNode.Content.Count > 0) {
                    ImGui.Separator();
                    ImGui.Text("Content");
                    foreach (var (cname, content) in quickEditNode.Content) {
                        ImGui.PushID($"qe_c_{cname}");
                        QuickColorEdit("col", content.Color, c => content.Color = c);
                        ImGui.SameLine();
                        bool ring = content.Highlight;
                        if (ImGui.Checkbox("Ring##c", ref ring)) content.Highlight = ring;
                        ImGui.SameLine();
                        bool cfav = content.Favorite;
                        if (ImGui.Checkbox("Fav##c", ref cfav)) content.Favorite = cfav;
                        ImGui.SameLine();
                        float cw = content.Weight;
                        ImGui.SetNextItemWidth(110);
                        if (ImGui.SliderFloat("##cw", ref cw, -100f, 100f, "%.0f")) content.Weight = cw;
                        ImGui.SameLine();
                        ImGui.TextUnformatted(content.Name);
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

    // Node currently shown in the debug popup, and whether it's open.
    private Node debugNode;
    private bool debugNodeOpen;

    // Floating popup showing element flags, atlas-passive state, biome, and content for the hovered node.
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

                // Named element status getters, rendered true=1/false=0 in this order:
                // CanTraverse, IsActive, IsCompleted, IsSaturated, IsScrollable, IsUnlocked,
                // IsValid, IsVisible, IsVisibleLocal, IsVisited, HasShinyHighlight.
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

                // Element.Flags is a separate List<bool> of the node's raw flag bits.
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
}
