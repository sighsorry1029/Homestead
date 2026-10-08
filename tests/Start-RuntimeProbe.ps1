param(
    [ValidateSet('client', 'server')][string]$Role = 'server',
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ClientInstall = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim',
    [string]$ServerInstall = 'D:/SteamLibrary/steamapps/common/Valheim dedicated server',
    [switch]$UiOnly,
    [switch]$IconOnly,
    [switch]$CameraTooltipOnly,
    [switch]$PlacementOnly,
    [switch]$GridOnly,
    [switch]$RotationOnly,
    [switch]$InfinityHammerOnly,
    [string]$InfinityHammerAssemblyPath,
    [switch]$ZenRedecorateOnly,
    [string]$ZenRedecorateAssemblyPath,
    [string]$GizmoAssemblyPath,
    [string]$PlantEasilyAssemblyPath,
    [string]$AssemblyPath,
    [int]$Width = 1280,
    [int]$Height = 720
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path $project "artifacts/runtime-$Role"
$loadInfinityHammer = $InfinityHammerOnly -or ($ZenRedecorateOnly -and $InfinityHammerAssemblyPath)
if ($loadInfinityHammer) {
    if ($Role -ne 'client' -or $RotationOnly -or $PlacementOnly -or $GridOnly -or $CameraTooltipOnly -or $UiOnly -or $IconOnly -or ($InfinityHammerOnly -and $ZenRedecorateOnly)) { throw 'Infinity Hammer probe requires a separate graphical client.' }
    if (-not (Test-Path -LiteralPath $InfinityHammerAssemblyPath -PathType Leaf)) { throw 'Supply the Infinity Hammer DLL.' }
    $root = Join-Path $project 'artifacts/runtime-infinityhammer-client'
    $pluginRoot = Split-Path -Parent (Split-Path -Parent $InfinityHammerAssemblyPath)
    $infinityDependencies = @(
        (Join-Path $pluginRoot 'JereKuusela-Server_devcommands/ServerDevcommands.dll'),
        (Join-Path $pluginRoot 'JereKuusela-World_Edit_Commands/WorldEditCommands.dll')
    )
    foreach ($dependency in $infinityDependencies) { if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "Missing dependency: $dependency" } }
}
if ($ZenRedecorateOnly) {
    if ($Role -ne 'client' -or $InfinityHammerOnly -or $RotationOnly -or $PlacementOnly -or $GridOnly -or $CameraTooltipOnly -or $UiOnly -or $IconOnly) { throw 'ZenRedecorate probe requires a separate graphical client.' }
    if (-not (Test-Path -LiteralPath $ZenRedecorateAssemblyPath -PathType Leaf)) { throw 'Supply the ZenRedecorate DLL.' }
    $suffix = if ($loadInfinityHammer) { 'zen-infinityhammer-client' } else { 'zen-client' }
    $root = Join-Path $project "artifacts/runtime-$suffix"
    $zenPluginRoot = Split-Path -Parent (Split-Path -Parent $ZenRedecorateAssemblyPath)
    $zenDependencies = @(
        (Join-Path $zenPluginRoot 'ZenDragon-Zen_ModLib/Zen.ModLib.dll'),
        (Join-Path $zenPluginRoot 'ValheimModding-Jotunn/Jotunn.dll')
    )
    foreach ($dependency in $zenDependencies) { if (-not (Test-Path -LiteralPath $dependency -PathType Leaf)) { throw "Missing dependency: $dependency" } }
}
if ($RotationOnly) {
    if ($Role -ne 'client' -or $PlacementOnly -or $GridOnly) { throw 'The rotation probe requires a separate graphical client.' }
    $suffix = if ($GizmoAssemblyPath) { 'rotation-gizmo-client' } else { 'rotation-client' }
    $root = Join-Path $project "artifacts/runtime-$suffix"
    if ($GizmoAssemblyPath -and -not (Test-Path -LiteralPath $GizmoAssemblyPath -PathType Leaf)) { throw 'Gizmo DLL not found.' }
}
if ($GridOnly) {
    if ($Role -ne 'client' -or $PlacementOnly) { throw 'The standalone grid probe requires a client without PlacementOnly.' }
    $root = Join-Path $project 'artifacts/runtime-grid-client'
    if (Test-Path "$root/BepInEx/plugins/Advize_PlantEasily.dll") { throw 'The standalone grid fixture must not contain PlantEasily.' }
}
if ($PlacementOnly) {
    if ($Role -ne 'client') { throw 'The placement probe requires a graphical client.' }
    if (-not (Test-Path -LiteralPath $PlantEasilyAssemblyPath -PathType Leaf)) { throw 'Supply the PlantEasily DLL for the placement probe.' }
    $root = Join-Path $project 'artifacts/runtime-placement-client'
}
$install = if ($Role -eq 'server') { $ServerInstall } else { $ClientInstall }
$exe = if ($Role -eq 'server') { 'valheim_server.exe' } else { 'valheim.exe' }
$data = if ($Role -eq 'server') { 'valheim_server_Data' } else { 'valheim_Data' }
if (Get-Process -ErrorAction SilentlyContinue | Where-Object Path -eq (Join-Path $root $exe)) { throw 'The isolated probe is already running.' }
New-Item -ItemType Directory -Force -Path "$root/BepInEx/plugins", "$root/BepInEx/core", "$root/BepInEx/config", "$root/saves" | Out-Null
# Assets are read from the installed game. Executable, loader, plugins, logs and all
# saves/configuration are isolated. Never recursively remove the junction targets.
foreach ($directory in @($data, 'MonoBleedingEdge')) {
    if (-not (Test-Path "$root/$directory")) { New-Item -ItemType Junction -Path "$root/$directory" -Target "$install/$directory" | Out-Null }
}
Get-ChildItem -LiteralPath $install -File | Where-Object { $_.Extension -in '.dll', '.exe' -or $_.Name -eq 'steam_appid.txt' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $root -Force }
# The dedicated executable expects the game's App ID, not the server tool's
# 896660. Correct only the isolated fixture; never modify the installation.
if ($Role -eq 'server') { Set-Content -LiteralPath "$root/steam_appid.txt" -Value '892970' -Encoding ascii }
Copy-Item -LiteralPath "$ClientInstall/winhttp.dll", "$ClientInstall/doorstop_config.ini" -Destination $root -Force
Get-ChildItem -LiteralPath "$ClientInstall/BepInEx/core" -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination "$root/BepInEx/core" -Force }
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) { $AssemblyPath = "$project/bin/$Configuration/Homestead.dll" }
Copy-Item -LiteralPath $AssemblyPath -Destination "$root/BepInEx/plugins/Homestead.dll" -Force
Copy-Item -LiteralPath "$PSScriptRoot/RuntimeProbe/bin/Debug/net48/RuntimeProbe.dll" -Destination "$root/BepInEx/plugins" -Force
if ($PlacementOnly) { Copy-Item -LiteralPath $PlantEasilyAssemblyPath -Destination "$root/BepInEx/plugins/Advize_PlantEasily.dll" -Force }
if ($RotationOnly -and $GizmoAssemblyPath) { Copy-Item -LiteralPath $GizmoAssemblyPath -Destination "$root/BepInEx/plugins/ComfyGizmo.dll" -Force }
if ($loadInfinityHammer) {
    Copy-Item -LiteralPath $InfinityHammerAssemblyPath -Destination "$root/BepInEx/plugins/InfinityHammer.dll" -Force
    foreach ($dependency in $infinityDependencies) { Copy-Item -LiteralPath $dependency -Destination "$root/BepInEx/plugins" -Force }
}
if ($ZenRedecorateOnly) {
    Copy-Item -LiteralPath $ZenRedecorateAssemblyPath -Destination "$root/BepInEx/plugins/ZenRedecorate.dll" -Force
    foreach ($dependency in $zenDependencies) { Copy-Item -LiteralPath $dependency -Destination "$root/BepInEx/plugins" -Force }
}
Copy-Item -LiteralPath "$project/samples/sample_001.blueprint" -Destination "$root/probe.blueprint" -Force
Set-Content -LiteralPath "$root/homestead-probe.enabled" -Value 'Isolated test only'
$arguments = @('-batchmode', '-savedir', "`"$root/saves`"", '-logFile', "`"$root/unity.log`"")
if ($UiOnly) { $arguments += '-homestead-ui-probe' }
if ($IconOnly) { $arguments += '-homestead-icon-probe' }
if ($CameraTooltipOnly) { $arguments += '-homestead-camera-tooltip-probe' }
if ($PlacementOnly) { $arguments += '-homestead-placement-probe' }
if ($GridOnly) { $arguments += '-homestead-grid-probe' }
if ($RotationOnly) { $arguments += '-homestead-rotation-probe' }
if ($InfinityHammerOnly) { $arguments += '-homestead-infinityhammer-probe' }
if ($ZenRedecorateOnly) { $arguments += '-homestead-zen-probe' }
if ($Role -eq 'server') {
    $arguments += @('-nographics', '-name', 'HomesteadCompatibility', '-port', '2499', '-world', 'HomesteadCompatibility', '-password', 'codex-test-only', '-public', '0')
} else {
    $arguments += @('-screen-fullscreen', '0', '-screen-width', $Width, '-screen-height', $Height)
}
$process = Start-Process -FilePath "$root/$exe" -WorkingDirectory $root -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath "$root/process-id.txt"
[pscustomobject]@{ Pid = $process.Id; Root = $root; Report = "$root/probe.txt" }
