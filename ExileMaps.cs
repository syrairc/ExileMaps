using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Helpers;
using ExileCore2.Shared.Nodes;
using ExileCore2.Shared.Enums;

using GameOffsets2.Native;

using ImGuiNET;

using RectangleF = ExileCore2.Shared.RectangleF;
using ExileMaps.Classes;

namespace ExileMaps;

public class ExileMapsCore : BaseSettingsPlugin<ExileMapsSettings>
{
    #region Declarations
    public static ExileMapsCore Main;

    private const string defaultMapsPath = "json\\maps.json";
    private const string defaultBiomesPath = "json\\biomes.json";
    private const string defaultContentPath = "json\\content.json";
    private const string ArrowPath = "textures\\arrow.png";
    // Selectable waypoint-path sprites (one dash each). Loaded if present; the path falls back to a
    // plain line when the selected texture is missing or 'Use Icons for Nodes' is off.
    private static readonly Dictionary<PathTextureStyle, string> PathTextureFiles = new()
    {
        { PathTextureStyle.Comet,         "textures\\path-energy-stripe.png" },
        { PathTextureStyle.Chevron,       "textures\\path-chevron.png" },
        { PathTextureStyle.DoubleChevron, "textures\\path-double-chevron.png" },
        { PathTextureStyle.Capsule,       "textures\\path-capsule.png" },
    };
    private const string IconsFile = "Icons.png";
    // New custom sprite sheet for the plugin (drop the generated PNG in textures/). Loaded if present;
    // grid layout (SpriteSheetCols x SpriteSheetRows) to be set once the sheet is finalized.
    // Custom sprite atlas (SpriteAtlas): 1024x768, 8x6, 128px cells, 48 desaturated icons.
    private const string CustomIconsPath = "textures\\Icons_Desaturated.png";
    private const string CustomIconsName = SpriteAtlas.FileName;
    
    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private readonly List<Node> selectedNodes = [];
    // How often (in frames) to recompute the on-screen node set. Throttles the per-frame cull only;
    // the cached set still redraws every frame. Was the "Render every N ticks" setting.
    private const int OnScreenRecomputeInterval = 5;
    // Waypoint-panel list filter: 0 = All, 1 = Manual only, 2 = Auto-created only. Transient (not saved).
    private int waypointListFilter = 0;
    // Reused each frame to resolve on-screen node rects without allocating a new list per render.
    private readonly List<(Node node, RectangleF rect)> nodePositions = [];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedExcludeRects = [];
    private Dictionary<Vector2i, Node> mapCache = [];
    public bool refreshCache = false;
    private bool refreshingCache = false;
    // When set, the next background refresh clears the cache first (atlas reopen / area change).
    private bool clearCacheOnRefresh = false;
    // 0..1 fraction of the current cache refresh, updated from the background refresh job and read by
    // the render thread to draw the reload progress bar. volatile so the render thread sees updates.
    private volatile float cacheRefreshProgress = 0f;
    private float maxMapWeight = 50.0f;
    private float minMapWeight = -50.0f;
    private readonly object mapCacheLock = new();
    private DateTime lastRefresh = DateTime.Now;
    private bool weightsDirty = false;
    private DateTime lastWeightRecalc = DateTime.Now;
    private int TickCount { get; set; }

    // Bumped whenever a cache refresh finishes. Step counts and the atlas-panel list are memoized
    // against this so the waypoint panel doesn't rerun a full BFS + filter/sort every render frame.
    private int mapCacheVersion = 0;
    private int weightsRecalcVersion = 0;
    // id -> Map index for the MatchID fallback, so resolving a node's map type is O(1) instead of an
    // O(n) scan of every map per node. Rebuilt when the map dictionary gains entries (scrape).
    private Dictionary<string, Map> mapIdIndex;
    private int mapIdIndexCount = -1;
    private Dictionary<Vector2i, int> cachedStepCounts;
    private int cachedStepCountsVersion = -1;
    private List<Node> cachedAtlasList;
    private (int ver, int wver, string filter, bool regex, string sort, string sort2, int maxSteps, int maxItems) cachedAtlasSig = (-1, -1, null, false, null, null, -1, -1);

    private Vector2 atlasOffset;
    private Vector2 atlasDelta;

    internal IntPtr iconsId;
    internal IntPtr arrowId;
    internal IntPtr customIconsId;
    private bool customIconsLoaded = false;
    // Texture ids for each loaded path sprite, keyed by style. Missing entries = file not present.
    private readonly Dictionary<PathTextureStyle, IntPtr> pathTextureIds = new();

    private bool AtlasHasBeenClosed = true;
    private bool gameFilesScraped = false;
    private bool WaypointPanelIsOpen = false;
    private bool ShowMinimap = false;


    #endregion

    #region ExileCore Methods
    public override bool Initialise()
    {
        Main = this;
        RegisterHotkeys();
        SubscribeToEvents();

        // Map/Content/Biome dictionaries are populated by game-file scraping (UpdateMapData etc.) once
        // the game files are available in Tick(), not here. Profile weight seeding therefore happens
        // post-scrape via SeedProfiles(). EnsureDefaultProfile only guarantees a valid ActiveProfile key.
        Settings.Profiles.EnsureDefaultProfile();

        // Must run before ExileCore re-saves the settings file as v2 (first save destroys the old data),
        // so we sniff + back it up here at load. The actual conversion is deferred to a user banner click.
        DetectOldSettings();

        Graphics.InitImage(IconsFile);
        iconsId = Graphics.GetTextureId(IconsFile);
        Graphics.InitImage("arrow.png", Path.Combine(DirectoryFullName, ArrowPath));
        arrowId = Graphics.GetTextureId("arrow.png");

        // Load each selectable waypoint-path sprite that exists. DrawWaypointPath falls back to a plain
        // line for any style whose file is missing.
        foreach (var (style, rel) in PathTextureFiles) {
            var full = Path.Combine(DirectoryFullName, rel);
            if (!File.Exists(full))
                continue;
            var name = Path.GetFileName(rel);
            Graphics.InitImage(name, full);
            pathTextureIds[style] = Graphics.GetTextureId(name);
        }

        // Load the custom sprite sheet if it's been added. Guarded so the plugin still runs before
        // the PNG exists; once present, address sprites via GetSpriteUV(col, row).
        var customIconsFull = Path.Combine(DirectoryFullName, CustomIconsPath);
        if (File.Exists(customIconsFull)) {
            Graphics.InitImage(CustomIconsName, customIconsFull);
            customIconsId = Graphics.GetTextureId(CustomIconsName);
            customIconsLoaded = true;
        }

        CanUseMultiThreading = true;

        return true;
    }
    public override void AreaChange(AreaInstance area)
    {
        refreshCache = true;
    }

    public override void Tick()
    {
        UI = GameController.Game.IngameState.IngameUi;
        AtlasPanel = UI.WorldMap.AtlasPanel;

        if (!AtlasPanel.IsVisible) {
            AtlasHasBeenClosed = true;
            WaypointPanelIsOpen = false;
            return;
        }

        if (AtlasHasBeenClosed) {
            // Enqueue an async clearing refresh instead of running it inline on the main thread
            // (which froze the overlay and hid the reload bar). Force past the throttle.
            refreshCache = true;
            clearCacheOnRefresh = true;
            lastRefresh = DateTime.MinValue;
        }

        AtlasHasBeenClosed = false;

        // Scrape map types, content types and biomes from the game's endgame files once they're
        // available (files may not be loaded yet at Initialise). All three load together, so gate on
        // the map scrape succeeding. Once scraped, seed/apply profile weights against the live data.
        if (!gameFilesScraped && UpdateMapData(false)) {
            UpdateContentData(false);
            UpdateBiomeData(false);
            gameFilesScraped = true;
            SeedProfiles();
        }

        // The atlas streams its node Descriptions in progressively after opening (0 -> ... -> full over
        // several seconds after an area change), so the live count can outrun what we cached. Re-flag a
        // refresh whenever the cache is behind, so a partial first fill gets corrected.
        bool cacheBehind = AtlasPanel.Descriptions.Count > mapCache.Count;
        if (cacheBehind)
            refreshCache = true;

        screenCenter = GameController.Window.GetWindowRectangle().Center - GameController.Window.GetWindowRectangle().Location;

        // Throttle full refreshes to MapCacheRefreshRate, EXCEPT while the cache is still filling (empty,
        // or behind the live node count). Without the cacheBehind bypass a partial cache caught mid-stream
        // right after an area change - e.g. 71 of 585 nodes - was shown until the rate elapsed (up to tens
        // of seconds); the old `mapCache.Count == 0` check only caught a fully empty cache. The 0.5s floor
        // stops a slow stream from kicking off a burst of back-to-back refreshes.
        double elapsed = DateTime.Now.Subtract(lastRefresh).TotalSeconds;
        bool throttleOpen = elapsed > Settings.Graphics.MapCacheRefreshRate
            || mapCache.Count == 0
            || (cacheBehind && elapsed > 0.5);
        if (refreshCache && !refreshingCache && throttleOpen)
        {
            bool clearCache = clearCacheOnRefresh;
            clearCacheOnRefresh = false;
            var job = new Job($"{nameof(ExileMaps)}RefreshCache", () =>
            {
                // Task.Run swallows exceptions: without this guard a thrown refresh would leave
                // refreshingCache stuck true and deadlock every future refresh + weight recalc. Reset the
                // flags here (not only at RefreshMapCache's tail) so failures recover. On error advance
                // lastRefresh so a persistently-failing refresh retries on the throttle, not every tick.
                try {
                    RefreshMapCache(clearCache);
                } catch (Exception e) {
                    LogError("Error refreshing map cache: " + e.Message + "\n" + e.StackTrace);
                    lastRefresh = DateTime.Now;
                } finally {
                    refreshingCache = false;
                    refreshCache = false;
                }
            });
            job.Start();
        }

        // Coalesce settings-change weight recalcs. Slider drags (and quick-edit edits) fire
        // PropertyChanged many times per second; mark dirty and recompute at most ~2x/sec (500ms debounce).
        if (weightsDirty && !refreshingCache && DateTime.Now.Subtract(lastWeightRecalc).TotalMilliseconds > 500) {
            weightsDirty = false;
            lastWeightRecalc = DateTime.Now;
            RecalculateWeights();
        }

        // Waypoint paths only change when nodes get visited (which triggers a cache refresh) or when
        // waypoints are added/removed. RefreshMapCache and Add/RemoveWaypoint already recompute them,
        // so there's no need to rerun the full per-waypoint BFS + closest-visited-node scan every tick.

        return;
    }

    public override void Render()
    {
        ProcessPendingWeightFile();

        CheckKeybinds();

        if (WaypointPanelIsOpen) DrawWaypointPanel();

        if (quickEditOpen) DrawQuickEditPanel();

        if (debugNodeOpen) DrawNodeDebugPanel();

        TickCount++;

        if (!AtlasPanel.IsVisible) return;
        // First-open population is handled asynchronously by Tick's refresh gate (which fires when
        // mapCache.Count == 0), so the overlay keeps drawing and the reload bar can show. Don't run
        // a synchronous refresh on the render thread here.

        // Cache panel/tooltip bounds once per frame so IsOnScreen avoids repeated game-memory reads.
        UpdateScreenBounds();

        // Recompute the on-screen node set only every OnScreenRecomputeInterval frames (the filter is
        // expensive: per-node GetClientRect + IsOnScreen over the whole atlas), but redraw the cached
        // set every frame so the immediate-mode overlay never flickers. Node positions stay live because
        // RenderNode reads GetClientRect fresh each frame. (This was the old "Render every N ticks"
        // setting; it only ever throttled this cull, never the draw, so it's an internal constant now.)
        if (TickCount % OnScreenRecomputeInterval == 0 || selectedNodes.Count == 0) {
            lock (mapCacheLock) {
                // Reuse the list (clear + refill) instead of allocating a new one each recompute.
                selectedNodes.Clear();
                selectedNodes.AddRange(mapCache.Values.AsParallel()
                    .Where(x => Settings.Features.ProcessVisitedNodes || !x.IsVisited || x.IsAttempted)
                    .Where(x => (Settings.Features.ProcessHiddenNodes && !x.IsVisible) || x.IsVisible)
                    .Where(x => (Settings.Features.ProcessLockedNodes && !x.IsUnlocked) || x.IsUnlocked)
                    .Where(x => (Settings.Features.ProcessUnlockedNodes && x.IsUnlocked) || !x.IsUnlocked)
                    .Where(x => IsOnScreen(x.MapNode.Element.GetClientRect().Center)));
            }
        }

        if (!ShowMinimap) {
            // Resolve each node's screen rect once per frame, then draw in fixed z-layers across the
            // whole set: lines -> node fills -> rings -> labels. Layered passes (rather than one full
            // draw per node) keep the z-order globally consistent and stop lines flickering over
            // circles/labels.
            nodePositions.Clear();
            foreach (var node in selectedNodes) {
                try { nodePositions.Add((node, node.MapNode.Element.GetClientRect())); }
                catch (Exception e) { DebugSwallow("Render: node rect read", e); }
            }

            if (Settings.Features.DebugMode) {
                foreach (var (node, _) in nodePositions)
                    DrawDebugging(node);
            } else {
                // 1. Lines
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLines(node, rect);
                // 2. Node fills
                foreach (var (node, rect) in nodePositions) {
                    try { DrawMapNode(node, rect); }
                    catch (Exception e) { LogError("Error drawing node fill: " + e.Message); }
                }
                // 3. Rings
                foreach (var (node, rect) in nodePositions)
                    DrawNodeRings(node, rect);
                // 3b. Favorite star markers (above rings, below labels)
                foreach (var (node, rect) in nodePositions)
                    DrawFavoriteIndicator(node, rect);
                // 3c. Special map markers (icon above the node instead of a covering fill)
                foreach (var (node, rect) in nodePositions)
                    DrawSpecialIndicator(node, rect);
                // 3d. Atlas-point markers (small silver star just above the node)
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasPointIndicator(node, rect);
                // 3e. Atlas-quest markers (small golden exclamation above the node)
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasQuestIndicator(node, rect);
                // 4. Labels
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLabels(node, rect);
            }
        }

        try {
            foreach (var (k,waypoint) in Settings.Waypoints.Waypoints) {
                DrawWaypoint(waypoint);
                DrawWaypointArrow(waypoint);
            }
        }
        catch (Exception e) {
            LogError("Error drawing waypoints: " + e.Message + "\n" + e.StackTrace);
        }

        DrawCacheProgressBar();

    }

