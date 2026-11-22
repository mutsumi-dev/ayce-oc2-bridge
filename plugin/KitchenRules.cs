using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private static MethodInfo kitchenServing, kitchenReturn, kitchenMixedContainer, kitchenCookableContainer, kitchenIngredientProperties, kitchenWorkableItem;
    private static MethodInfo kitchenCookingHandler, kitchenCookingStation, kitchenNewLookup, kitchenTryCast, kitchenNewCompositeOrder;
    private static Type kitchenOrder, kitchenCompositeOrder, kitchenCookedOrder, kitchenLookupEntry;
    private static MethodInfo kitchenIngredientContainer, kitchenPlateStack, kitchenTray;

    private static void InstallKitchenRules(Assembly game, Assembly unity)
    {
        kitchenIngredientContainer = GenericGameObjectMethod("GetComponent", game.GetType("IngredientContainer", true));
        kitchenServing = GenericGameObjectMethod("GetComponent", game.GetType("PlateStation", true));
        kitchenPlateStack = GenericGameObjectMethod("GetComponent", game.GetType("PlateStackBase", true));
        kitchenTray = GenericGameObjectMethod("GetComponent", game.GetType("Tray", true));
        kitchenReturn = GenericGameObjectMethod("GetComponent", game.GetType("PlateReturnStation", true));
        kitchenMixedContainer = GenericGameObjectMethod("GetComponent", game.GetType("MixableContainer", true));
        kitchenCookableContainer = GenericGameObjectMethod("GetComponent", game.GetType("CookableContainer", true));
        kitchenIngredientProperties = GenericGameObjectMethod("GetComponent", game.GetType("IngredientPropertiesComponent", true));
        kitchenWorkableItem = GenericGameObjectMethod("GetComponent", game.GetType("WorkableItem", true));
        kitchenCookingHandler = GenericGameObjectMethod("GetComponent", game.GetType("CookingHandler", true));
        var cookingStation = game.GetType("CookingStation", true);
        kitchenCookingStation = GenericGameObjectMethod("GetComponent", cookingStation);
        kitchenOrder = game.GetType("OrderDefinitionNode", true);
        kitchenCompositeOrder = game.GetType("CompositeOrderNode", true);
        kitchenCookedOrder = game.GetType("CookedCompositeOrderNode", true);
        var lookup = game.GetType("OrderToPrefabLookup", true);
        kitchenLookupEntry = game.GetType("OrderToPrefabLookup+ContentPrefabLookup", true);
        kitchenNewLookup = unity.GetType("UnityEngine.ScriptableObject", true).GetMethods().Single(method =>
            method.Name == "CreateInstance" && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
            .MakeGenericMethod(lookup);
        kitchenNewCompositeOrder = kitchenNewLookup.GetGenericMethodDefinition().MakeGenericMethod(kitchenCompositeOrder);
        kitchenTryCast = kitchenOrder.GetMethods().Single(method => method.Name == "TryCast" &&
            method.IsGenericMethodDefinition && method.GetParameters().Length == 0);
    }

    private static void ClearKitchenRules()
    {
        stockGridCheck = null;
        ClearKitchenPresentation();
    }

    private static int KitchenObjectId(object item) => (int)Call(item, "GetInstanceID");

    private static void ConnectStockReturns(StockKitchen kitchen, Dictionary<string, object> placed)
    {
        var targets = kitchen.Stations.Where(row => row.Role is "plate_return" or "glass_return")
            .ToDictionary(row => row.Id, row => kitchenReturn.Invoke(Get(placed[row.Id], "gameObject"), null));
        foreach (var row in kitchen.Stations.Where(station => station.ReturnStations != null))
        {
            var serving = kitchenServing.Invoke(Get(placed[row.Id], "gameObject"), null)
                ?? throw new InvalidOperationException($"Missing native serving component for {row.Id}.");
            var stations = row.ReturnStations.Select(id => targets[id] ??
                throw new InvalidOperationException($"Missing native return component for {id}."));
            SetNativeObjectArray(serving, "m_returnStations", kitchenReturn.ReturnType, stations);
        }
    }

    private static void SetNativeObjectArray(object owner, string name, Type element, IEnumerable<object> entries)
    {
        var values = entries.ToArray();
        var array = Array.CreateInstance(element, values.Length);
        for (var i = 0; i < values.Length; i++)
            array.SetValue(values[i], i);
        var type = owner.GetType();
        var wrapper = AccessTools.Property(type, name)?.PropertyType ?? AccessTools.Field(type, name)?.FieldType
            ?? throw new MissingMemberException(type.FullName, name);
        Set(owner, name, NativeArray(wrapper, array));
    }

    private static object CastKitchenOrder(object node, Type target) =>
        target.IsInstanceOfType(node) ? node : kitchenTryCast.MakeGenericMethod(target).Invoke(node, null);

    private static IEnumerable<object> KitchenRecipeNodes(IEnumerable<object> roots)
    {
        var remaining = new Stack<object>(roots);
        var visited = new HashSet<int>();
        while (remaining.TryPop(out var row))
        {
            if (row == null || !visited.Add((int)Get(row, "m_uID")))
                continue;
            yield return row;
            var composite = CastKitchenOrder(row, kitchenCompositeOrder);
            if (composite == null)
                continue;
            foreach (var name in new[] { "m_composition", "m_optional" })
                if (Get(composite, name) is object contents)
                    foreach (var child in Enumerate(contents))
                        remaining.Push(child);
        }
    }

    private static IEnumerable<object> StockTrayOrderNodes(StockLoad state, StockKitchen kitchen)
    {
        var supplies = kitchen.Stations.Where(row => row.Role == "plate_supply").Select(row => row.ReturnStack).Distinct();
        foreach (var path in supplies)
        {
            var stack = kitchenPlateStack.Invoke(state.Ingredients[path], null);
            var vessel = stack == null ? null : Get(stack, "m_platePrefab");
            var tray = vessel == null ? null : kitchenTray.Invoke(vessel, null);
            if (tray == null || Get(tray, "m_slots") is not object slots)
                continue;
            foreach (var lookup in Enumerate(slots))
            {
                if (lookup == null || Get(lookup, "m_lookupArray") is not object entries)
                    continue;
                foreach (var row in Enumerate(entries))
                    if (row != null && Get(row, "m_content") is object node)
                        yield return node;
            }
        }
    }

    private static IEnumerable<object> StockPlatingMatches(StockLoad state)
    {
        var marshmallow = state.ContentNodes.Values.FirstOrDefault(node => (string)Get(node, "name") == "RoastedMarshmallow");
        if (marshmallow == null)
            yield break;

        // Plate.CanPlaceOnPlate wraps a single cooked portion in a composite when the utensil is held.
        var match = kitchenNewCompositeOrder.Invoke(null, null);
        state.OwnedArt.Add(match);
        Set(match, "name", "RoastedMarshmallow (single plated portion)");
        Set(match, "m_uID", int.MaxValue);
        Set(match, "m_platingStep", Get(marshmallow, "m_platingStep"));
        Set(match, "m_platingPrefab", Get(marshmallow, "m_platingPrefab"));
        Set(match, "m_orderGuiDescription", Get(marshmallow, "m_orderGuiDescription"));
        SetNativeObjectArray(match, "m_composition", kitchenOrder, new[] { marshmallow });
        SetNativeObjectArray(match, "m_optional", kitchenOrder, Array.Empty<object>());
        yield return match;
    }

    private static object StockIngredientDefinition(object prefab)
    {
        if (kitchenIngredientProperties.Invoke(prefab, null) is object ingredient)
            return Get(ingredient, "m_ingredientOrderNode");
        var workable = kitchenWorkableItem.Invoke(prefab, null);
        var prepared = workable == null ? null : Get(workable, "m_nextPrefab");
        var properties = prepared == null ? null : kitchenIngredientProperties.Invoke(prepared, null);
        return properties == null ? null : Get(properties, "m_ingredientOrderNode");
    }

    private static IEnumerable<(object Node, object Visual)> AuthoredIngredientApprovals(StockStation station)
    {
        foreach (var path in station.AllowedIngredients ?? Array.Empty<string>())
        {
            if (stockLoad.IngredientNodes.TryGetValue(path, out var definition))
                yield return (definition, null);
            else
            {
                var prefab = stockLoad.Ingredients[path];
                if (StockIngredientDefinition(prefab) is object node)
                    yield return (node, prefab);
                else
                    log.LogWarning($"BRIDGE_STOCK_APPROVAL_MISSING id={station.Id} asset={path}");
            }
        }
    }

    private static void ApplyStockCookware(StockStation station, object item, object support)
    {
        try
        {
            if (station.UnresolvedIngredients is { Length: > 0 })
                log.LogWarning($"BRIDGE_STOCK_APPROVAL_UNRESOLVED id={station.Id} {string.Join(", ", station.UnresolvedIngredients)}");
            var explicitRules = station.AllowedIngredients?.Length > 0;
            if (!explicitRules && station.Role != "pan" && station.AllowedCookingStationTypes is not { Length: > 0 })
                return;

            var cookingHandler = kitchenCookingHandler.Invoke(item, null);
            if (cookingHandler != null && station.AllowedCookingStationTypes is { Length: > 0 } types)
            {
                var current = Get(cookingHandler, "m_stationType");
                var fixture = support == null ? null : kitchenCookingStation.Invoke(support, null);
                var supported = fixture == null ? Convert.ToInt32(current) : Convert.ToInt32(Get(fixture, "m_stationType"));
                int selected = types.Contains(supported) ? supported : types[0];
                Set(cookingHandler, "m_stationType", Enum.ToObject(current.GetType(), selected));
            }

            var mixer = kitchenMixedContainer.Invoke(item, null);
            var cookware = kitchenCookableContainer.Invoke(item, null);
            var cookingType = cookingHandler == null ? null : Get(cookingHandler, "m_cookingType");
            var cookingId = cookingType == null ? (int?)null : (int)Get(cookingType, "m_uID");
            bool IsOwnCookedRecipe(object order)
            {
                var cooked = CastKitchenOrder(order, kitchenCookedOrder);
                var step = cooked == null ? null : Get(cooked, "m_cookingStep");
                return cookingId.HasValue && step != null && (int)Get(step, "m_uID") == cookingId.Value;
            }

            if (explicitRules)
            {
                var entries = AuthoredIngredientApprovals(station).DistinctBy(entry => (int)Get(entry.Node, "m_uID")).ToArray();
                if (mixer != null)
                {
                    var mixable = entries.Where(entry => !IsOwnCookedRecipe(entry.Node)).Select(entry => entry.Node).ToArray();
                    if (mixable.Length != 0)
                    {
                        SetNativeObjectArray(mixer, "m_ApprovedIngredients", kitchenOrder, mixable);
                        log.LogInfo($"BRIDGE_STOCK_MIXER id={station.Id} approved={string.Join(", ", mixable.Select(node => Get(node, "name")))}");
                    }
                    entries = entries.Where(entry => IsOwnCookedRecipe(entry.Node)).ToArray();
                }
                if (cookware != null)
                    InstallCookingLookup(station.Id, cookware, entries, keepNative: station.UnresolvedIngredients?.Length > 0);
                return;
            }
            if (station.Role != "pan" || cookware == null || !cookingId.HasValue)
                return;

            var inputs = KitchenRecipeNodes(stockLoad.RecipeNodes.Values).Where(IsOwnCookedRecipe)
                .SelectMany(node => Enumerate(Get(CastKitchenOrder(node, kitchenCookedOrder), "m_composition")))
                .Where(node => node != null).DistinctBy(node => (int)Get(node, "m_uID"))
                .Select(node => (Node: node, Visual: (object)null)).ToArray();
            InstallCookingLookup(station.Id, cookware, inputs, keepNative: true);
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_COOKWARE_FAILED id={station.Id} {error}"); }
    }

    private static void InstallCookingLookup(string station, object cookware, (object Node, object Visual)[] approvals, bool keepNative)
    {
        if (approvals.Length == 0)
            return;
        var oldLookup = Get(cookware, "m_approvedContentsList");
        var old = oldLookup != null && Get(oldLookup, "m_lookupArray") is object existing
            ? Enumerate(existing).Where(row => row != null).ToArray() : Array.Empty<object>();
        var fallback = old.Select(row => Get(row, "m_prefab")).FirstOrDefault(prefab => prefab != null)
            ?? Get(cookware, "m_cosmeticsPrefab");
        var native = old.Where(row => Get(row, "m_content") != null)
            .GroupBy(row => (int)Get(Get(row, "m_content"), "m_uID")).ToDictionary(group => group.Key, group => group.First());
        var rows = keepNative ? old.ToList() : new List<object>();
        foreach (var (node, visual) in approvals)
        {
            if (native.TryGetValue((int)Get(node, "m_uID"), out var match))
            {
                if (!keepNative) rows.Add(match);
                continue;
            }
            var prefab = visual ?? fallback;
            if (prefab == null)
                continue;
            var row = Activator.CreateInstance(kitchenLookupEntry, nonPublic: true);
            Set(row, "m_content", node);
            Set(row, "m_prefab", prefab);
            rows.Add(row);
        }
        if (rows.Count == 0)
            return;
        var replacement = kitchenNewLookup.Invoke(null, null);
        stockLoad.OwnedArt.Add(replacement);
        Set(replacement, "name", $"AYCE bridge cookware {station}");
        SetNativeObjectArray(replacement, "m_lookupArray", kitchenLookupEntry, rows);
        Set(cookware, "m_approvedContentsList", replacement);
        log.LogInfo($"BRIDGE_STOCK_COOKWARE id={station} authored={approvals.Length} active={rows.Count}");
    }
}
