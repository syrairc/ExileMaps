# ExileMaps

**An Atlas overlay for Path of Exile 2.** ExileMaps draws helpful information on top of the in-game World Map (the Atlas) so you can see, at a glance, which maps are worth running and how to get to them.

It highlights map nodes, scores each one with a configurable **weight** (a "how good is this map?" number), marks the **content** on each map (Breach, Ritual, Expedition, etc.), flags maps that give **atlas passive points** or **atlas quests**, and lets you drop **waypoints** with on-screen arrows and a shortest-path line from where you've already explored.

> ExileMaps is a plugin for the third-party **ExileCore2** overlay (a "HUD"). It only *reads* the game and draws on top of it — it never clicks, moves, or plays for you. You need the ExileCore2 HUD installed to use it.

---

## What you'll see on the Atlas

- **Colored map nodes** — each unvisited map gets an icon/circle. By default the color goes from red (low value) to green (high value) based on its weight, so good maps pop out.
- **Map names + weight %** — the map's name and how good it is relative to everything on screen.
- **Content rings** — a colored ring around a node for each type of content it has (Breach, Ritual, Delirium, bosses, Corrupted, etc.).
- **Connection lines** — lines between connected maps, colored by whether they're locked, unlocked, or already done.
- **Favorite stars** — a gold star above maps you've marked as favorites.
- **Atlas point / quest markers** — a small silver star for maps that grant an atlas passive point, and a gold "!" for maps with an atlas quest.
- **Waypoints** — a marker above any map you've pinned, an arrow at the screen edge pointing to off-screen waypoints, and a line tracing the shortest route from explored territory.

---

## Requirements

- **Path of Exile 2**
- The **ExileCore2** HUD/overlay, installed and working

---

## Installation

Most people install ExileMaps the same way as any other ExileCore2 plugin:

1. Get the plugin (either a release build, or the source code).
2. Put it in your HUD's plugins folder:
   - **Compiled build:** copy the ExileMaps folder into your HUD's `Plugins/Compiled/` folder.
   - **Source code:** copy the source into your HUD's `Plugins/Source/ExileMaps/` folder — the HUD will build it for you the next time it starts.
3. Start the HUD (and the game), then open the HUD settings and **enable ExileMaps**.

The plugin ships with everything it needs (`json/` data files and `textures/` icons) — make sure those folders stay next to the plugin.

---

## First-time setup

