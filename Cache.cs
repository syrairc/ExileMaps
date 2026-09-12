using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using GameOffsets2.Native;
using ExileMaps.Classes;
using System.Drawing;
using System.IO;
using Newtonsoft.Json;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Enums;
using ExileCore2.Shared.Interfaces;
using ExileCore2.PoEMemory.FilesInMemory;
using System.Numerics;
using ImGuiNET;

namespace ExileMaps;

public partial class ExileMapsCore
{
    #region Map Cache

    private static NodeStates StateOf(Node n) =>
        n.IsDone ? NodeStates.Visited :
        n.IsUnlocked ? NodeStates.Unlocked :
        n.IsVisible ? NodeStates.Locked :
        NodeStates.Hidden;

    public void RefreshMapCache(bool clearCache = false)
    {
        cacheRefreshProgress = 0f;

        if (clearCache) {
            lock (mapCacheLock)
                mapCache.Clear();
            connectionCurves.Clear();
            lastCurveRecordCount = -1;
        }

        RefreshRitualLine();

        long tSnap = Stopwatch.GetTimestamp();
        List<AtlasNodeDescription> atlasNodes = [.. AtlasPanel.Descriptions];
        lastDescriptionCount = atlasNodes.Count;

        var points = AtlasPanel.Points.ToList();
        var forwardPoints = new Dictionary<Vector2i, List<Vector2i>>(points.Count);
        var reverseNeighbors = new Dictionary<Vector2i, List<Vector2i>>(points.Count);
        foreach (var point in points) {
            forwardPoints[point.Source] = point.Targets;
            foreach (var neighbor in point.Targets) {
                if (neighbor == default)
                    continue;
                if (!reverseNeighbors.TryGetValue(neighbor, out var sources))
                    reverseNeighbors[neighbor] = sources = new List<Vector2i>();
                sources.Add(point.Source);
            }
        }

        PerfMonitor.Record("Cache.Snapshot", Stopwatch.GetTimestamp() - tSnap);

        long tPhase = Stopwatch.GetTimestamp();
        int changes = 0;
        int total = atlasNodes.Count;
        int processed = 0;

        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode)) {
                if (RefreshCachedMapNode(node, cachedNode)) changes++;
            } else {
                changes += CacheNewMapNode(node);
            }

            cacheRefreshProgress = total > 0 ? 0.8f * (++processed) / total : 0.8f;
        }

        PerfMonitor.Record("Cache.NodePass1", Stopwatch.GetTimestamp() - tPhase);
        tPhase = Stopwatch.GetTimestamp();

        processed = 0;
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                if (CacheMapConnections(cachedNode, forwardPoints, reverseNeighbors)) changes++;
            cacheRefreshProgress = total > 0 ? 0.8f + 0.1f * (++processed) / total : 0.9f;
        }
        PerfMonitor.Record("Cache.NodePass2", Stopwatch.GetTimestamp() - tPhase);

        bool dirty = clearCache || changes > 0;

        if (dirty) {
            long t0 = Stopwatch.GetTimestamp();
            RecalculateWeights();
            PerfMonitor.Record("Cache.WeightRecalc", Stopwatch.GetTimestamp() - t0);
        }

        waypointSyncPending = true;

        SnapshotExpeditions();

        long tc0 = Stopwatch.GetTimestamp();
        List<Node> curveNodes;
        lock (mapCacheLock)
            curveNodes = [.. mapCache.Values];
        RefreshConnectionCurves(curveNodes);
        PerfMonitor.Record("Cache.ConnectionCurves", Stopwatch.GetTimestamp() - tc0);

        if (dirty) mapCacheVersion++;

        cacheRefreshProgress = 1f;
        lastRefreshMs = Environment.TickCount64;
    }

    private void RecalculateWeights() {

        weightsRecalcVersion++;

        if (mapCache.Count == 0)
            return;

        lock (mapCacheLock)
            foreach (var node in mapCache.Values)
                node.RecalculateWeight();
    }

    private int CacheNewMapNode(AtlasNodeDescription node)
    {
        var el = node.Element;
        var area = el.Area;
        string mapId = area.Id.Trim();
        string shortID = mapId.Replace("_NoBoss", "");
        Node newNode = new()
        {
            IsUnlocked = el.IsUnlocked,
            IsVisible = el.IsVisible,
            IsVisited = el.IsVisited,
            IsActive = el.IsActive,
            IsCompleted = el.IsCompleted,
            ParentAddress = node.Address,
            Coordinates = node.Coordinate,
            Name = area.Name,
            Id = mapId,
            MapNode = node,
            ArtWidth = el.Width,
            MapType = ResolveMapType(shortID, mapId)
        };

        newNode.ResolveSpecial();
        CacheWorldPos(node, newNode);

        if (!newNode.IsDone) {
            try {

                AddNodeContentFromIdentity(node, newNode);
                AddIdBasedContent(newNode);
                AddNodeBiome(node, newNode);
                SetAtlasPassive(node, newNode);
                AddSpecialModifiers(node, newNode);
                RefreshNodeAtlasMods(node, newNode);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }

        }

        newNode.StaticResolved = !string.IsNullOrWhiteSpace(mapId);

        newNode.RecalculateWeight();

        lock (mapCacheLock)
            return mapCache.TryAdd(node.Coordinate, newNode) ? 1 : 0;

    }

    private bool RefreshCachedMapNode(AtlasNodeDescription node, Node cachedNode)
    {
        var el = node.Element;
        bool unlocked = el.IsUnlocked, visible = el.IsVisible, visited = el.IsVisited, completed = el.IsCompleted;
        bool changed = unlocked != cachedNode.IsUnlocked || visible != cachedNode.IsVisible
                    || visited != cachedNode.IsVisited || completed != cachedNode.IsCompleted;

        cachedNode.IsUnlocked = unlocked;
        cachedNode.IsVisible = visible;
        cachedNode.IsVisited = visited;
        cachedNode.IsActive = el.IsActive;
        cachedNode.IsCompleted = completed;

        int kids = (int)el.ChildCount;
        if (kids != cachedNode.ModifierChildCount) {
            cachedNode.ModifierChildCount = kids;
            cachedNode.SpecialModifiers.Clear();
            cachedNode.ModifierDetails.Clear();
            AddSpecialModifiers(node, cachedNode);
            changed = true;
        }

        cachedNode.ParentAddress = node.Address;
        cachedNode.MapNode = node;
        cachedNode.ArtWidth = el.Width;

        CacheWorldPos(node, cachedNode);

        bool wasSpecial = cachedNode.IsSpecial;
        cachedNode.ResolveSpecial();
        changed |= wasSpecial != cachedNode.IsSpecial;

        changed |= RefreshNodeAtlasMods(node, cachedNode);

        if (cachedNode.IsDone)
            return changed;

        if (!cachedNode.StaticResolved) {
            string fullId = el.Area.Id;
            if (!string.IsNullOrWhiteSpace(fullId)) {
                cachedNode.Id = fullId.Trim();
                cachedNode.MapType = ResolveMapType(fullId.Trim().Replace("_NoBoss", ""), fullId);
                if (string.IsNullOrWhiteSpace(cachedNode.Name))
                    cachedNode.Name = el.Area.Name;
                cachedNode.Content.Clear();
                cachedNode.Biomes.Clear();
                cachedNode.SpecialModifiers.Clear();
                cachedNode.ModifierDetails.Clear();
                AddNodeContentFromIdentity(node, cachedNode);
                AddIdBasedContent(cachedNode);
                AddNodeBiome(node, cachedNode);
                SetAtlasPassive(node, cachedNode);
                AddSpecialModifiers(node, cachedNode);
                RefreshNodeAtlasMods(node, cachedNode, force: true);
                cachedNode.StaticResolved = true;
                changed = true;
            }
        }

        return changed;
    }

    private MapInfo ResolveMapType(string shortId, string fullId)
    {
        if (Settings.GameData.Maps.TryGetValue(shortId, out MapInfo mapType))
            return mapType;

        EnsureMapIdIndex();
        if (!string.IsNullOrWhiteSpace(fullId) && mapIdIndex.TryGetValue(fullId, out var byId))
            return byId;

        return new MapInfo();
    }

    private void EnsureMapIdIndex()
    {
        if (mapIdIndex != null && mapIdIndexCount == Settings.GameData.Maps.Count)
            return;

        var idx = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Settings.GameData.Maps.Values)
            foreach (var id in m.IDs)
                if (!string.IsNullOrWhiteSpace(id) && !idx.ContainsKey(id))
                    idx[id] = m;

        mapIdIndex = idx;
        mapIdIndexCount = Settings.GameData.Maps.Count;
    }

    private static void CacheWorldPos(AtlasNodeDescription node, Node cachedNode)
    {
        if (cachedNode.HasWorldPos)
            return;
        try {
            var d3d = node?.Description3D;
            if (d3d == null)
                return;
            var pos = d3d.Position;
            if (pos == System.Numerics.Vector3.Zero)
                return;
            cachedNode.WorldPos = pos;
            cachedNode.HasWorldPos = true;
        } catch { }
    }

    private bool CacheMapConnections(Node cachedNode,
        Dictionary<Vector2i, List<Vector2i>> forwardPoints,
        Dictionary<Vector2i, List<Vector2i>> reverseNeighbors) {

        if (cachedNode.ConnectionsResolved)
            return false;

        bool changed = false;

        if (forwardPoints.TryGetValue(cachedNode.Coordinates, out var connectionPoints)) {
            int haveNeighbors = 0;
            foreach (var kv in cachedNode.Neighbors)
                if (kv.Value.Coordinates != default) haveNeighbors++;
            int wantNeighbors = 0;
            foreach (var v in connectionPoints)
                if (v != default) wantNeighbors++;
            if (haveNeighbors >= wantNeighbors) {
                cachedNode.ConnectionsResolved = true;
                return true;
            }

            cachedNode.NeighborCoordinates = connectionPoints;
            foreach (Vector2i vector in connectionPoints)
                if (mapCache.TryGetValue(vector, out Node neighborNode))
                    changed |= cachedNode.Neighbors.TryAdd(vector, neighborNode);
        }

        if (reverseNeighbors.TryGetValue(cachedNode.Coordinates, out var sources))
            foreach (var source in sources)
                if (mapCache.TryGetValue(source, out Node neighborNode))
                    changed |= cachedNode.Neighbors.TryAdd(source, neighborNode);

        return changed;
    }
    private void AddNodeContentFromIdentity(AtlasNodeDescription node, Node toNode) {
        var contentIdentity = node.Element?.ContentIdentity;
        if (contentIdentity == null)
            return;

        foreach (var content in contentIdentity) {
            var id = content?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            var contentType = Settings.GameData.Content.TryGetValue(id, out var direct)
                ? direct
                : Settings.GameData.Content.FirstOrDefault(x => x.Key.Replace(" ", "") == id).Value;

            if (contentType != null)
                toNode.Content.TryAdd(contentType.Name, contentType);
        }
    }

    #region Atlas Modifiers

    private readonly Dictionary<(string, int), string> atlasModTextCache = [];
    private readonly Dictionary<string, AtlasModInfo> atlasModByKey = [];
    private readonly Dictionary<string, GameStat> atlasModStats = [];
    private readonly Dictionary<GameStat, int> atlasModScratch = [];
    private readonly List<AtlasStatValue> atlasModRaw = [];
    private readonly Dictionary<string, int> atlasModTotals = [];
    private readonly List<AtlasStatValue> atlasModOrder = [];
    private readonly Dictionary<StatDescriptionWrapper, HashSet<GameStat>> atlasModOwnedStats = [];
    private readonly HashSet<string> atlasModContentOnly = [];
    private bool atlasModDescriptionsUsable;
    private AtlasNodeModReader atlasModReader;
    private StatDescriptionWrapper endgameMapStatDescriptions;
    private bool endgameMapStatDescriptionsFailed;
    private long ritualLineAddress;
    private long ritualLineStamp;
    private bool atlasModsChanged;

    private const string EndgameMapStatDescriptionsFile = "Metadata/StatDescriptions/endgame_map_stat_descriptions.csd";

    private AtlasNodeModReader AtlasModReader =>
        atlasModReader ??= new AtlasNodeModReader(GameController.Memory, GameController.Files);

    private void RefreshRitualLine()
    {
        try {
            ritualLineAddress = AtlasModReader.ReadRitualLine(AtlasPanel?.Address ?? 0);
            ritualLineStamp = AtlasModReader.ReadRitualLineStamp(ritualLineAddress);
        } catch (Exception e) {
            ritualLineAddress = 0;
            ritualLineStamp = 0;
            DebugSwallow("RefreshRitualLine", e);
        }
    }

    private bool RefreshNodeAtlasMods(AtlasNodeDescription node, Node toNode, bool force = false)
    {
        try {
            var element = node.Element;
            long el = element?.Address ?? 0;
            if (el == 0)
                return false;

            var pin = AtlasModReader.ReadPin(el);
            int fingerprint = AtlasNodeModReader.Fingerprint(pin, ritualLineStamp);

            if (!force && toNode.AtlasModsBuilt && fingerprint == toNode.AtlasModFingerprint)
                return false;

            toNode.AtlasModFingerprint = fingerprint;
            toNode.AtlasModsBuilt = true;
            toNode.AtlasMods.Clear();

            atlasModRaw.Clear();
            AtlasModReader.Collect(pin, ritualLineAddress, atlasModRaw);
            int contentStart = atlasModRaw.Count;
            AddContentStats(element, atlasModRaw);

            atlasModTotals.Clear();
            atlasModOrder.Clear();
            atlasModContentOnly.Clear();
            for (int i = 0; i < atlasModRaw.Count; i++) {
                var stat = atlasModRaw[i];
                if (!atlasModTotals.ContainsKey(stat.Key)) {
                    atlasModOrder.Add(stat);
                    if (i >= contentStart)
                        atlasModContentOnly.Add(stat.Key);
                }
                atlasModTotals[stat.Key] = atlasModTotals.GetValueOrDefault(stat.Key) + stat.Value;
            }

            foreach (var stat in atlasModOrder) {
                int value = atlasModTotals[stat.Key];
                if (value == 0)
                    continue;

                var info = ResolveAtlasMod(stat.Key, stat.Stat, stat.Type, atlasModContentOnly.Contains(stat.Key));
                if (info == null)
                    continue;

                toNode.AtlasMods.Add(new AtlasModEntry(stat.Key, value, AtlasModText(info, value),
                    info.ScalesWithValue ? value : 1));
            }

            return true;
        } catch (Exception e) {
            DebugSwallow("RefreshNodeAtlasMods", e);
            return false;
        }
    }

    private static void AddContentStats(AtlasPanelNode element, List<AtlasStatValue> into)
    {
        var contents = element.Content;
        if (contents == null)
            return;

        foreach (var content in contents) {
            var stats = content?.Stats;
            var values = content?.StatValues;
            if (stats == null || values == null)
                continue;

            int n = Math.Min(stats.Count, values.Count);
            for (int i = 0; i < n; i++) {
                var record = stats[i];
                int value = values[i];
                if (record == null || value == 0 || string.IsNullOrEmpty(record.Key))
                    continue;

                into.Add(new AtlasStatValue(record.Key, record.MatchingStat, record.Type, value));
            }
        }
    }

    private AtlasModInfo ResolveAtlasMod(string key, GameStat stat, StatType type, bool fromContent)
    {
        if (atlasModByKey.TryGetValue(key, out var known)) {
            if (!fromContent && known is { FromContent: true })
                known.FromContent = false;
            return known;
        }

        string text = TranslateAtlasMod(stat);
        if (text == null) {
            if (atlasModDescriptionsUsable) {
                atlasModByKey[key] = null;
                return null;
            }
            text = PrettifyStatKey(key);
        }

        atlasModStats[key] = stat;

        var info = new AtlasModInfo {
            Key = key,
            ScalesWithValue = type == StatType.IntValue,
            FromContent = fromContent,
            Text = text
        };

        atlasModByKey[key] = info;
        Settings.GameData.AtlasMods[key] = info;
        SeedAtlasModShow(info);
        atlasModsChanged = true;
        return info;
    }

    private void SeedAtlasModShow(AtlasModInfo info)
    {
        if (info is { FromContent: true })
            Settings.Active.AtlasMods.GetOrAdd(info.Key, _ => new AtlasModTuning { Show = false });
    }

    public void SeedContentAtlasModShow()
    {
        foreach (var info in Settings.GameData.AtlasMods.Values)
            SeedAtlasModShow(info);
    }

    private string AtlasModText(AtlasModInfo info, int value)
    {
        var cacheKey = (info.Key, value);
        if (atlasModTextCache.TryGetValue(cacheKey, out var cached))
            return cached;

        string text = (atlasModStats.TryGetValue(info.Key, out var stat)
            ? TranslateAtlasMod(stat, value)
            : null) ?? info.Text;

        atlasModTextCache[cacheKey] = text;
        return text;
    }

    private static readonly char[] ModTextBreaks = ['\n', '\r'];

    private static readonly Regex ModTagPattern = new(@"\[(?:[^\[\]|]*\|)?([^\[\]]*)\]", RegexOptions.Compiled);

    private static string CollapseModText(string text) =>
        ModTagPattern.Replace(
            string.Join(" ", text.Split(ModTextBreaks, StringSplitOptions.RemoveEmptyEntries)), "$1").Trim();

    private static bool IsUntranslated(string text) =>
        string.IsNullOrWhiteSpace(text) || text.Contains("<unknown", StringComparison.OrdinalIgnoreCase);

    private StatDescriptionWrapper EndgameMapStatDescriptions
    {
        get {
            if (endgameMapStatDescriptions != null || endgameMapStatDescriptionsFailed)
                return endgameMapStatDescriptions;
            try {
                endgameMapStatDescriptions = new StatDescriptionWrapper(
                    GameController.Memory, GameController.Files.FindFile, EndgameMapStatDescriptionsFile);
            } catch (Exception e) {
                endgameMapStatDescriptionsFailed = true;
                DebugSwallow("EndgameMapStatDescriptions", e);
            }
            return endgameMapStatDescriptions;
        }
    }

    private bool DescribesStat(StatDescriptionWrapper wrapper, GameStat stat)
    {
        if (!atlasModOwnedStats.TryGetValue(wrapper, out var owned)) {
            owned = [];
            var entries = wrapper.EntriesList;
            if (entries != null)
                foreach (var entry in entries)
                    if (entry?.Stats != null)
                        foreach (var described in entry.Stats)
                            owned.Add(described);
            if (owned.Count == 0)
                return true;
            atlasModOwnedStats[wrapper] = owned;
            atlasModDescriptionsUsable = true;
        }

        return owned.Contains(stat);
    }

    private string TranslateWith(StatDescriptionWrapper wrapper, GameStat stat, string label)
    {
        if (wrapper == null)
            return null;
        try {
            if (!DescribesStat(wrapper, stat))
                return null;
            var text = wrapper.TranslateMod(atlasModScratch);
            return IsUntranslated(text) ? null : CollapseModText(text);
        } catch (Exception e) {
            DebugSwallow("TranslateAtlasMod: " + label, e);
            return null;
        }
    }

    private string TranslateAtlasMod(GameStat stat, int value = 1)
    {
        atlasModScratch.Clear();
        atlasModScratch[stat] = value;

        var files = GameController.Files;
        return TranslateWith(files.StatDescriptions, stat, "general")
            ?? TranslateWith(files.AtlasStatDescriptions, stat, "atlas")
            ?? TranslateWith(EndgameMapStatDescriptions, stat, "endgame");
    }

    private static string PrettifyStatKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return "";
        var words = key.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
            if (words[i].Length > 1 && char.IsLetter(words[i][0]))
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
        return string.Join(" ", words);
    }

    #endregion

    private void AddNodeBiome(AtlasNodeDescription node, Node toNode) {
        var biomeId = node.Element?.Biome?.Id;
        if (string.IsNullOrEmpty(biomeId))
            return;
        toNode.Biomes[biomeId] = new BiomeInfo { Name = biomeId };
    }

    private void AddIdBasedContent(Node toNode)
    {
        if (string.IsNullOrEmpty(toNode.Id))
            return;

        if (toNode.Id.Contains("ExpeditionLogbook", StringComparison.OrdinalIgnoreCase)) {
            var exp = Settings.GameData.Content.TryGetValue("Expedition", out var direct)
                ? direct
                : Settings.GameData.Content.Values.FirstOrDefault(c => c.Name == "Expedition");
            if (exp != null)
                toNode.Content.TryAdd(exp.Name, exp);
        }
    }

    private static readonly PropertyInfo AtlasChildrenProp =
        typeof(AtlasPanelNode).GetProperty("AtlasChildren", BindingFlags.NonPublic | BindingFlags.Instance);

    private void AddSpecialModifiers(AtlasNodeDescription node, Node toNode) {
        try {
            var element = node?.Element;
            if (element == null)
                return;

            IEnumerable<AtlasPanelNodeChild> children = null;
            if (AtlasChildrenProp != null)
                children = AtlasChildrenProp.GetValue(element) as IEnumerable<AtlasPanelNodeChild>;
            if (children == null || !children.Any())
                children = element.GetChildrenAs<AtlasPanelNodeChild>();
            if (children == null)
                return;

            foreach (var child in children) {
                var tt = child?.Tooltip;
                string text = null;
                if (tt != null) {
                    text = tt.TextNoTags;
                    if (string.IsNullOrWhiteSpace(text))
                        text = tt.Text;
                }
                if (string.IsNullOrWhiteSpace(text))
                    text = child?.TextNoTags;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var lines = text.Split('\n');
                var line = lines[0].Trim();
                if (line.Length == 0)
                    continue;
                if (!toNode.SpecialModifiers.Contains(line, StringComparer.OrdinalIgnoreCase))
                    toNode.SpecialModifiers.Add(line);
                for (int i = 1; i < lines.Length; i++) {
                    var detail = lines[i].Trim();
                    if (detail.Length == 0)
                        continue;
                    if (!toNode.ModifierDetails.Contains(detail, StringComparer.OrdinalIgnoreCase))
                        toNode.ModifierDetails.Add(detail);
                }
            }
        }
        catch (Exception e) { DebugSwallow("AddSpecialModifiers", e); }
    }

    private void SetAtlasPassive(AtlasNodeDescription node, Node toNode) {
        try {
            var passiveId = node.Element?.AtlasEntry?.PassiveSkill?.Id;
            bool completed = node.Element?.IsCompleted ?? false;
            bool grantsInside = passiveId?.Contains("Inside", StringComparison.OrdinalIgnoreCase) ?? false;

            bool isLeague = passiveId?.StartsWith("AtlasLeague", StringComparison.OrdinalIgnoreCase) ?? false;
            string contentType = isLeague
                ? ContentDisplaySettings.AtlasPointTypes.FirstOrDefault(t => passiveId.Contains(t, StringComparison.OrdinalIgnoreCase))
                : null;
            toNode.AtlasPointType = (grantsInside || contentType == null) ? null : contentType;
            toNode.GivesAtlasPoint = (grantsInside || contentType != null) && !completed;
            toNode.HasAtlasQuest = (passiveId?.Contains("AtlasQuest", StringComparison.OrdinalIgnoreCase) ?? false) && !completed;

            if (contentType != null && !completed) {
                var apContent = ResolveAtlasPointContent(contentType);
                if (apContent != null)
                    toNode.Content.TryAdd(apContent.Name, apContent);
            }
        }
        catch (Exception e) { toNode.GivesAtlasPoint = false; toNode.HasAtlasQuest = false; toNode.AtlasPointType = null; DebugSwallow("SetAtlasPassive", e); }
    }

    private ContentInfo ResolveAtlasPointContent(string type) {
        if (string.IsNullOrEmpty(type))
            return null;
        var types = Settings.GameData.Content;
        foreach (var c in types.Values)
            if (c?.Name != null && c.Name.Replace(" ", "").Equals(type, StringComparison.OrdinalIgnoreCase))
                return c;
        foreach (var (key, c) in types)
            if ((c?.Name != null && c.Name.Contains(type, StringComparison.OrdinalIgnoreCase))
                || (key != null && key.Contains(type, StringComparison.OrdinalIgnoreCase)))
                return c;
        return null;
    }

    private void MergeDuplicateMapsByName() {
        var dupeGroups = Settings.GameData.Maps
            .GroupBy(kv => kv.Value.Name)
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in dupeGroups) {
            var keep = group.First().Value;
            keep.IDs = group.SelectMany(kv => kv.Value.IDs ?? []).Distinct().ToArray();
            if (string.IsNullOrEmpty(keep.ShortestId))
                keep.ShortestId = keep.IDs.OrderBy(x => x.Length).FirstOrDefault();

            foreach (var dup in group.Skip(1))
                Settings.GameData.Maps.Remove(dup.Key);
        }
    }

    #endregion

    #region Game Data

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

            if (id.Contains("DNT-UNUSED") || name.Contains("DNT-UNUSED"))
                continue;

            var shortID = id.Replace("_NoBoss", "");

            var mapType = Settings.GameData.Maps.Values.FirstOrDefault(m => m.Name == name);
            if (mapType != null) {
                if (!mapType.IDs.Contains(id))
                    mapType.IDs = [.. mapType.IDs, id];
                if (string.IsNullOrEmpty(mapType.ShortestId))
                    mapType.ShortestId = shortID;
                updated++;
            } else {
                Settings.GameData.Maps.TryAdd(shortID, new MapInfo {
                    Name = name,
                    IDs = [id],
                    ShortestId = shortID });
                added++;
            }
        }

        if (writeToFile) {
            var json = JsonConvert.SerializeObject(Settings.GameData.Maps, Formatting.Indented);
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultMapsPath), json);
        }

        if (writeToFile) LogMessage($"Updated Map Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating map data from game files: " + e.Message);
        return false;
      }
    }

    private static readonly Color ContentColorFallback = Color.FromArgb(255, 220, 220, 220);

    internal static Color ContentDefaultColor(string id)
    {
        if (string.IsNullOrEmpty(id)) return ContentColorFallback;
        string s = id.ToLowerInvariant();

        if (s.Contains("waterbiome")) return Color.FromArgb(255, 80, 170, 230);
        if (s.Contains("mountainbiome")) return Color.FromArgb(255, 170, 175, 185);
        if (s.Contains("grassbiome")) return Color.FromArgb(255, 130, 210, 90);
        if (s.Contains("forestbiome")) return Color.FromArgb(255, 60, 150, 70);
        if (s.Contains("swampbiome")) return Color.FromArgb(255, 130, 140, 60);
        if (s.Contains("desertbiome")) return Color.FromArgb(255, 220, 195, 130);

        if (s.Contains("breach")) return Color.FromArgb(255, 170, 90, 230);
        if (s.Contains("ritual")) return Color.FromArgb(255, 210, 40, 40);
        if (s.Contains("abyss")) return Color.FromArgb(255, 120, 70, 200);
        if (s.Contains("expedition")) return Color.FromArgb(255, 215, 175, 95);
        if (s.Contains("delirium") || s.Contains("simulacrum")) return Color.FromArgb(255, 205, 205, 230);
        if (s.Contains("incursion")) return Color.FromArgb(255, 60, 200, 190);
        if (s.Contains("essence")) return Color.FromArgb(255, 100, 200, 230);
        if (s.Contains("azmeri")) return Color.FromArgb(255, 80, 200, 140);

        if (s.Contains("strongbox")) return Color.FromArgb(255, 230, 180, 60);
        if (s.Contains("shrine")) return Color.FromArgb(255, 80, 200, 180);
        if (s.Contains("stonecircle")) return Color.FromArgb(255, 220, 170, 70);
        if (s.Contains("rogueexile") || s.Contains("exile")) return Color.FromArgb(255, 90, 150, 230);

        if (s.Contains("headhunter")) return Color.FromArgb(255, 230, 60, 60);
        if (s.Contains("ultimarum") || s.Contains("ultimatum")) return Color.FromArgb(255, 200, 150, 60);
        if (s.Contains("sanctif") || s.Contains("sanctum")) return Color.FromArgb(255, 240, 210, 120);
        if (s.Contains("unique")) return Color.FromArgb(255, 175, 96, 37);
        if (s.Contains("itemrarity") || s.Contains("rarity") || s.Contains("rarecurrency")) return Color.FromArgb(255, 230, 215, 90);
        if (s.Contains("experience")) return Color.FromArgb(255, 150, 190, 230);

        if (s.Contains("corrupt")) return Color.FromArgb(255, 170, 35, 35);
        if (s.Contains("irradiated")) return Color.FromArgb(255, 120, 230, 80);

        if (s.Contains("boss")) return Color.FromArgb(255, 230, 70, 70);
        if (s.Contains("magicmonsters")) return Color.FromArgb(255, 110, 130, 230);
        if (s.Contains("giant")) return Color.FromArgb(255, 230, 140, 60);
        if (s.Contains("rare")) return Color.FromArgb(255, 230, 215, 90);

        if (s.Contains("trader")) return Color.FromArgb(255, 90, 160, 230);
        if (s.Contains("hideout")) return Color.FromArgb(255, 170, 170, 170);
        if (s.Contains("quest")) return Color.FromArgb(255, 255, 200, 40);

        return ContentColorFallback;
    }

    private static string SplitPascalCase(string s) =>
        string.IsNullOrEmpty(s) ? s
            : System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");

    private bool UpdateContentData(bool writeToFile = true) {
      try {
        var visuals = GameController.Files.EndgameMapContentVisualIdentity?.EntriesList;
        if (visuals == null || visuals.Count == 0)
            return false;

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

            if (Settings.GameData.Content.TryGetValue(id, out var existing)) {
                existing.Name = name;
                if (!string.IsNullOrEmpty(icon))
                    existing.AtlasIcon = icon;
                updated++;
            } else if (Settings.GameData.Content.TryAdd(id, new ContentInfo { Id = id, Name = name, AtlasIcon = icon })) {
                added++;
            }
        }

        if (writeToFile) {
            var json = JsonConvert.SerializeObject(Settings.GameData.Content, Formatting.Indented);
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultContentPath), json);
        }

        if (writeToFile) LogMessage($"Updated Content Data from game files ({added} new, {updated} updated)");
        return true;
      } catch (Exception e) {
        LogError("Error updating content data from game files: " + e.Message);
        return false;
      }
    }

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

            if (Settings.GameData.Biomes.ContainsKey(id))
                continue;

            if (Settings.GameData.Biomes.TryAdd(id, new BiomeInfo { Name = id })) {
                added++;
            }
        }

        if (writeToFile) {
            var json = JsonConvert.SerializeObject(Settings.GameData.Biomes, Formatting.Indented);
            File.WriteAllText(Path.Combine(DirectoryFullName, defaultBiomesPath), json);
        }

        if (writeToFile) LogMessage($"Updated Biome Data from game files ({added} new)");
        return true;
      } catch (Exception e) {
        LogError("Error updating biome data from game files: " + e.Message);
        return false;
      }
    }

    #endregion

    #region Pathfinding

    private static float RouteCost(Node n, HashSet<Vector2i> done, float? extraMapCost, out bool fresh)
    {
        fresh = !(n.IsDone || (done != null && done.Contains(n.Coordinates)));
        if (!fresh) return 0f;
        if (extraMapCost == null) return 1f;
        return Math.Max(0f, extraMapCost.Value - n.Weight);
    }

    private float? TourExtraMapCost => Settings.Tours.WeightAwareRouting ? Settings.Tours.ExtraMapCost : null;

    private float? WaypointExtraMapCost => Settings.Waypoints.WeightAwareRouting ? Settings.Waypoints.ExtraMapCost : null;

    private (List<Node> path, int steps, float weight) Route(Node start, Func<Node, bool> isGoal, HashSet<Vector2i> done, float? extraMapCost)
    {
        if (!start.IsNavigable) return (null, 0, 0f);

        var best = new Dictionary<Vector2i, (float cost, int steps, float weight)> { [start.Coordinates] = (0f, 0, 0f) };
        var parent = new Dictionary<Vector2i, Node> { [start.Coordinates] = null };
        var pq = new PriorityQueue<Node, (float cost, int steps, float negWeight)>();
        pq.Enqueue(start, (0f, 0, 0f));

        static bool Better((float cost, int steps, float weight) a, (float cost, int steps, float weight) b)
            => a.cost < b.cost || (a.cost == b.cost && (a.steps < b.steps || (a.steps == b.steps && a.weight > b.weight)));

        while (pq.TryDequeue(out var current, out var pri))
        {
            var c = current.Coordinates;
            var cur = best[c];
            if (Better(cur, (pri.cost, pri.steps, -pri.negWeight))) continue;
            if (isGoal(current))
            {
                var found = new List<Node>();
                for (Node n = current; n != null; n = parent[n.Coordinates]) found.Add(n);
                found.Reverse();
                return (found, cur.steps, cur.weight);
            }
            foreach (var nb in current.Neighbors.Values)
            {
                if (nb == null || !nb.IsNavigable) continue;
                float step = RouteCost(nb, done, extraMapCost, out bool fresh);
                var cand = (cur.cost + step, cur.steps + (fresh ? 1 : 0), cur.weight + (fresh ? nb.Weight : 0f));
                if (best.TryGetValue(nb.Coordinates, out var old) && !Better(cand, old)) continue;
                best[nb.Coordinates] = cand;
                parent[nb.Coordinates] = current;
                pq.Enqueue(nb, (cand.Item1, cand.Item2, -cand.Item3));
            }
        }
        return (null, 0, 0f);
    }

    private (List<Node> path, float weight) FindPathToNearestCompleted(Node destination, float? extraMapCost)
    {
        if (destination == null)
            return (null, 0f);

        if (destination.IsDone)
            return (new List<Node> { destination }, destination.Weight);

        var (path, _, weight) = Route(destination, n => n.IsDone && n.Coordinates != destination.Coordinates, null, extraMapCost);
        if (path == null)
            return (null, 0f);

        path.Reverse();
        return (path, weight + destination.Weight);
    }

    private (List<Node> path, int steps) FindPath(Node from, Node to, HashSet<Vector2i> done = null)
    {
        if (from == null || to == null) return (null, 0);
        if (from.Coordinates == to.Coordinates) return (new List<Node> { from }, 0);

        var (path, steps, _) = Route(from, n => n.Coordinates == to.Coordinates, done, TourExtraMapCost);
        return (path, steps);
    }

    private Dictionary<Vector2i, int> ComputeStepCounts()
    {
        if (cachedStepCounts != null && cachedStepCountsVersion == mapCacheVersion)
            return cachedStepCounts;

        long t0 = Stopwatch.GetTimestamp();
        var stepCounts = new Dictionary<Vector2i, int>();
        var queue = new Queue<Node>();

        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values.Where(x => x.IsDone))
            {
                stepCounts[node.Coordinates] = 0;
                queue.Enqueue(node);
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                int nextDist = stepCounts[current.Coordinates] + 1;
                foreach (var neighbor in current.Neighbors.Values)
                {
                    if (neighbor == null || !neighbor.IsNavigable || stepCounts.ContainsKey(neighbor.Coordinates))
                        continue;
                    stepCounts[neighbor.Coordinates] = nextDist;
                    queue.Enqueue(neighbor);
                }
            }
        }

        cachedStepCounts = stepCounts;
        cachedStepCountsVersion = mapCacheVersion;
        PerfMonitor.Record("Memo.StepCounts", Stopwatch.GetTimestamp() - t0);
        return stepCounts;
    }

    private List<Node> UnvisitedNodes(string filter, int maxSteps)
    {
        var sig = (mapCacheVersion, weightsRecalcVersion, filter ?? "", maxSteps);
        if (cachedAtlasList != null && cachedAtlasSig.Equals(sig))
            return cachedAtlasList;

        long t0 = Stopwatch.GetTimestamp();
        var stepCounts = ComputeStepCounts();
        int Steps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

        List<Node> nodes;
        lock (mapCacheLock)
            nodes = mapCache.Values.Where(x => !x.IsDone).ToList();

        IEnumerable<Node> q = nodes;
        if (!string.IsNullOrEmpty(filter))
            q = q.Where(n => MapSearchText(n).Contains(filter, StringComparison.OrdinalIgnoreCase));
        if (maxSteps > 0)
            q = q.Where(n => Steps(n) <= maxSteps);

        cachedAtlasList = q.OrderBy(Steps).ThenBy(n => n.Name).ToList();
        cachedAtlasSig = sig;
        PerfMonitor.Record("Memo.AtlasList", Stopwatch.GetTimestamp() - t0);
        return cachedAtlasList;
    }

    private const int HoverChainDepth = 12;
    private long hoverPickLogged;

    private Node HoveredAtlasNode() {
        try {
            var hover = GameController.IngameState.UIHoverElement;
            if (hover == null || hover.Address == 0)
                return null;

            var byAddress = BuildNodeAddressIndex();
            var el = hover;
            for (int depth = 0; el != null && el.Address != 0 && depth < HoverChainDepth; depth++, el = el.Parent) {
                if (!byAddress.TryGetValue(el.Address, out var coord))
                    continue;
                lock (mapCacheLock)
                    if (mapCache.TryGetValue(coord, out var n))
                        return n;
            }
        } catch (Exception e) { DebugSwallow("HoveredAtlasNode", e); }
        return null;
    }

    private Dictionary<long, Vector2i> BuildNodeAddressIndex() {
        var byAddress = new Dictionary<long, Vector2i>();
        foreach (var d in AtlasPanel.Descriptions) {
            long a = d?.Element?.Address ?? 0;
            if (a != 0) byAddress[a] = d.Coordinate;
        }
        return byAddress;
    }

    private Node ClosestNodeGeometric(Vector2 cursor) {
        var project = frameWorldToScreen;
        if (project == null)
            return null;

        var pool = selectedNodes.Count > 0 ? (IEnumerable<Node>)selectedNodes : mapCache.Values;

        Node centre = null, rectHit = null;
        float centreDistSq = float.MaxValue, rectDistSq = float.MaxValue;

        foreach (var n in pool) {
            if (n == null || !n.HasWorldPos)
                continue;
            float distSq = Vector2.DistanceSquared(cursor, project(n.WorldPos));
            if (distSq < centreDistSq) { centreDistSq = distSq; centre = n; }
            if (distSq >= rectDistSq)
                continue;
            var rect = GetNodeRect(n);
            if (rect.Width > 0 && rect.Contains(cursor)) { rectDistSq = distSq; rectHit = n; }
        }
        return rectHit ?? centre;
    }

    private Node GetClosestNodeToCursor() {
        var hovered = HoveredAtlasNode();
        var cursor = ImGui.GetMousePos();
        var pick = hovered ?? ClosestNodeGeometric(cursor);

        if (Settings.Features.DebugLogging) {
            long stamp = Environment.TickCount64 / 500;
            if (stamp != hoverPickLogged) {
                hoverPickLogged = stamp;
                LogMessage($"NodePick cursor=({cursor.X:0},{cursor.Y:0}) hover={Where(hovered)} pick={Where(pick)}");
            }
        }

        if (pick != null)
            return pick;

        AtlasNodeDescription closestNode = null;
        float bestDistSq = float.MaxValue;
        foreach (var d in AtlasPanel.Descriptions) {
            float distSq = Vector2.DistanceSquared(cursor, WorldAlignedRect(d, d.Element.GetClientRectCache).Center);
            if (distSq < bestDistSq) { bestDistSq = distSq; closestNode = d; }
        }

        if (closestNode != null && mapCache.TryGetValue(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else
            return null;
    }

    private static string Where(Node n) =>
        n == null ? "-" : $"{n.Name}[{n.Coordinates.X},{n.Coordinates.Y}]";




    private void UpdateWaypointPaths()
    {
        foreach (var waypoint in Settings.Waypoints.Waypoints.Values)
        {
            if (mapCache.TryGetValue(waypoint.Coordinates, out Node waypointNode))
            {
                var (path, weight) = FindPathToNearestCompleted(waypointNode, WaypointExtraMapCost);
                waypoint.PathFromStart = path;
                waypoint.PathWeight = weight;
            }
            else
            {
                waypoint.PathFromStart = null;
                waypoint.PathWeight = 0f;
            }
        }
    }

    #endregion

}
