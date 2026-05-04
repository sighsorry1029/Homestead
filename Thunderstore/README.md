# Homestead

Homestead is a Valheim BepInEx/Jotunn mod for dedicated-server homestead maintenance and player-driven building tools.

It combines inactive-player zone archiving, zone bundle restore, native WearNTear blueprints, blueprint trading, area save/dismantle tools, zone limits, build camera controls, placement helpers, zone UI, and Dvergr circlet quality-of-life features.

## Requirements

Required runtime dependencies:

- `denikson-BepInExPack_Valheim`
- `ValheimModding-Jotunn`

Bundled into `Homestead.dll`:

- `ServerSync`
- `YamlDotNet`

Only BepInExPack Valheim and Jotunn are runtime dependencies. ServerSync and YamlDotNet are bundled, and external admin/building mods are not required for Homestead's current zone bundle, blueprint, store, or archive workflows.

If Circlet Extended is installed, Homestead disables its own Dvergr circlet handling to avoid feature conflicts.

## Who Should Install It?

| Target | Recommendation |
| --- | --- |
| Dedicated server | Required for zone archiving, zone reset, server blueprint store data, synced configs, and authoritative commands. |
| Admin client | Recommended for in-game admin commands, zone restore workflows, and visual tools. |
| Regular client | Required for the Homestead hammer tab, native blueprints, blueprint store UI, placement helpers, zone UI, Dvergr circlet features, and synced client/server behavior. |

## Data Folders

Homestead stores generated data under one profile folder:

```text
BepInEx/config/Homestead/
  Blueprints/
  ServerBlueprints/
    PlanGhosts/
    Store/
  ZoneBundles/
  Diagnostics/
  activity.yml
  zones.yml
```

There are no per-world subfolders. Back up `BepInEx/config/Homestead/` if you want to preserve saved blueprints, store listings, archive records, diagnostic reports, activity data, and zone bundles.

## Feature Overview

| Feature | What It Does |
| --- | --- |
| Homestead hammer tab | Adds Area Save, Area Dismantle, Blueprint Store, and saved blueprint entries through Jotunn. |
| Area Save | Selects a rectangular WearNTear area and saves it as a native Homestead blueprint. |
| Area Dismantle | Removes eligible WearNTear in a selected area and returns build materials. |
| Native blueprints | Lets players place a blueprint chest, deposit materials, preview ghost progress, and confirm the final build. |
| Blueprint Store | Lets players list, preview, buy, hide, delist, edit, offer on, and withdraw payouts for server-backed blueprint listings. |
| Zone bundles | Saves and loads one zone, a rectangular zone range, or a full non-rectangular archive shape. |
| Auto archive | Finds zones whose WearNTear creators are inactive, saves eligible clusters, and optionally resets the source zones. |
| Zone limits | Counts WearNTear per zone and can block over-limit building. |
| Placement controls | Adds position nudging, rotation step control, grid snap, key hints, and a unified placement HUD. |
| Build camera | Adds a configurable build camera with server-side restrictions and optional Dvergr light follow. |
| Zone UI | Shows the current zone number and ground boundary for client-side zone lookup. |
| Dvergr circlet | Adds per-item light toggle, range/intensity adjustment, usage-based durability drain, repair station config, and tooltip hints. |
| Localization | Uses Jotunn localization with English and Korean translation files. |

## Player Workflows

### Area Save

Use `Area Save` from the Homestead hammer tab to save a selected group of WearNTear pieces.

Typical flow:

1. Select `Area Save`.
2. Aim at the build area.
3. Use the mouse wheel to resize the rectangle.
4. Use the configured area rotation modifier plus mouse wheel to rotate the rectangle.
5. Click to lift the selected preview.
6. Enter a blueprint name in the save UI.
7. Save it.

Area Save follows the configured creator policy:

- own WearNTear only
- own plus creatorless WearNTear
- own plus creatorless plus other creators

Recipe-less WearNTear prefabs are excluded from blueprints and zone bundles.

### Area Dismantle

Use `Area Dismantle` from the Homestead hammer tab to remove eligible WearNTear and return materials.

Safety rules:

