// Expeditions: rumour-data load, panel, highlight, waypoint. See docs/superpowers/specs/2026-07-09-expeditions-feature-design.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Shape of one json/rumors.json entry.
    private class RumorDef { public string content { get; set; } public string description { get; set; } public float defaultWeight { get; set; } }

    // Load the static rumour->content map and merge into the persisted RumorWeights, keeping any
    // weight the user already tuned (mirrors UpdateContentData's merge).
    public void UpdateRumorData()
    {
        try
        {
            var path = Path.Combine(DirectoryFullName, "json", "rumors.json");
            if (!File.Exists(path)) { LogError($"rumors.json missing: {path}"); return; }

            var defs = JsonSerializer.Deserialize<Dictionary<string, RumorDef>>(File.ReadAllText(path));
            if (defs == null) return;

            foreach (var (text, def) in defs)
            {
                if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var existing))
                {
                    // keep the user's Weight, refresh the metadata from json
                    existing.Text = text;
                    existing.Content = def.content;
                    existing.Description = def.description;
                }
                else
                {
                    Settings.Expeditions.RumorWeights.TryAdd(text, new Rumor
                    {
                        Text = text, Content = def.content, Description = def.description, Weight = def.defaultWeight
                    });
                }
            }
        }
        catch (Exception e) { LogError($"UpdateRumorData failed: {e.Message}"); }
    }

    // Rebuild the expeditions snapshot from AtlasPanel.Buttons. Called inside RefreshMapCache.
    private void SnapshotExpeditions()
    {
        var built = new List<Classes.Expedition>();
        try
        {
            var buttons = AtlasPanel?.Buttons;
            if (buttons != null)
            {
                foreach (var b in buttons)
                {
                    var exp = new Classes.Expedition
                    {
                        ButtonCoord = b.Coordinate,
                        RegionCoord = b.RegionCoordinate,
                    };
                    var nodes = b.RegionNodes;
                    if (nodes != null)
                        foreach (var n in nodes)
                            exp.MapCoords.Add(n.Coordinate);
                    var rumors = b.Rumors;
                    if (rumors != null)
                        foreach (var (k, v) in rumors)
                            exp.Rumors[k] = v;
                    built.Add(exp);
                }
            }
        }
        catch (Exception e) { LogError($"SnapshotExpeditions failed: {e.Message}"); }

        lock (mapCacheLock)
            expeditions = built;
    }

    // true once at least one expedition made it into the last cache refresh.
    private bool ExpeditionsLoaded()
    {
        lock (mapCacheLock) return expeditions.Count > 0;
    }

    // Sort score = sum(weight * count) over the expedition's rumours, using live weights.
    private float ExpeditionScore(Classes.Expedition e)
    {
        float score = 0f;
        foreach (var (text, count) in e.Rumors)
            if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r))
                score += r.Weight * count;
        return score;
    }

    // Label = highest-weight rumour's content + map count. Falls back to region coord if no known rumour.
    private string ExpeditionLabel(Classes.Expedition e)
    {
        string best = null; float bestW = float.NegativeInfinity;
        foreach (var (text, _) in e.Rumors)
            if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r) && r.Weight > bestW)
            { bestW = r.Weight; best = r.Content; }
        best ??= $"Region {e.RegionCoord.X},{e.RegionCoord.Y}";
        return $"{best}  ({e.MapCoords.Count} maps)";
    }

    private void DrawExpeditionsPanel()
    {
        try
        {
            bool justOpened = !expeditionsPanelWasOpen; expeditionsPanelWasOpen = true;
            BeginPersistedWindow(Settings.Expeditions.PanelRect, justOpened, screenCenter, new Vector2(460, 520));
            ImGui.SetNextWindowSizeConstraints(new Vector2(360, 240), new Vector2(float.MaxValue, float.MaxValue));
            ImGui.SetNextWindowBgAlpha(0.9f);

            if (ImGui.Begin("Expeditions###expeditionspanel", ref expeditionsPanelOpen, ImGuiWindowFlags.NoCollapse))
            {
                SavePersistedWindow(Settings.Expeditions.PanelRect);

                List<Classes.Expedition> snapshot;
                lock (mapCacheLock) snapshot = expeditions.ToList();

                if (snapshot.Count == 0)
                    ImGui.TextDisabled("No expeditions loaded. Open the atlas.");

                foreach (var e in snapshot.OrderByDescending(ExpeditionScore))
                {
                    ImGui.PushID($"exp_{e.ButtonCoord.X}_{e.ButtonCoord.Y}");
                    ImGui.CollapsingHeader(ExpeditionLabel(e));
                    ImGui.PopID();
                }

                if (ImGui.Button("Close##expeditions")) expeditionsPanelOpen = false;
            }
            ImGui.End();
        }
        catch (Exception ex)
        {
            LogError("Error drawing expeditions panel: " + ex.Message);
            expeditionsPanelOpen = false;
        }
    }
}
