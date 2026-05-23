# ZoneSavior Commands

ZoneSavior owns zone bundle, inactive-player auto archive, zone restore, and zone-limit administration.

Commands can be run from a dedicated server console, RCON, or an admin client with ZoneSavior installed. Supported admin-client commands are routed to the server by ZoneSavior RPC.

## Zone Bundle Commands

Zone bundle archives are written under:

```text
BepInEx/config/ZoneSavior/ZoneBundles/{tag}/
```

Each archive folder contains:

- `manifest.yml`
- one `bundleNNN.zonebundle.yml` file per saved source zone

### `zs_savezone`

Saves one source zone or a rectangular source range.

```text
zs_savezone (x,z) tag
zs_savezone (x~x,z~z) tag
```

Examples:

```text
zs_savezone (-21,-4) test_base
zs_savezone (-21~-20,-4) old_base
```

### `zs_loadzone`

Loads one saved source zone, or a rectangular source range, into another location.

```text
zs_loadzone (x,z) tag [to (x,z)] [offset=Y]
zs_loadzone (x~x,z~z) tag [to (x,z)] [offset=Y]
```

Examples:

```text
zs_loadzone (-21,-4) test_base to (10,3)
zs_loadzone (-21~-20,-4) old_base to (10,3)
zs_loadzone (-21,-4) test_base to (10,3) offset=2
```

Notes:

- `to (x,z)` is the target start zone.
- If `to (x,z)` is omitted, ZoneSavior uses the local player's current zone. A dedicated server console has no local player, so provide `to (x,z)` there.
- `offset=Y`, `yoffset=Y`, `y-offset=Y`, `--offset Y`, `--yoffset Y`, and `--y-offset Y` are accepted for vertical offset.
- Use `zs_loadarchive` for full manifest archives and non-rectangular clusters.

### `zs_loadarchive`

Loads every bundle listed in an archive manifest, preserving the saved non-rectangular shape.

```text
zs_loadarchive tag [to (x,z)] [offset=Y]
```

Examples:

```text
zs_loadarchive auto_halla_c178 to (-4,0)
zs_loadarchive auto_Snack_plus1_b7a5018f_c103 to (20,-3) offset=1.5
```

Notes:

- `to (x,z)` is the target anchor.
- ZoneSavior maps the saved archive's minimum source X/Z zone to this target zone and preserves every other manifest zone's relative offset.
- If `to (x,z)` is omitted, ZoneSavior uses the local player's current zone.

## Auto Archive Commands

Auto archive commands inspect inactive creators, write connected candidate zones into archive bundles, optionally reset source zones, and manage activity state.

Activity and zone rule files:

```text
BepInEx/config/ZoneSavior/activity.yml
BepInEx/config/ZoneSavior/zones.yml
```

Mode arguments:

- `dry` or `dry-run`: scan and report only.
- `save`: save matching archives but do not reset.
- `reset`: save matching archives and reset eligible source zones.

If a mode is omitted, the command uses current server config values.

### `zs_archive_scan`

Runs the inactive-player archive scanner manually.

```text
zs_archive_scan [dry|save|reset]
```

Examples:

```text
zs_archive_scan dry
zs_archive_scan save
zs_archive_scan reset
```

### `zs_archive_status`

Prints auto archive status, player record counts, recent scan time, next automatic scan time when scheduled scans are enabled, and file paths.

```text
zs_archive_status
```

### `zs_archive_player`

Runs a manual archive scan filtered to one owner.

```text
zs_archive_player steamID [dry|save|reset]
```

Examples:

```text
zs_archive_player 76561198000000000 dry
zs_archive_player steam:76561198000000000 reset
```

Notes:

- Steam ID is the intended target format.
- A short numeric playerID fallback is supported when ZoneSavior has not linked a Steam platform ID yet.

### `zs_archive_list`

Lists recent archive scanner runs and cluster records.

```text
zs_archive_list
```

### `zs_archive_mark_seen`

Marks a Valheim playerID as seen now.

```text
zs_archive_mark_seen playerID
```

Example:

```text
zs_archive_mark_seen 123456789
```

### `zs_archive_ignore_player`

Protects or unprotects a playerID from auto archive.

```text
zs_archive_ignore_player playerID [on|off]
```

Examples:

```text
zs_archive_ignore_player 123456789
zs_archive_ignore_player 123456789 on
zs_archive_ignore_player 123456789 off
```

Omitting `on|off` means `on`.

### `zs_archive_restore`

Restores an archived tag back to its original source zones.

```text
zs_archive_restore tag
```

Example:

```text
zs_archive_restore auto_halla_c178
```

This differs from `zs_loadarchive tag to (x,z)`: restore loads to the original zones recorded in the manifest.

### `zs_archive_schedule`

Shows or adjusts the automatic archive scan schedule anchor.

```text
zs_archive_schedule [status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm]
```

Examples:

```text
zs_archive_schedule
zs_archive_schedule status
zs_archive_schedule now
zs_archive_schedule clear
zs_archive_schedule last 2026-05-02 15:00
zs_archive_schedule next 2026-05-03 03:00
zs_archive_schedule next 2026-05-02T18:00:00Z
```

Notes:

- Scheduled auto scans are disabled when `Scan Interval Minutes` is `0`; manual archive commands still work.
- `now` sets the last auto scan time to the current server time.
- `clear` clears the last auto scan time.
- `last ...` sets `last_auto_scan_at`.
- `next ...` sets `last_auto_scan_at` to `next - Scan Interval Minutes`; it requires scheduled scans to be enabled.
- A date without `Z` or a timezone offset is parsed as the server computer's local time.

### `zs_archive_debug_zone`

Writes a YAML diagnostic report explaining one zone's auto archive eligibility.

```text
zs_archive_debug_zone (x,z)
```

Example:

```text
zs_archive_debug_zone (-7,12)
```
