using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private sealed record KitchenLoop(string Role, string Tag, string Address, float Volume);
    private const string CampfireLoop = "Assets/DownloadableContent/DLC05/DLC_Assets/Audio/SFX/Overcooked_02_DLC_05_Campfire.wav";
    private const string BarbecueAudio = "Assets/DownloadableContent/DLC02/DLC_Assets/Audio/DLC_02/SFX/";
    private static readonly KitchenLoop[] KitchenLoops =
    {
        new("campfire", "DLC_05_Fire_Sml", CampfireLoop, .3f),
        new("campfire", "DLC_05_Fire_Med", CampfireLoop, .45f),
        new("campfire", "DLC_05_Fire_Lrg", CampfireLoop, .6f),
        new("barbecue", "DLC_02_BBQ_Idle", BarbecueAudio + "DLC_02_BBQ_Idle.wav", .55f),
        new("barbecue", "DLC_02_BBQ_Smoke", BarbecueAudio + "DLC_02_BBQ_Idle_Smoke.wav", .55f),
        new("blender", "DLC_02_SmoothieMaker", "Assets/Audio/Overcooked2/Effects/MixerLoop.wav", .55f),
    };

    private static MethodInfo stockAudioEntry, stockAudioCreate, stockAudioAdd;
    private static PropertyInfo stockAudioManager;
    private static Type stockAudioTag, stockAudioDirectory, stockAudioClip, stockAudioEntryType, stockAudioTagType;

    private static void InstallStockAudio(Assembly game)
    {
        var manager = game.GetType("AudioManager", true);
        stockAudioManager = RequireProperty(manager, "s_instance");
        stockAudioTag = game.GetType("GameLoopingAudioTag", true);
        stockAudioTagType = game.GetType("AudioTagType", true);
        stockAudioDirectory = game.GetType("AudioDirectoryData", true);
        stockAudioEntryType = game.GetType("AudioDirectoryData+LoopingAudioDirectoryEntry", true);
        stockAudioClip = Assembly.Load("UnityEngine.AudioModule").GetType("UnityEngine.AudioClip", true);
        stockAudioEntry = Find(manager, "FindEntry", new[] { stockAudioTag });
        stockAudioCreate = kitchenNewLookup.GetGenericMethodDefinition().MakeGenericMethod(stockAudioDirectory);
        stockAudioAdd = Find(manager, "AddAudioDirectory", new[] { stockAudioDirectory });
    }

    private static IEnumerable<object> PrepareStockAudio(StockLoad state, StockKitchen kitchen)
    {
        var roles = kitchen.Stations.Select(row => row.Role).ToHashSet(StringComparer.Ordinal);
        var loops = KitchenLoops.Where(loop => roles.Contains(loop.Role)).ToArray();
        var tasks = loops.Select(loop => loop.Address).Distinct(StringComparer.Ordinal)
            .Select(key => (Key: key, Operation: StartAddressable(state, stockAudioClip, key))).ToArray();
        var clips = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            while (!(bool)Get(task.Operation, "IsDone")) yield return null;
            clips.Add(task.Key, ReadAddressable(task.Operation, task.Key));
        }
        foreach (var loop in loops)
            state.LoopingAudio.Add((loop, clips[loop.Address]));
    }

    private static void ApplyStockAudio(StockLoad state)
    {
        if (state.LoopingAudio.Count == 0 || state.RoundAudioDirectory != null)
            return;
        try
        {
            var manager = stockAudioManager.GetValue(null) ?? throw new InvalidOperationException("Game audio is not ready.");
            var entries = new List<object>();
            foreach (var (loop, clip) in state.LoopingAudio)
            {
                var tag = Enum.Parse(stockAudioTag, loop.Tag);
                if (stockAudioEntry.Invoke(manager, new[] { tag }) != null)
                    continue;
                var entry = Activator.CreateInstance(stockAudioEntryType, nonPublic: true);
                Set(entry, "Tag", tag);
                Set(entry, "AudioType", Enum.Parse(stockAudioTagType, "Gameplay"));
                Set(entry, "AudioFile", clip);
                Set(entry, "Volume", loop.Volume);
                Set(entry, "Pitch", 1f);
                Set(entry, "Priority", 128);
                Set(entry, "m_fadeInTime", .12f);
                Set(entry, "m_fadeOutTime", .2f);
                entries.Add(entry);
            }
            if (entries.Count == 0)
                return;
            var directory = stockAudioCreate.Invoke(null, null);
            state.OwnedArt.Add(directory);
            Set(directory, "name", "AYCE bridge kitchen audio");
            SetNativeObjectArray(directory, "LoopingAudio", stockAudioEntryType, entries);
            var oneShot = stockAudioDirectory.GetNestedType("OneShotAudioDirectoryEntry", BindingFlags.Public | BindingFlags.NonPublic);
            SetNativeObjectArray(directory, "OneShotAudio", oneShot, Array.Empty<object>());
            if ((bool)stockAudioAdd.Invoke(manager, new[] { directory }))
            {
                state.RoundAudioDirectory = directory;
                log.LogInfo($"BRIDGE_STOCK_AUDIO loops={entries.Count}");
            }
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_AUDIO_FAILED {error}"); }
    }

    private static void RestoreStockAudio(StockLoad state)
    {
        if (state.RoundAudioDirectory == null)
            return;
        try
        {
            var id = KitchenObjectId(state.RoundAudioDirectory);
            var manager = stockAudioManager.GetValue(null);
            if (manager != null && Get(manager, "m_audioDirectories") is object directories)
            {
                var original = Enumerate(directories).ToArray();
                var remaining = original.Where(directory => directory == null || KitchenObjectId(directory) != id).ToArray();
                if (remaining.Length != original.Length)
                    SetNativeObjectArray(manager, "m_audioDirectories", stockAudioDirectory, remaining);
            }
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_AUDIO_RESTORE_FAILED {error.Message}"); }
        state.RoundAudioDirectory = null;
    }
}
