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

[BepInPlugin(Id, "AYCE OC2 Bridge", "0.1.0")]
public sealed class Plugin : BasePlugin
{
    internal const string Id = "dev.ayce.oc2bridge";
    private const string SupportedBuild = "22894075";
    private const string SupportedHash = "494157d1daff4f8144acd0a4fd64add092281992abbcb0cdf5f462f0c747b42b";
    private const string SupportedScene = "s_sushi_1_1";
    private static ManualLogSource log;
    private static Overlay overlay;
    private static bool applyOverlay;
    private static MethodInfo isSteamLoggedOn;
    private static MethodInfo syncPhysics;
    private static Type vectorType;
    private static Type rotationType;
    private static int? sceneHandle;
    private static int? refusedHandle;
    private static readonly List<(object Owner, object Original)> RecipeChanges = new();

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
            {
                if (new FileInfo(path).Length > 512 * 1024)
                    throw new InvalidDataException("active.json exceeds 512 KiB.");
                overlay = JsonSerializer.Deserialize<Overlay>(File.ReadAllBytes(path));
                Validate(overlay);
            }
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
                var physics = Assembly.Load("UnityEngine.PhysicsModule").GetType("UnityEngine.Physics", throwOnError: true);
                syncPhysics = Find(physics, "SyncTransforms", Type.EmptyTypes);
            }

            var harmony = new Harmony(Id);
            try
            {
                Hook(harmony, Find(grid, "Awake", Type.EmptyTypes), nameof(BeforeGridAwake));
                Hook(harmony, Find(loader, "OnDestroy", Type.EmptyTypes), nameof(BeforeKitchenDestroyed));
                Hook(harmony, Find(round, "InitialiseRound", Type.EmptyTypes), nameof(BeforeRound));
                Hook(harmony, Find(store, "AddNewOrder"), nameof(AfterOrder), postfix: true);
                Hook(harmony, Find(store, "OnFoodDelivered"), nameof(AfterDelivery), postfix: true);
            }
            catch
            {
                harmony.UnpatchSelf();
                throw;
            }
            log.LogInfo($"BRIDGE_READY mode={(applyOverlay ? "overlay" : "observe")} build={SupportedBuild} scene={SupportedScene}");
        }
        catch (Exception error)
        {
            log.LogError($"BRIDGE_DISABLED {error}");
        }
    }

    private static void Validate(Overlay value)
    {
        if (value == null || value.Version != 1 || value.Build != SupportedBuild || value.Scene != SupportedScene ||
            !SupportedHash.Equals(value.Hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Overlay schema, target scene or game fingerprint is incompatible.");
        if (string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 120 || value.Moves == null || value.Moves.Length is < 1 or > 26)
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
        if (value.Recipes == null || value.Recipes.Length > 30 || value.Recipes.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 150) ||
            value.Recipes.Distinct(StringComparer.Ordinal).Count() != value.Recipes.Length)
            throw new InvalidDataException("Invalid recipe allowlist.");
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

    private static void Put(object owner, string member, object value)
    {
        var type = owner.GetType();
        if (AccessTools.Property(type, member) is PropertyInfo property)
            property.SetValue(owner, value);
        else
            (AccessTools.Field(type, member) ?? throw new MissingMemberException(type.FullName, member)).SetValue(owner, value);
    }

    private static object Call(object owner, string name) => Find(owner.GetType(), name, Type.EmptyTypes).Invoke(owner, null);

    private static object SceneOf(object component) => Get(Get(component, "gameObject"), "scene");

    private static int Handle(object scene) => (int)Get(scene, "handle");

    private static string SceneName(object scene) => (string)Get(scene, "name");

    private static void BeforeGridAwake(object __instance)
    {
        if (sceneHandle != null)
            return;
        try
        {
            var scene = SceneOf(__instance);
            if (SceneName(scene) != SupportedScene || refusedHandle == Handle(scene))
                return;
            if (applyOverlay && (bool)isSteamLoggedOn.Invoke(null, null))
                throw new InvalidOperationException("Switch Steam to Offline Mode before testing; no overlay has been applied.");
            if (applyOverlay)
                ApplyMoves(scene);
            sceneHandle = Handle(scene);
            log.LogInfo($"BRIDGE_SCENE scene={SupportedScene} mode={(applyOverlay ? "overlay" : "observe")} moves={(applyOverlay ? overlay.Moves.Length : 0)}");
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
        var plans = new List<(StationMove Move, object Transform, object Position, object Rotation)>();
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
            plans.Add((move, transform, position, rotation));
        }

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
        foreach (var plan in plans)
            log.LogInfo($"BRIDGE_MOVE {plan.Move.Path} -> {string.Join(", ", plan.Move.Position)}");
    }

    private static void SetTransform(object transform, object position, object rotation) =>
        Find(transform.GetType(), "SetPositionAndRotation", new[] { vectorType, rotationType }).Invoke(transform, new[] { position, rotation });

    private static void BeforeRound(object __instance)
    {
        if (sceneHandle == null)
            return;
        try
        {
            var recipeList = Get(__instance, "m_recipes");
            var entries = Get(recipeList, "m_recipes");
            var candidates = ((IEnumerable)entries).Cast<object>().Select(entry => (Entry: entry, Name: (string)Get(Get(entry, "m_order"), "name"))).ToArray();
            log.LogInfo($"BRIDGE_RECIPES {string.Join(" | ", candidates.Select(x => x.Name))}");
            if (!applyOverlay || overlay.Recipes.Length == 0)
                return;
            var allowed = candidates.Where(x => overlay.Recipes.Contains(x.Name, StringComparer.Ordinal)).ToArray();
            if (!overlay.Recipes.All(name => allowed.Any(row => row.Name == name)))
                throw new InvalidDataException("Requested recipe is not in this round's original AYCE recipe pool.");
            var type = entries.GetType();
            var array = Activator.CreateInstance(type, new object[] { (long)allowed.Length });
            var indexer = type.GetProperty("Item") ?? throw new MissingMemberException(type.FullName, "Item");
            var indexType = indexer.GetIndexParameters().Single().ParameterType;
            for (var i = 0; i < allowed.Length; i++)
                indexer.SetValue(array, allowed[i].Entry, new[] { Convert.ChangeType(i, indexType) });
            RecipeChanges.Add((recipeList, entries));
            Put(recipeList, "m_recipes", array);
            log.LogInfo($"BRIDGE_RECIPE_FILTER {string.Join(" | ", overlay.Recipes)}");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_RECIPE_SKIPPED {error.Message}");
        }
    }

    private static void AfterOrder()
    {
        if (sceneHandle != null)
            log.LogInfo("BRIDGE_ORDER_CREATED");
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
            if (refusedHandle == handle)
                refusedHandle = null;
            if (sceneHandle != handle)
                return;
            for (var index = RecipeChanges.Count - 1; index >= 0; index--)
            {
                var state = RecipeChanges[index];
                try { Put(state.Owner, "m_recipes", state.Original); }
                catch (Exception error) { log.LogError($"BRIDGE_RECIPE_RESTORE_ERROR {error}"); }
            }
            RecipeChanges.Clear();
            sceneHandle = null;
            log.LogInfo("BRIDGE_SCENE_EXIT");
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_SCENE_EXIT_ERROR {error.Message}");
        }
    }
}
