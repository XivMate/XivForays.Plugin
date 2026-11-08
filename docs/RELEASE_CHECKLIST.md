# Friends release checklist (0.0.0.14)

Plugin: XivMate Foray Data Gathering (`XivForays.Plugin`), Dalamud API 15.

## 1. Before tagging

1. Merge the release branch into `master` (it holds pose capture, player context and the release fixes).
2. `XivForays.Plugin/XivForays.Plugin.csproj` has `<Version>0.0.0.14</Version>`; `Core/ContextRules.cs` has
   `CaptureRevision = 3`. Bump both whenever capture logic changes.
3. `dotnet test XivForays.Plugin.Tests -c Release -p:Platform=x64` is green.
4. Build `dotnet build XivForays.Plugin/XivForays.Plugin.csproj -c Release -p:Platform=x64` and check one session in game:
   - the Debug tab shows pose and player-context lines, and dalamud.log has no `[XivForays.Plugin]` warnings;
   - in a party in Eureka, the Debug tab "Group" line shows each member's effective/current elemental level as the
     party list does (and "synced" while a member is synced to a FATE or NM); in Bozja the resistance rank; in the
     Occult Crescent the effective/current knowledge level. All zeros there means the game did not load the values;
   - the raw store shows kinds 1-19 for that visit, kind 14 with `gameVersion`, and no `enemyposition` uploads.
5. Re-read `docs/WHAT_WE_COLLECT.md`; send it to friends with the install steps.

## 2. Publish: option A, public GitHub repo (chosen)

1. On GitHub: `XivMate/XivForays` -> Settings -> General -> Danger Zone -> Change repository visibility -> Public.
   Check first that no file or commit holds a key or private data.
2. Tag and push:
   ```
   git tag friends-v0.0.0.14
   git push origin master friends-v0.0.0.14
   ```
3. The "Publish Friends Release" workflow (`.github/workflows/release.yml`) builds, tests, writes `repo.json` with
   `releases/download/friends-v0.0.0.14/XivForays.Plugin.zip` and publishes the release as Latest.
4. Check without signing in (private browser window or curl):
   `curl -sL https://github.com/XivMate/XivForays/releases/latest/download/repo.json`
   must return the JSON with `"AssemblyVersion": "0.0.0.14"`.

## 3. Publish: option B, host on web.xivforays.com (repo stays private)

1. Build the package locally (step 1.4); it is `XivForays.Plugin/bin/x64/Release/XivForays.Plugin/latest.zip`.
2. Make the listing:
   ```
   pwsh ./.github/scripts/New-FriendsRelease.ps1 -Tag friends-v0.0.0.14 -Repository XivMate/XivForays `
     -Package XivForays.Plugin/bin/x64/Release/XivForays.Plugin/latest.zip -OutputDirectory out/friends
   ```
   Then edit `out/friends/repo.json`: set `DownloadLinkInstall` and `DownloadLinkUpdate` to
   `https://web.xivforays.com/plugin/XivForays.Plugin.zip`.
3. Serve both files as static files from the web app (e.g. `wwwroot/plugin/repo.json` and
   `wwwroot/plugin/XivForays.Plugin.zip`), deploy, and check `curl -sL https://web.xivforays.com/plugin/repo.json`.
4. Friends then add `https://web.xivforays.com/plugin/repo.json` instead of the GitHub URL. Each update means
   repeating steps 1-3 by hand.

## 4. How friends install

1. In game: `/xlsettings` -> Experimental -> Custom Plugin Repositories.
2. Paste `https://github.com/XivMate/XivForays/releases/latest/download/repo.json` (option A) or
   `https://web.xivforays.com/plugin/repo.json` (option B), click **+**, tick **Enabled**, Save and Close.
3. `/xlplugins` -> search "XivMate Foray Data Gathering" -> Install.
4. `/xivforays config`: paste the personal API key, tick **Crowdsource data**.
5. Updates arrive through Dalamud as usual.

## 5. After release

- Watch `/status` and the raw store for each friend's first visit.
- Give each friend their own key, so one key can be disabled without affecting the others.
