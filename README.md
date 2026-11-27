# ShowTrainer: offline memory trainer for MLB The Show 26 on Eden

Single-player, offline use only. C# / .NET 8, WPF.

## Build / run

```powershell
dotnet build ShowTrainer.sln -c Release
src\ShowTrainer.App\bin\Release\net8.0-windows\ShowTrainer.exe        # UI
src\ShowTrainer.Cli\bin\Release\net8.0-windows\showtrainer-cli.exe detect   # headless check
```

Logs: `Documents\ShowTrainer\logs\trainer.log`. Sessions: `Documents\ShowTrainer\sessions\`.

## How guest memory is found (Eden build 467ab6441a)

1. **DRAM backing**: the largest committed `MEM_MAPPED` read/write view (4 GB here).
2. **Software page table** (primary): a private 1 GB allocation (2^27 entries for a 39-bit address space).
   Each entry is `(hostPtr - VA) | typeBits`, so `host = (entry & ~0xFFF) + VA`. It is picked when 100% of its
   non-zero entries decode into the DRAM backing.
3. **Fastmem arena** (fallback, cross-checked): a 512 GB reservation right after the backing. Its base is the
   reservation start **aligned up to 2 MB**, and `host = arena + VA`.
4. **Modules**: page starts whose word at +4 points at a `MOD0` header (the offset can be tens of MB).
   The order is rtld, main, [subsdk*], sdk. Main's build ID comes from its GNU build-id ELF note.
5. **Heap**: the largest mapped VA run that contains no modules.
6. **Title ID**: read from Eden's log (`%APPDATA%\eden\log\eden_log.txt`).

Detected for MLB The Show 26 v1.0.20: title `0100AA7024094000`, main build `AEE20BBC6F47C0D8`, main at `0x80489000`.

## Using the trainer

Boot the game, load your career, then start the app. The profile in `profiles/` loads only for its exact build ID.
**Targets** tab: press **Back up save now**, type a value, **Apply**, check in-game, then save. "Proven" means an edit survived a save and an Eden restart.
Season stat edits move the matching career total by the same amount (checkbox). Every write re-resolves pointers and checks the record first.

## CLI

```
showtrainer-cli check  --session profiles\<file>.json          # test every entry, no writes
showtrainer-cli set "Stamina" 80 --backed-up --session <file>  # guarded write
showtrainer-cli export <out.txt> --session <file>              # proven entries as Atmosphere cheats
showtrainer-cli cheatsim <out.txt> --session <file>            # simulate the cheats, no writes
showtrainer-cli first|next|results|group|ptrscan|ptrcheck|hexdump ...
```

## Status

- [x] Phase 1: scanner
- [x] Phase 2: pointer chains, build check, write guard (no AOB signatures: not needed so far)
- [ ] Phase 3: tokens, eight pitching attributes, 2029/career saves proven. Other stats and breaks located, not write-tested. Pitching role, secondary RF, two-way (+25 bit 7) and quirks (+28 bitmask, 12 bits) proven; other secondary positions decoded, not write-tested. Season rows are found by year (a new season adds a row on top). Velocity is a guess. GS/BS/HR and per-pitch control not found
- [x] Phase 4: Targets tab, backup, Atmosphere export. Token freeze works in-game. Not yet tested: the loading-screen freeze pause, the cheat file inside Eden

## Other players (Players tab, `players` / `pset` in the CLI)

Every player in the league table (0xB0 per entry; found from the RTTS player entry) has a ratings record at entry +48.
Offsets in that record: +01 Contact L, +02 Contact R, +03 Power L, +04 Power R, +05 Clutch, +06 Sac Bunt, +07 Drag Bunt, +08 Vision, +09 Discipline,
+0B Durability, +0C Speed, +0D Steal, +0E BR Agg, +0F Arm Str, +10 Arm Acc, +11 Fielding, +12..+15 Reaction parts (card Reaction = their average, rounded down), +16 Blocking, +17 Pop Time,
+28..+2F pitching (Stamina, Clutch, H/9 L, H/9 R, HR/9, K/9 L, K/9 R, Control). Matched against Judge, Rice and Skubal cards. Judge Speed 38 -> 60 showed on his card and survived save + Eden restart (2026-11-25).
+00 is the stored overall at the primary position (editable; the game does not recalculate it right away).

Info (read-only, from the player entry): jersey (+84 bits 7-13), age (+76 bits 0-6), bats (+76 bits 7-8: R/L/S), throws (+73 bit 6), height and weight (+74: bits 9-15 inches, bits 0-8 lb), player ID (+98).
Shown in the Players tab, `pinfo` and the CSV export. The team is not found yet.

The Players tab also has:
- **Player settings** panel. These are entry fields, written with a mask so only their bits change: primary position (+A2), pitching role (+72 bits 5-7),
  two-way (+25 bit 7), secondary positions (+86/+87) and the 19 quirks (+28 bitmask). Proven on the RTTS player: role, two-way, RF and 12 quirks. Not save-tested on other players.
- **Fill**: type one value (or `+5` / `-5`) into a whole group of ratings, then Apply.
- **Snapshots**: `Snapshot player` saves ratings and settings by name to `%LOCALAPPDATA%\ShowTrainer\snapshots`. One is also taken automatically before the first edit
  to each player. `Compare / restore...` shows the differences and can write them back.

**My edits** tab: every player edit (from the app or CLI) is remembered per career, across launches, in `%LOCALAPPDATA%\ShowTrainer\edit-memory`.
Check now shows which ones the game has changed since (progression, streaks, slumps), and Re-apply writes them back through the usual guards.

**League** tab: snapshot every player, play or sim, then compare to list every rating and setting that changed. Export ratings CSV writes all players to a spreadsheet.
The Players tab can also filter by position, sort by overall, and copy another player's ratings into the New value column.

**History** tab: every value written this launch (Targets and Players). Undo writes the old value back only if the target still checks out and nothing has changed it since.

CLI: `pinfo <name>`; `pset "<full name>" "<rating or setting>" <value> --backed-up` (e.g. `"Quirk: Dead Red" on`, `"Pitching role" CP`);
`psnap` / `pdiff` / `prestore`; `myedits [--reapply --backed-up]`; `leaguecsv <file>`; and `leaguesnap <file>` + `leaguediff <file>` to list every player whose ratings or settings changed (for example across a simmed day).
