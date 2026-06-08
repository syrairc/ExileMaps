using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // Merges map entries with the same Name into one, combining IDs. Different ids can share a Name.
    private void MergeDuplicateMapsByName() {
        var dupeGroups = Settings.Maps.Maps
            .GroupBy(kv => kv.Value.Name)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in dupeGroups) {
            var keep = group.First().Value;
            keep.IDs = group.SelectMany(kv => kv.Value.IDs ?? []).Distinct().ToArray();
            if (string.IsNullOrEmpty(keep.ShortestId))
                keep.ShortestId = keep.IDs.OrderBy(x => x.Length).FirstOrDefault();

            foreach (var dup in group.Skip(1))
                Settings.Maps.Maps.Remove(dup.Key);
        }
    }

    // Scrapes map types from EndgameMaps, adds missing entries, refreshes IDs.
    // Returns false if the file list isn't loaded yet.
    private bool UpdateMapData(bool writeToFile = true) {
      try {
        MergeDuplicateMapsByName();

        var endgameMaps = GameController.Files.EndgameMaps?.EntriesList;
        if (endgameMaps == null || endgameMaps.Count == 0)
            return false;

        int added = 0, updated = 0;
        foreach (var endgameMap in endgameMaps) {
            var area = endgameMap?.Area;
            var id = area?.Id;
            var name = area?.Name;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                continue;

            // Skip dev/unused placeholder maps.
            if (id.Contains("DNT-UNUSED") || name.Contains("DNT-UNUSED"))
                continue;

            // Strips _NoBoss suffix to form the key, matching CacheNewMapNode's lookup.
            var shortID = id.Replace("_NoBoss", "");

            var mapType = Settings.Maps.Maps.Values.FirstOrDefault(m => m.Name == name);
            if (mapType != null) {
                if (!mapType.IDs.Contains(id))
                    mapType.IDs = [.. mapType.IDs, id];
                if (string.IsNullOrEmpty(mapType.ShortestId))
                    mapType.ShortestId = shortID;
                updated++;
            } else if (Settings.Maps.Maps.TryGetValue(name.Replace(" ", ""), out mapType) || Settings.Maps.Maps.TryGetValue(shortID, out mapType)) {
                // Migrate any legacy name-keyed entry to the id key and make sure this id is recorded.
                Settings.Maps.Maps.Remove(name.Replace(" ", ""));
                mapType.Name = name;
                mapType.ShortestId = shortID;
                if (!mapType.IDs.Contains(id))
                    mapType.IDs = [.. mapType.IDs, id];
                Settings.Maps.Maps.TryAdd(shortID, mapType);
                updated++;
            } else {
                Settings.Maps.Maps.TryAdd(shortID, new Map {
                    Name = name,
                    IDs = [id],
                    ShortestId = shortID });
                added++;
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.Maps.Maps, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultMapsPath), json);
        }

        LogMessage($"Updated Map Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating map data from game files: " + e.Message);
        return false;
      }
    }

    // Scrapes content types from EndgameMapContent, adds missing entries, migrates legacy name-keyed
    // entries to id keys. Returns false if the file list isn't loaded yet.
    private bool UpdateContentData(bool writeToFile = true) {
      try {
        var contentEntries = GameController.Files.EndgameMapContent?.EntriesList;
        if (contentEntries == null || contentEntries.Count == 0)
            return false;

        // Build Id -> AtlasIcon lookup from the visual identity file list.
        var iconLookup = new Dictionary<string, string>();
        var visuals = GameController.Files.EndgameMapContentVisualIdentity?.EntriesList;
        if (visuals != null)
            foreach (var vi in visuals) {
                var vid = vi?.Id;
                if (!string.IsNullOrEmpty(vid))
                    iconLookup[vid] = vi.AtlasIcon?.ToString();
            }

        int added = 0, updated = 0;
        foreach (var entry in contentEntries) {
            var id = entry?.Id;
            var name = entry?.Name;
            if (string.IsNullOrEmpty(id))
                continue;
            if (string.IsNullOrEmpty(name))
                name = id;

            iconLookup.TryGetValue(id, out var icon);

            // Find an existing entry under the Id key or a legacy spaced-Name key.
            var existingKey = Settings.Maps.Content.ContentTypes.Keys.FirstOrDefault(k => k == id || k.Replace(" ", "") == id);
            if (existingKey != null) {
                var existing = Settings.Maps.Content.ContentTypes[existingKey];
                existing.Name = name;
                if (!string.IsNullOrEmpty(icon))
                    existing.AtlasIcon = icon;
                if (existingKey != id) {
                    Settings.Maps.Content.ContentTypes.Remove(existingKey);
                    Settings.Maps.Content.ContentTypes.TryAdd(id, existing);
                }
                updated++;
            } else if (Settings.Maps.Content.ContentTypes.TryAdd(id, new Content { Name = name, Weight = 25.0f, AtlasIcon = icon })) {
                added++;
                LogMessage($"Added Content Type: {id}");
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.Maps.Content.ContentTypes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultContentPath), json);
        }

        LogMessage($"Updated Content Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating content data from game files: " + e.Message);
        return false;
      }
    }

    // Scrapes biomes from EndgameMapBiomes. Returns false if the file list isn't loaded yet.
    private bool UpdateBiomeData(bool writeToFile = true) {
      try {
        var biomeEntries = GameController.Files.EndgameMapBiomes?.EntriesList;
        if (biomeEntries == null || biomeEntries.Count == 0)
            return false;

        int added = 0;
        foreach (var entry in biomeEntries) {
            var id = entry?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            if (Settings.Maps.Biomes.Biomes.ContainsKey(id))
                continue;

            if (Settings.Maps.Biomes.Biomes.TryAdd(id, new Biome { Name = id, Weight = 0.0f })) {
                added++;
                LogMessage($"Added Biome: {id}");
            }
        }

        if (writeToFile) {
            var json = JsonSerializer.Serialize(Settings.Maps.Biomes.Biomes, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultBiomesPath), json);
        }

        LogMessage($"Updated Biome Data from game files ({added} new)");
        return true;
      } catch (Exception e) {
        LogError("Error updating biome data from game files: " + e.Message);
        return false;
      }
    }
}
