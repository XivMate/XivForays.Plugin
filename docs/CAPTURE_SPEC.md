# Capture specification

## Baseline before Friends release

Source audited at plugin `864a94d` and ingest `5658449`. This describes the checked-in code, not production records. The data flow is plugin → `web.xivforays.com/api/forays/` → MongoDB → BestiaryGeneration.

Both uploads require **Allow crowdsourcing** (off on a fresh config) and game collection requires a loaded local player in a matching territory and map. The baseline uses localized place-name substring checks for Eureka, Zadnor, Bozjan Southern Front and South Horn. The base URL is configurable. Every POST uses JSON (camelCase on the wire), `X-Api-Key` and `User-Agent: XivForays/<assembly version>`. No durable upload spool exists.

| Endpoint and shape | Trigger and frequency | JSON fields |
| --- | --- | --- |
| `POST /enemyposition`, array of objects | About every 5 seconds, the plugin enumerates combatant battle NPCs with positive HP and queues a nonempty snapshot. Every 3 seconds a worker attempts one batch, requeues errors and clears the queue above 10 batches. A dead NPC still present in the object table can appear as a stale row until removed. | `mobIngameId` (NPC base ID, not its runtime object ID), `mobName`, `level` (native ForayInfo level on first sight; zero can persist), `x`, `y`, `z` (world coordinates), `isAdapted`, `isMutated`, `timeStamp` (UTC epoch seconds), `element` (native numeric code as text), `isInCombat` (has target or missing HP), `hasBeenInCombat` (observed earlier), `territoryId`, `mapId`, `instanceId` (random local visit GUID). |
| `POST /fateended`, one object | Polls the FATE table every 500 ms. Tracks a Running entry with positive start epoch and nonzero position. Queues it when Ended, Failed or missing from the table, provided the territory still matches. A worker tries one row every 2.5 seconds and requeues errors. | `fateId`, `name`, `x`, `y`, `z`, `radius`, `startedAt` (game epoch seconds), `endedAt` (observed UTC epoch seconds), `instanceId` (separate random local visit GUID), `territoryId`, `mapId`, `levelId` (FATE level), `reward` (always null on this path). |

The baseline has no FATE start/progress upload. The reward module returns before it activates. The diagnostic module subscribes locally to chat and inventory changes and can log matching chat text, sender, inventory changes and FATE reward diagnostics. It does not upload them. The baseline also logs the plugin's API key locally, and the ingest service logs the key, raw request body, FATE account association and Mongo connection string.

## Baseline ingest

`ApiGuidMiddleware` checks the GUID key against the enabled keys in the account database. `ForaysController` binds a typed DTO for each POST and `MongoService` maps it into `MongoEnemyPosition` (one document per array entry) or `MongoFate` (one document per completion). Mongo fields use PascalCase and omit the unused FATE reward. Each document gets a Mongo `_id`, `OwnerKey` (the authenticated key in GUID-string representation) and `PluginVersion` (from the User-Agent). `InstanceId` is also a GUID string. Legacy uploads are append-only and can duplicate on retry after an ambiguous response.

## Baseline BestiaryGeneration reads

- `Infrastructure/CloudMongoClient.cs` opens the configured cloud collections, defaulting to `MongoEnemyPosition` and `MongoFate` in `Configuration/CloudMongoOptions.cs`.
- `Pipeline/Bestiary/BestiaryCommand.cs` uses `MongoEnemyPositionAggregateSource` in `Pipeline/Bestiary/IEnemyPositionAggregateSource.cs` for the Mongo source. It matches territory, groups by base ID, name, level, element and mutation/adaptation flags, and counts documents and distinct visit IDs. It does not use raw XYZ or loot to select the bestiary level.
- `Pipeline/Fates/IFateRecordSource.cs` streams Eureka territory IDs 732, 763, 795 and 827. It reads document and owner IDs, visit ID, territory, FATE ID/name, XYZ, radius, start/end, map and level. `CloudEnemyPositionRepository` and `CloudFateRepository` also serve typed reads for older paths. These types do not declare the new observation fields or an ignore-extra-elements attribute.
- BestiaryGeneration also has local Mongo collections, cloud sync code and log-based analysis paths. This release leaves BestiaryGeneration read-only and does not execute those paths.

## Gaps and why they matter

| Gap | Why it matters |
| --- | --- |
| Enemy elemental level | A zero or stale value leaves many Eureka enemies without a reliable level and makes comparisons to player level unreliable. |
| Player elemental and effective sync level at kills and loot | Separates level-gap effects from synchronization when measuring where drops stop. Requires explicit kill/loot attribution, including no-drop kills. |
| Mutation and adaptation | Base and transformed mobs need separate level/drop distributions. The baseline selects one status and misspells the name in the adaptation branch. |
| Weather on each observation | Allows weather-conditioned spawns, transformations and rewards to be compared. |
| Enemy appearance, disappearance and movement | First/last visible positions and paths help distinguish spawn points, roaming and possible respawn timing. Client visibility alone cannot prove server spawn or despawn. |
| FATE start, update and end with centre/radius | Supports overlap, geography, progress and duration analysis. Missing visibility must be distinguishable from a confirmed end. |
| Coffer, bunny, pot and carrot positions and grades | Maps find/reward locations and helps separate routes and rewards by grade. |
| Chain number | Separates chain effects from level, weather and mutation effects in drop analyses. |
| Occult Crescent knowledge levels and Critical Encounters | Enables later comparisons of enemy/reward level gaps and CE timing and geography. |
