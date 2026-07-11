// Expeditions: rumour-data load, panel, highlight, waypoint. See docs/superpowers/specs/2026-07-09-expeditions-feature-design.md
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
}
