param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$AssemblyPath
)

# Run after building Homestead. These checks use temporary files and do not start
# Unity, initialize the plugin, deploy the DLL, or open a player's saved data.
# Use pwsh (PowerShell 7); current Unity metadata requires netstandard 2.1.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AssemblyPath)) {
    $AssemblyPath = Join-Path $projectRoot "bin\$Configuration\Homestead.dll"
}
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$staticFlags = [Reflection.BindingFlags]'Static, Public, NonPublic'
$repository = $assembly.GetType('Homestead.ZoneBlueprintStoreDraftRepository', $true)
$format = $assembly.GetType('Homestead.ZoneBlueprintFileFormat', $true)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

# The old implementation truncated the GUID for long names and returned the
# same ID for multiple listings created in the same second.
$createId = $repository.GetMethod('CreateListingId', $staticFlags)
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$names = @('', 'small', ('a' * 16), ('a' * 17), ('a' * 47), ('a' * 48),
    ('a' * 49), ('a' * 64), ('a' * 128), ('a' * 48 + 'different'),
    ('한글' * 32), '///___...')
foreach ($name in $names) {
    for ($i = 0; $i -lt 40; $i++) {
        $id = [string]$createId.Invoke($null, @($name))
        Assert-True ($id.Length -le 64) 'Listing ID exceeds its existing 64-character limit.'
        Assert-True ($id -cmatch '^\d{14}_.*_[0-9a-f]{32}$') "Listing ID lost its full GUID: $id"
        Assert-True ($ids.Add($id)) "Listing ID collision: $id"
    }
}
Write-Output "PASS: $($ids.Count) listing IDs, including long and identical-prefix names"

# Keep the existing deep-copy guard executable without plugin startup.
$cloneProbe = $repository.GetMethod('ValidateCatalogCloneMapping', $staticFlags)
if ($cloneProbe) {
    $null = $cloneProbe.Invoke($null, @())
    Write-Output 'PASS: catalog clone mapping and mutable collection independence'
} else {
    Assert-True ($Configuration -eq 'Release') 'Debug catalog clone probe is missing.'
    Write-Output 'SKIP: catalog clone probe is compiled only in Debug'
}

