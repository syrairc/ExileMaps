// Core plugin: fields, Initialise, AreaChange, Tick, Render, keybinds, events.
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

public partial class ExileMapsCore : BaseSettingsPlugin<ExileMapsSettings>
{
    #region Declarations
    public static ExileMapsCore Main;

    private const string defaultMapsPath = "json\\maps.json";
    private const string defaultBiomesPath = "json\\biomes.json";
    private const string defaultContentPath = "json\\content.json";
    private const string ArrowPath = "textures\\arrow.png";
    // Path sprites keyed by style; missing files fall back to a plain line.
    private static readonly Dictionary<PathTextureStyle, string> PathTextureFiles = new()
    {
        { PathTextureStyle.Comet,         "textures\\path-energy-stripe.png" },
        { PathTextureStyle.Chevron,       "textures\\path-chevron.png" },
        { PathTextureStyle.DoubleChevron, "textures\\path-double-chevron.png" },
        { PathTextureStyle.Capsule,       "textures\\path-capsule.png" },
    };
    private const string IconsFile = "Icons.png";
    // Custom sprite atlas: 1024x768, 8x6, 128px cells, 48 desaturated icons.
    private const string CustomIconsPath = "textures\\Icons_Desaturated.png";
    private const string CustomIconsName = SpriteAtlas.FileName;
    
    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private readonly List<Node> selectedNodes = [];
    // On-screen special maps, recomputed with selectedNodes. Drawn independently of the Process* node
    // filters so landmark specials (Ziggurat, Hilda's, etc.) show even when visited or locked.
    private readonly List<Node> specialNodes = [];
    // Frames between on-screen node set recomputes. The cull is expensive; the draw still runs every frame.
    private const int OnScreenRecomputeInterval = 5;
    // Waypoint-panel list filter: 0 = All, 1 = Manual only, 2 = Auto-created only.
    private int waypointListFilter = 0;
    // Reused each frame to avoid per-render allocation.
    private readonly List<(Node node, RectangleF rect)> nodePositions = [];
    // Content-icon rects drawn this frame + their content name, for hover tooltips. Cleared each frame
    // before the content-icon pass; consulted by DrawContentIconTooltip after all nodes are drawn.
    private readonly List<(RectangleF rect, string content)> contentIconRects = [];
    // Top Y of the content-icon row we drew this frame, per node. Lets the atlas-point/quest indicators
    // sit above our content icons. Cleared with contentIconRects.
    private readonly Dictionary<Vector2i, float> contentRowTopByCoord = [];
    // Per-frame memo of node screen rects. Element.GetClientRect() walks the parent chain (a
    // process-memory read per ancestor) on every call, and the same node is needed by the line and
    // path passes within one frame. Cleared + reseeded once per frame in Render; render-thread only.
    private readonly Dictionary<Vector2i, RectangleF> frameRectCache = [];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedExcludeRects = [];
    // True when a map node tooltip is up this frame. The tooltip's exclude rect drops the hovered
    // node from selectedNodes, so its icon/name are redrawn separately on top (DrawHoveredNodeOverTooltip).
    private bool mapTooltipVisible;
    private Dictionary<Vector2i, Node> mapCache = [];
    public bool refreshCache = false;
    private bool refreshingCache = false;
    // When set, the next background refresh clears the cache first (atlas reopen / area change).
    private bool clearCacheOnRefresh = false;
    // 0..1 progress of the current cache refresh; written by the job thread, read by render.
    private volatile float cacheRefreshProgress = 0f;
    private float maxMapWeight = 50.0f;
    private float minMapWeight = -50.0f;
    private readonly object mapCacheLock = new();
    private DateTime lastRefresh = DateTime.Now;
    private bool weightsDirty = false;
    private DateTime lastWeightRecalc = DateTime.Now;
    // Live profile-stored edits (weights/colors/highlight/icon/favorite) that haven't been snapshotted
    // into the active profile yet. Without this, edits are lost on reload: LoadProfile restores the
    // stale snapshot, discarding live changes (user had to switch profiles to force a save).
    private bool profileDirty = false;
    private DateTime lastProfileSave = DateTime.Now;
    // Transient per-refresh stats raise PropertyChanged but aren't persisted. Don't mark the profile dirty.
    private static readonly HashSet<string> TransientStatProps = new() { "Count", "LockedCount", "FogCount" };
    private int TickCount { get; set; }

