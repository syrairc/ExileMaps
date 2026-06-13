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
    // Frames between on-screen node set recomputes. The cull is expensive; the draw still runs every frame.
    private const int OnScreenRecomputeInterval = 5;
    // Waypoint-panel list filter: 0 = All, 1 = Manual only, 2 = Auto-created only.
    private int waypointListFilter = 0;
    // Reused each frame to avoid per-render allocation.
    private readonly List<(Node node, RectangleF rect)> nodePositions = [];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedExcludeRects = [];
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
    // into the active profile yet. Without this, edits live only in the working state and are lost on
    // reload — LoadProfile overlays the stale snapshot back over them (user had to switch profiles to save).
    private bool profileDirty = false;
    private DateTime lastProfileSave = DateTime.Now;
    // Transient per-refresh stats raise PropertyChanged but aren't persisted — don't mark the profile dirty.
    private static readonly HashSet<string> TransientStatProps = new() { "Count", "LockedCount", "FogCount" };
    private int TickCount { get; set; }

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

        // Textured atlas panel buttons (waypoints / tours / atlas), with per-state + tooltip images.
        LoadPanelButtonTextures();

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
        ProcessPendingWeightFile();

        CheckKeybinds();

        if (WaypointPanelIsOpen) DrawWaypointPanel(); else wpPanelWasOpen = false;

        if (quickEditOpen) DrawQuickEditPanel();

        if (debugNodeOpen) DrawNodeDebugPanel();

        if (atlasOverviewOpen) DrawAtlasOverviewPanel(); else overviewWasOpen = false;

        if (toursPanelOpen) DrawToursPanel(); else toursPanelWasOpen = false;

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

        // Recompute on-screen set every N frames (expensive cull); redraw cached set every frame.
        if (TickCount % OnScreenRecomputeInterval == 0 || selectedNodes.Count == 0) {
            lock (mapCacheLock) {
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
            // Resolve rects once, then draw in fixed z-layers: lines -> fills -> rings -> labels.
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

        DrawProgressReadout();
        DrawSearchPing();
        DrawTours();

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
            Settings.Maps.Maps.CollectionChanged += (_, _) => { refreshCache = true; };
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
            // Force past the throttle so the press refreshes immediately.
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
