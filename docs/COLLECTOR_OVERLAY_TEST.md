# Collector overlay: in-game test checklist (0.0.0.16)

Work through the sections in order. Each one turns on one more checkbox, so a problem points at one feature. Tick the
boxes in `/xivforays config`, **General**, section **Collector overlay**. Every checkbox starts off.

Good test spots: an ordinary Anemos or Pagos camp with a few needed points (Walk-past, To confirm or Never seen) and a
normal ~50 s respawn. Avoid FATEs for sections 2 to 4 until the end.

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

## 1. Nearby needed list

What to do: tick **1. Nearby needed list**. Stand in a camp, then walk 100 y or so in one direction.

What you should see:

- A "Collector overlay" window with a line such as "Coverage checked just now, data through 2026-11-13T15:00:00Z"
  (first time: "Coverage: downloading..." for a moment).
- Up to 10 rows (List rows option, 5 to 10): enemy name, status word in its colour (To confirm amber, Walk-past sky
  blue, Never seen lavender), a qualifier such as (FATE) when the row has one, ", height uncertain" on points whose
  height is not trusted, and the distance with a world compass direction ("42 y NE", north is up on the map).
- Confirmed points are not in the list.
- Rows reorder as you walk, nearest first, without jumping around when two are equally far.
- Clicking a row pins it (highlighted, "(pinned)" in section 3). Clicking it again unpins it.
- Unplug the network (or block the site) and wait 3 minutes: the line changes to "Coverage: offline / cached" and the
  list stays. Reload the plugin while offline: the list comes back from the cache.

Fail if:

- The list shows Confirmed points, or points from another zone.
- Directions are mirrored (a point to the north of you on the map shows S).
- The list is empty while the site shows needed points in this zone near you.
- Without any coverage (first run, offline) enemies are called "Never seen". It must say "Global coverage
  unavailable".

## 2. Respawn countdown

What to do: tick **2. Respawn countdown**. Kill one ordinary enemy at a needed point and stay near it. Then kill a
second one and let a party member (if you have one) kill a third.

What you should see:

- In the window: "Your kill  <name>  ~50 s  12 y N" counting down in whole seconds ("~49 s", "~48 s" ...). Party kills
  say "Party kill", deaths you only saw say "Observed death". Your kills are listed first.
- A clock marker with the same "~N s" text at the spawn point (not where the corpse fell if you pulled it).
- At zero the text reads **Awaiting spawn**. It never says Confirmed.
- When the enemy respawns, its row disappears.
- A FATE enemy shows "FATE enemy: no timer" instead of a countdown.

Fail if:

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

What to do: tick **4. Confirmed locally feedback**. Stay within 80 y of a needed point (To confirm, Walk-past or Never
seen) for at least 5 s, kill its enemy, and stay within 100 y until it respawns. Then do the same at another point
but go beyond 100 y while waiting and come back before it respawns, watching it appear from more than 80 y away.

What you should see:

- First point: within half a second of the respawn appearing, a teal **Confirmed locally** label over the spawn point
  for about 3 seconds, and the window line "1 confirmed locally, upload pending". The point leaves the needed list.
  When the server catches up (a few minutes, after its next refresh), the pending count drops by one.
- Without a layout id the label reads "Spawn watched locally; server match uncertain"; far from any known row it reads
  "Confirmed locally (new point)".
- Second point (you left 100 y and it appeared far away): **no** confirmation.

Fail if:

- A respawn watched from beyond 100 y, or an enemy that was already there when you zoned in, shows Confirmed locally.
- The confirmation waits for the 3 minute refresh or for a photo.
- With this box unticked, points still turn Confirmed locally.

## 5. Status markers over visible enemies

What to do: tick **5. Status markers over visible enemies**. Look at a camp with mixed statuses. Then tick **Show
confirmed**. Pull an enemy away from its spawn.

What you should see:

- Over each loaded enemy of a needed point: the colour, a short word and a shape. To confirm: amber outlined diamond.
  Walk-past: sky blue circle. Never seen: lavender question mark. Confirmed (only with Show confirmed): teal check.
  Enemies the overlay cannot tie to a row say "Unmatched" (or "Ambiguous" when two rows tie).
- A pulled enemy keeps its marker and status while it walks away.
- Markers only within the display distance (default 100 y) and at most 64 in total.

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

Fail if:

- A marker sits deep underground or high in the air.
- An occupied point also shows an empty marker at the same time.

## 7. Ground circles

What to do: tick **7. Ground circles**. Select a point (your latest kill or a pinned row) and walk around it.

What you should see:

- Two rings around the selected point only: sky blue "80 y watch" and amber "70 y photo", drawn at your own height.
- Rings shrink a little when you are far above or below the point (they are the cross-section of the 3D range).
  Segments behind the camera are skipped, not joined across the screen.
- No rings on points with an uncertain height.

Fail if:

- Rings appear on more than one point, use red, or draw lines across the whole screen.
- The ring and the "62 / 80 y" number clearly disagree when you stand on the ring on flat ground.

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

## Options

- **Display distance**, **Marker scale**, **Marker opacity**, **Max markers**, **List rows**: each takes effect at
  once.
- **Hide while in combat**: window and markers vanish when you enter combat and return when it ends.
- **Hide overlay** (also a button in the window): hides everything at once; recording continues. Untick it in General
  to bring the overlay back.

## Report back

For each failed item: the section number, the zone, roughly where, and what you saw. A screenshot helps. Lines from
`dalamud.log` that start with "Collector overlay" are useful too.
