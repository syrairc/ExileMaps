// Cache refresh, node caching, weight recalc, content/passive detection.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using GameOffsets2.Native;
using ExileMaps.Classes;
namespace ExileMaps;

public partial class ExileMapsCore
{
    // O(1) atlas-node lookup by coordinate via the live cache. Replaces an O(n) scan over
    // AtlasPanel.Descriptions (~1000 entries, each with two Coordinate.ToString() allocations).
    // Returns null if the coordinate isn't cached yet (or mid-refresh).
    internal AtlasNodeDescription GetAtlasNodeAt(Vector2i coord)
    {
        try {
            lock (mapCacheLock)
                return mapCache.TryGetValue(coord, out var node) ? node.MapNode : null;
        } catch {
            return null;
        }
    }

    public void RefreshMapCache(bool clearCache = false)
    {
        refreshingCache = true;
        cacheRefreshProgress = 0f;

        if (clearCache)
            lock (mapCacheLock)            
                mapCache.Clear();

        List<AtlasNodeDescription> atlasNodes = [.. AtlasPanel.Descriptions];

        // Build O(1) forward + reverse connection lookups from a single AtlasPanel.Points snapshot.
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

        var timer = new Stopwatch();
        timer.Start();
        long count = 0;
        int total = atlasNodes.Count;
        int processed = 0;
        // Phase 1: cache all nodes before resolving connections to avoid missing forward edges.
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                count += RefreshCachedMapNode(node, cachedNode);
            else
                count += CacheNewMapNode(node);

            // Cap the node loop at 0.8; the rest covers connections + weight/waypoint passes.
            cacheRefreshProgress = total > 0 ? 0.8f * (++processed) / total : 0.8f;
        }

