# What the plugin collects

XivMate Foray Data Gathering only records while **Crowdsource data** is ticked, and only inside Eureka, Bozja,
Zadnor, Delubrum and Occult Crescent. Outside those zones it does nothing.

## What it sends

- **Enemies you can see:** which monster it is (game ids), where it stands and how it is posed, its level, element,
  mutation/adaptation, when it appears, dies and respawns, and when it starts fighting.
- **FATEs, critical engagements, treasure coffers, pots and bunnies:** which one, where, and its progress.
- **Your own drops, EXP chains and kills:** read from game notices (item id and amount, chain number). Player chat is
  never read.
- **Your own foray state:** your position every 30 seconds, job and level, elemental/knowledge level, the magia board,
  the chain timer, whether you are dead or mounted, and a short list of foray buffs (sprint, sneak, logos/lost/phantom
  actions, food, raise/weakness).
- **Your group, as numbers only:** how many are in your party, and each member's job, level and role.
- **Context:** zone, the world (server) you are on, weather, game version, plugin version and times.

## What it never sends

- No character names: not yours, not your party's, not anyone else's.
- No content ids, account ids, object ids or home worlds of other players.
- No chat, tells, party chat or free company messages.
- No other players' positions, jobs or statuses (only your own party members' job, level and role).
- Nothing outside the foray zones.

## How it is sent

- Uploads go over HTTPS to web.xivforays.com with your personal API key. The key identifies the uploads as yours, so
  they can be removed on request.
- The plugin only reads game memory. It does not change the game, send game packets or automate anything.

## Turning it off

Untick **Crowdsource data** in `/xivforays config`. Uploads stop at once, and anything not yet sent is dropped.
