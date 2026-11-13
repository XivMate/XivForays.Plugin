# Collector overlay: in-game test checklist (0.0.0.16, round 1 fixes and the in-game map)

Work through the sections in order. Each one turns on one more checkbox, so a problem points at one feature. Tick the
boxes in `/xivforays config`, **General**, section **Collector overlay**. Every numbered checkbox starts off. The **Spawns now
only** filters (list, world markers, map) start on: points whose condition (night, weather, FATE) is known to be unmet
right now are left out, unknown conditions stay, and the picked point always stays. A config saved before this build
also gets the map one turned on once, even if it was stored as off; after that your choice is kept.

Good test spots: an ordinary Anemos or Pagos camp with a few needed points (Walk-past, To confirm or Never seen) and a
normal ~50 s respawn. Avoid FATEs for sections 2 to 4 until the end. For sections 2 and 7, a Pagos or Pyros
night camp or a sprite spot in its weather.

## 0. Setup and master switch

What to do:

1. Load the dev build (`XivForays.Plugin\bin\x64\Release\XivForays.Plugin.dll` after
   `dotnet build XivForays.Plugin.sln -c Release -p:Platform=x64`) as a dev plugin. Check `/xlplugins` shows version
   0.0.0.16.
2. Make sure **Crowdsource data** and **Record in: Eureka** are ticked (live feedback needs the recording scan).
3. Before ticking anything else, stand in Anemos for a minute. The first time, the folder
   `%AppData%\XIVLauncher\pluginConfigs\XivForays.Plugin\coverage-cache` must not exist yet: no download has
   happened.
4. Tick **Enable collector overlay** only (no numbered box).

What you should see:

- Nothing in the game view and no window in a supported zone (no numbered feature is on yet). The coverage file is
  downloaded once now, and the `coverage-cache` folder above appears.
- In Bozja, Zadnor or the Occult Crescent: a small "Collector overlay" window saying **Coverage not available for this
  zone.** Inside the Baldesion Arsenal: **Coverage not available here (Baldesion Arsenal).**
- Recording behaves exactly as before (Debug tab counters keep moving as usual).

Fail if:

- Anything is downloaded or drawn while the master switch is off.
- The plugin logs errors starting with "Collector overlay" in `dalamud.log`.
- Unticking the master switch does not remove the window at once.

## 1. Nearby needed list and the pointer

What to do: tick **1. Nearby needed list** (its sub-options **Point to the picked row** and **Spawns now only** are on
by default). Stand in a camp, then walk 100 y or so in one direction. Click a row, turn the camera away from that
point, then walk to it. By day near night-only enemies (or a sprite spot outside its weather), untick and tick
**Spawns now only**.

What you should see:

- A "Collector overlay" window, first time at the left of the screen about 480 x 340. Move and resize it: after a
  plugin reload or a game restart it opens where you left it.
- A top line with **Hide overlay** and "Coverage checked just now, data through 2026-11-13T15:00:00Z" (first time:
  "Coverage: downloading..." for a moment).
- The list has fixed columns (name, status, distance, condition) and always the same number of rows (List rows
  option, 5 to 10; blank rows when fewer points are needed). Nothing in the window jumps or changes width as rows
  update or a timer comes and goes.
- Each row: enemy name, status word in its colour (To confirm amber, Walk-past sky blue, Never seen lavender), a
  qualifier such as (FATE), ", height uncertain" on points whose height is not trusted, and the distance with a world
  compass direction ("42 y NE", north is up on the map). Confirmed points are not in the list.
- Clicking a row picks it: the row is highlighted and starts with ">", and the line under the list says "Picked: the
  game points to it". Clicking it again lets go (Unpin in section 3 does the same).
- **Spawns now only** on: no rows for points whose condition is unmet now (night-only by day, sprites outside their
  weather, FATE-only points while the FATE is not up); "any time" and unknown conditions stay. Off: those rows come
  back (greyed with box 8). A picked row stays in the list even when its condition goes unmet.
- For the picked point, in the game view: a cyan beacon (vertical line) over the spot with a small ring at its foot
  and "<name>  42 y" above it, and a dashed cyan guide on the ground from your feet to the spot.
- When the spot is off screen (behind you or beyond the screen edge): a cyan arrow at the screen edge pointing the way
  to turn, with "<name>  142 y NE". It moves round the edge as you turn the camera and disappears when the spot is on
  screen again.
- Unplug the network (or block the site) and wait 3 minutes: the line changes to "Coverage: offline / cached" and the
  list stays. Reload the plugin while offline: the list comes back from the cache.

Fail if:

- The list shows Confirmed points, or points from another zone.
- With Spawns now only on, a row whose condition is unmet now is listed (other than the picked one), or an "any time"
  row is missing.