    // Small, non-interactive progress bar centered at the bottom of the play area. Two states:
    //  - a determinate fill while the background refresh job rebuilds the cache (cacheRefreshProgress);
    //  - an indeterminate sweep during the first-open stall, before the refresh even starts, while we
    //    wait for the game's endgame files to load / scrape and the first cache to build (so the user
    //    sees "something is happening" instead of an empty atlas for a few seconds).
    // Fill color for the cache reload bar (not user-configurable; it's a brief transient indicator).
    private static readonly Color CacheBarColor = Color.FromArgb(255, 90, 200, 255);

    private void DrawCacheProgressBar()
    {
        // Pre-refresh loading: files not scraped yet, or the cache hasn't been populated for the first
        // time. Only meaningful while the atlas is open (this is only called from Render after the
        // AtlasPanel.IsVisible gate).
        bool loading = !gameFilesScraped || mapCache.Count == 0;
        if (!refreshingCache && !loading)
            return;

        try {
            const float barWidth = 260f;
            const float barHeight = 6f;
            const float bottomMargin = 48f;

            float left = cachedScreenRect.Center.X - barWidth / 2f;
            float top = cachedScreenRect.Bottom - bottomMargin;

            Vector2 bgStart = new(left, top);
            Vector2 bgEnd = new(left + barWidth, top + barHeight);

            // Track (background).
            Graphics.DrawBox(bgStart, bgEnd, Color.FromArgb(180, 0, 0, 0), 3f);

            string label;
            if (refreshingCache) {
                // Determinate fill.
                float pct = Math.Clamp(cacheRefreshProgress, 0f, 1f);
                if (pct > 0f)
                    Graphics.DrawBox(bgStart, new Vector2(left + barWidth * pct, top + barHeight), CacheBarColor, 3f);
                label = "Updating Atlas Cache";
            } else {
                // Indeterminate sweep (no percentage known yet): a segment slides left->right and wraps,
                // driven by the per-frame TickCount.
                float segW = barWidth * 0.3f;
                float travel = barWidth + segW;
                float pos = ((TickCount * 3f) % travel) - segW;
                float segLeft = Math.Clamp(left + pos, left, left + barWidth);
                float segRight = Math.Clamp(left + pos + segW, left, left + barWidth);
                if (segRight > segLeft)
                    Graphics.DrawBox(new Vector2(segLeft, top), new Vector2(segRight, top + barHeight), CacheBarColor, 3f);
                label = "Loading atlas data...";
            }

            DrawCenteredTextWithBackground(label, new Vector2(cachedScreenRect.Center.X, top - 6f),
                Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 8, 3);
        } catch (Exception e) {
            LogError("Error drawing cache progress bar: " + e.Message);
        }
    }
    #endregion

    #region Keybinds & Events

    ///MARK: SubscribeToEvents
    /// <summary>
    /// Subscribes to events that trigger a refresh of the map cache.
    /// </summary>
    private void SubscribeToEvents() {
        try {
            Settings.Maps.Maps.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Maps.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Biomes.Biomes.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Biomes.Biomes.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Content.ContentTypes.CollectionChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Content.ContentTypes.PropertyChanged += (_, _) => { weightsDirty = true; };
            Settings.Maps.Maps.CollectionChanged += (_, _) => { refreshCache = true; };
        } catch (Exception e) {
            LogError("Error subscribing to events: " + e.Message);
        }
    }

