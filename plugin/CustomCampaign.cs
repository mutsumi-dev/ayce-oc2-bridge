using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private static MethodInfo getKeyDown;
    private static MethodInfo getPortals;
    private static MethodInfo getMapControls;
    private static MethodInfo selectPortal;
    private static MethodInfo startMapCoroutine;
    private static MethodInfo getGameSession;
    private static PropertyInfo serverTimeLimit;
    private static PropertyInfo clientTimeLimit;
    private static PropertyInfo clientTimerStore;
    private static PropertyInfo clientTimeUpdatedId;
    private static MethodInfo writeTimerStore;
    private static FieldInfo timerValue;
    private static MethodInfo boxTimerValue;
    private static object launchKey;
    private static long launchExpires;
    private static bool customInputUnavailable;
    private static bool customRun;
    private static bool customRulesErrorReported;
    private static CampaignRules activeRules;
    private static bool CustomLaunchPending => launchExpires != 0 && Environment.TickCount64 < launchExpires;

    private static void ValidateCampaignRules(Overlay value)
    {
        var rules = value.CampaignRules;
        if (rules == null)
            return;
        if (value.Scene != KevinScene || value.Recipes.Length == 0 || !rules.ContainsKey("1") || rules.Count > 4)
            throw new InvalidDataException("Custom campaign requires an imported Kevin 2 menu and solo rules.");
        foreach (var (count, player) in rules)
        {
            if (count is not ("1" or "2" or "3" or "4") || player == null ||
                player.RoundSeconds is < 1 or > 3600 || player.OrderSeconds is < 1 or > 3600 ||
                player.IntervalSeconds is < 1 or > 3600 || player.StarPoints?.Length != 4 ||
                player.StarPoints.Any(n => n < 0) || !player.StarPoints.SequenceEqual(player.StarPoints.OrderBy(n => n)))
                throw new InvalidDataException($"Invalid campaign rules for {count} players.");
        }
    }

    private static void InstallCustomCampaignHooks(Harmony harmony, Assembly game, Assembly unity)
    {
        var inputAssembly = Assembly.Load("UnityEngine.InputLegacyModule");
        var input = inputAssembly.GetType("UnityEngine.Input", throwOnError: true);
        var key = inputAssembly.GetType("UnityEngine.KeyCode") ?? unity.GetType("UnityEngine.KeyCode", throwOnError: true);
        getKeyDown = Find(input, "GetKeyDown", new[] { key });
        launchKey = Enum.Parse(key, "F8");

        var flow = game.GetType("ClientWorldMapFlowController", throwOnError: true);
        var node = game.GetType("LevelPortalMapNode", throwOnError: true);
        var controls = game.GetType("MapAvatarControls", throwOnError: true);
        var unityObject = unity.GetType("UnityEngine.Object", throwOnError: true);
        var findObjects = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "FindObjectsOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        getPortals = findObjects.MakeGenericMethod(node);
        getMapControls = findObjects.MakeGenericMethod(controls);
        selectPortal = Find(flow, "OnSelectLevelPortal", new[] { controls, node });
        startMapCoroutine = Find(flow, "StartCoroutine", new[] { selectPortal.ReturnType });
        getGameSession = Find(game.GetType("GameUtils", throwOnError: true), "GetGameSession", Type.EmptyTypes);

        Hook(harmony, Find(flow, "LateUpdate", Type.EmptyTypes), nameof(AfterWorldMapUpdate), postfix: true);
        var config = game.GetType("CampaignLevelConfigBase", throwOnError: true);
        Hook(harmony, Find(config, "GetTimeLimit", Type.EmptyTypes), nameof(AfterTimeLimit), postfix: true);
        var serverTimer = game.GetType("ServerRoundTimer", throwOnError: true);
        var clientTimer = game.GetType("ClientRoundTimer", throwOnError: true);
        serverTimeLimit = RequireProperty(serverTimer, "m_timeLimit");
        clientTimeLimit = RequireProperty(clientTimer, "m_timeLimit");
        clientTimerStore = RequireProperty(clientTimer, "m_dataStore");
        clientTimeUpdatedId = RequireProperty(clientTimer, "k_timeUpdatedId");
        var nativeSingle = Assembly.Load("Il2Cppmscorlib").GetType("Il2CppSystem.Single", throwOnError: true);
        timerValue = AccessTools.Field(nativeSingle, "m_value") ?? throw new MissingFieldException(nativeSingle.FullName, "m_value");
        boxTimerValue = Find(nativeSingle, "BoxIl2CppObject", Type.EmptyTypes);
        writeTimerStore = Find(clientTimerStore.PropertyType, "Write", new[] { clientTimeUpdatedId.PropertyType, boxTimerValue.ReturnType });
        Hook(harmony, Find(serverTimer, "Initialise", Type.EmptyTypes), nameof(AfterServerTimerInitialised), postfix: true);
        Hook(harmony, Find(clientTimer, "Initialise", Type.EmptyTypes), nameof(AfterClientTimerInitialised), postfix: true);
        var order = game.GetType("ServerFixedTimeOrderController", throwOnError: true);
        Hook(harmony, Find(order, "GetNextTimeBetweenOrders", Type.EmptyTypes), nameof(AfterOrderInterval), postfix: true);
        Hook(harmony, Find(order, "GetNextOrderLifetime", Type.EmptyTypes), nameof(AfterOrderLifetime), postfix: true);
        var variant = game.GetType("SceneDirectoryData+PerPlayerCountDirectoryEntry", throwOnError: true);
        Hook(harmony, Find(variant, "GetStarForPoints", new[] { typeof(int), typeof(bool) }), nameof(AfterStarRating), postfix: true);
        Hook(harmony, Find(variant, "GetPointsForStar", new[] { typeof(int) }), nameof(AfterPointsForStar), postfix: true);
        // The results screen calls the individual getters for the four visible labels.
        Hook(harmony, RequireProperty(variant, "OneStarScore").GetMethod, nameof(AfterOneStarScore), postfix: true);
        Hook(harmony, RequireProperty(variant, "TwoStarScore").GetMethod, nameof(AfterTwoStarScore), postfix: true);
        Hook(harmony, RequireProperty(variant, "ThreeStarScore").GetMethod, nameof(AfterThreeStarScore), postfix: true);
        Hook(harmony, RequireProperty(variant, "FourStarScore").GetMethod, nameof(AfterFourStarScore), postfix: true);

        var progress = game.GetType("GameProgress", throwOnError: true);
        // The public settlement method has an IL2CPP ref array. Guard the writes it calls instead.
        foreach (var name in new[] { "ApplyLevelProgress", "RecordLevelScore", "RecordFailedAttempt", "UnlockNewGamePlus" })
            Hook(harmony, Find(progress, name), nameof(GuardCampaignProgress));
        var unlockCandidate = game.GetType("GameProgress+__c__DisplayClass58_0", throwOnError: true);
        Hook(harmony, Find(unlockCandidate, "_FindNewUnlocks_b__0"), nameof(GuardUnlockCandidate));
        var scores = game.GetType("HighScoreRepository", throwOnError: true);
        Hook(harmony, Find(scores, "LevelProgress"), nameof(GuardCampaignProgress));
        log.LogInfo($"BRIDGE_CUSTOM_ENTRY key=F8 kitchen={overlay.Name} scene={overlay.Scene}");
    }

    private static PropertyInfo RequireProperty(Type owner, string member) =>
        AccessTools.Property(owner, member) ?? throw new MissingMemberException(owner.FullName, member);

    private static void AfterWorldMapUpdate(object __instance)
    {
        if (sceneHandle != null || CustomLaunchPending || customInputUnavailable)
            return;
        try
        {
            if (!(bool)getKeyDown.Invoke(null, new[] { launchKey }))
                return;
        }
        catch (Exception error)
        {
            customInputUnavailable = true;
            log.LogWarning($"BRIDGE_CUSTOM_INPUT_UNAVAILABLE {error}");
            return;
        }
        try
        {
            if ((bool)isSteamLoggedOn.Invoke(null, null))
                throw new InvalidOperationException("Steam Offline Mode is required.");
            var map = Handle(SceneOf(__instance));
            var portals = ((IEnumerable)getPortals.Invoke(null, null)).Cast<object>();
            var portal = portals.SingleOrDefault(node => MapPortalTargets(node, map, overlay.Scene))
                ?? throw new InvalidOperationException("The target portal is not available on this world map.");
            var controller = Get(portal, "m_worldMapFlowController");
            var unlock = Find(controller.GetType(), "IsLevelUnlocked");
            if (!(bool)unlock.Invoke(controller, new[] { portal }))
                throw new InvalidOperationException("The target portal is locked in this save.");
            var controls = ((IEnumerable)getMapControls.Invoke(null, null)).Cast<object>()
                .SingleOrDefault(control => Handle(SceneOf(control)) == map)
                ?? throw new InvalidOperationException("Local world-map controls are not ready.");

            var routine = selectPortal.Invoke(__instance, new[] { controls, portal })
                ?? throw new InvalidOperationException("The game did not create a level-selection routine.");
            launchExpires = Environment.TickCount64 + 45000;
            startMapCoroutine.Invoke(__instance, new[] { routine });
            log.LogInfo($"BRIDGE_CUSTOM_LAUNCH kitchen={overlay.Name} scene={overlay.Scene}");
        }
        catch (Exception error)
        {
            launchExpires = 0;
            log.LogWarning($"BRIDGE_CUSTOM_LAUNCH_REJECTED {error}");
        }
    }

    private static bool MapPortalTargets(object node, int map, string target)
    {
        if (node == null || Handle(SceneOf(node)) != map)
            return false;
        var entry = Get(node, "m_sceneDirectoryEntry");
        if (entry == null || Get(entry, "SceneVarients") is not IEnumerable variants)
            return false;
        return variants.Cast<object>().Any(value => value != null && (string)Get(value, "SceneName") == target);
    }

    private static void BeginCustomRun()
    {
        launchExpires = 0;
        customRun = true;
        activeRules = null;
        customRulesErrorReported = false;
        log.LogInfo($"BRIDGE_CUSTOM_ACTIVE kitchen={overlay.Name}");
    }

    private static CampaignRules GetActiveCampaignRules()
    {
        if (!customRun && !CustomLaunchPending)
            return null;
        if (activeRules != null)
            return activeRules;
        try
        {
            var session = getGameSession.Invoke(null, null);
            var variant = Get(Get(session, "LevelSettings"), "SceneDirectoryVarientEntry");
            if (!customRun && (variant == null || (string)Get(variant, "SceneName") != overlay.Scene))
                return null;
            var count = (int)Get(variant, "PlayerCount");
            if (!overlay.CampaignRules.TryGetValue(count.ToString(CultureInfo.InvariantCulture), out var rules))
                throw new InvalidDataException($"No source rules for {count} players.");
            if (customRun)
            {
                activeRules = rules;
                log.LogInfo($"BRIDGE_CUSTOM_RULES players={count} round={rules.RoundSeconds} stars={string.Join(",", rules.StarPoints)}");
            }
            return rules;
        }
        catch (Exception error)
        {
            if (customRun && !customRulesErrorReported)
            {
                customRulesErrorReported = true;
                log.LogWarning($"BRIDGE_CUSTOM_RULES_SKIPPED {error}");
            }
            return null;
        }
    }

    private static void AfterTimeLimit(ref float __result)
    {
        if (GetActiveCampaignRules() is CampaignRules rules)
            __result = rules.RoundSeconds;
    }

    private static void AfterServerTimerInitialised(object __instance)
    {
        if (GetActiveCampaignRules() is not CampaignRules rules)
            return;
        try
        {
            var original = (float)serverTimeLimit.GetValue(__instance);
            serverTimeLimit.SetValue(__instance, (float)rules.RoundSeconds);
            log.LogInfo($"BRIDGE_CUSTOM_TIMER side=server original={original} seconds={serverTimeLimit.GetValue(__instance)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_CUSTOM_TIMER_SKIPPED side=server {error}");
        }
    }

    private static void AfterClientTimerInitialised(object __instance)
    {
        if (GetActiveCampaignRules() is not CampaignRules rules)
            return;
        try
        {
            var original = (float)clientTimeLimit.GetValue(__instance);
            var seconds = (float)rules.RoundSeconds;
            clientTimeLimit.SetValue(__instance, seconds);
            var value = Activator.CreateInstance(timerValue.DeclaringType);
            timerValue.SetValue(value, seconds);
            var id = clientTimeUpdatedId.GetValue(null);
            var boxed = boxTimerValue.Invoke(value, null);
            writeTimerStore.Invoke(clientTimerStore.GetValue(__instance), new[] { id, boxed });
            log.LogInfo($"BRIDGE_CUSTOM_TIMER side=client original={original} seconds={clientTimeLimit.GetValue(__instance)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_CUSTOM_TIMER_SKIPPED side=client {error}");
        }
    }

    private static void AfterOrderInterval(ref float __result)
    {
        if (GetActiveCampaignRules() is CampaignRules rules)
            __result = rules.IntervalSeconds;
    }

    private static void AfterOrderLifetime(ref float __result)
    {
        if (GetActiveCampaignRules() is CampaignRules rules)
            __result = rules.OrderSeconds;
    }

    private static void AfterStarRating(int __0, ref int __result)
    {
        if (GetActiveCampaignRules() is not CampaignRules rules)
            return;
        __result = rules.StarPoints.Count(threshold => __0 >= threshold);
        log.LogInfo($"BRIDGE_CUSTOM_RESULT points={__0} stars={__result}");
    }

    private static void AfterPointsForStar(int __0, ref int __result)
    {
        if (__0 is >= 1 and <= 4 && GetActiveCampaignRules() is CampaignRules rules)
            __result = rules.StarPoints[__0 - 1];
    }

    private static void AfterOneStarScore(object __instance, ref int __result) => ApplyStarScore(__instance, 1, ref __result);
    private static void AfterTwoStarScore(object __instance, ref int __result) => ApplyStarScore(__instance, 2, ref __result);
    private static void AfterThreeStarScore(object __instance, ref int __result) => ApplyStarScore(__instance, 3, ref __result);
    private static void AfterFourStarScore(object __instance, ref int __result) => ApplyStarScore(__instance, 4, ref __result);

    private static void ApplyStarScore(object variant, int star, ref int score)
    {
        if (!customRun || GetActiveCampaignRules() is not CampaignRules rules || (string)Get(variant, "SceneName") != overlay.Scene)
            return;
        score = rules.StarPoints[star - 1];
        log.LogInfo($"BRIDGE_CUSTOM_STAR_THRESHOLD stars={star} points={score}");
    }

    private static bool GuardCampaignProgress()
    {
        if (!customRun && failedKitchenHandle == null)
            return true;
        log.LogInfo("BRIDGE_CUSTOM_PROGRESS_BLOCKED");
        return false;
    }

    private static bool GuardUnlockCandidate(ref bool __result)
    {
        if (!customRun && failedKitchenHandle == null)
            return true;
        __result = false;
        return false;
    }

    private static void EndCustomRun()
    {
        customRun = false;
        activeRules = null;
    }
}