- Directions are mirrored (a point to the north of you on the map shows S), or the edge arrow points away from the
  spot.
- The guide or beacon is drawn while no row is picked, or for a row other than the picked one.
- Lines are drawn across the whole screen when the spot is behind the camera.
- Without any coverage (first run, offline) enemies are called "Never seen". It must say "Global coverage
  unavailable".

## 2. Respawn countdown

What to do: tick **2. Respawn countdown**. Kill one ordinary enemy at a needed point and stay near it. Then kill a
night-only enemy (an Ashkin or another night enemy) at night, and a sprite in its weather. If you can, be there when
the night or the weather ends.

What you should see:

- In the window, at the bottom: "Your kill  <name>  ~50 s (default)  12 y N", counting down in whole seconds. Party
  kills say "Party kill", deaths you only saw say "Observed death". Your kills are listed first.
- The number in brackets is the basis of the estimate: "(n 37)" is the measured median of that enemy and its sample
  count, "(class, n 248)" the value of all night-only (or weather-only) enemies, "(default)" the ordinary estimate.
- Measured medians: ordinary enemies about 50 s (51 s measured), night-only enemies 146 s, weather-only enemies
  (sprites) 191 s. So an Ashkin starts at "~146 s" and a sprite at "~191 s", not "~50 s".
- A clock marker with the same text at the spawn point (not where the corpse fell if you pulled it).
- When the enemy's condition is not met now, no countdown: "Back at night (ET 18:00, in ~12 min)" for a night enemy by
  day, "Needs Blizzard/Snow (next in ~40 min)" for a sprite out of its weather (the same forecast as the site).
- Night and weather enemies despawn within about 2 s of the change (ET 06:00, or the 8 ET hour weather change), and
  the respawn waits for the condition. When the condition comes back, the countdown starts again from that moment.
- In Hydatos (no measured kills yet) a night or weather enemy says "timer unknown (night only)" or "timer unknown
  (weather only)".
- At zero the text reads **Awaiting spawn**. It never says Confirmed. When the enemy respawns, its row disappears.
- A FATE enemy shows "FATE enemy: no timer" instead of a countdown.

Fail if:

- An Ashkin or a sprite shows "~50 s".
- A night enemy by day, or a sprite out of its weather, shows a countdown.
- The "in ~N min" is clearly off from the site's weather page for the same zone.
- The timer anchors on the corpse after a pull, or shows exact seconds without the "~".
- A timer turns into "Confirmed" or keeps counting after the enemy is back.
- An enemy that only walked out of view (did not die) gets a timer.

## 3. Range and readiness

What to do: tick **3. Range and readiness**. Use the point of your latest kill (or pin one in the list). Walk
inside 80 y of it, run around inside that radius, then walk out to 90 y and then beyond 100 y, and come back.

What you should see:

- Name and status of the selected point, then "62 / 80 y" (your 3D distance to the point; "(horizontal, height
  uncertain)" when the point's height is not trusted).
- A "5 s nearby history" bar that fills over 5 seconds inside 80 y, then **Ready to watch**. While it fills: Watching.
  Moving around inside 80 y does not reset it.
- Beyond 80 y: **Move closer** (amber) and the bar empties.
- After going beyond 100 y while a death of that point is pending: "| Respawn-watch continuity lost". Coming back
  does not remove it for that death (the capture rule keeps the furthest distance), but a new clean spawn seen from
  within 80 y can still count.
- "Respawn ~N s" under it while that point's enemy is dead; "Photo still needed: get within 70 y" on points that still
  need a photo.

Fail if:

- Ready to watch shows while you are beyond 80 y, or after less than about 4.5 s inside.
- Continuity lost does not appear after you went beyond 100 y.

## 4. Confirmed locally

What to do: tick **4. Confirmed locally feedback** (and **5. Status markers** with and without **Show confirmed**).
Stay within 80 y of a needed point (To confirm, Walk-past or Never seen) for at least 5 s, kill its enemy, and stay
within 100 y until it respawns. Then do the same at another point but go beyond 100 y while waiting and come back
before it respawns, watching it appear from more than 80 y away.

What you should see:

- First point: within half a second of the respawn appearing, **one** teal **Confirmed locally** label over the
  spawn for about 3 seconds. With box 5 on it is the enemy's own marker; with box 5 off it is the flash label. Never
  two labels over the same spawn.
- The window line "1 confirmed locally, upload pending" (the two confirmation lines are always there, so nothing
  moves). The point leaves the needed list. When the server catches up (a few minutes, after its next refresh), the
  pending count drops by one.
