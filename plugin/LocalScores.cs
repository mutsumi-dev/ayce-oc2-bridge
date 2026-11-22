using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;

namespace AyceOc2Bridge;

internal sealed record LocalScore(int Points, int Stars);

internal static class LocalScoreStore
{
    private static readonly object Gate = new();

    internal static Dictionary<string, LocalScore> Read(string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, LocalScore>();
        using var file = File.OpenRead(path);
        if (file.Length > 512 * 1024)
            throw new InvalidDataException("The standalone score file exceeds 512 KiB.");
        return JsonSerializer.Deserialize<Dictionary<string, LocalScore>>(file)
            ?? throw new InvalidDataException("The standalone score file is empty.");
    }

    internal static bool UpdateBest(string path, string key, LocalScore result)
    {
        lock (Gate)
        {
            var scores = Read(path);
            if (scores.TryGetValue(key, out var previous) && previous != null &&
                (previous.Points > result.Points || (previous.Points == result.Points && previous.Stars >= result.Stars)))
                return false;

            scores[key] = result;
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Open(temp, FileMode.CreateNew))
                {
                    JsonSerializer.Serialize(stream, scores);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temp, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            return true;
        }
    }
}

public sealed partial class Plugin
{
    private static LocalScore pendingLocalScore;
    private static bool wroteLocalScore;
    private static string LocalScorePath => Path.Combine(Paths.ConfigPath, "ayce-oc2-bridge-scores.json");

    private static string ScoreKey(Overlay level, int players)
    {
        var origin = level.Provenance;
        var identity = JsonSerializer.SerializeToUtf8Bytes(new object[] { origin?.Author, origin?.Scene ?? level.Name, origin?.Version, players });
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private static Dictionary<string, LocalScore> ReadScoresForMenu()
    {
        try { return LocalScoreStore.Read(LocalScorePath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            log.LogWarning($"BRIDGE_SCORE_READ_FAILED {error.Message}");
            return new Dictionary<string, LocalScore>();
        }
    }

    private static void CaptureLocalScore(int points, int stars)
    {
        if (!customRoundFromMenu || activePlayerCount is < 1 or > 4 || wroteLocalScore)
            return;
        pendingLocalScore = new LocalScore(points, stars);
        PersistLocalScore();
    }

    private static void PersistLocalScore()
    {
        if (!customRoundCompleted || pendingLocalScore == null || wroteLocalScore)
            return;
        try
        {
            var updated = LocalScoreStore.UpdateBest(LocalScorePath, ScoreKey(overlay, activePlayerCount), pendingLocalScore);
            wroteLocalScore = true;
            log.LogInfo($"BRIDGE_LOCAL_SCORE kitchen={overlay.Name} players={activePlayerCount} points={pendingLocalScore.Points} stars={pendingLocalScore.Stars} bestUpdated={updated}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            log.LogWarning($"BRIDGE_SCORE_WRITE_FAILED {error.Message}");
        }
    }
}