1. **Enable the plugin.** In the HUD settings, find ExileMaps and turn on the **Enable** toggle.
2. **Open the Atlas in-game.** The first time you open the World Map, ExileMaps reads the current league's map, content, and biome lists straight from the game and fills in its tables automatically. Give it a second or two. (Highlighting is on by default, so you should immediately see nodes light up.)
3. **Set your hotkeys.** Most hotkeys are **unbound** out of the box (they default to F13, which most keyboards don't have). Open **Keybinds** and bind the ones you'll use — at minimum the Waypoint Panel and the data-update keys. A few already have sensible defaults (see the table below).
4. **Check your weights.** ExileMaps comes with a starter set of weights so good maps are already highlighted. You can fine-tune them any time under **Maps**, **Content**, and **Biomes**.
5. **Mark your favorites.** Tick the **Fav** box next to the maps you always want to run — they'll get a star on the Atlas (and can auto-create waypoints, see below).

That's it — you're ready to go. Everything below is optional tuning.

---

## Default hotkeys

| Action | Default key | Notes |
|---|---|---|
| Open/close the Waypoint Panel | **End** | The big in-game panel for waypoints + finding good maps. |
| Add a waypoint (map under cursor) | **Insert** | Hover a map and press. |
| Remove a waypoint (map under cursor) | **Delete** | |
| Refresh the map cache | **Home** | Force a re-scan if something looks stale. |
| Quick Edit a node | *unbound* | Hover a map, press, and tweak that map type without opening settings. |
| Toggle the waypoints display | *unbound* | Show/hide all waypoints + arrows. |
| Update Map / Content / Biome data | *unbound* | Re-reads the game's lists (use after a league/patch). |
| Toggle showing locked/unlocked/visited/hidden nodes | *unbound* | Quick on/off while browsing. |
| Node Debug / Debug Mode | *unbound* | For troubleshooting; most people won't need these. |

You can rebind everything under **Keybinds**.

---

## Configuring

### Weights (how maps get scored)
A map's weight is just the sum of:
- the **map type's** base weight, plus
- the weight of each piece of **content** on it, plus
- the weight of its **biome(s)**.

Higher = better. Maps you've already finished are pushed to the bottom automatically. Set weights under **Maps** (the big map list), **Content**, and **Biomes**. Tip: *CTRL+Click* a slider to type an exact number, and use the **Set all** row at the top of each table to apply a value to every visible row at once. There's a search box to filter long lists, and a **Reset All Weights** button if you want to start over.

### Profiles (saved setups)
A **profile** saves all your weights *and* display choices (colors, icons, favorites) together. Use the **Profiles** section at the top of the settings to keep different setups — for example a "Breach farming" profile and a "Boss rushing" profile — and switch between them in one click. You can **New**, **Copy**, **Rename**, **Delete**, and **Import/Export** profiles to share them with friends.

### Favorites
Tick **Fav** on any map type (or content type) to mark it. Favorited maps get a star on the Atlas. If you turn on **Auto Create Waypoints for Favorite Maps** (under Waypoints), ExileMaps will automatically drop a waypoint on every favorite map it finds, so you always have an arrow pointing to them.

### Waypoints
Pin any map as a waypoint to get a marker, a screen-edge arrow, and a route line from your explored area. Add/remove them with the hotkeys (hover a map + Insert/Delete) or from the Waypoint Panel. Each waypoint shows how many **steps** away it is. You can recolor and resize each one in the panel.

### The Waypoint Panel (press End)
This in-game panel has two parts:
- **Waypoints** — your pinned maps: rename, recolor, resize, enable/disable, or delete them.
- **Atlas** — a searchable, sortable list of **every map currently on your Atlas**. Sort by weight, steps, or name; filter by name or content (with optional regex); limit how many results and how many steps away to show. This is the fastest way to answer "what's the best map I can reach right now?" — then click the waypoint button to pin it.

### Appearance
Under **Graphics** you can change almost everything: node size and color, whether to use the **icon set** or plain circles, ring size/width, line colors and widths, text colors, the favorite/special/atlas markers, and where map labels sit. Under **Maps** you can set per-map colors and pick a custom icon for each map type.

### Content rings
Under **Content** you choose which content types draw a ring, what color, and whether they show on locked/unlocked/hidden nodes. Mark content as favorite to make those maps count as favorites too.

### Sharing your setup
- **Export/Import Settings** — your entire ExileMaps configuration.
- **Export/Import Weights** — just the weight numbers (maps/content/biomes).
- **Export/Import Profile** — a single profile, perfect for sharing a curated farming setup.

All of these open a normal Windows file dialog.

---

## Tips & troubleshooting

- **Nothing is highlighted / the map lists are empty.** Make sure the plugin is enabled, then open the Atlas and wait a moment for the first scan. If it's still empty, bind and press the **Update Map / Content / Biome data** hotkeys while the Atlas is open. The **Refresh** hotkey (Home) forces a re-scan.
- **After a new league or patch.** Map IDs change between leagues. If maps stop matching, press the **Update … Data** hotkeys to re-read the game's current lists.
- **Icons look like plain circles.** That's the fallback look. The custom icon set lives at `textures/Icons_Desaturated.png` — make sure it's present next to the plugin, and that **Use Icons for Nodes** is on under Graphics.
- **The overlay covers a tooltip or my health orbs.** ExileMaps already avoids the map tooltip, title bar, orbs, flasks, and skill bar. If something still overlaps after a game UI change, let the maintainer know.
- **Performance.** If the overlay feels heavy on a huge atlas, raise **Render every N ticks** and **Map Cache Refresh Rate** under Graphics, and/or lower the **Atlas Range** under Features.
- **Weights changed but colors didn't update.** Updates are batched and apply within a moment; a Refresh (Home) forces it.

---

## Good to know

- ExileMaps is **display-only** — it reads the game and draws an overlay. It does not automate anything.
- It runs **inside the ExileCore2 HUD**; it is not a standalone program.
- Engine files for ExileCore2 are **not** included and are not redistributed here.
- This is a community hobby project for Path of Exile 2. Expect occasional breakage right after game patches — a quick **Update … Data** usually sorts it out.
