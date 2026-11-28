using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AyceOc2Bridge;

[BepInPlugin(Id, "AYCE OC2 Bridge", "0.8.7")]
public sealed partial class Plugin : BasePlugin
{
    internal const string Id = "dev.ayce.oc2bridge";
    private const string SupportedBuild = "22894075";
    private const string SupportedHash = "494157d1daff4f8144acd0a4fd64add092281992abbcb0cdf5f462f0c747b42b";
    private const string SushiScene = "s_sushi_1_1";
    private const string KevinScene = "s_city_h2";
    private static ManualLogSource log;
    private static Overlay overlay;
    private static Overlay installedOverlay;
    private static bool applyOverlay;
    private static string SceneNameToWatch => applyOverlay ? overlay.Scene : SushiScene;
    private static MethodInfo isSteamLoggedOn;
    private static MethodInfo syncPhysics;
    private static MethodInfo getMeshFilter;
    private static MethodInfo getMeshRenderer;
    private static Type vectorType;
    private static Type rotationType;
    private static int? sceneHandle;
    private static int? refusedHandle;
    private static OrderRecipe[] allowedOrders = Array.Empty<OrderRecipe>();
    private static int nextReplacement;
    private static OrderRecipe[] shuffledOrders = Array.Empty<OrderRecipe>();
    private static Dictionary<int, WorktopHighlight> worktopHighlights = new();
    private static readonly HashSet<int> ReportedWorktops = new();

    private sealed record Placement(StationMove Move, object Transform, object Position, object Rotation, object BakedWorktop);
    private sealed record WorktopHighlight(object Renderer, string Source, string Destination);
    private sealed record OrderRecipe(object Entry, string Name);

