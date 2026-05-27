# Homestead

![](https://i.ibb.co/C5vLNbRs/fullshot.png) <br>
Homestead is a Valheim building mod focused on saving, rebuilding, trading, and cleaning up player structures. It brings blueprint workflows into the hammer tab, with Area Save, Area Dismantle, a Blueprint Store, build camera controls, placement helpers, and Dvergr circlet quality-of-life features.

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

## What It Adds

- **Homestead hammer tab** for Area Save, Area Dismantle, Blueprint Store, and saved blueprints.
- **Native blueprints** that preserve build pieces, required materials, preview placement, and final confirmation.
- **Blueprint Store** for listing, buying, offering, notifications, and seller payouts.
- **Area Dismantle** for removing owned builds in a selected rectangle and returning materials.
- **Build camera** for easier large-scale building from a detached view.
- **Placement helpers** for grid snap, nudging, rotation step, and X/Z rotation offsets.
- **Dvergr circlet controls** for light toggle, intensity, range, durability drain, and synced visuals.

## Core Flow

### Save And Build

1. Equip the hammer.
2. Open the `Homestead` tab.
3. Select `Area Save`.
4. Mark a structure and save it as a blueprint.
5. Select the saved blueprint from the Homestead tab.
6. Place the blueprint chest.
7. Deposit the required materials.
8. Confirm the build.

Default confirm hotkey:

```text
Alt + E
```

### Sell A Blueprint

1. Open the `Homestead` hammer tab.
2. Hover a saved blueprint.
3. Use the Blueprint Store list modifier and click.
4. Place the price chest.
5. Set the price.
6. Confirm the listing.

### Buy A Blueprint

1. Open `Blueprint Store` from the Homestead tab.
2. Select a listing.
3. Preview or place a purchase chest.
4. Deposit the listed price materials.
5. Confirm the purchase.

The purchased blueprint is saved to the buyer's blueprint list.

## Area Tools

Area Save and Area Dismantle use the same rectangle controls:

- `Wheel`: rotate the area.
- `Alt + Wheel`: scale width and depth together.
- `Mouse3 + Wheel`: adjust depth.
- `Mouse4 + Wheel`: adjust width.
- Placement-adjust modifier + arrows/PgUp/PgDn: nudge the tool or preview.

Area Dismantle is intentionally conservative:

- only matching player-owned pieces are dismantled
- Homestead blueprint/store chests are protected
- containers, item stands, and armor stands with contents are skipped
- extra prefab blacklist entries can be configured

## Blueprint Building

Blueprint placement uses a temporary blueprint chest. The chest shows missing requirements, accepts only needed materials, and finalizes the build when everything is ready.

## Blueprint Store

The Blueprint Store lets players trade saved Homestead blueprints.

Store actions include:

- list a blueprint
- edit price
- preview before buying
- buy with materials
- make offers
- accept or decline offers
- hide listings locally
- withdraw seller earnings through payout chests

Notifications appear for store events such as new listings, offers, accepted offers, and purchases.

## Build Camera

Build camera helps with tall, wide, or awkward builds by letting the camera move away from the player while building.

It supports:

- configurable distance
- comfort-scaled distance
- pickup range
- look-at lock
- optional Dvergr circlet light follow

## Placement Helpers

Homestead adds small controls that make regular building less fussy:

- grid snap
- position nudging
- adjustable rotation step
- X/Z rotation offsets
- key hints for Homestead controls

## Dvergr Circlet

Homestead can extend the Dvergr circlet with per-item light controls:

- toggle light on/off
- adjust intensity
- adjust range
- drain durability while lit
- sync custom-slot visuals and light state for nearby players

## Github
Build camera code from https://github.com/AzumattDev/BuildCameraCustomHammersEdition <br>
https://github.com/sighsorry1029/Homestead <br>