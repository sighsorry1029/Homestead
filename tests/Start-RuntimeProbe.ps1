param(
    [ValidateSet('client', 'server')][string]$Role = 'server',
    [string]$ClientInstall = 'C:/Program Files (x86)/Steam/steamapps/common/Valheim',
    [string]$ServerInstall = 'D:/SteamLibrary/steamapps/common/Valheim dedicated server',
    [switch]$UiOnly,
    [int]$Width = 1280,
    [int]$Height = 720
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$root = Join-Path $project "artifacts/runtime-$Role"
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
Copy-Item -LiteralPath "$ClientInstall/winhttp.dll", "$ClientInstall/doorstop_config.ini" -Destination $root -Force
Get-ChildItem -LiteralPath "$ClientInstall/BepInEx/core" -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination "$root/BepInEx/core" -Force }
Copy-Item -LiteralPath "$project/bin/Debug/Homestead.dll", "$PSScriptRoot/RuntimeProbe/bin/Debug/net48/RuntimeProbe.dll" -Destination "$root/BepInEx/plugins" -Force
Copy-Item -LiteralPath "$project/samples/sample_001.blueprint" -Destination "$root/probe.blueprint" -Force
Set-Content -LiteralPath "$root/homestead-probe.enabled" -Value 'Isolated test only'
$arguments = @('-batchmode', '-savedir', "`"$root/saves`"", '-logFile', "`"$root/unity.log`"")
if ($UiOnly) { $arguments += '-homestead-ui-probe' }
if ($Role -eq 'server') {
    $arguments += @('-nographics', '-name', 'HomesteadCompatibility', '-port', '2499', '-world', 'HomesteadCompatibility', '-password', 'codex-test-only', '-public', '0')
} else {
    $arguments += @('-screen-fullscreen', '0', '-screen-width', $Width, '-screen-height', $Height)
}
$process = Start-Process -FilePath "$root/$exe" -WorkingDirectory $root -ArgumentList $arguments -WindowStyle Hidden -PassThru
$process.Id | Set-Content -LiteralPath "$root/process-id.txt"
[pscustomobject]@{ Pid = $process.Id; Root = $root; Report = "$root/probe.txt" }
