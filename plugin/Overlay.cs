using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AyceOc2Bridge;

internal sealed class Overlay
{
    [JsonPropertyName("schemaVersion")] public int Version { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("targetBuild")] public string Build { get; set; }
    [JsonPropertyName("targetGameSha256")] public string Hash { get; set; }
    [JsonPropertyName("targetScene")] public string Scene { get; set; }
    [JsonPropertyName("moves")] public StationMove[] Moves { get; set; }
    [JsonPropertyName("recipeAllowlist")] public string[] Recipes { get; set; }
    [JsonPropertyName("campaignRules")] public Dictionary<string, CampaignRules> CampaignRules { get; set; }
    [JsonPropertyName("kitchen")] public KitchenLayout Kitchen { get; set; }
    [JsonPropertyName("stockKitchen")] public StockKitchen Stock { get; set; }
    [JsonPropertyName("provenance")] public LevelProvenance Provenance { get; set; }
    [JsonIgnore] public bool HasKitchen => Kitchen != null || Stock != null;
}

internal sealed class StockKitchen
{
    [JsonPropertyName("stations")] public StockStation[] Stations { get; set; }
    [JsonPropertyName("boxColliders")] public KitchenBox[] Boxes { get; set; }
    [JsonPropertyName("recipes")] public StockRecipe[] Recipes { get; set; }
    [JsonPropertyName("donors")] public Dictionary<string, StockDonor> Donors { get; set; }
    [JsonPropertyName("art")] public string Art { get; set; }
    [JsonPropertyName("artOffset")] public float[] ArtOffset { get; set; }
    [JsonPropertyName("artTheme")] public string ArtTheme { get; set; }
    [JsonPropertyName("floorTexture")] public string FloorTexture { get; set; }
}

internal sealed class StockStation
{
    [JsonPropertyName("id")] public string Id { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; }
    [JsonPropertyName("scene")] public string Scene { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
    [JsonPropertyName("support")] public string Support { get; set; }
    [JsonPropertyName("ingredient")] public string Ingredient { get; set; }
    [JsonPropertyName("capacity")] public int Capacity { get; set; }
    [JsonPropertyName("plateCount")] public int PlateCount { get; set; }
    [JsonPropertyName("returnStack")] public string ReturnStack { get; set; }
    [JsonPropertyName("returnStations")] public string[] ReturnStations { get; set; }
    [JsonPropertyName("portalExit")] public string PortalExit { get; set; }
    [JsonPropertyName("portalDoubleSided")] public bool PortalDoubleSided { get; set; }
    [JsonPropertyName("motionSpeed")] public float? MotionSpeed { get; set; }
    [JsonPropertyName("attachedSpawn")] public StockAttachedSpawn AttachedSpawn { get; set; }
    [JsonPropertyName("condimentItems")] public string[] CondimentItems { get; set; }
    [JsonPropertyName("condimentTargets")] public string[] CondimentTargets { get; set; }
    [JsonPropertyName("allowedIngredients")] public string[] AllowedIngredients { get; set; }
    [JsonPropertyName("unresolvedIngredients")] public string[] UnresolvedIngredients { get; set; }
    [JsonPropertyName("allowedCookingStationTypes")] public int[] AllowedCookingStationTypes { get; set; }
}

internal sealed class StockAttachedSpawn
{
    [JsonPropertyName("items")] public string[] Items { get; set; }
    [JsonPropertyName("weights")] public float[] Weights { get; set; }
    [JsonPropertyName("seconds")] public float Seconds { get; set; }
    [JsonPropertyName("target")] public string Target { get; set; }
    [JsonPropertyName("inOrder")] public bool InOrder { get; set; }
    [JsonPropertyName("atStart")] public bool AtStart { get; set; }
}

internal sealed class StockRecipe
{
    [JsonPropertyName("addressKey")] public string Address { get; set; }
    [JsonPropertyName("nativeType")] public string NativeType { get; set; }
    [JsonPropertyName("score")] public int? Score { get; set; }
}

internal sealed class StockDonor
{
    [JsonPropertyName("path")] public string Path { get; set; }
    [JsonPropertyName("dependencies")] public string[] Dependencies { get; set; }
}

internal sealed class LevelProvenance
{
    [JsonPropertyName("author")] public string Author { get; set; }
    [JsonPropertyName("sourceScene")] public string Scene { get; set; }
    [JsonPropertyName("sourceVersion")] public string Version { get; set; }
}

internal sealed class KitchenLayout
{
    [JsonPropertyName("placements")] public KitchenPlacement[] Placements { get; set; }
    [JsonPropertyName("disabled")] public string[] Disabled { get; set; }
    [JsonPropertyName("boxColliders")] public KitchenBox[] Boxes { get; set; }
}

internal sealed class KitchenPlacement
{
    [JsonPropertyName("source")] public string Source { get; set; }
    [JsonPropertyName("target")] public string Target { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; }
    [JsonPropertyName("clone")] public bool Clone { get; set; }
    [JsonPropertyName("support")] public string Support { get; set; }
    [JsonPropertyName("reparent")] public bool Reparent { get; set; }
    [JsonPropertyName("capacity")] public int Capacity { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
}

internal sealed class KitchenBox
{
    [JsonPropertyName("path")] public string Path { get; set; }
    [JsonPropertyName("layer")] public int Layer { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
    [JsonPropertyName("size")] public float[] Size { get; set; }
}

internal sealed class CampaignRules
{
    [JsonPropertyName("roundSeconds")] public int RoundSeconds { get; set; }
    [JsonPropertyName("orderSeconds")] public int OrderSeconds { get; set; }
    [JsonPropertyName("intervalSeconds")] public int IntervalSeconds { get; set; }
    [JsonPropertyName("starPoints")] public int[] StarPoints { get; set; }
}

internal sealed class StationMove
{
    [JsonPropertyName("path")] public string Path { get; set; }
    [JsonPropertyName("expectedPosition")] public float[] Original { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
}
