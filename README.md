# XivMate Foray Data Gathering

An opt-in, passive Dalamud plugin that contributes world observations from Eureka, Bozjan Southern Front, Zadnor, Delubrum Reginae and the Occult Crescent to the XivForays bestiary project. After install and opt-in it needs no clicks.


## Install in Dalamud

1. With XIVLauncher and Dalamud running, open `/xlsettings` and select **Experimental**.
2. Under **Custom Plugin Repositories**, add [the Friends repository URL](https://github.com/XivMate/XivForays/releases/latest/download/repo.json) by pasting this address, then save:

   ```text
   https://github.com/XivMate/XivForays/releases/latest/download/repo.json
   ```

3. Open `/xlplugins`, find **XivMate Foray Data Gathering** and choose **Install**. Use a Dalamud build that supports API 15 for version 0.0.0.12.
4. Get your own API key from Jake or the [Forays site](https://web.xivforays.com/). Enter it in the first-run window or under **System** in `/xivforays config`. Keep the default API URL `https://web.xivforays.com/api/forays/`. Enable **Crowdsource data** in **General** when you want to contribute.

To update, open `/xlplugins` and install the available update. Keep the same repository URL. If you previously loaded a developer copy or used an older repository entry for this plugin, remove that entry before installing from the Friends feed.

The URL becomes available once Jake publishes the first Friends release. Dalamud needs to be able to open it without signing in. [Custom repository documentation](https://dalamud.dev/plugin-publishing/custom-repositories/)

## Privacy: exactly what is sent

Sharing is off on a fresh install. Nothing is read or sent outside these zones, by territory ID in any client language: Eureka Anemos/Pagos/Pyros/Hydatos incl. the Baldesion Arsenal, Bozjan Southern Front incl. Castrum Lacus Litore, Zadnor incl. the Dalriada, Delubrum Reginae (normal and Savage), and the Occult Crescent South/North Horn incl. the Forked Tower. Eureka Orthos is not included. Inside them, with **Crowdsource data** on, the plugin sends:

| What | Fields |
| --- | --- |
| Every record | record kind, UTC time, a random visit ID (new on every zone entry), zone and map ID, weather ID; in Eureka your current/effective elemental level and sync flag, in the Occult Crescent your current/effective Knowledge Level |
| Coverage heartbeat (every 30 s) | your own position (x, y, z). No name, job, world or character ID |
| Enemies near you | game NPC ID, a random per-enemy track ID, position, levels, element, mutated/adapted, in-combat, model ID and scale; when they appear, die in view, leave view, change, move 15+ yalms, and every 30 s. The legacy snapshot (every 15 s) also carries the enemy's game-defined name |
| FATEs | FATE ID, game-defined FATE name (legacy route), centre, radius, start time, progress, state |
| Critical engagements (Bozja, Zadnor, Occult Crescent) | encounter ID, state, progress, start time, seconds left, marker position and radius |
| Coffers, bunnies, pots, carrots | game object ID, kind, grade when known, position |
| Your own loot, EXP chain and defeat notices (English client) | item ID and quantity, chain number, or "you defeated an enemy". Numbers only; the notice text is discarded |

Every upload carries the plugin version and the API key you entered (to authenticate). The server stores the key with legacy enemy/FATE records and a SHA-256 of it with the newer records. The server also sees your IP address as part of normal web traffic.

**Never sent or logged:** chat text from any channel (say, party, FC, linkshell, tell, ...), chat sender names, player or character names, character/content/account IDs, other players' positions or data, party lists, inventory, retainers, screenshots, or files. The plugin only looks at the system loot/error channels for your own notices and keeps the numbers. It does not write chat, names, request bodies or your API key to `dalamud.log`; its log lines are short status messages (for example "observations endpoint returned 404").

Turn off **Crowdsource data** in `/xivforays config` under **General** to stop collecting and clear pending uploads (a request already in flight may still finish). See the [capture specification](docs/CAPTURE_SPEC.md) for field names and limits.

## Build and release

**Deploy order:** the web ingest `feature/friends-release` branch must be live before anyone installs 0.0.0.12. The deployed `master` returns 404 for `/observations` and the plugin drops those batches (it logs one warning saying so).

Use .NET 10, stable Dalamud API 15 references and the `XivForays.Plugin.sln` solution.

```powershell
dotnet build XivForays.Plugin.sln -c Release -p:Platform=x64 -p:RestoreLockedMode=true
dotnet test XivForays.Plugin.sln -c Release -p:Platform=x64 --no-build --no-restore
```

The Friends workflow builds on a `friends-v<assembly version>` tag. It publishes the DalamudPackager ZIP and a generated `repo.json` as assets, then makes that release current. The stable URL follows the latest release, while install and update links in the listing point at the exact tagged ZIP. The workflow uses GitHub's built-in token with release write access. There is no additional Actions secret for the plugin build. See the [release report](docs/FRIENDS_RELEASE_REPORT.md) for Jake's publishing steps.

[AGPL-3.0-or-later](LICENSE.md)
