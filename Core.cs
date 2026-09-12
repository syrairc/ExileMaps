
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.PoEMemory.Elements.AtlasElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Nodes;
using ImGuiNET;
using GameOffsets2.Native;
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
    private const string CustomIconsPath = "textures\\Icons_Desaturated.png";
    private const string CustomIconsName = SpriteAtlas.FileName;

    public IngameUIElements UI;
    public AtlasPanel AtlasPanel;

    private Vector2 screenCenter;
    private readonly List<Node> selectedNodes = [];
    private readonly List<Node> specialNodes = [];
    private const int OnScreenRecomputeInterval = 5;
    private int lastCullVersion = -1;
    private readonly List<Node> lastOnScreen = [];
    private readonly HashSet<Node> cullCandidates = [];
    private int cullsSinceFullScan = 0;
    private const int FullCullEvery = 10;

    private int lastDescriptionCount = -1;
    private int waypointListFilter = 0;
    private readonly List<(Node node, RectangleF rect)> nodePositions = [];

    private readonly List<(RectangleF rect, string title)> contentIconRects = [];
    private readonly Dictionary<Vector2i, float> contentRowTopByCoord = [];
    private readonly Dictionary<Vector2i, RectangleF> biomeIconRectByCoord = [];
    private readonly Dictionary<string, string> contentIconFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> biomeIconFileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string file, string content, Color tint)> contentIconScratch = [];
    private readonly Dictionary<Vector2i, RectangleF> frameRectCache = [];
    private readonly Dictionary<(Vector2i, Vector2i), Vector2[]> frameCurveCache = [];
    private readonly Dictionary<Vector2i, float> nameHalfByCoord = [];
    private readonly Dictionary<Vector2i, LabelStyleOverride> contentOverrideByCoord = [];
    private readonly HashSet<Vector2i> drawnCoords = [];
    private readonly Dictionary<(string, float), Vector2> textSizeCache = new(256);
    private Func<Vector3, Vector2> frameWorldToScreen;
    private readonly List<RectangleF> excludeScratch = [];
    private readonly Vector2[] polyScratch = new Vector2[520];
    private RectangleF cachedScreenRect;
    private readonly List<RectangleF> cachedExcludeRects = [];
    private bool mapTooltipVisible;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Vector2i, Node> mapCache = new();
    private volatile List<Classes.Expedition> expeditions = new();
    private ExileCore2.PoEMemory.Element frameLogbookPopup;
    private readonly HashSet<Vector2i> frameVisibleExpeditionButtonCoords = new();
    private readonly HashSet<Vector2i> highlightedExpeditionCoords = new();
    public bool refreshCache = false;
    private volatile bool refreshingCache = false;
    private bool clearCacheOnRefresh = false;
    private volatile float cacheRefreshProgress = 0f;
    private const float maxMapWeight = 50.0f;
    private const float minMapWeight = -50.0f;
    private const float maxForetellWeight = 300.0f;
    private const float minForetellWeight = 0.0f;
    private readonly object mapCacheLock = new();
    private long lastRefreshMs = Environment.TickCount64;
    private volatile bool waypointSyncPending;
    private bool weightsDirty = false;
    private readonly HashSet<HotkeySettings> registeredKeybindSets = new();
    private long lastWeightRecalcMs = Environment.TickCount64;
    internal int TickCount { get; private set; }

    private readonly System.Diagnostics.Stopwatch animClock = System.Diagnostics.Stopwatch.StartNew();
    private float AnimSeconds => (float)animClock.Elapsed.TotalSeconds;

    private int mapCacheVersion = 0;
    private int weightsRecalcVersion = 0;
    private Dictionary<string, MapInfo> mapIdIndex;
    private int mapIdIndexCount = -1;
    private Dictionary<Vector2i, int> cachedStepCounts;
    private int cachedStepCountsVersion = -1;
    private List<Node> cachedAtlasList;
    private (int ver, int wver, string filter, int maxSteps) cachedAtlasSig = (-1, -1, null, -1);

    internal IntPtr arrowId;
    internal IntPtr customIconsId;
    private ExileImGui2.Controls.IconSheet[] iconSheets;
    private readonly HashSet<string> loadedContentIcons = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loadedBiomeIcons = new(StringComparer.OrdinalIgnoreCase);
    private float maxNodeZoomMagnification = 0f;

    private bool AtlasHasBeenClosed = true;
    private bool gameFilesScraped = false;
    private bool panelOpen = false;
    private readonly ExileImGui2.NavState navState = new() { Active = "wp" };
    private string expSearchText = "";

    private string legacyBackupPath;
    private bool legacyImportPending;

    #endregion

    #region ExileCore Methods
    public override bool Initialise()
    {
        Main = this;
        BackupLegacySettingsOnce();
        RegisterHotkeys();

        WireProfiles();

        UpdateRumorData();
        UpdateRitualModData();

        Graphics.InitImage("arrow.png", Path.Combine(DirectoryFullName, ArrowPath));
        arrowId = Graphics.GetTextureId("arrow.png");

        var customIconsFull = Path.Combine(DirectoryFullName, CustomIconsPath);
        if (!File.Exists(customIconsFull)) {
            LogError($"Sprite atlas missing: {customIconsFull}");
            return false;
        }
        Graphics.InitImage(CustomIconsName, customIconsFull);
        customIconsId = Graphics.GetTextureId(CustomIconsName);

        iconSheets = [ExileImGui2.Controls.IconSheet.Grid(CustomIconsName, customIconsId,
            new ExileImGui2.GridAtlas(SpriteAtlas.AtlasWidth, SpriteAtlas.AtlasHeight,
                SpriteAtlas.CellSize, SpriteAtlas.Columns),
            SpriteAtlas.Count, i => ((SpriteIcon)i).ToString())];

        var texturesDir = Path.Combine(DirectoryFullName, "textures", "content");
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

        var biomesDir = Path.Combine(DirectoryFullName, "textures", "biomes");
        if (Directory.Exists(biomesDir)) {
            foreach (var file in Directory.GetFiles(biomesDir, "biome-*.png")) {
                var name = Path.GetFileName(file);
                try {
                    Graphics.InitImage(name, file);
                    Graphics.GetTextureId(name);
                    loadedBiomeIcons.Add(name);
                } catch (Exception e) {
                    LogError($"Failed to load biome icon {name}: {e.Message}");
                }
            }
        }

        LoadPanelButtonTextures();

        CanUseMultiThreading = true;

        return true;
    }
    public override void AreaChange(AreaInstance area)
    {
        refreshCache = true;
    }

    public void RequestSpecialMapsRefresh()
    {
        lock (mapCacheLock)
            foreach (var n in mapCache.Values)
                n.ResolveSpecial();
        weightsDirty = true;
    }

    public override void Tick()
    {
        UI = GameController.Game.IngameState.IngameUi;
        AtlasPanel = UI.WorldMap.AtlasPanel;

        TickHacks();

        if (!AtlasPanel.IsVisible) {
            AtlasHasBeenClosed = true;
            panelOpen = false;
            cachedSearchBox = null;
            maxNodeZoomMagnification = 0f;
            rumorPanelMemo.Clear();
            return;
        }

        if (AtlasHasBeenClosed) {
            refreshCache = true;
            clearCacheOnRefresh = true;
            lastRefreshMs = long.MinValue / 2;
            lastDescriptionCount = -1;
        }

        AtlasHasBeenClosed = false;

        if (!gameFilesScraped && UpdateMapData(false)) {
            UpdateContentData(false);
            UpdateBiomeData(false);
            gameFilesScraped = true;
            Settings.Profiles.Ensure();
            RebuildWeightEditorIds();
        }

        var winRect = GameController.Window.GetWindowRectangle();
        screenCenter = winRect.Center - winRect.Location;

        long nowMs = Environment.TickCount64;
        double elapsed = (nowMs - lastRefreshMs) / 1000.0;

        bool contentChanged = false;
        if (elapsed > 0.5 || mapCache.Count == 0) {
            int liveCount = AtlasPanel.Descriptions.Count;
            if (liveCount != lastDescriptionCount) {
                contentChanged = true;
                refreshCache = true;
            }
        }

        bool throttleOpen = elapsed > Settings.Graphics.MapCacheRefreshRate
            || mapCache.Count == 0
            || (contentChanged && elapsed > 0.5);
        if (refreshCache && !refreshingCache && throttleOpen)
        {
            bool clearCache = clearCacheOnRefresh;
            clearCacheOnRefresh = false;
            refreshCache = false;
            refreshingCache = true;
            Task.Run(() =>
            {
                try {
                    RefreshMapCache(clearCache);
                } catch (Exception e) {
                    LogError("Error refreshing map cache: " + e.Message + "\n" + e.StackTrace);
                    lastRefreshMs = Environment.TickCount64;
                } finally {
                    refreshingCache = false;
                }
            });
        }

        if (weightsDirty && !refreshingCache && nowMs - lastWeightRecalcMs > 500) {
            weightsDirty = false;
            lastWeightRecalcMs = nowMs;
            RecalculateWeights();
        }

        return;
    }

    public override void Render()
    {
        bool perf = Settings.Features.ShowPerfMonitor;
        long t0;

        ProcessPendingFileAction();
        CheckKeybinds();

        t0 = Stopwatch.GetTimestamp();
        if (panelOpen) DrawGroupedPanel();
        if (quickEditOpen) DrawQuickEditPanel();
        if (debugNodeOpen) DrawNodeDebugPanel();
        if (perf) PerfMonitor.Record("Panels", Stopwatch.GetTimestamp() - t0);

        DrawPerfMonitorOverlay();

        RenderHacks();

        TickCount++;

        if (!AtlasPanel.IsVisible) return;

        try {
            var snap = AtlasPanel?.Camera?.Snapshot;
            frameWorldToScreen = snap == null ? null : snap.WorldToScreen;
        } catch (Exception e) {
            frameWorldToScreen = null;
            DebugSwallow("Render: camera snapshot", e);
        }

        DrawExileMapsButton();
        DrawRitualOverlayButtons();

        if (atlasCamera is { Busy: true }) return;
        DrawSearch();

        if (buildModeActive) {
            HandleBuildMode();
            DrawBuildModeIndicator();
        }

        if (Settings.Features.DebugMode)
            HandleDebugMode();

        if (!Settings.Features.EnableDrawing) return;

        UpdateScreenBounds();

        frameRectCache.Clear();
        frameCurveCache.Clear();
        nameHalfByCoord.Clear();
        contentOverrideByCoord.Clear();
        drawnCoords.Clear();

        if (TickCount % OnScreenRecomputeInterval == 0 || lastCullVersion != mapCacheVersion) {
            t0 = Stopwatch.GetTimestamp();

            bool fullScan = selectedNodes.Count == 0
                || lastCullVersion != mapCacheVersion
                || ++cullsSinceFullScan >= FullCullEvery;

            if (fullScan) {
                cullsSinceFullScan = 0;
                List<Node> onScreen;
                lock (mapCacheLock)
                    onScreen = mapCache.Values.AsParallel().Where(OnScreenSafe).ToList();
                RebuildOnScreenSets(onScreen);
                if (perf) PerfMonitor.Record("Render.NodeCull.Full", Stopwatch.GetTimestamp() - t0);
            } else {
                cullCandidates.Clear();
                foreach (var n in lastOnScreen) {
                    cullCandidates.Add(n);
                    foreach (var nb in n.Neighbors.Values)
                        if (nb != null)
                            cullCandidates.Add(nb);
                }
                RebuildOnScreenSets(cullCandidates);
                if (perf) PerfMonitor.Record("Render.NodeCull.Incr", Stopwatch.GetTimestamp() - t0);
            }

            lastCullVersion = mapCacheVersion;
        }

        nodePositions.Clear();
        foreach (var node in selectedNodes) {
            try {
                var rect = node.MapNode.Element.GetClientRectCache;
                if (rect.Width <= 0)
                    continue;
                rect = WorldAlignedRect(node, rect);
                frameRectCache[node.Coordinates] = rect;
                drawnCoords.Add(node.Coordinates);
                nodePositions.Add((node, rect));
            }
            catch (Exception e) { DebugSwallow("Render: node rect read", e); }
        }

        t0 = Stopwatch.GetTimestamp();
        foreach (var (node, rect) in nodePositions)
            DrawNodeLines(node, rect);
        if (perf) PerfMonitor.Record("Render.Lines", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        foreach (var (node, rect) in nodePositions) {
            try { DrawMapNode(node, rect); }
            catch (Exception e) { LogError("Error drawing node fill: " + e.Message); }
        }
        if (perf) PerfMonitor.Record("Render.Fills", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        contentIconRects.Clear();
        contentRowTopByCoord.Clear();
        biomeIconRectByCoord.Clear();
        foreach (var (node, rect) in nodePositions) {
            DrawBiomeIcon(node, rect);
            DrawContentRow(node, rect);
            DrawOverrideIcon(node, rect);
        }
        if (perf) PerfMonitor.Record("Render.ContentRow", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        foreach (var (node, rect) in nodePositions)
            DrawFavoriteIndicator(node, rect);
        foreach (var node in specialNodes) {
            var rect = GetNodeRect(node);
            if (rect.Width > 0)
                DrawSpecialIndicator(node, rect);
        }
        foreach (var (node, rect) in nodePositions)
            DrawAtlasQuestIndicator(node, rect);
        if (perf) PerfMonitor.Record("Render.Indicators", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        foreach (var (node, rect) in nodePositions)
            DrawAtlasModLines(node, rect);
        foreach (var (node, rect) in nodePositions)
            DrawNodeLabels(node, rect);
        foreach (var node in specialNodes) {
            var rect = GetNodeRect(node);
            if (rect.Width > 0)
                DrawSpecialMapName(node, rect);
        }
        if (perf) PerfMonitor.Record("Render.Labels", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        DrawHoveredNodeOverTooltip();
        if (perf) PerfMonitor.Record("Render.HoveredOverTooltip", Stopwatch.GetTimestamp() - t0);

        t0 = Stopwatch.GetTimestamp();
        DrawIconTooltips();
        if (perf) PerfMonitor.Record("Render.IconTooltips", Stopwatch.GetTimestamp() - t0);

        if (waypointSyncPending) {
            waypointSyncPending = false;
            t0 = Stopwatch.GetTimestamp();
            SyncFavoriteWaypoints();
            RemoveCompletedWaypoints();
            UpdateWaypointPaths();
            if (perf) PerfMonitor.Record("Render.WaypointSync", Stopwatch.GetTimestamp() - t0);
        }

        t0 = Stopwatch.GetTimestamp();
        try {
            foreach (var (k,waypoint) in Settings.Waypoints.Waypoints) {
                var node = NodeAtCoords(waypoint.Coordinates);
                DrawWaypoint(waypoint, node);
                DrawWaypointArrow(waypoint, node);
            }
        }
        catch (Exception e) {
            LogError("Error drawing waypoints: " + e.Message + "\n" + e.StackTrace);
        }
        if (perf) PerfMonitor.Record("Render.Waypoints", Stopwatch.GetTimestamp() - t0);

        DrawSearchHighlight();
        DrawRitualPlanHighlight();
        DrawExpeditionHighlight();
        DrawExpeditionHoverRings();
        DrawExpeditions();

        t0 = Stopwatch.GetTimestamp();
        DrawTours();
        if (perf) PerfMonitor.Record("Render.Tours", Stopwatch.GetTimestamp() - t0);

        DrawCacheProgressBar();
    }

    private RectangleF WorldAlignedRect(AtlasNodeDescription desc, RectangleF rect)
    {
        if (frameWorldToScreen == null || rect.Width <= 0)
            return rect;
        var d3d = desc?.Description3D;
        if (d3d != null)
            rect.Location = frameWorldToScreen(d3d.Position) - rect.Size / 2f;
        return rect;
    }

    private RectangleF WorldAlignedRect(Node node, RectangleF rect)
    {
        if (node == null || !node.HasWorldPos)
            return WorldAlignedRect(node?.MapNode, rect);
        if (frameWorldToScreen == null || rect.Width <= 0)
            return rect;
        rect.Location = frameWorldToScreen(node.WorldPos) - rect.Size / 2f;
        return rect;
    }

    private bool OnScreenSafe(Node x)
    {
        var project = frameWorldToScreen;
        if (project != null && x.HasWorldPos)
            return IsOnScreen(project(x.WorldPos));
        try { return IsOnScreen(x.MapNode.Element.GetClientRectCache.Center); }
        catch { return false; }
    }

    private void RebuildOnScreenSets(IEnumerable<Node> candidates)
    {
        selectedNodes.Clear();
        specialNodes.Clear();
        lastOnScreen.Clear();
        var project = frameWorldToScreen;
        foreach (var x in candidates) {
            Vector2 center;
            if (project != null && x.HasWorldPos)
                center = project(x.WorldPos);
            else {
                try { center = x.MapNode.Element.GetClientRectCache.Center; }
                catch { continue; }
            }
            if (!IsOnScreen(center))
                continue;

            lastOnScreen.Add(x);
            if (x.IsSpecial)
                specialNodes.Add(x);
            if (Settings.Graphics.DrawNodes.HasFlag(StateOf(x)))
                selectedNodes.Add(x);
        }
    }

    private static readonly Color CacheBarColor = Color.FromArgb(255, 90, 200, 255);

    private void DrawCacheProgressBar()
    {
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

            Graphics.DrawBox(bgStart, bgEnd, Color.FromArgb(180, 0, 0, 0), 3f);

            string label;
            if (refreshingCache) {
                float pct = Math.Clamp(cacheRefreshProgress, 0f, 1f);
                if (pct > 0f)
                    Graphics.DrawBox(bgStart, new Vector2(left + barWidth * pct, top + barHeight), CacheBarColor, 3f);
                label = "Updating Atlas Cache";
            } else {
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
                OverlayText, OverlayBg, true, 8, 3);
        } catch (Exception e) {
            LogError("Error drawing cache progress bar: " + e.Message);
        }
    }
    #endregion

    #region Keybinds & Events

    public void MarkWeightsDirty() => weightsDirty = true;

    private void RegisterHotkeys() {
        if (!registeredKeybindSets.Add(Settings.Keybinds)) return;
        var k = Settings.Keybinds;
        if (k.BuildModeHotkey.Value?.Key == Keys.F13 && k.ToggleToursPanelHotkey.Value?.Key == Keys.F13)
            k.BuildModeHotkey.Value = new HotkeyNodeV2.HotkeyNodeValue(Keys.NumPad8);
        RegisterHotkey(Settings.Keybinds.ToggleDrawingHotkey);
        RegisterHotkey(Settings.Keybinds.RefreshMapCacheHotkey);
        RegisterHotkey(Settings.Keybinds.QuickEditNodeHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleAtlasOverviewHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleWaypointPanelHotkey);
        RegisterHotkey(Settings.Keybinds.ToggleToursPanelHotkey);
        RegisterHotkey(Settings.Keybinds.AddTourStopHotkey);
        RegisterHotkey(Settings.Keybinds.BuildModeHotkey);
        RegisterHotkey(Settings.Keybinds.AddWaypointHotkey);
        RegisterHotkey(Settings.Keybinds.DeleteWaypointHotkey);
    }

    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        Input.RegisterKey(hotkey.Value);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey.Value); };
    }
    private void CheckKeybinds() {
        if (!AtlasPanel.IsVisible) {
            buildModeActive = false;
            if (Settings.Keybinds.QuickEditNodeHotkey.PressedOnce()) {
                if (quickEditOpen) { quickEditOpen = false; quickEditNode = null; }
                else OpenQuickEditForCurrentArea();
            }
            return;
        }

        if (Settings.Keybinds.ToggleDrawingHotkey.PressedOnce())
            Settings.Features.EnableDrawing = !Settings.Features.EnableDrawing;

        if (Settings.Keybinds.RefreshMapCacheHotkey.PressedOnce()) {
            refreshCache = true;
            clearCacheOnRefresh = true;
            lastRefreshMs = long.MinValue / 2;
        }

        if (Settings.Keybinds.ToggleWaypointPanelHotkey.PressedOnce())
            TogglePanelSection("wp");

        if (Settings.Keybinds.ToggleAtlasOverviewHotkey.PressedOnce())
            TogglePanelSection("atlas");

        if (Settings.Keybinds.ToggleToursPanelHotkey.PressedOnce())
            TogglePanelSection("tours");

        if (Settings.Keybinds.AddTourStopHotkey.PressedOnce())
            AddStopToActiveTour(GetClosestNodeToCursor());

        if (Settings.Keybinds.BuildModeHotkey.PressedOnce())
            buildModeActive = !buildModeActive;

        if (buildModeActive && Input.IsKeyDown(Keys.Tab))
            buildModeActive = false;

        if (Settings.Keybinds.AddWaypointHotkey.PressedOnce())
            AddWaypoint(GetClosestNodeToCursor());

        if (Settings.Keybinds.QuickEditNodeHotkey.PressedOnce()) {
            if (quickEditOpen) { quickEditOpen = false; quickEditNode = null; }
            else {
                var editNode = GetClosestNodeToCursor();
                if (editNode != null) { quickEditNode = editNode; quickEditOpen = true; }
            }
        }

        if (Settings.Keybinds.DeleteWaypointHotkey.PressedOnce())
            RemoveWaypoint(GetClosestNodeToCursor());

    }
    #endregion

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
            if ((c0 | c1) == 0) { clippedStart = p0; clippedEnd = p1; return true; }
            if ((c0 & c1) != 0) return false;

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

    private const int ElementTextureRef = 0x240;

    private string TextureOf(ExileCore2.PoEMemory.Element element)
    {
        try {
            long addr = element?.Address ?? 0;
            if (addr == 0)
                return null;

            var mem = GameController.Memory;
            long slot = mem.Read<long>(addr + ElementTextureRef);
            if (slot == 0)
                return null;

            long str = mem.Read<long>(slot + 8);
            if (str == 0)
                return null;

            string raw = mem.ReadStringU(str, 512);
            if (string.IsNullOrEmpty(raw))
                return null;

            int bar = raw.IndexOf('|');
            return bar > 0 ? raw[..bar] : raw;
        } catch (Exception e) {
            DebugSwallow("TextureOf", e);
            return null;
        }
    }

    private const string TooltipTexturePrefix = "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/";
    private static bool IsTooltipTexture(string textureName) =>
        textureName != null && textureName.StartsWith(TooltipTexturePrefix) && textureName.Contains("Popup");

    private static readonly HashSet<string> ExcludeTextureNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/WorldMap/WorldmapTitleBar.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/Common/AtlasSearchBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapPinsWindow/MapPinLegendBG.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/MapLegend/LegendBg.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/KeystonesUI/MainBgExpanded.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/WorldMap/MapButtonsBgMiddle.dds",
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/QuestMap/QuestMapBase.dds",
    };

    private static readonly HashSet<string> ExcludeTextureSubtrees = new(StringComparer.OrdinalIgnoreCase)
    {
        "Art/Textures/Interface/2D/2DArt/UIImages/InGame/AtlasScreen/KeystonesUI/MainBgCollapsed.dds",
    };

    private void AddExcludeRectsByTexture(ExileCore2.PoEMemory.Element element, int depth, List<RectangleF> target)
    {
        if (element == null || depth > 8 || !element.IsVisible)
            return;

        string texture = TextureOf(element);
        if (texture != null)
        {
            if (ExcludeTextureSubtrees.Contains(texture))
            {
                AddSubtreeRects(element, 0, target);
                return;
            }

            if (ExcludeTextureNames.Contains(texture))
                target.Add(element.GetClientRect());
        }

        foreach (var child in element.Children)
            AddExcludeRectsByTexture(child, depth + 1, target);
    }

    private void AddSubtreeRects(ExileCore2.PoEMemory.Element element, int depth, List<RectangleF> target)
    {
        if (element == null || depth > 12 || !element.IsVisible)
            return;

        RectangleF rect = element.GetClientRect();
        if (rect.Width > 0 && rect.Height > 0)
            target.Add(rect);

        foreach (var child in element.Children)
            AddSubtreeRects(child, depth + 1, target);
    }

    private const int ChromeScanInterval = 10;
    private readonly List<RectangleF> cachedChromeRects = [];

    #region Screen Bounds

    private void UpdateScreenBounds()
    {
        try {
            frameLogbookPopup = null;
            var size = GameController.Window.GetWindowRectangleTimeCache.Size;
            float left = 0;
            float right = size.X;

            if (UI.OpenRightPanel.IsVisible)
                right -= UI.OpenRightPanel.GetClientRect().Width;

            if (UI.OpenLeftPanel.IsVisible)
                left += UI.OpenLeftPanel.GetClientRect().Width;

            cachedScreenRect = new RectangleF(left, 0, right - left, size.Y);

            if (TickCount % ChromeScanInterval == 0 || cachedChromeRects.Count == 0) {
                cachedChromeRects.Clear();
                AddExcludeRectsByTexture(UI.WorldMap, 0, cachedChromeRects);
            }

            cachedExcludeRects.Clear();

            mapTooltipVisible = false;
            foreach (var tooltip in UI.WorldMap.Children) {
                if (tooltip == null || !tooltip.IsVisible)
                    continue;
                if (!IsTooltipTexture(TextureOf(tooltip)))
                    continue;

                RectangleF mapTooltip = tooltip.GetClientRect();
                mapTooltip.Inflate(mapTooltip.Width * 0.1f, mapTooltip.Height * 0.1f);
                cachedExcludeRects.Add(mapTooltip);
                mapTooltipVisible = true;
            }

            ScanExpeditionButtons();
            if (frameLogbookPopup != null)
            {
                RectangleF lb = frameLogbookPopup.GetClientRect();
                if (lb.Width > 0 && lb.Height > 0)
                {
                    cachedExcludeRects.Add(lb);
                    mapTooltipVisible = true;
                }
            }

            cachedExcludeRects.AddRange(cachedChromeRects);

            AddExcludeRect(UI.GameUI?.LifeOrb);
            AddExcludeRect(UI.GameUI?.ManaOrb);
            AddExcludeRect(UI.GameUI?.FlaskPanel?.Parent);
            AddExcludeRect(UI.SkillBar?.Parent);

            LayoutExpeditions();
        } catch (Exception e) {
            LogError("Error updating screen bounds: " + e.Message);
        }
    }

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

    private void CollectExcludes(float minX, float minY, float maxX, float maxY, List<RectangleF> into)
    {
        into.Clear();
        var box = new RectangleF(minX - 1f, minY - 1f, maxX - minX + 2f, maxY - minY + 2f);
        foreach (var r in cachedExcludeRects)
            if (r.Intersects(box))
                into.Add(r);
    }

    private static bool CrossesExcluded(Vector2 a, Vector2 b, List<RectangleF> rects)
    {
        foreach (var r in rects)
            if (r.Contains(a) || r.Contains(b) || SegmentIntersectsRect(a, b, r))
                return true;
        return false;
    }

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

    private static float Cross(Vector2 a, Vector2 b, Vector2 c)
        => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    #endregion

    #region Search

    private string searchBoxText = "";

    private Vector2 searchAnchor;

    private readonly HashSet<Vector2i> searchHighlight = new();

    private string searchResultQuery;
    private (int, int) searchResultVersion;
    private List<Node> searchResults;

    private string IngameSearchText()
    {
        if (!Settings.Search.HookIngameSearch) return null;
        try {
            var outer = AtlasSearchBox()?.Children;
            if (outer == null || outer.Count < 1) return null;
            var inner = outer[0]?.Children;
            if (inner == null || inner.Count < 2) return null;
            return inner[1]?.Text;
        } catch (Exception e) {
            LogError("Error reading atlas search text: " + e.Message);
            return null;
        }
    }

    private void DrawSearch()
    {
        searchHighlight.Clear();
        try {
            string query = null;
            bool anchored = false;

            if (Settings.Search.ShowSearchBox) {
                anchored = DrawSearchBox();
                if (anchored && !string.IsNullOrWhiteSpace(searchBoxText)) query = searchBoxText;
            }

            if (query == null) {
                var ingame = IngameSearchText();
                if (!string.IsNullOrWhiteSpace(ingame)) {
                    var r = AtlasSearchBox()?.GetClientRect();
                    if (r.HasValue) {
                        query = ingame;
                        searchAnchor = new Vector2(r.Value.Left, r.Value.Bottom + 4f);
                        anchored = true;
                    }
                }
            }

            if (query != null && anchored) DrawSearchResults(query.Trim());
        } catch (Exception e) {
            LogError("Error drawing search: " + e.Message);
        }
    }

    private bool DrawSearchBox()
    {
        const float width = 220f;
        var pos = Settings.Search.BoxPos;

        if (pos == Vector2.Zero) {
            var anchor = Settings.Features.AtlasButtonPos;
            if (anchor == Vector2.Zero) return false;
            pos = new Vector2(Math.Max(0f, anchor.X - width - 8f), anchor.Y);
        }

        ImGui.SetNextWindowPos(pos, ImGuiCond.Once);
        ImGui.SetNextWindowSize(new Vector2(width, 0f), ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.85f);

        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse
                  | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;

        bool ok = false;
        if (ImGui.Begin("##exilemaps_search", flags)) {
            ImGui.TextDisabled("::");
            ExileImGui2.Controls.Tip("Drag to move.");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##q", "Search maps...", ref searchBoxText, 128);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Filters: map: content: biome: mod:");

            var actual = ImGui.GetWindowPos();
            Settings.Search.BoxPos = actual;
            searchAnchor = new Vector2(actual.X, actual.Y + ImGui.GetWindowSize().Y + 4f);
            ok = true;
        }
        ImGui.End();
        return ok;
    }

    private List<Node> SearchMatches(string query)
    {
        var version = (mapCacheVersion, weightsRecalcVersion);
        if (searchResults != null && searchResultQuery == query && searchResultVersion == version)
            return searchResults;

        searchResults = UnvisitedNodes(query, 0);
        searchResultQuery = query;
        searchResultVersion = version;
        return searchResults;
    }

    private void DrawSearchResults(string query)
    {
        var matches = SearchMatches(query);
        if (matches == null || matches.Count == 0) return;

        int shown = Math.Min(Math.Max(1, Settings.Search.MaxResults), matches.Count);
        bool canPan = HacksCameraPanReady;
        var steps = ComputeStepCounts();

        bool drag = ImGui.GetIO().KeyCtrl;
        if (!drag) ImGui.SetNextWindowPos(searchAnchor + Settings.Search.ResultsOffset, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0.9f);
        var flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize
                  | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoSavedSettings
                  | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.AlwaysAutoResize;
        if (!drag) flags |= ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##exilemaps_search_results", flags)) {
            if (drag) Settings.Search.ResultsOffset = ImGui.GetWindowPos() - searchAnchor;

            foreach (var n in matches)
                searchHighlight.Add(n.Coordinates);

            if (ImGui.BeginTable("##srt", 4, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg)) {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Steps");
                ImGui.TableSetupColumn("X");
                ImGui.TableSetupColumn("Y");
                ImGui.TableHeadersRow();

                for (int i = 0; i < shown; i++) {
                    var n = matches[i];
                    bool wp = HasWaypoint(n);
                    string dist = steps.TryGetValue(n.Coordinates, out var st) ? st.ToString() : "-";

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.Selectable($"{(wp ? "*" : " ")} {n.Name}" + "##sr" + i, wp, ImGuiSelectableFlags.SpanAllColumns);

                    if (ImGui.IsItemClicked(ImGuiMouseButton.Left)) {
                        if (wp) RemoveWaypoint(n); else AddWaypoint(n);
                    } else if (canPan && ImGui.IsItemClicked(ImGuiMouseButton.Right)) {
                        GotoNode(n);
                    }

                    ExileImGui2.Controls.Tip(canPan
                        ? "Left click: toggle waypoint. Right click: pan. Ctrl drag: move."
                        : "Left click: toggle waypoint. Ctrl drag: move.");

                    ImGui.TableNextColumn();
                    ImGui.Text(dist);
                    ImGui.TableNextColumn();
                    ImGui.Text(n.Coordinates.X.ToString());
                    ImGui.TableNextColumn();
                    ImGui.Text(n.Coordinates.Y.ToString());
                }
                ImGui.EndTable();
            }

            if (matches.Count > shown)
                ImGui.TextDisabled($"(+ {matches.Count - shown} more...)");
        }
        ImGui.End();
    }

    private const int SearchRingSegments = 24;

    private void DrawSearchHighlight()
    {
        if (!Settings.Search.HighlightMatches || searchHighlight.Count == 0) return;

        double secs = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        float pulse = 0.5f + 0.5f * MathF.Sin((float)(secs * 3.0));

        Color color = Settings.Search.HighlightColor;
        Color outer = Color.FromArgb((int)(color.A * (1f - pulse * 0.8f)), color.R, color.G, color.B);

        var cam = AtlasPanel?.Camera;
        float spacing = cam == null ? 0f : NodeWorldSpacing();
        float radius = spacing * Settings.Search.HighlightSize;

        foreach (var (node, rect) in nodePositions) {
            if (!searchHighlight.Contains(node.Coordinates)) continue;

            var d3d = radius > 0f ? node.MapNode?.Description3D : null;
            if (d3d != null) {
                Ring(d3d.Position, radius, color, 3f);
                Ring(d3d.Position, radius * (1.15f + pulse * 0.85f), outer, 2f);
            } else {
                float baseR = MathF.Max(rect.Width, rect.Height);
                Graphics.DrawCircle(rect.Center, baseR * 0.7f, color, 3f, 32);
                Graphics.DrawCircle(rect.Center, baseR * (0.8f + pulse * 0.6f), outer, 2f, 32);
            }
        }

        void Ring(Vector3 origin, float r, Color c, float thickness)
        {
            Vector2 prev = default;
            for (int i = 0; i <= SearchRingSegments; i++) {
                float a = i * MathF.Tau / SearchRingSegments;
                var p = cam.WorldToScreen(new Vector3(origin.X + MathF.Cos(a) * r, origin.Y + MathF.Sin(a) * r, origin.Z));
                if (i > 0) Graphics.DrawLine(prev, p, thickness, c);
                prev = p;
            }
        }
    }

    private void DrawRitualPlanHighlight()
    {
        if (ritualPlanSteps.Count == 0) return;

        double secs = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        float pulse = 0.5f + 0.5f * MathF.Sin((float)(secs * 3.0));

        Color next = Color.FromArgb(255, 255, 152, 105);
        Color later = Color.FromArgb(150, 255, 152, 105);

        var cam = AtlasPanel?.Camera;
        float spacing = cam == null ? 0f : NodeWorldSpacing();
        float radius = spacing * 0.42f;

        foreach (var (node, rect) in nodePositions) {
            if (!ritualPlanSteps.TryGetValue(node.Coordinates, out int step)) continue;

            bool isNext = step == 0;
            Color c = isNext ? next : later;
            float thick = isNext ? 4f : 2f;

            var d3d = radius > 0f ? node.MapNode?.Description3D : null;
            if (d3d != null) {
                Ring(d3d.Position, radius, c, thick);
                if (isNext) Ring(d3d.Position, radius * (1.15f + pulse * 0.55f),
                    Color.FromArgb((int)(next.A * (1f - pulse * 0.8f)), next.R, next.G, next.B), 3f);
            } else {
                float baseR = MathF.Max(rect.Width, rect.Height);
                Graphics.DrawCircle(rect.Center, baseR * 0.7f, c, thick, 32);
            }
        }

        void Ring(Vector3 origin, float r, Color c, float thickness)
        {
            Vector2 prev = default;
            for (int i = 0; i <= SearchRingSegments; i++) {
                float a = i * MathF.Tau / SearchRingSegments;
                var p = cam.WorldToScreen(new Vector3(origin.X + MathF.Cos(a) * r, origin.Y + MathF.Sin(a) * r, origin.Z));
                if (i > 0) Graphics.DrawLine(prev, p, thickness, c);
                prev = p;
            }
        }
    }

    private float NodeWorldSpacing()
    {
        if (worldSpacing > 0f) return worldSpacing;

        var samples = new List<float>();
        foreach (var (node, _) in nodePositions) {
            var a = node.MapNode?.Description3D?.Position;
            if (a == null) continue;
            foreach (var nb in node.Neighbors.Values) {
                var b = nb?.MapNode?.Description3D?.Position;
                if (b == null) continue;
                float steps = Vector2.Distance(new Vector2(node.Coordinates.X, node.Coordinates.Y),
                                               new Vector2(nb.Coordinates.X, nb.Coordinates.Y));
                if (steps <= 0f) continue;
                samples.Add(Vector2.Distance(new Vector2(a.Value.X, a.Value.Y), new Vector2(b.Value.X, b.Value.Y)) / steps);
            }
        }

        if (samples.Count < 20) return 0f;

        samples.Sort();
        worldSpacing = samples[samples.Count / 2];
        if (Settings.Features.DebugLogging)
            LogMessage($"Search ring: world units per grid step = {worldSpacing:F1} (from {samples.Count} links)");
        return worldSpacing;
    }

    private float worldSpacing;

    #endregion

    #region Debug Helpers

    private void DebugSwallow(string context, Exception e)
    {
        if (Settings.Features.DebugLogging)
            LogError($"{context}: {e.Message}");
    }

    #endregion

}
