using System;
using System.Collections.Generic;
using System.Drawing;
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

    // Untouched content color (matches Content's ctor default) — only entries still at this value get
    // recolored on update, so user customizations are preserved.
    private static readonly Color ContentColorDefault = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color ContentColorFallback = Color.FromArgb(255, 220, 220, 220);

    // Default display color per content type, chosen by id keyword (first match wins). Picked to match
    // each mechanic's in-game identity. Covers all current EndgameMapContentVisualIdentity ids; unknown
    // ids fall back to a neutral grey.
    private static Color ContentDefaultColor(string id)
    {
        if (string.IsNullOrEmpty(id)) return ContentColorFallback;
        string s = id.ToLowerInvariant();

        // Biomes
        if (s.Contains("waterbiome")) return Color.FromArgb(255, 80, 170, 230);
        if (s.Contains("mountainbiome")) return Color.FromArgb(255, 170, 175, 185);
        if (s.Contains("grassbiome")) return Color.FromArgb(255, 130, 210, 90);
        if (s.Contains("forestbiome")) return Color.FromArgb(255, 60, 150, 70);
        if (s.Contains("swampbiome")) return Color.FromArgb(255, 130, 140, 60);
        if (s.Contains("desertbiome")) return Color.FromArgb(255, 220, 195, 130);

        // League mechanics
        if (s.Contains("breach")) return Color.FromArgb(255, 170, 90, 230);
        if (s.Contains("ritual")) return Color.FromArgb(255, 210, 40, 40);
        if (s.Contains("abyss")) return Color.FromArgb(255, 120, 70, 200);
        if (s.Contains("expedition")) return Color.FromArgb(255, 215, 175, 95);
        if (s.Contains("delirium") || s.Contains("simulacrum")) return Color.FromArgb(255, 205, 205, 230);
        if (s.Contains("incursion")) return Color.FromArgb(255, 60, 200, 190);
        if (s.Contains("essence")) return Color.FromArgb(255, 100, 200, 230);
        if (s.Contains("azmeri")) return Color.FromArgb(255, 80, 200, 140);

        // Strongboxes / shrines / exiles
        if (s.Contains("strongbox")) return Color.FromArgb(255, 230, 180, 60);
        if (s.Contains("shrine")) return Color.FromArgb(255, 80, 200, 180);
        if (s.Contains("stonecircle")) return Color.FromArgb(255, 220, 170, 70);
        if (s.Contains("rogueexile") || s.Contains("exile")) return Color.FromArgb(255, 90, 150, 230);

        // Rewards / rarity / uniques
        if (s.Contains("headhunter")) return Color.FromArgb(255, 230, 60, 60);
        if (s.Contains("ultimarum") || s.Contains("ultimatum")) return Color.FromArgb(255, 200, 150, 60);
        if (s.Contains("sanctif") || s.Contains("sanctum")) return Color.FromArgb(255, 240, 210, 120);
        if (s.Contains("unique")) return Color.FromArgb(255, 175, 96, 37);
        if (s.Contains("itemrarity") || s.Contains("rarity") || s.Contains("rarecurrency")) return Color.FromArgb(255, 230, 215, 90);
        if (s.Contains("experience")) return Color.FromArgb(255, 150, 190, 230);

        // Corruption
        if (s.Contains("corrupt")) return Color.FromArgb(255, 170, 35, 35);
        if (s.Contains("irradiated")) return Color.FromArgb(255, 120, 230, 80);

        // Monsters / bosses
        if (s.Contains("boss")) return Color.FromArgb(255, 230, 70, 70);
        if (s.Contains("magicmonsters")) return Color.FromArgb(255, 110, 130, 230);
        if (s.Contains("giant")) return Color.FromArgb(255, 230, 140, 60);
        if (s.Contains("rare")) return Color.FromArgb(255, 230, 215, 90);

        // Misc
        if (s.Contains("trader")) return Color.FromArgb(255, 90, 160, 230);
        if (s.Contains("hideout")) return Color.FromArgb(255, 170, 170, 170);
        if (s.Contains("quest")) return Color.FromArgb(255, 255, 200, 40);

        return ContentColorFallback;
    }

    // "PowerfulMapBoss" -> "Powerful Map Boss". Inserts a space between a lower/digit and an upper.
    private static string SplitPascalCase(string s) =>
        string.IsNullOrEmpty(s) ? s
            : System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");

    // Scrapes content types from EndgameMapContentVisualIdentity (the authoritative marker set that
    // node.ContentIdentity is drawn from — 73 entries incl. biome/rarity markers the old
    // EndgameMapContent file lacks). That file has Id + AtlasIcon but no Name, so display names come
    // from a PascalCase split of the Id, enriched by EndgameMapContent.Name where it still exists.
    // Adds missing entries, migrates legacy name-keyed entries to id keys. False if not loaded yet.
    private bool UpdateContentData(bool writeToFile = true) {
      try {
        var visuals = GameController.Files.EndgameMapContentVisualIdentity?.EntriesList;
        if (visuals == null || visuals.Count == 0)
            return false;

        // Build Id -> Name lookup from EndgameMapContent for nicer display names (optional).
        var nameLookup = new Dictionary<string, string>();
        var contentEntries = GameController.Files.EndgameMapContent?.EntriesList;
        if (contentEntries != null)
            foreach (var ce in contentEntries) {
                var cid = ce?.Id;
                if (!string.IsNullOrEmpty(cid) && !string.IsNullOrEmpty(ce.Name))
                    nameLookup[cid] = ce.Name;
            }

        int added = 0, updated = 0;
        foreach (var entry in visuals) {
            var id = entry?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            var icon = entry.AtlasIcon?.ToString();
            var name = nameLookup.TryGetValue(id, out var nicer) ? nicer : SplitPascalCase(id);
            var defaultColor = ContentDefaultColor(id);

            // Find an existing entry under the Id key or a legacy spaced-Name key.
            var existingKey = Settings.Maps.Content.ContentTypes.Keys.FirstOrDefault(k => k == id || k.Replace(" ", "") == id);
            if (existingKey != null) {
                var existing = Settings.Maps.Content.ContentTypes[existingKey];
                existing.Name = name;
                if (!string.IsNullOrEmpty(icon))
                    existing.AtlasIcon = icon;
                // Recolor only entries still at the default white — don't clobber user choices.
                if (existing.Color.ToArgb() == ContentColorDefault.ToArgb())
                    existing.Color = defaultColor;
                if (existingKey != id) {
                    Settings.Maps.Content.ContentTypes.Remove(existingKey);
                    Settings.Maps.Content.ContentTypes.TryAdd(id, existing);
                }
                updated++;
            } else if (Settings.Maps.Content.ContentTypes.TryAdd(id, new Content { Name = name, Weight = 25.0f, AtlasIcon = icon, Color = defaultColor })) {
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
