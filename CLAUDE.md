# CLAUDE.md: ShowTrainer (MLB The Show 26 memory trainer for Eden)

An offline, single-player memory trainer for **MLB The Show 26 (Switch)** running in the **Eden emulator** on Windows. Built with C# / .NET 8, WPF app plus CLI. README.md has the user-facing overview; this file is the working context for Claude.

## Ground rules (from the user, always apply)
- **Offline and single-player only.**
- **Work incrementally and ask the user for in-game values.** Ask for concrete numbers or screenshots. Don't jump ahead to features they haven't chosen.
- **Every game write:**
  1. Get the user's explicit OK for that write.
  2. Back up the save first: copy the save folder to `backups/<yyyyMMdd_HHmmss>_<label>`, then check the copy with `diff -r`.
  3. Read the current value through the guards.
  4. Write, then re-read, and diff the player entry or record.
  5. The user checks the screen and saves in-game. **Verify that the SAVE_000 file time actually changed**, because one save didn't reach disk.
  6. Full Eden restart, then verify again. "Proven" / `VerifiedPersists` means the value survived that save and restart.
- Write only while the user is **on the hub**. During loading or in games, the pointers can read zeros or disagree; the guards refuse, which is correct.
- The folder is a local git repo (set up 2026-11-27 at the user's request). Commit only when the user asks, and never push without their OK. Keep the user's email and personal info out of anything external, including commits and pushed files.
- The user said **not** to fix the token-freeze display lag (the spend screen refills only after you leave and re-enter it).
- If ShowTrainer.exe is running it blocks the build; ask the user to close it.
- Python on this machine needs `export PYTHONIOENCODING=utf-8` to print player names (cp1252 errors otherwise).

## Build / run
```powershell
dotnet build ShowTrainer.sln -c Release
src\ShowTrainer.App\bin\Release\net8.0-windows\ShowTrainer.exe
src\ShowTrainer.Cli\bin\Release\net8.0-windows\showtrainer-cli.exe detect
```
- The CLI commands are `detect`, `check`, `set`, `export`, `cheatsim`, `players`, `pinfo`, `pset`, `psnap`, `pdiff`, `prestore`, `leaguesnap`, `leaguediff`, `leaguecsv`, `myedits`, `pdump`, and the scan commands `near`, `first`, `next`, `results`, `group`, `ptrscan`, `ptrcheck` and `hexdump`.
- `pset "<full name>" "<field>" <value> --backed-up` needs exactly one name match. The field is a rating (0-99) or a setting label (`"Quirk: Dead Red" on`, `"Pitching role" CP`, `"Secondary RF" 1`, `"Two-way player" 1`, `"Primary position" RF`).
- After editing the profile, copy `profiles/*.json` to the app's `bin/.../profiles`.
- App data (logs, sessions, `snapshots/`, `save-backups/`, `edit-memory/`, `league/`) lives in `%LOCALAPPDATA%\ShowTrainer`. Documents is OneDrive-redirected, and old logs and one backup remain in `Documents\ShowTrainer` on OneDrive.

## Environment and ids
- **Eden build:** 467ab6441a. **Game:** v1.0.20. **Title ID:** `0100AA7024094000`. **Main build ID:** `AEE20BBC6F47C0D8`.
- Main and heap move on every launch (ASLR). Chains starting from main survive this.
- **Save file:** `%APPDATA%/eden/nand/user/save/0000000000000000/37C05B2D6B76AC1944AB5DC5D0FAA6D5/0100AA7024094000/CARR00000000A74E327E/SAVE_000`
- **Guest memory discovery** (see README):
  - A software page table is the primary path; the fastmem arena is the fallback.
  - Modules are found through MOD0 headers.
  - The heap is the largest VA run without modules.
  - The title ID is read from `eden_log.txt`.

## Code map
| File | What it does |
|---|---|
| `src/ShowTrainer.Core/Guest/*` | GuestLocator, GuestMemory (Read/Write/Refresh) and TitleInfo |
| `src/ShowTrainer.Core/Scan/*` | ScanEngine, GroupScanner and FreezeService. `TryWrite` applies `Mask`: it keeps the other bits and refuses values that don't fit. |
| `src/ShowTrainer.Core/Session/AddressEntry.cs` | Entry model: Pointer chain, `Mask` (byte?), `Own(raw)` to pull the masked value out, `Row` (SeasonRow: Year, YearOffset, Stride, MaxRows 24), Copy type and guards |
| `src/ShowTrainer.Core/Resolve/PointerScanner.cs` | PointerResolver. `CheckForWrite` resolves the chain, checks how many pointers agree, finds the season row by year (exactly one match) and then runs the guards. `RowShift` gives the offset used for export. |
| `src/ShowTrainer.Core/Resolve/PlayerTable.cs` | League player table: `All`, `Find`, `Recheck`, `Read`, `Write`, `IsUser`. `Fields` = ratings record bytes (incl. +00 overall); `EntryFields` = masked settings in the 0xB0 entry (primary, role, two-way, secondaries, 19 quirks) with `Label` and `ProvenOnUser`. Writes re-read and record to an `EditHistory`. |
| `src/ShowTrainer.Core/Resolve/PlayerSnapshot.cs` | Per-player snapshot by field name (ratings + settings, never pointers): `Take`, `Compare`, `Restore`; league `TakeAll` / `SaveAll` / `LoadAll` |
| `src/ShowTrainer.Core/Session/EditMemory.cs` | Persistent per-career edit memory (`edit-memory/<RTTS player name>.json`): every player write/undo from the app or CLI updates it via `EditHistory.Recorded`; `Status` shows kept / changed by the game; `Reapply` writes changed ones back. Editing back to the game's original value forgets the edit. |
| `src/ShowTrainer.Core/Session/EditHistory.cs` | Per-launch write history with guarded undo: revalidates the target, refuses if the bytes changed since or a later edit overlaps. `FreezeService.History` holds it; Targets writes record there too. |
| `src/ShowTrainer.Core/Export/AtmosphereCheats.cs` | Atmosphere cheat export. Masked entries are skipped; row entries are shifted by RowShift. |
| `src/ShowTrainer.Core/Session/SaveBackup.cs` | Save backup |
| `src/ShowTrainer.Cli/Program.cs` | CLI |
| `src/ShowTrainer.App/MainWindow*.cs/.xaml` | Tabs: Targets (`MainWindow.Targets.cs`), Players (`MainWindow.Players.cs`: ratings grid with fill and `+5`/`-5` values, settings panel, snapshots, auto-snapshot before the first edit to each player), My edits and League (`MainWindow.League.cs`: edit memory, league snapshot/compare, CSV), History (`MainWindow.History.cs`: undo) and Scanner. Players tab also has a position filter, sort by overall, and Copy ratings from (types another player's values, writes nothing). The timer ticks `Tick()` and `RefreshRatings()`. |
| `profiles/0100AA7024094000_AEE20BBC6F47C0D8.json` | The live profile, about 119 entries. It loads only for this exact build ID. |
| `sessions/` | Scan results and test sessions: `rtts_candidates.json`, `positions_test.json`, `twoway_test.json`, `quirk_test.json` |
| `exports/AEE20BBC6F47C0D8.txt` | Atmosphere cheats, 11 entries. `cheatsim` matched all 11. |
| `backups/` | One timestamped copy of the save folder before each write |

## Proven memory map (build AEE20BBC6F47C0D8)
### Tokens
- `[main+2C5C9D8]+12C0`, Int16, Stored.
- The Int32 copy found by `TokenAllocation` is display only.

### RTTS player entry
The entry is at `[main+3A88798]->[+1E98]`. It is one row of the league player table, which has **0xB0 bytes per entry**. The table starts about 3,150 entries before the user (Judge −3134, Rice −3131) and holds about 3,300 named players.

| Offset | Field |
|---|---|
| +18 / +1C | Probably contract money; not tested |
| +25 bit 7 | **Two-way setting**: Stored, proven, confirmed in games. +8B bit 0 is a calculated copy that the game resets. |
| +28 u32 | **Quirk bitmask**: see the next table. 14 quirks displayed fine; the card has 15 slots. |
| +38 / +40 | Pointers to other records. +38 holds small values (maybe hot zones); +40 looks like appearance data. |
| +48 | Pointer to the ratings record |
| +50 / +60 | First and last name |
| +72 bits 5–7 | Pitching role: 0 SP, 1 RP, 2 CP. Proven: user set to SP, and RTTS starts them. |
| +73 bit 6 | Throws: 0 R, 1 L (read-only; 22 players matched) |
| +74 u16 | Bits 0–8 weight (lb), bits 9–15 height (inches) (read-only) |
| +76 u16 | Bits 0–6 age (game year 2030), bits 7–8 bats: 0 R, 1 L, 2 S (read-only) |
| +84 u32 bits 7–13 | Jersey number (read-only; Trout #27 matches his card) |
| +84 u32 bits 21–28 | Secondary positions C, 1B, 2B, 3B, SS, LF, CF, RF (byte +86 bits 5–7, byte +87 bits 0–4). RF is proven; the others aren't write-tested. |
| +98 | Player ID (user: 06 0D = 3334). Equals the player's row in the league table. |
| +A2 | Primary position: 0 P, 1 C, 2 1B, 3 2B, 4 3B, 5 SS, 6 LF, 7 CF, 8 RF |

**Team is not in the entry or its +38/+40/+48 records.** Searches for roster lists of player IDs (u16/u32, and by row) found only game lineups: heap+9AD7315C holds the user's 10-man lineup next to the token record, and a 16-byte lineup table has 9 records per game. The real-life Yankees IDs sit at 186–225, but IDs follow the original rosters (Trout 1038 is on the Yankees now). Next idea: follow pointers one level deeper, or do a changed-value scan around a roster move. `pdump` and `near` (CLI) help.

Packed fields must be written with a mask, never as the whole byte or word (+A2 is the only exception). Profile guards: name letters, ID 06/0D, primary 0.

**Quirk bits (+28)**

| Bit | Quirk | Bit | Quirk |
|---|---|---|---|
| 0 | Homebody | 10 | Bad Ball Hitter |
| 1 | Road Warrior | 11 | Situational Hitter (by elimination) |
| 2 | First-Pitch Hitter | 12 | Pinch Hitter |
| 3 | Dead Red | 13 | Day Player |
| 4 | Breaking Ball Hitter | 14 | Night Player |
| 5 | Unfazed | 15 | Outlier I |
| 6 | Fighter | 16 | Outlier II |
| 7 | Pickoff Artist | 17 | Break Outlier |
| 8 | Rally Monkey (by elimination) | 18 | Table Setter |
| 9 | Stopper | | |

- The user has 12 quirks: bits 0, 1, 2, 3, 5, 6, 10, 14, 15, 16, 17, 18. They survived a save and restart and are `VerifiedPersists`.
- The other 7 quirk entries aren't save-tested.

### Ratings record (entry +48)
The same layout holds for every player.

| Offset | Field |
|---|---|
| +00 | Stored overall at the primary position. The game doesn't recalculate it live. |
| +01 / +02 | Contact L / R |
| +03 / +04 | Power L / R |
| +05 | Clutch |
| +06 | Sac Bunt |
| +07 | Drag Bunt |
| +08 | Vision |
| +09 | Discipline |
| +0A | Unknown |
| +0B | Durability |
| +0C | Speed |
| +0D | Steal |
| +0E | BR Agg |
| +0F | Arm Str |
| +10 | Arm Acc |
| +11 | Fielding |
| +12..+15 | The four Reaction parts. The card's Reaction is their average, rounded down. |
| +16 | Blocking |
| +17 | Pop Time |
| +28..+2F | Pitching: Stamina, Clutch, H/9 L, H/9 R, HR/9, K/9 L, K/9 R, Control |

- The user's pitching block is reached through `[main+3A88798]->[+1E98]->[+48]->+28`. All attributes are 99.
- **Judge Speed 38→60** is proven Stored.

### Stats
- **Season rows:** 0x3C bytes per row, year word at row+0x18, newest year in row 0. The trainer finds the row by year.
  - Base chain: `[main+3A88600]->[+0]->[+38]->+10`
  - Fields (bytes unless marked u16): +10 outs, +14 H, +16 SO (u16), +2C ER, +30 HLD, +32 L, +34 R, +35 SV, +37 BB, +39 W
  - Proven: 2029 SV 37. The 2030 entries aren't tested.
- **Career table:** `[main+3A88600]->[+0]->[+120]`
  - Fields (u16): +0 outs, +30 W, +32 L, +3A SV, +3C H, +3E R, +40 ER, +44 BB, +46 SO, +4E BF
  - Proven: Career SV 178.
  - Season edits can move the career total by the same amount (checkbox in the Targets tab).
- **Not found:** GS, BS, HR and per-pitch control. Breaks match by value only. Velocity is a guess.

## Open state / next steps
- **Trout:** the old unsaved Reaction edit is gone (Eden restarted without saving). No open action.
- **Built 2026-11-26** (the user asked to "find useful things to add and implement them"). Offline-tested only, with a fake guest-memory harness (38 checks; scratchpad, not in the repo):
  - Players tab: settings panel for any player (primary, role, two-way, secondaries, 19 quirks), overall (+00) row, fill by group and `+5`/`-5` relative values, snapshots (manual, plus automatic before the first edit to each player) with compare / restore.
  - History tab: every Targets/Players write this launch, with guarded undo. Cleared when main moves (relaunch).
  - CLI: `pinfo`, extended `pset`, `psnap` / `pdiff` / `prestore`, `leaguesnap` / `leaguediff` (spot what progression or sims change).
  - PointerResolver now treats a null link as unresolved.
  - **Read-only checks in Eden passed 2026-11-26:** `pinfo` shows the user's SP, two-way, 12 quirks; Ohtani two-way; Rice's 6 quirks. `leaguesnap` 3,309 players saved to `sessions/league_20261126_before.json`.
  - Found on 2026-11-26: user's secondary is now **3B, not RF** (+87 = 01); the user changed it in-game themselves, so the decoder is right. +25 now 0x84 (game set bit 2), so the two-way profile guard `+0 <= 0x80` was dropped. Trout's unsaved Reaction 85s are gone (75-76). His card shows CF 97 / RF 99, and +00 reads 97, which confirms +00 = overall at the primary position. The secondary-position overall (RF 99) is not in the ratings record; where it lives is unknown (probably calculated). Stats/career entries refuse on the hub right now: only 2 of 3 chains agree (main chain still reaches the user's record, Career SV 178; four alt chains point at another player's stats).
  - **First live write 2026-11-26:** `pset "Ben Rice" "Quirk: Rally Monkey" on` changed only +29 (20 -> 21) and showed on his card; `prestore` from `sessions/snap_ben_rice_before_rallymonkey.json` put the entry back byte for byte (backup `backups/20261126_181940_before_rice_rallymonkey`). Memory only, nothing saved. So quirk writes work on other players' cards; **Persistence proven 2026-11-26:** Rice's Rally Monkey (written again via `pset`) survived in-game save (SAVE_000 17:33:03 -> 18:52:52) and a full Eden restart (main 8053B000 -> 80762000); `leaguediff` against `sessions/league_20261126_before.json` showed only Rice changed (backup `backups/20261126_184125_before_rice_persist`). Rice still has Rally Monkey unless the user asks to remove it.
  - **History-tab undo proven live 2026-11-26:** the same quirk applied from the Players-tab settings panel (auto snapshot written first) showed on the card; History > Undo last put the entry back byte for byte, and the card cleared. Memory only (backup `backups/20261126_183641_before_history_undo`).
  - **Not yet tried in Eden (other writes).** Live checks needed: the settings panel shows the user's known values; a quirk write on another player shows on the card; undo; snapshot restore; leaguediff across a simmed day. Other players' settings writes are not save-tested.
- **Built 2026-11-26 (round 2, user asked to "find more useful things to add"):** My edits tab + `myedits` (persistent edit memory, re-apply what progression changed), League tab + `leaguecsv` (league compare in the UI, CSV export), Players position filter / sort / Copy ratings from. Offline harness 54/54. Live: `myedits` (0 edits; Rice's Rally Monkey predates the memory), `leaguecsv` (3,309 rows) and `leaguediff` checked read-only. The app UI for these tabs is not yet checked by the user; no write has gone through re-apply yet.
- **Round 3 (2026-11-26, user asked for team and other non-rating info):** read-only Info box in the Players tab, `pinfo` and CSV columns: jersey, age, bats, throws, height, weight, player ID (`PlayerTable.InfoFields`). Harness 56/56; live `pinfo` matches real players; the user confirmed the app Info box looks right. Team not found yet (see the memory map).
- Further ideas, not started: 2030 season stat write test; pitch repertoire; zones and contracts; other players' stats; rosters; progression locks (e.g. leaguediff, then re-apply); per-career edit memory.
  - Reliability work: test the loading-screen freeze pause, test the cheat file inside Eden, and add AOB signatures for game updates.
- **Not yet tested in real use:** the loading-screen freeze pause and the cheat file inside Eden.
- Progress notes also live in Claude's memory file: `memory/project-showtrainer-status.md`.
