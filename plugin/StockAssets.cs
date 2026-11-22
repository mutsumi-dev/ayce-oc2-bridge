using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private sealed record DonorPrototype(object Transform, object[] Entities, object[] Components);

    private sealed class StockLoad
    {
        internal readonly List<object> Handles = new();
        internal readonly Dictionary<string, object> Ingredients = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, object> RecipeNodes = new();
        internal readonly Dictionary<int, object> ContentNodes = new();
        internal readonly Dictionary<int, object> CookingSteps = new();
        internal readonly Dictionary<int, (object Config, object Original)> RecipeConfigs = new();
        internal readonly Dictionary<string, object> IngredientNodes = new(StringComparer.Ordinal);
        internal readonly Dictionary<int, object> RepairedMaterials = new();
        internal readonly Dictionary<(string Scene, string Path), DonorPrototype> Prototypes = new();
        internal readonly List<OrderRecipe> Orders = new();
        internal readonly List<object> OwnedArt = new();
        internal readonly List<(KitchenLoop Loop, object Clip)> LoopingAudio = new();
        internal object RoundAudioDirectory;
        internal readonly Dictionary<string, object> VisualModels = new(StringComparer.Ordinal);
        internal ArtScene Art;
        internal object ArtShader;
        internal object FloorTexture;
        internal object Storage;
        internal object LoadingBundle;
        internal object DonorScene;
        internal object BundleRequest;
        internal object SceneRequest;
        internal string ScenePath;
    }

    private static StockLoad stockLoad;
    private static StockLoad retiredStockLoad;
    private static string stockLoadUnavailable;
    private static MethodInfo loadAddressable;
    private static MethodInfo managedToNativeString;
    private static MethodInfo stockLoadBundle;
    private static MethodInfo stockLoadScene;
    private static MethodInfo stockSceneByPath;
    private static MethodInfo stockUnloadScene;
    private static MethodInfo stockDontDestroy;
    private static Type stockOrderType;
    private static Type stockMatchListType, stockCookingStepType;
    private static MethodInfo stockCreateMatchList, stockGetLevelConfig;
    private static Type stockEntryType;
    private static Type stockBundleResource;
    private static object stockAdditiveMode;

    private static bool SafeAssetPath(string value, string extension) => value is { Length: > 12 and <= 225 } &&
        value.StartsWith("Assets/", StringComparison.Ordinal) && value.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
        !value.Contains('\\') && !value.Contains(':') && value.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));

    private static void ValidateStockKitchen(Overlay value)
    {
        var data = value.Stock;
        if (data == null)
            return;
        if (value.Kitchen != null || value.Scene != KevinScene || value.CampaignRules == null || value.Moves.Length != 0 ||
            data.Recipes?.Length is not (> 0 and <= 100) || data.Stations?.Length is not (> 5 and <= 240) ||
            data.Boxes?.Length is not (> 0 and <= 100) || data.Donors == null || data.Donors.Count > 20)
            throw new InvalidDataException("Invalid static kitchen manifest.");

        var recipeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in data.Recipes)
        {
            if (recipe == null || recipe.Score is null or < 0 or > 1023 || !SafeAssetPath(recipe.Address, ".asset") || !recipe.Address.Contains("/OrderDefinitions/RecipeItems/", StringComparison.OrdinalIgnoreCase) ||
                recipe.NativeType is not ("CompositeOrderNode" or "CookedCompositeOrderNode" or "MixedCompositeOrderNode") || !recipeKeys.Add(recipe.Address))
                throw new InvalidDataException("Invalid static kitchen recipe address.");
        }
        if (!recipeKeys.SetEquals(value.Recipes))
            throw new InvalidDataException("Recipe allowlist differs from the native stock menu.");

        foreach (var (name, donor) in data.Donors)
        {
            if (!Regex.IsMatch(name, "^s_[a-z0-9_]{1,45}$") || name == KevinScene || donor == null || !SafeAssetPath(donor.Path, ".unity") ||
                !donor.Path.EndsWith($"/{name}.unity", StringComparison.OrdinalIgnoreCase) || donor.Dependencies?.Length is not (> 0 and <= 150) ||
                donor.Dependencies.Any(key => key == null || !Regex.IsMatch(key, "^[a-zA-Z0-9_.-]{1,120}\\.bundle$") || key.Contains("..") || key.Contains("_scenes_all")))
                throw new InvalidDataException($"Invalid native donor {name}.");
        }

        var stationIds = new Dictionary<string, StockStation>(StringComparer.Ordinal);
        foreach (var station in data.Stations)
        {
            if (station == null || station.Id == null || !Regex.IsMatch(station.Id, "^p[0-9]{1,4}$") || !stationIds.TryAdd(station.Id, station) ||
                station.Role is null || !StockRoles.Contains(station.Role) || station.Scene is null ||
                !(station.Scene == "asset" ? SafeAssetPath(station.Path, ".prefab") :
                    (station.Scene == KevinScene || data.Donors.ContainsKey(station.Scene)) && LayoutPath(station.Path)) ||
                !ValidVector(station.Position, 3) || station.Position.Any(n => Math.Abs(n) > 48f) || !LayoutRotation(station.Rotation) ||
                (station.Role == "dispenser" ? !SafeAssetPath(station.Ingredient, ".prefab") : station.Ingredient != null) ||
                station.ReturnStack != null && (station.Role is not ("plate_return" or "glass_return" or "plate_supply") || !SafeAssetPath(station.ReturnStack, ".prefab")) ||
                station.ReturnStations != null && (station.Role != "serving" || station.ReturnStations.Length > data.Stations.Length) ||
                station.AllowedIngredients?.Any(key => !SafeAssetPath(key, ".asset") && !SafeAssetPath(key, ".prefab")) == true ||
                station.AllowedCookingStationTypes?.Any(type => type is < 1 or > 7) == true ||
                station.Role == "plate_supply" && (station.Support != null || station.ReturnStack == null || station.PlateCount <= 0) ||
                station.Capacity is < 0 or > 30 || station.PlateCount is < 0 or > 30)
                throw new InvalidDataException($"Invalid static kitchen station: {station?.Id}.");
        }
        if (!data.Stations.Where(station => station.Role == "spawn").Select(station => station.Path)
                .OrderBy(path => path, StringComparer.Ordinal).SequenceEqual(Enumerable.Range(1, 4).Select(index => $"Chefs/Player {index}")) ||
            data.Stations.Any(station => station.Role == "spawn" && (station.Scene != KevinScene || station.Support != null)))
            throw new InvalidDataException("Invalid native chef spawns.");
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var station in data.Stations.Where(row => row.Support != null))
        {
            if (!stationIds.TryGetValue(station.Support, out var support) || support == station || support.Support != null ||
                support.Role == "spawn" || !occupied.Add(station.Support))
                throw new InvalidDataException($"Invalid authored support for {station.Id}.");
        }
        foreach (var station in data.Stations.Where(station => station.ReturnStations != null))
            if (station.ReturnStations.Any(id => id == null || !stationIds.TryGetValue(id, out var target) ||
                target.Role is not ("plate_return" or "glass_return")))
                throw new InvalidDataException($"Invalid vessel return link for {station.Id}.");
        if (!data.Boxes.Any(box => box?.Layer == 9) || data.Boxes.Any(box => box == null || box.Layer is not (0 or 9 or 10) ||
                !ValidVector(box.Position, 3) || !ValidVector(box.Size, 3) || box.Size.Any(n => n is <= 0 or > 100) || !LayoutRotation(box.Rotation)))
            throw new InvalidDataException("Invalid authored collision boxes for a static kitchen.");
        if (data.Art != null && (!Regex.IsMatch(data.Art, "^[A-Za-z0-9_-]{1,75}\\.zip$") || !ValidVector(data.ArtOffset, 3)) ||
            data.FloorTexture != null && !SafeAssetPath(data.FloorTexture, ".tga"))
            throw new InvalidDataException("Invalid static kitchen art reference.");
    }

    private static void TryInstallStockKitchenHooks(Assembly game, Assembly unity)
    {
        var harmony = new Harmony(Id + ".stock-catalog");
        try
        {
            var addressables = Assembly.Load("Unity.Addressables").GetType("UnityEngine.AddressableAssets.Addressables", true);
            loadAddressable = addressables.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(method => method.Name == "LoadAssetAsync" &&
                method.IsGenericMethodDefinition && method.GetParameters() is var args && args.Length == 1 &&
                args[0].ParameterType.FullName is "Il2CppSystem.Object" or "System.Object");
            var interop = Assembly.Load("Il2CppInterop.Runtime").GetType("Il2CppInterop.Runtime.IL2CPP", true);
            managedToNativeString = Find(interop, "ManagedStringToIl2Cpp", new[] { typeof(string) });
            var asm = Assembly.Load("Unity.ResourceManager");
            stockBundleResource = asm.GetType("UnityEngine.ResourceManagement.ResourceProviders.IAssetBundleResource", true);
            var bundle = Assembly.Load("UnityEngine.AssetBundleModule").GetType("UnityEngine.AssetBundle", true);
            stockLoadBundle = Find(bundle, "LoadFromFileAsync", new[] { typeof(string), typeof(uint), typeof(ulong) });
            var scenes = unity.GetType("UnityEngine.SceneManagement.SceneManager", true);
            var scene = unity.GetType("UnityEngine.SceneManagement.Scene", true);
            var mode = unity.GetType("UnityEngine.SceneManagement.LoadSceneMode", true);
            stockAdditiveMode = Enum.Parse(mode, "Additive");
            stockLoadScene = Find(scenes, "LoadSceneAsync", new[] { typeof(string), mode });
            stockSceneByPath = Find(scenes, "GetSceneByPath", new[] { typeof(string) });
            stockUnloadScene = Find(scenes, "UnloadSceneAsync", new[] { scene });
            var unityObject = unity.GetType("UnityEngine.Object", true);
            stockDontDestroy = Find(unityObject, "DontDestroyOnLoad", new[] { unityObject });
            stockOrderType = game.GetType("OrderDefinitionNode", true);
            stockEntryType = game.GetType("RecipeList+Entry", true);
            InstallStockLayout(game, unity);
            InstallStockAudio(game);
            TryInstallStockArt(unity);
            var gameUtils = game.GetType("GameUtils", true);
            stockGetLevelConfig = Find(gameUtils, "GetLevelConfig", Type.EmptyTypes);
            stockMatchListType = game.GetType("RecipeMatchList", true);
            stockCookingStepType = game.GetType("CookingStepData", true);
            stockCreateMatchList = kitchenNewLookup.GetGenericMethodDefinition().MakeGenericMethod(stockMatchListType);
            Hook(harmony, Find(gameUtils, "GetOrderDefinitionNode", new[] { typeof(int) }), nameof(BeforeStockOrderLookup));
            Hook(harmony, Find(game.GetType("ClientKitchenFlowControllerBase", true), "CacheRecipeListData", Type.EmptyTypes),
                nameof(BeforeStockRecipeCache));
            log.LogInfo("BRIDGE_STOCK_CATALOG_READY");
        }
        catch (Exception error)
        {
            harmony.UnpatchSelf();
            stockLoadUnavailable = error.Message;
            log.LogWarning($"BRIDGE_STOCK_CATALOG_UNAVAILABLE {error}");
        }
    }

    private static object StartAddressable(StockLoad state, Type type, string key)
    {
        var method = loadAddressable.MakeGenericMethod(type);
        var native = method.GetParameters()[0].ParameterType;
        object argument = native == typeof(object) ? key : Activator.CreateInstance(native, managedToNativeString.Invoke(null, new object[] { key }));
        var handle = method.Invoke(null, new[] { argument }) ?? throw new InvalidOperationException($"No resource handle for {key}.");
        state.Handles.Add(handle);
        return handle;
    }

    private static object ReadAddressable(object handle, string key)
    {
        if (!string.Equals(Get(handle, "Status").ToString(), "Succeeded", StringComparison.Ordinal))
            throw new InvalidDataException($"Could not load native asset {key}: {Get(handle, "OperationException")}");
        return Get(handle, "Result") ?? throw new InvalidDataException($"Native resource was null: {key}.");
    }

    private static IEnumerator PrepareStockLoad(Overlay level)
    {
        if (stockLoadUnavailable != null)
            throw new InvalidOperationException(stockLoadUnavailable);
        while (retiredStockLoad != null)
        {
            DisposeRetiredStock();
            if (retiredStockLoad != null) yield return null;
        }
        var state = stockLoad = new StockLoad();
        var kitchen = level.Stock;
        var dependencies = kitchen.Donors.Values.SelectMany(donor => donor.Dependencies).Distinct(StringComparer.Ordinal).ToArray();
        var bundleTasks = dependencies.Select(key => (Key: key, Handle: StartAddressable(state, stockBundleResource, key))).ToArray();
        foreach (var task in bundleTasks)
        {
            while (!(bool)Get(task.Handle, "IsDone")) yield return null;
            ReadAddressable(task.Handle, task.Key);
        }

        foreach (var step in PrepareStockAudio(state, kitchen))
            yield return step;
        var approvals = kitchen.Stations.SelectMany(station => station.AllowedIngredients ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        var items = kitchen.Stations.Select(station => station.Ingredient ?? station.ReturnStack ?? (station.Scene == "asset" ? station.Path : null))
            .Where(key => key != null).Concat(approvals.Where(key => SafeAssetPath(key, ".prefab")))
            .Distinct(StringComparer.Ordinal).ToArray();
        var itemTasks = items.Select(key => (Key: key, Handle: StartAddressable(state, kitchenObjectType, key))).ToArray();
        foreach (var task in itemTasks)
        {
            while (!(bool)Get(task.Handle, "IsDone")) yield return null;
            state.Ingredients.Add(task.Key, ReadAddressable(task.Handle, task.Key));
        }
        var ingredientTasks = approvals.Where(key => SafeAssetPath(key, ".asset"))
            .Select(key => (Key: key, Handle: StartAddressable(state, stockOrderType, key))).ToArray();
        foreach (var task in ingredientTasks)
        {
            while (!(bool)Get(task.Handle, "IsDone")) yield return null;
            state.IngredientNodes.Add(task.Key, ReadAddressable(task.Handle, task.Key));
        }
        var recipeTasks = kitchen.Recipes.Select(recipe => (Recipe: recipe, Handle: StartAddressable(state, stockOrderType, recipe.Address))).ToArray();
        foreach (var task in recipeTasks)
        {
            while (!(bool)Get(task.Handle, "IsDone")) yield return null;
            var node = ReadAddressable(task.Handle, task.Recipe.Address);
            var id = (int)Get(node, "m_uID");
            if (!state.RecipeNodes.TryAdd(id, node))
                continue;
            var entry = Activator.CreateInstance(stockEntryType, nonPublic: true);
            Set(entry, "m_order", node);
            Set(entry, "m_weight", 1f);
            Set(entry, "m_scoreForMeal", task.Recipe.Score.Value);
            Set(entry, "m_scoreModifier", 0);
            state.Orders.Add(new OrderRecipe(entry, (string)Get(node, "name")));
        }
        if (state.Orders.Count == 0)
            throw new InvalidDataException("No native order definitions were loaded.");
        var dispensed = kitchen.Stations.Where(row => row.Ingredient != null).Select(row => row.Ingredient).Distinct(StringComparer.Ordinal)
            .Select(key => StockIngredientDefinition(state.Ingredients[key]));
        var trayNodes = StockTrayOrderNodes(state, kitchen).DistinctBy(node => (int)Get(node, "m_uID")).ToArray();
        if (trayNodes.Length != 0)
            log.LogInfo($"BRIDGE_STOCK_TRAY_RECIPES {string.Join(", ", trayNodes.Select(node => Get(node, "name")))}");
        var recipeRoots = state.RecipeNodes.Values.Concat(state.IngredientNodes.Values).Concat(dispensed).Concat(trayNodes);
        foreach (var node in KitchenRecipeNodes(recipeRoots))
        {
            state.ContentNodes.TryAdd((int)Get(node, "m_uID"), node);
            var cooked = CastKitchenOrder(node, kitchenCookedOrder);
            if (cooked != null && Get(cooked, "m_cookingStep") is object step)
                state.CookingSteps.TryAdd((int)Get(step, "m_uID"), step);
        }

        foreach (var step in PrepareStockArt(state, kitchen))
            yield return step;

        state.Storage = Activator.CreateInstance(kitchenObjectType, "AYCE bridge native station library");
        SetActive(state.Storage, false);
        stockDontDestroy.Invoke(null, new[] { state.Storage });
        var parent = Get(state.Storage, "transform");
        foreach (var (name, donor) in kitchen.Donors)
        {
            var path = Path.Combine(Paths.PluginPath, "AyceOc2Bridge", "donors", name + ".bundle");
            if (!File.Exists(path))
                throw new FileNotFoundException($"Native donor {name} is missing.", path);
            var load = stockLoadBundle.Invoke(null, new object[] { path, 0u, 0UL });
            state.BundleRequest = load;
            while (!(bool)Get(load, "isDone")) yield return null;
            state.LoadingBundle = Get(load, "assetBundle") ?? throw new InvalidDataException($"Native donor could not open: {name}.");
            state.BundleRequest = null;
            var scenePath = Enumerate(Call(state.LoadingBundle, "GetAllScenePaths")).Cast<string>()
                .SingleOrDefault(candidate => string.Equals(candidate, donor.Path, StringComparison.OrdinalIgnoreCase));
            if (scenePath == null)
                throw new InvalidDataException($"Donor bundle does not contain {donor.Path}.");
            var operation = stockLoadScene.Invoke(null, new[] { scenePath, stockAdditiveMode });
            if (operation == null)
                throw new InvalidDataException($"Native donor scene could not open: {name}.");
            state.ScenePath = scenePath;
            state.SceneRequest = operation;
            while (!(bool)Get(operation, "isDone")) yield return null;
            var scene = stockSceneByPath.Invoke(null, new object[] { scenePath });
            state.DonorScene = scene;
            state.SceneRequest = null;
            var roots = ReadSceneRoots(scene);
            if (roots.Any(group => group.Any(root => (bool)Get(root, "activeSelf"))))
                throw new InvalidDataException($"Native donor is not inactive: {name}.");
            var levelData = kitchenLevelData.Invoke(Get(ResolveSceneObject(roots, "Level Data"), "gameObject"), null);
            var entities = SnapshotNativeList(Get(levelData, "m_EntityRegistry"));
            var components = SnapshotNativeList(Get(levelData, "m_EntityRegistryComponents"));
            foreach (var item in kitchen.Stations.Where(station => station.Scene == name).GroupBy(station => station.Path))
            {
                var source = ResolveSceneObject(roots, item.Key);
                var clone = kitchenInstantiate.Invoke(null, new[] { Get(source, "gameObject"), parent, false });
                var transform = Get(clone, "transform");
                var copies = new Dictionary<int, object>();
                MapKitchenClone(source, transform, copies);
                state.Prototypes.Add((name, item.Key), new DonorPrototype(transform, RemapRegistry(entities, copies), RemapRegistry(components, copies)));
            }
            var unload = stockUnloadScene.Invoke(null, new[] { scene });
            while (unload != null && !(bool)Get(unload, "isDone")) yield return null;
            state.DonorScene = null;
            Find(state.LoadingBundle.GetType(), "Unload", new[] { typeof(bool) }).Invoke(state.LoadingBundle, new object[] { false });
            state.LoadingBundle = null;
        }
        log.LogInfo($"BRIDGE_STOCK_PREPARED kitchen={level.Name} donors={kitchen.Donors.Count} recipes={state.Orders.Count} ingredients={items.Length}");
    }

    private static object[] RemapRegistry(object[] baselines, Dictionary<int, object> copies) => baselines
        .Where(item => item != null && copies.ContainsKey((int)Call(item, "GetInstanceID")))
        .Select(item => copies[(int)Call(item, "GetInstanceID")]).ToArray();

    private static void BeforeStockRecipeCache(object __instance)
    {
        if (overlay?.Stock == null || kitchenHandle == null || stockLoad == null)
            return;
        try { InstallStockRecipeMatching(Call(__instance, "GetLevelConfig")); }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_RECIPE_CACHE_FAILED {error}"); }
    }

    private static void InstallStockRecipeMatching(object config)
    {
        if (config == null || stockLoad == null)
            return;
        var id = KitchenObjectId(config);
        if (stockLoad.RecipeConfigs.ContainsKey(id))
            return;
        var original = Get(config, "m_recipeMatchingList");
        var originalRecipes = original == null ? null : Get(original, "m_recipes");
        var originalSteps = original == null ? null : Get(original, "m_cookingSteps");
        var recipes = (originalRecipes == null ? Enumerable.Empty<object>() : Enumerate(originalRecipes))
            .Concat(stockLoad.ContentNodes.Values).Concat(StockPlatingMatches(stockLoad))
            .Where(node => node != null).DistinctBy(node => (int)Get(node, "m_uID"));
        var steps = (originalSteps == null ? Enumerable.Empty<object>() : Enumerate(originalSteps))
            .Concat(stockLoad.CookingSteps.Values).Where(step => step != null).DistinctBy(step => (int)Get(step, "m_uID"));
        var matching = stockCreateMatchList.Invoke(null, null);
        stockLoad.OwnedArt.Add(matching);
        Set(matching, "name", "AYCE bridge recipe matching");
        SetNativeObjectArray(matching, "m_recipes", stockOrderType, recipes);
        SetNativeObjectArray(matching, "m_cookingSteps", stockCookingStepType, steps);
        var included = original == null ? null : Get(original, "m_includeLists");
        if (included != null)
            Set(matching, "m_includeLists", included);
        else
            SetNativeObjectArray(matching, "m_includeLists", stockMatchListType, Array.Empty<object>());
        Set(config, "m_recipeMatchingList", matching);
        stockLoad.RecipeConfigs.Add(id, (config, original));
        log.LogInfo($"BRIDGE_STOCK_RECIPE_MATCH nodes={stockLoad.ContentNodes.Count} cookingSteps={stockLoad.CookingSteps.Count}");
    }

    private static bool BeforeStockOrderLookup(int __0, ref object __result)
    {
        if (overlay?.Stock == null || sceneHandle == null || stockLoad == null || !stockLoad.ContentNodes.TryGetValue(__0, out var order))
            return true;
        __result = order;
        return false;
    }

    private static void ApplyStockOrders()
    {
        if (stockLoad == null || stockLoad.Orders.Count == 0)
            throw new InvalidDataException("No native orders were prepared for this kitchen.");
        ApplyStockAudio(stockLoad);
        allowedOrders = stockLoad.Orders.ToArray();
        log.LogInfo($"BRIDGE_STOCK_RECIPES count={allowedOrders.Length} {string.Join(" | ", allowedOrders.Select(entry => entry.Name))}");
    }

    private static void RestoreStockSettings(StockLoad state)
    {
        RestoreStockAudio(state);
        foreach (var (config, original) in state.RecipeConfigs.Values)
        {
            try { Set(config, "m_recipeMatchingList", original); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_RECIPE_RESTORE_FAILED {error.Message}"); }
        }
        state.RecipeConfigs.Clear();
    }

    private static void ReleaseStockRoundAssets()
    {
        if (stockLoad == null)
            return;
        RestoreStockSettings(stockLoad);
        foreach (var item in stockLoad.OwnedArt.AsEnumerable().Reverse())
        {
            try { kitchenDestroy.Invoke(null, new[] { item }); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_CLEANUP {error.Message}"); }
        }
        stockLoad.OwnedArt.Clear();
        stockLoad.RepairedMaterials.Clear();
        stockLoad.ArtShader = null;
    }

    private static void ReleaseStockKitchen()
    {
        if (stockLoad == null)
            return;
        RestoreStockSettings(stockLoad);
        retiredStockLoad = stockLoad;
        stockLoad = null;
    }

    private static void DisposeRetiredStock()
    {
        var state = retiredStockLoad;
        if (state == null)
            return;
        if (state.BundleRequest != null && !(bool)Get(state.BundleRequest, "isDone") ||
            state.SceneRequest != null && !(bool)Get(state.SceneRequest, "isDone"))
            return;
        retiredStockLoad = null;
        void Attempt(Action cleanup)
        {
            try { cleanup(); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_CLEANUP {error.Message}"); }
        }
        if (state.DonorScene == null && state.SceneRequest != null)
            Attempt(() => state.DonorScene = stockSceneByPath.Invoke(null, new object[] { state.ScenePath }));
        if (state.LoadingBundle == null && state.BundleRequest != null)
            Attempt(() => state.LoadingBundle = Get(state.BundleRequest, "assetBundle"));
        if (state.DonorScene != null)
            Attempt(() => stockUnloadScene.Invoke(null, new[] { state.DonorScene }));
        if (state.LoadingBundle != null)
            Attempt(() => Find(state.LoadingBundle.GetType(), "Unload", new[] { typeof(bool) }).Invoke(state.LoadingBundle, new object[] { false }));
        if (state.Storage != null)
            Attempt(() => kitchenDestroy.Invoke(null, new[] { state.Storage }));
        foreach (var item in state.OwnedArt.AsEnumerable().Reverse())
            Attempt(() => kitchenDestroy.Invoke(null, new[] { item }));
        foreach (var handle in state.Handles.AsEnumerable().Reverse())
            Attempt(() => Call(handle, "Release"));
    }
}