    ///MARK: RegisterHotkeys
    /// <summary>
    /// Registers the hotkeys defined in the settings.
    /// </summary>
    private void RegisterHotkeys() {
        RegisterHotkey(Settings.Keybinds.RefreshMapCacheHotkey);
        RegisterHotkey(Settings.Keybinds.DebugKey);
        RegisterHotkey(Settings.Keybinds.ToggleDebugModeHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.QuickEditNodeHotkey);
        RegisterHotkey(Settings.Keybinds.DebugNodeHotkey);
        RegisterHotkey(Settings.Keybinds.DeleteWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.UpdateMapsKey);
        RegisterHotkey(Settings.Keybinds.UpdateContentKey);
        RegisterHotkey(Settings.Keybinds.UpdateBiomesKey);
        RegisterHotkey(Settings.Keybinds.ToggleLockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleUnlockedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleVisitedNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleHiddenNodesHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointsHotkey);
    }
    
    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        Input.RegisterKey(hotkey.Value);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey.Value); };
    }
    private void CheckKeybinds() {
        if (!AtlasPanel.IsVisible)
            return;

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {
            // Enqueue a background refresh instead of running it inline on the render thread (which
            // froze the overlay for the whole refresh and hid the reload bar). Force past the
            // throttle so a manual press refreshes immediately.
            refreshCache = true;
            lastRefresh = DateTime.MinValue;
        }

        if (Settings.Keybinds.DebugKey.PressedOnce())
            DoDebugging();

        if (Settings.Keybinds.ToggleDebugModeHotkey.PressedOnce())
            Settings.Features.DebugMode.Value = !Settings.Features.DebugMode.Value;

        if (Settings.Keybinds.UpdateMapsKey.PressedOnce())
            UpdateMapData();

        if (Settings.Keybinds.UpdateContentKey.PressedOnce())
            UpdateContentData();

        if (Settings.Keybinds.UpdateBiomesKey.PressedOnce())
            UpdateBiomeData();

        if (Settings.Keybinds.ToggleWaypointPanelHotkey.PressedOnce()) {  
            WaypointPanelIsOpen = !WaypointPanelIsOpen;
        }

        if (Settings.Keybinds.AddWaypointHotkey.PressedOnce())
            AddWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.QuickEditNodeHotkey.PressedOnce()) {
            var editNode = GetClosestNodeToCursor();
            if (editNode != null) { quickEditNode = editNode; quickEditOpen = true; }
        }

        if (Settings.Keybinds.DebugNodeHotkey.PressedOnce()) {
            var dbgNode = GetClosestNodeToCursor();
            if (dbgNode != null) { debugNode = dbgNode; debugNodeOpen = true; }
        }

        if (Settings.Keybinds.DeleteWaypointHotkey.PressedOnce())        
            RemoveWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.ToggleLockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessLockedNodes.Value = !Settings.Features.ProcessLockedNodes.Value;
        
        if (Settings.Keybinds.ToggleUnlockedNodesHotkey.PressedOnce())        
            Settings.Features.ProcessUnlockedNodes.Value = !Settings.Features.ProcessUnlockedNodes.Value;

        if (Settings.Keybinds.ToggleVisitedNodesHotkey.PressedOnce())
            Settings.Features.ProcessVisitedNodes.Value = !Settings.Features.ProcessVisitedNodes.Value;

        if (Settings.Keybinds.ToggleHiddenNodesHotkey.PressedOnce())
            Settings.Features.ProcessHiddenNodes.Value = !Settings.Features.ProcessHiddenNodes.Value;

        if (Settings.Keybinds.ToggleWaypointsHotkey.PressedOnce()) {
            bool show = !Settings.Waypoints.ShowWaypoints;
            Settings.Waypoints.ShowWaypoints = show;
            Settings.Waypoints.ShowWaypointArrows = show;
        }

    }
    #endregion

    #region Load Defaults
    // Collapses map-type entries that share a display Name into a single entry, merging their ids and
    // removing the redundant rows. Different ids can share a Name (e.g. the "Precursor Tower" towers),
    // and json seeding can leave several such rows; node matching uses MatchID(IDs), so the union of ids
    // on the kept entry preserves matching.
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

    // Called after a profile is overlaid onto the live map/content/biome data (LoadProfile). Cached
    // Node.Weight values are stale until each node's RecalculateWeight re-runs, which happens in a cache
    // refresh - so force one, bypassing the throttle, so weights/colors/waypoints update immediately
    // instead of after the next throttled refresh (or not at all). Cheap: no clear, existing nodes just
    // get recomputed.
    public void OnProfileApplied() {
        refreshCache = true;
        lastRefresh = DateTime.MinValue;
    }

    // Reset live weights to the bundled plugin defaults AND persist them: the active profile is the
    // source of truth that gets overlaid onto live data on every launch (SeedProfiles -> LoadProfile),
    // so without SaveCurrentProfile the reset would be lost on reload.
    public void ResetWeightsToDefaults() {
        LoadDefaultWeights();
        Settings.Profiles.SaveCurrentProfile();
        weightsDirty = true;
    }

    public void LoadDefaultWeights() {
        try {
            // Load bundled default weights and apply to current map/content/biome entries
            var weightsPath = Path.Combine(DirectoryFullName, "json\\exilemaps_weights.json");
            if (!File.Exists(weightsPath))
                return;

            var json = File.ReadAllText(weightsPath);
            var export = JsonSerializer.Deserialize<WeightExport>(json);

            if (export?.Maps != null) {
                foreach (var kvp in export.Maps) {
                    if (Settings.Maps.Maps.TryGetValue(kvp.Key, out var map)) {
                        map.Weight = kvp.Value;
                    }
                }
            }

            if (export?.Content != null) {
                foreach (var kvp in export.Content) {
                    if (Settings.Maps.Content.ContentTypes.TryGetValue(kvp.Key, out var content)) {
                        content.Weight = kvp.Value;
                    }
                }
            }

            if (export?.Biomes != null) {
                foreach (var kvp in export.Biomes) {
                    if (Settings.Maps.Biomes.Biomes.TryGetValue(kvp.Key, out var biome)) {
                        biome.Weight = kvp.Value;
                    }
                }
            }
        } catch (Exception e) {
            LogError("Error loading default weights: " + e.Message);
        }
    }

    // Called once after the game-file scrape populates the live Map/Content/Biome dictionaries.
    // First run (active profile empty): seed live weights from the bundled defaults and capture them
    // into the active profile. Subsequent runs: overlay the saved active profile onto the live data
    // (which may have just gained newly-scraped entries).
    private void SeedProfiles() {
        try {
            var profiles = Settings.Profiles;
            profiles.EnsureDefaultProfile();

            if (!profiles.Profiles.TryGetValue(profiles.ActiveProfile, out var active))
                return;

            if (active.Maps.Count == 0 && active.Content.Count == 0 && active.Biomes.Count == 0) {
                LoadDefaultWeights();
                profiles.SaveCurrentProfile();
            } else {
                profiles.LoadProfile(profiles.ActiveProfile);
            }

            weightsDirty = true;
        } catch (Exception e) {
            LogError("Error seeding profiles: " + e.Message);
        }
    }

    #endregion

    #region Weight Import/Export
    // The host is a DirectX overlay (ClickableTransparentOverlay), not a WinForms app, so a WinForms
    // ShowDialog joined on the render thread freezes/faults the overlay. Instead we open a native
    // comdlg32 dialog on a short-lived background STA thread (no join, render keeps running), stash the
    // chosen path in a volatile field, and let ProcessPendingWeightFile (called from Render) do the
    // actual read/write on the plugin thread.
    private volatile string pendingExportSettingsPath;
    private volatile string pendingImportSettingsPath;
    private volatile string pendingExportProfilePath;
    private volatile string pendingImportProfilePath;
    private volatile string pendingImportOldSettingsPath;
    // Profile name to capture a pending old-settings import into (set by the auto-detect banner; the
    // manual file-picker leaves it null so the profile is named after the file).
    private volatile string pendingImportOldSettingsName;
    private volatile bool weightDialogBusy;

    // Pre-rework ("v1") settings auto-detect state. Set in Initialise: if the on-disk settings file still
    // has the old structure we back it up (ExileCore rewrites the live file as v2 on the next save) and
    // raise a one-time import banner. oldSettingsBackupPath points at the preserved copy.
    private bool oldSettingsPendingImport = false;
    private string oldSettingsBackupPath;
    public bool OldSettingsPendingImport => oldSettingsPendingImport;

    // Called from a button in settings. Save/load the entire settings object (all categories).
    public void ExportSettings() => OpenSettingsFileDialogAsync(save: true);
    public void ImportSettings() => OpenSettingsFileDialogAsync(save: false);

    // Called from buttons in the Profiles section. Save/load a single weight profile as JSON.
    public void ExportProfile() => OpenProfileFileDialogAsync(save: true);
    public void ImportProfile() => OpenProfileFileDialogAsync(save: false);

    // Called from a button in settings. Convert + import a pre-rework ("v1") settings file.
    public void ImportOldSettings() => OpenOldSettingsFileDialogAsync();

    // Runs once at load (Initialise). If the user hasn't already handled the v1 migration and the on-disk
    // settings file still has the pre-rework structure, copy it to a sidecar backup - ExileCore rewrites
    // the live file as v2 on the next save, destroying the old data - and raise the one-time import banner.
    private void DetectOldSettings() {
        try {
            if (Settings.Profiles.OldSettingsMigrationHandled)
                return;

            var path = FindSettingsFilePath();
            if (path == null || !File.Exists(path))
                return;

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text) || !LooksLikeOldSettings(text))
                return;

            // Preserve the v1 data before ExileCore overwrites the live file on the next save.
            var dir = Path.GetDirectoryName(path);
            var backup = Path.Combine(dir, "ExileMaps_settings.v1.bak.json");
            File.Copy(path, backup, overwrite: true);
            oldSettingsBackupPath = backup;
            oldSettingsPendingImport = true;
            LogMessage($"Detected pre-rework settings; backed up to {backup}. Import available in plugin settings.");
        } catch (Exception e) {
            LogError("Error detecting old settings: " + e.Message);
        }
    }

    // Locate this plugin's ExileCore2 settings file: <ConfigDirectory>/<Name>_settings.json
    // (e.g. config/global/ExileMaps_settings.json). Falls back to any *_settings.json in that directory.
    private string FindSettingsFilePath() {
        try {
            var dir = ConfigDirectory;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return null;
            var primary = Path.Combine(dir, InternalName + "_settings.json");
            if (File.Exists(primary))
                return primary;
            var matches = Directory.GetFiles(dir, "*_settings.json");
            return matches.FirstOrDefault(f => Path.GetFileName(f).Contains("ExileMaps", StringComparison.OrdinalIgnoreCase))
                ?? matches.FirstOrDefault();
        } catch {
            return null;
        }
    }

    // The v1 settings file has top-level sections that no longer exist in the current structure
    // (MapTypes -> Maps, separate MapContent, dropped MapMods). On-disk files wrap data in "data".
    private static bool LooksLikeOldSettings(string json) {
        try {
            var root = Newtonsoft.Json.Linq.JObject.Parse(json);
            var data = root["data"] as Newtonsoft.Json.Linq.JObject ?? root;
            return data["MapTypes"] != null || data["MapContent"] != null || data["MapMods"] != null;
        } catch {
            return false;
        }
    }

    // Banner "Import": convert the backed-up v1 file into an "Original Settings" profile (and restore
    // keybinds/other categories) on the next ProcessPendingWeightFile pass, then stop prompting.
    public void ImportDetectedOldSettings() {
        if (oldSettingsBackupPath != null) {
            pendingImportOldSettingsName = "Original Settings";
            pendingImportOldSettingsPath = oldSettingsBackupPath;
        }
        oldSettingsPendingImport = false;
        Settings.Profiles.OldSettingsMigrationHandled = true;
    }

    // Banner "Dismiss": stop prompting. The .v1.bak file stays on disk, so the manual "Import Old
    // Settings" button can still convert it later if the user changes their mind.
    public void DismissDetectedOldSettings() {
        oldSettingsPendingImport = false;
        Settings.Profiles.OldSettingsMigrationHandled = true;
    }

    // Applies any path the dialog thread produced. Runs on the plugin thread (from Render) so all
    // settings access stays on the same thread the UI uses.
    public void ProcessPendingWeightFile() {
        var exportSettings = pendingExportSettingsPath;
        if (exportSettings != null) {
            pendingExportSettingsPath = null;
            WriteSettings(exportSettings);
        }

        var importSettings = pendingImportSettingsPath;
        if (importSettings != null) {
            pendingImportSettingsPath = null;
            ReadSettings(importSettings);
        }

        var exportProfile = pendingExportProfilePath;
        if (exportProfile != null) {
            pendingExportProfilePath = null;
            WriteProfile(exportProfile);
        }

        var importProfile = pendingImportProfilePath;
        if (importProfile != null) {
            pendingImportProfilePath = null;
            ReadProfile(importProfile);
        }

        var importOld = pendingImportOldSettingsPath;
        if (importOld != null) {
            pendingImportOldSettingsPath = null;
            var importOldName = pendingImportOldSettingsName;
            pendingImportOldSettingsName = null;
            ConvertOldSettings(importOld, importOldName);
        }
    }

    // Serializes the entire settings object with Newtonsoft (the same serializer ExileCore2 uses to
    // persist settings) so every category round-trips. CustomNode/EmptyNode UI fields are [JsonIgnore]
    // and skipped automatically.
    private void WriteSettings(string path) {
        try {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(Settings, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
            LogMessage($"Exported settings to {path}");
        } catch (Exception e) {
            LogError("Error exporting settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports a settings file written by WriteSettings. Uses PopulateObject so the live Settings instance
    // (held by ExileCore2) is mutated in place rather than replaced - existing object/dictionary
    // references stay valid, and dictionary entries merge by key. Forces a full recalc + cache refresh.
    private void ReadSettings(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing settings: file not found ({path}).");
                return;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) {
                LogError("Error importing settings: file was empty or invalid.");
                return;
            }

            Newtonsoft.Json.JsonConvert.PopulateObject(json, Settings);

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported settings from {path}");
        } catch (Exception e) {
            LogError("Error importing settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Saves the active weight profile (weights + per-entry display settings) as a standalone JSON file
    // so users can share or back up presets. Uses System.Text.Json so Color fields serialize via the
    // JsonColorConverter on WeightProfile. SaveCurrentProfile first so unsaved live edits are captured.
    private void WriteProfile(string path) {
        try {
            Settings.Profiles.SaveCurrentProfile();
            if (!Settings.Profiles.Profiles.TryGetValue(Settings.Profiles.ActiveProfile, out var profile)) {
                LogError("Error exporting profile: active profile not found.");
                return;
            }

            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
            LogMessage($"Exported profile '{Settings.Profiles.ActiveProfile}' to {path}");
        } catch (Exception e) {
            LogError("Error exporting profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports a weight profile written by WriteProfile, adding it as a new profile named after the file
    // (deduped) and switching to it so it applies to the live map/content/biome data immediately.
    private void ReadProfile(string path) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing profile: file not found ({path}).");
                return;
            }

            var profile = JsonSerializer.Deserialize<WeightProfile>(File.ReadAllText(path));
            if (profile == null) {
                LogError("Error importing profile: file was empty or invalid.");
                return;
            }

            // Name the profile after the file, deduped against existing names.
            string baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported Profile";
            string name = baseName;
            int i = 2;
            while (Settings.Profiles.Profiles.ContainsKey(name))
                name = $"{baseName} {i++}";

            Settings.Profiles.SaveCurrentProfile();
            Settings.Profiles.Profiles[name] = profile;
            Settings.Profiles.ActiveProfile = name;
            Settings.Profiles.LoadProfile(name);

            weightsDirty = true;
            LogMessage($"Imported profile '{name}' from {path} ({profile.Maps.Count} maps, {profile.Content.Count} content, {profile.Biomes.Count} biomes)");
        } catch (Exception e) {
            LogError("Error importing profile: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // Imports a settings file from the previous ("v1") version of the plugin, converting its structure
    // to the current one. The old layout differs: a top-level "MapTypes" (now "Maps"), separate
    // top-level "Biomes"/"MapContent" sections (now nested under Maps), an int-valued "Keybinds" shape
    // (now HotkeyNodeV2), and a vestigial "MapMods" section (dropped). The on-disk ExileCore2 settings
    // file also wraps everything in a "data" object. Map/content/biome weights+colors+favorites are
    // captured into a NEW profile (named after the file) and made active so existing profiles are left
    // intact; every other category is applied directly to the live settings. Each section is populated
    // in isolation so one malformed section can't abort the whole import.
    private void ConvertOldSettings(string path, string profileName = null) {
        try {
            if (!File.Exists(path)) {
                LogError($"Error importing old settings: file not found ({path}).");
                return;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) {
                LogError("Error importing old settings: file was empty or invalid.");
                return;
            }

            var root = Newtonsoft.Json.Linq.JObject.Parse(text);
            // On-disk settings wrap everything in "data"; a hand-made/exported file may not.
            var data = root["data"] as Newtonsoft.Json.Linq.JObject ?? root;

            // Preserve any unsaved live edits to the current active profile before we overwrite live state.
            Settings.Profiles.SaveCurrentProfile();

            // Non-weight categories apply directly to the live settings objects.
            PopulateOldSection(data["Enable"], Settings.Enable, "Enable");
            PopulateOldSection(data["Features"], Settings.Features, "Features");
            PopulateOldSection(data["Graphics"], Settings.Graphics, "Graphics");
            PopulateOldSection(data["Waypoints"], Settings.Waypoints, "Waypoints");
            ConvertOldKeybinds(data["Keybinds"] as Newtonsoft.Json.Linq.JObject);

            // Reshape MapTypes + the formerly top-level Biomes/MapContent into the current Maps object.
            if (data["MapTypes"] is Newtonsoft.Json.Linq.JObject mapTypes) {
                var mapsObj = (Newtonsoft.Json.Linq.JObject)mapTypes.DeepClone();
                if (data["Biomes"] is Newtonsoft.Json.Linq.JObject oldBiomes)
                    mapsObj["Biomes"] = oldBiomes;
                if (data["MapContent"] is Newtonsoft.Json.Linq.JObject oldContent)
                    mapsObj["Content"] = oldContent;
                PopulateOldSection(mapsObj, Settings.Maps, "Maps");
            }

            // Capture the just-imported map/content/biome state into a new profile and switch to it.
            // Auto-detect import passes an explicit name ("Original Settings"); the manual file-picker
            // leaves it null, so the profile is named after the chosen file.
            string baseName = profileName;
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "Imported";
            string name = baseName;
            int i = 2;
            while (Settings.Profiles.Profiles.ContainsKey(name))
                name = $"{baseName} {i++}";

            Settings.Profiles.Profiles[name] = new WeightProfile();
            Settings.Profiles.ActiveProfile = name;
            Settings.Profiles.SaveCurrentProfile(); // snapshot live (imported) -> the new profile

            weightsDirty = true;
            refreshCache = true;
            LogMessage($"Imported old settings from {path} into new profile '{name}'.");
        } catch (Exception e) {
            LogError("Error importing old settings: " + e.Message + "\n" + e.StackTrace);
        }
    }

    // PopulateObject one old-settings section onto a live target, isolated so a bad section doesn't
    // abort the whole import. Uses Newtonsoft (the serializer ExileCore2 settings round-trip through).
    private void PopulateOldSection(Newtonsoft.Json.Linq.JToken token, object target, string label) {
        if (token == null)
            return;
        try {
            // Use ExileCore2's settings serializer (SettingsContainer.jsonSettings) - it registers the
            // converters for ColorNode/ToggleNode/etc. Default Newtonsoft can't parse a ColorNode hex
            // string like "ffffffff" and would throw, aborting the whole section.
            Newtonsoft.Json.JsonConvert.PopulateObject(token.ToString(), target, SettingsContainer.jsonSettings);
        } catch (Exception e) {
            LogError($"Error importing old settings section '{label}': {e.Message}");
        }
    }

    // Old keybinds stored a single int (a System.Windows.Forms.Keys value) per bind; the current
    // settings use HotkeyNodeV2 ({ ValueV2: { Key, ControllerKey, ControllerModifierKey, Win } }).
    // Translate each int to the named key and populate. Binds removed in the rework simply no-op.
    private void ConvertOldKeybinds(Newtonsoft.Json.Linq.JObject oldKeybinds) {
        if (oldKeybinds == null)
            return;
        try {
            var converted = new Newtonsoft.Json.Linq.JObject();
            foreach (var prop in oldKeybinds.Properties()) {
                var valTok = prop.Value?["Value"];
                if (valTok == null)
                    continue;
                int vk = valTok.ToObject<int>();
                string keyName = ((System.Windows.Forms.Keys)vk).ToString();
                converted[prop.Name] = new Newtonsoft.Json.Linq.JObject {
                    ["ValueV2"] = new Newtonsoft.Json.Linq.JObject {
                        ["Key"] = keyName,
                        ["ControllerKey"] = "None",
                        ["ControllerModifierKey"] = "None",
                        ["Win"] = false
                    }
                };
            }
            Newtonsoft.Json.JsonConvert.PopulateObject(converted.ToString(), Settings.Keybinds, SettingsContainer.jsonSettings);
        } catch (Exception e) {
            LogError($"Error importing old settings section 'Keybinds': {e.Message}");
        }
    }

    // Same async-dialog pattern as the others, but open-only, for a pre-rework settings file.
    private void OpenOldSettingsFileDialogAsync() {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string chosen = NativeFileDialog.ShowOpen("Import Old Settings", DirectoryFullName);
                if (!string.IsNullOrEmpty(chosen))
                    pendingImportOldSettingsPath = chosen;
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    // Opens a native common dialog on a background STA thread for the full settings file. Does NOT block
    // the render thread; the selected path is picked up by ProcessPendingWeightFile on a later frame.
    private void OpenSettingsFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Settings", "exilemaps_settings.json", initialDir)
                    : NativeFileDialog.ShowOpen("Import Settings", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportSettingsPath = chosen;
                    else pendingImportSettingsPath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }

    // Same async-dialog pattern, but for a single weight profile file.
    private void OpenProfileFileDialogAsync(bool save) {
        if (weightDialogBusy)
            return;
        weightDialogBusy = true;

        var thread = new System.Threading.Thread(() => {
            try {
                string initialDir = DirectoryFullName;
                string defaultName = $"{Settings.Profiles.ActiveProfile}.json";
                string chosen = save
                    ? NativeFileDialog.ShowSave("Export Profile", defaultName, initialDir)
                    : NativeFileDialog.ShowOpen("Import Profile", initialDir);

                if (!string.IsNullOrEmpty(chosen)) {
                    if (save) pendingExportProfilePath = chosen;
                    else pendingImportProfilePath = chosen;
                }
            } catch (Exception e) {
                LogError("Error opening file dialog: " + e.Message);
            } finally {
                weightDialogBusy = false;
            }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
    }
    #endregion

    #region Map Processing
    ///MARK: RenderNode
    /// <summary>
    /// Renders a map node on the atlas panel.
    /// </summary>
    /// <param name="cachedNode"></param>
    // Node rendering runs as separate passes over the whole node set, one per z-layer, so the draw
    // order is globally fixed instead of per-node. Render calls these in order:
    //   1. DrawNodeLines  (connections + tower range)
    //   2. DrawMapNode    (the node fill circle)
    //   3. DrawNodeRings  (content rings)
    //   4. DrawNodeLabels (map name, weight, tower mod boxes)
    // Then waypoint lines/icons/arrows draw on top (see Render). This guarantees a later node's line
    // never lands over an earlier node's circle/ring/label, which was the source of the flicker.
    private void DrawNodeLines(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawConnections(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node lines: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeRings(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            var ringCount = 0;
            // Draw a ring for every content present on this node (was a hardcoded whitelist that
            // silently dropped any content type not listed, e.g. "Power of Faith").
            foreach (var contentName in cachedNode.Content.Keys)
                ringCount += DrawContentRings(cachedNode, nodeCurrentPosition, ringCount, contentName);
        } catch (Exception e) {
            LogError("Error drawing node rings: " + e.Message + " - " + e.StackTrace);
        }
    }

    private void DrawNodeLabels(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            DrawMapName(cachedNode, nodeCurrentPosition);
            DrawWeight(cachedNode, nodeCurrentPosition);
        } catch (Exception e) {
            LogError("Error drawing node labels: " + e.Message + " - " + e.StackTrace);
        }
    }
    #endregion

    #region Debugging
    private void DoDebugging() {
        mapCache.TryGetValue(GetClosestNodeToCursor().Coordinates, out Node cachedNode);
        LogMessage(cachedNode.ToString());

    }

    private void DrawDebugging(Node cachedNode) {
        var rect = cachedNode.MapNode.Element.GetClientRect();
        string debugText = cachedNode.DebugText() + $"Size: {rect.Width:0} x {rect.Height:0}\n";
        using (Graphics.SetTextScale(1.0f))
            DrawCenteredTextWithBackground(debugText, rect.Center, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

    }

    // Map types are scraped from the game's endgame map file list (GameController.Files.EndgameMaps)
    // rather than the live atlas, so the full set is known without visiting/seeing nodes. The json seed
    // (LoadDefaultMaps) still supplies curated Weight/Color/Highlight/Biomes for existing types; this
    // only adds missing types and refreshes their IDs. Returns false if the file list isn't loaded yet
    // (e.g. called before the game finishes loading files) so the caller can retry.
    private bool UpdateMapData(bool writeToFile = true) {
      try {
        // Collapse any entries that share a display Name into a single row (union of ids). Different map
        // ids can legitimately share a Name (e.g. the various "Precursor Tower" towers); we show one row
        // per Name. Node matching falls back to MatchID(IDs), so every merged id still resolves.
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

            // Existing map-type matching strips the _NoBoss suffix to form the key (see CacheNewMapNode).
            var shortID = id.Replace("_NoBoss", "");

            // Merge identically-named maps into a single entry, collecting all their ids. The json seed
            // and prior scrapes already store Name, so a Name match covers both. Node matching falls back
            // to MatchID(IDs), so every merged id still resolves even though the entry has one key.
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

    // Scans the whole atlas for content types (AtlasPanelNode.ContentIdentity[].Id) and adds any not
    // already in the Content data source, then writes content.json. Content types come from the game's
    // EndgameMapContent file list (Name + Id), with icons from EndgameMapContentVisualIdentity (matched
    // by Id). New entries are keyed by Id; legacy entries keyed by the spaced Name are migrated to the
    // Id key (deduped via space-stripped compare). Returns false if the file list isn't loaded yet.
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

    // Biomes come from the game's EndgameMapBiomes file list (Id only). Keyed by Id, matching the
    // existing biomes.json convention. Returns false if the file list isn't loaded yet.
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

    #endregion

    // Find the shortest route from a waypoint destination outward to the nearest completed (visited)
    // map. BFS expands from the destination and stops at the first visited node reached on each branch,
    // so the returned path contains exactly ONE completed map — the anchor at index 0. Among all routes
    // of the minimum graph distance, the one with the greatest summed map weight is chosen (so equal-
    // length paths prefer higher-value maps). Returns the path ordered
    // [completed anchor, ...incomplete maps..., destination] plus its summed weight, or (null, 0) if no
    // completed map is reachable.
    private (List<Node> path, float weight) FindPathToNearestCompleted(Node destination)
    {
        if (destination == null)
            return (null, 0f);

        // Destination itself already completed — trivial one-node path.
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

            // FIFO guarantees all nodes at anchorDist are dequeued before any further-out node; once we
            // step past that distance nothing closer/equal can appear, so stop.
            if (cd > anchorDist)
                break;

            // A completed map (other than the destination): candidate anchor. Do NOT expand its
            // neighbors — the route ends here so only one completed map is ever on the path. Keep the
            // nearest anchor, and among equally-near anchors the highest-weight route.
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
                // Exclude completed maps from the path weight: visited nodes are pinned to 500, and the
                // only visited node on a path is the anchor you already stand on — counting its 500 would
                // swamp the real total. Weight = destination + intermediate incomplete maps only.
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
                    // Equal-distance alternative with a higher summed weight — prefer it.
                    best[neighbor.Coordinates] = nw;
                    parent[neighbor.Coordinates] = current;
                }
            }
        }

        if (anchor == null)
            return (null, 0f);

        // Walk parents from the anchor back to the destination. Parent links point toward the
        // destination (parent[destination] == null), so this yields [anchor, ..., destination] directly.
        var path = new List<Node>();
        for (Node n = anchor; n != null; n = parent[n.Coordinates])
            path.Add(n);

        return (path, anchorWeight);
    }

    // Multi-source BFS seeded with every visited node at distance 0. Each unvisited node's value is
    // the minimum number of steps to reach the explored region (same semantics as a waypoint's
    // PathFromStart step count, but computed for the whole graph in a single O(V+E) pass so the
    // atlas list can sort/display steps without a per-node BFS). Unreachable nodes are omitted.
    // Logs a swallowed exception only when Debug Mode is on. Hot draw/scrape paths read live game
    // memory that can shift mid-frame; we must keep swallowing per-item so one bad read never aborts the
    // whole frame, but routing those swallows through here makes them diagnosable on demand (enable
    // Debug Mode) instead of vanishing silently.
    private void DebugSwallow(string context, Exception e)
    {
        if (Settings.Features.DebugMode)
            LogError($"{context}: {e.Message}");
    }

    private Dictionary<Vector2i, int> ComputeStepCounts()
    {
        // Memoized against the cache version: the multi-source BFS only changes when the cache is
        // refreshed (which is what flips visited flags / neighbors), so reuse it across render frames.
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

    // Memoized atlas-panel list: non-visited nodes filtered + sorted + capped, recomputed only when the
    // cache version or any of the panel's filter/sort inputs change (otherwise the panel would rerun the
    // whole pipeline every render frame). Returns an ordered List<Node> ready to enumerate.
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
        // Measure distance from the actual mouse cursor, not UIHoverElement: over a revealed node the
        // hover element is that node, but over fog/unrevealed atlas the hover element is the panel
        // background, which would snap to whatever node sits near the panel center instead of the one
        // under the cursor. The raw cursor position works for revealed and fog nodes alike (we draw on
        // both, so both are in Descriptions/mapCache).
        // ImGui's mouse pos is screen-space (same coords as GetClientRect) and stays valid over fog,
        // unlike GameController.IngameState.MousePosX/Y (currently always 0) or UIHoverElement (which
        // is the panel background when not over a revealed node).
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

    #region Map Cache


    public void RefreshMapCache(bool clearCache = false)
    {
        refreshingCache = true;
        cacheRefreshProgress = 0f;

        if (clearCache)
            lock (mapCacheLock)            
                mapCache.Clear();

        List<AtlasNodeDescription> atlasNodes = [.. AtlasPanel.Descriptions];

        // Snapshot connection points once and build O(1) forward + reverse lookups, instead of
        // scanning AtlasPanel.Points twice per node (previously O(N^2) over the whole atlas, with a
        // fresh game-memory read on every scan).
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

        // Start timer
        var timer = new Stopwatch();
        timer.Start();
        long count = 0;
        int total = atlasNodes.Count;
        int processed = 0;
        // Phase 1: cache every node first. Connections are built in a second pass below — building them
        // inline here would miss forward edges to nodes not yet cached (the neighbor lookup fails), so a
        // freshly-opened atlas had one-directional adjacency and waypoint BFS dead-ended until a manual
        // refresh backfilled the missing edges.
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                count += RefreshCachedMapNode(node, cachedNode);
            else
                count += CacheNewMapNode(node);

            // Cap the node loop at 0.8; the rest covers connections + weight/waypoint passes.
            cacheRefreshProgress = total > 0 ? 0.8f * (++processed) / total : 0.8f;
        }

        // Phase 2: now that all nodes exist, resolve neighbor references (both directions complete).
        processed = 0;
        foreach (var node in atlasNodes) {
            if (mapCache.TryGetValue(node.Coordinate, out Node cachedNode))
                CacheMapConnections(cachedNode, forwardPoints, reverseNeighbors);
            cacheRefreshProgress = total > 0 ? 0.8f + 0.1f * (++processed) / total : 0.9f;
        }
        // stop timer
        timer.Stop();
        long time = timer.ElapsedMilliseconds;
        float average = (float)time / count;
        //LogMessage($"Map cache refreshed in {time}ms, {count} nodes processed, average time per node: {average:0.00}ms");

        RecalculateWeights();
        //LogMessage($"Max Map Weight: {maxMapWeight}, Min Map Weight: {minMapWeight}");

        SyncFavoriteWaypoints();
        UpdateWaypointPaths();

        // Invalidate the memoized step-counts / atlas-panel list: the cache contents (and visited flags
        // / neighbors that drive them) just changed.
        mapCacheVersion++;

        cacheRefreshProgress = 1f;
        refreshingCache = false;
        refreshCache = false;
        lastRefresh = DateTime.Now;
    }

    private void DrawWaypointPath(Waypoint waypoint)
    {
        // Snapshot the path reference once. UpdateWaypointPaths rebuilds/nulls this field on the
        // cache-refresh job thread, so re-reading waypoint.PathFromStart per access can NRE mid-draw
        // (e.g. set to null between the Count check and an index access).
        var path = waypoint.PathFromStart;
        if (path == null || path.Count <= 1)
            return;

        var g = Settings.Graphics;
        float dashLen = g.WaypointDashLength;
        float gap = g.WaypointDashGap;
        float period = dashLen + gap;
        // Color the path to match its own waypoint marker (each waypoint gets a distinct color).
        var color = waypoint.Color;
        bool dashed = dashLen > 0f && period > 0f;
        // Tie the textured style to the global shapes/textures toggle: icons on = animated texture,
        // off = plain flat line. Falls back to a line too if the selected sprite didn't load.
        IntPtr pathTexId = default;
        bool textured = g.UseNodeIcons && pathTextureIds.TryGetValue(g.WaypointPathTexture, out pathTexId);
        // Textured path gets a thickness multiplier so the sprite reads at a usable size; a plain line
        // keeps the raw width.
        float width = textured ? g.WaypointLineWidth * g.WaypointPathTextureScale : g.WaypointLineWidth;

        // Animated phase scrolls the dash pattern toward the destination. Arc length increases from
        // path[0] (the anchor near explored territory) to the last node (the waypoint), so subtracting
        // a growing phase makes the "on" dashes march in the +arc direction = toward the waypoint.
        // Phase derives from the per-frame TickCount (no wall-clock needed) and is taken mod period so
        // the float never grows unbounded.
        float phase = (dashed && g.WaypointPathAnimated && g.WaypointDashSpeed > 0f)
            ? (TickCount * g.WaypointDashSpeed.Value) % period
            : 0f;

        // Cumulative arc length along the WHOLE polyline (measured on the unclipped node centers). The
        // dash pattern is positioned against this global arc so it stays continuous across corners and
        // does not jump when a segment endpoint gets clipped off-screen.
        float arc = 0f;

        for (int i = 0; i < path.Count - 1; i++)
        {
            var currentNode = path[i];
            var nextNode = path[i + 1];

            // Guard the whole chain: a refresh can swap a node's MapNode/Element out from under us.
            // An unguarded read here throws into Render's waypoint loop and aborts EVERY remaining
            // waypoint's path + arrow for the frame (reads as flicker), so swallow per-segment.
            if (currentNode?.MapNode?.Element == null || nextNode?.MapNode?.Element == null)
                continue;

            Vector2 start, end;
            try {
                start = currentNode.MapNode.Element.GetClientRect().Center;
                end = nextNode.MapNode.Element.GetClientRect().Center;
            } catch (Exception e) {
                DebugSwallow("DrawWaypointPath: segment rect read", e);
                continue;
            }

            float segArcStart = arc;
            arc += (end - start).Length();

            // Clip the segment to the visible screen rect instead of dropping any segment that has
            // an off-screen endpoint. This keeps the path connected right up to the screen edge as
            // it heads toward an off-screen destination (the old IsLineVisible required BOTH ends
            // on screen, so the most useful edge-crossing segment never drew).
            if (!ClipLineToScreen(start, end, out Vector2 clippedStart, out Vector2 clippedEnd))
                continue;

            // Still skip segments that touch/cross an excluded UI rect (tooltip, title bar, etc.).
            bool crossesExcluded = false;
            foreach (var rect in cachedExcludeRects)
                if (rect.Contains(clippedStart) || rect.Contains(clippedEnd) ||
                    SegmentIntersectsRect(clippedStart, clippedEnd, rect)) {
                    crossesExcluded = true;
                    break;
                }
            if (crossesExcluded)
                continue;

            if (!dashed) {
                if (textured)
                    DrawTexturedSegment(clippedStart, clippedEnd, width, color, pathTexId);
                else
                    Graphics.DrawLine(clippedStart, clippedEnd, width, color);
                continue;
            }

            // Map the clipped endpoints back onto the global arc so the dash pattern lines up across
            // this and neighboring segments. Clipping happens along the same line, so the clipped
            // endpoints' arc offsets are just their distances from the unclipped segment start.
            float clipArcStart = segArcStart + (clippedStart - start).Length();
            float clipArcEnd = segArcStart + (clippedEnd - start).Length();
            DrawDashedSegment(clippedStart, clippedEnd, clipArcStart, clipArcEnd, width, color, dashLen, period, phase, textured, pathTexId);
        }
    }

    // Draws the "on" dash intervals of a single straight segment whose endpoints sit at arc offsets
    // [arcStart, arcEnd] along the overall path. A point at arc a is "on" where
    // ((a - phase) mod period) is in [0, dashLen). Walks dash-start boundaries and clamps each dash to
    // the segment, so a dash that straddles a corner or the screen edge still renders its visible part.
    private void DrawDashedSegment(Vector2 a, Vector2 b, float arcStart, float arcEnd,
        float width, Color color, float dashLen, float period, float phase, bool textured, IntPtr texId)
    {
        float span = arcEnd - arcStart;
        if (span <= 0.01f)
            return;

        Vector2 dir = (b - a) / span; // unit vector (span == |b - a| for a clipped straight segment)

        // First dash-start at or before arcStart, aligned to the phase lattice.
        float firstDash = MathF.Floor((arcStart - phase) / period) * period + phase;
        for (float dashStart = firstDash; dashStart < arcEnd; dashStart += period)
        {
            float on0 = MathF.Max(dashStart, arcStart);
            float on1 = MathF.Min(dashStart + dashLen, arcEnd);
            if (on1 <= on0)
                continue;
            Vector2 p0 = a + dir * (on0 - arcStart);
            Vector2 p1 = a + dir * (on1 - arcStart);
            if (textured)
                DrawTexturedSegment(p0, p1, width, color, texId);
            else
                Graphics.DrawLine(p0, p1, width, color);
        }
    }

    // Draws the chosen path sprite stretched over a straight segment as a width-thick rotated quad
    // (UV 0..1, so the sprite maps once per call). Used per dash, so a dash sprite (chevron, glow, etc.)
    // tiles along the path. No-op on a degenerate segment.
    private void DrawTexturedSegment(Vector2 a, Vector2 b, float width, Color color, IntPtr texId)
    {
        Vector2 dir = b - a;
        float len = dir.Length();
        if (len < 0.001f)
            return;
        dir /= len;
        Vector2 n = new Vector2(-dir.Y, dir.X) * (width * 0.5f); // half-width perpendicular offset
        Graphics.DrawQuad(texId, a + n, b + n, b - n, a - n, color);
    }

    // Cohen–Sutherland clip of a segment to the cached on-screen rect. Returns false if the segment
    // lies entirely outside the screen; on true, clippedStart/clippedEnd are the visible portion.
    private bool ClipLineToScreen(Vector2 a, Vector2 b, out Vector2 clippedStart, out Vector2 clippedEnd)
    {
        RectangleF r = cachedScreenRect;
        float xMin = r.Left, xMax = r.Right, yMin = r.Top, yMax = r.Bottom;

        int Code(Vector2 p) {
            int c = 0;
            if (p.X < xMin) c |= 1; else if (p.X > xMax) c |= 2;
            if (p.Y < yMin) c |= 4; else if (p.Y > yMax) c |= 8;
            return c;
        }

        Vector2 p0 = a, p1 = b;
        int c0 = Code(p0), c1 = Code(p1);
        clippedStart = a;
        clippedEnd = b;

        while (true) {
            if ((c0 | c1) == 0) { clippedStart = p0; clippedEnd = p1; return true; } // both inside
            if ((c0 & c1) != 0) return false;                                        // both off the same side

            int co = c0 != 0 ? c0 : c1;
            Vector2 p;
            if ((co & 8) != 0)      p = new Vector2(p0.X + (p1.X - p0.X) * (yMax - p0.Y) / (p1.Y - p0.Y), yMax);
            else if ((co & 4) != 0) p = new Vector2(p0.X + (p1.X - p0.X) * (yMin - p0.Y) / (p1.Y - p0.Y), yMin);
            else if ((co & 2) != 0) p = new Vector2(xMax, p0.Y + (p1.Y - p0.Y) * (xMax - p0.X) / (p1.X - p0.X));
            else                    p = new Vector2(xMin, p0.Y + (p1.Y - p0.Y) * (xMin - p0.X) / (p1.X - p0.X));

            if (co == c0) { p0 = p; c0 = Code(p0); }
            else          { p1 = p; c1 = Code(p1); }
        }
    }

    private void RecalculateWeights() {

        // Weight edits change the atlas-panel Weight sort without touching cache structure; bump this so
        // the memoized list refreshes (step counts, which don't depend on weight, stay cached).
        weightsRecalcVersion++;

        if (mapCache.Count == 0)
            return;

        // Snapshot under the lock — the background refresh job mutates mapCache concurrently.
        List<float> weights;
        lock (mapCacheLock)
            weights = mapCache.Values.Where(x => !x.IsVisited).Select(x => x.Weight).Distinct().ToList();

        if (weights.Count == 0) {
            minMapWeight = 0;
            maxMapWeight = 0;
            return;
        }

        // Single ascending sort, then index both ends (was two separate OrderBy passes).
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
            ParentAddress = node.Address,
            Address = node.Element.Address,
            Coordinates = node.Coordinate,
            Name = node.Element.Area.Name,
            Id = mapId,
            MapNode = node,
            MapType = ResolveMapType(shortID, mapId)
        };

        // Set Content
        if (!newNode.IsVisited) {
            // Check if the map has content
            try {

                AddNodeContentFromIdentity(node, newNode);
                SetAtlasPassive(node, newNode);

            } catch (Exception e) {
                LogError($"Error getting Content for map type {node.Address.ToString("X")}: " + e.Message);
            }
            
            // Biome data now comes from atlas nodes directly, not from map config
            try {
                // Biome associations removed — nodes expose biome data via AtlasPanel
            }   catch (Exception e) {
                LogError($"Error getting Biomes for map type {mapId}: " + e.Message);
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

    // Resolves a node's map type: direct hit on the short id, else an O(1) lookup in the id index
    // (rebuilt only when the map dictionary grows), else a blank Map. Replaces a per-node O(n) scan.
    private Map ResolveMapType(string shortId, string fullId)
    {
        if (Settings.Maps.Maps.TryGetValue(shortId, out Map mapType))
            return mapType;

        EnsureMapIdIndex();
        if (!string.IsNullOrWhiteSpace(fullId) && mapIdIndex.TryGetValue(fullId, out var byId))
            return byId;

        return new Map();
    }

    // (Re)builds the id -> Map index when the map dictionary's entry count changes (entries are only
    // added, via scrape). First id wins on collisions, matching the old Where(...).FirstOrDefault().
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
    // Named content used to live on AtlasPanelNode.Content; it is now exposed via
    // AtlasPanelNode.ContentIdentity, a list whose elements carry an .Id (e.g. "PowerfulMapBoss").
    // The Id is space-stripped, while ContentTypes keys/names have spaces ("Powerful Map Boss"),
    // so match by stripping spaces from the key.
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

    // A map grants an atlas passive point when its AtlasEntry.PassiveSkill.Id contains "Inside"
    // and the node hasn't been completed yet.
    private void SetAtlasPassive(AtlasNodeDescription node, Node toNode) {
        try {
            var passiveId = node.Element?.AtlasEntry?.PassiveSkill?.Id;
            bool completed = node.Element?.IsCompleted ?? false;
            bool grantsInside = passiveId?.Contains("Inside", StringComparison.OrdinalIgnoreCase) ?? false;
            toNode.GivesAtlasPoint = grantsInside && !completed;
            toNode.HasAtlasQuest = (passiveId?.Contains("AtlasQuest", StringComparison.OrdinalIgnoreCase) ?? false) && !completed;
        }
        catch (Exception e) { toNode.GivesAtlasPoint = false; toNode.HasAtlasQuest = false; DebugSwallow("SetAtlasPassive", e); }
    }
    #endregion

    #region Drawing Functions
    //MARK: DrawConnections
    /// <summary>
    /// Draws lines between a map node and its connected nodes on the atlas.
    /// </summary>
    /// <param name="WorldMap">The atlas panel containing the map nodes and their connections.</param>
    /// <param name="cachedNode">The map node for which connections are to be drawn.</param>
    /// 
    private void DrawConnections(Node cachedNode, RectangleF nodeCurrentPosition)
    {       
         foreach (Vector2i coordinates in cachedNode.GetNeighborCoordinates())
        {
            if (coordinates == default)
                continue;
            
            if (!mapCache.TryGetValue(coordinates, out Node destinationNode))
                continue;
                
            if (!Settings.Features.DrawVisitedNodeConnections && (destinationNode.IsVisited || cachedNode.IsVisited))
                continue;

            if ((!Settings.Features.DrawHiddenNodeConnections || !Settings.Features.ProcessHiddenNodes) && (!destinationNode.IsVisible || !cachedNode.IsVisible))
                continue;
            
            var destinationPos = destinationNode.MapNode.Element.GetClientRect();

            if (!IsLineVisible(nodeCurrentPosition.Center, destinationPos.Center))
                continue;

            if (Settings.Graphics.DrawGradientLines) {
                Color sourceColor = cachedNode.IsVisited ? Settings.Graphics.VisitedLineColor : cachedNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                Color destinationColor = destinationNode.IsVisited ? Settings.Graphics.VisitedLineColor : destinationNode.IsUnlocked ? Settings.Graphics.UnlockedLineColor : Settings.Graphics.LockedLineColor;
                
                if (sourceColor == destinationColor)
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor);
                else
                    Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, sourceColor, destinationColor);

            } else {
                var color = Settings.Graphics.LockedLineColor;

                if (destinationNode.IsUnlocked || cachedNode.IsUnlocked)
                    color = Settings.Graphics.UnlockedLineColor;
                
                if (destinationNode.IsVisited && cachedNode.IsVisited)
                    color = Settings.Graphics.VisitedLineColor;

                Graphics.DrawLine(nodeCurrentPosition.Center, destinationPos.Center, Settings.Graphics.MapLineWidth, color);
            }
            
        }
    }

    /// MARK: HighlightMapContent
    /// <summary>
    /// Highlights a map node by drawing a circle around it if certain conditions are met.
    /// </summary>
    /// <param name="cachedNode">The map node to be highlighted.</param>
    /// <param name="Count">The count used to calculate the radius of the circle.</param>
    /// <param name="Content">The content string to check within the map node's elements.</param>
    /// <param name="Draw">A boolean indicating whether to draw the circle or not.</param>
    /// <param name="color">The color of the circle to be drawn.</param>
    /// <returns>Returns 1 if the circle is drawn, otherwise returns 0.</returns>
    private int DrawContentRings(Node cachedNode, RectangleF nodeCurrentPosition, int Count, string Content)
    {
        if ((cachedNode.IsVisited && !cachedNode.IsAttempted) || 
            (!Settings.Maps.Content.ShowRingsOnLockedNodes && !cachedNode.IsUnlocked) || 
            (!Settings.Maps.Content.ShowRingsOnUnlockedNodes && cachedNode.IsUnlocked) || 
            (!Settings.Maps.Content.ShowRingsOnHiddenNodes && !cachedNode.IsVisible) ||         
            !cachedNode.Content.TryGetValue(Content, out Content cachedContent) || 
            !cachedNode.MapType.Highlight || !cachedContent.Highlight)            
            return 0;

        float radius = (Count * Settings.Graphics.RingWidth) + 1 + ((nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 2 * Settings.Graphics.RingRadius);
        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            // User-tunable scale (default 1.0 = original ring size).
            float d = radius * 2f * Settings.Graphics.ContentRingIconScale;
            DrawNodeSprite(nodeCurrentPosition.Center, d, d, SpriteIcon.CircleOutline, cachedContent.Color);
        }
        else
            Graphics.DrawCircle(nodeCurrentPosition.Center, radius, cachedContent.Color, Settings.Graphics.RingWidth, 32);

        return 1;
    }
    

    /// MARK: DrawMapNode
    /// Draws a highlighted circle around a map node on the atlas if the node is configured to be highlighted.
    /// </summary>
    /// <param name="mapNode">The atlas node description containing information about the map node to be drawn.</param>   
    private void DrawMapNode(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.HighlightMapNodes || cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;
            
        // "Special" maps have wider node art. Skip the solid circle (it would cover the map art) —
        // they get an icon drawn above the node by DrawSpecialIndicator instead.
        if (Settings.Graphics.ShowSpecialMapIndicator && nodeCurrentPosition.Width > Settings.Graphics.SpecialMapWidthThreshold)
            return;

        var radius = (nodeCurrentPosition.Right - nodeCurrentPosition.Left) / 4 * Settings.Graphics.NodeRadius;
        var weight = cachedNode.Weight;
        Color color = cachedNode.MapType.ColorNodesByWeight ? ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, (weight - minMapWeight) / (maxMapWeight - minMapWeight)) : cachedNode.MapType.NodeColor;

        if (customIconsLoaded && Settings.Graphics.UseNodeIcons) {
            DrawNodeSprite(nodeCurrentPosition.Center, radius * 2f, radius * 2f, cachedNode.MapType.Icon, color);
        } else {
            Graphics.DrawCircleFilled(nodeCurrentPosition.Center, radius, color, 16);
        }
    }

    // Normalized UV RectangleF for a custom-atlas icon, adapting SpriteAtlas's corner-pair UVs to the
    // RectangleF (x, y, w, h) form ExileCore's Graphics.DrawImage expects.
    private static RectangleF GetSpriteUV(SpriteIcon icon)
    {
        var (uv0, uv1) = SpriteAtlas.GetUVPair(icon);
        return new RectangleF(uv0.X, uv0.Y, uv1.X - uv0.X, uv1.Y - uv0.Y);
    }

    // True once the custom sprite atlas PNG has been found and loaded.
    private bool CustomIconsAvailable => customIconsLoaded;

    /// MARK: DrawNodeSprite
    /// Draws a custom-atlas sprite centered at <paramref name="center"/>. IconFlatten vertically squashes
    /// it (shorter dest rect) so a round sprite reads as a flat disc lying on the tilted atlas plane.
    /// Uses Graphics.DrawImage so it layers like every other node draw (above lines, below labels/windows).
    private void DrawNodeSprite(Vector2 center, float width, float height, SpriteIcon icon, Color color, bool allowFlatten = true)
    {
        float h = allowFlatten ? height * (1f - Settings.Graphics.IconFlatten) : height;
        Graphics.DrawImage(CustomIconsName, new RectangleF(center.X - width / 2f, center.Y - h / 2f, width, h), GetSpriteUV(icon), color);
    }

    /// MARK: DrawSpecialIndicator
    /// Draws an icon above "special" map nodes (wider node art) instead of covering them with a fill.
    private void DrawSpecialIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowSpecialMapIndicator || !Settings.Maps.HighlightMapNodes ||
                cachedNode.IsVisited || cachedNode.MapType == null || !cachedNode.MapType.Highlight ||
                nodeCurrentPosition.Width <= Settings.Graphics.SpecialMapWidthThreshold)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.SpecialMapIconScale;

            // Position above the node by a fixed offset from its center (independent of node size).
            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(iconSize.X / 2, Settings.Graphics.SpecialMapIconOffset + iconSize.Y / 2);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, Settings.Graphics.SpecialMapIcon, Settings.Graphics.SpecialMapColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteHexagon), Settings.Graphics.SpecialMapColor);
        } catch (Exception e) {
            LogError("Error drawing special map indicator: " + e.Message);
        }
    }

    // Silver tint for the atlas-point marker.
    private static readonly Color AtlasPointColor = Color.FromArgb(255, 200, 200, 205);

    /// MARK: DrawAtlasPointIndicator
    /// Draws a small silver Star8 just above nodes whose map grants an atlas passive point.
    private void DrawAtlasPointIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasPointIndicator || !cachedNode.GivesAtlasPoint || cachedNode.IsVisited || !customIconsLoaded)
                return;

            float size = Settings.Graphics.AtlasIndicatorSize;
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Star8, AtlasPointColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas point indicator: " + e.Message);
        }
    }

    // Golden tint for the atlas-quest marker.
    private static readonly Color AtlasQuestColor = Color.FromArgb(255, 255, 200, 40);

    /// MARK: DrawAtlasQuestIndicator
    /// Draws a small golden exclamation just above nodes that have atlas quest content.
    private void DrawAtlasQuestIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!Settings.Graphics.ShowAtlasQuestIndicator || !cachedNode.HasAtlasQuest || cachedNode.IsVisited || !customIconsLoaded)
                return;

            float size = Settings.Graphics.AtlasIndicatorSize;
            // Offset right of the atlas-point star (which sits centered above the node) so both can show.
            Vector2 center = new Vector2(nodeCurrentPosition.Center.X + size, nodeCurrentPosition.Center.Y - nodeCurrentPosition.Height / 2f - size / 2f - 2f);
            DrawNodeSprite(center, size, size, SpriteIcon.Exclamation, AtlasQuestColor, allowFlatten: false);
        } catch (Exception e) {
            LogError("Error drawing atlas quest indicator: " + e.Message);
        }
    }

    /// MARK: DrawFavoriteIndicator
    /// Draws a star icon above nodes whose map type is flagged as a favorite.
    private void DrawFavoriteIndicator(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        try {
            if (!cachedNode.IsFavorited || cachedNode.IsVisited)
                return;

            Vector2 iconSize = new Vector2(48, 48) * Settings.Graphics.FavoriteIconScale;

            // Position above the node (mirrors the waypoint icon offset).
            Vector2 iconPosition = nodeCurrentPosition.Center - new Vector2(0, nodeCurrentPosition.Height / 2 + 20);
            iconPosition -= new Vector2(iconSize.X / 2, iconSize.Y);

            RectangleF iconRect = new RectangleF(iconPosition.X, iconPosition.Y, iconSize.X, iconSize.Y);
            if (customIconsLoaded)
                DrawNodeSprite(iconRect.Center, iconRect.Width, iconRect.Height, SpriteIcon.Star5, Settings.Graphics.FavoriteColor, allowFlatten: false);
            else
                Graphics.DrawImage(IconsFile, iconRect, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteStar), Settings.Graphics.FavoriteColor);
        } catch (Exception e) {
            LogError("Error drawing favorite indicator: " + e.Message);
        }
    }

    //DrawMapNode
    private void DrawWeight(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.DrawWeightOnMap ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;  

        // Normalized position in the clipped weight range — drives only the text color gradient.
        float norm = (maxMapWeight - minMapWeight) > 0
            ? (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight)
            : 0.5f;
        norm = Math.Clamp(norm, 0f, 1f);

        float offsetX = Settings.Maps.ShowMapNames ? (Graphics.MeasureText(cachedNode.Name.ToUpper()).X / 2) + 30 : 50;
        Vector2 position = new(nodeCurrentPosition.Center.X + offsetX + Settings.Graphics.MapNameOffsetX, nodeCurrentPosition.Center.Y + Settings.Graphics.MapNameOffsetY);

        // Show the actual computed node weight, not a relative percentage.
        DrawCenteredTextWithBackground($"{cachedNode.Weight:0}", position, ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, norm), Settings.Graphics.BackgroundColor, true, 10, 3);
    }
    /// <summary>
    /// Draws the name of the map on the atlas.
    /// </summary>
    /// <param name="cachedNode">The atlas node description containing information about the map.</param>
    private void DrawMapName(Node cachedNode, RectangleF nodeCurrentPosition)
    {
        if (!Settings.Maps.ShowMapNames ||
            (!cachedNode.IsVisible && !Settings.Maps.ShowMapNamesOnHiddenNodes) ||
            (cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnUnlockedNodes) ||
            (!cachedNode.IsUnlocked && !Settings.Maps.ShowMapNamesOnLockedNodes) ||
            cachedNode.IsVisited || !cachedNode.MapType.Highlight)
            return;

        Color fontColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.NameColor : Settings.Graphics.FontColor;
        Color backgroundColor = Settings.Maps.UseColorsForMapNames ? cachedNode.MapType.BackgroundColor : Settings.Graphics.BackgroundColor;

        bool isSpecial = nodeCurrentPosition.Width > Settings.Graphics.SpecialMapWidthThreshold;

        if (isSpecial) {
            // Special maps get their own customizable name color (never weight-based).
            fontColor = Settings.Graphics.SpecialMapNameColor;
        } else if (cachedNode.MapType.UseWeightColorForName) {
            float weight = (cachedNode.Weight - minMapWeight) / (maxMapWeight - minMapWeight);
            fontColor = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor, Settings.Maps.GoodNodeColor, weight);
        }

        // Name text is always fully opaque regardless of the configured/weight color's alpha.
        fontColor = Color.FromArgb(255, fontColor.R, fontColor.G, fontColor.B);

        Vector2 namePosition = nodeCurrentPosition.Center + new Vector2(Settings.Graphics.MapNameOffsetX, Settings.Graphics.MapNameOffsetY);

        // Special maps render their name 20% larger.
        using (Graphics.SetTextScale(isSpecial ? 1.2f : 1.0f))
            DrawCenteredTextWithBackground(cachedNode.Name.ToUpper(), namePosition, fontColor, backgroundColor, true, 10, 3);
    }

    #endregion

    #region Misc Drawing
    /// <summary>
    /// Draws text with a background color at the specified position.
    /// </summary>
    /// <param name="text">The text to draw.</param>
    /// <param name="position">The position to draw the text at.</param>
    /// <param name="textColor">The color of the text.</param>
    /// <param name="backgroundColor">The color of the background.</param>
    /// Yes, I know exilecore has this built in, but I wanted padding and rounded corners.

    private void DrawCenteredTextWithBackground(string text, Vector2 position, Color color, Color backgroundColor, bool center = false, int xPadding = 0, int yPadding = 0)
    {
        if (!IsOnScreen(position))
            return;

        var boxSize = Graphics.MeasureText(text);

        boxSize += new Vector2(xPadding, yPadding);    

        if (center)
            position = position - new Vector2(boxSize.X / 2, boxSize.Y / 2);

        Graphics.DrawBox(position, boxSize + position, backgroundColor, 5.0f);       

        position += new Vector2(xPadding / 2, yPadding / 2);

        Graphics.DrawText(text, position, color);
    }

    private void DrawRotatedImage(IntPtr textureId, Vector2 position, Vector2 size, float angle, Color color)
    {
        Vector2 center = position + size / 2;

        float cosTheta = (float)Math.Cos(angle);
        float sinTheta = (float)Math.Sin(angle);

        Vector2 RotatePoint(Vector2 point)
        {
            Vector2 translatedPoint = point - center;
            Vector2 rotatedPoint = new Vector2(
                translatedPoint.X * cosTheta - translatedPoint.Y * sinTheta,
                translatedPoint.X * sinTheta + translatedPoint.Y * cosTheta
            );
            return rotatedPoint + center;
        }

        Vector2 topLeft = RotatePoint(position);
        Vector2 topRight = RotatePoint(position + new Vector2(size.X, 0));
        Vector2 bottomRight = RotatePoint(position + size);
        Vector2 bottomLeft = RotatePoint(position + new Vector2(0, size.Y));


        Graphics.DrawQuad(textureId, topLeft, topRight, bottomRight, bottomLeft, color);
        }
        private void DrawGradientLine(Vector2 start, Vector2 end, Color startColor, Color endColor, float lineWidth)
    {
        // No need to draw a gradient if the colors are the same
        if (startColor == endColor)
        {
            Graphics.DrawLine(start, end, Settings.Graphics.MapLineWidth, startColor);
            return;
        }

        int segments = 10; // Number of segments to create the gradient effect
        Vector2 direction = (end - start) / segments;

        for (int i = 0; i < segments; i++)
        {
            Vector2 segmentStart = start + direction * i;
            Vector2 segmentEnd = start + direction * (i + 1);

            float t = (float)i / segments;
            Color segmentColor = ColorUtils.InterpolateColor(startColor, endColor, t);

            Graphics.DrawLine(segmentStart, segmentEnd, lineWidth, segmentColor);

        }
    }

    #endregion
    private void UpdateWaypointPaths()
    {
        foreach (var waypoint in Settings.Waypoints.Waypoints.Values)
        {
            if (mapCache.TryGetValue(waypoint.Coordinates, out Node waypointNode))
            {
                // Shortest route from this waypoint out to the nearest completed map (one anchor only),
                // tie-broken by highest summed map weight.
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
    #region Helper Functions

    // Map tooltip is a WorldMap child identified by its popup texture; checked in IsOnScreen.
    // Matches any AtlasScreen "*Popup*" texture (e.g. AtlasMapNodePopup / AtlasMapNodePopupSelected).
    private const string TooltipTexturePrefix = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/";
    private static bool IsTooltipTexture(string textureName) =>
        textureName != null && textureName.StartsWith(TooltipTexturePrefix) && textureName.Contains("Popup");

    // Static atlas UI textures we never want to draw the overlay over (title bar, search box bg).
    // Matched by exact texture name anywhere under the WorldMap element tree.
    private static readonly HashSet<string> ExcludeTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/WorldMap/WorldmapTitleBar.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/Common/AtlasSearchBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapPinsWindow/MapPinLegendBG.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapLegend/LegendBg.dds",
    };

    // Recursively adds the client rect of any visible descendant whose texture is in
    // ExcludeTextureNames. Depth-limited so a malformed/deep tree can't stall the frame.
    private void AddExcludeRectsByTexture(ExileCore2.PoEMemory.Element element, int depth)
    {
        if (element == null || depth > 8 || !element.IsVisible)
            return;

        if (element.TextureName != null && ExcludeTextureNames.Contains(element.TextureName))
            cachedExcludeRects.Add(element.GetClientRect());

        foreach (var child in element.Children)
            AddExcludeRectsByTexture(child, depth + 1);
    }

    // Recomputes the on-screen rect and any visible map-tooltip rects once per render frame.
    // IsOnScreen then reads these cached values instead of re-reading panel/tooltip game memory
    // on every call (it's called dozens of times per node per frame).
    private void UpdateScreenBounds()
    {
        try {
            var size = GameController.Window.GetWindowRectangleTimeCache.Size;
            float left = 0;
            float right = size.X;

            if (UI.OpenRightPanel.IsVisible)
                right -= UI.OpenRightPanel.GetClientRect().Width;

            if (UI.OpenLeftPanel.IsVisible || WaypointPanelIsOpen)
                left += Math.Max(UI.OpenLeftPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Width);

            cachedScreenRect = new RectangleF(left, 0, right - left, size.Y);

            // Don't render over the map tooltip. Its child index varies, so identify it
            // by its popup texture (selected/unselected) instead of a fixed position.
            cachedExcludeRects.Clear();
            foreach (var tooltip in UI.WorldMap.Children) {
                if (tooltip == null || !tooltip.IsVisible)
                    continue;
                if (!IsTooltipTexture(tooltip.TextureName))
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedExcludeRects.Add(mapTooltip);
            }

            // Don't render over the atlas title bar / search box background.
            AddExcludeRectsByTexture(UI.WorldMap, 0);

            // Don't render over the fixed HUD elements (life/mana orbs, flask panel, skill bar).
            AddExcludeRect(UI.GameUI?.LifeOrb);
            AddExcludeRect(UI.GameUI?.ManaOrb);
            AddExcludeRect(UI.GameUI?.FlaskPanel?.Parent);
            AddExcludeRect(UI.SkillBar?.Parent);
        } catch (Exception e) {
            // Keep last good bounds on a failed memory read rather than blanking the overlay.
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

    // Adds a visible element's client rect to the no-draw set. Null/invisible elements are skipped.
    private void AddExcludeRect(ExileCore2.PoEMemory.Element element)
    {
        if (element == null || !element.IsVisible)
            return;
        cachedExcludeRects.Add(element.GetClientRect());
    }

    private bool IsOnScreen(Vector2 position)
    {
        foreach (var tooltip in cachedExcludeRects)
            if (tooltip.Contains(position))
                return false;

        return cachedScreenRect.Contains(position);
    }

    // A line is drawable only if both endpoints are on screen AND the segment doesn't
    // pass through a tooltip rect (a line can clear both endpoint checks yet still cross
    // the tooltip between them).
    private bool IsLineVisible(Vector2 start, Vector2 end)
    {
        if (!IsOnScreen(start) || !IsOnScreen(end))
            return false;

        foreach (var tooltip in cachedExcludeRects)
            if (SegmentIntersectsRect(start, end, tooltip))
                return false;

        return true;
    }

    // Segment vs axis-aligned rect. Endpoints are already known outside the rect (IsOnScreen
    // rejects points inside a tooltip), so test the segment against the four edges.
    private static bool SegmentIntersectsRect(Vector2 a, Vector2 b, RectangleF rect)
    {
        Vector2 tl = new(rect.Left, rect.Top);
        Vector2 tr = new(rect.Right, rect.Top);
        Vector2 br = new(rect.Right, rect.Bottom);
        Vector2 bl = new(rect.Left, rect.Bottom);

        return SegmentsIntersect(a, b, tl, tr) ||
               SegmentsIntersect(a, b, tr, br) ||
               SegmentsIntersect(a, b, br, bl) ||
               SegmentsIntersect(a, b, bl, tl);
    }

    private static bool SegmentsIntersect(Vector2 p1, Vector2 p2, Vector2 p3, Vector2 p4)
    {
        float d1 = Cross(p3, p4, p1);
        float d2 = Cross(p3, p4, p2);
        float d3 = Cross(p1, p2, p3);
        float d4 = Cross(p1, p2, p4);

        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
               ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }

    // Cross product of (b - a) x (c - a); sign tells which side of line ab point c is on.
    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    public float GetDistanceToNode(Node cachedNode)
    {
        return Vector2.Distance(screenCenter, cachedNode.MapNode.Element.GetClientRect().Center);
    }
    
    #endregion
    #region Waypoint Panel
    // MARK: Quick Edit
    // Node currently being edited via the hover hotkey, and whether the popup is showing.
    private Node quickEditNode;
    private bool quickEditOpen;

    private static void QuickColorEdit(string id, Color color, Action<Color> set) {
        Vector4 v = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        if (ImGui.ColorEdit4($"##{id}", ref v, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))
            set(Color.FromArgb((int)(v.W * 255), (int)(v.X * 255), (int)(v.Y * 255), (int)(v.Z * 255)));
    }

    /// MARK: DrawQuickEditPanel
    /// Floating popup (opened by the Quick Edit hotkey while hovering a node) to edit that node's
    /// map type and its content types inline. Edits the shared Map/Content instances, so changes
    /// persist to settings and apply live.
    private void DrawQuickEditPanel() {
        try {
            var map = quickEditNode?.MapType;
            if (quickEditNode == null || map == null) { quickEditOpen = false; quickEditNode = null; return; }

            Vector2 pos;
            try { pos = quickEditNode.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch (Exception e) { pos = screenCenter; DebugSwallow("waypoint position read", e); }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin($"Quick Edit###quickedit", ref quickEditOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextDisabled($"{quickEditNode.Name}  ({map.Name})");
                ImGui.Separator();

                bool highlight = map.Highlight;
                if (ImGui.Checkbox("Highlight##qe", ref highlight)) map.Highlight = highlight;
                ImGui.SameLine();
                bool fav = map.Favorite;
                if (ImGui.Checkbox("Favorite##qe", ref fav)) map.Favorite = fav;

                float weight = map.Weight;
                ImGui.SetNextItemWidth(220);
                if (ImGui.SliderFloat("Weight##qe", ref weight, -50f, 50f, "%.1f")) map.Weight = weight;

                QuickColorEdit("qe_node", map.NodeColor, c => map.NodeColor = c);
                ImGui.SameLine(); ImGui.Text("Node");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_name", map.NameColor, c => map.NameColor = c);
                ImGui.SameLine(); ImGui.Text("Name");
                ImGui.SameLine(); ImGui.Spacing(); ImGui.SameLine();
                QuickColorEdit("qe_bg", map.BackgroundColor, c => map.BackgroundColor = c);
                ImGui.SameLine(); ImGui.Text("Text BG");

                bool cbw = map.ColorNodesByWeight;
                if (ImGui.Checkbox("Color node by weight##qe", ref cbw)) map.ColorNodesByWeight = cbw;
                bool nbw = map.UseWeightColorForName;
                if (ImGui.Checkbox("Color name by weight##qe", ref nbw)) map.UseWeightColorForName = nbw;

                ImGui.Text("Icon"); ImGui.SameLine();
                SettingsHelpers.IconPicker("qeicon", map.Icon, i => map.Icon = i);

                if (quickEditNode.Content.Count > 0) {
                    ImGui.Separator();
                    ImGui.Text("Content");
                    foreach (var (cname, content) in quickEditNode.Content) {
                        ImGui.PushID($"qe_c_{cname}");
                        QuickColorEdit("col", content.Color, c => content.Color = c);
                        ImGui.SameLine();
                        bool ring = content.Highlight;
                        if (ImGui.Checkbox("Ring##c", ref ring)) content.Highlight = ring;
                        ImGui.SameLine();
                        bool cfav = content.Favorite;
                        if (ImGui.Checkbox("Fav##c", ref cfav)) content.Favorite = cfav;
                        ImGui.SameLine();
                        float cw = content.Weight;
                        ImGui.SetNextItemWidth(110);
                        if (ImGui.SliderFloat("##cw", ref cw, -100f, 100f, "%.0f")) content.Weight = cw;
                        ImGui.SameLine();
                        ImGui.TextUnformatted(content.Name);
                        ImGui.PopID();
                    }
                }

                ImGui.Separator();
                if (ImGui.Button("Close##qe")) quickEditOpen = false;
            }
            ImGui.End();

            if (!quickEditOpen) quickEditNode = null;
        } catch (Exception e) {
            LogError("Error drawing quick edit panel: " + e.Message);
            quickEditOpen = false;
            quickEditNode = null;
        }
    }

    // MARK: Node Debug
    // Node currently shown in the debug popup, and whether it's open.
    private Node debugNode;
    private bool debugNodeOpen;

    /// MARK: DrawNodeDebugPanel
    /// Floating popup (opened by the Debug Node hotkey while hovering a node) showing the node's
    /// debug text, element flags (as a binary string + per-flag), atlas-passive presence, biome id,
    /// and the content present. All game-memory reads are guarded so a stale read can't crash the HUD.
    private void DrawNodeDebugPanel() {
        try {
            if (debugNode == null) { debugNodeOpen = false; return; }
            var node = debugNode;
            var el = node.MapNode?.Element;

            Vector2 pos;
            try { pos = node.MapNode.Element.GetClientRect().Center + new Vector2(30, 0); }
            catch (Exception e) { pos = screenCenter; DebugSwallow("waypoint position read", e); }
            ImGui.SetNextWindowPos(pos, ImGuiCond.Appearing);
            ImGui.SetNextWindowSize(new Vector2(360, 0), ImGuiCond.Appearing);
            ImGui.SetNextWindowBgAlpha(0.93f);

            if (ImGui.Begin("Node Debug###nodedebug", ref debugNodeOpen, ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize)) {
                ImGui.TextUnformatted(node.DebugText(false));

                string parentAddr = $"{node.ParentAddress:X}";
                if (ImGui.SmallButton($"Copy Parent Address##nd")) ImGui.SetClipboardText(parentAddr);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip($"Copy {parentAddr} to clipboard");

                ImGui.Separator();

                // Named element status getters, rendered true=1/false=0 in this order:
                // CanTraverse, IsActive, IsCompleted, IsSaturated, IsScrollable, IsUnlocked,
                // IsValid, IsVisible, IsVisibleLocal, IsVisited, HasShinyHighlight.
                var status = new List<bool>();
                void Add(Func<bool> get) { try { status.Add(get()); } catch { status.Add(false); } }
                Add(() => el.CanTraverse);
                Add(() => el.IsActive);
                Add(() => el.IsCompleted);
                Add(() => el.IsSaturated);
                Add(() => el.IsScrollable);
                Add(() => el.IsUnlocked);
                Add(() => el.IsValid);
                Add(() => el.IsVisible);
                Add(() => el.IsVisibleLocal);
                Add(() => el.IsVisited);
                Add(() => el.HasShinyHighlight);
                ImGui.Text($"Status: {string.Concat(status.Select(b => b ? "1" : "0"))}");

                // Element.Flags is a separate List<bool> of the node's raw flag bits.
                string bits = "";
                try { bits = string.Concat(el.Flags.Select(b => b ? "1" : "0")); } catch { }
                ImGui.Text($"Flags: {bits}");

                bool passive = false;
                try { passive = el?.AtlasEntry?.PassiveSkill != null; } catch { }
                ImGui.Text($"AtlasEntry.PassiveSkill: {(passive ? "1" : "0")}");

                string biomeId = "";
                try { biomeId = node.MapNode.Element.Biome?.Id ?? ""; } catch { }
                ImGui.Text($"Biome.Id: {biomeId}");

                ImGui.Separator();
                ImGui.Text("Content:");
                if (node.Content.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var (_, c) in node.Content)
                        ImGui.TextUnformatted($"  {c.Name}");

                ImGui.Text("Biomes:");
                var biomeNames = node.Biomes.Where(x => x.Value != null).Select(x => x.Value.Name).ToList();
                if (biomeNames.Count == 0)
                    ImGui.TextDisabled("  (none)");
                else
                    foreach (var b in biomeNames)
                        ImGui.TextUnformatted($"  {b}");

                ImGui.Separator();
                if (ImGui.Button("Close##nd")) debugNodeOpen = false;
            }
            ImGui.End();

            if (!debugNodeOpen) debugNode = null;
        } catch (Exception e) {
            LogError("Error drawing node debug panel: " + e.Message);
            debugNodeOpen = false;
            debugNode = null;
        }
    }

    private void DrawWaypointPanel() {
        Vector2 panelSize = new Vector2(UI.SettingsPanel.GetClientRect().Width, UI.SettingsPanel.GetClientRect().Height);
        Vector2 panelPosition = UI.SettingsPanel.GetClientRect().TopLeft;
        ImGui.SetNextWindowPos(panelPosition, ImGuiCond.Always);
        ImGui.SetNextWindowSize(panelSize, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.8f);

        ImGui.Begin("WaypointPanel", ref WaypointPanelIsOpen, ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoMove);

        // Settings table
        if (ImGui.BeginTable("waypoint_top_table", 2, ImGuiTableFlags.NoBordersInBody|ImGuiTableFlags.PadOuterX))
        {
            ImGui.TableSetupColumn("Check", ImGuiTableColumnFlags.WidthFixed, 60);                                                               
            ImGui.TableSetupColumn("Option", ImGuiTableColumnFlags.WidthStretch, 300);                     

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            bool _show = Settings.Waypoints.ShowWaypoints;
            if(ImGui.Checkbox($"##show_waypoints", ref _show))                        
                Settings.Waypoints.ShowWaypoints = _show;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoints on Atlas");

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool _showArrows = Settings.Waypoints.ShowWaypointArrows;
            if(ImGui.Checkbox($"##show_arrows", ref _showArrows))                        
                Settings.Waypoints.ShowWaypointArrows = _showArrows;

            ImGui.TableNextColumn();
            ImGui.Text("Show Waypoint Arrows on Atlas");

            ImGui.TableNextRow();
        }
        ImGui.EndTable();

        ImGui.Spacing();

        // larger font size
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 10));
        ImGui.Text("Waypoints");
        ImGui.PopStyleVar();        
        ImGui.Separator();


        #region Waypoints Table
        // Collapse
        if (ImGui.CollapsingHeader("Waypoints", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Filter (All / Manual / Auto) + bulk delete. Manual = user-placed, Auto = favorite-synced.
            string[] wpFilters = { "All", "Manual", "Auto" };
            ImGui.Text("Show:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(120);
            ImGui.Combo("##wpListFilter", ref waypointListFilter, wpFilters, wpFilters.Length);
            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            if (ImGui.Button("Clear Auto")) {
                foreach (var k in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(k);
                UpdateWaypointPaths();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove all auto-created (favorite) waypoints. Manual ones are kept.");
            ImGui.SameLine();
            if (ImGui.Button("Clear All")) {
                Settings.Waypoints.Waypoints.Clear();
                UpdateWaypointPaths();
            }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Remove every waypoint, manual and auto.");
            ImGui.Spacing();

            var flags = ImGuiTableFlags.BordersInnerH;
            ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(0, 0, 0, 0));
            if (ImGui.BeginTable("waypoint_list_table", 10, flags))//, new Vector2(-1, panelSize.Y/3)))
            {
                ImGui.TableSetupColumn("Enable", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Waypoint Name", ImGuiTableColumnFlags.WidthFixed, 280);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("X", ImGuiTableColumnFlags.WidthFixed, 40);                    
                ImGui.TableSetupColumn("Y", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Color", ImGuiTableColumnFlags.WidthFixed, 40);     
                ImGui.TableSetupColumn("Scale", ImGuiTableColumnFlags.WidthFixed, 70);     
                ImGui.TableSetupColumn("Opt", ImGuiTableColumnFlags.WidthFixed, 80); 
                ImGui.TableSetupColumn("", ImGuiTableColumnFlags.WidthStretch, 50);
                ImGui.TableHeadersRow();                    

                // Snapshot the (filtered) rows so a per-row Del can mutate the dictionary without
                // invalidating the enumerator. Filter: 1 = manual only, 2 = auto only, 0 = all.
                var wpRows = Settings.Waypoints.Waypoints.Values.Where(w =>
                    waypointListFilter == 0
                    || (waypointListFilter == 1 && !w.AutoCreated)
                    || (waypointListFilter == 2 && w.AutoCreated)).ToList();
                foreach (var waypoint in wpRows) {
                    string id = waypoint.Address.ToString();
                    ImGui.PushID(id);
                    
                    ImGui.TableNextRow();

                    // Enabled
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    bool _show = waypoint.Show;
                    if (ImGui.Checkbox($"##{id}_enabled", ref _show)) {
                        waypoint.Show = _show;
                    }

                    // Name
                    ImGui.TableNextColumn();
                    ImGui.SetNextItemWidth(300);
                    string _name = waypoint.Name;                    
                    if (ImGui.InputText($"##{id}_name", ref _name, 32)) {
                        waypoint.Name = _name;
                    }

                    // steps
                    ImGui.TableNextColumn();
                    int steps = waypoint.PathFromStart?.Count > 0 ? waypoint.PathFromStart.Count - 1 : -1;
                    if (steps >= 0)
                    {
                        // Optionally color-code based on distance
                        Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                        Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                        ImGui.Text(steps.ToString());
                        ImGui.PopStyleColor();
                    }
                    else
                    {
                        ImGui.Text("-");
                    }

                    // Path Weight
                    ImGui.TableNextColumn();
                    ImGui.Text(steps >= 0 ? waypoint.PathWeight.ToString("0") : "-");


                    // Coordinates
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.X.ToString());

                    ImGui.TableNextColumn();                    
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 40.0f) / 2.0f);
                    ImGui.Text(waypoint.Coordinates.Y.ToString());


                    // Color
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                    Color _color = waypoint.Color;
                    Vector4 _vector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                    if(ImGui.ColorEdit4($"##{id}_nodecolor", ref _vector, ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview | ImGuiColorEditFlags.NoInputs))                        
                        waypoint.Color = Color.FromArgb((int)(_vector.W * 255), (int)(_vector.X * 255), (int)(_vector.Y * 255), (int)(_vector.Z * 255));
                    
                    // Scale
                    ImGui.TableNextColumn();
                    float _scale = waypoint.Scale;
                    ImGui.SetNextItemWidth(70);
                    if(ImGui.SliderFloat($"##{id}_weight", ref _scale, 0.1f, 2.0f, "%.2f"))                        
                        waypoint.Scale = _scale;


                    // Buttons
                    ImGui.TableNextColumn();
                    ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 50.0f) / 2.0f);
                    ImGui.SetNextItemWidth(50);
                    if (ImGui.Button("Del")) {
                        RemoveWaypoint(waypoint);
                    }
                    ImGui.PopID();
                }
                ImGui.EndTable();
                ImGui.PopStyleColor();
            }
            #endregion
            
        }
       
        ImGui.Spacing();

        #region Atlas Table
        if (ImGui.CollapsingHeader("Atlas"))
        {
            

            // Sort by Combobox
            ImGui.Text("Sort: ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy = Settings.Waypoints.WaypointPanelSortBy;
            if (ImGui.BeginCombo("##sortByCombo", sortBy))
            {
                if (ImGui.Selectable("Name", sortBy == "Name"))
                    sortBy = "Name";
                if (ImGui.Selectable("Weight", sortBy == "Weight"))
                    sortBy = "Weight";
                if (ImGui.Selectable("Steps", sortBy == "Steps"))
                    sortBy = "Steps";

                Settings.Waypoints.WaypointPanelSortBy = sortBy;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Text("then ");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            string sortBy2 = Settings.Waypoints.WaypointPanelSortBy2;
            if (ImGui.BeginCombo("##sortByCombo2", sortBy2))
            {
                if (ImGui.Selectable("None", sortBy2 == "None"))
                    sortBy2 = "None";
                if (ImGui.Selectable("Name", sortBy2 == "Name"))
                    sortBy2 = "Name";
                if (ImGui.Selectable("Weight", sortBy2 == "Weight"))
                    sortBy2 = "Weight";
                if (ImGui.Selectable("Steps", sortBy2 == "Steps"))
                    sortBy2 = "Steps";

                Settings.Waypoints.WaypointPanelSortBy2 = sortBy2;
                ImGui.EndCombo();
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Items: ");
            ImGui.SameLine();
            int maxItems = Settings.Waypoints.WaypointPanelMaxItems;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxItems", ref maxItems))
                Settings.Waypoints.WaypointPanelMaxItems = maxItems;

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();

            ImGui.Text("Max Steps:");
            ImGui.SameLine();
            int maxSteps = Settings.Waypoints.WaypointPanelMaxSteps;
            ImGui.SetNextItemWidth(100);
            if (ImGui.InputInt("##maxSteps", ref maxSteps))
                Settings.Waypoints.WaypointPanelMaxSteps = Math.Max(0, maxSteps);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Only show maps within this many steps of the explored region. 0 = unlimited.");

            ImGui.Separator();

            ImGui.Text("Search: ");
            ImGui.SameLine();
            string regex = Settings.Waypoints.WaypointPanelFilter;
            ImGui.SetNextItemWidth(250);
            if (ImGui.InputText("##search", ref regex, 32, ImGuiInputTextFlags.EnterReturnsTrue)) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemDeactivatedAfterEdit()) {
                Settings.Waypoints.WaypointPanelFilter = regex;
            } else if (ImGui.IsItemHovered()) {
                ImGui.SetTooltip("Searches for map names and/or mod text. Press enter to search.");
            }

            ImGui.SameLine();
            ImGui.Spacing();
            ImGui.SameLine();
            bool useRegex = Settings.Waypoints.WaypointsUseRegex;
            if (ImGui.Checkbox("Regex", ref useRegex))
                Settings.Waypoints.WaypointsUseRegex = useRegex;
            
            ImGui.Separator();
        
            // Step counts (steps from the explored region) for every reachable node. Memoized; used for
            // both the Steps sort options and the Steps column display below.
            var stepCounts = ComputeStepCounts();
            int GetSteps(Node n) => stepCounts.TryGetValue(n.Coordinates, out var s) ? s : int.MaxValue;

            // Memoized non-visited list: filter -> step cap -> sort -> take, recomputed only when the
            // cache or any of these inputs change (was rerunning the whole pipeline every render frame).
            int maxStepsFilter = Settings.Waypoints.WaypointPanelMaxSteps;
            var atlasList = GetAtlasPanelList(Settings.Waypoints.WaypointPanelFilter, useRegex, sortBy, sortBy2, maxStepsFilter, maxItems);

            var flags = ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Hideable | ImGuiTableFlags.NoSavedSettings;
            ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(2, 2)); // Adjust the padding values as needed
            ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(2, 2)); // A
            if (ImGui.BeginTable("atlas_list_table", 7, flags))//, new Vector2(-1, panelSize.Y/3)))
            {
                ImGui.TableSetupColumn("Map Name", ImGuiTableColumnFlags.WidthFixed, 200);
                ImGui.TableSetupColumn("Content", ImGuiTableColumnFlags.WidthFixed, 110);
                ImGui.TableSetupColumn("Steps", ImGuiTableColumnFlags.WidthFixed, 50);
                ImGui.TableSetupColumn("Weight", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableSetupColumn("Unlocked", ImGuiTableColumnFlags.WidthFixed, 28);
                ImGui.TableSetupColumn("Way", ImGuiTableColumnFlags.WidthFixed, 32);
                ImGui.TableHeadersRow();

                Vector4 _colorVector;
                Color _color;

                if (atlasList != null) {
                    foreach (var node in atlasList) {
                        string id = node.Address.ToString();
                        ImGui.PushID(id);                        
                        ImGui.TableNextRow();

                        // Name
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(node.Name);

                        // Content — kept near full size; 0.7 was unreadable.
                        ImGui.SetWindowFontScale(0.9f);
                        ImGui.TableNextColumn();
                        foreach(var (k,content) in node.Content) {
                            _color = content.Color;
                            _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                            ImGui.TextUnformatted(content.Name);
                            ImGui.PopStyleColor();
                        }

                        ImGui.SetWindowFontScale(1.0f);
                        // Steps — graph distance from the explored region (see ComputeStepCounts),
                        // shown for every node so it stays consistent with the Steps sort options.
                        ImGui.TableNextColumn();
                        int steps = GetSteps(node);
                        if (steps >= 0 && steps != int.MaxValue)
                        {
                            // Color-code based on distance
                            Color stepsColor = steps <= 3 ? Color.Green : (steps <= 7 ? Color.Yellow : Color.Red);
                            Vector4 stepsColorVector = new Vector4(stepsColor.R / 255.0f, stepsColor.G / 255.0f, stepsColor.B / 255.0f, stepsColor.A / 255.0f);
                            ImGui.PushStyleColor(ImGuiCol.Text, stepsColorVector);
                            ImGui.TextUnformatted(steps.ToString());
                            ImGui.PopStyleColor();
                        }
                        else
                        {
                            ImGui.TextUnformatted("-");
                        }
                        // Weight
                        ImGui.TableNextColumn();
                        // set color
                        float weight = (node.Weight - minMapWeight) / (maxMapWeight - minMapWeight);        
                        _color = ColorUtils.InterpolateColor(Settings.Maps.BadNodeColor,Settings.Maps.GoodNodeColor, weight);
                        _colorVector = new Vector4(_color.R / 255.0f, _color.G / 255.0f, _color.B / 255.0f, _color.A / 255.0f);
                        ImGui.PushStyleColor(ImGuiCol.Text, _colorVector);
                        ImGui.TextUnformatted(node.Weight.ToString("0.0"));
                        ImGui.PopStyleColor();

                        

                        // Unlocked
                        ImGui.TableNextColumn();
                        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - 30.0f) / 2.0f);
                        bool _unlocked = node.IsUnlocked;
                        ImGui.BeginDisabled();                        
                        ImGui.Checkbox($"##{id}_enabled", ref _unlocked);
                        ImGui.EndDisabled();
    //
                        // Buttons
                        ImGui.TableNextColumn();
                        RectangleF icon = SpriteHelper.GetUV(MapIconsIndex.Waypoint);
                        
                        if (!node.IsWaypoint){
                            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.TableRowBg));
                            if (ImGui.ImageButton($"$${id}_wp", iconsId, new Vector2(32,32), icon.TopLeft, icon.BottomRight)) {
                                AddWaypoint(node);
                            } else if (ImGui.IsItemHovered()) {
                                ImGui.SetTooltip("Add Waypoint");
                            }
                            ImGui.PopStyleColor();
                        }

                        ImGui.PopID();
                    }
                }
            }
            ImGui.EndTable();
            ImGui.PopStyleVar(2);
            #endregion
            
        }

        ImGui.End();
    }
    #endregion

    #region Waypoint Functions
    private void DrawWaypoint(Waypoint waypoint) {
        var mapNode = waypoint.MapNode();
        if (!Settings.Waypoints.ShowWaypoints || mapNode == null || !waypoint.Show)
            return;

        // Draw the path first, independent of whether the destination node is on screen. The path
        // has its own per-segment clipping (ClipLineToScreen), so a path leading toward an
        // off-screen waypoint still draws up to the screen edge instead of vanishing entirely.
        if (Settings.Graphics.ShowPaths)
            DrawWaypointPath(waypoint);

        RectangleF nodeRect = mapNode.Element.GetClientRect();
        if (!IsOnScreen(nodeRect.Center))
            return;

        Vector2 waypointSize = new Vector2(48, 48);
        waypointSize *= waypoint.Scale;

        Vector2 iconPosition = nodeRect.Center - new Vector2(0, nodeRect.Height / 2);

        if (mapNode.Element.GetChildAtIndex(0) != null)
            iconPosition -= new Vector2(0, mapNode.Element.GetChildAtIndex(0).GetClientRect().Height);

        iconPosition -= new Vector2(0, 20);
        Vector2 waypointTextPosition = iconPosition - new Vector2(0, 10);
        // Add step count + path weight to waypoint label if available
        string displayText = waypoint.StepCount >= 0
            ? $"{waypoint.Name} ({waypoint.StepCount} steps, {waypoint.PathWeight:0} wt)"
            : waypoint.Name;

        DrawCenteredTextWithBackground(displayText, waypointTextPosition, Settings.Graphics.FontColor, Settings.Graphics.BackgroundColor, true, 10, 4);

        iconPosition -= new Vector2(waypointSize.X / 2, 0);
        RectangleF iconSize = new RectangleF(iconPosition.X, iconPosition.Y, waypointSize.X, waypointSize.Y);
        if (customIconsLoaded)
            DrawNodeSprite(iconSize.Center, iconSize.Width, iconSize.Height, waypoint.Icon, waypoint.Color, allowFlatten: false);
        else
            Graphics.DrawImage(IconsFile, iconSize, SpriteHelper.GetUV(MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse), waypoint.Color);


    }

    private static readonly Random waypointColorRng = new();

    // Picks a distinct-ish rainbow color for a new waypoint: a random hue from an evenly spaced palette
    // that no existing waypoint already uses. As waypoints accumulate and a tier fills up, expand to
    // finer hue steps and shifted shades so colors stay distinguishable instead of repeating.
    private Color GetNextWaypointColor() {
        var used = new HashSet<int>();
        foreach (var (_, wp) in Settings.Waypoints.Waypoints)
            used.Add(wp.Color.ToArgb());

        for (int tier = 0; tier < 6; tier++) {
            int count = 8 + tier * 8;                   // 8, 16, 24, ... distinct hues per tier
            float sat = (tier % 2 == 0) ? 0.85f : 0.6f; // alternate saturation as tiers expand
            float val = (tier < 2) ? 1.0f : 0.8f;       // then add dimmer shades
            float hueOffset = tier * 15f;               // interleave later tiers between earlier hues

            var candidates = new List<Color>();
            for (int i = 0; i < count; i++) {
                var c = ColorUtils.ColorFromHSV((360f * i / count) + hueOffset, sat, val);
                if (!used.Contains(c.ToArgb()))
                    candidates.Add(c);
            }
            if (candidates.Count > 0)
                return candidates[waypointColorRng.Next(candidates.Count)];
        }

        // Everything's taken (very many waypoints) - hand back a random rainbow hue.
        return ColorUtils.ColorFromHSV(waypointColorRng.Next(360), 0.8f, 1.0f);
    }

    private void AddWaypoint(Node cachedNode) {
        if (Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Waypoint newWaypoint = cachedNode.ToWaypoint();
        newWaypoint.Icon = SpriteIcon.TriangleDown;
        newWaypoint.Color = GetNextWaypointColor();

        Settings.Waypoints.Waypoints.Add(cachedNode.Coordinates.ToString(), newWaypoint);
        UpdateWaypointPaths();
    }

    /// MARK: SyncFavoriteWaypoints
    /// Keeps auto-created waypoints in sync with favorite map types. Adds waypoints for favorite,
    /// non-visited nodes and removes auto-created waypoints whose map is no longer a favorite (or
    /// removes all auto-created waypoints when the feature is disabled). Manual waypoints
    /// (AutoCreated == false) are never touched. Does not call UpdateWaypointPaths — the caller
    /// (RefreshMapCache) does that once after this returns.
    private void SyncFavoriteWaypoints() {
        try {
            // Snapshot favorite, non-visited nodes under the lock (background job mutates mapCache).
            Dictionary<string, Node> favorites;
            lock (mapCacheLock)
                favorites = mapCache.Values
                    .Where(x => x.IsFavorited && !x.IsVisited)
                    .GroupBy(x => x.Coordinates.ToString())
                    .ToDictionary(g => g.Key, g => g.First());

            if (!Settings.Waypoints.AutoWaypointFavorites) {
                // Feature off: clean up everything we auto-created.
                foreach (var key in Settings.Waypoints.Waypoints.Where(x => x.Value.AutoCreated).Select(x => x.Key).ToList())
                    Settings.Waypoints.Waypoints.Remove(key);
                return;
            }

            // Add waypoints for favorites that don't have one yet.
            foreach (var (key, node) in favorites) {
                if (Settings.Waypoints.Waypoints.ContainsKey(key))
                    continue;

                Waypoint newWaypoint = node.ToWaypoint();
                newWaypoint.Icon = SpriteIcon.TriangleDown;
                newWaypoint.Color = GetNextWaypointColor();
                newWaypoint.AutoCreated = true;
                Settings.Waypoints.Waypoints.Add(key, newWaypoint);
            }

            // Remove auto-created waypoints whose map is no longer a favorite.
            foreach (var key in Settings.Waypoints.Waypoints
                .Where(x => x.Value.AutoCreated && !favorites.ContainsKey(x.Key))
                .Select(x => x.Key).ToList())
                Settings.Waypoints.Waypoints.Remove(key);
        } catch (Exception e) {
            LogError("Error syncing favorite waypoints: " + e.Message);
        }
    }

    private void RemoveWaypoint(Node cachedNode) {
        if (!Settings.Waypoints.Waypoints.ContainsKey(cachedNode.Coordinates.ToString()))
            return;

        Settings.Waypoints.Waypoints.Remove(cachedNode.Coordinates.ToString());
        UpdateWaypointPaths();
    }
    private void RemoveWaypoint(Waypoint waypoint) {
        Settings.Waypoints.Waypoints.Remove(waypoint.Coordinates.ToString());
        UpdateWaypointPaths();
    }

    private void DrawWaypointArrow(Waypoint waypoint) {
        var mapNode = waypoint.MapNode();
        if (!Settings.Waypoints.ShowWaypointArrows || mapNode == null)
            return;

        Vector2 waypointPosition = mapNode.Element.GetClientRect().Center;

        float distance = Vector2.Distance(screenCenter, waypointPosition);

        if (distance < Settings.Graphics.WaypointArrowMinDistance)
            return;

        Vector2 arrowSize = new(64, 64);
        Vector2 arrowPosition = waypointPosition;
        arrowPosition.X = Math.Clamp(arrowPosition.X, 0, GameController.Window.GetWindowRectangleTimeCache.Size.X);
        arrowPosition.Y = Math.Clamp(arrowPosition.Y, 0, GameController.Window.GetWindowRectangleTimeCache.Size.Y);
        arrowPosition = Vector2.Lerp(screenCenter, arrowPosition, 0.80f);
        arrowPosition -= new Vector2(arrowSize.X / 2, arrowSize.Y / 2);

        Vector2 direction = waypointPosition - screenCenter;
        float phi = (float)Math.Atan2(direction.Y, direction.X) + (float)(Math.PI / 2);

        Color color = Color.FromArgb(255, waypoint.Color);
        DrawRotatedImage(arrowId, arrowPosition, arrowSize, phi, color);
         Vector2 textPosition = arrowPosition + new Vector2(arrowSize.X / 2, arrowSize.Y / 2);
        textPosition = Vector2.Lerp(textPosition, screenCenter, 0.10f);
        if (Settings.Waypoints.InverWaypointArrowsColors)
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition,  Settings.Graphics.BackgroundColor, color, true, 10, 4);
        }
        else
        {
            DrawCenteredTextWithBackground($"{waypoint.Name} ({waypoint.StepCount:0})", textPosition, color, Settings.Graphics.BackgroundColor, true, 10, 4);
        }
    }

    #endregion



}
