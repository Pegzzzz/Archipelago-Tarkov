# Escape from Tarkov (SPT) Setup Guide

The full tutorial is on GitHub: <https://github.com/Pegzzzz/Archipelago-Tarkov/blob/main/TUTORIAL.md>

## Required software

- A legal copy of Escape from Tarkov, with **SPT 4.1.6** (game build 0.16.9.5.40743) and the .NET 10 and ASP.NET
  Core 10 runtimes. For SPT 4.0.13, use release 1.1.1 of the mod.
- Archipelago 0.6.4 or newer.
- From the [releases page](https://github.com/Pegzzzz/Archipelago-Tarkov/releases):
  `ArchipelagoTarkov-<version>-SPT-4.1.6.zip`, `tarkov.apworld` and the YAML template.

## Installing

1. Close the game and the SPT server.
2. Extract the release zip into your SPT folder (the one with `EscapeFromTarkov.exe`). You get
   `SPT_Runtime/user/mods/ArchipelagoTarkov/ArchipelagoTarkov.dll` (the server mod, required) and
   `BepInEx/plugins/ArchipelagoTarkov/ArchipelagoTarkov.Client.dll` (the client plugin, optional: in-raid
   notifications and DeathLink deaths in raid).
3. Start `SPT_Runtime/SPT.Server.exe`. The console shows `Archipelago for Tarkov loaded`.
4. Install `tarkov.apworld` in Archipelago (double-click it, or copy it into `custom_worlds`).

## Generating

Edit the YAML template (your slot name and options), put it in Archipelago's `Players` folder and generate. Seeds
must be generated on a PC with the APWorld installed; hosting them on archipelago.gg works.

## Connecting

1. Make a **new** SPT profile for each multiworld. The **Standard** edition is recommended (other editions start with
   hideout levels built).
2. In game, open the messenger, pick the **Archipelago** contact and send
   `connect <server:port> <slot name> [password]`, for example `connect archipelago.gg:38281 TarkovRunner`
   (use quotes for slot names with spaces).
3. When the contact says you're connected, restart the game once so the locked maps and traders show.

The connection is remembered and the server reconnects by itself whenever it starts. Other commands: `status`,
`items`, `hint <item>`, `!<server command>`, `disconnect`, `reconnect`, `reset`, `help`. Any other text goes to the
multiworld chat.

## Things to know

- Items arrive in your **System** messages; press **Receive** to take them. The Archipelago contact tells you what
  arrived.
- Newly unlocked maps appear after your next raid or a game restart.
- Experience, reputation and traps show in game after your next inventory action, trade or raid; in raid they wait
  until the raid ends.
- Extractions count only as a PMC, on maps you own. Transits are not extractions.
- With `starting_kit: replace`, restart the game right after linking (before any raid): that's when your starting
  stash is swapped for your random kit, which arrives in your System messages.
- **Collector goal:** accept Therapist's *Colleagues - Part 3* before turning in Jaeger's *The Huntsman Path -
  Sadist*, or *Collector* can't be finished.
- Before removing the mod, send `reset` then `reset confirm` on each linked profile so its traders and Flea Market
  unlock again.
