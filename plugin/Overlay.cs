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