- Only WearNTear with the same creator playerID as the user can be dismantled.
- Homestead blueprint/store chests are always protected internally.
- Configured blacklist prefabs are skipped.
- Containers, item stands, and armor stands with contents or attachments are skipped.

### Native Blueprint Building

Saved Homestead blueprints appear in the Homestead hammer tab with generated snapshot icons.

Typical flow:

1. Select a saved blueprint.
2. Move, rotate, and adjust the placement preview.
3. Click to place a Homestead Blueprint Chest.
4. Add required materials into the chest.
5. Ghost pieces visually fill in as materials become available.
6. Crouch-use the chest with the configured confirm hotkey to create the real WearNTear build.

Blueprint terrain support is configurable. When enabled, Homestead can restore saved terrain contact cells so terrain touches the same supported parts of the build.

### Blueprint Store

The Blueprint Store is a server-backed player marketplace for Homestead blueprints.

Players can:

- browse listings
- preview a listing before purchase
- buy with a material-price purchase chest
- hide or show listings locally
- list their own blueprints
- edit prices
- delist their own listings
- make price offers
- accept, decline, or delete offers
- buy through accepted offers
- withdraw seller payouts into payout chests
- receive store notifications

Store chest types:

| Chest | Purpose |
| --- | --- |
| Price Barrel | Sets the listing price before publishing a blueprint. |
| Purchase Chest | Holds payment for a direct purchase or accepted offer. |
| Payout Chest | Holds seller payouts; it is receive-only and is cleaned up after the shared chest timeout once emptied. |

Server admins can choose whether Blueprint Store ownership, offer buyer permissions, and direct notifications are matched by Valheim playerID or by Steam/platform identity. PlayerID mode treats each character separately; SteamID mode treats characters on the same platform account as one store identity.

### Zone UI

The client zone UI hotkey toggles:

- current zone number
- current zone boundary drawn on the ground

This is useful when a returning player wants to tell an admin which empty zone should receive a restored archive.

## Admin Workflows

### Zone Bundles

Zone bundles save WearNTear, selected stable ZDO fields, tamed MonsterAI data, and terrain support contact data.

Saved archives are written to:

```text
BepInEx/config/Homestead/ZoneBundles/{tag}/
```

Each archive folder contains:

- `manifest.yml`
- one `bundleNNN.zonebundle.yml` file per saved source zone

Terrain mode is fixed to SupportFill. Homestead builds a terrain support plan before it overwrites the target zone. If saved terrain contacts are available, it uses the saved-contact strategy; otherwise it falls back to collider footprint sampling. If no usable support plan can be created, the load is aborted before the target zone is cleared.

Zone bundle manifests and per-zone bundle files include `sourceZoneCreators`, which lists WearNTear creators discovered in the original source zone. This is meant for admin review of mixed-owner archives and may include creators whose individual pieces were later skipped by recipe or support filters.

### Inactive Player Auto Archive

The auto archive scanner is designed for dedicated servers.

Typical flow:

1. Homestead records player activity in `activity.yml`.
2. The scanner inspects WearNTear creators in world zones.
3. A zone becomes a candidate only when all WearNTear creators in that zone are archive-eligible.
4. Connected candidate zones are grouped into clusters even when their creator sets differ.
5. Each cluster is saved as a zone bundle archive, with `sourceZoneCreators` written into the manifest and per-zone bundle files for admin review.
6. Depending on config, the original zones are saved only, saved and reset, or small clutter clusters are reset without saving.

Archive protection can be configured in `zones.yml` by Steam ID, playerID, or player name.

For troubleshooting, `hs_archive_debug_zone (x,z)` writes a YAML report under `Diagnostics/` explaining why one zone is or is not an auto archive candidate.

## Command Quick Reference

Most admin commands can be run from the dedicated server console, RCON, or an admin client with Homestead installed. Supported admin-client commands are routed to the server by Homestead RPC.

For more detail, see [docs/Commands.md](docs/Commands.md).

### Zone Bundle Commands

