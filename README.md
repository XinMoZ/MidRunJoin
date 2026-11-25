<div align="center">

# MidRunJoin

**Join friends mid-run or adjust your multiplayer party roster in *Slay the Spire 2***

[![Steam Workshop](https://img.shields.io/badge/Steam%20Workshop-3844124271-1b2838?logo=steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271)
[![Version](https://img.shields.io/badge/version-0.5.1-blue)](MidRunJoin.json)
[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![STS2 Branches](https://img.shields.io/badge/STS2-public%20%26%20public--beta-orange)](#compatibility)

**English** | [简体中文](README.zh-CN.md)

</div>

---

## Overview

**MidRunJoin** lets the host of a *Slay the Spire 2* multiplayer save add new players mid-run (with visited-room catch-up rewards) or remove absent teammates (with optional run-scoped progress stashing so they can rejoin later and receive delta catch-up rewards).

- Works with both **Standard** and **Custom** multiplayer saves.
- Single DLL compatible with both the **live mainline (`public`)** and **`public-beta`** branches.
- Marked `affects_gameplay: false` so **only the host and joining players who need catch-up rewards need to install the mod**—unmodded teammates can still join safely.

---

## Features

### Party Roster Editor (Host from Save)
- Adds a floating **`+ Mid-Run Join`** button in the top-right corner of the **Host from Save** button (automatically follows the game's active language for English / Chinese).
- **Add multiple guests at once**:
  - **Steam mode**: Pick from your Steam friends list (sorted by *Playing STS2* → *Online* → *Offline*, with search and *Online only* filter), or enter a non-friend SteamID64 manually.
  - **Local LAN / `--fastmp` mode**: Enter numeric player IDs directly.
  - **Character selection**: Choose any available character or **`Random`** (assigned when hosting the updated lobby).
- **Remove absent players (inline 3-button menu)**:
  - **Delete**: Removes the player from the save and makes them immediately available in the picker below.
  - **Save Progress**: Stashes the player's full personal state (deck, relics, potions, gold, HP, personal RNG, and relic grab bag) keyed to the current run's fingerprint (seed, start time, host, ascension, game mode, and acts).
  - **Cancel**: Cancels the pending removal.
- **Pinned saved-progress recovery**: Stashed players are pinned to the top of the friend list with a gold save icon; selecting them locks their original character, restores their saved state, and grants **only delta catch-up rewards** for rooms missed while away. Stashes are automatically cleared when the multiplayer save finishes or is deleted.
- **Automatic Steam invites & zero-side-effect cancel**: Automatically invites newly added guests over Steam when the updated lobby opens. Cancelling before starting leaves the original save, RNG streams, and relic pools untouched.

### Visited-Room Catch-Up Rewards
Scans the **host's route** in `MapPointHistory` (compatible with split-path mods) and uses STS2's native `RewardsSet.GenerateRewardsFor` + `Hook.ModifyRewards` to generate and merge rewards into a single reward screen offered right after loading:

| Room Type | Catch-Up Reward |
|---|---|
| **Enemy / Elite / Boss** | Native encounter drops (merged into 1 Gold slot; Potions capped to potion slots; supports modded reward hooks) + 1 Card Removal per Boss |
| **Treasure Room** | Rolled Treasure Gold + Relic |
| **Unknown (`???`) / Merchant (Shop)** | +10 Gold each |
| **Rest Site** | 1 Card Upgrade per 2 Rest Sites (rounded down) |
| **Ancient (incl. Neow)** | 1 native choose-1-of-3 Ancient Relic reward per visited Ancient |
| **HP** | Current HP initialized to match the party's average remaining HP% |
| **Custom Mode Modifiers** | Per-player run-start Modifiers (`GenerateNeowOption` / `ClearsPlayerDeck`) run once for fresh guests (skipped for restored stashed players) |

### Adaptive Safe-Sync & Host Missing-Mod Warning
- **Early-floor history backfill**: The host backfills empty `PlayerMapPointHistoryEntry` placeholders for newly added guests across earlier floors (alongside [HistorySafety.cs](src/HistorySafety.cs)), preventing post-run Game Over and Run History hover crashes even on unmodded clients.
- **Adaptive sync mode**:
  - **All peers have MidRunJoin**: Full catch-up mode (native drops, card upgrades, per-player custom modifiers).
  - **Unmodded teammate present**: Automatically switches to **Safe-Sync mode**—runs locally on the joining guest using a detached RNG and emits only `RewardSynchronizer`-broadcastable rewards (gold, cards, pre-picked relics, potions, Ancient 1-of-3, card removals), skipping card upgrades and custom modifiers so unmodded teammates never desync.
- **Host-side missing-mod warning modal**: When the host detects one or more joining peers without MidRunJoin (via `GetVersionInfoForPeer`), a modal lists all missing-mod players in a vertical list with role badges and two buttons:
  1. **Open Workshop**: Opens the Steam Workshop page securely via Steam Overlay (`SteamFriends.ActivateGameOverlayToWebPage`), or displays a translucent toast if Steam Overlay is unavailable.
  2. **Ignore & Enter with Starter State** / **Ignore & Enter with Saved State** *(Starter State takes priority if both fresh guests and saved teammates are missing the mod)*.

---

## Installation

### Steam Workshop (Recommended)
Subscribe on the [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3844124271) and enable **MidRunJoin** in the in-game Mods menu.

### Manual Installation
1. Build the Release package (see [Building from Source](#building-from-source)) or download a release archive.
2. Copy the `MidRunJoin/` folder (containing `MidRunJoin.dll` and `MidRunJoin.json`) into your game's `mods/` directory.
3. Launch *Slay the Spire 2* with mods enabled. A successful load logs `[MidRunJoin] Checkpoint lobby enabled.`.

---

## Usage

1. Finish the current room's rewards, advance to a checkpoint, and exit to the main menu.
2. As the host, open **Multiplayer** and click **`+ Mid-Run Join`** on the top-right of **Host from Save**.
3. Remove absent players (*Delete* or *Save Progress*) and/or add new players with a character (or *Random*) and catch-up mode, then click **Host Updated Lobby**.
4. Once all players join and ready up, the run loads and newly joined guests receive their merged catch-up reward screen before continuing together.

---

## Compatibility

- **Game Branches**: Tested on both **mainline (`public`, v0.111.1)** and **`public-beta` (v0.114.0)** using a single cross-version DLL ([src/Compat.cs](src/Compat.cs)).
- **Other Mods**: Compatible with Custom Run modifiers, split-path mods (catch-up follows the host's route), and player-limit-break mods.

---

## FAQ & Known Limitations

- **Why are catch-up rewards disabled when the save is at the start of combat?**  
  Granting catch-up cards/relics/upgrades during an active combat mutates combat state mid-fight and triggers multiplayer `StateDivergence` desyncs. Finish the current combat first (so the save is at a post-combat map checkpoint) to join with catch-up rewards, or join with **No Catch-Up**.
- **Why does the missing-mod warning appear on the host's screen instead of the unmodded guest's?**  
  An unmodded client runs 100% vanilla code; STS2's native protocol only supports fixed disconnect error codes and cannot push custom UI modals to unmodded clients.
- **Why are Card Upgrades and Custom Run start modifiers skipped in Safe-Sync mode?**  
  STS2's `RewardSynchronizer` natively broadcasts gold, cards, relics, potions, and card removals to unmodded peers, whereas card upgrades use lockstep `PlayerChoiceSynchronizer` and custom modifiers mutate decks locally. Skipping them with a detached RNG guarantees zero `StateDivergence` desyncs for unmodded teammates. For the full feature set, have everyone install MidRunJoin.

---

## Building from Source

Requires **.NET 9 SDK** and a local Windows installation of *Slay the Spire 2* (configurable via `STS2_GAME_DIR` environment variable or `-p:GameDir`):

```powershell
dotnet build .\MidRunJoin.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

Release output (`MidRunJoin.dll` + `MidRunJoin.json`) is staged to `dist/MidRunJoin/`.

---

## Project Layout

| Path | Responsibility |
|---|---|
| [MidRunJoin.json](MidRunJoin.json) | Mod manifest (`v0.5.1`, `affects_gameplay: false`) |
| [src/Entry.cs](src/Entry.cs) | `ModInitializer` entry point and Harmony patch registration |
| [src/JoinLobby.cs](src/JoinLobby.cs) | Floating entry button, roster editor modal, inline 3-button remove menu, lobby ready guard |
| [src/MissingModWarning.cs](src/MissingModWarning.cs) | Host `PeerVersionInfo` detection, missing-mod modal with vertical player list, translucent toast, Safe-Sync downgrade |
| [src/Candidate.cs](src/Candidate.cs) | Candidate `SerializableRun` builder, stash restore, early-floor history backfill |
| [src/PlayerStash.cs](src/PlayerStash.cs) | Run-fingerprinted player progress stash with auto-cleanup on `DeleteCurrentMultiplayerRun` |
| [src/CatchUp/ICatchUpFlow.cs](src/CatchUp/ICatchUpFlow.cs) | Catch-up strategy interface & host-route history scanner |
| [src/CatchUp/CatchUpRegistry.cs](src/CatchUp/CatchUpRegistry.cs) | Native room-drop catch-up flow (Full + Safe-Sync modes) |
| [src/CatchUp/CatchUpCoordinator.cs](src/CatchUp/CatchUpCoordinator.cs) | 4-band 8-bit marker codec (`240..255`), post-load catch-up & per-player modifier orchestration |
| [src/HistorySafety.cs](src/HistorySafety.cs) | Post-run settlement & Run History hover safety patches |
| [src/Compat.cs](src/Compat.cs) | Cross-version compatibility shims (`public` + `public-beta`) |
| [src/SteamHelper.cs](src/SteamHelper.cs) | Steam friends listing, persona resolution, lobby invites, Steam Overlay web page |
| [src/VanillaUi.cs](src/VanillaUi.cs) | Vanilla STS2 UI styling helpers & procedural save icon |
| [src/Localization.cs](src/Localization.cs) | Data-driven localization loader (`localization/*.json`, embedded + external overrides) |
| [localization/](localization/) | Standalone JSON localization tables ([eng.json](localization/eng.json), [zhs.json](localization/zhs.json)) |

---

## Testing

See [tests/ACCEPTANCE.md](tests/ACCEPTANCE.md) for the full 4-layer test matrix (Unit Tests → CompatProbe → Headless In-Game Fixture → 4-Process Real ENet E2E):

```powershell
pwsh -NoProfile -File .\tests\Run-AllTests.ps1 -GameDir "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

---

## License & Credits

- Licensed under the [MIT License](LICENSE).
- Development notes: Initial feasibility research and architecture exploration were assisted by **GPT-6 Astra**; core implementation, cross-version compatibility, and automated test engineering were assisted by **Gemini Neon 4**.
