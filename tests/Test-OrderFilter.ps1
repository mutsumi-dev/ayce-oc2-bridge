param(
    [Parameter(Mandatory)][string]$BepInExCore,
    [string]$Plugin = (Join-Path $PSScriptRoot '../release/AyceOc2Bridge.dll')
)

$ErrorActionPreference = 'Stop'
$core = (Resolve-Path -LiteralPath $BepInExCore).Path
foreach ($library in ('BepInEx.Core.dll', '0Harmony.dll', 'BepInEx.Unity.IL2CPP.dll')) {
    [Reflection.Assembly]::LoadFrom((Join-Path $core $library)) | Out-Null
}

Add-Type -TypeDefinition @'
public sealed class OrderFilterTestDish
{
    public string name { get; set; }
}

public sealed class OrderFilterTestEntry
{
    public OrderFilterTestDish m_order { get; set; }
}

public sealed class OrderFilterTestPool
{
    public OrderFilterTestEntry[] m_recipes { get; set; }
}

public sealed class OrderFilterTestRound
{
    public OrderFilterTestPool m_recipes { get; set; }
}

public sealed class OrderFilterTestSelection
{
    private readonly OrderFilterTestEntry[] entries;
    public OrderFilterTestSelection(OrderFilterTestEntry entry) => entries = new[] { entry };
    public int Length => entries.Length;
    public OrderFilterTestEntry this[int index] { get => entries[index]; set => entries[index] = value; }
}
'@

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $Plugin).Path)
$type = $assembly.GetType('AyceOc2Bridge.Plugin', $true)
$flags = [Reflection.BindingFlags]'Static,NonPublic'
$dto = $assembly.GetType('AyceOc2Bridge.Overlay', $true)
$overlay = [Activator]::CreateInstance($dto, $true)
$dto.GetProperty('Recipes').SetValue($overlay, [string[]]@('Sushi_PlainFish'))
$type.GetField('overlay', $flags).SetValue($null, $overlay)
$type.GetField('applyOverlay', $flags).SetValue($null, $true)
$type.GetField('sceneHandle', $flags).SetValue($null, 123)
$type.GetField('log', $flags).SetValue($null, [BepInEx.Logging.ManualLogSource]::new('Order filter test'))

$fish = [OrderFilterTestEntry]::new()
$fish.m_order = [OrderFilterTestDish]::new()
$fish.m_order.name = 'Sushi_PlainFish'
$prawn = [OrderFilterTestEntry]::new()
$prawn.m_order = [OrderFilterTestDish]::new()
$prawn.m_order.name = 'Sushi_PlainPrawn'
$round = [OrderFilterTestRound]::new()
$round.m_recipes = [OrderFilterTestPool]::new()
$round.m_recipes.m_recipes = @($fish, $prawn)
$type.GetMethod('BeforeRound', $flags).Invoke($null, @($round)) | Out-Null

$filter = $type.GetMethod('AfterRecipeSelection', $flags)
foreach ($entry in @($prawn, $fish, $prawn)) {
    $selection = [OrderFilterTestSelection]::new($entry)
    $filter.Invoke($null, @($selection)) | Out-Null
    if ($selection.get_Item(0).m_order.name -cne 'Sushi_PlainFish') { throw 'A scripted or generated order escaped filtering.' }
}
if ($round.m_recipes.m_recipes.Count -ne 2 -or $round.m_recipes.m_recipes[1].m_order.name -cne 'Sushi_PlainPrawn') { throw 'The original recipe pool was modified.' }
$type.GetField('sceneHandle', $flags).SetValue($null, $null)
$selection = [OrderFilterTestSelection]::new($prawn)
$filter.Invoke($null, @($selection)) | Out-Null
if ($selection.get_Item(0).m_order.name -cne 'Sushi_PlainPrawn') { throw 'The filter affected another scene.' }
Write-Host 'Selected array replacement, original pool, and scene isolation passed.'
