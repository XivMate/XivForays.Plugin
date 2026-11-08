# Pose capture design (v2 poses), for Jake's OK

Status: DESIGN ONLY (task 2026-11-08-0200, narrowed by addendum 0205, extended by addendum 0210 "record everything
raw + aggro events"). Nothing in this branch changes plugin or web code. Jake's v2 spec is the project doc;
`docs/CAPTURE_SPEC.md` here is still the committed baseline and is unchanged. Built on `feature/friends-release` @ 87c1292 (0.0.0.12 + FATE end). Web reference: master @ 31d54d2 (PR 164).

Goal: one full re-capture pass by Jake of every enemy at every spawn point, recording the client's SETTLED truth
(ground height, skeleton-root tilt, scales, model, timelines) that ACT never sees, plus form changes and idle
behaviour, so the Imaging plugin can place copies exactly (pack_snapshots v2) instead of reconstructing tilt.

## 1. What the game exposes (read-only, from the object table and draw objects)

FFXIVClientStructs as shipped with Dalamud.NET.Sdk 15.0.0 (the SDK both this plugin and XivForays.Imaging use).
"Imaging" = already read live by XivForays.Imaging `liveSnapshot` (feature/on-location), so the read is proven.

| Data | Source | Proven | Note |
| --- | --- | --- | --- |
| Spawn-point / layout id | `GameObject.LayoutId` (uint) | **no** | Exists in ClientStructs. For server-spawned BNpcs it is the layout instance id from the spawn packet. Unknown whether it is per spawn point, per camp/group, or 0 in foray zones. **Trial item 1.** Also `SharedGroupLayoutInstance`, `EventId`, `GimmickId` (expected 0 for field mobs). |
| "Server" position + heading | `GameObject.Position`, `.Rotation` | Imaging | No separate server value without packet hooks (not allowed). This is the networked value, interpolated; ACT 270/271 is the same source. |
| Settled position + rotation | `DrawObject->Object.Position`, `.Rotation` (quat) | Imaging | Draw Y = settled ground height. Rotation stays level (Imaging, 01:25). |
| Skeleton root | `CharacterBase->Skeleton->Transform` (pos + quat) | Imaging | The slope tilt (Squibs up to ~15°). Derive pitch/yaw/roll/tilt with Imaging's `GroundTilt.Degrees/TiltDegrees`. |
| Scales | `GameObject.Scale`, `DrawObject->Object.Scale` | Imaging | |
| Hitbox radius | `GameObject.HitboxRadius` | no (trivial) | |
| ModelChara + type/variant | `ModelContainer.ModelCharaId`; type/model/base/variant from the ModelChara sheet | Imaging / Lumina | Variant/IMC from the sheet, not memory: 0 bytes. |
| Drawn skeleton | `PartialSkeletons[0].SkeletonResourceHandle->FileName` | Imaging | Sent as numbers: kind (c/d/m/w), id, base. Drawn skeleton can differ from the ModelChara (Imaging 20:48). |
| Customize | `DrawData.CustomizeData` (26 B) | Imaging | Hash = first 48 bits of SHA-1 (the same as Imaging's 12-hex `customizeHash`), sent as a number. |
| Equipment / weapons | `DrawData.Equipment(0..9)`, `Weapon(Main/Off).ModelId` | Imaging | Usually all zero for monsters: omitted when zero. |
| Attached models | `DrawObject->Object.ChildObject` chain (≤8) | Imaging | Offset from the model plus scale. |
| Timelines | `Timeline.TimelineSequencer.TimelineIds[0..13]`, speeds, `BaseOverride`, `Mode`, `ModeParam`, `OverallSpeed`; Havok control `LocalTime`/duration | Imaging | The base/idle timeline is slot 0. |
| Level, element, form | `ForayInfo`, status list (Mutation / Adaptation, as now) | live 0.0.0.12 | Form: normal / mutated / adapted. |
| Weather | `WeatherManager` (as now) | live | |
| Eorzea time | derived from `observedAtMs` (ET = UTC × 3600/175) | — | Not sent; computed in export. |
| Collision loaded | player distance ≤ 120 y | heuristic | Flag + distance. Confidence, not a guarantee. |
| Seen spawning | first-seen distance and the visit's age | heuristic | Can't be read; inferred (see §3). |

Addendum 2 ("record everything raw"): also on the Character in this ClientStructs build. Field names are verified in
FFXIVClientStructs.xml. What they mean for foray mobs is unverified, so they are sent raw:

| Data | Source | Note |
| --- | --- | --- |
| **Home / spawn position** | `Character.DefaultPosition`, `.DefaultRotation` | If set for BNpcs, this is the server's home point (leash target): an EXACT spawn-point key, even for mobs seen walking. **Trial item 1b.** |
| FATE id (FATE adds) | `Character.FateId` | Raw on kinds 1–4, 15, 18. |
| Claim | `CombatTagType`, `CombatTaggerId` | Tagger mapped to a CATEGORY only (§2b); the id is never sent. |
| Battle / hostility | `Character.Battalion`, `RelationFlags`, `Flags`, Dalamud `StatusFlags` (Hostile, InCombat, WeaponOut, IsCasting...), `ActorControlFlags`, `EventState` | Raw bytes/ints. |
| Targetable, nameplate | `TargetableStatus`, `NamePlateIconId`, `Icon`, `RenderFlags`, `Visibility` | Raw. |
| Movement / mode | `MovementState`, `Mode`, `ModeParam`, `TransformationId` | Raw. |
| Statuses | Dalamud `StatusList` (≤ 30: id, param/stacks, time left, source) | Enemies: raw ids. Source = category only. |
| Cast | `CastInfo` (action id/type, progress, total) | Enemy casts at aggro. |
| HP | `Health`, `MaxHealth` | |
| Aggro type | **no client field** | Aggro (sight/sound/proximity/magic/blood) is server-side. The client sheets have BNpcBase `Behavior`, `Battalion`, `LinkRace` (assist group), `IsOmnidirectional`, `Rank`, `Special` and unnamed `Unknown*` columns. These are joined OFFLINE by bnpcId: 0 bytes in the plugin. So aggro type is INFERRED from aggro events (§2b). |
| Line of sight | `BGCollision` `Raycast` exists | A game function CALL, not a pure memory read. Proposed **off** in the distributed build and only as an opt-in for the capture pass, if Jake wants it. Otherwise the export infers it from height/terrain offline. |

Not feasible in a distributed plugin: true server position (packet hook), navmesh ground (vnavmesh is Imaging-only),
anything that writes memory, a "killer" (the client has no killer field: see §2b).

## 2. Records (observations stream, `schemaVersion` stays 1)

Additive only: new kinds, plus nested objects that carry their own `poseVersion: 1`. Keep the batch at
`schemaVersion: 1`. The live server only accepts exactly 1, and a 4xx makes the plugin drop the whole batch, so a
bump would lose ALL data until the deploy. **Until the ingest is deployed, the live server stores kinds 15–17 with
only their top-level fields; `pose` / `formChange` / `idle` are silently dropped** (unknown JSON members are ignored).
The same thing is already happening today to FateEnded `outcome` / `outcomeInferred` / `durationSeconds`: the web DTO
on master lacks them. Add them in the same ingest change.

Also add `layoutId` to the existing enemy kinds 1–4 (4 bytes each). It makes respawn (item 4) exact if the trial
proves it stable.

| Kind | Name | When |
| --- | --- | --- |
| 15 | EnemySpawnPose | Once per settled instance (rules §3), optional one "near" retake |
| 16 | EnemyFormChange | Form key changed (mutate/adapt/model/scale/ids): before → after |
| 17 | EnemyIdleTrack | Capture-pass mode only: 60 s idle track after the spawn pose |
| 18 | EnemyAggro | Out-of-combat → in-combat edge of an enemy (§2b) |

Scope (addendum 2): every battle enemy (BattleNpc Combatant with a BaseId), including sprites/elementals, FATE adds
and NMs. The current filter already admits them. Rank and FATE membership come from `FateId` and the BNpcBase join;
nothing is filtered in the plugin.

### `pose` object (kinds 15, 16 before/after)
`poseVersion, bnpcId, nameId, form (0 normal, 1 mutated, 2 adapted, 3 both), layoutId, firstSeenMs, presentAtStart,
seenSpawning, playerDistance, collisionLoaded, hitboxRadius, position[3], rotation (rad), scale, drawPosition[3],
drawRotation[4] (x,y,z,w), drawScale[3], skeletonPosition[3], skeletonRotation[4], skeletonPitch, skeletonYaw,
skeletonRoll, skeletonTilt (deg), modelCharaId, skeleton[3] (kind ascii, id, base), customizeHash, equipment[10]?
(id | variant<<16), mainHand[3]? / offHand[3]? (id,type,variant), attached[]? ([dx,dy,dz,scale]), baseTimeline,
baseOverride, mode, modeParam, overallSpeed, timelines[] ([slot,id,speed] non-zero), animTime[2]? (local, duration),
inCombat, moving, readAgeMs`, plus the raw state (addendum 2): `homePosition[3]?, homeRotation?` (DefaultPosition/
Rotation), `fateId?, battalion, relationFlags, flags, statusFlags, actorControlFlags, eventState, targetable,
namePlateIconId, icon, renderFlags, movementState, transformationId, hp, maxHp, claim (category), statuses:
[[id, param, timeLeftDs, sourceCategory], ...]`. Floats rounded: positions 3 dp, quaternions 4 dp, degrees 2 dp. Non-finite → the record
is dropped in the plugin (System.Text.Json throws on NaN, which would wedge the retry batch).

### `formChange` object (kind 16)
`poseVersion, changed (bitmask: 1 model, 2 scale, 4 drawScale, 8 nameId, 16 bnpcId, 32 mutated, 64 adapted,
128 skeleton), before (pose, may be null), after (pose), beforeAgeMs, changeMs (first detection), drawHeightDelta
(after.drawY − before.drawY), drawOffsetDelta ((drawY − objectY) after − before: valid even if it moved),
afterSettled`. The top-level `enemyTrackId` is the after track; `beforeTrackId` is set when a bnpcId change made the
tracker start a new track.

### `idle` object (kind 17, capture-pass mode)
`poseVersion, startMs, stepMs (2000), samples: [[dtMs, x, y, z, heading], ...]` (only samples that moved > 0.05 y or
turned > 1°, plus the first and last), `timelines: [[dtMs, slot0Id, baseOverride], ...]` on change,
`endReason (0 complete 60 s, 1 combat, 2 lost, 3 form change)`.

### 2b. `aggro` object (kind 18) and the categories

Categories (one byte, used for target, claim, killer and status source): 0 none, 1 self, 2 own party/alliance member,
3 other player, 4 own pet/companion/chocobo, 5 other player's pet, 6 NPC/enemy. They are computed locally from
`TargetId` / `OwnerId` / `CombatTaggerId` against the local player's entity id and `IPartyList`. **No player name,
content id or object id is ever sent.** Other enemies are named only by bnpcId and the existing random
`enemyTrackId` (the stream's convention: no game object ids).

Trigger: an enemy's InCombat flag (or TargetId) goes from off to on, seen on the 500 ms scan (so the timing is
±0.5 s). Fields:
`poseVersion, targetCategory, distanceToPlayer, enemyFacingToPlayerDeg (0 = facing the player), playerFacingToEnemyDeg,
dy (player − enemy height), enemyHomeDistance?, enemyMovedSinceSettledY, enemyCast? [actionId, type, progress],
enemyStatuses[], player: { moving, speed (y/s over the last scan), sprinting, mounted, inCombatAlready, casting?
[actionId, type], hpPct, mpPct?, stealth/sneak flags, jobRole? }, linked: [[bnpcId, enemyTrackId, dtMs, distance,
targetCategory], ...]` (other enemies that entered combat within ±1.5 s and 40 y). The time, ET and weather come from
the record context. The player's statuses are NOT sent raw: only the allow-listed flags (Sprint, Sneak/Stealth/Hide/
Invisible/"Lost Stealth", Mounted), resolved by English sheet name like Mutation/Adaptation now. `jobRole` (tank/
healer/dps, aggro-relevant) is optional, and off unless Jake wants it.

Purpose: the offline export classifies each enemy's aggro type. Sight = aggro only within the facing cone and never
from behind. Sound = aggro from behind, gated by `moving`/`speed`. Proximity = from any angle at short range. Magic =
correlated with player `casting`. Blood = correlated with low `hpPct`. Plus the link groups from `linked` against
BNpcBase `LinkRace`. Target category ≠ self/party aggros are kept, but don't count for the player-state inference.

**Killer on death.** The client has no killer. On kind 3 (died) add `claim` (CombatTagType + tagger category), the
last `targetCategory` and `fateId`. "Killed by self" is the existing kind-13 self-defeat notice joined by time. That's
the closest the client gets, and it's a category only.

### Sample (fake values)
```json
{"schemaVersion":1,"batchId":"0f8e...","observations":[
 {"kind":15,"observedAtMs":1793940205123,"visitId":"4463f91b-...","territoryId":732,"mapId":414,"weatherId":3,
  "gameId":7777,"enemyTrackId":"5305629c-...","layoutId":4012345,"x":-123.456,"y":12.345,"z":456.789,
  "isMutated":false,"isAdapted":false,"modelCharaId":1234,"modelScale":1.2,
  "pose":{"poseVersion":1,"bnpcId":7777,"nameId":1234,"form":0,"layoutId":4012345,"firstSeenMs":1793940203100,
   "presentAtStart":false,"seenSpawning":true,"playerDistance":43.2,"collisionLoaded":true,"hitboxRadius":1.5,
   "position":[-123.456,12.345,456.789],"rotation":1.2345,"scale":1.2,
   "drawPosition":[-123.456,11.987,456.789],"drawRotation":[0,0.5801,0,0.8146],"drawScale":[1.2,1.2,1.2],
   "skeletonPosition":[-123.456,11.987,456.789],"skeletonRotation":[0.0712,0.5788,-0.0123,0.8121],
   "skeletonPitch":-9.87,"skeletonYaw":70.9,"skeletonRoll":0.43,"skeletonTilt":9.9,
   "modelCharaId":1234,"skeleton":[109,123,1],"customizeHash":123456789012345,
   "baseTimeline":3,"baseOverride":0,"mode":0,"modeParam":0,"overallSpeed":1,"timelines":[[0,3,1]],
   "animTime":[1.234,2.4],"inCombat":false,"moving":false,"readAgeMs":0}}]}
```
Measured (compact JSON, these values): spawn pose 1.32 KB, form change 2.26 KB, idle track 0.6–1.6 KB.

## 3. Rules

**Who.** BattleNpc, subkind Combatant, BaseId > 0, HP > 0, zone gate as now. Never players, pets, companions,
chocobos. Nothing new identifies a person: the ids are the existing random `enemyTrackId` and game ids. The local
`GameObjectId` key stays in memory. `UploadTypesHaveNoTextFields` stays true: no strings (the skeleton path is sent as
numbers).

**Settle (spawn pose).** Taken when all hold: first seen ≥ 1.5 s ago; out of combat (no target, HP = max, no InCombat
flag); not moving for ≥ 1.0 s (position < 0.05 y and heading < 1° between 500 ms scans); draw object present, visible
and a CharacterBase with its skeleton loaded; no form change pending. Moving or combat resets the still timer
("retake if moving" = it waits). An enemy that never settles gets no spawn pose: no forced capture.

**Near retake.** A pose taken with the player > 120 y away gets at most one retake once it is settled again with the
player within 120 y (collision loaded). The consumer prefers `collisionLoaded: true`.

**Dedupe.** (a) One spawn pose per track (= per instance per appearance), plus at most one near retake. (b) Content
key per visit: (bnpcId, modelCharaId, form, round(x·2), round(z·2), round(headingDeg/3), collisionLoaded). The same
idle mob re-entering view (a new track) is not recorded again. The set is bounded (50k) and cleared on visit change.
Server side the batch `_id` already makes retries idempotent.

**Seen spawning / present at start.** `presentAtStart` = first seen in the visit's first 5 s of scanning (zone-in or
enable). `seenSpawning` = not present at start, first-seen distance ≤ 80 y and the player stood within 80 y for the
previous 5 s (heartbeat positions). It's a heuristic; the trial checks it against kills seen respawning.

