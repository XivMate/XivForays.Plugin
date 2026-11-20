# Changelog

## Unreleased (performance pass)

- Lighter on the game: the Baldesion Arsenal portal scan runs every 5 seconds instead of every second, the FATE scan every second instead of every half second, and your foray state is read every 2 seconds instead of every second. Treasure and monster scans reuse what they looked up and allocate less. Nothing new is recorded and nothing recorded is dropped; a portal that appears is noted up to 5 seconds later than before.

## 0.0.0.18

- Eurekan kettle light (Pagos and Pyros): the plugin now records each light you collect, how much it added to the kettle, and what caused it (a monster kill or a FATE). Numbers only, no chat text.
- Baldesion Arsenal portals (Hydatos): when a blue or red Aetherial Node appears or goes away, its colour, where it is and when.
- Weather is now recorded two ways: the weather worked out from the time and the zone's weather table, and the weather your game is showing. They can differ, for example while a Hydatos notorious monster changes the sky.
- Your Eureka EXP buffs (Elemental EXP Up, Elemental Blessing, Elemental Harmony) are included in your foray state.
- Baldesion Arsenal portals are now actually recorded: the portal sampler was never being called, so no portal records were produced before this fix.
- Collector overlay: weather conditions are judged against the calculated weather, not the weather shown on screen, so a Hydatos notorious monster changing the sky no longer hides or shows the wrong monsters.
- No change to what is never sent: no names, no other players, no chat, nothing outside foray zones. See docs/WHAT_WE_COLLECT.md.
