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
    // Per-node routing cost. Completed nodes (visited, or in `done`) are free. A fresh map costs 1, or
    // with weight-aware routing (`extraMapCost` set: extraMapCost - weight, clamped >= 0): a map worth
    // more than the cost of running one extra map is free to detour through, a worse one costs the difference.
    private static float RouteCost(Node n, HashSet<Vector2i> done, float? extraMapCost, out bool fresh)
    {
        fresh = !(n.IsVisited || n.IsCompleted || (done != null && done.Contains(n.Coordinates)));
        if (!fresh) return 0f;
        if (extraMapCost == null) return 1f;
        return Math.Max(0f, extraMapCost.Value - n.Weight);
    }

    // Tours / Waypoints each have their own toggle + cost; null = weight-aware routing off.
    private float? TourExtraMapCost => Settings.Tours.WeightAwareRouting ? Settings.Tours.ExtraMapCost.Value : null;
    private float? WaypointExtraMapCost => Settings.Waypoints.WeightAwareRouting ? Settings.Waypoints.ExtraMapCost : null;

    // Shared Dijkstra core. Expands from `start` until a node satisfying `isGoal` is popped. Priority
    // is (route cost, fresh-map count, -summed weight), so without weight-aware routing this is
    // "fewest maps, then heaviest". Goal nodes are never expanded. Returns [start, ..., goal] plus the
    // fresh-map count and summed fresh weight, or (null, 0, 0) if no goal is reachable.
    private (List<Node> path, int steps, float weight) Route(Node start, Func<Node, bool> isGoal, HashSet<Vector2i> done, float? extraMapCost)
    {
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
            if (Better(cur, (pri.cost, pri.steps, -pri.negWeight))) continue; // stale entry
            if (isGoal(current))
            {
                var path = new List<Node>();
                for (Node n = current; n != null; n = parent[n.Coordinates]) path.Add(n);
                path.Reverse(); // start -> goal
                return (path, cur.steps, cur.weight);
            }
            foreach (var nb in current.Neighbors.Values)
            {
                if (nb == null) continue;
                float step = RouteCost(nb, done, extraMapCost, out bool fresh);
                var cand = (cur.cost + step, cur.steps + (fresh ? 1 : 0), cur.weight + (fresh ? nb.Weight : 0f));
                if (best.TryGetValue(nb.Coordinates, out var old) && !Better(cand, old)) continue;
                best[nb.Coordinates] = cand; parent[nb.Coordinates] = current;
                pq.Enqueue(nb, (cand.Item1, cand.Item2, -cand.Item3));
            }
        }
        return (null, 0, 0f);
    }

    // Route from destination to the nearest visited node (anchor). Used for waypoint routes and a tour's
    // lead-in; the caller passes its own extra-map cost (null = off). Returns [anchor, ..., destination]
    // plus summed weight, or (null, 0) if unreachable.
    private (List<Node> path, float weight) FindPathToNearestCompleted(Node destination, float? extraMapCost)
    {
        if (destination == null) return (null, 0f);
        if (destination.IsVisited) return (new List<Node> { destination }, destination.Weight);

        var (path, _, weight) = Route(destination, n => n.IsVisited && n.Coordinates != destination.Coordinates, null, extraMapCost);
        if (path == null) return (null, 0f);
        path.Reverse(); // anchor -> destination
        return (path, weight + destination.Weight);
    }

    // Tour segment (Tours.WeightAwareRouting). `done` = nodes committed earlier in the same tour (free to reuse).
    // Returns [from, ..., to] plus the number of fresh maps on it, or (null, 0) if unreachable.
    private (List<Node> path, int steps) FindPath(Node from, Node to, HashSet<Vector2i> done = null)
    {
        if (from == null || to == null) return (null, 0);
        if (from.Coordinates == to.Coordinates) return (new List<Node> { from }, 0);
        var (path, steps, _) = Route(from, n => n.Coordinates == to.Coordinates, done, TourExtraMapCost);
        return (path, steps);
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
                q = q.Where(n => Regex.IsMatch(n.Name, filter, RegexOptions.IgnoreCase) || n.Content.Any(c => c.Value.Name == filter) || n.SpecialModifiers.Any(m => Regex.IsMatch(m, filter, RegexOptions.IgnoreCase)));
            else
                q = q.Where(n => n.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) || n.Content.Any(c => c.Value.Name == filter) || n.SpecialModifiers.Any(m => m.Contains(filter, StringComparison.CurrentCultureIgnoreCase)));
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

        // Single-pass nearest by squared distance: no sort, no sqrt. GetClientRectCache is the
        // engine's last-computed rect (cheap struct read) vs GetClientRect's per-node parent-chain
        // walk. Runs every frame while a node tooltip is up (DrawHoveredNodeOverTooltip), so the old
        // OrderBy over all ~1000 descriptions was the cost. Cache can lag a frame while panning (same
        // accepted tradeoff as the on-screen cull); irrelevant for cursor snapping.
        AtlasNodeDescription closestNode = null;
        float bestDistSq = float.MaxValue;
        foreach (var d in AtlasPanel.Descriptions) {
            float distSq = Vector2.DistanceSquared(cursor, d.Element.GetClientRectCache.Center);
            if (distSq < bestDistSq) { bestDistSq = distSq; closestNode = d; }
        }

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
}
