# Homestead Dependency And Runtime Notes

Last checked: 2026-05-25

## Runtime Shape

This checkout builds Homestead only. Homestead owns player-facing construction workflows: native WearNTear blueprints, Blueprint Store, Area Save/Dismantle, build camera controls, placement helpers, and Dvergr circlet QoL.

ZoneSavior-related references in this repo are compatibility hooks or historical companion documentation. `ZoneSavior.csproj` and ZoneSavior runtime code are not part of the current workspace.

Homestead writes blueprint data under:

```text
BepInEx/config/Homestead/Blueprints/
BepInEx/config/Homestead/ServerBlueprints/PlanGhosts/
BepInEx/config/Homestead/ServerBlueprints/Store/
```

Blueprint names are shared within the active BepInEx profile. Homestead does not currently create per-world blueprint subfolders.

## Client Behavior

Homestead client-side behavior includes:

- native WearNTear blueprints through the Jotunn-backed hammer `Homestead` tab
- Area Save selection and preview
- Area Dismantle selection and server-routed dismantle execution
- blueprint placement through temporary plan chests
- Blueprint Store listing, buying, offers, notifications, and payout chests
- build camera controls
- placement adjustment and grid snap
- Dvergr circlet light controls

The active Homestead console command surface is documented in [HomesteadCommands.md](HomesteadCommands.md).

## Hard Dependencies

Homestead has a hard runtime dependency on Jotunn. Jotunn owns the custom hammer `Homestead` tab/category plumbing and is used to render generated snapshot icons for saved native blueprints.

`ServerSync.dll` and `YamlDotNet.dll` are bundled into `Homestead.dll` by ILRepack, so they are not Thunderstore runtime dependencies.

## Optional Compatibility

| Mod | Role |
| --- | --- |
| ContentsWithin | Homestead can provide virtual requirement previews for blueprint/store chests. |
| AzuCraftyBoxes | Homestead can pull missing blueprint/store materials from nearby containers and protects Homestead chests from being used as source containers. |
| AzuExtendedPlayerInventory | Homestead can find custom equipment visuals for Dvergr circlet support. |
| InventorySlots | Homestead can find custom equipped items and custom equipment visuals for Dvergr circlet support. |
| VeiledRecipes | Homestead can register virtual hammer pieces as known recipe overrides. |
| ZoneSavior | Homestead can ask a present companion ZoneSavior assembly to rebuild zone WearNTear counts. ZoneSavior itself is not built from this checkout. |

## Thunderstore Manifest

Thunderstore dependencies use the `{team}-{package}-{version}` format documented by Thunderstore. The current Homestead manifest lists:

```json
[
  "denikson-BepInExPack_Valheim-5.4.2202",
  "ValheimModding-Jotunn-2.29.0"
]
```

## Referenced Mod Summary

Jotunn is a Valheim modding library that provides managers and helpers for custom pieces, piece-table categories, prefabs, localization, assets, GUI hooks, and snapshot rendering. Homestead uses it directly for the hammer blueprint UI instead of manually expanding the vanilla build menu.

## Sources Checked

- Local `Homestead.csproj`, `Homestead.sln`, and compiled C# sources in this checkout.
- Thunderstore dependency format: https://wiki.thunderstore.io/mods/creating-a-package
- Jotunn: https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/