- Without a layout id the label reads "Spawn watched locally; server match uncertain"; far from any known row it reads
  "Confirmed locally (new point)".
- Second point (you left 100 y and it appeared far away): **no** confirmation.

Fail if:

- "Confirmed" or "Confirmed locally" appears twice over one enemy, even for a moment right after it respawns.
- A respawn watched from beyond 100 y, or an enemy that was already there when you zoned in, shows Confirmed locally.
- The confirmation waits for the 3 minute refresh or for a photo.
- With this box unticked, points still turn Confirmed locally.

## 5. Status markers over visible enemies

What to do: tick **5. Status markers over visible enemies**. Look at a camp with mixed statuses. Then tick **Show
confirmed**. Pull an enemy away from its spawn. Under box 6 the option **Spawns now only (5 and 6)** (on by default,
shown when box 5 or 6 is ticked) applies to these markers too.

What you should see:

- Over each loaded enemy of a needed point: the colour, a short word and a shape. To confirm: amber outlined diamond.
  Walk-past: sky blue circle. Never seen: lavender question mark. Confirmed (only with Show confirmed): teal check.
  Enemies the overlay cannot tie to a row say "Unmatched" (or "Ambiguous" when two rows tie).
- A pulled enemy keeps its marker and status while it walks away.
- Markers only within the display distance (default 100 y) and at most 64 in total.
- Spawns now only (5 and 6) on: no marker over an enemy tied to a point whose condition is known to be unmet now
  (the picked point and the 3 s Confirmed locally flash still show). Unmatched enemies keep their marker.

Fail if:

- An enemy is labelled with the status of a different spot (several spots of one layout recoloured together).
- Show confirmed off still marks Confirmed enemies (other than the 3 s flash after a local confirmation).
- Markers flicker or jump as the camera turns. (Moving enemies are followed in small steps, twice a second; that is
  expected.)

## 6. Markers on empty needed points

What to do: tick **6. Markers on empty needed points**. Clear a camp and look at the empty spawn points.

What you should see:

- A marker with the status word on needed points that have no live enemy bound to them. Walk-past (approximate)
  points are hollow circles.
- Points with an uncertain height get no marker (they stay in the list with ", height uncertain"). Nothing is drawn
  far under the ground.
- When the enemy respawns, the empty marker goes and (with box 5) the enemy marker takes its place.
- Spawns now only (5 and 6) on: no empty marker on points whose condition is unmet now (night-only by day, sprites
  outside their weather, FATE-only outside their FATE); the picked point keeps its marker. Off: they come back, faded
  with box 8.

Fail if:

- A marker sits deep underground or high in the air.
- An occupied point also shows an empty marker at the same time.
- With Spawns now only on, an empty marker shows on a point whose condition is unmet now (other than the picked one).

## 7. Pack watch

What to do: tick **7. Pack watch** (Keep within: 60 y by default, 50 to 80 y; a margin under the 80 y rule). Kill a
pack of 4 or 5 enemies at needed points (party kills count too). Walk away from all of them, then follow the advice
until all have respawned. Then try a pack whose points are more than 2 x 60 y apart.

What you should see:

- A small sky blue ring at each spawn point still waiting for its respawn (own and party kills only; deaths you only
  saw are not part of the pack).
- A "Best spot" cross (the centre of the smallest circle round the waiting points) and an outline of the safe area:
  where you are within the radius of all of them. Amber while you are outside, green inside. The area shrinks as
  points respawn.
- One status line, in the window and at the top of the screen:
  - "Pack watch: in range of 4/5, move 12 y NE" while you are outside the safe area (walk that far towards the best
    spot);
  - "In range of all 5, waiting: next ~20 s" inside it (the soonest estimated respawn of the waiting points, from the
    same estimates as section 2);
  - "All good: 5 of 5 respawned, 4 confirmed, 1 already done. Move on." when all are back. Points that were already
    Confirmed when killed count as "already done" and are not waited for.
- A second line "1 missed: <name>" when a point respawned without a confirmation (usually because you were out of
  range). Then the "Move on." is left out.
- A point leaves the waiting set when it respawns, when its condition ends (night or weather), or after 5 minutes
  ("2 dropped (condition ended or 5 min)").
- Too far apart: "Pack watch: no spot covers all 3: <A> and <B> are 140 y apart. Split: <A>, <C> | <B>" and no safe
  area. Watch one group, then the other.
- The next kill after "All good" starts a new pack.
- **Single-point 80 y / 70 y rings** (off by default, under Pack watch): the old sky blue "80 y watch" and amber
  "70 y photo" rings round the selected point only, at your own height.

Fail if:

- You stand inside the drawn safe area and the line does not say "In range of all".
- A respawn you watched from inside the area counts as missed.
- The line says "All good" while a ring is still drawn.
- Rings or outline segments are drawn across the whole screen.

