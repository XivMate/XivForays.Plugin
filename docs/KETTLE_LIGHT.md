# Kettle light (kind 20)

Records the light the Eurekan kettle gets in Pagos and Pyros ("frosted" and "smoldering" protean crystals), tied to the
kill or FATE that caused it. Only while Crowdsource data is on and the Eureka recording toggle is ticked. Read only.

## Where the numbers come from

| Part | Source | Status |
| --- | --- | --- |
| Grade of the light | LogMessage rows 9020 feeble, 9021 gentle, 9022 bright, 9023 brilliant, 9024 blinding, 9025 "light of a newborn star", 9026 "light of an exploding star" (game LogMessage sheet, checked against the local sheets). Row ids are the same in every client language, so no text is read. | Certain |
| Kettle full | LogMessage row 9027 "cannot accumulate any more vitiated aether" | Certain |
| Forge emptied | LogMessage row 3743 "convert the kettle's accumulated vitiated aether into ..."; row 3741 "Insufficient vitiated aether" is ignored | Certain |
| Precise kettle value | Not documented. See below. | Open |

### The precise value

Searched, all read-only:

- FFXIVClientStructs `PublicContentEureka` (0x15B8 bytes) and `EurekaState` (0x12 bytes: CurrentExperience,
  NeededExperience, MagiaAetherCharge, Fire, Ice, Wind, Earth, Lightning, Water, Magicite, MagiaAether). No kettle or light
  field. `MagiaAetherCharge` is the magia board charge, not the kettle (not tested against the kettle in game).
- `PlayerState.GetContentValue` keys 1 to 7 (Rival Wings, Eureka elemental levels and sync, Bozja rank, Occult Crescent
  knowledge). No kettle key. Only three key/value slots exist.
- The local Lumina sheets: kettle text exists only in LogMessage (above), Quest (Gerolt's quest names) and Item (Frosted and
  Smoldering Protean Crystal). No Addon or event item row mentions the kettle, so no UI addon text names it.
- Web search found no plugin, struct or community note naming a kettle field.

So no signature or struct field is known. `Services/KettleLightValueSource.Read()` is the single place to put it: it
returns null today, and the records are then notice-only (grade, link, group size) with before/after absent. A field that
sits inside a struct that the game version moves needs a recheck after each patch; a signature-based read survives
patches better but must be written and tested in game.

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
9. Convert at the Crystal Forge: a record with `forgeEmptied` true (row 3743). With the value source wired, `lightAfter` 0.
10. Leave for Anemos or Hydatos: no kind 20 records. Untick Eureka: none either.
11. If the value source is wired, `lightBefore + lightDelta == lightAfter`, and the delta matches the grade across kills;
    note the unit (raw or percent) from the first records.
