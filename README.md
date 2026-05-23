# Homestead + ZoneSavior

This repository builds two Valheim BepInEx mods:

- **Homestead**: player-facing construction tools, native blueprints, Blueprint Store, Area Save/Dismantle, build camera controls, placement helpers, and Dvergr circlet QoL.
- **ZoneSavior**: dedicated-server zone maintenance, inactive-player zone archiving, zone bundle save/load/restore, zone UI, and per-zone WearNTear limits.

The split keeps player construction features and server zone maintenance deployable as separate DLLs.

## Projects

| Project | DLL | Purpose |
| --- | --- | --- |
| `Homestead.csproj` | `Homestead.dll` | Construction, blueprints, Blueprint Store, build camera, placement controls, Dvergr circlet QoL. |
| `ZoneSavior.csproj` | `ZoneSavior.dll` | Zone bundles, inactive-player archive scans, zone reset/restore workflows, zone UI, zone WearNTear limits. |

## Build

```powershell
dotnet msbuild .\Homestead.sln /t:Rebuild /p:Configuration=Debug /p:DebugType=portable /p:JotunnPath="C:\Users\blizz\AppData\Roaming\com.kesomannen.gale\cache\ValheimModding-Jotunn\2.29.0\BepInEx\plugins\ValheimModding-Jotunn\Jotunn.dll" /v:minimal
```

Debug outputs:

```text
bin/Debug/Homestead.dll
bin/ZoneSavior/Debug/ZoneSavior.dll
```

## Thunderstore Metadata

Thunderstore metadata is split by mod:

```text
Thunderstore/Homestead/
  manifest.json
  README.md
  CHANGELOG.md
  icon.png

Thunderstore/ZoneSavior/
  manifest.json
  README.md
  CHANGELOG.md
  icon.png
```

## Documentation

- [Homestead Commands](docs/HomesteadCommands.md)
- [ZoneSavior Commands](docs/ZoneSaviorCommands.md)
- [Combined Command Index](docs/Commands.md)

## Runtime Data

Homestead stores player-facing blueprint data under:

```text
BepInEx/config/Homestead/
  Blueprints/
  ServerBlueprints/
    PlanGhosts/
    Store/
```

ZoneSavior stores server zone data under:

```text
BepInEx/config/ZoneSavior/
  ZoneBundles/
  Diagnostics/
  activity.yml
  zones.yml
```
