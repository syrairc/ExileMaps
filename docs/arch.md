# Architecture

Read this when working on lifecycle, cache, pathfinding, waypoints, or threading.

## Lifecycle (ExileMaps.cs)

`ExileMapsCore : BaseSettingsPlugin<ExileMapsSettings>`. Static `Main` self-reference used throughout for settings/logging access.

- **`Initialise()`** — registers hotkeys, subscribes to settings events (`SubscribeToEvents`), loads textures (core `Icons.png`, local `arrow.png`, path sprites from `textures/path-*.png`, `Icons_Desaturated.png`), calls `EnsureDefaultProfile`, `DetectOldSettings` (sniffs/backs up pre-v2 settings), sets `CanUseMultiThreading=true`. Does NOT scrape map/content/biome data.
- **`AreaChange()`** — sets `refreshCache = true`.
- **`Tick()`** — grabs `AtlasPanel`; returns early if not visible. Sets `AtlasHasBeenClosed = true` while closed (triggers async clear-and-refresh on reopen: `clearCacheOnRefresh = true`). Scrapes data once (`gameFilesScraped` gate): `UpdateMapData` → `UpdateContentData` → `UpdateBiomeData` → `SeedProfiles`. Atlas streams nodes progressively after open (tracks `cacheBehind = Descriptions.Count > mapCache.Count`). Throttles cache refresh (`MapCacheRefreshRate`; bypassed when cache is empty or behind + >0.5s elapsed). Debounces weight recalculation (~500ms).
- **`Render()`** — `ProcessPendingWeightFile`, `CheckKeybinds`, panels (Waypoint/QuickEdit/NodeDebug, plus `if (atlasOverviewOpen) DrawAtlasOverviewPanel()` — drawn with the other panel toggles near the top of Render). Recomputes on-screen node set every `OnScreenRecomputeInterval` (5) frames into `selectedNodes`. Resolves node rects into `nodePositions` then draws in fixed z-layers. Draws waypoints. After waypoints, draws top layers: `DrawProgressReadout` (corner HUD), `DrawSearchPing` (pulsing rings on matching nodes), `DrawTour` (multi-waypoint tour segments). Draws `DrawCacheProgressBar`.

## Core architecture

- **`mapCache : Dictionary<Vector2i, Node>`** — keyed by atlas grid coordinate. Refreshed from `AtlasPanel.Descriptions`. Guarded by `mapCacheLock`; refresh runs on a background `Job`. Reads use `.AsParallel()`.
- **Two-phase cache refresh** (`ExileMaps.MapCache.cs`, `RefreshMapCache`):
  1. Phase 1 (0–80% progress): cache/update every node — reads flags, resolves `MapType` via `ResolveMapType`, detects content via `AddNodeContentFromIdentity`, sets atlas-passive flags via `SetAtlasPassive`.
  2. Phase 2 (80–90% progress): build connections — forward + reverse lookups from a pre-built snapshot of `AtlasPanel.Points` (one game-memory read total). Two-phase prevents one-directional adjacency on first open.
  3. Post-refresh: `RecalculateWeights`, `SyncFavoriteWaypoints`, `UpdateWaypointPaths`, `SnapshotExpeditions` (reads `AtlasPanel.Buttons` → transient `expeditions`, lock-guarded by `mapCacheLock`), bump `mapCacheVersion`.
- **`AtlasHasBeenClosed`** flag — triggers async clear-and-refresh when atlas reopens, so reload bar shows instead of freezing the render thread.
- **`cacheRefreshProgress`** (volatile float, 0..1) — drives progress bar. Updated from background job, read by render thread without a lock.
- **Map type resolution** (`ResolveMapType`): O(1) hit on short id → fallback to `mapIdIndex` (id→Map dict, rebuilt only when `Settings.Maps.Maps.Count` changes, not per-node) → new blank `Map`.
- **`mapCacheVersion`** bumped on cache refresh; **`weightsRecalcVersion`** bumped on weight recalc. Both gate memoized results so BFS/filter/sort don't rerun every render frame.

## Content and atlas-passive detection

**Content** — `AddNodeContentFromIdentity`: reads `node.Element.ContentIdentity[].Id`, strips spaces, matches against `ContentTypes` dict keys. No texture-name matching (removed).

**Atlas passives** — `SetAtlasPassive`: reads `node.Element.AtlasEntry.PassiveSkill.Id` and `node.Element.IsCompleted`.
- `GivesAtlasPoint` = id contains `"Inside"` AND not completed
- `HasAtlasQuest` = id contains `"AtlasQuest"` AND not completed

## Threading

- Cache refresh on a `Job` (background thread). `mapCacheLock` guards mutations.
- `cacheRefreshProgress` is `volatile` — render thread reads without lock.
- File import/export: background STA thread (comdlg32); result processed on plugin thread in `ProcessPendingWeightFile`.
- Weight recalculation takes a snapshot under `mapCacheLock`, then operates outside the lock.

## Pathfinding (ExileMaps.Pathfinding.cs)