$readFile = $format.GetMethod('ReadFile', $staticFlags)
$serialize = $format.GetMethod('Serialize', $staticFlags)
$deserialize = $format.GetMethod('Deserialize', $staticFlags)
$expectedSamples = @{
    'sample_001.blueprint' = @(63, 0)
    'sample_002.blueprint' = @(132, 0)
    'sample_003.blueprint' = @(236, 0)
    'sample_snap.blueprint' = @(4, 9)
}
foreach ($name in $expectedSamples.Keys) {
    [string]$samplePath = Join-Path $projectRoot "samples\$name"
    $sample = $readFile.Invoke($null, @($samplePath))
    Assert-True ($sample.Entries.Count -eq $expectedSamples[$name][0]) "Sample piece count changed: $name"
    Assert-True ($sample.SnapPoints.Count -eq $expectedSamples[$name][1]) "Sample snap count changed: $name"
    $text = $serialize.Invoke($null, @($sample))
    $roundTrip = $deserialize.Invoke($null, @($text, $sample.Name))
    Assert-True ($text -ceq $serialize.Invoke($null, @($roundTrip))) "Blueprint round trip changed: $name"
}
Write-Output 'PASS: all four sample blueprints and serialized round trips'

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ('HomesteadRegression-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($tempDirectory)
try {
    $blueprintType = $assembly.GetType('Homestead.ZoneBlueprintFile', $true)
    $blueprint = [Activator]::CreateInstance($blueprintType, $true)
    $blueprint.Name = 'original'
    $write = $format.GetMethod('WriteFile', $staticFlags, $null, @([string], $blueprintType), $null)
    $writeNew = $format.GetMethod('WriteNewFile', $staticFlags)
    Assert-True ($null -ne $writeNew) 'Create-only blueprint writer is missing.'
    [string]$path = Join-Path $tempDirectory 'draft.blueprint'
    $null = $writeNew.Invoke($null, @($path, $blueprint))
    $original = [IO.File]::ReadAllText($path)

    $blueprint.Name = 'replacement'
    $collisionRejected = $false
    try { $null = $writeNew.Invoke($null, @($path, $blueprint)) }
    catch {
        if ($_.Exception.GetBaseException() -isnot [IO.IOException]) { throw }
        $collisionRejected = $true
    }
    Assert-True $collisionRejected 'A new draft overwrote an existing file.'
    Assert-True ($original -ceq [IO.File]::ReadAllText($path)) 'A rejected draft changed the original file.'

    # Normal saves must retain their existing overwrite behavior.
    $null = $write.Invoke($null, @($path, $blueprint))
    Assert-True ($readFile.Invoke($null, @($path)).Name -ceq 'replacement') 'Normal blueprint replacement failed.'
    $replacement = [IO.File]::ReadAllText($path)

    # Inject an actual Windows filesystem failure during the replacement.
    $lock = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    $writeRejected = $false
    try {
        $blueprint.Name = 'must not replace locked file'
        try { $null = $write.Invoke($null, @($path, $blueprint)) }
        catch {
            if ($_.Exception.GetBaseException() -isnot [IO.IOException]) { throw }
            $writeRejected = $true
        }
    } finally { $lock.Dispose() }
    Assert-True $writeRejected 'Expected the locked destination to reject replacement.'
    Assert-True ($replacement -ceq [IO.File]::ReadAllText($path)) 'Failed replacement changed the original file.'
    Assert-True (@(Get-ChildItem -LiteralPath $tempDirectory -Filter '*.tmp').Count -eq 0) 'Temporary blueprint writes leaked.'
    Write-Output 'PASS: create-only collision protection, ordinary replacement, and failed-write preservation'
} finally {
    # Only this newly created directory's files are removed; no recursive cleanup.
    Get-ChildItem -LiteralPath $tempDirectory -File | Remove-Item
    [IO.Directory]::Delete($tempDirectory)
}

$resources = $assembly.GetManifestResourceNames()
foreach ($name in $expectedSamples.Keys) {
    Assert-True ($resources -ccontains "Homestead.Samples.$name") "Embedded sample missing: $name"
}
foreach ($language in @('English', 'Korean')) {
    Assert-True ($resources -ccontains "Homestead.translations.$language.yml") "Embedded translation missing: $language"
}
Write-Output 'PASS: embedded blueprint and localization resources'

# Exercise the actual circlet update prefix with controlled Unity/input doubles.
# Only nullable/out-variable syntax is lowered for Windows PowerShell's compiler;
# production branch order, cleanup, synchronization and UI checks stay intact.
$circletSource = Get-Content -Raw -LiteralPath (Join-Path $projectRoot 'ZoneDvergrCirclet.cs')
$updateMatches = [regex]::Matches($circletSource,
    '(?s)internal static void Update\(\)\s*\{.*?if \(ShouldBlockInput\(\)\)\s*\{\s*return;\s*\}')
Assert-True ($updateMatches.Count -eq 1) 'Circlet update must defer its input check until after the existing guards.'
$updatePrefix = $updateMatches[0].Value.Replace('ItemDrop.ItemData?', 'ItemDrop.ItemData')
$updatePrefix = $updatePrefix.Replace('out ItemDrop.ItemData equipped', 'out equipped').Replace('item!;', 'item;')
$updatePrefix = $updatePrefix.Replace('bool active = Active;', 'ItemDrop.ItemData equipped; bool active = Active;')
Assert-True ([regex]::Matches($updatePrefix, 'ShouldBlockInput\(').Count -eq 1) 'Circlet input must not be queried early or more than once.'
$probeSource = @"
using System;
using System.Collections.Generic;
public static class HomesteadCircletInputProbe
{
    public static readonly List<string> Calls = new List<string>();
    public static bool Active, HasItem, ValidItem, BlockInput;
    public sealed class Player
    {
        public static Player m_localPlayer;
        public bool Dead;
        public bool IsDead() { return Dead; }
        public static implicit operator bool(Player player) { return player != null; }
    }
    private static class ItemDrop { internal sealed class ItemData { } }
    private sealed class CircletState { }
    private static bool TryGetEquippedDvergrCirclet(Player player, out ItemDrop.ItemData item)
    {
        item = HasItem ? new ItemDrop.ItemData() : null;
        return HasItem;
    }
    private static bool PatchItemData(ItemDrop.ItemData item, bool initializeDurability)
    { return item != null && ValidItem; }
    private static void CleanupFallbackVisuals() { Calls.Add("cleanup"); }
    private static CircletState LoadState(ItemDrop.ItemData item)
    { Calls.Add("load"); return new CircletState(); }
    private static void EnsureLocalCircletVisual(Player player, ItemDrop.ItemData item, CircletState state)
    { Calls.Add("visual"); }
    private static void PublishLocalCircletState(Player player, ItemDrop.ItemData item, CircletState state)
    { Calls.Add(item == null ? "clear" : "publish"); }
    private static bool ShouldBlockInput() { Calls.Add("ui"); return BlockInput; }
    $updatePrefix
        Calls.Add("hotkeys");
    }
    public static string Run(bool active, bool playerPresent, bool dead, bool hasItem, bool validItem, bool blocked)
    {
        Calls.Clear(); Active = active; HasItem = hasItem; ValidItem = validItem; BlockInput = blocked;
        Player.m_localPlayer = playerPresent ? new Player { Dead = dead } : null;
        Update();
        return string.Join(",", Calls.ToArray());
    }
}
"@
Add-Type -TypeDefinition $probeSource
$circletCases = @(
    @{ Name = 'missing player / dedicated'; Args = @($true, $false, $false, $true, $true, $false); Expected = 'cleanup' },
    @{ Name = 'inactive'; Args = @($false, $true, $false, $true, $true, $false); Expected = 'clear,cleanup' },
    @{ Name = 'dead'; Args = @($true, $true, $true, $true, $true, $false); Expected = 'clear,cleanup' },
    @{ Name = 'unequipped'; Args = @($true, $true, $false, $false, $true, $false); Expected = 'clear,cleanup' },
    @{ Name = 'invalid item'; Args = @($true, $true, $false, $true, $false, $false); Expected = 'clear,cleanup' },
    @{ Name = 'UI open'; Args = @($true, $true, $false, $true, $true, $true); Expected = 'load,visual,publish,ui' },
    @{ Name = 'gameplay'; Args = @($true, $true, $false, $true, $true, $false); Expected = 'load,visual,publish,ui,hotkeys' }
)
foreach ($case in $circletCases) {
    $a = $case.Args
    $actual = [HomesteadCircletInputProbe]::Run($a[0], $a[1], $a[2], $a[3], $a[4], $a[5])
    Assert-True ($actual -ceq $case.Expected) "Circlet guard/cleanup/sync order changed for $($case.Name): $actual"
}
Write-Output 'PASS: seven circlet input contexts, no premature UI access, and preserved cleanup/visual/sync order'
Write-Output 'Regression checks passed. Unity and multiplayer behavior require in-game validation.'
