# Matching capturers in the same Eureka instance

Not a release blocker: two friends reporting the same spawn are two independent confirmations, which is wanted.

## What the client exposes

There is no readable per-instance id in Eureka:

- `Director.DirectorStartTimestamp` of the active public content director reads 0 in Eureka (live, 2026-11-08).
- `Director.ContentId` is the content row (the same for every instance of a zone).
- `UIState.PublicInstance.InstanceId` is for numbered overworld instances; it is not expected to be set in forays
  (unverified: kind 19 sends it when non-zero, so the raw store will show it if it is).
- The server instance number is not sent to the client.

Kind 19 still records all three raw (`instanceStartTs`, `directorRowId`, `publicInstanceId`) in case a patch fills
them in, plus `currentWorldId` (the local player's own current world). Two visits can only share an instance when
they are on the same world, so `currentWorldId` + `territoryId` narrows the candidates before the rule below.

## Offline rule (exporter)

Two visits (`visitId`, different API keys) are the same instance when all hold:

1. Same `territoryId` and the same `currentWorldId` (kind 19), and their time ranges (first to last record) overlap.
2. At least one **server-timed event** matches: a FATE with the same id and the same `startedAt` (kinds 5-8; `startedAt`
   is the server's start second, so different instances practically never share it), or a critical engagement with
   the same id and `startedAt` (kind 12, Bozja / Zadnor / Occult Crescent).
3. No conflict: no FATE with the same id that both saw at the same time with different `startedAt`.

Fallback when neither saw a FATE start: two or more deaths (kind 3) of the same `layoutId` within 2 s of each other in
both visits, or two or more respawn appearances (kind 1 with `respawn`) of the same `layoutId` within 2 s.

Once visits are matched, records are deduplicated per instance by (`layoutId`, kind, time within 2 s) for enemies and
by (`gameId`, `startedAt`) for FATEs and CEs. Server-side clock offsets are taken from kind 19 (`eorzeaTime`,
`frameworkUtc` vs `observedAtMs`) before comparing times.
