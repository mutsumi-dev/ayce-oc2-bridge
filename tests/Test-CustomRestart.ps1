param(
    [Parameter(Mandatory)][string]$BepInExCore,
    [Parameter(Mandatory)][string]$Plugin,
    [Parameter(Mandatory)][string]$LegacyOverlay,
    [Parameter(Mandatory)][string]$StockOverlay
)

$ErrorActionPreference = 'Stop'
$core = (Resolve-Path -LiteralPath $BepInExCore).Path
foreach ($library in ('BepInEx.Core.dll', '0Harmony.dll', 'BepInEx.Unity.IL2CPP.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $core $library)) | Out-Null
}
Add-Type -TypeDefinition @"
public sealed class RestartTestScene
{
    public int handle { get; set; }
}
public sealed class RestartTestObject
{
    public RestartTestScene scene { get; set; }
}
public sealed class RestartTestLoader
{
    public RestartTestObject gameObject { get; set; }
}
public sealed class RestartTestRecipeConfig
{
    public object m_recipeMatchingList { get; set; }
}
public static class RestartTestUnity
{
    public static int Destroyed { get; private set; }
    public static void Destroy(object item) { Destroyed++; }
}
"@

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$type = $assembly.GetType('AyceOc2Bridge.Plugin', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$read = $type.GetMethod('ReadOverlay', $flags)
$legacy = $read.Invoke($null, @((Resolve-Path -LiteralPath $LegacyOverlay).Path))
$stock = $read.Invoke($null, @((Resolve-Path -LiteralPath $StockOverlay).Path))
function Read-State([string]$Name) { $type.GetField($Name, $flags).GetValue($null) }
function Set-State([string]$Name, $Value) { $type.GetField($Name, $flags).SetValue($null, $Value) }
function Call-Plugin([string]$Name, [object[]]$Arguments = @()) { $type.GetMethod($Name, $flags).Invoke($null, $Arguments) }
function Use-TestScene([int]$Handle) {
    $script:loader = [RestartTestLoader]@{ gameObject = [RestartTestObject]@{ scene = [RestartTestScene]@{ handle = $Handle } } }
    Set-State sceneHandle $Handle
    Set-State kitchenHandle $Handle
}

Set-State log ([BepInEx.Logging.ManualLogSource]::new('Custom restart test'))
Set-State installedOverlay $legacy
Set-State overlay $stock
Set-State temporarySessionId 99
Set-State launchFromLevelMenu $true
Set-State kitchenDestroy ([RestartTestUnity].GetMethod('Destroy'))
Use-TestScene 21
Call-Plugin BeginCustomRun
Call-Plugin BeforeKitchenReload @('s_sushi_1_1')
if ($null -ne (Read-State customRestart)) { throw 'An unrelated reload was captured.' }

$assetsType = $type.GetNestedType('StockLoad', [Reflection.BindingFlags]'NonPublic')
$assets = [Activator]::CreateInstance($assetsType, $true)
$instanceFlags = [Reflection.BindingFlags]'Instance,NonPublic'
$configs = $assetsType.GetField('RecipeConfigs', $instanceFlags).GetValue($assets)
$art = $assetsType.GetField('OwnedArt', $instanceFlags).GetValue($assets)
$handles = $assetsType.GetField('Handles', $instanceFlags).GetValue($assets)
$config = [RestartTestRecipeConfig]@{ m_recipeMatchingList = 'patched matching data' }
$configs.Add(1, [ValueTuple[object,object]]::new($config, 'original matching data'))
$art.Add([object]::new())
$handles.Add([object]::new())
Set-State stockLoad $assets

foreach ($handle in 22, 23) {
    Call-Plugin BeforeKitchenReload @('s_city_h2')
    if ($null -eq (Read-State customRestart)) { throw 'A reload of the selected map was missed.' }
    Call-Plugin BeforeKitchenDestroyed @($loader)
    if ((Read-State sceneHandle) -or (Read-State kitchenHandle) -or (Read-State customRun) -or
        !(Read-State launchFromLevelMenu) -or (Read-State launchExpires) -le [Environment]::TickCount64) {
        throw 'Reload did not arm a new custom round.'
    }
    if (![object]::ReferenceEquals((Read-State overlay), $stock) -or ![object]::ReferenceEquals((Read-State stockLoad), $assets) -or
        (Read-State temporarySessionId) -ne 99 -or $handles.Count -ne 1) {
        throw 'Reload dropped the selected map, resources or temporary session.'
    }
    if ($art.Count -ne 0 -or $configs.Count -ne 0 -or [RestartTestUnity]::Destroyed -ne 1 -or
        $config.m_recipeMatchingList -ne 'original matching data') {
        throw 'Reload did not release previous-round resources.'
    }
    if ((Call-Plugin GuardCampaignProgress) -ne $false) { throw 'Reload allowed campaign progress updates.' }
    Use-TestScene $handle
    Call-Plugin BeginCustomRun
    if ((Read-State customRestart) -or (Read-State launchExpires) -or !(Read-State customRoundFromMenu)) {
        throw 'Reload did not finish as a new custom round.'
    }
}
Call-Plugin BeforeKitchenDestroyed @($loader)
if (![object]::ReferenceEquals((Read-State overlay), $legacy) -or
    (Read-State stockLoad) -or ![object]::ReferenceEquals((Read-State retiredStockLoad), $assets) -or (Read-State launchExpires)) {
    throw 'An ordinary exit kept the custom restart active.'
}

Use-TestScene 24
Call-Plugin BeginCustomRun
Call-Plugin BeforeKitchenReload @('s_city_h2')
Call-Plugin BeforeKitchenDestroyed @($loader)
if ((Read-State launchExpires) -le [Environment]::TickCount64 -or (Read-State launchFromLevelMenu)) {
    throw 'The legacy overlay did not restart independently of the catalog.'
}
Use-TestScene 25
Call-Plugin BeginCustomRun
Call-Plugin BeforeKitchenDestroyed @($loader)
if ((Read-State customRestart) -or (Read-State customRun)) { throw 'A legacy exit kept restart state.' }

$restartType = $type.GetNestedType('KitchenRestart', [Reflection.BindingFlags]'NonPublic')
Set-State customRestart ([Activator]::CreateInstance($restartType,[object[]]@(25,$true,([Environment]::TickCount64 - 100))))
Set-State launchExpires ([long]1)
Set-State launchFromLevelMenu $true
Set-State overlay $stock
Call-Plugin ExpireCustomRestart
if ((Read-State customRestart) -or (Read-State launchExpires) -or (Read-State launchFromLevelMenu) -or
    ![object]::ReferenceEquals((Read-State overlay), $legacy)) { throw 'An expired reload remained active.' }

Write-Host 'Repeated catalog restarts, legacy restart, ordinary exit, timeout and resource lifetime passed.'
