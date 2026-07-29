# Changelog

## 1.2.2

- Hardened Blueprint Store purchase, price, payout, and blueprint plan chest placement by repairing missing current-scene prefab registrations, validating the spawned network view and ZDO, and rolling back invalid spawns instead of reporting false success.
- Fixed `hs_clearchests dry` and cleanup requests from dedicated-server administrators being rejected by an unreliable client-side admin check; the server remains authoritative for command permission.

## 1.2.1

- Reduced AzuCraftyBoxes compatibility overhead by resolving the installed plugin once, removing repeated assembly searches when absent, and avoiding a helper-array allocation in protected-container checks.
- Prevented blueprint directory watcher failures from repeatedly rescanning every blueprint; recovery now performs one full rescan after a successful reconnect, while idle watcher updates no longer allocate change lists or enter a lock.
- Limited blueprint chest map discovery to once per world and incrementally tracked newly created chests and owner changes for later map refreshes.

## 1.2.0

- Added snap-aware X/Z placement rotation that preserves native piece snapping, while deferring ordinary-piece rotation step, random rotation correction, and X/Z rotation to ComfyGizmo when it is loaded.
- Moved the Area Save/Dismantle uniform-scale modifier into Area Tools; placement nudging now uses arrows and PgUp/PgDn directly, and Position Adjust is client-only.
- Simplified Area Save/Dismantle piece descriptions and aligned displayed mouse button numbers with Valheim's one-based labels.
- Improved key hints with the active Grid Size, clearer Build Camera comfort requirements, and one-based Blueprint Store back-button labels.
- Prevented Blueprint Store preview placement input while menus or text fields are active.

## 1.1.10

- Made blueprint plan confirmation safer by validating transform and placement bounds, rolling back newly created pieces and Homestead terrain changes when possible, and reporting incomplete rollback instead of silently leaving partial work.
- Hardened Blueprint Store purchases by targeting the exact nearby purchase chest, rejecting changed prices, refunding deposits from dismantled unconfirmed purchase chests, and retrying completed blueprint saves during the current world session.
- Strengthened validated atomic blueprint and Store catalog writes, immediate persistence for important listing and offer changes, and recovery handling for failed purchase and withdrawal transactions.
- Prevented stale Store list, offer, preview, and plan-preview responses from replacing newer state; added request timeouts, bounded preview caches, and full-rescan recovery after blueprint directory watcher failures.
- Simplified duplicated Store, area-tool, blueprint-save, and Build Camera session state to reduce stale state and maintenance risk.

## 1.1.9

- Fixed Build Camera piece placement and dismantling actions emitting player-centered gameplay noise that could alert nearby AI, remove Resting, and immediately exit Build Camera.

## 1.1.8

- Added InventorySlots container-preview compatibility for Homestead blueprint and plan chests, showing their virtual missing-material requirements instead of misleading chest contents.
- Prevented InventorySlots from previewing Blueprint Store price-setting chests.

## 1.1.7

- Hardened Blueprint Store catalog persistence with validated writes, automatic backup recovery, and protection against corrupt or incompatible catalog versions.
- Expired and delisted listings now clean up related offers and unreferenced blueprint files after the catalog is safely saved.
- Fixed Store panels, locked previews, chest registries, build camera state, and blueprint caches carrying stale state across world changes.
- Added bounds for Store request and cache data, corrected timestamp ordering across time zones, prevented stale list/icon responses, and protected material totals from integer overflow.
- Simplified duplicated blueprint, terrain, input, Store, and session-lifecycle code to reduce maintenance risk.

## 1.1.6

- Added compatibility with My Little UI so Homestead blueprint store and plan chest hover hints keep their custom actions after container hover UI mods rewrite chest hover text.

## 1.1.5

- Fixed area repair tooltip text leaking onto veiled build pieces or empty build-menu slots when compatibility mods update the build HUD.

## 1.1.4