    // High-resolution monotonic clock for animations. Frame-count (TickCount) drives animation by a
    // fixed phase-step per frame, so irregular frame delivery (e.g. the heavier every-Nth-frame cull)
    // makes motion stutter; DateTime.Now only has ~15ms resolution so it steps at high FPS. Wall-clock
    // seconds advance animations by real elapsed time → smooth regardless of frame timing.
    private readonly System.Diagnostics.Stopwatch animClock = System.Diagnostics.Stopwatch.StartNew();
    private float AnimSeconds => (float)animClock.Elapsed.TotalSeconds;

    // Bumped each refresh. Memoized step counts and atlas-panel list recompute when this changes.
    private int mapCacheVersion = 0;
    private int weightsRecalcVersion = 0;
    // id -> Map index for O(1) MatchID fallback. Rebuilt when the map dictionary gains entries.
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
    // Loaded path sprite textures keyed by style.
    private readonly Dictionary<PathTextureStyle, IntPtr> pathTextureIds = new();
    // Per-content-type icon PNGs: icon-<contenttype>.png, keyed by file name.
    private readonly HashSet<string> loadedContentIcons = new(StringComparer.OrdinalIgnoreCase);
    // Largest screen-px-per-art-unit magnification seen (== full zoom). Self-calibrates the content
    // icon zoom factor so settings tuned "at full zoom" scale down correctly as the atlas zooms out.
    private float maxNodeZoomMagnification = 0f;

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

        // Map/Content/Biome dicts are populated by game-file scraping in Tick(), not here.
        Settings.Profiles.EnsureDefaultProfile();

        // Mirror the persisted special-map config (names + max-weight) into the Node statics that
        // detection/weighting read. Re-synced on edit via RequestSpecialMapsRefresh.
        Classes.Node.SetSpecialConfig(Settings.Maps.SpecialMaps.Names(),
            Settings.Maps.SpecialMaps.UseMaxWeight, Settings.Maps.SpecialMaps.MaxWeight);

        // Backs up v1 settings before ExileCore overwrites them on the next save.
        DetectOldSettings();

        Graphics.InitImage(IconsFile);
        iconsId = Graphics.GetTextureId(IconsFile);
        Graphics.InitImage("arrow.png", Path.Combine(DirectoryFullName, ArrowPath));
        arrowId = Graphics.GetTextureId("arrow.png");

        // Load path sprites that exist; DrawWaypointPath falls back to a plain line for missing ones.
        foreach (var (style, rel) in PathTextureFiles) {
            var full = Path.Combine(DirectoryFullName, rel);
            if (!File.Exists(full))
                continue;
            var name = Path.GetFileName(rel);
            Graphics.InitImage(name, full);
            pathTextureIds[style] = Graphics.GetTextureId(name);
        }

        // Load custom sprites if present; plugin runs without them.
        var customIconsFull = Path.Combine(DirectoryFullName, CustomIconsPath);
        if (File.Exists(customIconsFull)) {
            Graphics.InitImage(CustomIconsName, customIconsFull);
            customIconsId = Graphics.GetTextureId(CustomIconsName);
            customIconsLoaded = true;
        }

        // Load per-content-type icon PNGs from textures/icon-*.png (icon-breach.png etc.).
        var texturesDir = Path.Combine(DirectoryFullName, "textures");
        foreach (var file in Directory.GetFiles(texturesDir, "icon-*.png")) {
            var name = Path.GetFileName(file);
            try {
                Graphics.InitImage(name, file);
                Graphics.GetTextureId(name);
                loadedContentIcons.Add(name);
            } catch (Exception e) {
                LogError($"Failed to load content icon {name}: {e.Message}");
            }
        }
        LogMessage($"ExileMaps: loaded {loadedContentIcons.Count} content icons.");

        // Textured atlas panel buttons (waypoints / tours / atlas), with per-state + tooltip images.
        LoadPanelButtonTextures();

        CanUseMultiThreading = true;

