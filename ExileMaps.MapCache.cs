using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using GameOffsets2.Native;
using ExileMaps.Classes;
namespace ExileMaps;

public partial class ExileMapsCore
{
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
        RecalculateWeights();

        SyncFavoriteWaypoints();
        UpdateWaypointPaths();

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
        string mapId = node.Element.Area.Id.Trim();
        string shortID = mapId.Replace("_NoBoss", "");
        Node newNode = new()
        {
            IsUnlocked = node.Element.IsUnlocked,
            IsVisible = node.Element.IsVisible,
            IsVisited = node.Element.IsVisited,
            IsActive = node.Element.IsActive,
            IsCompleted = node.Element.IsCompleted,
            ParentAddress = node.Address,
            Address = node.Element.Address,
            Coordinates = node.Coordinate,
            Name = node.Element.Area.Name,
            Id = mapId,
            MapNode = node,
            MapType = ResolveMapType(shortID, mapId)
        };

        if (!newNode.IsVisited) {
            try {

                AddNodeContentFromIdentity(node, newNode);
                SetAtlasPassive(node, newNode);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }
            
        }
    
        newNode.RecalculateWeight();

        lock (mapCacheLock)        
            return mapCache.TryAdd(node.Coordinate, newNode) ? 1 : 0;

    }

    private int RefreshCachedMapNode(AtlasNodeDescription node, Node cachedNode)
    {
        string shortID = node.Element.Area.Id.Trim().Replace("_NoBoss", "");
        cachedNode.IsUnlocked = node.Element.IsUnlocked;
        cachedNode.IsVisible = node.Element.IsVisible;
        cachedNode.IsVisited = node.Element.IsVisited || (!node.Element.IsUnlocked && node.Element.IsVisited);
        cachedNode.IsActive = node.Element.IsActive;
        cachedNode.IsCompleted = node.Element.IsCompleted;
        cachedNode.Address = node.Element.Address;
        cachedNode.ParentAddress = node.Address;     
        cachedNode.MapNode = node;
        cachedNode.MapType = ResolveMapType(shortID, node.Element.Area.Id);

        if (cachedNode.IsVisited)
            return 1;

        cachedNode.Content.Clear();

        AddNodeContentFromIdentity(node, cachedNode);
        SetAtlasPassive(node, cachedNode);

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

    // Content-point passive ids embed the content type (e.g. "AtlasLeagueBreachOuterNode14").
    private static readonly string[] AtlasPointContentTypes = { "Breach", "Abyss", "Incursion", "Delirium", "Ritual" };

    // Sets GivesAtlasPoint when the passive grants a point and node is not completed.
    // Generic points have ids containing "Inside"; content points embed the content type
    // (e.g. "AtlasLeagueBreachOuterNode14") -> AtlasPointType records it for tinting.
    private void SetAtlasPassive(AtlasNodeDescription node, Node toNode) {
        try {
            var passiveId = node.Element?.AtlasEntry?.PassiveSkill?.Id;
            bool completed = node.Element?.IsCompleted ?? false;
            bool grantsInside = passiveId?.Contains("Inside", StringComparison.OrdinalIgnoreCase) ?? false;
            string contentType = passiveId == null ? null
                : AtlasPointContentTypes.FirstOrDefault(t => passiveId.Contains(t, StringComparison.OrdinalIgnoreCase));
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