| Command | Syntax | Purpose |
| --- | --- | --- |
| Save one zone | `hs_savezone (x,z) tag` | Saves one source zone. |
| Save zone range | `hs_savezone (x~x,z~z) tag` | Saves a rectangular source range. |
| Load one zone | `hs_loadzone (x,z) tag to (x,z) [offset=Y]` | Loads one saved source zone to a target zone. |
| Load zone range | `hs_loadzone (x~x,z~z) tag to (x,z) [offset=Y]` | Loads a saved rectangular range to a target anchor. |
| Load full archive | `hs_loadarchive tag to (x,z) [offset=Y]` | Loads every zone in the archive manifest and preserves non-rectangular shapes. |

Notes:

- `to (x,z)` is recommended on dedicated servers because there is no local player zone.
- `offset=Y` applies a vertical load offset.
- `hs_loadarchive` is the preferred command for auto archive folders and non-rectangular clusters.

Examples:

```text
hs_savezone (-21,-4) test_base
hs_loadzone (-21,-4) test_base to (10,3)

hs_savezone (-21~-20,-4) old_base
hs_loadzone (-21~-20,-4) old_base to (10,3)

hs_loadarchive auto_halla_c001 to (20,-3)
```

### Auto Archive Commands

| Command | Syntax | Purpose |
| --- | --- | --- |
| Run scan | `hs_archive_scan [dry|save|reset]` | Runs the inactive-player scanner manually. |
| Status | `hs_archive_status` | Shows scanner state, counts, schedule, and file paths. |
| Scan one Steam owner | `hs_archive_player steamID [dry|save|reset]` | Runs archive logic for one Steam owner. |
| Recent runs | `hs_archive_list` | Lists recent archive runs and cluster records. |
| Mark player seen | `hs_archive_mark_seen playerID` | Updates one Valheim playerID as seen now. |
| Ignore player | `hs_archive_ignore_player playerID [on|off]` | Protects or unprotects a playerID from auto archive. |
| Restore original zones | `hs_archive_restore tag` | Loads an archive back to the original saved zones. |
| Schedule anchor | `hs_archive_schedule [status|now|clear|last yyyy-MM-dd HH:mm|next yyyy-MM-dd HH:mm]` | Shows or adjusts the automatic scan schedule anchor. |
| Debug one zone | `hs_archive_debug_zone (x,z)` | Writes a diagnostic YAML report for one zone's archive eligibility. |

Mode arguments:

- `dry`: scan and report only.
- `save`: save matching archives but do not reset.
- `reset`: save matching archives and reset eligible source zones.

Examples:

```text
hs_archive_scan dry
hs_archive_scan save
hs_archive_scan reset

hs_archive_player 76561198000000000 dry
hs_archive_player steam:76561198000000000 reset

hs_archive_schedule status
hs_archive_schedule last 2026-05-02 15:00
hs_archive_schedule next 2026-05-03 03:00

hs_archive_debug_zone (-7,12)
```

### Blueprint Chest Maintenance

| Command | Syntax | Purpose |
| --- | --- | --- |
| Clear blueprint chests | `hs_clearchests [dry]` | Deletes Homestead blueprint build/store chest ZDOs from the world. |

Use `dry` first to count matching chests without deleting them:

```text
hs_clearchests dry
hs_clearchests
```

## Common Admin Recipes

### Test Save And Restore

```text
hs_savezone (-21,-4) test_base
hs_loadzone (-21,-4) test_base to (10,3)
```

### Move A Multi-Zone Build

```text
hs_savezone (-21~-20,-4) old_base
hs_loadzone (-21~-20,-4) old_base to (10,3)
```

### Restore A Saved Auto Archive Elsewhere

```text
hs_archive_list
hs_loadarchive auto_halla_c001 to (20,-3)
```

### Run A Safe Auto Archive Check

```text
hs_archive_scan dry
```

### Save Eligible Inactive Zones Without Reset

```text
hs_archive_scan save
```

### Save And Reset Eligible Inactive Zones

```text
hs_archive_scan reset
```

## Dedicated Server Notes

Homestead is safest on a dedicated server because archive scans, ZDO inspection, file writes, and reset workflows need authoritative server state.

Normal clients do not run inactive-player archive/reset logic. Clients install Homestead for synced config, build tools, blueprint UI, store UI, key hints, zone UI, and Dvergr circlet features.

## More Documentation

- [Command Reference](docs/Commands.md)
- [Dependency And Runtime Notes](docs/DependenciesAndRuntimeNotes.md)
