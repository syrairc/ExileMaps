// HTML export: atlas as a zoomable SVG + stats dashboard in a self-contained HTML file.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ExileMaps.Classes;
using Newtonsoft.Json;

namespace ExileMaps;

// Exports the atlas as a zoomable SVG + stats dashboard in a self-contained HTML file.
// Layout uses grid coordinates; svg-pan-zoom via CDN; inline JS for toggles, search, detail panel.
public partial class ExileMapsCore
{
    // Grid-unit -> SVG-pixel spacing and layout padding.
    private const float ExportSpacing = 64f;
    private const float ExportPad = 40f;

    // Guards against double-clicks while a background export is in flight.
    private volatile bool htmlExportBusy = false;

    // ---- Plain, serializable snapshots (no game-memory refs held past the lock) ----

    private sealed class ExportNode
    {
        public string Name;
        public string Id;
        public string MapType;
        public int X, Y;
        public float Weight;
        public bool IsUnlocked, IsVisited, IsCompleted, IsFailed, IsWaypoint, IsFavorited;
        public bool GivesAtlasPoint, HasAtlasQuest;
        public string AtlasPointType;
        public int NodeColorArgb;
        public bool ColorByWeight;
        public bool IsSpecial;         // from Node.IsSpecial (size + name) — the source of truth
        public string SpecialId = "";  // non-empty → has a dedicated special icon; else generic icon
        public List<(string name, int colorArgb)> Content = new();
        public List<string> Biomes = new();
        public List<(int x, int y)> Neighbors = new();
    }