## 8. Spawn conditions

What to do: tick **8. Spawn conditions**. Look at the list during a weather that some enemies need, at night and
during the day, and while a FATE is up.

What you should see:

- A fourth column with the condition text ("any time", "night", "weather Snow", "during its FATE (1346)").
- Rows greyed when the condition is known to be unmet now, with the reason: "Needs Snow", "Night only", "FATE
  inactive". Empty markers of such points are faded.
- Night is Eorzea 18:00 to 06:00.
- Unknown conditions are never greyed.

Fail if:

- A point is greyed while its weather, time or FATE is active.
- A point is greyed only because the condition text is unknown.

## 9. Show on the in-game map

How it works (approach): the points are drawn with ImGui over the game's own area map window, not added as game map
markers. Game markers (the map agent's marker list) are a short fixed list that the game rebuilds itself, which does
not fit 1,200 points per zone; drawing over the window keeps the plugin read-only (nothing in the game is
changed). The map image node of the open map tells where the map texture is on screen, so markers follow zoom, pan,
window moves and the UI scale; the map agent gives the open map's id, size factor and offsets (world x/z to texture
pixel: `(world + offset) * sizeFactor / 100 + 1024` on the 2048 pixel texture). Markers are culled to the map view,
the nearest to you are drawn first, at most **Max map markers** (400 by default, 50 to 1,500). The minimap is not
covered yet: it draws from a rotating render texture whose scale needs its own check in game, so it comes later.

What to do: tick **9. Show on the in-game map**. Its status boxes start as To confirm, Walk-past only and Never seen
on, Confirmed off, and **Spawns now only** on (also on a config from an earlier build that had it off). Open the area map (default M) in Anemos or Pagos. Zoom in and out, drag the map, move and resize
the map window. Hover a few markers. Then try each status box, **Spawns now only** off and on again (at night or in a weather some
enemies need, best with box 8 on to compare) and **Pending respawn only** (kill two or three enemies at needed points
first). Click a row in the list (box 1) and look for it on the map. With Pack watch on (box 7), kill a pack.

What you should see:

- Small markers in the in-world colours and shapes: diamond To confirm, hollow circle Walk-past, circle with "?" Never
  seen, check mark Confirmed. With **Spawns now only** off, points whose condition is unmet now are drawn faded; with it
  on (the default) they are hidden.
- The markers sit on the right spots and stay on them while you zoom, drag and move the window. Nothing is drawn
  outside the map view.
- The window line "Map: N markers in view (M match the filters), arrow check K px". K is how far the game's own player
  arrow is from where the overlay puts you; a few pixels is fine. "Map: showing another map ..." when you look at a
  different zone or a world map: then nothing is drawn.
- Hover: a tooltip with the enemy name, "Lv N" once an enemy of that kind was seen this session, the status, "When:"
  and the condition (with the reason when unmet now), and for a pending respawn "Respawn ~32 s" (or "Back at night
  ...").
- **Spawns now only**: night-only points vanish by day, weather points vanish outside their weather, FATE points while
  the FATE is not up; "any time" and unknown conditions stay.
- **Pending respawn only**: only the points of enemies that died recently and are waiting to respawn, each with a green
  ring; a point leaves as soon as its enemy is back. FATE enemies are not included.
- The picked row: a large cyan ring with a cross, shown even when its status box is off or the cap is reached.
- Pack watch: sky blue rings on the waiting points, the safe area outline (amber outside, green inside) and the best
  spot cross, the same as in the world.
- Closing the map removes everything at once; framerate does not drop with the map open.

Fail if:

- Markers are offset from where the enemies stand (say so with the "arrow check" number and the zoom level), or drift
  when you zoom or drag.
- Markers are drawn outside the map view, over the map's buttons, or stay after the map closes.
- A filter hides a point it should keep (for example an "any time" point with Spawns now only).
- The framerate drops noticeably with the map open.

The first time the map opens after the plugin loads, `dalamud.log` gets one line starting "Collector overlay map view"
with the map numbers. If the markers are offset, please send that line.

## Options

- **Display distance**, **Marker scale**, **Marker opacity**, **Max markers**, **List rows**: each takes effect at
  once. Marker scale and opacity also apply to the map markers (section 9).
- **Hide while in combat**: window and markers vanish when you enter combat and return when it ends.
- **Hide overlay** (also a button in the window): hides everything at once; recording continues. Untick it in General
  to bring the overlay back.

## Report back

For each failed item: the section number, the zone, roughly where, and what you saw. A screenshot helps. Lines from
`dalamud.log` that start with "Collector overlay" are useful too.
