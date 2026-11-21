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
}

internal sealed class StationMove
{
    [JsonPropertyName("path")] public string Path { get; set; }
    [JsonPropertyName("expectedPosition")] public float[] Original { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
}
