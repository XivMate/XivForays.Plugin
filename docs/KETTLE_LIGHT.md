# Kettle light (kind 20)

Records the light the Eurekan kettle gets in Pagos and Pyros ("frosted" and "smoldering" protean crystals), tied to the
kill or FATE that caused it. Only while Crowdsource data is on and the Eureka recording toggle is ticked. Read only.

## Where the numbers come from

| Part | Source | Status |
| --- | --- | --- |
| Grade of the light | LogMessage rows 9020 feeble, 9021 gentle, 9022 bright, 9023 brilliant, 9024 blinding, 9025 "light of a newborn star", 9026 "light of an exploding star" (game LogMessage sheet, checked against the local sheets). Row ids are the same in every client language, so no text is read. | Certain |
| Kettle full | LogMessage row 9027 "cannot accumulate any more vitiated aether" | Certain |
| Forge emptied | LogMessage row 3743 "convert the kettle's accumulated vitiated aether into ..."; row 3741 "Insufficient vitiated aether" is ignored | Certain |
| Precise kettle value | A Cheat Engine table shared in the Eurekan Explorers Discord (pointer chain below). Not in FFXIVClientStructs. | Wired, guarded, unverified in game |

### The precise value

Source: a Cheat Engine table shared in the Eurekan Explorers Discord for the light bar, described as working until the next
game patch. It is a 4 byte value behind a pointer chain from the game exe. Cheat Engine lists the offsets last applied first
(134, 20, C8, 10, 38), so the read is:

    p = [exe base + 0x19053B8]
    p = [p + 0x38]
    p = [p + 0x10]
    p = [p + 0xC8]
    p = [p + 0x20]
    value = int32 at p + 0x134        (the table's last seen value was 61)

`Services/KettleLightValueSource` does this with read-only `ReadProcessMemory` on the game's own process (a bad address
returns false, it cannot fault), a null and range check at every hop (`Core/KettleLightChain`), and no writes of any kind.
It is polled once per 1 s signal tick, in Pagos and Pyros only.

Scale: not confirmed. 61 looks like a percent or a 0 to 100 bar, but nothing in the game data says so. The raw integer is
recorded as `lightBefore`, `lightAfter`, `lightDelta`; values above 100000 or below 0 are treated as unreadable. Work out the
unit from the first records (step 11 below), and note it here.

#### Guard (why it may switch itself off)

The RVA is a raw address, not a ClientStructs field or a signature, so it is only read when both hold:

1. The game version is in `KettleLightGuard.AllowedVersions` (now only 2026.11.03.0000.0000). On any other version it logs
   once ("Kettle light value is off: ...") and returns null; the kind 20 records are then notice-only.
2. The RVA lies in a writable, non-executable section of the loaded exe (read from the exe's PE headers in memory). A static
   pointer cannot live in code.

Finding at hand-over: on the installed 2026.11.03 build (ffxivgame.ver 2026.11.03.0000.0000, `ffxiv_dx11.exe`, read as a file
only) RVA 0x19053B8 is inside the `.text` code section (sections: .text 0x1000 to 0x210D8E2, .rdata, .data from 0x28AE000),
and nothing in the exe references it. So the raw address does not match this build, and the guard will keep the value off
(with one log line) until a matching address is supplied. Either the table is for another build, or the figure was copied
with a digit wrong. The ClientStructs `ida/data.yml` for 2026.11.03 has no entry at that address either (nearest named
symbol is a constructor at 0x14190EFB0), and the Eureka entries it does know are only `IsInEureka`,
`GetEurekaPublicContentDirector`, `PublicContentEureka` and `AgentEurekaChainInfo` and friends, none of which is a kettle.

#### Re-finding it after a patch

1. Get the current table from the Discord (or find the value yourself in Cheat Engine: scan for the bar's number, kill
   something that gives light, scan for the increased value, repeat; then pointer-scan with a depth of 5).
2. Put the new base in `KettleLightChain.BaseRva` and the offsets in `PointerOffsets` / `ValueOffset`. Remember Cheat Engine
   lists offsets last applied first.
3. Add the game version to `KettleLightGuard.AllowedVersions` only after step 11 of the checklist passes on it.
4. Prefer replacing the raw RVA with a ClientStructs field or a signature scan once the owning struct is known: the chain
   starts at a static pointer, and the first hop (+0x38) and onward look like a director or agent object tree, which is a
   better thing to resolve by signature than by RVA.

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
9. Convert at the Crystal Forge: a record with `forgeEmptied` true (row 3743). If the value source is on, `lightAfter` 0.
10. Leave for Anemos or Hydatos: no kind 20 records. Untick Eureka: none either.
11. Check the Dalamud log first: either no "Kettle light value is off" line, or the line names the reason (expected on 2026.11.03 until the address is corrected). If the value source is on, `lightBefore + lightDelta == lightAfter`, and the delta matches the grade across kills;
    note the unit (raw or percent) from the first records.
