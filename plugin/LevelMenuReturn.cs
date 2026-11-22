using System;
using System.Reflection;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private enum MenuReturn
    {
        None,
        WorldMap,
        Frontend,
        OpenCatalog,
        OpeningExtras,
    }

    private static bool launchFromLevelMenu;
    private static bool customRoundFromMenu;
    private static bool customRoundCompleted;
    private static MethodInfo quitCampaign;
    private static MenuReturn menuReturn;
    private static long menuReturnDeadline;
    private static long nextReturnStep;
    private static int mainMenuId;

    private static void InstallLevelMenuReturnHooks(Harmony harmony, Assembly game)
    {
        quitCampaign = Find(game.GetType("GameUtils", throwOnError: true), "QuitLevel", Type.EmptyTypes);
        foreach (var mode in new[] { "GameModes.ClientCampaignMode", "GameModes.ServerCampaignMode" })
            Hook(harmony, Find(game.GetType(mode, throwOnError: true), "OnOutro"), nameof(BeforeCustomOutro));
        var frontend = game.GetType("FrontendMenuBehaviour", throwOnError: true);
        Hook(harmony, Find(frontend, "Update", Type.EmptyTypes), nameof(AfterFrontendUpdate), postfix: true);
    }

    private static void BeforeCustomOutro()
    {
        if (customRun && customRoundFromMenu)
            customRoundCompleted = true;
    }

    private static void BeginLevelMenuReturn()
    {
        if (!customRun || !customRoundFromMenu || !customRoundCompleted)
            return;
        menuReturn = MenuReturn.WorldMap;
        menuReturnDeadline = Environment.TickCount64 + 90_000;
        nextReturnStep = 0;
        log.LogInfo("BRIDGE_MENU_RETURN_REQUESTED");
    }

    private static bool AdvanceLevelMenuReturn(object map)
    {
        if (menuReturn != MenuReturn.WorldMap || MenuReturnExpired())
            return false;
        var now = Environment.TickCount64;
        if (now < nextReturnStep)
            return true;
        nextReturnStep = now + 500;
        try
        {
            if (!(bool)allUsersInMap.Invoke(null, new[] { inMap }) || Get(map, "m_runIntro") != null || (bool)anyOpenDialogs.Invoke(null, null))
                return true;

            menuReturn = MenuReturn.Frontend;
            quitCampaign.Invoke(null, null);
            log.LogInfo("BRIDGE_MENU_RETURN_LEAVING_CAMPAIGN");
        }
        catch (Exception error)
        {
            menuReturn = MenuReturn.None;
            log.LogWarning($"BRIDGE_MENU_RETURN_FAILED {error}");
        }
        return true;
    }

    private static void MarkLevelMenuFrontendReady(object main)
    {
        if (menuReturn != MenuReturn.Frontend || MenuReturnExpired())
            return;
        mainMenuId = (int)Call(main, "GetInstanceID");
        menuReturn = MenuReturn.OpenCatalog;
        nextReturnStep = Environment.TickCount64 + 300;
    }

    private static void AfterFrontendUpdate(object __instance)
    {
        if (menuReturn == MenuReturn.None || MenuReturnExpired() || menuReturn != MenuReturn.OpenCatalog ||
            Environment.TickCount64 < nextReturnStep || (int)Call(__instance, "GetInstanceID") != mainMenuId)
            return;
        try
        {
            menuReturn = MenuReturn.OpeningExtras;
            levelMenuUi.OpenExtraContent(__instance);
        }
        catch (Exception error)
        {
            menuReturn = MenuReturn.None;
            log.LogWarning($"BRIDGE_MENU_RETURN_FAILED {error}");
        }
    }

    private static void FinishLevelMenuReturn(LevelMenuView view)
    {
        if (menuReturn != MenuReturn.OpeningExtras)
            return;
        menuReturn = MenuReturn.None;
        view.OpenCatalog();
        log.LogInfo("BRIDGE_MENU_RETURN_COMPLETE");
    }

    private static bool MenuReturnExpired()
    {
        if (Environment.TickCount64 < menuReturnDeadline)
            return false;
        log.LogWarning($"BRIDGE_MENU_RETURN_TIMED_OUT stage={menuReturn}");
        menuReturn = MenuReturn.None;
        return true;
    }
}
