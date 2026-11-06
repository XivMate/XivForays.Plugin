# XivMate Foray Data Gathering

An opt-in Dalamud plugin that contributes world observations from Eureka, Bozjan Southern Front, Zadnor and Occult Crescent to the XivForays bestiary project.


## Install in Dalamud

1. With XIVLauncher and Dalamud running, open `/xlsettings` and select **Experimental**.
2. Under **Custom Plugin Repositories**, add [the Friends repository URL](https://github.com/XivMate/XivForays/releases/latest/download/repo.json) by pasting this address, then save:

   ```text
   https://github.com/XivMate/XivForays/releases/latest/download/repo.json
   ```

3. Open `/xlplugins`, find **XivMate Foray Data Gathering** and choose **Install**. Use a Dalamud build that supports API 15 for version 0.0.0.11.
4. Get your own API key from Jake or the [Forays site](https://web.xivforays.com/). Enter it in the first-run window or under **System** in `/xivforays config`. Keep the default API URL `https://web.xivforays.com/api/forays/`. Enable **Crowdsource data** in **General** when you want to contribute.

To update, open `/xlplugins` and install the available update. Keep the same repository URL. If you previously loaded a developer copy or used an older repository entry for this plugin, remove that entry before installing from the Friends feed.

The URL becomes available once Jake publishes the first Friends release. Dalamud needs to be able to open it without signing in. [Custom repository documentation](https://dalamud.dev/plugin-publishing/custom-repositories/)

## What we collect

Crowdsourcing starts off for a fresh install. With it enabled, the plugin sends the following while you are in a supported zone:

- Nearby enemies' game IDs, game-defined names, positions, observed levels and elements, mutation/adaptation, combat state, and when enemies appear, move, die in view or leave view.
- FATE game IDs and game-defined names, centre, radius, start and observation times, progress, state and observed end or disappearance. Occult Crescent Critical Encounters contribute their game IDs, timers, progress, states and available map marker positions.
- Recognized coffers, bunnies, pots and carrots with game IDs, locations and any recognized grades.
- Zone and map IDs, UTC timestamps, current weather, and your current and effective elemental or Knowledge Level where available. Eureka observations can also include whether your elemental level is synced.
- Recognized English game notices contribute an item ID and quantity when available, a chain number, or a signal that you defeated an enemy. The notice text is discarded. The plugin records visible deaths, self-defeat signals and loot separately.

The plugin sends its version, random IDs for visits and enemy tracks, and the API key you enter to authenticate uploads. The key is saved locally in your Dalamud plugin configuration. The server can link uploads made with the same key. Legacy enemy and FATE records retain the existing key reference, and the new observation stream stores a one-way reference to it. The server receives your connection IP as part of normal web traffic.

The plugin sends no chat text, chat sender names, character or player names, character or account IDs, player positions, party lists, inventory contents, passwords, screenshots, or files from your computer. It does not upload Dalamud or ACT logs. Enemy and event names come from the game. Turn off **Crowdsource data** in `/xivforays config` under **General** to stop collecting and clear pending uploads. A request already sent may still finish.

See the [capture specification](docs/CAPTURE_SPEC.md) for field names and limits.

## Build and release

Use .NET 10, stable Dalamud API 15 references and the `XivForays.Plugin.sln` solution.

```powershell
dotnet build XivForays.Plugin.sln -c Release -p:Platform=x64 -p:RestoreLockedMode=true
dotnet test XivForays.Plugin.sln -c Release -p:Platform=x64 --no-build --no-restore
```

The Friends workflow builds on a `friends-v<assembly version>` tag. It publishes the DalamudPackager ZIP and a generated `repo.json` as assets, then makes that release current. The stable URL follows the latest release, while install and update links in the listing point at the exact tagged ZIP. The workflow uses GitHub's built-in token with release write access. There is no additional Actions secret for the plugin build. See the [release report](docs/FRIENDS_RELEASE_REPORT.md) for Jake's publishing steps.

[AGPL-3.0-or-later](LICENSE.md)
