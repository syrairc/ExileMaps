// BFS pathfinding: nearest completed anchor, tour segments, step counts, atlas panel list.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using GameOffsets2.Native;
using ImGuiNET;
using ExileMaps.Classes;

namespace ExileMaps;

public partial class ExileMapsCore
{
    // BFS from destination to nearest visited node (anchor). Among equal-length paths, picks highest
    // summed weight. Returns [anchor, ..., destination] plus weight, or (null, 0) if unreachable.
    private (List<Node> path, float weight) FindPathToNearestCompleted(Node destination)
    {
        if (destination == null)
            return (null, 0f);

        // Destination already completed: trivial one-node path.
        if (destination.IsVisited)
            return (new List<Node> { destination }, destination.Weight);

        var dist = new Dictionary<Vector2i, int>();
        var best = new Dictionary<Vector2i, float>();    // max accumulated weight among shortest paths
        var parent = new Dictionary<Vector2i, Node>();
        var queue = new Queue<Node>();

        dist[destination.Coordinates] = 0;
        best[destination.Coordinates] = destination.Weight;
        parent[destination.Coordinates] = null;
        queue.Enqueue(destination);

        int anchorDist = int.MaxValue;
        float anchorWeight = float.NegativeInfinity;
        Node anchor = null;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            int cd = dist[current.Coordinates];

            // All nodes at anchorDist are dequeued before farther nodes (FIFO); stop once past it.
            if (cd > anchorDist)
                break;

            // Visited node: candidate anchor. Don't expand; route ends here. Prefer nearest, then heaviest.
            if (current.IsVisited && current.Coordinates != destination.Coordinates)
            {
                float w = best[current.Coordinates];
                if (cd < anchorDist || (cd == anchorDist && w > anchorWeight))
                {
                    anchorDist = cd;
                    anchorWeight = w;
                    anchor = current;
                }
                continue;
            }

            foreach (var neighbor in current.Neighbors.Values)
            {
                if (neighbor == null)
                    continue;
                int nd = cd + 1;
                // Skip visited nodes in path weight (pinned to 500; only the anchor appears).
                float nw = best[current.Coordinates] + (neighbor.IsVisited ? 0f : neighbor.Weight);
                if (!dist.TryGetValue(neighbor.Coordinates, out int existing))
                {
                    dist[neighbor.Coordinates] = nd;
                    best[neighbor.Coordinates] = nw;
                    parent[neighbor.Coordinates] = current;
                    queue.Enqueue(neighbor);
                }
                else if (existing == nd && nw > best[neighbor.Coordinates])
                {
                    // Equal-distance alternative with a higher summed weight: prefer it.
                    best[neighbor.Coordinates] = nw;
                    parent[neighbor.Coordinates] = current;
                }
            }
        }

        if (anchor == null)
            return (null, 0f);

        // Walk parent links from anchor toward destination to reconstruct the path.
        var path = new List<Node>();
        for (Node n = anchor; n != null; n = parent[n.Coordinates])
            path.Add(n);

