# CLAUDE.md

Guidance for AI agents. Keep accurate — **update CLAUDE.md and the relevant `docs/` file after every major architectural change.**

## What this is

**ExileMaps** — ExileCore2 HUD plugin for PoE2. Draws overlay on the Atlas: highlights nodes by weight, marks content (Breach, Ritual…), flags atlas-passive/quest maps, draws connections, manages waypoints with path routing. Rendering via ExileCore2 `Graphics` + ImGuiNET.

Class library (`OutputType=Library`), loaded as a plugin by ExileCore2.

> Tower tablet mods removed from PoE2 — `Effect`/`Mod` classes, `mods.json`, `Node.Effects` are gone. Don't reintroduce.

## Build

- `net8.0-windows`, x64, WinForms enabled
- Engine refs (`ExileCore2.dll`, `GameOffsets2.dll`) via `exileCore2Package` env var → HUD folder
- NuGet: `ImGui.NET` 1.90.0.1, `Newtonsoft.Json` 13.0.3, `SharpDX.Mathematics` 4.2.0
- Preferred: drop source under HUD's `Plugins/Source/ExileMaps` — ExileCore2 wires output path
- `json/*.json` and `textures/*.png` are `CopyToOutputDirectory=PreserveNewest`
- No tests, no CI. Verification = in-game build on the Atlas.
- **DevTree2src** (`c:\Exile\DevTree2src`) — live memory explorer for atlas/UI data
- **ExileCore2src** (`c:\Exile\ExileCore2src`) — decompiled `AtlasElements.cs`, `Elements.cs` for property/offset reference

## File map

`ExileMapsCore` is a **partial class** split across:

| File | Content |
|---|---|
| `ExileMaps.cs` | Fields, `Initialise`, `AreaChange`, `Tick`, `Render`, keybinds/events |
| `ExileMaps.Rendering.cs` | All draw methods (nodes, lines, rings, labels, indicators) |
| `ExileMaps.MapCache.cs` | `RefreshMapCache`, node caching, content/passive detection, special-modifier (child-tooltip) scan, weight recalc |
| `ExileMaps.GameData.cs` | `UpdateMapData`, `UpdateContentData`, `UpdateBiomeData` |
| `ExileMaps.Pathfinding.cs` | `FindPathToNearestCompleted`, `ComputeStepCounts`, `GetAtlasPanelList` |
| `ExileMaps.Panels.cs` | `DrawSettings` override: regroups the flat engine settings into **tabs** (General/Appearance/Maps/Content/Waypoints/Panels/Keybinds) by drawing named holders + CustomNode delegates in the wanted order (`DrawHolder(name)` / `DrawCustom(delegate)`, `_holderIndex` built from `Drawers`). ISettings classes untouched, so saved JSON is unaffected. **Gotcha: a new `[Menu]` setting won't show until it's added to a `Draw*Tab` method** (a missing name logs `[Tabs] setting not found`). Also: Debug, Quick Edit, Node Debug, Waypoint panels (ImGui) |
| `ExileMaps.Waypoints.cs` | Waypoint draw, path draw, add/remove, sync favorites |
| `ExileMaps.AtlasOverview.cs` | Atlas Overview: reachable-content tally, progress readout HUD, node search ping, visible-tour summary |
| `ExileMaps.Expeditions.cs` | Expeditions: snapshot `AtlasPanel.Buttons`, panel (rumour->content, search), per-expedition map highlight, nearest-map waypoint, optional on-atlas marker (anchored to the nearest member map node — the button's own Element rect never resolves), rumour-data load. Rumour weights edited at the bottom of the Content settings tab. |
| `ExileMaps.Tours.cs` | Named tours: build/optimize/draw routes, Tours panel, floating panel-button bar, interactive Build Mode (click nodes to add/remove — captures clicks via an invisible ImGui button over the hovered node) |
| `ExileMaps.SettingsIO.cs` | Import/export, profile apply, weight reset, old-settings migration |
| `ExileMaps.HtmlExport.cs` | Export whole atlas as a zoomable SVG + stats dashboard in a self-contained HTML file. Snapshots the cache off-lock, lays out from grid coords, svg-pan-zoom via CDN+SRI, inline JS for toggles/search/detail. **Celestial theme:** inlined nebula-tile `<pattern>`s (4 moods + Off, `feTurbulence` filters, pans/zooms with the map via a `patternTransform` rAF sync on a behind-the-atlas `#atlas-bg` SVG) + inlined star-glyph `<symbol>` sheets giving 3 marker modes (dots / colored star icons / weight-tinted grey icons via `currentColor`); all toggleable in-page + localStorage-persisted (defaults: celestial on, star icons, Deep Field). Source design assets live in `atlas-export/` but are **inlined as C# string constants** — nothing external is deployed. Triggered by the `export` floating panel button (see `ExileMaps.Tours.cs` button bar) → overwrites `exports\atlas.html` and opens it |
| `ExileMaps.ScreenBounds.cs` | `UpdateScreenBounds`, `IsOnScreen`, `IsLineVisible` |
| `ExileMapsSettings.cs` | `ISettings` — all settings classes and ImGui UI |
| `Classes/` | Data model: `Node`, `Map`, `Content`, `Biome`, `WeightProfile`, `Waypoint`, `Tour`, etc. |

## Working agreements

- Hobbyist overlay. Small, reviewable changes. State that verification requires in-game build.
- Defensive try/catch + `LogError` on anything touching AtlasPanel/game memory/`GameController.Files`.
- Don't add absolute paths to csproj; the HUD owns output paths.
- **Commits are manual** — don't `git commit`/push unless explicitly asked.
- **Update CLAUDE.md and the relevant `docs/` file after every major architectural change.**

## Detail docs (read when working on that area)

- [`docs/arch.md`](docs/arch.md) — lifecycle, core architecture, threading, pathfinding, waypoints
- [`docs/data.md`](docs/data.md) — data sources, weight profiles, data model, import/export, settings UI
- [`docs/rendering.md`](docs/rendering.md) — rendering pipeline, icons, sprite atlas, path textures
- [`docs/gotchas.md`](docs/gotchas.md) — conventions, gotchas, pitfalls, performance notes
