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
    // Both -1 values have distinct meanings in the stock game: OC2 base campaign and no save slot.
    private const int Oc2BaseCampaign = -1;
    private const int NoSaveSlot = -1;
    private sealed record StandaloneRequest(Overlay Level, int? PreviousSession, long Deadline);
    private static StandaloneRequest standaloneRequest;
    private static MethodInfo frontendInstance;
    private static MethodInfo createTemporarySession;
    private static MethodInfo wrapFrontendRoutine;
    private static MethodInfo startFrontendRoutine;
    private static MethodInfo stopFrontendRoutine;
    private static MethodInfo countLocalUsers;
    private static MethodInfo countRemoteUsers;
    private static MethodInfo countServerUsers;
    private static MethodInfo loadByIndex;
    private static PropertyInfo serverGameMode;
    private static Type levelSettingsType;
    private static object campaignMode;
    private static object loadKitchen;
    private static object runKitchen;
    private static int? temporarySessionId;
    private static object standaloneCoroutine;
    private static object standaloneFrontend;

    private static void InstallStandaloneLaunch(Harmony harmony, Assembly game)
    {
        var frontend = game.GetType("T17FrontendFlow", throwOnError: true);
        frontendInstance = Find(frontend, "get_Instance", Type.EmptyTypes);
        var saveDialog = game.GetType("SelectSaveDialog", throwOnError: true);
        createTemporarySession = Find(saveDialog, "CreateFreshGameSessionForSlotAsync", new[] { typeof(int), typeof(int) });
        startFrontendRoutine = Find(frontend, "StartCoroutine", new[] { createTemporarySession.ReturnType });
        stopFrontendRoutine = Find(frontend, "StopCoroutine", new[] { startFrontendRoutine.ReturnType });
        var collections = Assembly.Load("BepInEx.Unity.IL2CPP")
            .GetType("BepInEx.Unity.IL2CPP.Utils.Collections.CollectionExtensions", throwOnError: true);
        wrapFrontendRoutine = Find(collections, "WrapToIl2Cpp", new[] { typeof(IEnumerator) });
        var clientUsers = game.GetType("Team17.Online.ClientUserSystem", throwOnError: true);
        countLocalUsers = Find(clientUsers, "GetNumberOfLocalUsers", Type.EmptyTypes);
        countRemoteUsers = Find(clientUsers, "GetNumberOfRemoteUsers", Type.EmptyTypes);
        countServerUsers = Find(game.GetType("Team17.Online.ServerUserSystem", throwOnError: true), "GetNumClientUsers", Type.EmptyTypes);
        serverGameMode = RequireProperty(game.GetType("ServerGameSetup", throwOnError: true), "Mode");
        campaignMode = Enum.Parse(serverGameMode.PropertyType, "Campaign");
        var state = game.GetType("GameState", throwOnError: true);
        loadKitchen = Enum.Parse(state, "LoadKitchen");
        runKitchen = Enum.Parse(state, "RunKitchen");
        loadByIndex = Find(game.GetType("ServerMessenger", throwOnError: true), "LoadLevel", new[] { typeof(uint), typeof(uint), state, state });
        var session = game.GetType("GameSession", throwOnError: true);
        levelSettingsType = RequireProperty(session, "LevelSettings").PropertyType;

        Hook(harmony, Find(frontend, "Update", Type.EmptyTypes), nameof(AfterStandaloneFrontendUpdate), postfix: true);
        Hook(harmony, Find(session, "IsSaveable", Type.EmptyTypes), nameof(BeforeStandaloneIsSaveable));
        Hook(harmony, Find(session, "OnDestroy", Type.EmptyTypes), nameof(BeforeTemporarySessionDestroyed));
    }

    private static int LocalPlayerCount() => (int)countLocalUsers.Invoke(null, null);

    private static void BeginStandaloneLevel(Overlay level)
    {
        ExpireCustomRestart();
        if (standaloneRequest != null || CustomLaunchPending || sceneHandle != null)
            return;
        if ((bool)isSteamLoggedOn.Invoke(null, null) || (int)countRemoteUsers.Invoke(null, null) != 0)
            throw new InvalidOperationException("Standalone kitchens require Steam Offline Mode and local players.");
        var players = LocalPlayerCount();
        if (!level.CampaignRules.ContainsKey(players.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidOperationException($"This package has no rules for {players} local players.");

        var frontend = frontendInstance.Invoke(null, null) ?? throw new InvalidOperationException("The game frontend is not ready.");
        var previous = getGameSession.Invoke(null, null);
        int? previousId = previous == null ? null : (int)Call(previous, "GetInstanceID");
        var routine = createTemporarySession.Invoke(null, new object[] { Oc2BaseCampaign, NoSaveSlot })
            ?? throw new InvalidOperationException("The game did not create a temporary session routine.");
        var request = new StandaloneRequest(level, previousId, Environment.TickCount64 + (level.Stock == null ? 30_000 : 150_000));
        standaloneRequest = request;
        standaloneFrontend = frontend;
        try
        {
            var wrapper = wrapFrontendRoutine.Invoke(null, new object[] { PrepareStandaloneKitchen(request, routine) });
            standaloneCoroutine = startFrontendRoutine.Invoke(frontend, new[] { wrapper });
            log.LogInfo($"BRIDGE_STANDALONE_PREPARING kitchen={level.Name} players={players}");
        }
        catch
        {
            ClearStandaloneRequest();
            throw;
        }
    }

    private static void AfterStandaloneFrontendUpdate()
    {
        ExpireCustomRestart();
        if (sceneHandle == null)
            DisposeRetiredStock();
        if (standaloneRequest == null || Environment.TickCount64 < standaloneRequest.Deadline)
            return;
        CancelStandaloneRequest();
        levelMenu?.ShowStatus("载入会话超时，请重试。");
        log.LogWarning("BRIDGE_STANDALONE_REJECTED temporary session creation timed out");
    }

    private static IEnumerator PrepareStandaloneKitchen(StandaloneRequest request, object nativeSessionRoutine)
    {
        yield return nativeSessionRoutine;
        if (request.Level.Stock != null && ReferenceEquals(standaloneRequest, request))
        {
            var preparation = PrepareStockLoad(request.Level);
            while (ReferenceEquals(standaloneRequest, request))
            {
                if (!AdvanceStockPreparation(preparation))
                    break;
                yield return preparation.Current;
            }
        }
        while (ReferenceEquals(standaloneRequest, request) && !TryStartStandaloneKitchen(request))
            yield return null;
    }

    private static bool AdvanceStockPreparation(IEnumerator preparation)
    {
        try { return preparation.MoveNext(); }
        catch (Exception error)
        {
            ClearStandaloneRequest();
            ReleaseStockKitchen();
            levelMenu?.ShowStatus("厨房资源准备失败，请查看桥接日志。");
            log.LogWarning($"BRIDGE_STOCK_PREPARATION_FAILED {error}");
            return false;
        }
    }

    private static bool TryStartStandaloneKitchen(StandaloneRequest request)
    {
        try
        {
            var session = getGameSession.Invoke(null, null);
            if (session == null || (int)Call(session, "GetInstanceID") == request.PreviousSession ||
                (int)Get(session, "DLC") != Oc2BaseCampaign || (int)Get(session, "SaveSlot") != NoSaveSlot)
                throw new InvalidDataException("The game did not produce a new OC2 session without a save slot.");
            if ((bool)isSteamLoggedOn.Invoke(null, null) || (int)countRemoteUsers.Invoke(null, null) != 0)
                throw new InvalidOperationException("The session changed from local offline mode.");
            var players = LocalPlayerCount();
            if (players != (int)countServerUsers.Invoke(null, null))
                return false;
            if (!request.Level.CampaignRules.ContainsKey(players.ToString(CultureInfo.InvariantCulture)))
                throw new InvalidOperationException($"This package has no rules for {players} local players.");

            StartStandaloneKitchen(request.Level, session, players);
            return true;
        }
        catch (Exception error)
        {
            ClearStandaloneRequest();
            if (request.Level.Stock != null)
                ReleaseStockKitchen();
            levelMenu?.ShowStatus("开局失败，请确认 Steam 已离线后重试。");
            log.LogWarning($"BRIDGE_STANDALONE_REJECTED {error}");
            return true;
        }
    }

    private static void CancelStandaloneRequest()
    {
        if (standaloneRequest == null)
            return;
        if (standaloneRequest.Level.Stock != null)
            ReleaseStockKitchen();
        var coroutine = standaloneCoroutine;
        var frontend = standaloneFrontend;
        ClearStandaloneRequest();
        if (coroutine == null || frontend == null)
            return;
        try { stopFrontendRoutine.Invoke(frontend, new[] { coroutine }); }
        catch (Exception error) { log.LogWarning($"BRIDGE_STANDALONE_CANCEL_FAILED {error.Message}"); }
    }

    private static void ClearStandaloneRequest()
    {
        standaloneRequest = null;
        standaloneCoroutine = null;
        standaloneFrontend = null;
    }

    private static void StartStandaloneKitchen(Overlay level, object session, int players)
    {
        var progress = Get(session, "Progress");
        var directory = Get(progress, "m_sceneDirectory");
        var scenes = (IEnumerable)Get(directory, "Scenes");
        var match = scenes.Cast<object>().Select((entry, index) => (Entry: entry, Index: index))
            .Single(candidate => candidate.Entry != null && Enumerate(Get(candidate.Entry, "SceneVarients"))
                .Any(variant => variant != null && (string)Get(variant, "SceneName") == level.Scene && (int)Get(variant, "PlayerCount") == players));
        var variant = Find(match.Entry.GetType(), "GetSceneVarient", new[] { typeof(int) }).Invoke(match.Entry, new object[] { players });
        if (variant == null || (string)Get(variant, "SceneName") != level.Scene)
            throw new InvalidDataException("The native scene variant does not match the selected layout.");

        var settings = Get(session, "LevelSettings") ?? Activator.CreateInstance(levelSettingsType);
        Set(settings, "SceneDirectoryVarientEntry", variant);
        Set(session, "LevelSettings", settings);
        temporarySessionId = (int)Call(session, "GetInstanceID");
        // The temporary session must show the fourth authored threshold on the native result screen.
        var temporaryProgress = Get(progress, "SaveData");
        Set(temporaryProgress, "NewGamePlusEnabled", true);
        Set(temporaryProgress, "NewGamePlusDialogShown", true);
        var previousMode = serverGameMode.GetValue(null);
        var previousOverlay = overlay;
        ClearStandaloneRequest();
        try
        {
            serverGameMode.SetValue(null, campaignMode);
            overlay = level;
            launchFromLevelMenu = true;
            launchExpires = Environment.TickCount64 + 45_000;
            loadByIndex.Invoke(null, new[] { (object)(uint)match.Index, (uint)players, loadKitchen, runKitchen });
            log.LogInfo($"BRIDGE_STANDALONE_LAUNCH kitchen={level.Name} scene={level.Scene} index={match.Index} players={players} saveSlot={NoSaveSlot}");
            CloseLevelMenu();
        }
        catch
        {
            launchExpires = 0;
            launchFromLevelMenu = false;
            overlay = previousOverlay;
            serverGameMode.SetValue(null, previousMode);
            throw;
        }
    }

    private static bool BeforeStandaloneIsSaveable(object __instance, ref bool __result)
    {
        if (temporarySessionId == null || (int)Call(__instance, "GetInstanceID") != temporarySessionId)
            return true;
        __result = false;
        return false;
    }

    private static void BeforeTemporarySessionDestroyed(object __instance)
    {
        if (temporarySessionId != null && (int)Call(__instance, "GetInstanceID") == temporarySessionId)
            temporarySessionId = null;
    }
}