    // Maps the game's map name to the slug used in #star-special-* SVG symbol ids.
    private static readonly Dictionary<string, string> SpecialMapIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["The Ziggurat Refuge"]      = "ziggurat-refuge",
        ["The Burning Monolith"]     = "burning-monolith",
        ["Ancient Gateway"]          = "ancient-gateway",
        ["Vaal Ruins"]               = "vaal-ruins",
        ["The Origin Tower"]         = "origin-tower",
        ["The Patriarch Halls"]      = "patriarch-halls",
        ["The Matriarch Halls"]      = "matriarch-halls",
        ["Precursor Tower"]          = "precursor-tower",
        ["Western Enigma Chamber"]   = "western-enigma",
        ["Eastern Enigma Chamber"]   = "eastern-enigma",
        ["Ruins of Kingsmarch"]      = "ruins-kingsmarch",
        ["Western Gateway"]          = "western-gateway",
        ["Eastern Gateway"]          = "eastern-gateway",
        ["Hilda's Campsite"]         = "hildas-campsite",
        ["The Withered Willow"]      = "withered-willow",
        ["Monastery of the Keepers"] = "monastery-keepers",
        ["The Well of Souls"]        = "well-of-souls",
        ["Sealed Vault"]             = "sealed-vault",
        ["The Reliquary Vault"]      = "reliquary-vault",
        ["Caer Tarth"]               = "caer-tarth",
        // Size-detected landmarks (wider art) — added once their names were confirmed from live memory.
        ["Site of the Chosen"]       = "site-of-chosen",
        ["The Fallen Star"]          = "fallen-star",
        ["The Chained Beast"]        = "chained-beast",
    };

    private sealed class ExportWaypoint { public string Name; public int X, Y; public int ColorArgb; }
    private sealed class ExportTour { public string Name; public int ColorArgb; public List<(int x, int y)> Stops = new(); }

    private sealed class ExportExpedition
    {
        public int Id;
        public int ColorArgb;
        public (int x, int y) Spawn;
        public List<(int x, int y)> Maps = new();
        public List<string> Rumors = new();  // decoded content names, weight-sorted, deduped
        public float TopWeight;
    }

    // ---- Public entry point (called from the settings button) ----

    public void ExportAtlasHtml()
    {
        if (htmlExportBusy) return;
        htmlExportBusy = true;
        try
        {
            // Snapshot on the calling (render) thread; mapCache + Settings are valid here.
            var nodes = SnapshotExportNodes();
            var waypoints = SnapshotExportWaypoints();
            var tours = SnapshotExportTours();
            var exportExpeditions = SnapshotExportExpeditions();

            float minW = minMapWeight, maxW = maxMapWeight;
            var bad = Settings.Maps.BadNodeColor;
            var good = Settings.Maps.GoodNodeColor;
            var visitedLine = Settings.Graphics.VisitedLineColor;
            var unlockedLine = Settings.Graphics.UnlockedLineColor;
            var lockedLine = Settings.Graphics.LockedLineColor;
            float nodeRadius = Settings.Graphics.NodeRadius;

            // Heavy string-building + file write run off-thread so the frame doesn't hitch.
            var t = new Thread(() =>
            {
                try
                {
                    string html = BuildAtlasHtml(nodes, waypoints, tours, exportExpeditions, minW, maxW,
                        bad, good, visitedLine, unlockedLine, lockedLine, nodeRadius);

                    string dir = Path.Combine(DirectoryFullName, "exports");
                    Directory.CreateDirectory(dir);
                    // Fixed name: overwrite the single export in place each time, no timestamped history.
                    string file = Path.Combine(dir, "atlas.html");
                    File.WriteAllText(file, html);
                    LogMessage($"Exported atlas to {file}");

                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true }); }
                    catch (Exception e) { LogError("Atlas HTML written but failed to open browser: " + e.Message); }
                }
                catch (Exception e) { LogError("Error building atlas HTML: " + e.Message + "\n" + e.StackTrace); }
                finally { htmlExportBusy = false; }
            })
            { IsBackground = true };
            t.Start();
        }
        catch (Exception e)
        {
            LogError("Error exporting atlas HTML: " + e.Message + "\n" + e.StackTrace);
            htmlExportBusy = false;
        }
    }

    private List<ExportNode> SnapshotExportNodes()
    {
        var list = new List<ExportNode>();
        lock (mapCacheLock)
        {
            foreach (var node in mapCache.Values)
            {
                if (node == null) continue;
                var en = new ExportNode
                {
                    Name = node.Name ?? "",
                    Id = node.Id ?? "",
                    MapType = node.MapType?.Name ?? node.Name ?? "Unknown",
                    X = node.Coordinates.X,
                    Y = node.Coordinates.Y,
                    Weight = node.Weight,
                    IsUnlocked = node.IsUnlocked,
                    IsVisited = node.IsVisited,
                    IsCompleted = node.IsCompleted,
                    IsFailed = node.IsFailed,
                    IsWaypoint = node.IsWaypoint,
                    IsFavorited = node.IsFavorited,
                    GivesAtlasPoint = node.GivesAtlasPoint,
                    HasAtlasQuest = node.HasAtlasQuest,
                    AtlasPointType = node.AtlasPointType ?? "",
                    NodeColorArgb = (node.MapType?.NodeColor ?? Color.Gray).ToArgb(),
                    ColorByWeight = node.MapType?.ColorNodesByWeight ?? false,
                };
                // Source of truth for "is this special" is Node.IsSpecial (raw art width + name set),
                // so size-detected specials with no dedicated icon still render (via the generic icon).
                en.IsSpecial = node.IsSpecial;
                // SpecialId picks a dedicated icon when we have one. Tolerant of "The " prefix drift.
                if (SpecialMapIds.TryGetValue(en.Name, out var sid)) en.SpecialId = sid;
                else if (en.Name.StartsWith("The ", StringComparison.OrdinalIgnoreCase)
                         && SpecialMapIds.TryGetValue(en.Name.Substring(4), out sid)) en.SpecialId = sid;
                else if (SpecialMapIds.TryGetValue("The " + en.Name, out sid)) en.SpecialId = sid;
                foreach (var c in node.Content.Values)
                    if (c != null && !string.IsNullOrEmpty(c.Name)) en.Content.Add((c.Name, c.Color.ToArgb()));
                foreach (var b in node.Biomes.Values)
                    if (b != null && !string.IsNullOrEmpty(b.Name)) en.Biomes.Add(b.Name);
                foreach (var nc in node.NeighborCoordinates)
                    en.Neighbors.Add((nc.X, nc.Y));
                list.Add(en);
            }
        }
        return list;
    }

    private List<ExportWaypoint> SnapshotExportWaypoints()
    {
        var list = new List<ExportWaypoint>();
        try
        {
            foreach (var w in Settings.Waypoints.Waypoints.Values)
            {
                if (w == null) continue;
                list.Add(new ExportWaypoint
                {
                    Name = string.IsNullOrEmpty(w.Name) ? w.CoordinatesString : w.Name,
                    X = w.Coordinates.X,
                    Y = w.Coordinates.Y,
                    ColorArgb = w.Color.ToArgb(),
                });
            }
        }
        catch (Exception e) { LogError("Atlas export: waypoint snapshot failed: " + e.Message); }
        return list;
    }

    private List<ExportTour> SnapshotExportTours()
    {
        var list = new List<ExportTour>();
        try
        {
            foreach (var tour in Settings.Tours.Tours.Values)
            {
                if (tour?.Stops == null) continue;
                var et = new ExportTour { Name = tour.Name ?? "Tour", ColorArgb = tour.Color.ToArgb() };
                foreach (var s in tour.Stops) et.Stops.Add((s.X, s.Y));
                if (et.Stops.Count > 0) list.Add(et);
            }
        }
        catch (Exception e) { LogError("Atlas export: tour snapshot failed: " + e.Message); }
        return list;
    }

    // Snapshot on the render thread: RumorWeights + the expeditions list are valid here, so decode
    // each region's rumours to content names now (weight-sorted, deduped) rather than holding refs.
    private List<ExportExpedition> SnapshotExportExpeditions()
    {
        var list = new List<ExportExpedition>();
        try
        {
            List<Classes.Expedition> snap;
            lock (mapCacheLock) snap = expeditions.ToList();
            foreach (var e in snap)
            {
                if (e == null) continue;
                var ee = new ExportExpedition
                {
                    Id = e.Id,
                    ColorArgb = ExpeditionTint(e.Id).ToArgb(),
                    Spawn = (e.SpawnCoord.X, e.SpawnCoord.Y),
                };
                foreach (var c in e.MapCoords) ee.Maps.Add((c.X, c.Y));

                var rows = new List<(string content, float w)>();
                foreach (var (text, _) in e.Rumors)
                    if (Settings.Expeditions.RumorWeights.TryGetValue(text, out var r) && !string.IsNullOrEmpty(r.Content))
                        rows.Add((r.Content, r.Weight));
                rows.Sort((a, b) => b.w.CompareTo(a.w));
                ee.TopWeight = rows.Count > 0 ? rows[0].w : 0f;
                var seen = new HashSet<string>();
                foreach (var (content, _) in rows)
                    if (seen.Add(content)) ee.Rumors.Add(content);
                list.Add(ee);
            }
        }
        catch (Exception e) { LogError("Atlas export: expedition snapshot failed: " + e.Message); }
        return list;
    }

    // ---- Stats ----

    private struct ExportStats
    {
        public int Total, Unlocked, Locked, Run, Completed, Failed, Favorites, Waypoints;
        public int AtlasPointsTotal, Quests;
        public List<(string type, int total, int run, int completed, int locked)> ByType;
        public List<(string name, int count, int colorArgb)> Content;
        public List<(string name, int count)> Biomes;
        public List<(string type, int count)> AtlasPointsByType;
        public float CompletionPct => Total > 0 ? Completed * 100f / Total : 0f;
        public float RunPct => Total > 0 ? Run * 100f / Total : 0f;
    }

    private static ExportStats ComputeExportStats(List<ExportNode> nodes, int waypointCount)
    {
        var s = new ExportStats { Total = nodes.Count, Waypoints = waypointCount };
        var byType = new Dictionary<string, (int total, int run, int completed, int locked)>();
        var content = new Dictionary<string, (int count, int color)>();
        var biomes = new Dictionary<string, int>();
        var apByType = new Dictionary<string, int>();

        foreach (var n in nodes)
        {
            if (n.IsUnlocked) s.Unlocked++; else s.Locked++;
            if (n.IsVisited) s.Run++;
            if (n.IsCompleted) s.Completed++;
            if (n.IsFailed) s.Failed++;
            if (n.IsFavorited) s.Favorites++;
            if (n.HasAtlasQuest) s.Quests++;
            if (n.GivesAtlasPoint)
            {
                s.AtlasPointsTotal++;
                var key = string.IsNullOrEmpty(n.AtlasPointType) ? "Generic" : n.AtlasPointType;
                apByType[key] = apByType.GetValueOrDefault(key) + 1;
            }

            var t = string.IsNullOrEmpty(n.MapType) ? "Unknown" : n.MapType;
            var cur = byType.GetValueOrDefault(t);
            cur.total++;
            if (n.IsVisited) cur.run++;
            if (n.IsCompleted) cur.completed++;
            if (!n.IsUnlocked) cur.locked++;
            byType[t] = cur;

            foreach (var (cname, ccolor) in n.Content)
            {
                var cc = content.GetValueOrDefault(cname);
                content[cname] = (cc.count + 1, cc.count == 0 ? ccolor : cc.color);
            }
            foreach (var b in n.Biomes)
                biomes[b] = biomes.GetValueOrDefault(b) + 1;
        }

        s.ByType = byType.Select(kv => (kv.Key, kv.Value.total, kv.Value.run, kv.Value.completed, kv.Value.locked))
                         .OrderByDescending(x => x.total).ToList();
        s.Content = content.Select(kv => (kv.Key, kv.Value.count, kv.Value.color))
                           .OrderByDescending(x => x.count).ToList();
        s.Biomes = biomes.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Item2).ToList();
        s.AtlasPointsByType = apByType.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Item2).ToList();
        return s;
    }

    // ---- Formatting helpers ----

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    private static string Hex(int argb) => Hex(Color.FromArgb(argb));
    private static float Alpha(int argb) => Color.FromArgb(argb).A / 255f;

    private static string H(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&#39;");
    }

    private static string StateName(ExportNode n)
    {
        if (n.IsCompleted) return "Completed";
        if (n.IsFailed) return "Failed";
        if (n.IsVisited) return "Run";
        if (n.IsUnlocked) return "Unlocked";
        return "Locked";
    }

    private static Color StateColor(ExportNode n)
    {
        if (n.IsCompleted) return Color.FromArgb(80, 200, 120);
        if (n.IsFailed) return Color.FromArgb(220, 90, 90);
        if (n.IsVisited) return Color.FromArgb(90, 150, 230);
        if (n.IsUnlocked) return Color.FromArgb(220, 200, 90);
        return Color.FromArgb(120, 120, 130);
    }

    // ---- HTML / SVG assembly ----

    private string BuildAtlasHtml(
        List<ExportNode> nodes, List<ExportWaypoint> waypoints, List<ExportTour> tours,
        List<ExportExpedition> expeditions,
        float minW, float maxW, Color bad, Color good,
        Color visitedLine, Color unlockedLine, Color lockedLine, float nodeRadiusSetting)
    {
        var stats = ComputeExportStats(nodes, waypoints.Count);
        string svg = nodes.Count == 0
            ? "<p class='empty'>No atlas nodes were cached. Open the Atlas in-game and export again.</p>"
            : BuildSvg(nodes, waypoints, tours, expeditions, minW, maxW, bad, good, visitedLine, unlockedLine, lockedLine, nodeRadiusSetting);

        string embeddedJson = BuildEmbeddedJson(nodes, stats);

        var sb = new StringBuilder(1 << 20);
        sb.Append("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'>");
        sb.Append("<meta name='viewport' content='width=device-width, initial-scale=1'>");
        sb.Append("<title>ExileMaps Atlas Export</title>");
        AppendStyle(sb);
        // Baked-in default look (JS reconciles with localStorage on load): celestial backdrop + star icons.
        sb.Append("</head><body class='celestial markers-icons'>");

        // Inline the star-glyph symbol sheets once (referenced by each node's <use> markers).
        sb.Append(NodeIconsSvg).Append(NodeIconsGreySvg);
        sb.Append(NodeIconsSpecialSvg).Append(NodeIconsSpecialGreySvg);

        // Header
        sb.Append("<header><h1>ExileMaps Atlas</h1>");
        sb.Append($"<span class='ts'>Exported {H(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))}</span></header>");

        sb.Append("<div class='layout'>");

        // Sidebar: stats dashboard
        sb.Append("<aside class='stats'>");
        AppendStatsHtml(sb, stats, expeditions);
        sb.Append("</aside>");

        // Main: controls + map
        sb.Append("<main>");
        AppendControls(sb);
        sb.Append("<div id='mapwrap'>");
        sb.Append(svg);
        sb.Append("</div></main>");

        // Detail panel
        sb.Append("<aside id='detail' class='detail hidden'><button id='detailclose'>&times;</button><div id='detailbody'></div></aside>");

        sb.Append("</div>"); // layout

        // Embedded raw data (split closing tag so the JSON can't terminate the script element)
        sb.Append("<script type='application/json' id='atlas-data'>").Append(embeddedJson).Append("</script>");

        // svg-pan-zoom from CDN, pinned + SRI-protected
        sb.Append("<script src='https://cdn.jsdelivr.net/npm/svg-pan-zoom@3.6.1/dist/svg-pan-zoom.min.js' ");
        sb.Append("integrity='sha384-yc/c2Lk1s2V2ir1rxvjo8YyVD9PlOlYTqpNr3Wm1WIuAA30GlDYNx6U5104OiavY' ");
        sb.Append("crossorigin='anonymous'></script>");

        // Tile data URLs as JS globals. Browser rasterizes each SVG tile to a bitmap on first use and caches it.
        // applyBg() sets mapwrap.style.backgroundImage to one of these; no per-frame updates needed (static background).
        sb.Append("<script>var _TILE_URLS={");
        sb.Append($"deepfield:'{SvgDataUrl(TileDeepField)}'");
        sb.Append($",violet:'{SvgDataUrl(TileViolet)}'");
        sb.Append($",verdant:'{SvgDataUrl(TileVerdant)}'");
        sb.Append($",ember:'{SvgDataUrl(TileEmber)}'");
        sb.Append("};</script>");

        AppendScript(sb);

        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static string BuildEmbeddedJson(List<ExportNode> nodes, ExportStats stats)
    {
        var obj = new
        {
            exportedAt = DateTime.Now.ToString("u"),
            stats = new
            {
                stats.Total, stats.Unlocked, stats.Locked, stats.Run, stats.Completed,
                stats.Failed, stats.Favorites, stats.Waypoints,
                stats.AtlasPointsTotal, stats.Quests,
                completionPct = stats.CompletionPct, runPct = stats.RunPct,
            },
            nodes = nodes.Select(n => new
            {
                n.Name, type = n.MapType, n.X, n.Y, weight = n.Weight,
                state = StateName(n),
                content = n.Content.Select(c => c.name).ToArray(),
                biomes = n.Biomes.ToArray(),
                atlasPoint = n.GivesAtlasPoint,
                quest = n.HasAtlasQuest,
            }),
        };
        var json = JsonConvert.SerializeObject(obj);
        // Defensive: prevent any "</script>" inside string data from closing the host element early.
        return json.Replace("</", "<\\/");
    }

    private string BuildSvg(
        List<ExportNode> nodes, List<ExportWaypoint> waypoints, List<ExportTour> tours,
        List<ExportExpedition> expeditions,
        float minW, float maxW, Color bad, Color good,
        Color visitedLine, Color unlockedLine, Color lockedLine, float nodeRadiusSetting)
    {
        // Isometric diamond layout: matches in-game atlas orientation.
        // a = x+y runs left(SW)→right(NE); b = x-y runs top(NW)→bottom(SE).
        // Large game-Y (north) → smaller b → closer to top of SVG.
        int minA = nodes.Min(n => n.X + n.Y), maxA = nodes.Max(n => n.X + n.Y);
        int minB = nodes.Min(n => n.X - n.Y), maxB = nodes.Max(n => n.X - n.Y);

        // 90° CCW from the prior NE-up layout → NW now faces up-left, matching in-game orientation.
        // Old a-axis (x+y, ran SW→NE) now runs top→bottom; old b-axis (x-y, ran NW→SE) now runs left→right.
        float Sx(int x, int y) => (x - y - minB) * ExportSpacing + ExportPad;
        float Sy(int x, int y) => (maxA - (x + y)) * ExportSpacing + ExportPad;
        float width  = (maxB - minB) * ExportSpacing + 2 * ExportPad;
        float height = (maxA - minA) * ExportSpacing + 2 * ExportPad;

        float baseR = 6f + 4f * Math.Clamp(nodeRadiusSetting <= 0 ? 1f : nodeRadiusSetting, 0.3f, 3f);

        Color WeightColor(float w)
        {
            float denom = maxW - minW;
            float frac = denom > 0 ? (w - minW) / denom : 0.5f;
            return ColorUtils.InterpolateColor(bad, good, Math.Clamp(frac, 0f, 1f));
        }

        var byCoord = new Dictionary<(int, int), ExportNode>();
        foreach (var n in nodes) byCoord[(n.X, n.Y)] = n;

        var waypointByCoord = new Dictionary<(int, int), ExportWaypoint>();
        foreach (var w in waypoints) waypointByCoord[(w.X, w.Y)] = w;

        // map coord -> expedition id, so each node can carry a data-exp tag the sidebar filter keys off.
        var expByCoord = new Dictionary<(int, int), int>();
        foreach (var e in expeditions)
            foreach (var m in e.Maps) expByCoord[m] = e.Id;

        var sb = new StringBuilder(1 << 20);
        sb.Append($"<svg id='atlas-svg' xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {F(width)} {F(height)}'>");

        // --- Connections (drawn first, under nodes) ---
        sb.Append("<g class='connections'>");
        // Coordinate -> stable id, so each undirected edge is drawn once.
        static long IdOf(int x, int y) => ((long)(x + 1_000_000) << 21) | (uint)(y + 1_000_000);
        var drawn = new HashSet<(long, long)>();
        foreach (var n in nodes)
        {
            foreach (var (nx, ny) in n.Neighbors)
            {
                if (!byCoord.TryGetValue((nx, ny), out var other)) continue;
                long ia = IdOf(n.X, n.Y), ib = IdOf(nx, ny);
                if (!drawn.Add((Math.Min(ia, ib), Math.Max(ia, ib)))) continue;

                Color lc = (n.IsVisited && other.IsVisited) ? visitedLine
                         : (n.IsUnlocked || other.IsUnlocked) ? unlockedLine
                         : lockedLine;
                sb.Append($"<line x1='{F(Sx(n.X, n.Y))}' y1='{F(Sy(n.X, n.Y))}' x2='{F(Sx(nx, ny))}' y2='{F(Sy(nx, ny))}' ");
                sb.Append($"stroke='{Hex(lc)}' stroke-opacity='{F(lc.A / 255f)}' stroke-width='2'/>");
            }
        }
        sb.Append("</g>");

        // --- Tour routes ---
        sb.Append("<g class='tours'>");
        foreach (var tour in tours)
        {
            if (tour.Stops.Count < 2) continue;
            var pts = string.Join(" ", tour.Stops.Select(s => $"{F(Sx(s.x, s.y))},{F(Sy(s.x, s.y))}"));
            sb.Append($"<polyline points='{pts}' fill='none' stroke='{Hex(tour.ColorArgb)}' stroke-width='3' stroke-opacity='0.85' stroke-dasharray='6 5'/>");
        }
        sb.Append("</g>");

        // --- Expeditions (tinted rings on the region's maps + a marker at the spawn) ---
        sb.Append("<g class='expeditions'>");
        foreach (var e in expeditions)
        {
            string tint = Hex(e.ColorArgb);
            string title = H($"Expedition #{e.Id}" + (e.Rumors.Count > 0 ? "\n" + string.Join(", ", e.Rumors) : ""));
            foreach (var (mx, my) in e.Maps)
                sb.Append($"<circle cx='{F(Sx(mx, my))}' cy='{F(Sy(mx, my))}' r='{F(baseR * 3f)}' fill='none' stroke='{tint}' stroke-width='2' stroke-opacity='0.7'><title>{title}</title></circle>");
            sb.Append($"<circle cx='{F(Sx(e.Spawn.x, e.Spawn.y))}' cy='{F(Sy(e.Spawn.x, e.Spawn.y))}' r='{F(baseR * 1.6f)}' fill='{tint}' fill-opacity='0.85' stroke='#000' stroke-width='1'><title>{title}</title></circle>");
        }
        sb.Append("</g>");

        // --- Nodes ---
        sb.Append("<g class='nodes'>");
        int idx = 0;
        foreach (var n in nodes)
        {
            float cx = Sx(n.X, n.Y), cy = Sy(n.X, n.Y);
            Color wc = WeightColor(n.Weight);
            Color sc = StateColor(n);
            string cls = "node " + StateName(n).ToLowerInvariant();
            if (n.IsFavorited) cls += " favorite";
            if (n.GivesAtlasPoint) cls += " atlaspoint";
            if (n.HasAtlasQuest) cls += " quest";

            bool isSpecial = n.IsSpecial;
            if (isSpecial) cls += " special";
            bool isLandmark = n.SpecialId == "ziggurat-refuge";
            if (isLandmark) cls += " landmark";
            string contentAttr = H(string.Join(" ", n.Content.Select(c => c.name)).ToLowerInvariant());
            string biomeAttr = H(string.Join(", ", n.Biomes));

            // style color drives the grey-icon marker (currentColor); defaults to the weight color.
            sb.Append($"<g id='n{idx}' class='{cls}' transform='translate({F(cx)},{F(cy)})' style='color:{Hex(wc)}' ");
            if (isLandmark) sb.Append("data-home='1' ");
            sb.Append($"data-name='{H(n.Name)}' data-type='{H(n.MapType)}' data-weight='{F(n.Weight)}' ");
            sb.Append($"data-state='{StateName(n)}' data-content='{contentAttr}' data-biomes='{biomeAttr}' ");
            sb.Append($"data-coord='{n.X},{n.Y}' data-atlas='{(n.GivesAtlasPoint ? 1 : 0)}' data-quest='{(n.HasAtlasQuest ? 1 : 0)}' data-waypoint='{(n.IsWaypoint ? 1 : 0)}' ");
            // comma-wrapped so the substring filter (",3," in data-exp) can't match ",13,"
            if (expByCoord.TryGetValue((n.X, n.Y), out var expId)) sb.Append($"data-exp=',{expId},' ");
            sb.Append($"data-cw='{Hex(wc)}' data-cm='{Hex(n.NodeColorArgb)}' data-cs='{Hex(sc)}'>");

            // Node size: r = baseR for all nodes. CSS --scale-special / --scale-nodes drives visual size.
            float r = baseR;

            sb.Append("<g class='ni'>");

            // Content rings (stacked outward from node radius)
            float ringR = r + 2f;
            foreach (var (cname, ccolor) in n.Content)
            {
                sb.Append($"<circle r='{F(ringR)}' fill='none' stroke='{Hex(ccolor)}' stroke-width='2.5' stroke-opacity='{F(Alpha(ccolor))}'/>");
                ringR += 3.5f;
            }

            // Atlas-point halo
            if (n.GivesAtlasPoint)
                sb.Append($"<circle r='{F(ringR + 1.5f)}' fill='none' stroke='#ffd860' stroke-width='1.5' stroke-dasharray='2 2'/>");

            // Node body (default colored by weight; JS color-by switch swaps the fill)
            sb.Append($"<circle class='body' r='{F(r)}' fill='{Hex(wc)}'/>");

            // Quest marker
            if (n.HasAtlasQuest)
                sb.Append($"<rect x='{F(-r * 0.4f)}' y='{F(-r * 0.4f)}' width='{F(r * 0.8f)}' height='{F(r * 0.8f)}' fill='#ffffff' opacity='0.9'/>");

            // Star-glyph markers (hidden by default; shown when body has markers-icons / markers-grey).
            // Generated here, not JS-injected, so size tracks baseR exactly and no per-node DOM work runs client-side.
            // Special maps use their own dedicated icons (larger) instead of the generic state icons.
            // Size-detected specials without a dedicated icon fall back to the generic special star.
            string gstate = n.IsWaypoint ? "waypoint" : StateName(n).ToLowerInvariant();
            string sslug = n.SpecialId != "" ? n.SpecialId : "generic";
            float gh = baseR * 1.8f;
            string glyphId     = isSpecial ? $"star-special-{sslug}"      : $"star-{gstate}";
            string glyphGreyId = isSpecial ? $"star-special-grey-{sslug}" : $"star-grey-{gstate}";
            sb.Append($"<use class='glyph' href='#{glyphId}' x='{F(-gh)}' y='{F(-gh)}' width='{F(2 * gh)}' height='{F(2 * gh)}' pointer-events='none'/>");
            sb.Append($"<use class='glyph-grey' href='#{glyphGreyId}' x='{F(-gh)}' y='{F(-gh)}' width='{F(2 * gh)}' height='{F(2 * gh)}' pointer-events='none'/>");

            // Waypoint indicator: small downward-pointing triangle above the node, inside .ni so it scales with the node.
            if (n.IsWaypoint && waypointByCoord.TryGetValue((n.X, n.Y), out var wpData))
            {
                float tipY  = -(r + 4f);
                float baseY = tipY - 9f;
                sb.Append($"<path d='M0,{F(tipY)} L5,{F(baseY)} L-5,{F(baseY)} Z' fill='{Hex(wpData.ColorArgb)}' stroke='#0008' stroke-width='0.8' pointer-events='none'/>");
            }

            sb.Append("</g>");

            // Native hover tooltip (outside .ni so it doesn't scale)
            string tip = $"{n.Name} ({StateName(n)})\nType: {n.MapType}  Weight: {F(n.Weight)}";
            if (n.Content.Count > 0) tip += "\nContent: " + string.Join(", ", n.Content.Select(c => c.name));
            if (n.Biomes.Count > 0) tip += "\nBiomes: " + string.Join(", ", n.Biomes);
            sb.Append($"<title>{H(tip)}</title>");

            sb.Append("</g>");
            idx++;
        }
        sb.Append("</g>");

        // --- Labels (hidden by default) ---
        // Special-map labels carry class='special' so CSS lifts them by the special-icon offset;
        // normal labels use the normal-icon offset. Both offsets track their slider in JS.
        sb.Append("<g class='labels'>");
        foreach (var n in nodes)
        {
            bool sp = n.IsSpecial;
            float lr = sp ? baseR * 2.5f : baseR;
            string visited = (n.IsVisited || n.IsCompleted) ? "1" : "0";
            string locked = !n.IsUnlocked ? "1" : "0";
            string lcls = sp ? " class='special'" : "";
            sb.Append($"<text{lcls} data-visited='{visited}' data-locked='{locked}' x='{F(Sx(n.X, n.Y))}' y='{F(Sy(n.X, n.Y) - lr - 4f)}' text-anchor='middle'>{H(n.Name)}</text>");
        }
        sb.Append("</g>");

        // --- Waypoints ---
        // Atlas-node waypoints render their indicator inside the .ni group (so it scales with the node).
        // Only orphan waypoints (no matching atlas node) are rendered here as standalone triangles.
        sb.Append("<g class='waypoints'>");
        foreach (var w in waypoints)
        {
            if (byCoord.ContainsKey((w.X, w.Y))) continue;
            float cx = Sx(w.X, w.Y), cy = Sy(w.X, w.Y);
            sb.Append($"<g transform='translate({F(cx)},{F(cy)})'>");
            sb.Append($"<path d='M0,-{F(baseR + 6)} L{F(baseR)},{F(baseR)} L-{F(baseR)},{F(baseR)} Z' fill='{Hex(w.ColorArgb)}' stroke='#000' stroke-width='1'/>");
            sb.Append($"<title>{H("Waypoint: " + w.Name)}</title></g>");
        }
        sb.Append("</g>");

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static void AppendStatsHtml(StringBuilder sb, ExportStats s, List<ExportExpedition> expeditions)
    {
        // Summary cards
        sb.Append("<div class='cards'>");
        void Card(string label, string value) => sb.Append($"<div class='card'><div class='v'>{H(value)}</div><div class='l'>{H(label)}</div></div>");
        Card("Total nodes", s.Total.ToString());
        Card("Completed", $"{s.Completed} ({F(s.CompletionPct)}%)");
        Card("Run", $"{s.Run} ({F(s.RunPct)}%)");
        Card("Unlocked", s.Unlocked.ToString());
        Card("Locked", s.Locked.ToString());
        Card("Failed", s.Failed.ToString());
        Card("Favorites", s.Favorites.ToString());
        Card("Waypoints", s.Waypoints.ToString());
        Card("Atlas points", s.AtlasPointsTotal.ToString());
        Card("Atlas quests", s.Quests.ToString());
        sb.Append("</div>");

        // Per-map-type table (clicking a row highlights matching nodes on the map)
        sb.Append("<h2>Maps by type</h2>");
        sb.Append("<table class='sortable'><thead><tr><th>Map</th><th>Total</th><th>Run</th><th>Done</th><th>Locked</th></tr></thead><tbody>");
        foreach (var (type, total, run, completed, locked) in s.ByType)
            sb.Append($"<tr class='clickrow' data-filter='type' data-fval='{H(type.ToLowerInvariant())}'><td>{H(type)}</td><td>{total}</td><td>{run}</td><td>{completed}</td><td>{locked}</td></tr>");
        sb.Append("</tbody></table>");

        // Content tally (clickable)
        sb.Append("<h2>Content</h2>");
        sb.Append("<table class='sortable'><thead><tr><th></th><th>Content</th><th>Count</th></tr></thead><tbody>");
        foreach (var (name, count, colorArgb) in s.Content)
            sb.Append($"<tr class='clickrow' data-filter='content' data-fval='{H(name.ToLowerInvariant())}'><td><span class='sw' style='background:{Hex(colorArgb)}'></span></td><td>{H(name)}</td><td>{count}</td></tr>");
        sb.Append("</tbody></table>");

        // Atlas points by type
        if (s.AtlasPointsByType.Count > 0)
        {
            sb.Append("<h2>Atlas points (available)</h2>");
            sb.Append("<table class='sortable'><thead><tr><th>Type</th><th>Count</th></tr></thead><tbody>");
            foreach (var (type, count) in s.AtlasPointsByType)
                sb.Append($"<tr><td>{H(type)}</td><td>{count}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // Expeditions (clicking a row highlights that expedition's maps; needs the map overlay toggle on to see rings)
        if (expeditions.Count > 0)
        {
            sb.Append("<h2>Expeditions</h2>");
            sb.Append("<table class='sortable'><thead><tr><th>#</th><th>Content</th><th>Maps</th></tr></thead><tbody>");
            foreach (var e in expeditions.OrderByDescending(x => x.TopWeight))
            {
                string content = e.Rumors.Count > 0 ? string.Join(", ", e.Rumors.Take(3)) : "(unknown)";
                sb.Append($"<tr class='clickrow' data-filter='exp' data-fval=',{e.Id},'><td><span class='sw' style='background:{Hex(e.ColorArgb)}'></span>{e.Id}</td><td>{H(content)}</td><td>{e.Maps.Count}</td></tr>");
            }
            sb.Append("</tbody></table>");
        }

        // Biomes (clickable)
        if (s.Biomes.Count > 0)
        {
            sb.Append("<h2>Biomes</h2>");
            sb.Append("<table class='sortable'><thead><tr><th>Biome</th><th>Count</th></tr></thead><tbody>");
            foreach (var (name, count) in s.Biomes)
                sb.Append($"<tr class='clickrow' data-filter='biomes' data-fval='{H(name.ToLowerInvariant())}'><td>{H(name)}</td><td>{count}</td></tr>");
            sb.Append("</tbody></table>");
        }
    }

    private static void AppendControls(StringBuilder sb)
    {
        sb.Append("<div class='controls'>");
        sb.Append("<button id='btn-home' title='Pan to Ziggurat Refuge'>⌂ Home</button>");
        sb.Append("<input id='search' type='search' placeholder='Search nodes (name or content)…'>");
        sb.Append("<label>Color by <select id='colorby'><option value='cw'>Weight</option><option value='cm'>Map type</option><option value='cs'>State</option></select></label>");
        sb.Append("<label>Background <select id='bgsel'><option value='deepfield'>Deep Field</option><option value='violet'>Violet Veil</option><option value='verdant'>Verdant Aurora</option><option value='ember'>Emberdust</option><option value='off'>Off</option></select></label>");
        sb.Append("<label>Markers <select id='mksel'><option value='dots'>Dots</option><option value='icons'>Star icons</option><option value='grey' selected>Weight icons</option></select></label>");
        sb.Append("<label><input type='checkbox' id='t-glow'> Glow</label>");
        sb.Append("<span class='sliders'>");
        sb.Append("<label>Icons <input type='range' id='sl-node' min='0.5' max='3' step='0.1' value='3'> <span id='sl-node-v'>3.0×</span></label>");
        sb.Append("<label>Special <input type='range' id='sl-special' min='0.5' max='6' step='0.1' value='5.5'> <span id='sl-special-v'>5.5×</span></label>");
        sb.Append("<label>Labels <input type='range' id='sl-label' min='0.5' max='4' step='0.1' value='4'> <span id='sl-label-v'>4.0×</span></label>");
        sb.Append("</span>");
        sb.Append("<span class='toggles'>");
        void Tog(string id, string label, bool on) =>
            sb.Append($"<label><input type='checkbox' id='{id}'{(on ? " checked" : "")}> {H(label)}</label>");
        Tog("t-connections", "Connections", true);
        Tog("t-rings", "Content rings", true);
        Tog("t-labels", "Labels", true);
        Tog("t-waypoints", "Waypoints", false);
        Tog("t-tours", "Tours", false);
        Tog("t-expeditions", "Expeditions", false);
        Tog("t-visited", "Visited", true);
        Tog("t-locked", "Locked", true);
        sb.Append("</span>");
        sb.Append("<span class='legend'>");
        void Leg(string color, string label) => sb.Append($"<span class='li'><span class='sw' style='background:{color}'></span>{H(label)}</span>");
        Leg("#50c878", "Completed"); Leg("#5a96e6", "Run"); Leg("#dcc85a", "Unlocked"); Leg("#787882", "Locked"); Leg("#dc5a5a", "Failed");
        sb.Append("</span>");
        sb.Append("</div>");
    }

    private static void AppendStyle(StringBuilder sb)
    {
        sb.Append("<style>");
        sb.Append(@"
:root{--bg:#0e0f13;--panel:#171922;--panel2:#1f2230;--text:#d9dbe4;--muted:#8a8f9e;--accent:#ffd860;--border:#2a2e3c;}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 'Segoe UI',system-ui,sans-serif}
header{display:flex;align-items:baseline;gap:14px;padding:10px 16px;background:var(--panel);border-bottom:1px solid var(--border)}
header h1{margin:0;font-size:18px;color:var(--accent)}
header .ts{color:var(--muted);font-size:12px}
.layout{display:flex;height:calc(100vh - 49px)}
.stats{width:320px;min-width:280px;overflow:auto;padding:12px;background:var(--panel);border-right:1px solid var(--border)}
.stats h2{font-size:13px;text-transform:uppercase;letter-spacing:.05em;color:var(--muted);margin:18px 0 6px}
.cards{display:grid;grid-template-columns:1fr 1fr;gap:8px}
.card{background:var(--panel2);border:1px solid var(--border);border-radius:6px;padding:8px}
.card .v{font-size:18px;font-weight:600;color:#fff}
.card .l{font-size:11px;color:var(--muted)}
table{width:100%;border-collapse:collapse;font-size:12px}
th,td{text-align:left;padding:3px 6px;border-bottom:1px solid var(--border)}
th{cursor:pointer;color:var(--muted);user-select:none}
th:hover{color:var(--text)}
td:nth-child(n+2),th:nth-child(n+2){text-align:right}
.sw{display:inline-block;width:12px;height:12px;border-radius:3px;vertical-align:middle;margin-right:4px;border:1px solid #0006}
main{flex:1;display:flex;flex-direction:column;min-width:0}
.controls{display:flex;flex-wrap:wrap;align-items:center;gap:10px 16px;padding:8px 12px;background:var(--panel);border-bottom:1px solid var(--border)}
.controls input[type=search]{background:var(--panel2);border:1px solid var(--border);color:var(--text);border-radius:5px;padding:5px 8px;min-width:220px}
.controls select{background:var(--panel2);border:1px solid var(--border);color:var(--text);border-radius:5px;padding:4px}
.controls .toggles,.controls .legend,.controls .sliders{display:flex;flex-wrap:wrap;gap:6px 12px;font-size:12px;color:var(--muted)}
.controls .legend .li{display:flex;align-items:center}
#mapwrap{flex:1;min-height:0;background:radial-gradient(circle at 50% 40%,#15171f,#0a0b0e)}
#atlas-svg{width:100%;height:100%;display:block}
.connections line{transition:opacity .1s}
.labels text{fill:#cfd3df;font-size:9px;paint-order:stroke;stroke:#000;stroke-width:2px;pointer-events:none;transform:translateY(calc(-1 * var(--label-offset, 10px)))}
.labels text.special{transform:translateY(calc(-1 * var(--label-offset-special, 55px)))}
.node{cursor:pointer}
.node .body{stroke:#0008;stroke-width:1}
.node:hover .body{stroke:#fff;stroke-width:2}
.node.locked{opacity:.45}
/* Visited maps (run + completed): dim icon+name and desaturate (drop weight coloring). Special maps excluded. */
.node.run:not(.special),.node.completed:not(.special){opacity:.6}
.node.run:not(.special) .ni,.node.completed:not(.special) .ni{filter:saturate(0)}
.labels text[data-visited='1']:not(.special){opacity:.6}
.node.favorite .body{stroke:var(--accent);stroke-width:2}
/* Selected node: pulsing cyan halo, full visibility (works in every marker mode).
   Animation supplies `filter`, overriding any visited saturate(0) without needing !important. */
.node.selected{opacity:1 !important}
.node.selected .ni{animation:selPulse 1.2s ease-in-out infinite}
.node.selected .body{stroke:#19e9ff;stroke-width:2.5}
@keyframes selPulse{0%,100%{filter:drop-shadow(0 0 3px #19e9ff) drop-shadow(0 0 7px #19e9ff)}50%{filter:drop-shadow(0 0 6px #19e9ff) drop-shadow(0 0 16px #19e9ff)}}
/* layer + state toggles flip these via body classes */
.hide-connections .connections,.hide-rings .node circle:not(.body):not([stroke-dasharray]),
.hide-labels .labels,.hide-waypoints .waypoints,.hide-tours .tours,.hide-expeditions .expeditions,
.hide-visited .node.run,.hide-visited .node.completed,.hide-locked .node.locked,
.hide-visited .labels text[data-visited='1'],.hide-locked .labels text[data-locked='1']{display:none}
.searching .node:not(.match){opacity:.08}
.searching .node.match{opacity:1}
.searching .node.match .body{stroke:var(--accent);stroke-width:2.5}
/* Matches stay fully visible even when locked / Locked-layer hidden (completed are excluded in JS) */
body.searching.hide-locked .node.match.locked,body.filter-active.hide-locked .node.filter-match.locked{display:block}
.detail{width:300px;min-width:260px;background:var(--panel);border-left:1px solid var(--border);padding:14px;overflow:auto;position:relative}
.detail.hidden{display:none}
.detail h3{margin:0 0 8px;color:var(--accent)}
.detail .row{display:flex;justify-content:space-between;gap:8px;padding:3px 0;border-bottom:1px solid var(--border);font-size:13px}
.detail .row span:first-child{color:var(--muted)}
#detailclose{position:absolute;top:8px;right:10px;background:none;border:none;color:var(--muted);font-size:20px;cursor:pointer}
.empty{color:var(--muted);padding:40px;text-align:center}
/* --- Celestial backdrop: static CSS background on #mapwrap; JS just swaps backgroundImage --- */
#mapwrap{position:relative;background-repeat:repeat;background-size:640px}
body.celestial #mapwrap{background-color:#04060d}
/* Stroke glow instead of CSS filter; zero per-node rasterization cost, visually equivalent */
body.celestial .node:not(.locked) .body{stroke:rgba(140,180,255,.5);stroke-width:8}
body.celestial .connections line{stroke:rgba(150,180,228,.30)}
body.celestial .labels text{fill:#dde6f8}
/* --- Marker modes: dots (default) / star icons / weight (grey) icons --- */
.node{color:var(--weight-color,#3fbf5f)}
.node .glyph,.node .glyph-grey{display:none}
body.markers-icons .node .glyph{display:block}
body.markers-icons .node .ni>circle:not(.body),body.markers-icons .node .ni>rect{display:none}
body.markers-icons .node .body{fill:transparent}
body.markers-grey .node .glyph-grey{display:block}
body.markers-grey .node .ni>circle:not(.body),body.markers-grey .node .ni>rect{display:none}
body.markers-grey .node .body{fill:transparent}
/* --- Node scale (CSS variables, driven by sliders) --- */
:root{--scale-nodes:3;--scale-special:5.5;--scale-labels:4;--label-offset:34px;--label-offset-special:59px}
.ni{transform-box:fill-box;transform-origin:50% 50%}
.node:not(.special) .ni{transform:scale(var(--scale-nodes))}
.node.special .ni{transform:scale(var(--scale-special))}
.node.landmark .ni{transform:scale(calc(var(--scale-special) * 1.5))}
.labels text{font-size:calc(9px * var(--scale-labels))}
.controls input[type=range]{width:80px;vertical-align:middle;cursor:pointer;accent-color:var(--accent)}
/* --- Home button --- */
#btn-home{background:var(--panel2);border:1px solid var(--border);color:var(--accent);border-radius:5px;padding:4px 10px;cursor:pointer;font-size:13px;white-space:nowrap}
#btn-home:hover{background:var(--border)}
/* --- Clickable stat rows --- */
tr.clickrow{cursor:pointer;transition:background .12s}
tr.clickrow:hover td{background:var(--panel2)}
tr.clickrow.active-filter td{background:#ffd86018;color:var(--accent)}
/* --- Filter highlight --- */
body.filter-active .node:not(.filter-match){opacity:.07}
body.filter-active .node.filter-match{opacity:1}
body.filter-active .node.filter-match .body{stroke:var(--accent) !important;stroke-width:3 !important}
");
        sb.Append("</style>");
    }

    private static void AppendScript(StringBuilder sb)
    {
        sb.Append("<script>");
        sb.Append(@"
(function(){
  var spz = null;
  var svgEl = document.getElementById('atlas-svg');
  if (svgEl && window.svgPanZoom) {
    spz = svgPanZoom(svgEl, {zoomEnabled:true,controlIconsEnabled:true,fit:true,center:true,
      minZoom:0.2,maxZoom:40,zoomScaleSensitivity:0.4});
  }
  var body = document.body;
  // Cache node list once to avoid repeated querySelectorAll in color-by, search, etc.
  var allNodes = Array.prototype.slice.call(document.querySelectorAll('.node'));
  // Layer + state toggles
  var map = {'t-connections':'hide-connections','t-rings':'hide-rings','t-labels':'hide-labels',
             't-waypoints':'hide-waypoints','t-tours':'hide-tours','t-expeditions':'hide-expeditions',
             't-visited':'hide-visited','t-locked':'hide-locked'};
  Object.keys(map).forEach(function(id){
    var el = document.getElementById(id); if(!el) return;
    function apply(){ body.classList.toggle(map[id], !el.checked); }
    el.addEventListener('change', apply); apply();
  });
  // Color-by (uses cached list)
  var colorby = document.getElementById('colorby');
  function applyColor(){
    var attr = 'data-' + colorby.value;
    allNodes.forEach(function(n){
      var c = n.getAttribute(attr); if(!c) return;
      var b = n.querySelector('.body'); if(b) b.setAttribute('fill', c);
      n.style.color = c; // grey-icon markers inherit this via currentColor
    });
  }
  if(colorby) colorby.addEventListener('change', applyColor);
  // Search: debounced, uses cached list
  var search = document.getElementById('search'), searchTimer;
  if(search) search.addEventListener('input', function(){
    clearTimeout(searchTimer);
    searchTimer = setTimeout(function(){
      var q = search.value.trim().toLowerCase();
      if(!q){ body.classList.remove('searching'); allNodes.forEach(function(n){n.classList.remove('match');}); return; }
      body.classList.add('searching');
      allNodes.forEach(function(n){
        var name = (n.getAttribute('data-name')||'').toLowerCase();
        var content = (n.getAttribute('data-content')||'');
        var hit = (name.indexOf(q)>=0 || content.indexOf(q)>=0) && !n.classList.contains('completed');
        n.classList.toggle('match', hit);
      });
    }, 100);
  });
  // Node detail panel: single delegated listener instead of one per node (2,000+)
  var detail = document.getElementById('detail'), dbody = document.getElementById('detailbody');
  function row(k,v){ return v?'<div class=row><span>'+k+'</span><span>'+v+'</span></div>':''; }
  function clearSelected(){ var p=document.querySelector('.node.selected'); if(p) p.classList.remove('selected'); }
  if(svgEl) svgEl.addEventListener('click', function(e){
    var n = e.target && e.target.closest && e.target.closest('.node');
    if(!n){ return; }
    clearSelected(); n.classList.add('selected');
    if(n.parentNode) n.parentNode.appendChild(n); // raise above siblings so the halo isn't clipped
    var d = function(a){ return n.getAttribute('data-'+a)||''; };
    dbody.innerHTML = '<h3>'+(d('name')||'(unnamed)')+'</h3>'
      +row('Type',d('type'))+row('State',d('state'))+row('Weight',d('weight'))
      +row('Coord',d('coord'))
      +row('Content',(d('content')||'').replace(/\b\w/g,function(c){return c.toUpperCase();}))
      +row('Biomes',d('biomes'))
      +row('Atlas point',d('atlas')==='1'?'Yes':'')+row('Quest',d('quest')==='1'?'Yes':'');
    detail.classList.remove('hidden');
  });
  var dc = document.getElementById('detailclose');
  if(dc) dc.addEventListener('click', function(){ detail.classList.add('hidden'); clearSelected(); });
  // Sortable tables
  document.querySelectorAll('table.sortable').forEach(function(t){
    t.querySelectorAll('th').forEach(function(th, ci){
      th.addEventListener('click', function(){
        var tb = t.tBodies[0], rows = Array.prototype.slice.call(tb.rows);
        var asc = th._asc = !th._asc;
        rows.sort(function(a,b){
          var x=a.cells[ci].textContent.trim(), y=b.cells[ci].textContent.trim();
          var nx=parseFloat(x), ny=parseFloat(y);
          if(!isNaN(nx)&&!isNaN(ny)) return asc?nx-ny:ny-nx;
          return asc? x.localeCompare(y): y.localeCompare(x);
        });
        rows.forEach(function(r){ tb.appendChild(r); });
      });
    });
  });
  // --- Celestial backdrop + marker settings (persisted in localStorage) ---
  function lsGet(k,d){ try{ var v=localStorage.getItem('em_'+k); return v===null?d:v; }catch(e){ return d; } }
  function lsSet(k,v){ try{ localStorage.setItem('em_'+k,v); }catch(e){} }
  var mapwrap = document.getElementById('mapwrap');
  var bgSel = document.getElementById('bgsel');
  var mkSel = document.getElementById('mksel');
  var glow = document.getElementById('t-glow');
  function applyBg(v){
    if(!mapwrap) return;
    mapwrap.style.backgroundImage = (v==='off') ? 'none' : 'url('+_TILE_URLS[v]+')';
  }
  function applyMarkers(v){
    body.classList.remove('markers-icons','markers-grey');
    if(v==='icons') body.classList.add('markers-icons');
    else if(v==='grey') body.classList.add('markers-grey');
  }
  if(bgSel){ bgSel.value = lsGet('bg','deepfield'); applyBg(bgSel.value);
    bgSel.addEventListener('change', function(){ applyBg(bgSel.value); lsSet('bg', bgSel.value); }); }
  if(mkSel){ mkSel.value = lsGet('mk','grey'); applyMarkers(mkSel.value);
    mkSel.addEventListener('change', function(){ applyMarkers(mkSel.value); lsSet('mk', mkSel.value); }); }
  if(glow){ glow.checked = lsGet('glow','0')==='1'; body.classList.toggle('celestial', glow.checked);
    glow.addEventListener('change', function(){ body.classList.toggle('celestial', glow.checked); lsSet('glow', glow.checked?'1':'0'); }); }
  // --- Home button (zoom + center on Ziggurat Refuge) ---
  // svg-pan-zoom has no setViewBox; center by panBy-ing the node's screen position to the
  // container centre. Zoom first, then center on the next frame (bbox shifts after zoom).
  var btnHome = document.getElementById('btn-home');
  function centerHome(){
    var hn = document.querySelector('[data-home]');
    if(!hn || !spz) return;
    var nr = hn.getBoundingClientRect();
    var mr = (mapwrap || document.getElementById('mapwrap')).getBoundingClientRect();
    spz.panBy({
      x: (mr.left + mr.width/2) - (nr.left + nr.width/2),
      y: (mr.top + mr.height/2) - (nr.top + nr.height/2)
    });
  }
  if(btnHome) btnHome.addEventListener('click', function(){
    if(!spz) return;
    spz.zoom(3);
    requestAnimationFrame(centerHome);
  });
  // --- Stat row filter (click map type / content / biome row to highlight matching nodes) ---
  var statsEl = document.querySelector('.stats');
  if(statsEl) statsEl.addEventListener('click', function(e){
    var tr = e.target.closest('tr.clickrow');
    if(!tr) return;
    var wasActive = tr.classList.contains('active-filter');
    document.querySelectorAll('tr.active-filter').forEach(function(r){r.classList.remove('active-filter');});
    allNodes.forEach(function(n){n.classList.remove('filter-match');});
    body.classList.remove('filter-active');
    if(wasActive) return;
    tr.classList.add('active-filter');
    body.classList.add('filter-active');
    var attr = tr.dataset.filter, val = (tr.dataset.fval||'').toLowerCase();
    allNodes.forEach(function(n){
      var nodeVal = (n.getAttribute('data-'+attr)||'').toLowerCase();
      n.classList.toggle('filter-match', nodeVal.indexOf(val)>=0 && !n.classList.contains('completed'));
    });
  });
  // --- Scale sliders ---
  var root = document.documentElement;
  [
    {id:'sl-node',    cssVar:'--scale-nodes',   lsKey:'scn', def:'3'},
    {id:'sl-special', cssVar:'--scale-special',  lsKey:'scs', def:'5.5'},
    {id:'sl-label',   cssVar:'--scale-labels',   lsKey:'scl', def:'4'},
  ].forEach(function(s){
    var el=document.getElementById(s.id), vEl=document.getElementById(s.id+'-v');
    if(!el) return;
    var v=lsGet(s.lsKey, s.def);
    el.value=v; root.style.setProperty(s.cssVar, v);
    if(vEl) vEl.textContent=parseFloat(v).toFixed(1)+'×';
    el.addEventListener('input', function(){
      root.style.setProperty(s.cssVar, el.value);
      if(vEl) vEl.textContent=parseFloat(el.value).toFixed(1)+'×';
      // Recalculate label offsets when node/special scale changes. Normal labels track node
      // scale; special labels track special scale (labels move further up with larger icons).
      if(s.id==='sl-node' || s.id==='sl-special'){
        var ns = parseFloat(document.getElementById('sl-node').value) || 3;
        var ss = parseFloat(document.getElementById('sl-special').value) || 5.5;
        root.style.setProperty('--label-offset', (10 * ns + 4) + 'px');
        root.style.setProperty('--label-offset-special', (10 * ss + 4) + 'px');
      }
      lsSet(s.lsKey, el.value);
    });
  });
  // Initialise label offsets from the (possibly localStorage-restored) scale values.
  (function(){
    var ns = parseFloat(lsGet('scn','3')) || 3, ss = parseFloat(lsGet('scs','5.5')) || 5.5;
    root.style.setProperty('--label-offset', (10 * ns + 4) + 'px');
    root.style.setProperty('--label-offset-special', (10 * ss + 4) + 'px');
  })();
})();
");
        sb.Append("</script>");
    }

    // ---- Inlined celestial assets (self-contained; the plugin doesn't deploy source asset files) ----

    // Encode a tile SVG for use as a CSS background-image data URL embedded in a JS single-quoted string.
    // The tile const strings are inner content only (no <svg> root); wrap them before encoding.
    // Uri.EscapeDataString follows RFC 2396 and leaves ' unencoded, so we replace it manually.
    private static string SvgDataUrl(string tileInner)
    {
        string full = "<svg xmlns='http://www.w3.org/2000/svg' width='1024' height='1024' viewBox='0 0 1024 1024'>"
                      + tileInner + "</svg>";
        return "data:image/svg+xml," + Uri.EscapeDataString(full).Replace("'", "%27");
    }

    // Inner markup of each tile SVG (verbatim from atlas-export/assets, attributes single-quoted). Filter ids stay
    // f0..f4 here and are prefixed per-mood by NebPattern. Seamless via feTurbulence stitchTiles='stitch'.
    private const string TileDeepField =
        "<defs><filter id='f0' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0017 0.0017' numOctaves='5' seed='2' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.0784 0 0 0 0 0.1333 0 0 0 0 0.2667 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.22'></feFuncA></feComponentTransfer></filter><filter id='f1' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0045 0.0045' numOctaves='5' seed='11' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.1098 0 0 0 0 0.1804 0 0 0 0 0.3608 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.26'></feFuncA></feComponentTransfer></filter><filter id='f2' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.013 0.013' numOctaves='4' seed='23' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.1569 0 0 0 0 0.1176 0 0 0 0 0.3059 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.12'></feFuncA></feComponentTransfer></filter><filter id='f3' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.18 0.18' numOctaves='2' seed='7' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.5882 0 0 0 0 0.6275 0 0 0 0 0.7451 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter><filter id='f4' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.34 0.34' numOctaves='2' seed='91' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.3765 0 0 0 0 0.4235 0 0 0 0 0.5490 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter></defs><rect width='1024' height='1024' fill='#04060d'></rect><rect width='1024' height='1024' filter='url(#f0)'></rect><rect width='1024' height='1024' filter='url(#f1)'></rect><rect width='1024' height='1024' filter='url(#f2)'></rect><rect width='1024' height='1024' filter='url(#f3)'></rect><rect width='1024' height='1024' filter='url(#f4)'></rect>";

    private const string TileViolet =
        "<defs><filter id='f0' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0017 0.0017' numOctaves='5' seed='9' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.1569 0 0 0 0 0.0706 0 0 0 0 0.2039 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.2'></feFuncA></feComponentTransfer></filter><filter id='f1' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0044 0.0044' numOctaves='5' seed='31' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.2510 0 0 0 0 0.1098 0 0 0 0 0.2980 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.25'></feFuncA></feComponentTransfer></filter><filter id='f2' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.013 0.013' numOctaves='4' seed='5' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.1490 0 0 0 0 0.1098 0 0 0 0 0.3373 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.12'></feFuncA></feComponentTransfer></filter><filter id='f3' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.18 0.18' numOctaves='2' seed='14' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.5882 0 0 0 0 0.5647 0 0 0 0 0.6902 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter><filter id='f4' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.34 0.34' numOctaves='2' seed='77' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.3922 0 0 0 0 0.3686 0 0 0 0 0.4863 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter></defs><rect width='1024' height='1024' fill='#070411'></rect><rect width='1024' height='1024' filter='url(#f0)'></rect><rect width='1024' height='1024' filter='url(#f1)'></rect><rect width='1024' height='1024' filter='url(#f2)'></rect><rect width='1024' height='1024' filter='url(#f3)'></rect><rect width='1024' height='1024' filter='url(#f4)'></rect>";

    private const string TileVerdant =
        "<defs><filter id='f0' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0017 0.0017' numOctaves='5' seed='4' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.0471 0 0 0 0 0.1569 0 0 0 0 0.1333 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.2'></feFuncA></feComponentTransfer></filter><filter id='f1' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0044 0.0044' numOctaves='6' seed='17' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.0706 0 0 0 0 0.2275 0 0 0 0 0.1961 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.25'></feFuncA></feComponentTransfer></filter><filter id='f2' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.013 0.013' numOctaves='4' seed='3' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.0863 0 0 0 0 0.1804 0 0 0 0 0.3059 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.11'></feFuncA></feComponentTransfer></filter><filter id='f3' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.18 0.18' numOctaves='2' seed='22' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.5490 0 0 0 0 0.6588 0 0 0 0 0.6275 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter><filter id='f4' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.34 0.34' numOctaves='2' seed='64' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.3765 0 0 0 0 0.4706 0 0 0 0 0.4471 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter></defs><rect width='1024' height='1024' fill='#03100c'></rect><rect width='1024' height='1024' filter='url(#f0)'></rect><rect width='1024' height='1024' filter='url(#f1)'></rect><rect width='1024' height='1024' filter='url(#f2)'></rect><rect width='1024' height='1024' filter='url(#f3)'></rect><rect width='1024' height='1024' filter='url(#f4)'></rect>";

    private const string TileEmber =
        "<defs><filter id='f0' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0017 0.0017' numOctaves='5' seed='6' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.2196 0 0 0 0 0.1412 0 0 0 0 0.0627 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.19'></feFuncA></feComponentTransfer></filter><filter id='f1' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.0042 0.0042' numOctaves='5' seed='41' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.3059 0 0 0 0 0.2039 0 0 0 0 0.0941 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.24'></feFuncA></feComponentTransfer></filter><filter id='f2' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='fractalNoise' baseFrequency='0.013 0.013' numOctaves='4' seed='13' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.2980 0 0 0 0 0.1255 0 0 0 0 0.0941 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='table' tableValues='0 0.11'></feFuncA></feComponentTransfer></filter><filter id='f3' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.18 0.18' numOctaves='2' seed='9' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.6510 0 0 0 0 0.6039 0 0 0 0 0.5176 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter><filter id='f4' x='0' y='0' width='1024' height='1024' filterUnits='userSpaceOnUse' color-interpolation-filters='sRGB'><feTurbulence type='turbulence' baseFrequency='0.34 0.34' numOctaves='2' seed='55' stitchTiles='stitch'></feTurbulence><feColorMatrix type='matrix' values='0 0 0 0 0.4706 0 0 0 0 0.4157 0 0 0 0 0.3373 1 0 0 0 0'></feColorMatrix><feComponentTransfer><feFuncA type='discrete' tableValues='0 0 0 0 0 0 0 0 0 1'></feFuncA></feComponentTransfer></filter></defs><rect width='1024' height='1024' fill='#0b0703'></rect><rect width='1024' height='1024' filter='url(#f0)'></rect><rect width='1024' height='1024' filter='url(#f1)'></rect><rect width='1024' height='1024' filter='url(#f2)'></rect><rect width='1024' height='1024' filter='url(#f3)'></rect><rect width='1024' height='1024' filter='url(#f4)'></rect>";

    // Coloured-by-state star glyph symbols (#star-completed / -run / -unlocked / -failed / -locked / -waypoint).
    private const string NodeIconsSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='0' height='0' style='position:absolute' aria-hidden='true'><defs>"
        + "<symbol id='star-completed' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -11 L 1.9 -1.9 L 11 0 L 1.9 1.9 L 0 11 L -1.9 1.9 L -11 0 L -1.9 -1.9 Z' fill='#ffd87a'></path><path d='M0 -6.6 L 1.3 -1.3 L 6.6 0 L 1.3 1.3 L 0 6.6 L -1.3 1.3 L -6.6 0 L -1.3 -1.3 Z' transform='rotate(45)' fill='#ffd87a' opacity='.8'></path><circle r='1.6' fill='#fff4d6'></circle></symbol>"
        + "<symbol id='star-run' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='#8fb8ff'></path><circle r='1.3' fill='#eaf2ff'></circle></symbol>"
        + "<symbol id='star-unlocked' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='none' stroke='#67e6ff' stroke-width='1.5' stroke-linejoin='round'></path><circle r='1.1' fill='#dffaff'></circle></symbol>"
        + "<symbol id='star-failed' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='#ff7a52'></path><circle r='3' fill='#180407'></circle></symbol>"
        + "<symbol id='star-locked' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -8 L 2.6 -2.6 L 8 0 L 2.6 2.6 L 0 8 L -2.6 2.6 L -8 0 L -2.6 -2.6 Z' fill='#8694b0' opacity='.6'></path></symbol>"
        + "<symbol id='star-waypoint' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -12 L 2 -2 L 12 0 L 2 2 L 0 12 L -2 2 L -12 0 L -2 -2 Z' fill='#ffe6a0'></path><path d='M0 -7.5 L 1.6 -1.6 L 7.5 0 L 1.6 1.6 L 0 7.5 L -1.6 1.6 L -7.5 0 L -1.6 -1.6 Z' transform='rotate(45)' fill='#ffe6a0' opacity='.9'></path><circle r='1.6' fill='#fff8e6'></circle></symbol>"
        + "</defs></svg>";

    // Greyscale star glyphs with all fills as currentColor, so the node's style color (weight color) tints them.
    private const string NodeIconsGreySvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='0' height='0' style='position:absolute' aria-hidden='true'><defs>"
        + "<symbol id='star-grey-completed' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -11 L 1.9 -1.9 L 11 0 L 1.9 1.9 L 0 11 L -1.9 1.9 L -11 0 L -1.9 -1.9 Z' fill='currentColor'></path><path d='M0 -6.6 L 1.3 -1.3 L 6.6 0 L 1.3 1.3 L 0 6.6 L -1.3 1.3 L -6.6 0 L -1.3 -1.3 Z' transform='rotate(45)' fill='currentColor' opacity='.8'></path><circle r='1.6' fill='white' opacity='.9'></circle></symbol>"
        + "<symbol id='star-grey-run' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='currentColor'></path><circle r='1.3' fill='white' opacity='.9'></circle></symbol>"
        + "<symbol id='star-grey-unlocked' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='none' stroke='currentColor' stroke-width='1.5' stroke-linejoin='round'></path><circle r='1.1' fill='white' opacity='.8'></circle></symbol>"
        + "<symbol id='star-grey-failed' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -10 L 1.7 -1.7 L 10 0 L 1.7 1.7 L 0 10 L -1.7 1.7 L -10 0 L -1.7 -1.7 Z' fill='currentColor'></path><circle r='3' fill='white' opacity='.12'></circle></symbol>"
        + "<symbol id='star-grey-locked' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -8 L 2.6 -2.6 L 8 0 L 2.6 2.6 L 0 8 L -2.6 2.6 L -8 0 L -2.6 -2.6 Z' fill='currentColor' opacity='.45'></path></symbol>"
        + "<symbol id='star-grey-waypoint' viewBox='-16 -16 32 32' overflow='visible'><path d='M0 -12 L 2 -2 L 12 0 L 2 2 L 0 12 L -2 2 L -12 0 L -2 -2 Z' fill='currentColor'></path><path d='M0 -7.5 L 1.6 -1.6 L 7.5 0 L 1.6 1.6 L 0 7.5 L -1.6 1.6 L -7.5 0 L -1.6 -1.6 Z' transform='rotate(45)' fill='currentColor' opacity='.9'></path><circle r='1.6' fill='white' opacity='.9'></circle></symbol>"
        + "</defs></svg>";

    // Special-map colored icons, one unique symbol per map, named #star-special-{id}.
    private const string NodeIconsSpecialSvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='0' height='0' style='position:absolute' aria-hidden='true'><defs>"
        + "<symbol id='star-special-ziggurat-refuge' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.273,-1.273 L11.000,0.000 L1.273,1.273 L0.000,11.000 L-1.273,1.273 L-11.000,0.000 L-1.273,-1.273 Z' fill='#c8a848'></path></symbol>"
        + "<symbol id='star-special-burning-monolith' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-13.000 L1.273,-1.273 L5.000,0.000 L1.273,1.273 L0.000,13.000 L-1.273,1.273 L-5.000,0.000 L-1.273,-1.273 Z' fill='#e05828'></path></symbol>"
        + "<symbol id='star-special-ancient-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.750,-4.763 L8.660,-5.000 L5.500,0.000 L8.660,5.000 L2.750,4.763 L0.000,10.000 L-2.750,4.763 L-8.660,5.000 L-5.500,0.000 L-8.660,-5.000 L-2.750,-4.763 Z' fill='#7870c8'></path></symbol>"
        + "<symbol id='star-special-vaal-ruins' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.531,-3.696 L7.071,-7.071 L3.696,-1.531 L10.000,0.000 L3.696,1.531 L7.071,7.071 L1.531,3.696 L0.000,10.000 L-1.531,3.696 L-7.071,7.071 L-3.696,1.531 L-10.000,0.000 L-3.696,-1.531 L-7.071,-7.071 L-1.531,-3.696 Z' fill='#50b08c'></path></symbol>"
        + "<symbol id='star-special-origin-tower' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.294,-4.830 L5.500,-9.526 L3.536,-3.536 L9.526,-5.500 L4.830,-1.294 L11.000,0.000 L4.830,1.294 L9.526,5.500 L3.536,3.536 L5.500,9.526 L1.294,4.830 L0.000,11.000 L-1.294,4.830 L-5.500,9.526 L-3.536,3.536 L-9.526,5.500 L-4.830,1.294 L-11.000,0.000 L-4.830,-1.294 L-9.526,-5.500 L-3.536,-3.536 L-5.500,-9.526 L-1.294,-4.830 Z' fill='#38c0e8'></path></symbol>"
        + "<symbol id='star-special-patriarch-halls' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L2.645,-3.641 L10.462,-3.399 L4.280,1.391 L6.466,8.899 L0.000,4.500 L-6.466,8.899 L-4.280,1.391 L-10.462,-3.399 L-2.645,-3.641 Z' fill='#b87840'></path></symbol>"
        + "<symbol id='star-special-matriarch-halls' viewBox='-16 -16 32 32' overflow='visible'><path d='M6.466,-8.899 L4.280,-1.391 L10.462,3.399 L2.645,3.641 L0.000,11.000 L-2.645,3.641 L-10.462,3.399 L-4.280,-1.391 L-6.466,-8.899 L-0.000,-4.500 Z' fill='#c04888'></path></symbol>"
        + "<symbol id='star-special-precursor-tower' viewBox='-16 -16 32 32' overflow='visible'><path d='M7.778,-7.778 L1.800,0.000 L7.778,7.778 L0.000,1.800 L-7.778,7.778 L-1.800,0.000 L-7.778,-7.778 L-0.000,-1.800 Z' fill='#30c8b0'></path></symbol>"
        + "<symbol id='star-special-western-enigma' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.952,-4.054 L7.818,-6.235 L4.387,-1.001 L9.749,2.225 L3.518,2.806 L4.339,9.010 L0.000,4.500 L-4.339,9.010 L-3.518,2.806 L-9.749,2.225 L-4.387,-1.001 L-7.818,-6.235 L-1.952,-4.054 Z' fill='#5868c8'></path></symbol>"
        + "<symbol id='star-special-eastern-enigma' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L0.574,-1.386 L7.778,-7.778 L1.386,-0.574 L11.000,0.000 L1.386,0.574 L7.778,7.778 L0.574,1.386 L0.000,11.000 L-0.574,1.386 L-7.778,7.778 L-1.386,0.574 L-11.000,0.000 L-1.386,-0.574 L-7.778,-7.778 L-0.574,-1.386 Z' fill='#e0b028'></path></symbol>"
        + "<symbol id='star-special-ruins-kingsmarch' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.545,-4.755 L5.878,-8.090 L4.045,-2.939 L9.511,-3.090 L5.000,0.000 L9.511,3.090 L4.045,2.939 L5.878,8.090 L1.545,4.755 L0.000,10.000 L-1.545,4.755 L-5.878,8.090 L-4.045,2.939 L-9.511,3.090 L-5.000,0.000 L-9.511,-3.090 L-4.045,-2.939 L-5.878,-8.090 L-1.545,-4.755 Z' fill='#7888a0'></path></symbol>"
        + "<symbol id='star-special-western-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L3.500,-6.062 L8.660,-5.000 L7.000,0.000 L8.660,5.000 L3.500,6.062 L0.000,10.000 L-3.500,6.062 L-8.660,5.000 L-7.000,0.000 L-8.660,-5.000 L-3.500,-6.062 Z' fill='#6070b0'></path></symbol>"
        + "<symbol id='star-special-eastern-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.000,-1.732 L9.526,-5.500 L2.000,0.000 L9.526,5.500 L1.000,1.732 L0.000,11.000 L-1.000,1.732 L-9.526,5.500 L-2.000,0.000 L-9.526,-5.500 L-1.000,-1.732 Z' fill='#a07090'></path></symbol>"
        + "<symbol id='star-special-hildas-campsite' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L3.897,-2.250 L9.526,5.500 L0.000,4.500 L-9.526,5.500 L-3.897,-2.250 Z' fill='#d07838'></path></symbol>"
        + "<symbol id='star-special-withered-willow' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-9.000 L1.556,-1.556 L9.000,0.000 L1.556,1.556 L0.000,9.000 L-1.556,1.556 L-9.000,0.000 L-1.556,-1.556 Z M6.364,-6.364 L2.200,0.000 L6.364,6.364 L0.000,2.200 L-6.364,6.364 L-2.200,0.000 L-6.364,-6.364 L-0.000,-2.200 Z' fill='#708060'></path></symbol>"
        + "<symbol id='star-special-monastery-keepers' viewBox='-16 -16 32 32' overflow='visible'><path d='M7.071,-7.071 L5.500,0.000 L7.071,7.071 L0.000,5.500 L-7.071,7.071 L-5.500,0.000 L-7.071,-7.071 L-0.000,-5.500 Z' fill='#8888b0'></path></symbol>"
        + "<symbol id='star-special-well-of-souls' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.009,-6.182 L5.878,-8.090 L5.259,-3.821 L9.511,-3.090 L6.500,0.000 L9.511,3.090 L5.259,3.821 L5.878,8.090 L2.009,6.182 L0.000,10.000 L-2.009,6.182 L-5.878,8.090 L-5.259,3.821 L-9.511,3.090 L-6.500,0.000 L-9.511,-3.090 L-5.259,-3.821 L-5.878,-8.090 L-2.009,-6.182 Z' fill='#3858a0'></path></symbol>"
        + "<symbol id='star-special-sealed-vault' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.487,-6.005 L7.071,-7.071 L6.005,-2.487 L10.000,0.000 L6.005,2.487 L7.071,7.071 L2.487,6.005 L0.000,10.000 L-2.487,6.005 L-7.071,7.071 L-6.005,2.487 L-10.000,0.000 L-6.005,-2.487 L-7.071,-7.071 L-2.487,-6.005 Z' fill='#607890'></path></symbol>"
        + "<symbol id='star-special-reliquary-vault' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.941,-7.244 L5.000,-8.660 L5.303,-5.303 L8.660,-5.000 L7.244,-1.941 L10.000,0.000 L7.244,1.941 L8.660,5.000 L5.303,5.303 L5.000,8.660 L1.941,7.244 L0.000,10.000 L-1.941,7.244 L-5.000,8.660 L-5.303,5.303 L-8.660,5.000 L-7.244,1.941 L-10.000,0.000 L-7.244,-1.941 L-8.660,-5.000 L-5.303,-5.303 L-5.000,-8.660 L-1.941,-7.244 Z' fill='#9858c0'></path></symbol>"
        + "<symbol id='star-special-caer-tarth' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.750,-3.031 L8.660,-5.000 L3.500,0.000 L8.660,5.000 L1.750,3.031 L0.000,10.000 L-1.750,3.031 L-8.660,5.000 L-3.500,0.000 L-8.660,-5.000 L-1.750,-3.031 Z M0,-3.5 A3.5,3.5 0 1,1 0,3.5 A3.5,3.5 0 1,1 0,-3.5 Z' fill-rule='evenodd' fill='#7080a8'></path></symbol>"
        + "<symbol id='star-special-site-of-chosen' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.531,-3.696 L7.071,-7.071 L3.696,-1.531 L10.000,0.000 L3.696,1.531 L7.071,7.071 L1.531,3.696 L0.000,10.000 L-1.531,3.696 L-7.071,7.071 L-3.696,1.531 L-10.000,0.000 L-3.696,-1.531 L-7.071,-7.071 L-1.531,-3.696 Z' fill='#7058c0'></path></symbol>"
        + "<symbol id='star-special-fallen-star' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.750,-3.031 L8.660,-5.000 L3.500,0.000 L8.660,5.000 L1.750,3.031 L0.000,10.000 L-1.750,3.031 L-8.660,5.000 L-3.500,0.000 L-8.660,-5.000 L-1.750,-3.031 Z M0,-3.5 A3.5,3.5 0 1,1 0,3.5 A3.5,3.5 0 1,1 0,-3.5 Z' fill-rule='evenodd' fill='#e8a830'></path></symbol>"
        + "<symbol id='star-special-chained-beast' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.294,-4.830 L5.500,-9.526 L3.536,-3.536 L9.526,-5.500 L4.830,-1.294 L11.000,0.000 L4.830,1.294 L9.526,5.500 L3.536,3.536 L5.500,9.526 L1.294,4.830 L0.000,11.000 L-1.294,4.830 L-5.500,9.526 L-3.536,3.536 L-9.526,5.500 L-4.830,1.294 L-11.000,0.000 L-4.830,-1.294 L-9.526,-5.500 L-3.536,-3.536 L-5.500,-9.526 L-1.294,-4.830 Z' fill='#c83838'></path></symbol>"
        + "<symbol id='star-special-generic' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.487,-6.005 L7.071,-7.071 L6.005,-2.487 L10.000,0.000 L6.005,2.487 L7.071,7.071 L2.487,6.005 L0.000,10.000 L-2.487,6.005 L-7.071,7.071 L-6.005,2.487 L-10.000,0.000 L-6.005,-2.487 L-7.071,-7.071 L-2.487,-6.005 Z' fill='#c850ff'></path></symbol>"
        + "</defs></svg>";

    // Special-map grey (currentColor) icons, weight-tinted when in markers-grey mode.
    private const string NodeIconsSpecialGreySvg =
        "<svg xmlns='http://www.w3.org/2000/svg' width='0' height='0' style='position:absolute' aria-hidden='true'><defs>"
        + "<symbol id='star-special-grey-ziggurat-refuge' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.273,-1.273 L11.000,0.000 L1.273,1.273 L0.000,11.000 L-1.273,1.273 L-11.000,0.000 L-1.273,-1.273 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-burning-monolith' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-13.000 L1.273,-1.273 L5.000,0.000 L1.273,1.273 L0.000,13.000 L-1.273,1.273 L-5.000,0.000 L-1.273,-1.273 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-ancient-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.750,-4.763 L8.660,-5.000 L5.500,0.000 L8.660,5.000 L2.750,4.763 L0.000,10.000 L-2.750,4.763 L-8.660,5.000 L-5.500,0.000 L-8.660,-5.000 L-2.750,-4.763 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-vaal-ruins' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.531,-3.696 L7.071,-7.071 L3.696,-1.531 L10.000,0.000 L3.696,1.531 L7.071,7.071 L1.531,3.696 L0.000,10.000 L-1.531,3.696 L-7.071,7.071 L-3.696,1.531 L-10.000,0.000 L-3.696,-1.531 L-7.071,-7.071 L-1.531,-3.696 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-origin-tower' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.294,-4.830 L5.500,-9.526 L3.536,-3.536 L9.526,-5.500 L4.830,-1.294 L11.000,0.000 L4.830,1.294 L9.526,5.500 L3.536,3.536 L5.500,9.526 L1.294,4.830 L0.000,11.000 L-1.294,4.830 L-5.500,9.526 L-3.536,3.536 L-9.526,5.500 L-4.830,1.294 L-11.000,0.000 L-4.830,-1.294 L-9.526,-5.500 L-3.536,-3.536 L-5.500,-9.526 L-1.294,-4.830 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-patriarch-halls' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L2.645,-3.641 L10.462,-3.399 L4.280,1.391 L6.466,8.899 L0.000,4.500 L-6.466,8.899 L-4.280,1.391 L-10.462,-3.399 L-2.645,-3.641 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-matriarch-halls' viewBox='-16 -16 32 32' overflow='visible'><path d='M6.466,-8.899 L4.280,-1.391 L10.462,3.399 L2.645,3.641 L0.000,11.000 L-2.645,3.641 L-10.462,3.399 L-4.280,-1.391 L-6.466,-8.899 L-0.000,-4.500 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-precursor-tower' viewBox='-16 -16 32 32' overflow='visible'><path d='M7.778,-7.778 L1.800,0.000 L7.778,7.778 L0.000,1.800 L-7.778,7.778 L-1.800,0.000 L-7.778,-7.778 L-0.000,-1.800 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-western-enigma' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.952,-4.054 L7.818,-6.235 L4.387,-1.001 L9.749,2.225 L3.518,2.806 L4.339,9.010 L0.000,4.500 L-4.339,9.010 L-3.518,2.806 L-9.749,2.225 L-4.387,-1.001 L-7.818,-6.235 L-1.952,-4.054 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-eastern-enigma' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L0.574,-1.386 L7.778,-7.778 L1.386,-0.574 L11.000,0.000 L1.386,0.574 L7.778,7.778 L0.574,1.386 L0.000,11.000 L-0.574,1.386 L-7.778,7.778 L-1.386,0.574 L-11.000,0.000 L-1.386,-0.574 L-7.778,-7.778 L-0.574,-1.386 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-ruins-kingsmarch' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.545,-4.755 L5.878,-8.090 L4.045,-2.939 L9.511,-3.090 L5.000,0.000 L9.511,3.090 L4.045,2.939 L5.878,8.090 L1.545,4.755 L0.000,10.000 L-1.545,4.755 L-5.878,8.090 L-4.045,2.939 L-9.511,3.090 L-5.000,0.000 L-9.511,-3.090 L-4.045,-2.939 L-5.878,-8.090 L-1.545,-4.755 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-western-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L3.500,-6.062 L8.660,-5.000 L7.000,0.000 L8.660,5.000 L3.500,6.062 L0.000,10.000 L-3.500,6.062 L-8.660,5.000 L-7.000,0.000 L-8.660,-5.000 L-3.500,-6.062 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-eastern-gateway' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.000,-1.732 L9.526,-5.500 L2.000,0.000 L9.526,5.500 L1.000,1.732 L0.000,11.000 L-1.000,1.732 L-9.526,5.500 L-2.000,0.000 L-9.526,-5.500 L-1.000,-1.732 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-hildas-campsite' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L3.897,-2.250 L9.526,5.500 L0.000,4.500 L-9.526,5.500 L-3.897,-2.250 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-withered-willow' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-9.000 L1.556,-1.556 L9.000,0.000 L1.556,1.556 L0.000,9.000 L-1.556,1.556 L-9.000,0.000 L-1.556,-1.556 Z M6.364,-6.364 L2.200,0.000 L6.364,6.364 L0.000,2.200 L-6.364,6.364 L-2.200,0.000 L-6.364,-6.364 L-0.000,-2.200 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-monastery-keepers' viewBox='-16 -16 32 32' overflow='visible'><path d='M7.071,-7.071 L5.500,0.000 L7.071,7.071 L0.000,5.500 L-7.071,7.071 L-5.500,0.000 L-7.071,-7.071 L-0.000,-5.500 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-well-of-souls' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.009,-6.182 L5.878,-8.090 L5.259,-3.821 L9.511,-3.090 L6.500,0.000 L9.511,3.090 L5.259,3.821 L5.878,8.090 L2.009,6.182 L0.000,10.000 L-2.009,6.182 L-5.878,8.090 L-5.259,3.821 L-9.511,3.090 L-6.500,0.000 L-9.511,-3.090 L-5.259,-3.821 L-5.878,-8.090 L-2.009,-6.182 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-sealed-vault' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.487,-6.005 L7.071,-7.071 L6.005,-2.487 L10.000,0.000 L6.005,2.487 L7.071,7.071 L2.487,6.005 L0.000,10.000 L-2.487,6.005 L-7.071,7.071 L-6.005,2.487 L-10.000,0.000 L-6.005,-2.487 L-7.071,-7.071 L-2.487,-6.005 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-reliquary-vault' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.941,-7.244 L5.000,-8.660 L5.303,-5.303 L8.660,-5.000 L7.244,-1.941 L10.000,0.000 L7.244,1.941 L8.660,5.000 L5.303,5.303 L5.000,8.660 L1.941,7.244 L0.000,10.000 L-1.941,7.244 L-5.000,8.660 L-5.303,5.303 L-8.660,5.000 L-7.244,1.941 L-10.000,0.000 L-7.244,-1.941 L-8.660,-5.000 L-5.303,-5.303 L-5.000,-8.660 L-1.941,-7.244 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-caer-tarth' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.750,-3.031 L8.660,-5.000 L3.500,0.000 L8.660,5.000 L1.750,3.031 L0.000,10.000 L-1.750,3.031 L-8.660,5.000 L-3.500,0.000 L-8.660,-5.000 L-1.750,-3.031 Z M0,-3.5 A3.5,3.5 0 1,1 0,3.5 A3.5,3.5 0 1,1 0,-3.5 Z' fill-rule='evenodd' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-site-of-chosen' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.531,-3.696 L7.071,-7.071 L3.696,-1.531 L10.000,0.000 L3.696,1.531 L7.071,7.071 L1.531,3.696 L0.000,10.000 L-1.531,3.696 L-7.071,7.071 L-3.696,1.531 L-10.000,0.000 L-3.696,-1.531 L-7.071,-7.071 L-1.531,-3.696 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-fallen-star' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L1.750,-3.031 L8.660,-5.000 L3.500,0.000 L8.660,5.000 L1.750,3.031 L0.000,10.000 L-1.750,3.031 L-8.660,5.000 L-3.500,0.000 L-8.660,-5.000 L-1.750,-3.031 Z M0,-3.5 A3.5,3.5 0 1,1 0,3.5 A3.5,3.5 0 1,1 0,-3.5 Z' fill-rule='evenodd' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-chained-beast' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-11.000 L1.294,-4.830 L5.500,-9.526 L3.536,-3.536 L9.526,-5.500 L4.830,-1.294 L11.000,0.000 L4.830,1.294 L9.526,5.500 L3.536,3.536 L5.500,9.526 L1.294,4.830 L0.000,11.000 L-1.294,4.830 L-5.500,9.526 L-3.536,3.536 L-9.526,5.500 L-4.830,1.294 L-11.000,0.000 L-4.830,-1.294 L-9.526,-5.500 L-3.536,-3.536 L-5.500,-9.526 L-1.294,-4.830 Z' fill='currentColor'></path></symbol>"
        + "<symbol id='star-special-grey-generic' viewBox='-16 -16 32 32' overflow='visible'><path d='M0.000,-10.000 L2.487,-6.005 L7.071,-7.071 L6.005,-2.487 L10.000,0.000 L6.005,2.487 L7.071,7.071 L2.487,6.005 L0.000,10.000 L-2.487,6.005 L-7.071,7.071 L-6.005,2.487 L-10.000,0.000 L-6.005,-2.487 L-7.071,-7.071 L-2.487,-6.005 Z' fill='currentColor'></path></symbol>"
        + "</defs></svg>";
}