        // Phase 2: resolve connections now that all nodes exist.
        processed = 0;
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                CacheMapConnections(cachedNode, forwardPoints, reverseNeighbors);
            cacheRefreshProgress = total > 0 ? 0.8f + 0.1f * (++processed) / total : 0.9f;
        }
        timer.Stop();
        long time = timer.ElapsedMilliseconds;
        float average = (float)time / count;
        PerfMonitor.Record("Cache.NodePass", timer.ElapsedTicks);

        long t0 = Stopwatch.GetTimestamp();
        RecalculateWeights();
        PerfMonitor.Record("Cache.WeightRecalc", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        SyncFavoriteWaypoints();
        RemoveCompletedWaypoints();
        UpdateWaypointPaths();
        PerfMonitor.Record("Cache.WaypointSync", Stopwatch.GetTimestamp() - t0);

        SnapshotExpeditions();

        // Invalidate memoized step counts and atlas-panel list.
        mapCacheVersion++;

        cacheRefreshProgress = 1f;
        refreshingCache = false;
        refreshCache = false;
        lastRefresh = DateTime.Now;
    }

    private void RecalculateWeights() {

        // Bump so the memoized atlas-panel list refreshes; step counts don't depend on weight.
        weightsRecalcVersion++;

        if (mapCache.Count == 0)
            return;

        // Snapshot under lock; the background job mutates mapCache concurrently.
        List<float> weights;
        lock (mapCacheLock)
            weights = mapCache.Values.Where(x => !x.IsVisited).Select(x => x.Weight).Distinct().ToList();

        if (weights.Count == 0) {
            minMapWeight = 0;
            maxMapWeight = 0;
            return;
        }

        // Clip outliers for color normalization: 6th-smallest and 11th-largest when enough samples.
        weights.Sort();
        int n = weights.Count;
        minMapWeight = n > 5 ? weights[5] : weights[0];
        maxMapWeight = n > 10 ? weights[n - 11] : weights[n - 1];
    }

    private int CacheNewMapNode(AtlasNodeDescription node)
    {
        // Hoist the element + area wrappers once; each node.Element access is a cross-process read.
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
            Address = el.Address,
            Coordinates = node.Coordinate,
            Name = area.Name,
            Id = mapId,
            MapNode = node,
            ArtWidth = el.Width,
            MapType = ResolveMapType(shortID, mapId)
        };

        if (!newNode.IsVisited) {
            try {

                AddNodeContentFromIdentity(node, newNode);
                AddIdBasedContent(newNode);
                SetAtlasPassive(node, newNode);
                AddSpecialModifiers(node, newNode);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }

        }

        // Static data is now resolved; periodic refreshes skip the re-read (see RefreshCachedMapNode).
        // Leave false when the area id is blank (node not loaded yet) so a later pass resolves it.
        newNode.StaticResolved = !string.IsNullOrWhiteSpace(mapId);

        newNode.RecalculateWeight();

        lock (mapCacheLock)        
            return mapCache.TryAdd(node.Coordinate, newNode) ? 1 : 0;

    }

    private int RefreshCachedMapNode(AtlasNodeDescription node, Node cachedNode)
    {
        // Volatile state, always refreshed (cheap boolean reads + the re-snapshotted node pointer).
        // Hoist the element wrapper once; each node.Element access is a cross-process read.
        var el = node.Element;
        cachedNode.IsUnlocked = el.IsUnlocked;
        cachedNode.IsVisible = el.IsVisible;
        cachedNode.IsVisited = el.IsVisited;   // was IsVisited || (!IsUnlocked && IsVisited), which reduces to IsVisited
        cachedNode.IsActive = el.IsActive;
        cachedNode.IsCompleted = el.IsCompleted;
        cachedNode.Address = el.Address;
        cachedNode.ParentAddress = node.Address;
        cachedNode.MapNode = node;
        // Cheap single read; keeps the zoom-independent art width current for IsSpecial detection.
        cachedNode.ArtWidth = el.Width;

        if (cachedNode.IsVisited)
            return 1;

        // Static per-coordinate data (map type, content identity, atlas passive) is immutable for a
        // node, but re-reading Area.Id + the ContentIdentity/AtlasEntry memory lists for every node on
        // every refresh dominates Cache.NodePass and grows as panning loads more nodes. Resolve once.
        // A full cache rebuild (atlas reopen / manual refresh / map-list change) recreates nodes, so
        // StaticResolved resets to false and this re-runs then. Skipped while Area.Id is blank (node
        // not loaded yet) so it self-heals on a later pass.
        if (!cachedNode.StaticResolved) {
            string fullId = el.Area.Id;
            if (!string.IsNullOrWhiteSpace(fullId)) {
                cachedNode.Id = fullId.Trim();
                cachedNode.MapType = ResolveMapType(fullId.Trim().Replace("_NoBoss", ""), fullId);
                cachedNode.Content.Clear();
                cachedNode.Biomes.Clear();
                cachedNode.SpecialModifiers.Clear();
                AddNodeContentFromIdentity(node, cachedNode);
                AddIdBasedContent(cachedNode);
                AddNodeBiome(node, cachedNode);
                SetAtlasPassive(node, cachedNode);
                AddSpecialModifiers(node, cachedNode);
                cachedNode.StaticResolved = true;
            }
        }

        if (Settings.Features.RecalculateNodeWeightsOnRefresh)
            cachedNode.RecalculateWeight();
        return 1;
    }

    // Returns the map type for shortId: direct hit, then id-index fallback, then blank Map.
    private Map ResolveMapType(string shortId, string fullId)
    {
        if (Settings.Maps.Maps.TryGetValue(shortId, out Map mapType))
            return mapType;

        EnsureMapIdIndex();
        if (!string.IsNullOrWhiteSpace(fullId) && mapIdIndex.TryGetValue(fullId, out var byId))
            return byId;

        return new Map();
    }

    // Rebuilds the id->Map index when the dictionary grows. First id wins on collisions.
    private void EnsureMapIdIndex()
    {
        if (mapIdIndex != null && mapIdIndexCount == Settings.Maps.Maps.Count)
            return;

        var idx = new Dictionary<string, Map>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Settings.Maps.Maps.Values)
            foreach (var id in m.IDs)
                if (!string.IsNullOrWhiteSpace(id) && !idx.ContainsKey(id))
                    idx[id] = m;

        mapIdIndex = idx;
        mapIdIndexCount = Settings.Maps.Maps.Count;
    }

    private void CacheMapConnections(Node cachedNode,
        Dictionary<Vector2i, List<Vector2i>> forwardPoints,
        Dictionary<Vector2i, List<Vector2i>> reverseNeighbors) {

        // Forward connections: this node's own neighbor coordinates.
        if (forwardPoints.TryGetValue(cachedNode.Coordinates, out var connectionPoints)) {
            if (cachedNode.Neighbors.Count(x => x.Value.Coordinates != default) >= connectionPoints.Count(v => v != default))
                return;

            cachedNode.NeighborCoordinates = connectionPoints;
            foreach (Vector2i vector in connectionPoints)
                if (mapCache.TryGetValue(vector, out Node neighborNode))
                    cachedNode.Neighbors.TryAdd(vector, neighborNode);
        }

        // Reverse connections: other nodes that point at this node.
        if (reverseNeighbors.TryGetValue(cachedNode.Coordinates, out var sources))
            foreach (var source in sources)
                if (mapCache.TryGetValue(source, out Node neighborNode))
                    cachedNode.Neighbors.TryAdd(source, neighborNode);

    }
    // ContentIdentity.Id is space-stripped (e.g. "PowerfulMapBoss") but ContentTypes keys have spaces;
    // strip spaces from the key for the fallback lookup.
    private void AddNodeContentFromIdentity(AtlasNodeDescription node, Node toNode) {
        var contentIdentity = node.Element?.ContentIdentity;
        if (contentIdentity == null)
            return;

        foreach (var content in contentIdentity) {
            var id = content?.Id;
            if (string.IsNullOrEmpty(id))
                continue;

            var contentType = Settings.Maps.Content.ContentTypes.TryGetValue(id, out var direct)
                ? direct
                : Settings.Maps.Content.ContentTypes.FirstOrDefault(x => x.Key.Replace(" ", "") == id).Value;

            if (contentType != null)
                toNode.Content.TryAdd(contentType.Name, contentType);
        }
    }

    // Per-node biome: AtlasPanelNode.Biome is a single EndgameMapBiome keyed by Id (its Name is blank
    // in memory). Map the Id onto the biome definition so DrawBiomeIcon, label biome-overrides, and the
    // biome-weight term in RecalculateWeight can read it. Keyed by the definition's Name, like content.
    private void AddNodeBiome(AtlasNodeDescription node, Node toNode) {
        var biomeId = node.Element?.Biome?.Id;
        if (string.IsNullOrEmpty(biomeId))
            return;
        if (Settings.Maps.Biomes.Biomes.TryGetValue(biomeId, out var biome))
            toNode.Biomes.TryAdd(biome.Name, biome);
    }

    // Some maps carry content the ContentIdentity list doesn't include but the area id reveals.
    // Expedition logbook maps (id contains "ExpeditionLogbook") always yield Expedition content.
    private void AddIdBasedContent(Node toNode)
    {
        if (string.IsNullOrEmpty(toNode.Id))
            return;

        if (toNode.Id.Contains("ExpeditionLogbook", StringComparison.OrdinalIgnoreCase)) {
            var exp = Settings.Maps.Content.ContentTypes.TryGetValue("Expedition", out var direct)
                ? direct
                : Settings.Maps.Content.ContentTypes.Values.FirstOrDefault(c => c.Name == "Expedition");
            if (exp != null)
                toNode.Content.TryAdd(exp.Name, exp);
        }
    }


    // Content-point passive ids embed the content type (e.g. "AtlasLeagueBreachOuterNode14").
    private static readonly string[] AtlasPointContentTypes = { "Breach", "Abyss", "Incursion", "Delirium", "Ritual" };

    // AtlasChildren is private in ExileCore2.dll; reach it by reflection. Null if the property is gone.
    private static readonly PropertyInfo AtlasChildrenProp =
        typeof(AtlasPanelNode).GetProperty("AtlasChildren", BindingFlags.NonPublic | BindingFlags.Instance);

    // Reads the first line of each atlas-node child tooltip into toNode.SpecialModifiers. These are the
    // special content modifier strings on a node (e.g. on special maps). Tries the private AtlasChildren
    // list first, falls back to the public UI children cast. Defensive: a miss yields no modifiers.
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
                if (tt == null)
                    continue;
                var text = tt.TextNoTags;
                if (string.IsNullOrWhiteSpace(text))
                    text = tt.Text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var line = text.Split('\n')[0].Trim();
                if (line.Length == 0)
                    continue;
                if (!toNode.SpecialModifiers.Contains(line, StringComparer.OrdinalIgnoreCase))
                    toNode.SpecialModifiers.Add(line);
            }
        }
        catch (Exception e) { DebugSwallow("AddSpecialModifiers", e); }
    }

    // Sets GivesAtlasPoint when the passive grants a point and node is not completed.
    // Generic points have passive ids containing "Inside". Content (league) points are identified by
    // the PassiveSkill.Id prefix: "AtlasLeague<ContentType>..." awards a content point, while the
    // "AtlasNormal<ContentType>..." variant carries the same content but awards nothing.
    private void SetAtlasPassive(AtlasNodeDescription node, Node toNode) {
        try {
            var passiveId = node.Element?.AtlasEntry?.PassiveSkill?.Id;
            bool completed = node.Element?.IsCompleted ?? false;
            bool grantsInside = passiveId?.Contains("Inside", StringComparison.OrdinalIgnoreCase) ?? false;

            // Only "AtlasLeague..." passives award a content point; match the embedded content type by
            // name. "AtlasNormal..." passives contain the content type too but grant nothing.
            bool isLeague = passiveId?.StartsWith("AtlasLeague", StringComparison.OrdinalIgnoreCase) ?? false;
            string contentType = isLeague
                ? AtlasPointContentTypes.FirstOrDefault(t => passiveId.Contains(t, StringComparison.OrdinalIgnoreCase))
                : null;
            toNode.AtlasPointType = (grantsInside || contentType == null) ? null : contentType;
            toNode.GivesAtlasPoint = (grantsInside || contentType != null) && !completed;
            toNode.HasAtlasQuest = (passiveId?.Contains("AtlasQuest", StringComparison.OrdinalIgnoreCase) ?? false) && !completed;

            // An uncompleted content-type atlas point (e.g. a Breach point) means the map yields that
            // mechanic when run - mark the node as also having that content so it gets a ring, weight,
            // and shows in the reachable-content tally. TryAdd dedups if it already has it from ContentIdentity.
            if (contentType != null && !completed) {
                var apContent = ResolveAtlasPointContent(contentType);
                if (apContent != null)
                    toNode.Content.TryAdd(apContent.Name, apContent);
            }
        }
        catch (Exception e) { toNode.GivesAtlasPoint = false; toNode.HasAtlasQuest = false; toNode.AtlasPointType = null; DebugSwallow("SetAtlasPassive", e); }
    }

    // Resolves an atlas-point content word ("Breach", "Ritual", ...) to the matching Content definition.
    // Prefers an exact (space-stripped, case-insensitive) name match to avoid picking boss/variant entries,
    // then falls back to the first content whose name or key contains the word. Null if no match.
    private Content ResolveAtlasPointContent(string type) {
        if (string.IsNullOrEmpty(type))
            return null;
        var types = Settings.Maps.Content.ContentTypes;
        foreach (var c in types.Values)
            if (c?.Name != null && c.Name.Replace(" ", "").Equals(type, StringComparison.OrdinalIgnoreCase))
                return c;
        foreach (var (key, c) in types)
            if ((c?.Name != null && c.Name.Contains(type, StringComparison.OrdinalIgnoreCase))
                || (key != null && key.Contains(type, StringComparison.OrdinalIgnoreCase)))
                return c;
        return null;
    }
}