    public override void Load()
    {
        log = Log;
        var enabled = Config.Bind("Bridge", "ApplyOverlay", false, "Apply the pinned test overlay in Steam Offline Mode.");
        try
        {
            var path = Path.Combine(Paths.PluginPath, "AyceOc2Bridge", "active.json");
            using var dll = File.OpenRead(Path.Combine(Paths.GameRootPath, "GameAssembly.dll"));
            using var sha = SHA256.Create();
            var currentHash = Convert.ToHexString(sha.ComputeHash(dll));
            if (!SupportedHash.Equals(currentHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("GameAssembly.dll is from an unsupported AYCE build.");
            if (File.Exists(path))
                overlay = installedOverlay = ReadOverlay(path);
            applyOverlay = enabled.Value && overlay != null;

            var game = Assembly.Load("Assembly-CSharp");
            var unity = Assembly.Load("UnityEngine.CoreModule");
            vectorType = unity.GetType("UnityEngine.Vector3", throwOnError: true);
            rotationType = unity.GetType("UnityEngine.Quaternion", throwOnError: true);
            var grid = game.GetType("StaticGridLocation", throwOnError: true);
            var loader = game.GetType("KitchenLoaderManager", throwOnError: true);
            var round = game.GetType("RoundData", throwOnError: true);
            var store = game.GetType("ClientOrderControllerBase", throwOnError: true);
            if (applyOverlay)
            {
                var steam = Assembly.Load("Assembly-CSharp-firstpass").GetType("Steamworks.SteamUser", throwOnError: true);
                isSteamLoggedOn = Find(steam, "BLoggedOn", Type.EmptyTypes);
            }
            if (applyOverlay && overlay.Moves.Length > 0)
            {
                var physics = Assembly.Load("UnityEngine.PhysicsModule").GetType("UnityEngine.Physics", throwOnError: true);
                syncPhysics = Find(physics, "SyncTransforms", Type.EmptyTypes);
                var gameObject = unity.GetType("UnityEngine.GameObject", throwOnError: true);
                var getComponent = gameObject.GetMethods().Single(m => m.Name == "GetComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                getMeshFilter = getComponent.MakeGenericMethod(unity.GetType("UnityEngine.MeshFilter", throwOnError: true));
                getMeshRenderer = getComponent.MakeGenericMethod(unity.GetType("UnityEngine.MeshRenderer", throwOnError: true));
            }

            var harmony = new Harmony(Id);
            try
            {
                Hook(harmony, Find(grid, "Awake", Type.EmptyTypes), nameof(BeforeGridAwake));
                Hook(harmony, Find(loader, "OnDestroy", Type.EmptyTypes), nameof(BeforeKitchenDestroyed));
                Hook(harmony, Find(round, "InitialiseRound", Type.EmptyTypes), nameof(BeforeRound));
                Hook(harmony, Find(store, "AddNewOrder"), nameof(AfterOrder), postfix: true);
                Hook(harmony, Find(store, "OnFoodDelivered"), nameof(AfterDelivery), postfix: true);
                if (applyOverlay)
                {
                    if (overlay.CampaignRules != null)
                        InstallCustomCampaignHooks(harmony, game, unity);
                    if (overlay.HasKitchen)
                    {
                        InstallKitchenHooks(harmony, game, unity);
                        TryInstallStockKitchenHooks(game, unity);
                    }
                    if (overlay.Moves.Length > 0)
                    {
                        var highlighter = game.GetType("ClientAnticipateInteractionHighlight", throwOnError: true);
                        Hook(harmony, Find(highlighter, "SaveRenderersStartingSettings", Type.EmptyTypes), nameof(BeforeSaveHighlightMaterials));
                    }
                    if (overlay.Recipes.Length > 0)
                    {
                        var scripted = game.GetType("ScriptedRoundData", throwOnError: true);
                        var roundState = game.GetType("RoundInstanceDataBase", throwOnError: true);
                        Hook(harmony, Find(round, "GetNextRecipe", new[] { roundState }), nameof(AfterRecipeSelection), postfix: true);
                        Hook(harmony, Find(scripted, "GetNextRecipe", new[] { roundState }), nameof(AfterRecipeSelection), postfix: true);
                        var server = game.GetType("ServerOrderControllerBase", throwOnError: true);
                        var entry = game.GetType("RecipeList+Entry", throwOnError: true);
                        Hook(harmony, Find(server, "AddNewOrder", new[] { entry }), nameof(TraceServerOrder));
                    }
                }
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
            log.LogInfo($"BRIDGE_READY mode={(applyOverlay ? "overlay" : "observe")} build={SupportedBuild} scene={SceneNameToWatch}");
            if (applyOverlay && installedOverlay.HasKitchen)
                TryInstallLevelMenu(game, unity);
        }
        catch (Exception error)
        {
            log.LogError($"BRIDGE_DISABLED {error}");
        }
    }

    private static Overlay ReadOverlay(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length > 512 * 1024)
            throw new InvalidDataException("Overlay exceeds 512 KiB.");
        var value = JsonSerializer.Deserialize<Overlay>(file);
        Validate(value);
        return value;
    }

    private static void Validate(Overlay value)
    {
        if (value == null || value.Version != 1 || value.Build != SupportedBuild || value.Scene is not (SushiScene or KevinScene) ||
            !SupportedHash.Equals(value.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Overlay schema, target scene or game fingerprint is incompatible.");
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120 || value.Moves == null || value.Moves.Length > 26 ||
            (value.Scene == KevinScene && value.Moves.Length != 0))
            throw new InvalidDataException("Invalid overlay name or station count.");
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var move in value.Moves)
        {
            if (move?.Path == null || move.Path.Length > 160 || !paths.Add(move.Path) || !IsSupportedStation(move.Path) ||
                !ValidVector(move.Original, 3) || !ValidVector(move.Position, 3) || !ValidVector(move.Rotation, 4) ||
                Math.Abs(move.Original[1] - move.Position[1]) > .05f || DistanceSquared(move.Original, move.Position) > 100f ||
                Math.Abs(move.Rotation.Sum(v => v * v) - 1f) > .005f)
                throw new InvalidDataException($"Invalid move for {move?.Path}.");
        }
        if (value.Recipes == null || value.Recipes.Length > (value.Stock == null ? 30 : 100) || (value.Moves.Length == 0 && value.Recipes.Length == 0) ||
            value.Recipes.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 225) ||
            value.Recipes.Distinct(StringComparer.Ordinal).Count() != value.Recipes.Length)
            throw new InvalidDataException("Invalid recipe allowlist.");
        ValidateCampaignRules(value);
        ValidateKitchen(value);
        ValidateStockKitchen(value);
    }

    private static bool IsSupportedStation(string path)
    {
        const string stations = "Design/Gameplay Stations/";
        const string work = "Design/Work Surfaces/";
        if (path.Count(c => c == '/') != 2)
            return false;
        return path.StartsWith(stations + "ChoppingBoard_", StringComparison.Ordinal) ||
            path.Equals(stations + "Bin 1", StringComparison.Ordinal) ||
            path.StartsWith(work + "countertop_", StringComparison.Ordinal);
    }

    private static bool ValidVector(float[] value, int length) => value?.Length == length && value.All(x => float.IsFinite(x) && Math.Abs(x) < 1000);

    private static float DistanceSquared(float[] a, float[] b) => a.Zip(b, (x, y) => (x - y) * (x - y)).Sum();

    private static void Hook(Harmony harmony, MethodInfo method, string patch, bool postfix = false)
    {
        var hook = new HarmonyMethod(Find(typeof(Plugin), patch));
        harmony.Patch(method, prefix: postfix ? null : hook, postfix: postfix ? hook : null);
    }

    private static MethodInfo Find(Type owner, string method, Type[] args = null) =>
        AccessTools.Method(owner, method, args) ?? throw new MissingMethodException(owner.FullName, method);

    private static object Get(object owner, string member)
    {
        var type = owner.GetType();
        return AccessTools.Property(type, member) is PropertyInfo property
            ? property.GetValue(owner)
            : (AccessTools.Field(type, member) ?? throw new MissingMemberException(type.FullName, member)).GetValue(owner);
    }

    private static object Call(object owner, string name) => Find(owner.GetType(), name, Type.EmptyTypes).Invoke(owner, null);

    private static object SceneOf(object component) => Get(Get(component, "gameObject"), "scene");

    private static int Handle(object scene) => (int)Get(scene, "handle");

    private static string SceneName(object scene) => (string)Get(scene, "name");

    private static void BeforeGridAwake(object __instance)
    {
        if (sceneHandle != null || applyingKitchen)
            return;
        try
        {
            var scene = SceneOf(__instance);
            if (SceneName(scene) != SceneNameToWatch || refusedHandle == Handle(scene))
                return;
            if (applyOverlay && overlay.CampaignRules != null && !CustomLaunchPending)
                return;
            if (applyOverlay && (bool)isSteamLoggedOn.Invoke(null, null))
                throw new InvalidOperationException("Switch Steam to Offline Mode before testing; no overlay has been applied.");
            if (applyOverlay && overlay.HasKitchen)
                ApplyKitchenOnce(scene);
            if (applyOverlay && overlay.Moves.Length > 0)
                ApplyMoves(scene);
            sceneHandle = Handle(scene);
            if (applyOverlay && overlay.CampaignRules != null)
                BeginCustomRun();
            log.LogInfo($"BRIDGE_SCENE scene={SceneNameToWatch} mode={(applyOverlay ? "overlay" : "observe")} moves={(applyOverlay ? overlay.Moves.Length : 0)}");
        }
        catch (Exception error)
        {
            try { refusedHandle = Handle(SceneOf(__instance)); } catch { }
            log.LogError($"BRIDGE_SCENE_REJECTED {error}");
        }
    }

    private static void ApplyMoves(object scene)
    {
        var roots = (IEnumerable)Call(scene, "GetRootGameObjects");
        var rootGroups = roots.Cast<object>().ToLookup(o => (string)Get(o, "name"), StringComparer.Ordinal);
        var plans = new List<Placement>();
        foreach (var move in overlay.Moves)
        {
            var parts = move.Path.Split('/');
            var root = rootGroups[parts[0]].Single();
            var tf = Get(root, "transform");
            var find = Find(tf.GetType(), "Find", new[] { typeof(string) });
            var transform = find.Invoke(tf, new object[] { string.Join('/', parts.Skip(1)) })
                ?? throw new InvalidDataException($"Missing scene object: {move.Path}");
            var position = Get(transform, "position");
            var rotation = Get(transform, "rotation");
            var actual = new[] { (float)Get(position, "x"), (float)Get(position, "y"), (float)Get(position, "z") };
            if (DistanceSquared(actual, move.Original) > .0025f)
                throw new InvalidDataException($"Scene object origin changed: {move.Path} ({string.Join(", ", actual)})");
            if (plans.Any(p => DistanceSquared(p.Move.Position, move.Position) < .25f))
                throw new InvalidDataException($"Station destination overlaps another move: {move.Path}");
            plans.Add(new Placement(move, transform, position, rotation, FindBakedWorktop(transform)));
        }

        var remaps = BuildHighlightBindings(plans);

        var attempted = 0;
        try
        {
            foreach (var plan in plans)
            {
                var position = Activator.CreateInstance(vectorType, plan.Move.Position.Cast<object>().ToArray());
                var rotation = Activator.CreateInstance(rotationType, plan.Move.Rotation.Cast<object>().ToArray());
                attempted++;
                SetTransform(plan.Transform, position, rotation);
            }
            syncPhysics.Invoke(null, null);
        }
        catch
        {
            for (var index = attempted - 1; index >= 0; index--)
            {
                var plan = plans[index];
                try { SetTransform(plan.Transform, plan.Position, plan.Rotation); }
                catch (Exception error) { log.LogError($"BRIDGE_ROLLBACK_ERROR {error}"); }
            }
            try { syncPhysics.Invoke(null, null); } catch { }
            throw;
        }
        worktopHighlights = remaps;
        foreach (var plan in plans)
            log.LogInfo($"BRIDGE_MOVE {plan.Move.Path} -> {string.Join(", ", plan.Move.Position)}");
    }

    private static object FindBakedWorktop(object transform)
    {
        var find = Find(transform.GetType(), "Find", new[] { typeof(string) });
        var meshTransform = find.Invoke(transform, new object[] { "Worktop/Mesh" });
        if (meshTransform == null)
            return null;
        var gameObject = Get(meshTransform, "gameObject");
        var filter = getMeshFilter.Invoke(gameObject, null);
        var mesh = filter == null ? null : Get(filter, "sharedMesh");
        if (mesh == null || !((string)Get(mesh, "name")).StartsWith("Combined Mesh", StringComparison.Ordinal))
            return null;
        return getMeshRenderer.Invoke(gameObject, null)
            ?? throw new InvalidDataException("A baked worktop is missing its mesh renderer.");
    }

    private static Dictionary<int, WorktopHighlight> BuildHighlightBindings(IReadOnlyList<Placement> plans)
    {
        var bindings = new Dictionary<int, WorktopHighlight>();
        foreach (var source in plans)
        {
            if (source.BakedWorktop == null || DistanceSquared(source.Move.Original, source.Move.Position) < .0025f)
                continue;
            // Unity draws these batched submeshes in their original world positions.
            var destination = plans.SingleOrDefault(p => DistanceSquared(p.Move.Original, source.Move.Position) < .0025f);
            if (destination?.BakedWorktop == null)
                throw new InvalidDataException($"Baked worktop requires another moved worktop at the destination: {source.Move.Path}");
            var rendererId = (int)Call(source.BakedWorktop, "GetInstanceID");
            bindings.Add(rendererId, new WorktopHighlight(destination.BakedWorktop, source.Move.Path, destination.Move.Path));
        }
        return bindings;
    }

    private static void BeforeSaveHighlightMaterials(object __instance)
    {
        if (sceneHandle == null || worktopHighlights.Count == 0)
            return;

        try
        {
            // The game has just rebuilt this list and will cache the materials from it next.
            var list = Get(__instance, "m_renderers");
            var count = (int)Get(list, "Count");
            var indexer = list.GetType().GetProperty("Item")
                ?? throw new MissingMemberException(list.GetType().FullName, "Item");
            for (var i = 0; i < count; i++)
            {
                var index = new object[] { i };
                var renderer = indexer.GetValue(list, index);
                if (renderer == null)
                    continue;
                var id = (int)Call(renderer, "GetInstanceID");
                if (!worktopHighlights.TryGetValue(id, out var binding))
                    continue;
                indexer.SetValue(list, binding.Renderer, index);
                if (ReportedWorktops.Add(id))
                    log.LogInfo($"BRIDGE_HIGHLIGHT_REMAP {binding.Source} -> {binding.Destination}");
            }
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_HIGHLIGHT_SKIPPED {error}");
        }
    }

    private static void SetTransform(object transform, object position, object rotation) =>
        Find(transform.GetType(), "SetPositionAndRotation", new[] { vectorType, rotationType }).Invoke(transform, new[] { position, rotation });

    private static void BeforeRound(object __instance)
    {
        if (sceneHandle == null)
            return;
        ResetOrderSelection();
        try
        {
            if (overlay?.Stock != null)
            {
                RepairStockGrid(activate: true);
                ApplyStockOrders();
                return;
            }
            var recipeList = Get(__instance, "m_recipes");
            var entries = Get(recipeList, "m_recipes");
            var candidates = ((IEnumerable)entries).Cast<object>().Select(entry => new OrderRecipe(entry, RecipeName(entry))).ToArray();
            log.LogInfo($"BRIDGE_RECIPES {string.Join(" | ", candidates.Select(x => x.Name))}");
            if (!applyOverlay || overlay.Recipes.Length == 0)
                return;
            if (!overlay.Recipes.All(name => candidates.Any(row => row.Name == name)))
                throw new InvalidDataException("Requested recipe is not in this round's original AYCE recipe pool.");
            allowedOrders = overlay.Recipes.Select(name => candidates.First(row => row.Name == name)).ToArray();
            log.LogInfo($"BRIDGE_RECIPE_FILTER {string.Join(" | ", overlay.Recipes)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_RECIPE_SKIPPED {error.Message}");
        }
    }

    private static string RecipeName(object entry) => (string)Get(Get(entry, "m_order"), "name");

    private static void AfterRecipeSelection(object __result)
    {
        if (sceneHandle == null || allowedOrders.Length == 0 || __result == null)
            return;
        try
        {
            // Both selectors return a new native array. Editing its entries avoids rewriting native call arguments.
            var count = Convert.ToInt32(Get(__result, "Length"));
            var indexer = FindIndexer(__result);
            var indexType = indexer.GetIndexParameters().Single().ParameterType;
            for (var i = 0; i < count; i++)
            {
                var index = new[] { Convert.ChangeType(i, indexType) };
                var requested = RecipeName(indexer.GetValue(__result, index));
                var randomize = customRun && overlay?.HasKitchen == true;
                if (!randomize && allowedOrders.Any(recipe => recipe.Name == requested))
                    continue;
                var replacement = randomize ? DrawCustomOrder() : allowedOrders[nextReplacement];
                indexer.SetValue(__result, replacement.Entry, index);
                var selected = RecipeName(indexer.GetValue(__result, index));
                if (selected != replacement.Name)
                    throw new InvalidOperationException("The native selection array did not keep the replacement.");
                if (!randomize)
                    nextReplacement = (nextReplacement + 1) % allowedOrders.Length;
                log.LogInfo($"BRIDGE_RECIPE_SELECTED requested={requested} selected={selected}");
            }
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_RECIPE_SELECTION_SKIPPED {error}");
        }
    }

    private static OrderRecipe DrawCustomOrder()
    {
        if (nextReplacement == 0)
        {
            var previous = shuffledOrders.LastOrDefault();
            shuffledOrders = (OrderRecipe[])allowedOrders.Clone();
            for (int i = shuffledOrders.Length - 1; i > 0; i--)
            {
                var j = Random.Shared.Next(i + 1);
                (shuffledOrders[i], shuffledOrders[j]) = (shuffledOrders[j], shuffledOrders[i]);
            }
            if (shuffledOrders.Length > 1 && shuffledOrders[0] == previous)
            {
                var j = Random.Shared.Next(1, shuffledOrders.Length);
                (shuffledOrders[0], shuffledOrders[j]) = (shuffledOrders[j], shuffledOrders[0]);
            }
        }
        var selected = shuffledOrders[nextReplacement];
        nextReplacement = (nextReplacement + 1) % shuffledOrders.Length;
        return selected;
    }

    private static void ResetOrderSelection()
    {
        allowedOrders = Array.Empty<OrderRecipe>();
        shuffledOrders = Array.Empty<OrderRecipe>();
        nextReplacement = 0;
    }

    private static PropertyInfo FindIndexer(object collection) => collection.GetType().GetProperty("Item")
        ?? throw new MissingMemberException(collection.GetType().FullName, "Item");

    private static void TraceServerOrder(object __0)
    {
        if (sceneHandle == null || allowedOrders.Length == 0)
            return;
        try
        {
            var name = RecipeName(__0);
            log.LogInfo($"BRIDGE_SERVER_ORDER recipe={name} allowed={allowedOrders.Any(recipe => recipe.Name == name)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_SERVER_ORDER_TRACE_SKIPPED {error.Message}");
        }
    }

    private static void AfterOrder(object __instance)
    {
        if (sceneHandle == null)
            return;
        log.LogInfo("BRIDGE_ORDER_CREATED");
        if (allowedOrders.Length == 0)
            return;
        try
        {
            var active = Get(__instance, "m_activeOrders");
            var count = (int)Get(active, "Count");
            if (count == 0)
                return;
            var last = FindIndexer(active).GetValue(active, new object[] { count - 1 });
            var name = RecipeName(Get(last, "RecipeListEntry"));
            log.LogInfo($"BRIDGE_CLIENT_ORDER recipe={name} allowed={allowedOrders.Any(recipe => recipe.Name == name)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_CLIENT_ORDER_TRACE_SKIPPED {error.Message}");
        }
    }

    private static void AfterDelivery(bool __0)
    {
        if (sceneHandle != null)
            log.LogInfo($"BRIDGE_DELIVERY accepted={__0}");
    }

    private static void BeforeKitchenDestroyed(object __instance)
    {
        try
        {
            var handle = Handle(SceneOf(__instance));
            var restart = customRestart;
            var restarting = sceneHandle == handle && restart?.SceneHandle == handle && Environment.TickCount64 < restart.Deadline;
            var wasCustomKitchen = failedKitchenHandle == handle || kitchenHandle == handle;
            if (refusedHandle == handle)
                refusedHandle = null;
            if (failedKitchenHandle == handle)
            {
                failedKitchenHandle = null;
                ReleaseStockKitchen();
            }
            if (kitchenHandle == handle)
            {
                kitchenHandle = null;
                kitchenProps.Clear();
                ClearKitchenRules();
                if (restarting)
                    ReleaseStockRoundAssets();
                else
                    ReleaseStockKitchen();
            }
            if (sceneHandle != handle)
            {
                if (wasCustomKitchen)
                    overlay = installedOverlay ?? overlay;
                return;
            }
            ResetOrderSelection();
            worktopHighlights.Clear();
            ReportedWorktops.Clear();
            sceneHandle = null;
            if (restarting)
            {
                if (customRoundCompleted)
                    PersistLocalScore();
                EndCustomRun();
                launchFromLevelMenu = restart.FromMenu;
                launchExpires = restart.Deadline;
                return;
            }
            customRestart = null;
            BeginLevelMenuReturn();
            overlay = installedOverlay ?? overlay;
            EndCustomRun();
            log.LogInfo("BRIDGE_SCENE_EXIT");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_SCENE_EXIT_ERROR {error.Message}");
        }
    }
}
