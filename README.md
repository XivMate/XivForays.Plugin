# XivMate Foray Data Gathering

An opt-in Dalamud plugin that contributes world observations from Eureka, Bozjan Southern Front, Zadnor and Occult Crescent to the XivForays bestiary project.

## What we collect

Crowdsourcing starts off for a fresh install. With it enabled, the plugin sends the following while you are in a supported zone:

- Nearby enemies' game IDs, game-defined names, positions, observed levels and elements, mutation/adaptation, combat state, and when enemies appear, move, die in view or leave view.
- FATE game IDs and game-defined names, centre, radius, start and observation times, progress, state and observed end or disappearance. Occult Crescent Critical Encounters contribute their game IDs, timers, progress, states and available map marker positions.
- Recognized coffers, bunnies, pots and carrots with game IDs, locations and any recognized grades.
- Zone and map IDs, UTC timestamps, current weather, and your current and effective elemental or Knowledge Level where available. Eureka observations can also include whether your elemental level is synced.
- Recognized English game loot and chain notices contribute the item ID, quantity when available, or chain number. The game notice is read locally to get those numbers and discarded. A visible death does not tell us who got kill credit, and loot is recorded separately.

The plugin sends its version, random IDs for visits and enemy tracks, and the API key you enter to authenticate uploads. The key is saved locally in your Dalamud plugin configuration. The server can link uploads made with the same key. Legacy enemy and FATE records retain the existing key reference, and the new observation stream stores a one-way reference to it. The server receives your connection IP as part of normal web traffic.

The plugin sends no chat text, chat sender names, character or player names, character or account IDs, player positions, party lists, inventory contents, passwords, screenshots, or files from your computer. It does not upload Dalamud or ACT logs. Enemy and event names come from the game. Turn off **Crowdsource data** in `/xivforays config` under **General** to stop collecting and clear pending uploads. A request already sent may still finish.

See the [capture specification](docs/CAPTURE_SPEC.md) for field names and limits.
