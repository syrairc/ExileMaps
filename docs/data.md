# Data sources, model, and settings

Read this when working on data scraping, profiles, import/export, settings, or the data model classes.

## Data sources

Map types, content types, and biomes are scraped from game endgame file lists — not loaded from JSON at startup, not read off the live atlas:

| Source | Method | Live dict |
|---|---|---|
| `GameController.Files.EndgameMaps` | `UpdateMapData` | `Settings.Maps.Maps` |
| `GameController.Files.EndgameMapContent` + `EndgameMapContentVisualIdentity` | `UpdateContentData` | `Settings.Maps.Content.ContentTypes` |
| `GameController.Files.EndgameMapBiomes` | `UpdateBiomeData` | `Settings.Maps.Biomes.Biomes` |
| `json/rumors.json` (bundled) | `UpdateRumorData` | `Settings.Expeditions.RumorWeights` |

Scrape runs once in `Tick` (gated on `gameFilesScraped`) and manually via hotkeys. Side-effects: writes `json/maps.json`, `content.json`, `biomes.json`. All three return `false` if game files not loaded yet. `json/rumors.json` maps Island Rumour text → `{content, description, defaultWeight}`; seeds the per-profile `Settings.Expeditions.RumorWeights` (`ObservableDictionary<string,Rumor>`) via `UpdateRumorData()`, editable in the Content tab.

`UpdateMapData` calls `MergeDuplicateMapsByName` first — collapses entries sharing a display Name into one (union of ids). Node matching falls back to `MatchID(IDs)` so every merged id still resolves.

`json/exilemaps_weights.json` — bundled default weights only. Applied by `LoadDefaultWeights()`.

The live `Settings.Maps.*` dicts are the working state. They persist via ExileCore2 settings (Newtonsoft) between runs and gain new entries on each scrape. `LoadProfile` overlays profile data onto them (never clears — dicts hold scraped definitions).

## Weight profiles

`Classes/WeightProfile.cs` + `ExileMapsSettings.ProfileSettings`. A profile stores per-map/content/biome weights + display settings (colors, highlight, favorite, icon).

- **`SaveCurrentProfile`** — snapshots live `Settings.Maps.*` into active profile.
- **`LoadProfile`** — overlays a profile onto live dicts; switching auto-saves first; calls `OnProfileApplied` → force cache refresh (bypasses throttle).
- **`SeedProfiles`** — called after first scrape; seeds the Default profile with bundled defaults if not already present, then applies the active profile.
- Weight changes → `weightsDirty` → debounced `RecalculateWeights` (~500ms). Don't recompute synchronously in setters.

## Import / Export (ExileMaps.SettingsIO.cs)

All use native comdlg32 dialog on a background STA thread (WinForms `ShowDialog` on the render thread freezes/faults the DirectX overlay). Dialog stashes chosen path in a `volatile` field; `ProcessPendingWeightFile` (called from `Render`, on the plugin thread) does the actual read/write so settings access stays single-threaded.

| Scope | Serializer | Notes |
|---|---|---|
| Weights | System.Text.Json | `WeightExport` DTO; keyed by settings key; maps fall back to Name match for cross-install presets |
| Settings | Newtonsoft `PopulateObject` | Whole settings object mutated in place |
| Profile | System.Text.Json `WeightProfile` | Imports as new named profile (deduped), switches to it |

## Data model (Classes/)

