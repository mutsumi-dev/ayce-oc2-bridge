using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private const int LevelsPerPage = 5;
    private sealed record LocalLevel(Overlay Overlay, string Path);
    private sealed record LevelCatalog(LocalLevel[] Levels, int Rejected);
    private static LevelMenuUi levelMenuUi;
    private static LevelMenuView levelMenu;

    private static LevelCatalog ReadLocalLevels(string directory, Overlay defaultLevel)
    {
        var levels = new List<LocalLevel>();
        if (defaultLevel?.Kitchen != null)
            levels.Add(new LocalLevel(defaultLevel, "active.json"));
        if (!Directory.Exists(directory))
            return new LevelCatalog(levels.ToArray(), 0);

        var rejected = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Take(100))
        {
            try
            {
                var imported = ReadOverlay(path);
                if (imported.Kitchen == null)
                    throw new InvalidDataException("The package does not include a playable kitchen layout.");
                levels.Add(new LocalLevel(imported, Path.GetFileName(path)));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                rejected++;
                log.LogWarning($"BRIDGE_MENU_LEVEL_REJECTED file={Path.GetFileName(path)} reason={error.Message}");
            }
        }
        return new LevelCatalog(levels.OrderBy(level => level.Overlay.Name, StringComparer.OrdinalIgnoreCase).ToArray(), rejected);
    }

    private static void TryInstallLevelMenu(Assembly game, Assembly unity)
    {
        var harmony = new Harmony(Id + ".level-menu");
        try
        {
            var extra = game.GetType("ExtraTrimmingsMenu", throwOnError: true);
            var main = game.GetType("MainMenu_Bagel", throwOnError: true);
            var root = game.GetType("FrontendRootMenu", throwOnError: true);
            var behaviour = game.GetType("BaseMenuBehaviour", throwOnError: true);
            var user = game.GetType("GamepadUser", throwOnError: true);
            levelMenuUi = new LevelMenuUi(game, unity);
            var show = new[] { user, behaviour, levelMenuUi.GameObject, typeof(bool) };
            Hook(harmony, Find(extra, "Show", show), nameof(AfterExtraMenuShow), postfix: true);
            Hook(harmony, Find(main, "Show", show), nameof(AfterMainMenuShow), postfix: true);
            Hook(harmony, Find(extra, "Hide", new[] { typeof(bool), typeof(bool) }), nameof(AfterExtraMenuHide), postfix: true);
            Hook(harmony, Find(extra, "ForceClose", new[] { typeof(bool) }), nameof(AfterExtraMenuForceClose), postfix: true);
            Hook(harmony, Find(root, "OnDestroy", Type.EmptyTypes), nameof(BeforeFrontendDestroyed));
            Hook(harmony, Find(levelMenuUi.Button, "OnPointerClick"), nameof(BeforeMenuPointerClick));
            Hook(harmony, Find(levelMenuUi.Button, "OnSubmit"), nameof(BeforeMenuSubmit));
            InstallLevelMenuReturnHooks(harmony, game);
            log.LogInfo("BRIDGE_MENU_READY source=ExtraTrimmings");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            log.LogWarning($"BRIDGE_MENU_DISABLED {error}");
        }
    }

    private static void AfterExtraMenuShow(object __instance, bool __result)
    {
        if (!__result)
            return;
        CancelMenuLaunch();
        CloseLevelMenu();
        try
        {
            levelMenu = new LevelMenuView(__instance, levelMenuUi);
            levelMenu.ShowEntry();
            FinishLevelMenuReturn(levelMenu);
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_MENU_ENTRY_SKIPPED {error}");
            CloseLevelMenu();
        }
    }

    private static void AfterExtraMenuHide(object __instance, bool __result)
    {
        if (__result)
            AfterExtraMenuForceClose(__instance);
    }

    private static void AfterExtraMenuForceClose(object __instance)
    {
        if (levelMenu?.ExtraId == (int)Call(__instance, "GetInstanceID"))
            CloseLevelMenu();
    }

    private static void BeforeFrontendDestroyed() => CloseLevelMenu();

    private static void AfterMainMenuShow(object __instance, bool __result)
    {
        if (__result)
        {
            CancelMenuLaunch();
            MarkLevelMenuFrontendReady(__instance);
        }
    }

    private static void CloseLevelMenu()
    {
        var view = levelMenu;
        levelMenu = null;
        try { view?.Dispose(); }
        catch (Exception error) { log.LogWarning($"BRIDGE_MENU_CLOSE_ERROR {error.Message}"); }
    }

    private static bool BeforeMenuPointerClick(object __instance, object __0)
    {
        if (levelMenu == null || !levelMenu.OwnsButton(__instance) || __0 == null || Convert.ToInt32(Get(__0, "button")) != 0)
            return true;
        return BeforeMenuSubmit(__instance);
    }

    private static bool BeforeMenuSubmit(object __instance) => levelMenu == null || !levelMenu.InvokeButton(__instance);

    private sealed class LevelMenuView : IDisposable
    {
        private readonly object extra;
        private readonly LevelMenuUi ui;
        private readonly object host;
        private readonly object eventSystem;
        private readonly object font;
        private readonly Dictionary<int, (object Button, Action Action, bool Catalog)> actions = new();
        private object entry;
        private object panel;
        private object status;
        private object nativeNeighbor;
        private object originalNavigation;
        private LevelCatalog catalog;

        internal int ExtraId { get; }

        internal LevelMenuView(object extra, LevelMenuUi ui)
        {
            this.extra = extra;
            this.ui = ui;
            ExtraId = (int)Call(extra, "GetInstanceID");
            host = ui.FindCanvas(Get(extra, "transform"));
            eventSystem = Get(extra, "CachedEventSystem");
            font = ui.CreateFont();
        }

        internal void ShowEntry()
        {
            entry = AddButton(host, "自定义关卡", .68f, .15f, .93f, .22f, OpenCatalog, inCatalog: false);
            var nativeButtons = (IEnumerable)Get(extra, "m_Buttons");
            nativeNeighbor = nativeButtons.Cast<object>().LastOrDefault(button => button != null && (bool)Get(button, "isActiveAndEnabled"));
            if (nativeNeighbor == null)
                return;
            originalNavigation = Get(nativeNeighbor, "navigation");
            var newNavigation = Get(nativeNeighbor, "navigation");
            Set(newNavigation, "mode", ui.ExplicitNavigation);
            Set(newNavigation, "selectOnUp", Call(nativeNeighbor, "FindSelectableOnUp"));
            Set(newNavigation, "selectOnLeft", Call(nativeNeighbor, "FindSelectableOnLeft"));
            Set(newNavigation, "selectOnRight", Call(nativeNeighbor, "FindSelectableOnRight"));
            Set(newNavigation, "selectOnDown", entry);
            Set(nativeNeighbor, "navigation", newNavigation);
            ui.Connect(entry, nativeNeighbor, nativeNeighbor);
        }

        internal bool InvokeButton(object button)
        {
            if (!actions.TryGetValue((int)Call(button, "GetInstanceID"), out var action))
                return false;
            try { action.Action(); }
            catch (Exception error)
            {
                log.LogWarning($"BRIDGE_MENU_ACTION_FAILED {error}");
                if (status != null)
                    Set(status, "text", "打开失败，请返回后重试。");
            }
            return true;
        }

        internal bool OwnsButton(object button) => actions.ContainsKey((int)Call(button, "GetInstanceID"));

        internal void OpenCatalog()
        {
            if (panel != null)
                return;
            catalog = ReadLocalLevels(Path.Combine(Paths.PluginPath, "AyceOc2Bridge", "levels"), installedOverlay);
            ShowPage(0);
            log.LogInfo($"BRIDGE_MENU_OPEN levels={catalog.Levels.Length} rejected={catalog.Rejected}");
        }

        private void ShowPage(int page)
        {
            RemovePanel();
            ui.Activate(Get(entry, "gameObject"), false);
            panel = ui.Node(host, "Custom levels", 0f, 0f, 1f, 1f);
            ui.Paint(panel, .05f, .08f, .12f, .86f);
            var content = ui.Node(Get(panel, "transform"), "Local level list", .21f, .12f, .79f, .88f);
            ui.Paint(content, .09f, .15f, .21f, 1f);
            var body = Get(content, "transform");
            ui.Label(body, font, "自定义关卡", .07f, .84f, .93f, .97f, 36, centered: false);
            status = ui.Label(body, font, "离线游玩。选择已解锁凯文 2 的 OC2 存档。", .07f, .76f, .93f, .86f, 22, centered: false);
            if (catalog.Rejected != 0)
                Set(status, "text", $"离线游玩。需要已解锁凯文 2 的存档。{catalog.Rejected} 个关卡包未通过检查。");

            var visible = new List<object>();
            var rows = catalog.Levels.Skip(page * LevelsPerPage).Take(LevelsPerPage).ToArray();
            for (var index = 0; index < rows.Length; index++)
            {
                var level = rows[index];
                var author = level.Overlay.Provenance?.Author;
                var release = level.Overlay.Provenance?.Version;
                var details = string.Join("  ·  ", new[] { author, release, level.Path }.Where(value => !string.IsNullOrWhiteSpace(value)));
                var label = $"{Limit(level.Overlay.Name, 52)}\n{Limit(details, 90)}";
                var top = .735f - index * .102f;
                visible.Add(AddButton(body, label, .07f, top - .089f, .93f, top, () => SelectLevel(level)));
            }
            if (rows.Length == 0)
                ui.Label(body, font, "没有可玩的本地关卡。", .07f, .55f, .93f, .7f, 24, centered: false);
            if (page > 0)
                visible.Add(AddButton(body, "上一页", .07f, .075f, .23f, .16f, () => ShowPage(page - 1)));
            if ((page + 1) * LevelsPerPage < catalog.Levels.Length)
                visible.Add(AddButton(body, "下一页", .26f, .075f, .42f, .16f, () => ShowPage(page + 1)));
            visible.Add(AddButton(body, "返回", .73f, .075f, .93f, .16f, ReturnToExtras));
            for (var index = 0; index < visible.Count; index++)
                ui.Connect(visible[index], visible[(index + visible.Count - 1) % visible.Count], visible[(index + 1) % visible.Count]);
            Focus(visible[0]);
        }

        private static string Limit(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

        private object AddButton(object parent, string title, float left, float bottom, float right, float top,
            Action action, bool inCatalog = true)
        {
            var button = ui.CreateButton(parent, font, title, left, bottom, right, top);
            ui.AddSelectable.Invoke(extra, new[] { button });
            actions.Add((int)Call(button, "GetInstanceID"), (button, action, inCatalog));
            return button;
        }

        private void SelectLevel(LocalLevel level)
        {
            if ((bool)isSteamLoggedOn.Invoke(null, null))
            {
                Set(status, "text", "请将 Steam 切换为离线模式，再选择关卡。");
                return;
            }
            try
            {
                ui.OpenSaveSelection(Get(extra, "m_frontendRootMenu"));
                QueueMenuLaunch(level.Overlay);
                log.LogInfo($"BRIDGE_MENU_SELECTED kitchen={level.Overlay.Name} path={level.Path}");
                CloseLevelMenu();
            }
            catch
            {
                CancelMenuLaunch();
                throw;
            }
        }

        private void ReturnToExtras()
        {
            RemovePanel();
            ui.Activate(Get(entry, "gameObject"), true);
            Focus(entry);
        }

        private void Focus(object button)
        {
            if (eventSystem != null)
                ui.Focus.Invoke(eventSystem, new[] { Get(button, "gameObject") });
        }

        private void RemovePanel()
        {
            RemoveActions(catalogOnly: true);
            if (panel != null)
                ui.Delete(panel);
            panel = null;
            status = null;
        }

        private void RemoveActions(bool catalogOnly)
        {
            var allowed = Get(extra, "m_AllowedSelectables");
            var remove = allowed == null ? null : Find(allowed.GetType(), "Remove", new[] { ui.Selectable });
            foreach (var (id, item) in actions.ToArray())
            {
                if (catalogOnly && !item.Catalog)
                    continue;
                remove?.Invoke(allowed, new[] { item.Button });
                actions.Remove(id);
            }
        }

        public void Dispose()
        {
            try
            {
                if (nativeNeighbor != null && originalNavigation != null)
                    Set(nativeNeighbor, "navigation", originalNavigation);
                RemoveActions(catalogOnly: false);
            }
            finally
            {
                if (panel != null)
                    ui.Delete(panel);
                if (entry != null)
                    ui.Delete(Get(entry, "gameObject"));
                ui.ReleaseFont(font);
                actions.Clear();
            }
        }
    }
}