        return (path, anchorWeight);
    }

    // Plain BFS over the adjacency graph from `from` to `to`. Returns [from, ..., to], or null if
    // unreachable. Used to build tour segments between two arbitrary cache nodes.
    private List<Node> FindPath(Node from, Node to)
    {
        if (from == null || to == null) return null;
        if (from.Coordinates == to.Coordinates) return new List<Node> { from };

        var parent = new Dictionary<Vector2i, Node> { [from.Coordinates] = null };
        var queue = new Queue<Node>();
        queue.Enqueue(from);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var neighbor in current.Neighbors.Values)
            {
                if (neighbor == null || parent.ContainsKey(neighbor.Coordinates)) continue;
                parent[neighbor.Coordinates] = current;
                if (neighbor.Coordinates == to.Coordinates)
                {
                    var path = new List<Node>();
                    for (Node n = neighbor; n != null; n = parent[n.Coordinates]) path.Add(n);
                    path.Reverse(); // from -> to
                    return path;
                }
                queue.Enqueue(neighbor);
            }
        }
        return null;
    }

    // Multi-source BFS from all visited nodes. Returns step distances from the explored region;
    // unreachable nodes are omitted.
    private Dictionary<Vector2i, int> ComputeStepCounts()
    {
        // Memoized against cache version; only recomputed on cache refresh.
        if (cachedStepCounts != null && cachedStepCountsVersion == mapCacheVersion)
            return cachedStepCounts;

        var stepCounts = new Dictionary<Vector2i, int>();
        var queue = new Queue<Node>();

        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values.Where(x => x.IsVisited))
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
                    if (neighbor == null || stepCounts.ContainsKey(neighbor.Coordinates))
                        continue;
                    stepCounts[neighbor.Coordinates] = nextDist;
                    queue.Enqueue(neighbor);
                }
            }
        }

        cachedStepCounts = stepCounts;
        cachedStepCountsVersion = mapCacheVersion;
        return stepCounts;
    }

    // Memoized list of non-visited nodes filtered and sorted. Recomputed only when cache or
    // filter/sort inputs change.
    private List<Node> GetAtlasPanelList(string filter, bool useRegex, string sortBy, string sortBy2, int maxSteps, int maxItems)
    {
        var sig = (mapCacheVersion, weightsRecalcVersion, filter ?? "", useRegex, sortBy ?? "", sortBy2 ?? "", maxSteps, maxItems);
        if (cachedAtlasList != null && cachedAtlasSig.Equals(sig))
            return cachedAtlasList;

        var stepCounts = ComputeStepCounts();
        int GetSteps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

        List<Node> nodes;
        lock (mapCacheLock)
            nodes = mapCache.Values.Where(x => !x.IsVisited).ToList();

        IEnumerable<Node> q = nodes;
        if (!string.IsNullOrEmpty(filter)) {
            if (useRegex)
                q = q.Where(n => Regex.IsMatch(n.Name, filter, RegexOptions.IgnoreCase) || n.Content.Any(c => c.Value.Name == filter));
            else
                q = q.Where(n => n.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || n.Content.Any(c => c.Value.Name == filter));
        }
        if (maxSteps > 0)
            q = q.Where(n => GetSteps(n) <= maxSteps);

        IOrderedEnumerable<Node> ordered = sortBy switch
        {
            "Name" => q.OrderBy(n => n.Name),
            "Steps" => q.OrderBy(n => GetSteps(n)),
            _ => q.OrderByDescending(n => n.Weight),
        };
        ordered = sortBy2 switch
        {
            "Name" => ordered.ThenBy(n => n.Name),
            "Weight" => ordered.ThenByDescending(n => n.Weight),
            "Steps" => ordered.ThenBy(n => GetSteps(n)),
            _ => ordered,
        };

        cachedAtlasList = ordered.Take(maxItems).ToList();
        cachedAtlasSig = sig;
        return cachedAtlasList;
    }

    private Node GetClosestNodeToCursor() {
        // ImGui.GetMousePos() works over fog; UIHoverElement returns the panel background over fog,
        // which would snap to the panel center instead of the cursor position.
        var cursor = ImGui.GetMousePos();
        var closestNode = AtlasPanel.Descriptions
            .OrderBy(x => Vector2.Distance(cursor, x.Element.GetClientRect().Center))
            .FirstOrDefault();

        if (closestNode != null && mapCache.TryGetValue(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else
            return null;
    }

    private Node GetClosestNodeToCenterScreen() {
        var closestNode = AtlasPanel.Descriptions.OrderBy(x => Vector2.Distance(screenCenter, x.Element.GetClientRect().Center)).AsParallel().FirstOrDefault();
        if (mapCache.TryGetValue(closestNode.Coordinate, out Node cachedNode))
            return cachedNode;
        else 
            return null;
    }

    private void UpdateWaypointPaths()
    {
        foreach (var waypoint in Settings.Waypoints.Waypoints.Values)
        {
            if (mapCache.TryGetValue(waypoint.Coordinates, out Node waypointNode))
            {
                var (path, weight) = FindPathToNearestCompleted(waypointNode);
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
}
