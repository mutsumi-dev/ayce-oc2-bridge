param(
    [Parameter(Mandatory)][string]$BepInExCore,
    [string]$OutputDir = (Join-Path $PSScriptRoot '../release'),
    [string]$NetRuntime = 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App\6.0.11'
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../plugin')).Path
$bep = (Resolve-Path -LiteralPath $BepInExCore).Path
$release = (New-Item -ItemType Directory -Path $OutputDir -Force).FullName
$sdk = Get-Command dotnet -ErrorAction SilentlyContinue
if ($sdk -and (& $sdk.Source --list-sdks | Where-Object { $_ -match '^6\.' })) {
    & $sdk.Source build (Join-Path $project 'AyceOc2Bridge.csproj') -c Release -p:BepInExCore=$bep -o $release
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
} else {
    if ($PSVersionTable.PSVersion.Major -lt 7) {
        throw 'Use PowerShell 7 for the portable compiler path or install the .NET 6 SDK.'
    }
    foreach ($name in ('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll')) {
        Add-Type -Path (Join-Path $PSHOME $name)
    }
    $assemblies = Get-ChildItem -LiteralPath $NetRuntime -Filter '*.dll' -File
    $references = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
    foreach ($library in $assemblies) {
        if ($library.Name -notmatch '^(System|Microsoft\.CSharp|Microsoft\.VisualBasic|mscorlib|netstandard)' -or $library.Name -like '*.Native.dll') { continue }
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($library.FullName))
    }
    foreach ($name in ('BepInEx.Core.dll', 'BepInEx.Unity.IL2CPP.dll', '0Harmony.dll')) {
        $references.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile((Join-Path $bep $name)))
    }
    $trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
    foreach ($file in (Get-ChildItem -LiteralPath $project -Filter '*.cs' -File | Sort-Object Name)) {
        $source = [IO.File]::ReadAllText($file.FullName)
        $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($source, $null, $file.Name, [Text.Encoding]::UTF8, [Threading.CancellationToken]::None))
    }
    $options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
    $options = $options.WithDeterministic($true).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release)
    $build = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('AyceOc2Bridge', $trees, $references, $options)
    $output = Join-Path $release 'AyceOc2Bridge.dll'
    $memory = [IO.MemoryStream]::new()
    try {
        $result = $build.Emit($memory, $null, $null, $null, $null, $null, [Threading.CancellationToken]::None)
        foreach ($diagnostic in $result.Diagnostics) {
            if ($diagnostic.Severity -in ('Error', 'Warning')) { Write-Host $diagnostic.ToString() }
        }
        if (-not $result.Success) { throw 'Roslyn build failed' }
        [IO.File]::WriteAllBytes($output, $memory.ToArray())
    } finally {
        $memory.Dispose()
    }
}
Get-FileHash -LiteralPath (Join-Path $release 'AyceOc2Bridge.dll') -Algorithm SHA256
