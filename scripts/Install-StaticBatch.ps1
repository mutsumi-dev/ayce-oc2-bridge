param(
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$ActiveOverlay,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
$game = (Resolve-Path -LiteralPath $GamePath).Path
$root = (Resolve-Path -LiteralPath $Package).Path
$index = Get-Content -LiteralPath (Join-Path $root 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
if ($index.format -cne 'ayce-oc2-stock-test/1' -or $index.build -cne '22894075' -or $index.levels -lt 1 -or !$index.sha256) {
    throw 'Invalid static batch manifest.'
}
$gameHash = (Get-FileHash -LiteralPath (Join-Path $game 'GameAssembly.dll')).Hash
if ($gameHash -ne $index.gameSha256) { throw 'This batch does not match the installed AYCE build.' }
$dll = Join-Path $root 'AyceOc2Bridge.dll'
if (!(Test-Path -LiteralPath $dll -PathType Leaf)) { throw 'The static batch package is missing AyceOc2Bridge.dll.' }
if ($index.dllSha256 -and (Get-FileHash -LiteralPath $dll).Hash -ne $index.dllSha256) { throw 'The plugin DLL hash does not match the package.' }

$checked = @(
    foreach ($entry in $index.sha256.GetEnumerator()) {
        if ($entry.Key -cnotmatch '^(levels/[a-zA-Z0-9_-]+\.json|donors/s_[a-z0-9_]+\.bundle|art/[a-zA-Z0-9_-]+\.zip)$' -or $entry.Value -notmatch '^[a-f0-9]{64}$') {
            throw "Unexpected package file: $($entry.Key)"
        }
        $path = (Resolve-Path -LiteralPath (Join-Path $root $entry.Key)).Path
        if (!$path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $path).Hash -ne $entry.Value) { throw "File hash does not match: $($entry.Key)" }
        [pscustomobject]@{ Path = $path; Name = $entry.Key; Hash = $entry.Value }
    }
)
$levels = @($checked | Where-Object Name -like 'levels/*')
$donors = @($checked | Where-Object Name -like 'donors/*')
$art = @($checked | Where-Object Name -like 'art/*')
if ($levels.Count -ne $index.levels -or $donors.Count -ne $index.donors) { throw 'The package counts do not match the manifest.' }
foreach ($level in $levels) {
    $content = Get-Content -LiteralPath $level.Path -Raw | ConvertFrom-Json -AsHashtable
    if ($content.schemaVersion -ne 1 -or $content.targetScene -cne 's_city_h2' -or $content.targetGameSha256 -ne $gameHash -or !$content.stockKitchen) {
        throw "Invalid kernel layout: $($level.Name)"
    }
    foreach ($name in $content.stockKitchen.donors.Keys) {
        if ("donors/$name.bundle" -cnotin $donors.Name) { throw "Missing donor $name for $($level.Name)" }
    }
    if ($content.stockKitchen.art -and "art/$($content.stockKitchen.art)" -cnotin $art.Name) {
        throw "Missing art archive for $($level.Name)"
    }
}
if ($VerifyOnly) {
    Write-Host "Verified $($levels.Count) levels, $($donors.Count) native donor scenes and $($art.Count) original art archives."
    return
}
if (Get-Process -Name 'Overcooked All You Can Eat' -ErrorAction SilentlyContinue) { throw 'Close AYCE before installing the batch.' }

$plugin = Join-Path $game 'BepInEx/plugins/AyceOc2Bridge'
New-Item -ItemType Directory -Path (Join-Path $plugin 'donors'), (Join-Path $plugin 'levels'), (Join-Path $plugin 'art') -Force | Out-Null
foreach ($row in $checked) {
    $relative = if ($row.Name -like 'levels/*') { 'levels/stock-' + (Split-Path -Leaf $row.Name) } else { $row.Name }
    $destination = Join-Path $plugin $relative
    Copy-Item -LiteralPath $row.Path -Destination $destination -Force
    if ((Get-FileHash -LiteralPath $destination).Hash -ne $row.Hash) { throw "Installed hash does not match: $relative" }
}
& (Join-Path $PSScriptRoot 'Install-Bridge.ps1') -GamePath $game -Mode Custom -Overlay $ActiveOverlay -ReleaseDir $root
Write-Host "Installed $($levels.Count) static kitchens. Select them in the Custom Levels menu."