**Form change.** A cheap form key on every 500 ms scan for every enemy: (ModelCharaId, GameObject scale, draw scale,
NameId, BaseId, mutated, adapted, skeleton id). On a change: before = the cached last full read of that object (a
round-robin refresh, ≤ 3 s old, `beforeAgeMs`). After = a full read 1.5 s after the LAST key change (debounced, at
most 5 s after the first), whether in combat or not. `afterSettled` says if it was still. One record per debounced
change.

**Budget.** At most 6 full reads per 500 ms scan (priority: form-after > spawn > refresh), on the framework thread,
inside the existing enemy tick. Every pointer is null-checked and every object read is in its own try/catch that skips
the object. Note: .NET cannot catch an AccessViolation, so the null and type checks (`GetObjectType() ==
CharacterBase`, skeleton/partial counts, the 260-byte path limit) are the real guard.

**Death → respawn (item 4).** Derived offline, no new record: kind 3 (died) + the next kind 15 with the same
`layoutId` (if proven) or the same exact spawn point (≤ 1.5 y) and bnpcId in the same visit → interval and whether the
form is the same. Only counted when the player stayed within range (heartbeats) the whole time.

**Pack context (item 1).** Derived offline from spawn poses + idle tracks of the same (bnpcId, form) within 35 y in
the same visit within ±10 s. That is the existing pack_snapshots rule, now with settled poses. No plugin cost.

