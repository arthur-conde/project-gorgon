# Legolas — User Guide

Legolas is the **Survey** module: it tails Project Gorgon's chat log for
`[Status]` lines, plots each ping on a translucent map overlay, and computes a
sane walking route between them. It also has a **Motherlode** sub-mode that
trilaterates a treasure's location from three distance readings.

## 1. Get to the panel

Click the **Legolas · Survey** tab (target/crosshair icon) in the shell. The
panel is lazy-loaded — the chat-log ingest service starts the first time you
open the tab and stays running for the rest of the session.

## 2. Two modes

The panel has a **Mode** group at the top:

- **Survey** — for *Surveying* skill pings (e.g. "The Iron Ore is 14m north
  and 7m east").
- **Motherlode** — for *Animal Handling*-style "The treasure is 23 metres from
  here" lines.

Switch modes via the radio buttons or the bindable hotkey commands
`legolas.mode.survey` / `legolas.mode.motherlode` (no default keys — assign them
in the shell's hotkey settings if you want them).

## 3. Survey mode workflow

### 3a. Open the Map Overlay

Click **Map Overlay** in the panel. You get a transparent, always-on-top window
with a movable/resizable frame. Drop it over your in-game minimap so the
overlay's coordinate system aligns with the world you see.

> The overlay starts non-click-through. Use the **Click-Through** group on the
> panel (or `legolas.overlay.map.clickthrough.toggle`) to let mouse clicks pass
> through once you're done placing pins. Toggle it back when you need to
> interact with the overlay again.

### 3b. Set your player position

The status bar in the **Session** group will say *"Click the map to set player
position."* Click your in-world location on the overlay. That fixes the
**projector origin** (the (0,0) point that all metre-offsets get drawn relative
to). The status changes to *"Surveying — waiting for next [Status] line."*

You can re-anchor at any time via the **Set Player Position** button (or the
`legolas.session.set_position` hotkey) — it puts the session back into Idle so
the *next* click resets the origin.

### 3c. Cast Survey in-game

The chat-log parser listens for:

```
[Status] The <name> is <a>m <dir> and <b>m <dir>
```

When one fires, the panel's *"Last event"* row shows
`Survey: Iron Ore (14E, -7N)` and the status flips to **AwaitingPin**:
*"Click the ping for: Iron Ore (14E, 7N)"*.

### 3d. Click the ping on the map

Click roughly where the ping appeared in-game on top of the overlay. Legolas
drops a pin and stores both the parsed metre-offset *and* the pixel you clicked
as a **manual override**. Each click is a calibration sample.

After the **second** override on the same session, the projector switches from
a fixed scale to a least-squares-fitted **scale + rotation**. From that point
onward, *new* pings are auto-projected — you'll see them appear at the correct
spot without needing to click. Existing manually-placed pins keep their
override; uncorrected ones get re-projected. You can still **drag any pin** to
refine the fit; each drag refits.

### 3e. Optimize the route

When you have several uncollected surveys, click **Optimize Route** (or
`legolas.route.optimize`). The optimizer is `AdaptiveRouteOptimizer` — exact
Held-Karp for ≤ ~12 stops, falling back to nearest-neighbour + 2-opt for larger
sets. Pins get numbered 1, 2, 3… and a route polyline is drawn from the player
position through them in order. The next-up pin is highlighted as the
**active target**.

### 3f. Collect

Walk to the targets in-game. The parser also catches:

```
[Status] <name> [xN] collected!
```

…and marks the matching survey collected automatically. The active-target
highlight advances. If the auto-match fails (rare; name mismatch), use the
**Mark Collected** button — it marks whichever survey is currently active.

When everything is collected, **Auto-reset when all surveys collected** (on by
default) wipes the session list and goes straight back to Surveying. The
player position and projector calibration are preserved.

### 3g. Tuning knobs (Session group)

- **Dedup radius (m)** — re-pings of the same name within this distance are
  treated as the same survey instead of stacking duplicates. Default 5m.
- **Pin radius (px)** — visual size of the dot. Default 8.
- **Show bearing wedges** — for *uncorrected* pings, draws a 45°-wide arc from
  the player at the parsed distance, since direction is somewhat ambiguous
  before calibration.
- **Show route lines** — toggles the polyline between optimized stops.

## 4. Motherlode mode workflow

For "The treasure is X metres from here" prompts, where you need three readings
to triangulate.

1. Switch to **Motherlode** mode. A Motherlode group appears at the bottom of
   the panel.
2. Position 1: stand at your first spot in-game, click your map location on the
   overlay (this also sets the player position), then click **Record Player
   Position**.
3. Trigger the in-game distance check. The line is auto-parsed and added; or
   type the metres manually into **Distance (m)** and click **Record Distance**.
4. Walk to a different spot, repeat — record position, then distance. Three
   positions/distances total.
5. After the third reading, `TrilaterationSolver` produces an estimated pixel
   position for the treasure. The slot's **Estimate** field updates from
   `(pending)` to coordinates.
6. Add more treasures by recording fresh distances (a new slot opens
   automatically). Click **Optimize Route** to order them.
7. **Reset** clears positions, slots, and distances.

## 5. Inventory overlay (independent of mode)

Click **Inventory Overlay** to open a transparent grid sized for tracking
what's in your in-game bag. The **Inventory Grid Settings** group lets you set
Columns, Cell Width/Height, and gaps so the grid lines up with the game's bag
UI. Click-through and opacity are independent from the map overlay.

This is purely a visual aid — Legolas doesn't read inventory data; the grid is
a guide you can lay over the bag window.

## 6. Hotkeys (all bindable, none bound by default)

| ID | Action |
|---|---|
| `legolas.session.start` | Start / Reset session |
| `legolas.session.set_position` | Arm next click as new player position |
| `legolas.session.mark_collected` | Mark active target collected |
| `legolas.mode.survey` / `legolas.mode.motherlode` | Switch modes |
| `legolas.route.optimize` | Run route optimizer |
| `legolas.overlay.map.toggle` | Show/hide map overlay |
| `legolas.overlay.inventory.toggle` | Show/hide inventory overlay |
| `legolas.overlay.all.toggle` | Toggle both at once |
| `legolas.overlay.map.clickthrough.toggle` | Map: click-through on/off |
| `legolas.overlay.inventory.clickthrough.toggle` | Inventory: click-through on/off |
| `legolas.overlay.wedges.toggle` | Show/hide bearing wedges |

Bind these in the shell's hotkey settings (the global hotkey
conflict-detector enforces uniqueness across all modules).

## 7. Persistence

All settings live in `%LocalAppData%/Mithril/Legolas/settings.json`: window
positions/sizes, opacities, click-through state, dedup/pin radii, the Inventory
Grid layout, and Auto-Reset. Session contents (surveys, calibration) are **not**
persisted — they're per-game-session by design.

## 8. Tips

- Always set player position **first**, then start surveying. Pings detected
  while in Idle are dropped with a *"press Start first"* event in the status
  bar.
- The first two pings are calibration. Until you've placed two manual
  overrides, every survey forces the AwaitingPin loop. Once two are in, future
  pings auto-place.
- If the projector's scale/rotation drifts (you moved the overlay or zoomed the
  in-game map), drag a pin or two — each correction refits the projector.
- The dedup radius defaults to 5m — useful when a survey re-fires with a
  slightly different reading because you walked. Bump it higher on dense
  routes; lower it if real distinct nodes are getting merged.
