# Homestead Commands

This document lists the console commands currently registered by Homestead.

Commands are grouped by workflow. All commands below are admin/server workflows unless noted otherwise. On a dedicated server, archive and zone-bundle commands can be run from the server console, RCON, or an admin client with Homestead installed; Homestead routes supported admin-client commands to the server with RPC.

## Zone Bundle Commands

Zone bundle commands save and load WearNTear/tamed-monster zone bundles under:

```text
BepInEx/config/Homestead/ZoneBundles/{tag}/
```

### `hs_savezone`

Saves one or more source zones as a SupportFill zone bundle archive.

```text
hs_savezone (x,z) tag
hs_savezone (x~x,z~z) tag
```

Examples:

```text
hs_savezone (-21,-4) test1
hs_savezone (-21~-20,-4) old_base
```

Notes:

- A single zone and a rectangular source range are both supported.
- The saved tag becomes the archive folder name after path sanitization.
- Terrain mode is fixed to SupportFill; there is no terrain-mode command option.

### `hs_loadzone`

Loads one saved source zone, or a rectangular source range, into another location.

```text
hs_loadzone (x,z) tag [to (x,z)] [offset=Y]
hs_loadzone (x~x,z~z) tag [to (x,z)] [offset=Y]
```

Examples:

```text
hs_loadzone (-21,-4) test1 to (1,0)
hs_loadzone (-21~-20,-4) old_base to (10,3)
hs_loadzone (-21,-4) test1 to (1,0) offset=2
```

Notes:

- `to (x,z)` is the target start zone.
- If `to (x,z)` is omitted, Homestead uses the local player's current zone. A dedicated server console has no local player, so provide `to (x,z)` there.
- `offset=Y`, `yoffset=Y`, `y-offset=Y`, `--offset Y`, `--yoffset Y`, and `--y-offset Y` are accepted for vertical offset.
- Use `hs_loadarchive tag to (x,z)` for whole archive manifests and non-rectangular clusters.

### `hs_loadarchive`

Loads every bundle listed in an archive manifest, preserving the saved non-rectangular shape.

```text
hs_loadarchive tag [to (x,z)] [offset=Y]
```

Examples:

```text
hs_loadarchive auto_halla_c178 to (-4,0)
hs_loadarchive auto_Snack_plus1_b7a5018f_c103 to (20,-3) offset=1.5
```

Notes:

- `to (x,z)` is the target anchor. Homestead maps the saved archive's minimum source X/Z zone to this target zone and preserves every other manifest zone's relative offset.
- If the saved archive is an L-shape or other non-rectangular cluster, the manifest shape is preserved.
- If `to (x,z)` is omitted, Homestead uses the local player's current zone. Use `to (x,z)` from a dedicated server console.

## Auto Archive Commands

Auto archive commands inspect inactive creators, write connected candidate zones into archive bundles, optionally reset source zones, and manage the activity state file. Mixed-owner clusters stay together when they are adjacent; creator playerIDs are written into the manifest and bundle files for later admin review:

```text
BepInEx/config/Homestead/activity.yml
```

Zone limit rules are stored beside it:

```text
BepInEx/config/Homestead/zones.yml
```

Mode arguments:

- `dry` or `dry-run`: scan and report only.
- `save`: save matching archives but do not reset.
- `reset`: save matching archives and reset eligible source zones.

If a mode is omitted, the command uses the current server config values.

### `hs_archive_scan`

Runs the inactive-player archive scanner manually.

```text
hs_archive_scan [dry|save|reset]
```

Examples:

```text
hs_archive_scan dry
hs_archive_scan save
hs_archive_scan reset
```

### `hs_archive_status`

Prints the auto archive status, player record counts, recent scan time, next automatic scan time, and activity file path.

```text
hs_archive_status
```

### `hs_archive_player`

Runs a manual archive scan filtered to one owner.

```text
hs_archive_player steamID [dry|save|reset]
```

Examples:

```text
hs_archive_player 76561198000000000 dry
hs_archive_player steam:76561198000000000 reset
```

Notes:

- Steam ID is the intended target format.
- A short numeric playerID fallback is supported for cases where Homestead has not linked a Steam platform ID yet.
- If a Steam ID is unknown, the player must join while Homestead activity tracking is active, or an admin must use `hs_archive_mark_seen` with the playerID fallback.

### `hs_archive_list`

Lists the 10 most recent archive scanner runs and up to 5 cluster records per run.

```text
hs_archive_list
```

### `hs_archive_mark_seen`

Marks a playerID as seen now in `Homestead/activity.yml`.

```text
hs_archive_mark_seen playerID
```

Example:

```text
hs_archive_mark_seen 123456789
```

Notes:

- This command takes a Valheim playerID, not a player name.
- Prefer Steam ID workflows when available; this is mainly a fallback/manual repair command.

### `hs_archive_ignore_player`

Protects or unprotects a playerID from auto archive.

```text
hs_archive_ignore_player playerID [on|off]
```

Examples:

```text
hs_archive_ignore_player 123456789
hs_archive_ignore_player 123456789 on
hs_archive_ignore_player 123456789 off
```

Notes:

- Omitting `on|off` means `on`.
- Ignored playerIDs are stored in `Homestead/activity.yml`.

### `hs_archive_restore`

Restores an archived tag back to its original source zones.

```text
hs_archive_restore tag
```

Example:

```text
hs_archive_restore auto_halla_c178
```

Notes:

- This is different from `hs_loadarchive tag to (x,z)`.
- `hs_archive_restore` loads the archive back to the original zones recorded in the manifest.

### `hs_archive_schedule`

Shows or adjusts the automatic archive scan schedule anchor.

```text
hs_archive_schedule [status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm]
```

Examples:

```text
hs_archive_schedule
hs_archive_schedule status
hs_archive_schedule now
hs_archive_schedule clear
hs_archive_schedule last 2026-05-02 15:00
hs_archive_schedule next 2026-05-03 03:00
hs_archive_schedule next 2026-05-02T18:00:00Z
```

Notes:

- `status` prints the current interval, last auto scan, and next auto scan.
- `now` sets the last auto scan time to the current server time.
- `clear` clears the last auto scan time, allowing the interval logic to treat it as never scanned.
- `last ...` sets `last_auto_scan_at`.
- `next ...` sets `last_auto_scan_at` to `next - Scan Interval Minutes`.
- A date without `Z` or a timezone offset is parsed as the server computer's local time.
- A date with `Z` or an explicit offset is parsed as UTC/offset time.

## Blueprint Admin Commands

### `hs_clearchests`

Deletes every Homestead blueprint chest ZDO in the world:

- blueprint build chest
- store price chest
- store purchase chest
- store payout chest

```text
hs_clearchests [dry]
```

Examples:

```text
hs_clearchests dry
hs_clearchests
```

Notes:

- `dry` or `dry-run` counts matching chests without deleting them.
- This scans all world ZDOs, so use it as an admin maintenance command rather than a frequent player-facing action.
- Unconfirmed store price chest draft files owned by those chests are deleted with the chest.
- On a dedicated server, this can be run from the server console, RCON, or an admin client with Homestead installed.

## Removed Or UI-Only Features

- Native blueprint save/load is driven by the Homestead hammer tab and blueprint chest flow.
- There is no current `hs_zoneui` console command. Zone UI is toggled by the configured client hotkey.
