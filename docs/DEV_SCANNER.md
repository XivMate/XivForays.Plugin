# Developer value scanner (not shipped)

A read-only memory scanner used by the maintainer to locate the kettle light value after a game patch. It lives in
`XivForays.Plugin/Dev/` and is compiled only when `-p:DevScanner=true` is passed. The GitHub Actions workflows build
`Release` without that property, so `Dev/` is not compiled and no scanner type is in the published dll (checked by
building Release and Debug+DevScanner and searching both dlls for the scanner type names).

Build a developer build: `dotnet build XivForays.Plugin -c Debug -p:Platform=x64 -p:DevScanner=true`.
Tests: `dotnet test XivForays.Plugin.Tests -p:Platform=x64 -p:DevScanner=true` (without the property the scanner tests are not built).

## Safety

- Read only: VirtualQuery for committed, read-write, private or image-data regions and ReadProcessMemory on this process. No write call, no hook, no game function call. A test checks that no write API exists in the scanner types.
- Throttled: one low-priority background thread, a 3 ms sleep after every 512 KB read.
- Its own candidate lists are stored XOR-masked and the pointer lookup table is excluded, so a pointer scan never reports the scanner itself.

## Driving it (local files, no network)

Drop `<id>.json` into `<plugin config folder>/dev-scanner/commands/`: `{"id": "s1", "cmd": "scanFirst", "args": {...}}`.
The plugin polls every 0.5 s, moves the file to `done/` and writes `results/<id>.json`. Every command accepts
`"outDir"` for the text output folder. `/xfscan` logs the status, `/xfscan stop` cancels the running job.

| cmd | args | what it does |
|---|---|---|
| scanFirst | type int32/int16/byte/float/unknown, value | first scan (unknown keeps a capped snapshot) |
| scanNext | op equal/increased/decreased/unchanged/changed, value for equal | filters the candidates |
| scanList | max | lists candidates to `<id>-list.txt` |
| scanWatch | max or addresses, seconds | logs values once a second to `<id>-watch.txt` |
| scanWatchStop | | stops the watch |
| scanOwner | address, depth (4), maxOffset (0x400), maxPerLevel, maxChains, roots ("structs" checks the scanStructs structs first, one or two hops, and skips the heap walk when it finds a chain; generic:true runs it anyway), timeLimit (seconds, default 60; partial results), objectBytes | reverse pointer scan to the game module's static data or a ClientStructs struct (Framework, UIModule, AgentModule, each agent, AtkStage) |
| scanStructs | address | base, size and the pointer fields near the address (window target-0x2000 to target+0x200) of EventFramework, the active content director (and as the Eureka director), UIState, AgentModule, Eureka/Elemental/Magia/Content/ToDo agents and the _ToDoList window, to `<id>-structs.txt` |
| scanResolve | start (address or module+0xRVA), offsets, type | follows a saved chain now and reads the value |
| scanAddons | filter | names of loaded UI windows |
| scanAddon | name | text and counter nodes of an open window plus the owning agent's raw ints |
| scanStatus, scanStop, scanReset | | |