| Class | Role | Notes |
|---|---|---|
| `Node` | Cached atlas node (runtime) | Most fields `[JsonIgnore]` — transient. `IsAttempted = !IsUnlocked && IsVisited`. `IsFavorited` = map type OR any content is favorited. `IsCompleted` = `[JsonIgnore]` transient bool set in cache build from `node.Element.IsCompleted`; used by `ComputeAtlasStats`. `SpecialModifiers` = first line of each atlas-node child tooltip (special content modifier strings), scraped in `AddSpecialModifiers` during static-resolve; feeds search + Atlas Overview tally. |
| `Map` | Map-type config | `ID` deprecated (import compat); `IDs`/`ShortestId` live. Persisted + scraped. |
| `Content` | Content type | weight/color/highlight/favorite + scraped `AtlasIcon`. |
| `Biome` | Biome | Rendering removed; weight still feeds node weight. |
| `WeightProfile` (+`MapProfileEntry`/`ContentProfileEntry`/`BiomeProfileEntry`) | Saved preset | |
| `WeightExport` | Weights-only export DTO | maps/content/biomes key→weight |
| `Waypoint` | User waypoint | `PathFromStart` (path to nearest visited), `PathWeight`, `AutoCreated`, `StepCount` |
| `Tour` (+`TourStop`) | Self-contained named route | `Show`, `Color`, ordered `List<TourStop>` of bare coords; runtime `Segments`/`ResolvedStops`/`Skipped`/`BuiltVersion` (`[JsonIgnore]`). Separate from waypoints. |
| `SpriteIcon` / `SpriteAtlas` | Icon enum + UV helpers | `Icons_Desaturated.png`, 48 icons |
| `ObservableDictionary` | Settings collection with change events | Drives weight recalculation |
| `Job` | Background work wrapper | Cache refresh runs here |
| `NativeFileDialog` | comdlg32 wrapper | Import/export off render thread |
| `JsonColorConverter`, `Vector2iConverter` | (de)serialize `Color`/`Vector2i` | From either JSON namespace |
| `ColorUtils` | `InterpolateColor` for weight gradients | |

## Settings & UI (ExileMapsSettings.cs)

`ISettings`. Top level: import/export buttons + **Profiles**, then **Features, Keybinds, Graphics, Maps** (with **Biomes** and **Content** nested inside Maps), **Waypoints**. Heavy use of `CustomNode.DrawDelegate` for hand-drawn ImGui tables (search box, "Set all" bulk row, per-row weight slider + color pickers + icon picker + favorite toggle).

`SettingsHelpers` — `ToggleAll`, `SetAllColor`, `IconPicker`, reset/import/export buttons.

In-game panels (toggled by hotkey or the floating button bar, drawn from `Render`): Waypoint Panel, Quick Edit popup, Node Debug popup, Atlas Overview panel, Tours panel. The three movable panels (Waypoints/Overview/Tours) persist their window pos/size in a `PanelRect` setting each (`Settings.Waypoints.PanelRect`, `Settings.AtlasOverview.PanelRect`, `Settings.Tours.PanelRect`) — restored on open via `BeginPersistedWindow`, saved each frame via `SavePersistedWindow`.

`AtlasOverviewSettings` (`Settings.AtlasOverview`, `[Menu("Atlas Overview")]`):
- `ReachableSteps` — how many BFS steps from the explored frontier to count as reachable (used by `ComputeAtlasStats` and the overview panel tally).
- `ShowProgressReadout` — toggles the always-on corner HUD text.
- `ReadoutCorner` — enum (TopLeft / TopRight / BottomLeft / BottomRight) controlling which screen corner the readout appears in.
- `SearchPingColor` — color of the pulsing rings drawn by `DrawSearchPing`.

`TourSettings` (`Settings.Tours`, `[Menu("Tours")]`):
- `ShowTours` — master toggle for drawing all enabled tour routes.
- `ActiveTourId` — id of the tour the Add-Tour-Stop hotkey targets.
- `Tours` — `ObservableDictionary<string, Tour>` of named tours.
- `AutoTourContent` (persisted `List<string>`), `AutoTourStepRange`, `AutoTourScreenMarginPct` — inputs for `AutoCreateTour` (which content to route through, max steps between stops, and how far off-screen a match may sit).

`FeatureSettings.ShowPanelButtons` — toggles the floating bottom-center button bar that opens the Waypoints/Overview/Tours panels.
`FeatureSettings.ShowAtlasSearchBox` — toggles the floating top-center atlas search box (`DrawAtlasSearchBox`), which drives the same `searchPingText` / `DrawSearchPing` as the Atlas Overview search.

`ToggleAtlasOverviewHotkey` (default `Pause`), `ToggleToursPanelHotkey` and `AddTourStopHotkey` (default `F13`, unbound) — in `HotkeySettings` under the "Panels" separator group.

Real default hotkeys: Refresh = `Home`, Add Waypoint = `Insert`, Delete Waypoint = `Delete`, Waypoint Panel = `End`. All others default to `F13` (effectively unbound). The `[Menu]` tooltip "Default: …" strings are stale relative to actual `HotkeyNodeV2` defaults.