        return true;
    }
    public override void AreaChange(AreaInstance area)
    {
        refreshCache = true;
    }

    // Called from the Special Maps settings editor when the name list or max-weight option changes:
    // re-mirror the config into Node's statics and rebuild the cache so detection + weights re-apply.
    public void RequestSpecialMapsRefresh()
    {
        Classes.Node.SetSpecialConfig(Settings.Maps.SpecialMaps.Names(),
            Settings.Maps.SpecialMaps.UseMaxWeight, Settings.Maps.SpecialMaps.MaxWeight);
        refreshCache = true;
        clearCacheOnRefresh = true;
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
            refreshCache = true;
            clearCacheOnRefresh = true;
            lastRefresh = DateTime.MinValue;
        }

        AtlasHasBeenClosed = false;

        // Scrape map types, content, and biomes from game files once available. Gate on map scrape.
        if (!gameFilesScraped && UpdateMapData(false)) {
            UpdateContentData(false);
            UpdateBiomeData(false);
            gameFilesScraped = true;
            SeedProfiles();
        }

        // Atlas streams nodes progressively; re-flag refresh when cache is behind live count.
        bool cacheBehind = AtlasPanel.Descriptions.Count > mapCache.Count;
        if (cacheBehind)
            refreshCache = true;

        screenCenter = GameController.Window.GetWindowRectangle().Center - GameController.Window.GetWindowRectangle().Location;

        // Bypass throttle while cache is empty or behind the live count; 0.5s floor prevents bursts.
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
                // Guard so a thrown refresh doesn't leave refreshingCache stuck true.
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

        // Debounce weight recalcs: slider drags fire many PropertyChanged events per second.
        if (weightsDirty && !refreshingCache && DateTime.Now.Subtract(lastWeightRecalc).TotalMilliseconds > 500) {
            weightsDirty = false;
            lastWeightRecalc = DateTime.Now;
            RecalculateWeights();
        }

        // Snapshot live edits into the active profile so they persist (ExileCore serializes the profile,
        // not the working state). Debounced, and gated on gameFilesScraped so we never overwrite a profile
        // with an empty live state before the game files are read. Fixes "had to switch profiles to save".
        if (profileDirty && gameFilesScraped && !refreshingCache && DateTime.Now.Subtract(lastProfileSave).TotalMilliseconds > 1000) {
            profileDirty = false;
            lastProfileSave = DateTime.Now;
            try {
                Settings.Profiles.SaveCurrentProfile();
            } catch (Exception e) {
                LogError("Error saving current profile: " + e.Message);
            }
        }

        return;
    }

    public override void Render()
    {
        bool perf = Settings.Features.ShowPerfMonitor;
        long t0;

        ProcessPendingWeightFile();
        CheckKeybinds();

        t0 = Stopwatch.GetTimestamp();
        if (WaypointPanelIsOpen) DrawWaypointPanel(); else wpPanelWasOpen = false;
        if (quickEditOpen) DrawQuickEditPanel();
        if (debugNodeOpen) DrawNodeDebugPanel();
        if (atlasOverviewOpen) DrawAtlasOverviewPanel(); else overviewWasOpen = false;
        if (toursPanelOpen) DrawToursPanel(); else toursPanelWasOpen = false;
        if (perf) PerfMonitor.Record("Panels", Stopwatch.GetTimestamp() - t0);

        DrawPerfMonitorOverlay();

        TickCount++;

        if (!AtlasPanel.IsVisible) return;

        // Floating panel buttons + search box: shown whenever the atlas is open, even if overlay drawing is off.
        DrawPanelButtonBar();
        DrawAtlasSearchBox();

        // Build Tour mode: capture node clicks + show the indicator. Runs even if overlay drawing is
        // off so the mode stays usable; only emits ImGui geometry over the hovered node.
        if (buildModeActive) {
            HandleBuildMode();
            DrawBuildModeIndicator();
        }

        // Master draw toggle (Scroll Lock by default). Keybinds still processed above so it can be re-enabled.
        if (!Settings.Features.EnableDrawing) return;

        // Cache panel/tooltip bounds once per frame so IsOnScreen avoids repeated game-memory reads.
        UpdateScreenBounds();

        // Reset the per-frame node-rect memo. Seeded by the nodePositions build below; consumed by
        // the line + waypoint-path passes. Cleared unconditionally so minimap mode reads fresh too.
        frameRectCache.Clear();

        // Recompute on-screen set every N frames (expensive cull); redraw cached set every frame.
        if (TickCount % OnScreenRecomputeInterval == 0 || selectedNodes.Count == 0) {
            t0 = Stopwatch.GetTimestamp();
            lock (mapCacheLock) {
                selectedNodes.Clear();
                selectedNodes.AddRange(mapCache.Values.AsParallel()
                    .Where(x => Settings.Features.ProcessVisitedNodes || !x.IsVisited || x.IsAttempted)
                    .Where(x => (Settings.Features.ProcessHiddenNodes && !x.IsVisible) || x.IsVisible)
                    .Where(x => (Settings.Features.ProcessLockedNodes && !x.IsUnlocked) || x.IsUnlocked)
                    .Where(x => (Settings.Features.ProcessUnlockedNodes && x.IsUnlocked) || !x.IsUnlocked)
                    // EXPERIMENT (Change C): GetClientRectCache is the engine's last-computed rect
                    // (a cheap struct read) instead of GetClientRect(), which re-walks the parent
                    // chain for all ~1000 nodes every cull. VERIFY IN-GAME: pan/zoom the atlas and
                    // confirm nodes stay aligned and Render.NodeCull drops. If overlays lag/freeze
                    // while panning, or nodes stop appearing, the cache is stale/unpopulated.
                    // Revert this one line to GetClientRect().Center.
                    .Where(x => IsOnScreen(x.MapNode.Element.GetClientRectCache.Center)));

                // Special maps draw their indicator regardless of visited/locked/highlight filters.
                specialNodes.Clear();
                specialNodes.AddRange(mapCache.Values.AsParallel()
                    .Where(x => x.IsSpecial)
                    .Where(x => IsOnScreen(x.MapNode.Element.GetClientRectCache.Center)));
            }
            if (perf) PerfMonitor.Record("Render.NodeCull", Stopwatch.GetTimestamp() - t0);
        }

        if (!ShowMinimap) {
            // Resolve rects once, then draw in fixed z-layers: lines -> fills -> rings -> labels.
            nodePositions.Clear();
            foreach (var node in selectedNodes) {
                try {
                    var rect = node.MapNode.Element.GetClientRect();
                    frameRectCache[node.Coordinates] = rect;   // seed memo for line + path passes
                    nodePositions.Add((node, rect));
                }
                catch (Exception e) { DebugSwallow("Render: node rect read", e); }
            }

            if (Settings.Features.DebugMode) {
                foreach (var (node, _) in nodePositions)
                    DrawDebugging(node);
            } else {
                // 1. Lines
                t0 = Stopwatch.GetTimestamp();
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLines(node, rect);
                if (perf) PerfMonitor.Record("Render.Lines", Stopwatch.GetTimestamp() - t0);

                // 2. Node fills
                t0 = Stopwatch.GetTimestamp();
                foreach (var (node, rect) in nodePositions) {
                    try { DrawMapNode(node, rect); }
                    catch (Exception e) { LogError("Error drawing node fill: " + e.Message); }
                }
                if (perf) PerfMonitor.Record("Render.Fills", Stopwatch.GetTimestamp() - t0);

                // 3. Rings
                t0 = Stopwatch.GetTimestamp();
                foreach (var (node, rect) in nodePositions)
                    DrawNodeRings(node, rect);
                if (perf) PerfMonitor.Record("Render.Rings", Stopwatch.GetTimestamp() - t0);

                // 3b. Content icons (game-style per-type PNGs above each node)
                t0 = Stopwatch.GetTimestamp();
                contentIconRects.Clear();
                contentRowTopByCoord.Clear();
                foreach (var (node, rect) in nodePositions)
                    DrawContentIcons(node, rect);
                if (perf) PerfMonitor.Record("Render.ContentIcons", Stopwatch.GetTimestamp() - t0);

                // 3c-3f. Indicators (favorites, special, atlas-point, atlas-quest)
                t0 = Stopwatch.GetTimestamp();
                foreach (var (node, rect) in nodePositions)
                    DrawFavoriteIndicator(node, rect);
                // Special indicators use their own on-screen set so visited/locked specials still draw.
                foreach (var node in specialNodes) {
                    try { DrawSpecialIndicator(node, node.MapNode.Element.GetClientRect()); }
                    catch (Exception e) { DebugSwallow("Render: special indicator", e); }
                }
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasPointIndicator(node, rect);
                foreach (var (node, rect) in nodePositions)
                    DrawAtlasQuestIndicator(node, rect);
                if (perf) PerfMonitor.Record("Render.Indicators", Stopwatch.GetTimestamp() - t0);

                // 4. Labels
                t0 = Stopwatch.GetTimestamp();
                foreach (var (node, rect) in nodePositions)
                    DrawNodeLabels(node, rect);
                // Special names from the dedicated set so visited/locked specials get labelled too.
                foreach (var node in specialNodes) {
                    try { DrawSpecialMapName(node, node.MapNode.Element.GetClientRect()); }
                    catch (Exception e) { DebugSwallow("Render: special name", e); }
                }
                if (perf) PerfMonitor.Record("Render.Labels", Stopwatch.GetTimestamp() - t0);

                // Keep the hovered node's icon + name on top of its tooltip (the tooltip rect
                // otherwise culls it from the passes above).
                DrawHoveredNodeOverTooltip();

                // Content-icon hover tooltip (drawn last so it sits on top of everything).
                DrawContentIconTooltip();
            }
        }

        t0 = Stopwatch.GetTimestamp();
        try {
            foreach (var (k,waypoint) in Settings.Waypoints.Waypoints) {
                // Resolve the atlas node once per waypoint, not separately in each draw method.
                var mapNode = waypoint.MapNode();
                DrawWaypoint(waypoint, mapNode);
                DrawWaypointArrow(waypoint, mapNode);
            }
        }
        catch (Exception e) {
            LogError("Error drawing waypoints: " + e.Message + "\n" + e.StackTrace);
        }
        if (perf) PerfMonitor.Record("Render.Waypoints", Stopwatch.GetTimestamp() - t0);

        DrawProgressReadout();
        DrawSearchPing();

        t0 = Stopwatch.GetTimestamp();
        DrawTours();
        if (perf) PerfMonitor.Record("Render.Tours", Stopwatch.GetTimestamp() - t0);

        DrawCacheProgressBar();
    }

    // Fill color for the cache reload bar.
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

    private void SubscribeToEvents() {
        try {
            Settings.Maps.Maps.CollectionChanged += (_, _) => { weightsDirty = true; profileDirty = true; };
            Settings.Maps.Maps.PropertyChanged += (_, e) => OnSettingPropertyChanged(e?.PropertyName);
            Settings.Maps.Biomes.Biomes.PropertyChanged += (_, e) => OnSettingPropertyChanged(e?.PropertyName);
            Settings.Maps.Biomes.Biomes.CollectionChanged += (_, _) => { weightsDirty = true; profileDirty = true; };
            Settings.Maps.Content.ContentTypes.CollectionChanged += (_, _) => { weightsDirty = true; profileDirty = true; };
            Settings.Maps.Content.ContentTypes.PropertyChanged += (_, e) => OnSettingPropertyChanged(e?.PropertyName);
            // Full rebuild on map-list add/remove so static data (map type) re-resolves for the change.
            Settings.Maps.Maps.CollectionChanged += (_, _) => { refreshCache = true; clearCacheOnRefresh = true; };
        } catch (Exception e) {
            LogError("Error subscribing to events: " + e.Message);
        }
    }

    // A live Map/Content/Biome property changed. Always re-flags the weight recalc; flags the profile
    // for snapshotting too, unless it's a transient per-refresh stat (Count/LockedCount/FogCount).
    private void OnSettingPropertyChanged(string propertyName) {
        weightsDirty = true;
        if (propertyName == null || !TransientStatProps.Contains(propertyName))
            profileDirty = true;
    }

    private void RegisterHotkeys() {
        RegisterHotkey(Settings.Keybinds.ToggleDrawingHotkey);
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
        RegisterHotkey(Settings.Keybinds.ToggleAtlasOverviewHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleToursPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddTourStopHotkey);
        RegisterHotkey(Settings.Keybinds.BuildModeHotkey);
        RegisterHotkey(Settings.Keybinds.BuildModeExitHotkey);
    }
    
    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        Input.RegisterKey(hotkey.Value);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey.Value); };
    }
    private void CheckKeybinds() {
        if (!AtlasPanel.IsVisible) {
            // Build Mode is an atlas-only interaction; drop out of it when the atlas closes.
            buildModeActive = false;
            // In a map (atlas closed): quick edit the current area's map type, if we know it.
            if (Settings.Keybinds.QuickEditNodeHotkey.PressedOnce())
                OpenQuickEditForCurrentArea();
            return;
        }

        if (Settings.Keybinds.ToggleDrawingHotkey.PressedOnce())
            Settings.Features.EnableDrawing.Value = !Settings.Features.EnableDrawing.Value;

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {
            // Force past the throttle and do a full rebuild so static node data (map type, content,
            // passives) is re-resolved. The manual refresh is the user's "re-read everything" escape.
            refreshCache = true;
            clearCacheOnRefresh = true;
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

        if (Settings.Keybinds.ToggleAtlasOverviewHotkey.PressedOnce())
            atlasOverviewOpen = !atlasOverviewOpen;

        if (Settings.Keybinds.ToggleToursPanelHotkey.PressedOnce())
            toursPanelOpen = !toursPanelOpen;

        if (Settings.Keybinds.AddTourStopHotkey.PressedOnce())
            AddStopToActiveTour(GetClosestNodeToCursor());

        if (Settings.Keybinds.BuildModeHotkey.PressedOnce())
            buildModeActive = !buildModeActive;

        if (buildModeActive && Settings.Keybinds.BuildModeExitHotkey.PressedOnce())
            buildModeActive = false;

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

}