- Dvergr circlets now repair after other worn equipment instead of taking repair priority while equipped.
- Dvergr circlets are only considered repairable at 95% durability or lower, preventing tiny light-drain ticks from keeping the repair button focused on the circlet.

## 1.1.3

- Added area repair with base radius and comfort-scaled radius options.

## 1.1.2

- `Preview Ghost Color` now defaults to `#FFFFFF40` (RGBA 1, 1, 1, 0.25).
- Blueprint Store offer dialogs now prefill the seller's listed price.
- Build camera HUD distances now show one decimal place when needed.
- Large blueprint icons now keep extra front-visible layers so dense builds look less hollow.
- Homestead hammer tab now sorts saved blueprints by filename.
- Now the mod includes 3 blueprint samples.

## 1.1.1

![](https://i.ibb.co/S4V2z9pS/Screenshot-2026-05-31-160539.png)

- Blueprint, plan, station, and store preview ghosts now preserve source piece textures while applying the configured preview color.
- `Preview Ghost Color` now defaults to `#FFFFFFFF`, and store preview ghosts use the same preview color option as other blueprint ghosts.
- Added a safe text-input visibility fallback so Homestead no longer logs repeated errors when another mod patches `TextInput.IsVisible` before its UI is ready.

## 1.1.0

- Blueprint placement, preview, icon rendering, and store validation now ignore missing prefab entries and recipe-only prefabs without WearNTear, treating only Homestead-loadable WearNTear pieces as buildable entries.
- Lowered the default Preview Ghost Color alpha to 0.05 for subtler unfinished blueprint previews.

## 1.0.10

- Switched Homestead blueprints to the native `.blueprint` format with PlanBuild/Infinity Hammer-compatible metadata and simplified local blueprint handling. (No legacy support for hsbp.yml format. Regenerate your config and catalog.yml)
- Simplified Blueprint Store, build camera, area tool, Dvergr circlet, preview, and remote visual config options.
- Added comfort-scaled build camera distance, placement distance, and resource pickup range HUD support.
- Improved Blueprint Store upload/listing/purchase flows, icon limits, cleanup behavior, and RPC response handling.
- Refactored blueprint save/menu, hammer table, material escrow, price input, and store UI internals for lower duplication and easier maintenance.

## 1.0.9

- Improved Blueprint Store save safety, chest cleanup, RPC handling, and purchase completion VFX behavior.

## 1.0.8

- Added compat for VeildRecipes so that blueprint icon is not veiled.
- Minor config cleanup.

## 1.0.7

- Added compat to InventorySlots mod.
- Reduced minimum dgree of rotation to 0.5 from 1.
- Fixed initial rotation offset.

## 1.0.6

- Readme Fix.

## 1.0.5

- Removed ZoneSavior part.
- Added exclamation mark for the withdrawls.
- Added depth/width control with area save/dismantle.

## 1.0.4

- Changed default rotation step to 22.5%.
- Fixed blueprint store flickering for some clients.
- HUD now shows piece rotation too.

## 1.0.3

- Various optimizations and fix for dedi.
- Added localization file.

## 1.0.2

- Fixed VFX not showing up on dedi.

## 1.0.1

- Improved dedicated server compatibility for blueprint store, purchase, payout, and blueprint plan chest placement, including safer chest metadata setup and client-side placement VFX playback.
- Fixed blueprint store chest lookup edge cases, including purchase offer matching and registry fallback behavior, to avoid incorrect matches and large synchronous ZDO scans.
- Added PlayerId/SteamId-aware store identity handling for ownership, hidden listings, and debug diagnostics.
- Reduced server hitch risk in blueprint/store RPC paths by tightening payload handling, cooldowns, queue limits, and removing expensive fallback scans.
- Improved Homestead build-tool UX with throttled hammer/menu refreshes, better key hints, and reduced forced HUD rebuilds.
- Strengthened auto archive and zone bundle restore behavior, including safer eligibility diagnostics, creator handling, and terrain restore planning.

## 1.0.0

- Initial Release.