**Quality (item 5).** Every pose carries `poseVersion`, the plugin version (User-Agent, as now), `collisionLoaded`,
`playerDistance`, `readAgeMs`, `moving`, `inCombat`, `presentAtStart`, `seenSpawning`. Export confidence: `high` =
collisionLoaded && still ≥ 1 s && out of combat; `medium` = settled but far; `low` = form-after not settled.

## 4. Cost

CPU (framework thread, measured on Imaging's equivalent reads, estimated here): a full read is ~60 pointer reads + one
≤ 260 B string + SHA-1 of 26 B, about 2–5 µs. The cheap form key is about 0.5 µs per enemy. Per 500 ms scan with 60
enemies: about 30 µs key + about 30 µs for 6 full reads, so < 0.1 ms every 30th frame and nothing on the other frames.
Idle tracks reuse the scan's position reads.

Bytes per hour of normal farming, as JSON on the wire (BSON in Mongo is about 1.2× with PascalCase keys and stored
nulls). Assumptions: about 200 kills/h plus about 200 range re-entries/h = about 400 new instances. About 75% settle
→ 300 spawn poses + about 30 near retakes. About 20 form changes/h. Idle tracks only in capture-pass mode.

| Item | Records/h | KB/h |
| --- | --- | --- |
| Spawn poses (+ retakes) | ~330 | ~435 |
| Form changes | ~20 | ~45 |
| Idle tracks (capture pass only) | ~300 | ~300 |
| layoutId / fateId / home pos on kinds 1–4 | — | ~40 |
| Raw state + statuses on each pose (addendum 2) | (in poses) | ~+60 |
| Aggro events (about 1 per kill + links) | ~250 | ~200 |
| Death claim fields on kind 3 | — | ~5 |
| **Total** | | **~0.8 MB/h normal, ~1.1 MB/h capture pass** |

An aggro event is about 0.8 KB: a cheap read on an edge, about 10 µs including the ≤ 30-entry status scans. The raw
state adds about 20 field reads to each full read (+1 µs). The optional raycast is about 10–50 µs per aggro edge, and
only in capture-pass mode if OK'd.

That is well inside the batch limits (256 records, 512 KB request): about 1 batch/2 s at most.

## 5. Ingest and Mongo (web, `feature/pose-capture` off master 31d54d2)

- `Models/Dto/ObservationBatch.cs`: kinds 15–18; `Observation.LayoutId`, `FateId`, `HomePosition`, `Claim`,
  `LastTargetCategory`, `Pose`, `FormChange`, `Idle`, `Aggro` (new records `EnemyPose`, `EnemyFormChange`,
  `EnemyIdleTrack`, `EnemyAggro`), `BeforeTrackId`; also FateEnded `Outcome`,
  `OutcomeInferred`, `DurationSeconds`. `IsValid()`: finite floats inside the nested objects, array lengths (quat 4,
  vec 3, equipment 10, samples ≤ 64). An invalid pose sets that nested object to null instead of a 400, so one bad
  record never drops a 256-record batch.
- Mongo: no migration. Same collection `MongoForayObservationBatch`, embedded in `Observations[]`. Optional index
  `{ "Observations.Kind": 1, ReceivedAt: -1 }` for the exporter (created in the background like the owner indexes).
- `/status`: `ObservationsByKind` counts the new kinds automatically once they're in the enum. Add `PoseRecords`
  (15+16+17 windows) and `PoseVersions24h` to `IngestStatus` and a row on `forays-status.html`.
- Tests: the contract HTTP round trip for kinds 15–17 with unknown fields, the invalid-pose → null rule, the Mongo
  container test for the status counter.
- Deploy needs: Jake's OK, merge PR → Azure pipeline → app service. No Mongo migration; index creation runs at
  startup.

## 6. pack_snapshots v2 (export for Imaging)

A new BestiaryGeneration tool `tools/pack-snapshots/export_poses.py` (read-only Mongo → developer JSON),
`ui-data/v1/developer/pack_snapshots_v2_<zone>.json`, not in the manifest (as v1):
```json
{ "schemaVersion": "2.0.0", "developerOnly": true, "zone": "anemos", "source": "plugin-poses",
  "snapshots": [ { "bnpcId": 8174, "nameId": 1234, "form": "normal", "when": "2026-11-08T02:30:00Z",
    "eorzeaTime": "19:05", "weatherId": 3, "visit": "visit-<12 hex sha256>", "confidence": "high",
    "instances": [ { "layoutId": 4012345, "spawnPointId": "anemos-8174-017",
      "object": { "x": -41.63, "y": -14.28, "z": 471.18, "heading": 0.13, "scale": 1.2 },
      "draw": { "x": -41.63, "y": -14.61, "z": 471.18, "rotation": [0,0.06,0,0.998], "scale": [1.2,1.2,1.2] },
      "skeletonRoot": { "rotation": [0.07,0.06,-0.01,0.995], "pitch": -9.9, "roll": 0.4, "tilt": 9.9 },
      "modelChara": 1234, "skeleton": "chara/monster/m0123/skeleton/base/b0001/skl_m0123b0001.sklb",
      "customizeHash": "0123456789AB", "baseTimeline": 3, "baseOverride": 0, "hitboxRadius": 1.5,
      "collisionLoaded": true, "playerDistance": 43.2 } ],
    "formChanges": [ { "to": "mutated", "modelChara": 1235, "scaleRatio": 1.25, "drawHeightDelta": 0.12,
      "drawOffsetDelta": 0.11, "skeletonRoot": { "rotation": [0,0,0,1] } } ] } ] }
```
Coordinates use the same convention as v1 (`y` = height). Imaging applies `draw.y` and the `skeletonRoot.rotation`
directly: no tilt formula. A form change is emulated in place: apply the ratio and the offset delta. Per (bnpcId, form)
the export keeps the largest pack of `high`-confidence poses (the v1 pack rule). Every instance keeps its spawn-point
key, so Imaging can also place a single enemy at any exact spawn point.

## 7. Coverage mode on the site (BestiaryGeneration, design only)

- Pipeline: `pose-coverage` command (C# like `exact-spawn-points`, reads Mongo kinds 15–16 with poseVersion ≥ 1 via
  `CloudMongoClient` + a new `MongoForayObservationBatch` source). Writes
  `ui-data/v1/developer/pose_coverage_<zone>.json`: per exact spawn point (from `exact_spawn_points.json`) and form:
  `captured` when a high-confidence pose of that bnpcId lies within 1.5 y (or has the same layoutId, once proven),
  plus `bestConfidence`, `capturedAt`, `nightOnly`/weather from `mobs.json` spawn rules.
- Site: a 5th Field work list "Pose coverage · v2" (developer build only), rows = enemy × form × spawn point NOT
  captured, grouped by camp. The existing Spawns now / next / all filter covers night-only and weather-locked mobs.
  The map shows missing points hollow and captured ones solid, with a header like "Anemos 212 / 340 spawn points
  posed". The existing done-storage lets Jake tick off camps by hand.
- Freshness: one pipeline run (minutes) between sessions. During a session the plugin's Debug tab shows "poses this
  visit" (count + the last 10 bnpcIds + flags), so Jake knows a camp is done before he leaves it.

## 8. 15-minute trial plan

Build: `feature/pose-capture` TRIAL build (items 1 + 3 + raw state + aggro events + layoutId/fateId/home on 1–4; no
idle tracks, no raycast), dev DLL in `bin\x64\Release`
(Dalamud reloads it). Each pose also writes one summary line to dalamud.log (`[XivForays.Plugin] pose 15 bnpc 7777
layout 4012345 near 1 tilt 9.9 dy -0.358`, no names), and the Debug tab gets a "poses" table. So the trial is
verifiable even before the ingest deploy. If Jake OKs the ingest deploy first, /status shows `PoseRecords` too.

1. (0–2 min) Reload check: the Debug tab shows the pose table, no warnings in the log.
2. (2–6 min) Stand 40–80 y from a sloped camp (Squibs), out of aggro, without moving. Expect one pose per camp member
   within about 3 s, `near 1`, tilts 5–15° matching what you see.
3. (6–7 min) Walk 150 y away and back: expect NO duplicates (content dedupe).
4. (7–11 min) Kill 2–3 of one camp and wait for respawns in view. Check that `layoutId` is nonzero, the same for the
   respawn at the same spot, different between camp members, `seenSpawning 1`. Also check that `homePosition`
   (DefaultPosition) is set and stays the same while the mob wanders (1b). **This decides whether spawn points get an
   exact key.**
4b. (during 4) Pull one mob from in front, one from behind while walking, one while casting: three kind-18 records
   with the angles, `moving` and `casting`, and `linked` for any assist.
5. (11–14 min) Trigger one mutation/adaptation (Field work "Attack these · mutation/adaptation checks"): expect
   one kind 16 with `changed` bits and `dy`.
6. (14–15 min) Zone out/in: new visit, `presentAtStart 1` for mobs already there.

Then read the plugin-tagged lines only and report the layoutId verdict, the pose counts, the dedupe and
the bytes.

## 9. Build estimate after OK

| Work | Effort |
| --- | --- |
| Plugin trial (items 1, 3, layoutId, log/Debug view, tests) | ~1 session |
| + raw state, statuses, aggro events, categories, death claim (addendum 2) | ~+0.75 |
| Plugin idle tracks + capture-pass toggle | ~0.5 |
| Web ingest + status + tests (commit only; deploy on OK) | ~0.5 |
| pose-coverage pipeline + site list/map | ~1–1.5 |
| pack_snapshots v2 exporter | ~0.5 |
