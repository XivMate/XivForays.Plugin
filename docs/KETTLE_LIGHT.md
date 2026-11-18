# Kettle light (kind 20)

Records the light the Eurekan kettle gets in Pagos and Pyros ("frosted" and "smoldering" protean crystals), tied to the
kill or FATE that caused it. Only while Crowdsource data is on and the Eureka recording toggle is ticked. Read only.

## Where the numbers come from

| Part | Source | Status |
| --- | --- | --- |
| Grade of the light | LogMessage rows 9020 feeble, 9021 gentle, 9022 bright, 9023 brilliant, 9024 blinding, 9025 "light of a newborn star", 9026 "light of an exploding star" (game LogMessage sheet, checked against the local sheets). Row ids are the same in every client language, so no text is read. | Certain |
| Kettle full | LogMessage row 9027 "cannot accumulate any more vitiated aether" | Certain |
| Forge emptied | LogMessage row 3743 "convert the kettle's accumulated vitiated aether into ..."; row 3741 "Insufficient vitiated aether" is ignored | Certain |
| Precise kettle value | Yield and bar behind the Eureka content director (offsets below). Not named in FFXIVClientStructs. | Wired and guarded; seen to match the screen in Pyros |

### The precise value (yield and bar)

Found by a read-only structs scan on 2026-11-18 (no Cheat Engine table needed, no raw exe address). The read is:

    director = EventFramework.Instance()->DirectorModule.ActiveContentDirector   (only when its Type is PublicContentEureka)
    block    = pointer at director + 0x440        (read again at every sample; the block is rebuilt per instance, never cached)
    yield    = int32 at block + 0x78              (kettles filled)
    bar      = int32 at block + 0x1D8             (the light bar, 0 to 120 allowed)

`Services/KettleLightValueSource` does this with read-only `ReadProcessMemory` on the game's own process (a bad address
returns false, it cannot fault), a null and range check at every hop (`Core/KettleLightChain`), and no writes or game calls of
any kind. The director comes from the FFXIVClientStructs static instance and its type field. The field at director + 0x440 is
not named in ClientStructs. It is polled once per 1 s signal tick, in Pagos and Pyros only.

Checked in Pyros on 2026-11-18: yield and bar matched the on-screen kettle. Pagos is expected to use the same offsets but is
not yet confirmed. Re-check after a relog and a zone change (step 11 below).

Scale: one yield is a number of bar points that is not yet known (the bar is allowed 0 to 120). Records therefore carry the
raw values and the delta is `(yieldAfter - yieldBefore) * cap + barAfter - barBefore`. With the yield unchanged no cap is
needed; with a yield change and an unknown cap `lightDelta` is absent and `lightCap` is absent. Work the cap out from the
first records with a rollover and put it in `KettleLightChain.BarCap`.

#### Guard (why it may switch itself off)

The offsets are inside a struct ClientStructs does not describe, so they are only read when:

1. The game version is in `KettleLightGuard.AllowedVersions` (now only `2026.11.03.0000.0000`, the build they were found and
   checked on). On any other version it logs once ("Kettle light value is off: ...") and returns null; the kind 20 records
   are then notice-only. On the right version it logs "Kettle light value is on (game version ...)" once.
2. Every reading passes the sanity check: bar 0 to 120 and yield 0 to 999. One impossible reading switches the source off for
   the rest of the session, with one log line. An unresolved chain (no Eureka director, loading screen) is normal, returns
   null and is retried at the next sample.

#### Re-finding it after a patch

1. Run the developer structs scan (docs/DEV_SCANNER.md) to find the pointer into the Eureka director and the two ints, then
   confirm them against the on-screen numbers.
2. Put the offsets in `KettleLightChain` (`BlockPointerOffset`, `YieldOffset`, `BarOffset`).
3. Add the game version to `KettleLightGuard.AllowedVersions` only after step 11 of the checklist passes on it.

#### What each record carries

Per kettle light message, forge conversion, or change of the bar or yield (kind 20): `lightYieldBefore`, `lightBarBefore`
(the last 1 s sample before the message), `lightYieldAfter`, `lightBarAfter` (read after it; when nothing changed, the latest
sample), `lightDelta`, `lightCap`. A drop in the bar or yield (the forge converting the light) sets `forgeEmptied` even with
no notice. The kill or FATE link is described below.

#### Developer build only

In a build made with `-p:DevScanner=true` (never in Release): one Dalamud log line per record, prefixed `[kettle]`, with
tier, notice row, yield/bar before and after, delta and link; and the `/xfkettle` command, which prints the last 5 records to
your own chat log (local echo only, nothing is sent). Both live in `Dev/KettleDevLog.cs` and the `DEV_SCANNER` guards in
`Gathering/ForaySignalModule.cs`. The log lines and the command appear only while Crowdsource data is on (records are only
built then).

## Linking

Candidates are the kills (own or party, kind 13) and FATE completions (first time a FATE is seen at 100 % or ended) in the
3.5 seconds before the light message or value change (the window used in the log study). One candidate is linked. Several
are `lightLink` 3 with the count and the kill track ids; nothing is guessed. A record is finished 1.6 s after the notice so a
defeat whose enemy is still being looked up can link. A notice and a value change within 1 s are one record.

## In-game test checklist (next Pagos session)

Needs a build installed by the maintainer; nothing here is done by the plugin for you.

1. Enter Pagos with Crowdsource data on and Eureka ticked. Open Debug tab; confirm no errors in the log.
2. Kill one level-appropriate mob alone with a chain. If the kettle gets light, expect one kind 20 record: `noticeRow`
   9020 to 9022, `lightLink` 1, the mob's `gameId`, `chainNumber` at the kill, `groupSize` 1.
3. Kill a mutated or adapted mob: `isMutated` or `isAdapted` true and `form` 1 or 2 on the linked record.
4. Pull several mobs and AoE them together: records should be `lightLink` 3 (ambiguous) with `lightCandidates` above 1.
5. Kill an NM: expect grade 3 (bright) in Pagos, one kill link.
6. Complete a FATE you are inside: expect `fateRewardId` set and `lightFateCredit` 1 on a light record right after it; stand
   outside the radius and expect 0.
7. Join a party and repeat step 2: `groupSize` equals party size; a party member's kill gives `defeatBy` 2.
8. Fill the kettle and kill again: a 9027 record with `lightFull` true.
9. Convert at the Crystal Forge: a record with `forgeEmptied` true (row 3743). If the value source is on, `lightBarAfter` and `lightYieldAfter` drop (usually to 0).
10. Leave for Anemos or Hydatos: no kind 20 records. Untick Eureka: none either.
11. Check the Dalamud log first: expect "Kettle light value is on (game version 2026.11.03.0000.0000)". On any other build it says it is off and why. If the value source is on, `lightBarBefore + lightDelta == lightBarAfter` when the yield did not change, and the delta matches the grade across kills. Relog and change zone once and confirm the numbers still match the screen. Note the cap (bar points per yield) from the first rollover.