- **`FindPathToNearestCompleted(destination)`** — BFS outward from the waypoint destination. Stops expanding from visited nodes. Returns path `[anchor, ..., destination]` (anchor = nearest visited map). Among equal-distance routes, prefers highest summed map weight (weight of intermediate incomplete maps only; visited nodes contribute 0 to avoid the pinned-to-500 swamping the total). Returns `(null, 0)` if no visited map reachable.
- **`ComputeStepCounts()`** — multi-source BFS seeded with all visited nodes at distance 0. Returns `Dictionary<Vector2i, int>` (step count from explored region). Memoized against `mapCacheVersion`.
- **`GetAtlasPanelList(filter, useRegex, sortBy, sortBy2, maxSteps, maxItems)`** — memoized node list. Recomputed only when `(mapCacheVersion, weightsRecalcVersion, filter, useRegex, sortBy, sortBy2, maxSteps, maxItems)` changes. Supports regex and multi-sort.
- **`FindPath(from, to)`** — plain BFS from `from` to `to`. Returns full path `[from..to]` as a list, or `null` if `to` is unreachable. Used by tour segment building.
- **`GetClosestNodeToCursor()`** — uses `ImGui.GetMousePos()` (stays valid over fog; UIHoverElement is the panel background over unrevealed areas).
- **`UpdateWaypointPaths()`** — called from `RefreshMapCache` and on add/remove waypoint; runs `FindPathToNearestCompleted` for each waypoint.

## Waypoints (ExileMaps.Waypoints.cs)

- Persisted in `Settings.Waypoints.Waypoints` (`ObservableDictionary<string,Waypoint>`, keyed by coord string).
- `AddWaypoint` / `RemoveWaypoint` — each recomputes waypoint paths after mutation.
- `SyncFavoriteWaypoints` — auto-creates waypoints for favorited maps (`Waypoint.AutoCreated = true`); removes auto-created waypoints when map is un-favorited.
- Waypoint panel has a `waypointListFilter` (0 = All, 1 = Manual, 2 = Auto-created).
- Path drawing: animated via `TickCount * WaypointDashSpeed` phase; textured via `PathTextureStyle` (Comet/Chevron/DoubleChevron/Capsule); falls back to plain dashed line if texture missing or `UseNodeIcons` off.

## Atlas Overview (ExileMaps.AtlasOverview.cs)

- **`ComputeAtlasStats()`** — memoized on `(mapCacheVersion, weightsRecalcVersion, ReachableSteps)`. Returns `AtlasStats` struct: maps run/completed, atlas points total/reachable, atlas quests reachable, reachable maps, and per-content counts within N steps of the explored frontier.
- **`DrawProgressReadout()`** — always-on corner HUD text (maps run/completed, atlas points reachable/total). Corner is user-configurable.
- **`DrawAtlasOverviewPanel()`** — ImGui panel toggled by `ToggleAtlasOverviewHotkey` (default Pause): node search box, reachable-content tally table, atlas point/quest/map summary, and a read-only summary of currently visible tours.
- **`MatchesSearch(node, text)` / `DrawSearchPing()`** — pulsing rings drawn over on-screen nodes whose name or content name matches the search text. Animated via `TickCount`.

## Named Tours (ExileMaps.Tours.cs)

- Tours are self-contained named routes, persisted in `Settings.Tours.Tours` (`ObservableDictionary<string,Tour>`). Each `Tour` owns an ordered `List<TourStop>` of bare coordinates — independent of waypoints; adding a stop never creates a waypoint. Multiple tours can be visible at once, each with its own `Show` toggle and `Color`. `Settings.Tours.ActiveTourId` is the tour the Add-Tour-Stop hotkey targets; `ShowTours` is the master visibility toggle.
- **`BuildTour(Tour t)`** — builds path segments in the tour's manual stop order: seg0 = lead-in from the explored frontier (`FindPathToNearestCompleted`) to stop0; seg_i = `FindPath(stop_{i-1}, stop_i)`. Unresolvable/unreachable stops are recorded in `t.Skipped` and skipped without breaking the chain. Stamps `t.BuiltVersion = mapCacheVersion`; rebuilt lazily by `DrawTours` whenever stale.
- **`OptimizeTour(Tour t)`** — greedy nearest-neighbor reorder (first = nearest to frontier, each next = shortest `FindPath`), written back to `t.Stops`, then rebuilt.
- **`DrawTours()`** — for each shown tour, rebuilds if stale, draws each segment via shared `DrawPath` in the tour color plus 1-based order numbers at each resolved stop.
- **`AddStopToActiveTour(Node)`** — toggles a node in the active tour (auto-creates a first tour if none). **`DrawToursPanel()`** — dedicated ImGui panel (toggled by `ToggleToursPanelHotkey`) to add/delete/rename/recolor tours, set the active tour, reorder/remove stops, and Optimize/Build. **`DrawPanelButtonBar()`** — floating bottom-center button bar (gated by `Features.ShowPanelButtons`) to open the Waypoints/Overview/Tours panels.
- **`AutoCreateTour()`** — builds a tour from unvisited nodes carrying any selected content (`Settings.Tours.AutoTourContent`, same content list as the Atlas Overview tally) whose element center sits within the screen rect inflated by `AutoTourScreenMarginPct`. Starts at the match nearest the explored frontier, then greedily chains to the nearest remaining match within `AutoTourStepRange` steps (via `FindPath`) until none remain in range. Names the tour `Auto: <content…>`, sets it active, and builds it. UI lives in the `DrawAutoTourSection()` collapsing header in the Tours panel.
