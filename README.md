# Homestead

![](https://i.ibb.co/C5vLNbRs/fullshot.png) <br>
Homestead is the player-facing construction half of the Homestead/ZoneSavior split.
It adds native WearNTear blueprints, Area Save, Area Dismantle, a server-backed Blueprint Store, build camera controls, placement helpers, key hints, and Dvergr circlet quality-of-life features.

![](https://i.ibb.co/FL5GVNbn/01-hammertab.png) <br>
Homestead tab within vanilla hammer. <br>
`Area save` to make a blueprint, `Area dismantle` to teardown the builds, `blueprint shop` to sell the blueprints <br>

![](https://i.ibb.co/qM3Bkd39/areasave.gif) <br>
Use build camera and area save to make blueprints. <br>

![](https://i.ibb.co/Wpnhwd60/blueprintbuild.gif) <br>
Place your blueprint and put according materials into blueprint chest and confirm it. (Stations are needed to) <br>

![](https://i.ibb.co/Pv4LZnKm/blueprintstore.gif) <br>
Alt+click your blueprint and put it on the ground and put a price on your blueprint and list in on blueprint store <br>

![](https://i.ibb.co/Y7QdKQ7P/store.png) <br>
You can offer price for blueprints and get notifications for blueprints being enlisted and for offers being accepted/declined/suggested <br>

![](https://i.ibb.co/qYddZS39/buyandwithdraw.gif) <br>
Buy the blueprints and pay the price for it. And if you have sold your blueprints you can withdraw that from the store. <br>.

![](https://i.ibb.co/Qjz7SDZg/dismantle.gif) <br>
No more clicking all the pieces or using sledge hammer to teardown the build. You can use the tool `area dismantle` (Only dismantles what each player has built, respectively)

![](https://i.ibb.co/ymQJrHRd/buildcamera.gif) <br>
Build camera, dvergr circlet attached to build camera, dvergr circlet light adjustment, lock in pov (freefly feature), position adjustment

## Requirements

- BepInExPack Valheim
- Jotunn

Jotunn is a hard dependency. Homestead uses it for the custom hammer tab/category, pieces, localization, and blueprint UI integration.

Optional integrations:

- AzuCraftyBoxes: Homestead blueprint chests can pull missing materials from nearby allowed containers.
- ContentsWithin: Homestead adjusts blueprint/store chest requirement displays so requirement slots remain readable.
- AzuExtendedPlayerInventory: Homestead supports Dvergr circlet visuals when custom equipment slots are used.
- ZoneSavior: optional companion mod for server zone maintenance. Homestead does not require it.

## Main Features

- Homestead hammer tab with Area Save, Area Dismantle, Blueprint Store, and saved blueprint pieces.
- Area Save rectangle tool for saving WearNTear structures into native Homestead blueprints.
- Area Dismantle rectangle tool for dismantling player-owned WearNTear and dropping materials.
- Native blueprint build workflow with ghost pieces, material chest, requirement display, and final confirm.
- Server-backed Blueprint Store with listing, buying, offers, accepted offer prices, delisting, hiding, payouts, notifications, and withdraw chests.
- Build camera mode with configurable distance, comfort scaling, pickup behavior, demister/circlet light follow, and look-at lock.
- Placement controls: grid snap, position nudging, rotation step, X/Z axis rotation offsets, and key hints.
- Dvergr circlet extension: per-item light on/off, intensity/range controls, durability drain while lit, repair station control, and tooltip help.

## Install Notes

- Install on clients for the player-facing hammer tools and UI.
- Install on the server if you want synchronized config, server-backed Blueprint Store, blueprint ghost persistence, or multiplayer blueprint builds.
- Homestead can be used without ZoneSavior. ZoneSavior can be used without Homestead.
- For dedicated servers, server-side Homestead data is written under `BepInEx/config/Homestead/`.

## Data And File Layout

Homestead uses one normal BepInEx config file plus a data folder under `BepInEx/config/Homestead/`.

```text
BepInEx/config/
  sighsorry.Homestead.cfg
  Homestead/
    Blueprints/
    ServerBlueprints/
      PlanGhosts/
      Store/
        catalog.yml
        *.hsbp.yml
```

### `sighsorry.Homestead.cfg`

Main BepInEx config.

Important sections:

- `01 - General`
  - server config lock
- `02 - Client`
  - build counter display time
  - unified HUD position/font size
- `03 - Blueprint`
  - native blueprint terrain support
  - blueprint chest rows and confirm hotkey
  - AzuCraftyBoxes pull mode
  - upload/entry/icon safety limits
  - blueprint chest cleanup, map icons, and per-player active chest limits
  - Area Save/Area Dismantle size, colors, creator policy, and blacklist
  - preview ghost color/brightness
- `04 - Blueprint Store`
  - store listing lifetime and max listings per Steam/platform identity
  - store identity mode
  - store UI scaling
  - notification mode and anonymous notification policy
  - listing and purchase pending preview colors
  - store list/back hotkeys
- `05 - Build Camera`
  - build camera enabled state
  - camera distance mode and comfort scaling
  - movement speed
  - build camera hotkey
  - pickup range and comfort restriction
  - demister and Dvergr circlet light follow settings
- `06 - Placement Controls`
  - grid snap hotkey and grid size
  - position adjust step and modifier
  - shared rotation step
  - ordinary-piece X/Z axis rotation offsets
- `08 - Dvergr Circlet`
  - circlet extension enabled state
  - fuel duration, repair station, max intensity/range, adjustment step
  - light toggle and adjustment hotkeys
  - remote visual sync

### `Homestead/Blueprints/`

Client/local blueprint folder.

Area Save writes `.hsbp.yml` files here. These are the player's own saved blueprints and are shown in the Homestead hammer tab.

Saved blueprint files include:

- WearNTear prefab entries
- local position/rotation/scale
- saved ZDO data needed for reconstruction
- material requirements inferred from the build pieces
- optional terrain support contacts when terrain support capture is enabled
- icon/snapshot metadata when available

### `Homestead/ServerBlueprints/PlanGhosts/`

Server-side temporary blueprint payload folder.

On a dedicated server, a client can place a blueprint ghost/chest for a blueprint that exists only on that client. Homestead uploads a bounded, compressed blueprint payload so the server can keep enough information to confirm the build, restore ghost visuals while loaded, and clean up stale plan data.

These files are temporary server state, not the player's normal blueprint library.

### `Homestead/ServerBlueprints/Store/`

Server-backed Blueprint Store folder.

It contains:

- `catalog.yml`: listings, offers, notifications, purchase counts, balances, and store state.
- `*.hsbp.yml`: blueprint bodies owned by active store listings or temporary store drafts.

The server owns this data. Clients request summaries, icons, previews, offers, and purchase payloads through Homestead RPC instead of directly reading the server files.

## Hammer Tab Tools

Homestead adds a custom `Homestead` hammer category.

### Area Save

Use Area Save to capture a group of WearNTear pieces as a native Homestead blueprint.

Typical flow:

1. Equip the hammer.
2. Select `Homestead -> Area Save`.
3. Aim the rectangular area over the structure.
4. Use mouse wheel controls to rotate/resize the rectangle.
5. Click to lift a preview of selected pieces.
6. Review the preview.
7. Save through the naming UI.

Default area tool controls:

- `Wheel`: rotate the rectangle
- `Alt + Wheel`: scale width and depth together while keeping the current ratio
- `Mouse3 + Wheel`: adjust depth
- `Mouse4 + Wheel`: adjust width
- placement adjust modifier + arrow keys/PgUp/PgDn: nudge the tool/preview

Selection policy is controlled by `Area Save Creator Mode`:

- `AllCreators`: save any WearNTear in the area.
- `OwnedAndCreatorless`: save your own plus creatorless WearNTear.
- `OwnedOnly`: save only your own WearNTear.

Area Save skips invalid/non-build recipe prefabs. The in-game overlay marks which pieces will be saved and which will be skipped.

### Area Dismantle

Use Area Dismantle to remove player-owned WearNTear in a rectangle and return build materials as item stacks.

It is intended for player cleanup, not admin griefing:

- only matching owner/player pieces are dismantled
- built-in Homestead blueprint/store chests are protected
- extra blacklist entries can be configured
- containers, item stands, and armor stands with contents/attachments are skipped

Default controls match Area Save:

- `Wheel`: rotate
- `Alt + Wheel`: scale width and depth together while keeping the current ratio
- `Mouse3 + Wheel`: adjust depth
- `Mouse4 + Wheel`: adjust width
- placement adjust modifier + arrow keys/PgUp/PgDn: nudge

## Native Blueprint Build Flow

Saved blueprints appear as build pieces in the Homestead hammer tab.

Typical flow:

1. Select a saved blueprint in the Homestead tab.
2. Place the blueprint chest/ghost at the target location.
3. Open the Homestead blueprint chest.
4. Deposit required materials.
5. Requirement slots show the needed item icons and remaining amounts.
6. When the requirements are complete, use the configured confirm hotkey.
7. Homestead creates the final WearNTear pieces and removes the build chest.

Default confirm hotkey:

```text
Alt + E
```

The blueprint chest only accepts required materials up to the amount still needed. If AzuCraftyBoxes is installed and enabled, Homestead can pull missing materials from nearby containers on confirm, or on open and confirm depending on config.

Terrain support is optional. `Terrain Support = Off` places only structures. `On` restores saved terrain support contacts for everyone. `AdminDebug` restores terrain support only for admin/debug/no-cost placement.

## Blueprint Chest Types

Homestead uses several temporary chest-like pieces:

- Blueprint build chest: holds materials for a placed blueprint plan.
- Store price chest: used to create a store listing price.
- Store purchase chest: holds payment materials before buying a store blueprint.
- Store payout chest: spawned when withdrawing store earnings.

Cleanup behavior:

- Empty chests can time out after `Blueprint Chest Timeout Minutes`.
- Chests with visible items, absorbed materials, price data, purchase deposits, or payout contents are kept.
- `Blueprint Chest Map Icon Size` controls map icons for your active Homestead blueprint/store chests.
- `Max Active Blueprint Chests Per SteamID` limits spam. If Steam/platform identity cannot be resolved, Homestead falls back to Valheim player ID.

## Blueprint Store

The Blueprint Store lets players trade Homestead blueprints through server-backed listings.

Store supports:

- listing a local blueprint
- editing listing price
- hiding listings locally
- delisting your own listings
- previewing before purchase
- buying for item/material prices
- making offers
- sellers accepting/declining/deleting offers
- buyers deleting their own offers
- accepted offers as alternate purchase prices
- purchase notifications and offer notifications
- withdrawing coins/materials through payout chests

### Listing a blueprint

From the Homestead hammer tab:

1. Hover a saved blueprint.
2. Use the configured `Blueprint Store List Modifier Key` plus click.
3. Place the store price chest/preview.
4. Enter up to 8 price item rows in the price editor.
5. List the blueprint.

The server stores the listing in `ServerBlueprints/Store/catalog.yml` and keeps the listing blueprint body as a store `.hsbp.yml` file.

### Buying a blueprint

1. Open Blueprint Store from the Homestead tab.
2. Select a listing.
3. Click Buy to enter preview/purchase placement.
4. Place the purchase chest.
5. Deposit the listed price materials.
6. Confirm the purchase chest.

After purchase, the buyer receives the blueprint payload and the seller gets a store balance. The seller can withdraw the balance as payout chests.

### Offers

Players can make material offers on listings. The seller can accept or decline. Accepted offers remain visible as alternate prices unless removed by store actions/config policy.

### Notifications

`Blueprint Store Notification Mode` controls notification display:

- `Off`: hide the notification button and disable fallback polling.
- `BadgeOnly`: show the notification button and unread count, but do not open the panel automatically.
- `AutoOpenPanel`: open the notification panel when a new unread notification arrives.

`Blueprint Store Anonymous Notifications` can hide buyer/seller/offer actor names in notification messages.

## Build Camera

Homestead includes BuildCameraCHE-style build camera controls.

Use it when:

- building large structures
- placing high or awkward pieces
- working from a fixed player position while the camera moves

Main behavior:

- toggle while a build tool is equipped
- move the camera away from the avatar up to a configured range
- optionally scale range by comfort level
- optionally restrict entry or pickup by coziness/comfort
- pick up dropped resources from a configurable range
- optional demister and Dvergr circlet light follow
- look-at lock hotkey while active

If the standalone BuildCameraCHE mod is installed, disable Homestead's build camera to avoid overlap.

## Placement Controls

Homestead adds construction helpers for ordinary hammer pieces and Homestead tools:

- Grid snap toggle, default `G`.
- Grid size rounded to 0.05m steps.
- Position adjust for hammer pieces, Homestead blueprints, Area Save, and Area Dismantle.
- PgUp/PgDn vertical adjustment and arrow-key horizontal adjustment while holding the configured modifier.
- Shared rotation step for area tools, blueprint yaw, and placement rotation controls.
- Optional X/Z axis rotation offsets for ordinary hammer build pieces.
- Bottom key hints are extended to show Homestead controls.

## Dvergr Circlet Extension

Homestead can extend the Dvergr circlet with:

- per-item light on/off state
- adjustable brightness and range
- usage-based durability drain only while the light is on
- custom repair station config
- tooltip hints for hotkeys and current settings
- remote visual/light sync so other clients can see custom-slot circlet visuals

Default controls:

- `L`: toggle light
- modifier + Up/Down: intensity
- modifier + Left/Right: range

If Circlet Extended is installed, Homestead leaves circlet handling to that mod.

## Command

Homestead is mostly UI-driven. It currently adds one admin maintenance command.

### `hs_clearchests`

Deletes Homestead blueprint-related chest ZDOs from the world.

Targets:

- blueprint build chest
- store price chest
- store purchase chest
- store payout chest

Syntax:

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

## Typical Workflows

### Save and build your own blueprint

```text
Hammer -> Homestead -> Area Save
Select structure -> click preview -> save name
Hammer -> Homestead -> your blueprint
Place blueprint chest -> add materials -> Alt+E confirm
```

### Sell a blueprint

```text
Hammer -> Homestead
Hover saved blueprint -> list modifier + click
Place store price chest -> enter price -> list
```

### Buy a blueprint

```text
Hammer -> Homestead -> Blueprint Store
Select listing -> Buy
Place purchase chest -> deposit price -> confirm
```

### Clean abandoned temporary blueprint chests

```text
hs_clearchests dry
hs_clearchests
```

## Safety And Performance Notes

- Large blueprints are bounded by server upload size and entry-count configs.
- Store listing pages use paged summaries and icon caching to reduce UI hitching.
- Blueprint payloads are compressed for network transfer.
- Temporary store/plan data is cleaned when related chests are destroyed, confirmed, or swept as orphaned drafts.
- Blueprint/store chests are protected from Area Dismantle.
- Area Dismantle skips containers/item stands/armor stands that still contain items or attachments.

## Compatibility With ZoneSavior

Homestead and ZoneSavior are separate mods.

Install Homestead for construction features. Install ZoneSavior for server zone maintenance.

When both are present, Homestead-created blueprint pieces are marked so ZoneSavior's WearNTear counter can count them consistently, including safety cases where creator data is missing.

## Git
Build camera code from <br>
https://github.com/AzumattDev/BuildCameraCustomHammersEdition <br>

The mods code <br>
https://github.com/sighsorry1029/Homestead