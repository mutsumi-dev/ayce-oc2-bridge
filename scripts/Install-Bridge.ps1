param(
    [Parameter(Mandatory)][string]$GamePath,
    [ValidateSet('Observe', 'Demo', 'Custom', 'Disable')][string]$Mode = 'Observe',
    [string]$Overlay,
    [string]$BepInExZip,
    [string]$ReleaseDir = (Join-Path $PSScriptRoot '../release')
)

$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GamePath).Path
$gameExe = Join-Path $game 'Overcooked All You Can Eat.exe'
if (-not (Test-Path -LiteralPath $gameExe -PathType Leaf)) { throw 'GamePath is not the Steam AYCE game folder.' }
if (Get-Process -Name 'Overcooked All You Can Eat' -ErrorAction SilentlyContinue) { throw 'Close AYCE before changing the plugin.' }
$actualHash = (Get-FileHash -LiteralPath (Join-Path $game 'GameAssembly.dll')).Hash.ToLowerInvariant()
if ($actualHash -ne '494157d1daff4f8144acd0a4fd64add092281992abbcb0cdf5f462f0c747b42b') { throw 'This early build supports AYCE Steam Build 22894075 only.' }

$bep = Join-Path $game 'BepInEx'
$pluginDir = Join-Path $bep 'plugins/AyceOc2Bridge'
$config = Join-Path $bep 'config/dev.ayce.oc2bridge.cfg'
if ($Mode -eq 'Disable') {
    if (Test-Path -LiteralPath $config -PathType Leaf) {
        [IO.File]::WriteAllText($config, "[Bridge]`r`nApplyOverlay = false`r`n")
    }
    Write-Host 'Overlay disabled. The plugin will only observe the test scene if it is installed.'
    return
}

$dll = (Resolve-Path -LiteralPath (Join-Path $ReleaseDir 'AyceOc2Bridge.dll')).Path
$active = Join-Path $pluginDir 'active.json'
if ($Mode -eq 'Demo') { $Overlay = Join-Path $ReleaseDir 'smoke.json' }
if ($Mode -eq 'Custom' -and -not $Overlay) { throw 'Specify -Overlay with the converter output.' }
$data = $null
if ($Mode -ne 'Observe') {
    $data = Get-Content -Raw -LiteralPath $Overlay | ConvertFrom-Json
    if ($data.schemaVersion -ne 1 -or $data.targetBuild -ne '22894075' -or $data.targetScene -cnotin @('s_sushi_1_1', 's_city_h2') -or $data.targetGameSha256 -ne $actualHash) {
        throw 'The overlay target does not match this build and scene.'
    }
}

$core = Join-Path $bep 'core/BepInEx.Unity.IL2CPP.dll'
if (-not (Test-Path -LiteralPath $core -PathType Leaf)) {
    if ((Test-Path -LiteralPath $bep) -or (Test-Path -LiteralPath (Join-Path $game 'winhttp.dll'))) {
        throw 'An existing loader installation was found. Install or repair BepInEx 6 IL2CPP x64 independently before adding this plugin.'
    }
    if (-not $BepInExZip) { throw 'Supply -BepInExZip with the official BepInEx Unity IL2CPP x64 6.0.0-pre.2 ZIP.' }
    $zip = (Resolve-Path -LiteralPath $BepInExZip).Path
    if ((Get-FileHash -LiteralPath $zip).Hash -ne '616EC7EB06CF11B2A0000E8FCEF04D1B12BB58E84A2E0BDAC9523234FC193CEB') {
        throw 'BepInEx ZIP differs from the official 6.0.0-pre.2 package used by this build.'
    }
    Expand-Archive -LiteralPath $zip -DestinationPath $game
    if (-not (Test-Path -LiteralPath $core -PathType Leaf)) { throw 'BepInEx extraction did not create the IL2CPP core.' }
}

New-Item -ItemType Directory -Path $pluginDir, (Split-Path $config) -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination (Join-Path $pluginDir 'AyceOc2Bridge.dll') -Force
if ($data) {
    $selected = (Resolve-Path -LiteralPath $Overlay).Path
    if ($selected -ne $active) { Copy-Item -LiteralPath $selected -Destination $active -Force }
}
$value = $(if ($data) { 'true' } else { 'false' })
[IO.File]::WriteAllText($config, "[Bridge]`r`nApplyOverlay = $value`r`n")
Write-Host "Installed AYCE OC2 Bridge in $Mode mode. Log: $bep\LogOutput.log"
