# Homestead Commands

Homestead is mostly driven by the hammer tab UI. Its current console command surface is limited to blueprint chest maintenance.

## `hs_clearchests`

Deletes Homestead blueprint-related chest ZDOs from the world:

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
- This is an admin maintenance command and scans world ZDOs.
- Unconfirmed store price chest draft files owned by deleted chests are cleaned up with the chest.
- On a dedicated server, run it from the server console, RCON, or an admin client with Homestead installed.

## UI-Only Workflows

These workflows are intentionally UI-driven instead of command-driven:

- Area Save
- Area Dismantle
- native blueprint placement
- Blueprint Store listing/buying/offers
- build camera
- placement adjustment
- Dvergr circlet controls
