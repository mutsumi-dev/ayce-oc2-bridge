using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private static readonly HashSet<string> StockRoles = new(StringComparer.Ordinal)
    {
        "spawn", "counter", "chopping", "dispenser", "serving", "sink", "trash", "plate_return", "plate_stack",
        "plate", "plate_supply", "extinguisher", "water_gun", "stove", "mixer", "mixing_bowl", "steamer", "barbecue", "bellows", "blender", "blender_cup",
        "campfire", "fryer", "fryer_basket", "glass_return", "glass_stack", "griddle", "oven", "pan", "pot", "skewer",
        "toasting_fork", "roasting_tray", "tray_stack", "serving_tray", "mug_stack", "teleportal", "conveyor", "travelator", "ingredient_spray",
        "attached_spawner", "condiment_switch", "condiment_dispenser"
    };

    private static readonly string[] StockHostGroups =
    {
        "Design/Collision", "Design/Animated Objects", "Design/Gameplay Stations", "Design/Work Surfaces",
        "Design/Cooking Stations", "Design/Utensils", "Design/Plates", "Design/Environment Objects", "Design/Floors",
        "Design/Walls", "Design/KillPlanes", "Art/Lights/ColourTint", "Art/Lights/Light Shafts",
    };

    private static MethodInfo stockSpawner, stockTeleportal;
    private static MethodInfo stockConveyor, stockTravelator, stockSpray;
    private static MethodInfo stockAttachedSpawner, stockSpawnTimers;
    private static Type stockWeightedPrefab;
    private static MethodInfo stockSwitchRelay, stockCondimentSwitcher, stockCondimentEffects, stockCondimentColors;
    private static Type stockIngredientType, stockColorMaterial, stockColorParticle;
    private sealed record StockGridCell(string Id, object Location, object Item, object Index);
    private static MethodInfo stockCellCenter, stockCellOccupant, stockRemoveOccupant, stockInversePoint;
    private static (object Grid, StockGridCell[] Cells)? stockGridCheck;
    private static MethodInfo stockCamera;
    private static MethodInfo stockMonoBehaviours;
    private static Type stockPoint;

    private static void InstallStockLayout(Assembly game, Assembly unity)
    {
        stockSpawner = GenericGameObjectMethod("GetComponent", game.GetType("PickupItemSpawner", true));
        stockTeleportal = GenericGameObjectMethod("GetComponent", game.GetType("Teleportal", true));
        stockConveyor = GenericGameObjectMethod("GetComponent", game.GetType("ConveyorStation", true));
        stockTravelator = GenericGameObjectMethod("GetComponent", game.GetType("Travelator", true));
        stockSpray = GenericGameObjectMethod("GetComponent", game.GetType("IngredientSpray", true));
        var spawner = game.GetType("TriggerAttachedSpawn", true);
        stockAttachedSpawner = GenericGameObjectMethod("GetComponent", spawner);
        stockWeightedPrefab = game.GetType("TriggerAttachedSpawn+WeightedPrefab", true);
        stockSpawnTimers = GenericGameObjectMethod("GetComponents", game.GetType("TriggerTimer", true));
        stockSwitchRelay = GenericGameObjectMethod("GetComponent", game.GetType("TriggerOnObject", true));
        stockCondimentSwitcher = GenericGameObjectMethod("GetComponent", game.GetType("PlacementItemSwitcher", true));
        stockCondimentEffects = GenericGameObjectMethod("GetComponent", game.GetType("CondimentDispenserCosmeticDecisions", true));
        stockCondimentColors = GenericGameObjectMethod("GetComponent", game.GetType("TriggerColourCycle", true));
        stockIngredientType = game.GetType("IngredientOrderNode", true);
        stockColorMaterial = unity.GetType("UnityEngine.Material", true);
        stockColorParticle = Assembly.Load("UnityEngine.ParticleSystemModule").GetType("UnityEngine.ParticleSystem", true);
        var grid = game.GetType("QuadGridManager", true);
        var index = game.GetType("GridIndex", true);
        stockCellCenter = Find(grid, "GetPosFromGridLocation", new[] { index });
        stockCellOccupant = Find(grid, "GetGridOccupant", new[] { index });
        stockRemoveOccupant = Find(grid, "TryDeoccupyGrid", new[] { index, kitchenObjectType });
        stockInversePoint = Find(kitchenTransformType, "InverseTransformPoint", new[] { vectorType });
        stockCamera = GenericGameObjectMethod("GetComponent", game.GetType("MultiplayerCamera", true));
        stockMonoBehaviours = kitchenObjectType.GetMethods()
            .Single(method => method.Name == "GetComponentsInChildren" && method.IsGenericMethodDefinition &&
                              method.GetParameters() is var args && args.Length == 1 && args[0].ParameterType == typeof(bool))
            .MakeGenericMethod(unity.GetType("UnityEngine.MonoBehaviour", true));
        stockPoint = game.GetType("Point3", true);
    }

    private static ILookup<string, object> ReadSceneRoots(object scene) => Enumerate(Call(scene, "GetRootGameObjects"))
        .ToLookup(item => (string)Get(item, "name"), StringComparer.Ordinal);

    private static object ResolveSceneObject(ILookup<string, object> roots, string path, bool required = true)
    {
        var parts = path.Split('/', 2);
        var root = roots[parts[0]].SingleOrDefault();
        var transform = root == null ? null : Get(root, "transform");
        if (transform != null && parts.Length == 2)
            transform = Find(kitchenTransformType, "Find", new[] { typeof(string) }).Invoke(transform, new object[] { parts[1] });
        if (transform == null && required)
            throw new InvalidDataException($"Missing native scene object: {path}.");
        return transform;
    }

    private static void AddStockRegistry(object list, Type itemType, IEnumerable<object> rows)
    {
        var add = Find(list.GetType(), "Add", new[] { itemType });
        foreach (var item in rows)
            add.Invoke(list, new[] { item });
    }

    private static void PlaceStockKitchen(object scene, StockKitchen kitchen)
    {
        if (stockLoad == null)
            throw new InvalidOperationException("Native kitchen assets have not been prepared.");
        var roots = ReadSceneRoots(scene);
        var level = kitchenLevelData.Invoke(Get(ResolveSceneObject(roots, "Level Data"), "gameObject"), null);
        var grid = kitchenGrid.Invoke(Get(ResolveSceneObject(roots, "CampaignGameEnvironment/GridManager"), "gameObject"), null);
        var entities = Get(level, "m_EntityRegistry");
        var components = Get(level, "m_EntityRegistryComponents");
        var entityBaseline = SnapshotNativeList(entities);
        var componentBaseline = SnapshotNativeList(components);
        if (entityBaseline.Length != 58 || componentBaseline.Length != 369)
            throw new InvalidDataException("Unsupported native kitchen registry.");

        var room = Activator.CreateInstance(kitchenObjectType, "AYCE custom kitchen");
        SetActive(room, false);
        var roomTransform = Get(room, "transform");
        var placed = new Dictionary<string, object>(StringComparer.Ordinal);
        var restored = new Stack<Action>();
        try
        {
            if (Handle(Get(room, "scene")) != Handle(scene))
                kitchenSceneMove.Invoke(null, new[] { room, scene });
            Parent(roomTransform, ResolveSceneObject(roots, "Design"));
            var gridSize = Get(grid, "AccessGridHalfSize");
            restored.Push(() => Set(grid, "AccessGridHalfSize", gridSize));
            Set(grid, "AccessGridHalfSize", Activator.CreateInstance(stockPoint, 32, 8, 32));
            var fallPlane = ResolveSceneObject(roots, "CampaignGameEnvironment/KillPlane");
            var fallScale = Get(fallPlane, "localScale");
            restored.Push(() => Set(fallPlane, "localScale", fallScale));
            Set(fallPlane, "localScale", Vector(new[] { 16f, (float)Get(fallScale, "y"), 16f }));
            var render = ResolveSceneObject(roots, "MultiplayerGameCamera");
            var camera = stockCamera.Invoke(Get(render, "gameObject"), null);
            var span = kitchen.Stations.Max(row => row.Position[0]) - kitchen.Stations.Min(row => row.Position[0]);
            if (camera != null && span > 16f)
            {
                var range = (float)Get(camera, "m_minDistance");
                var maximum = (float)Get(camera, "m_maxDistance");
                restored.Push(() => { Set(camera, "m_minDistance", range); Set(camera, "m_maxDistance", maximum); });
                float factor = Math.Min(1.5f, span / 16f);
                Set(camera, "m_minDistance", range * factor);
                Set(camera, "m_maxDistance", maximum * factor);
            }

            foreach (var box in kitchen.Boxes)
            {
                var layer = box.Layer == 0 ? 10 : box.Layer;
                CreateKitchenCube(roomTransform, box.Path?.Split('/')[^1] ?? "Kitchen boundary", box.Position, box.Rotation,
                    box.Size, layer, collision: true, visible: false);
            }
            try { BuildStockVisuals(roomTransform, kitchen); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_FALLBACK {error}"); }

            foreach (var station in kitchen.Stations.OrderBy(row => row.Support != null))
            {
                object transform;
                if (station.Role == "spawn")
                {
                    transform = ResolveSceneObject(roots, station.Path);
                    var old = Get(transform, "position");
                    var rotation = Get(transform, "rotation");
                    restored.Push(() => SetTransform(transform, old, rotation));
                }
                else
                {
                    transform = CloneStockStation(station, roomTransform, roots, entities, components, entityBaseline, componentBaseline);
                    var gameObject = Get(transform, "gameObject");
                    foreach (var location in Enumerate(kitchenGridLocations.Invoke(gameObject, new object[] { true })))
                        Set(location, "m_gridManager", grid);
                    if (station.MotionSpeed is float speed)
                    {
                        var surface = station.Role == "travelator";
                        var mechanism = (surface ? stockTravelator : stockConveyor).Invoke(gameObject, null)
                            ?? throw new InvalidDataException($"Missing native moving surface for {station.Id}.");
                        Set(mechanism, surface ? "m_speed" : "m_conveySpeed", speed);
                    }
                    if (station.Ingredient != null)
                    {
                        var spray = station.Role == "ingredient_spray";
                        var itemSpawner = (spray ? stockSpray : stockSpawner).Invoke(gameObject, null);
                        if (itemSpawner == null)
                            throw new InvalidDataException($"No native ingredient source for {station.Id}.");
                        Set(itemSpawner, spray ? "m_OrderPrefab" : "m_itemPrefab", stockLoad.Ingredients[station.Ingredient]);
                    }
                    if (station.Capacity > 0)
                    {
                        var container = kitchenIngredientContainer.Invoke(gameObject, null) ??
                            throw new InvalidDataException($"Missing native ingredient container for {station.Id} ({station.Role}).");
                        Set(container, "m_capacity", station.Capacity);
                    }
                    if (station.Role is "plate_return" or "glass_return" or "plate_supply")
                    {
                        var plateReturn = kitchenReturn.Invoke(gameObject, null);
                        if (station.PlateCount > 0)
                            Set(plateReturn, "m_startingPlateNumber", station.PlateCount);
                        if (station.ReturnStack != null)
                            Set(plateReturn, "m_stackPrefab", stockLoad.Ingredients[station.ReturnStack]);
                    }
                    if (station.Role == "toasting_fork" && kitchenBoxCollider.Invoke(gameObject, null) is object forkCollider)
                        Set(forkCollider, "size", Vector(new[] { 1f, .1f, .4f }));
                    var support = station.Support == null ? null : Get(placed[station.Support], "gameObject");
                    ApplyStockCookware(station, gameObject, support);
                    try { RepairStockStationMaterials(gameObject); }
                    catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_MATERIAL_FAILED id={station.Id} {error.Message}"); }
                }
                var position = station.Position;
                if (station.Support != null)
                {
                    var parent = kitchenAttachStation.Invoke(Get(placed[station.Support], "gameObject"), null);
                    if (parent == null)
                        throw new InvalidDataException($"No attach station for {station.Id}.");
                    var anchor = Get(Get(parent, "m_attachPoint"), "position");
                    position = new[] { (float)Get(anchor, "x"), (float)Get(anchor, "y") + .04f, (float)Get(anchor, "z") };
                }
                SetTransform(transform, Vector(position), Rotation(station.Rotation));
                placed.Add(station.Id, transform);
                if (station.Role == "serving")
                    CreateKitchenCube(roomTransform, "Serving surface", new[] { position[0], position[1] + .25f, position[2] }, station.Rotation,
                        new[] { 1.1f, .5f, .9f }, 0, collision: false, visible: true);
            }

            ConnectStockReturns(kitchen, placed);
            foreach (var station in kitchen.Stations.Where(row => row.AttachedSpawn != null))
                ConfigureStockAttachedSpawner(station, placed);
            ConnectStockCondiments(kitchen, placed);
            foreach (var station in kitchen.Stations.Where(row => row.PortalExit != null))
            {
                var portal = stockTeleportal.Invoke(Get(placed[station.Id], "gameObject"), null)
                    ?? throw new InvalidDataException($"Missing native portal component for {station.Id}.");
                Set(portal, "m_exitPortal", Get(placed[station.PortalExit], "gameObject"));
                if (station.PortalDoubleSided)
                {
                    try { BuildStockPortalBack(placed[station.Id]); }
                    catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_PORTAL_BACK_FAILED {station.Id}: {error.Message}"); }
                }
                log.LogInfo($"BRIDGE_STOCK_PORTAL {station.Id} -> {station.PortalExit}");
            }
            var origin = Get(grid, "AccessOrigin");
            restored.Push(() => Set(grid, "AccessOrigin", origin));
            AlignStockGrid(grid, kitchen, placed);

            var turnOff = StockHostGroups.Select(path => ResolveSceneObject(roots, path, required: false)).Where(transform => transform != null)
                .Concat(Children(ResolveSceneObject(roots, "Art")).Where(child => (string)Get(child, "name") is not
                    ("Lights" or "AnchorOverride_Chefs" or "AnchorOverride_Plates" or "LP_ProbeAnchor")));
            foreach (var transform in turnOff)
            {
                var gameObject = Get(transform, "gameObject");
                var active = (bool)Get(gameObject, "activeSelf");
                restored.Push(() => SetActive(gameObject, active));
                SetActive(gameObject, false);
            }
            var props = kitchen.Stations.Where(row => row.Support != null).ToDictionary(row => row.Support, StringComparer.Ordinal);
            var planned = new Dictionary<int, KitchenProp>();
            foreach (var station in kitchen.Stations.Where(row => row.Role != "spawn" && row.Support == null))
            {
                var gameObject = Get(placed[station.Id], "gameObject");
                if (kitchenAttachStation.Invoke(gameObject, null) == null)
                    continue;
                planned.Add((int)Call(gameObject, "GetInstanceID"), props.TryGetValue(station.Id, out var prop)
                    ? new KitchenProp(prop.Id, station.Id, Get(placed[prop.Id], "gameObject")) : null);
            }
            kitchenProps = planned;
            var occupied = new HashSet<(int X, int Y, int Z)>();
            var cells = new List<StockGridCell>();
            var overlaps = 0;
            foreach (var station in kitchen.Stations.Where(row => row.Role != "spawn" && row.Support == null))
            {
                var gameObject = Get(placed[station.Id], "gameObject");
                foreach (var location in Enumerate(kitchenGridLocations.Invoke(gameObject, new object[] { true })))
                {
                    var position = Get(Get(location, "transform"), "position");
                    var unclamped = GridCell(kitchenUnclampedCell.Invoke(grid, new[] { position }));
                    var index = kitchenClampedCell.Invoke(grid, new[] { position });
                    var actual = GridCell(index);
                    if (unclamped != actual)
                        throw new InvalidDataException($"Grid clipped source station {station.Id}.");
                    if (!occupied.Add(actual)) overlaps++;
                    if (Convert.ToInt32(Get(location, "m_method")) == 0 && (bool)Get(Get(location, "gameObject"), "activeSelf"))
                        cells.Add(new StockGridCell(station.Id, location, Get(location, "gameObject"), index));
                }
            }
            stockGridCheck = (grid, cells.ToArray());
            // Awake does not unregister a fixture when its parent is disabled.
            RepairStockGrid(activate: false);
            try { InstallStockRecipeMatching(stockGetLevelConfig.Invoke(null, null)); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_RECIPE_CONFIG_FAILED {error}"); }
            SetActive(room, true);
            syncPhysics.Invoke(null, null);
            RepairStockGrid(activate: true);
            log.LogInfo($"BRIDGE_STOCK_KITCHEN placements={placed.Count} boxes={kitchen.Boxes.Length} cells={occupied.Count} overlaps={overlaps}");
            var supplies = kitchen.Stations.Where(row => row.Role == "plate_supply").ToArray();
            log.LogInfo($"BRIDGE_STOCK_VESSELS supplies={supplies.Length} initial={supplies.Sum(row => row.PlateCount)} " +
                        $"returns={kitchen.Stations.Count(row => row.Role is "plate_return" or "glass_return")}");
            log.LogInfo($"BRIDGE_STOCK_CAPACITIES configured={kitchen.Stations.Count(row => row.Capacity > 0)}");
            log.LogInfo($"BRIDGE_STOCK_MATERIALS_REPAIRED count={stockLoad.RepairedMaterials.Count}");
        }
        catch
        {
            kitchenProps.Clear();
            while (restored.TryPop(out var restore))
            {
                try { restore(); }
                catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ROLLBACK {error.Message}"); }
            }
            Truncate(components, componentBaseline.Length);
            Truncate(entities, entityBaseline.Length);
            SetActive(room, false);
            kitchenDestroy.Invoke(null, new[] { room });
            syncPhysics.Invoke(null, null);
            throw;
        }
    }

    private static void ConnectStockCondiments(StockKitchen kitchen, Dictionary<string, object> placed)
    {
        var reversed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dispenser in kitchen.Stations.Where(row => row.CondimentItems != null))
        {
            var instance = Get(placed[dispenser.Id], "gameObject");
            var switcher = stockCondimentSwitcher.Invoke(instance, null);
            var ingredient = kitchenIngredientProperties.Invoke(instance, null);
            var items = dispenser.CondimentItems
                .Select(key => CastKitchenOrder(stockLoad.IngredientNodes[key], stockIngredientType)).ToArray();
            if (switcher == null || ingredient == null || items.Any(item => item == null))
                throw new InvalidDataException($"Missing native condiment components for {dispenser.Id}.");
            var original = Enumerate(Get(switcher, "m_ingredients")).ToArray();
            if (!Equals(Get(original[0], "m_uID"), Get(items[0], "m_uID")))
            {
                reversed.Add(dispenser.Id);
                var effects = stockCondimentEffects.Invoke(instance, null);
                SetNativeObjectArray(effects, "m_particleEffects", kitchenObjectType, Enumerate(Get(effects, "m_particleEffects")).Reverse());
            }
            SetNativeObjectArray(switcher, "m_ingredients", stockIngredientType, items);
            Set(ingredient, "m_ingredientOrderNode", items[0]);
        }
        foreach (var button in kitchen.Stations.Where(row => row.CondimentTargets != null))
        {
            var instance = Get(placed[button.Id], "gameObject");
            var relay = stockSwitchRelay.Invoke(instance, null) ??
                throw new InvalidDataException($"Missing native condiment switch for {button.Id}.");
            var targets = button.CondimentTargets.Select(id => Get(placed[id], "gameObject")).ToArray();
            if (targets.Length == 1)
                Set(relay, "m_targetObject", targets[0]);
            else
            {
                Set(relay, "m_targetObject", null);
                SetNativeObjectArray(relay, "m_targetObjects", kitchenObjectType, targets);
            }
            if (reversed.Contains(button.CondimentTargets[0]))
            {
                var colors = stockCondimentColors.Invoke(instance, null);
                SetNativeObjectArray(colors, "m_materials", stockColorMaterial, Enumerate(Get(colors, "m_materials")).Reverse());
                SetNativeObjectArray(colors, "m_glowEffects", stockColorParticle, Enumerate(Get(colors, "m_glowEffects")).Reverse());
            }
            log.LogInfo($"BRIDGE_STOCK_CONDIMENT_SWITCH {button.Id} -> {string.Join(", ", button.CondimentTargets)}");
        }
    }

    private static void ConfigureStockAttachedSpawner(StockStation station, Dictionary<string, object> placed)
    {
        var feed = station.AttachedSpawn;
        var gameObject = Get(placed[station.Id], "gameObject");
        var spawner = stockAttachedSpawner.Invoke(gameObject, null) ??
            throw new InvalidDataException($"Missing native automatic food source for {station.Id}.");
        var selector = Get(spawner, "m_gridPointSelector") ??
            throw new InvalidDataException($"Missing native conveyor selector for {station.Id}.");
        var target = Get(placed[feed.Target], "position");
        var height = (float)Get(Get(selector, "position"), "y");
        Set(selector, "position", Vector(new[] { (float)Get(target, "x"), height, (float)Get(target, "z") }));
        var recipes = feed.Items.Select((key, i) =>
        {
            var weighted = Activator.CreateInstance(stockWeightedPrefab, nonPublic: true);
            Set(weighted, "AttachmentPrefab", stockLoad.Ingredients[key]);
            Set(weighted, "m_weight", feed.Weights[i]);
            return weighted;
        });
        SetNativeObjectArray(spawner, "m_attachmentPrefabs", stockWeightedPrefab, recipes);
        Set(spawner, "m_spawnInOrder", feed.InOrder);

        var timers = Enumerate(stockSpawnTimers.Invoke(gameObject, null)).ToArray();
        var trigger = (string)Get(spawner, "m_trigger");
        var repeating = timers.Single(timer => Equals(Get(timer, "m_startTrigger"), trigger));
        var initial = timers.Single(timer => string.IsNullOrEmpty((string)Get(timer, "m_startTrigger")));
        Set(repeating, "m_time", feed.Seconds);
        Set(initial, "m_time", feed.AtStart ? .1f : feed.Seconds);
        log.LogInfo($"BRIDGE_STOCK_FOOD_SOURCE {station.Id} -> {feed.Target} ({feed.Seconds}s)");
    }

    private static void AlignStockGrid(object grid, StockKitchen kitchen, Dictionary<string, object> placed)
    {
        var axes = Get(grid, "transform");
        var samples = kitchen.Stations.Where(station => station.Role is "counter" or "chopping").Select(station =>
        {
            var position = Get(placed[station.Id], "position");
            var index = kitchenUnclampedCell.Invoke(grid, new[] { position });
            var centre = stockCellCenter.Invoke(grid, new[] { index });
            var localPosition = stockInversePoint.Invoke(axes, new[] { position });
            var localCentre = stockInversePoint.Invoke(axes, new[] { centre });
            return (X: (float)Get(localPosition, "x") - (float)Get(localCentre, "x"),
                    Z: (float)Get(localPosition, "z") - (float)Get(localCentre, "z"));
        }).ToArray();
        if (samples.Length < 3 || samples.Any(sample => Math.Abs(sample.X - samples[0].X) > .035f ||
            Math.Abs(sample.Z - samples[0].Z) > .035f))
            return;
        var dx = samples.Average(sample => sample.X);
        var dz = samples.Average(sample => sample.Z);
        if (Math.Abs(dx) <= .01f && Math.Abs(dz) <= .01f)
            return;
        var origin = Get(grid, "AccessOrigin");
        Set(grid, "AccessOrigin", Vector(new[] { (float)Get(origin, "x") + dx, (float)Get(origin, "y"), (float)Get(origin, "z") + dz }));
        log.LogInfo($"BRIDGE_STOCK_GRID_ALIGNMENT x={dx:F3} z={dz:F3} samples={samples.Length}");
    }

    private static void RepairStockGrid(bool activate)
    {
        if (stockGridCheck is not { } grid)
            return;
        int repaired = 0;
        var mismatches = new List<string>();
        foreach (var cell in grid.Cells)
        {
            if (activate && !(bool)Get(cell.Item, "activeInHierarchy"))
                continue;
            try
            {
                var occupant = stockCellOccupant.Invoke(grid.Grid, new[] { cell.Index });
                if (occupant != null && KitchenObjectId(occupant) != KitchenObjectId(cell.Item) &&
                    !(bool)Get(occupant, "activeInHierarchy"))
                {
                    stockRemoveOccupant.Invoke(grid.Grid, new[] { cell.Index, occupant });
                    occupant = stockCellOccupant.Invoke(grid.Grid, new[] { cell.Index });
                    repaired++;
                }
                if (!activate)
                    continue;
                if (occupant == null)
                {
                    Call(cell.Location, "Initialise");
                    occupant = stockCellOccupant.Invoke(grid.Grid, new[] { cell.Index });
                    if (occupant != null && KitchenObjectId(occupant) == KitchenObjectId(cell.Item))
                        repaired++;
                }
                if (occupant == null || KitchenObjectId(occupant) != KitchenObjectId(cell.Item))
                    mismatches.Add($"{cell.Id}:{GridCell(cell.Index)}={ (occupant == null ? "empty" : Get(occupant, "name")) }");
            }
            catch (Exception error) { mismatches.Add($"{cell.Id}:{error.GetBaseException().Message}"); }
        }
        if (activate || repaired != 0)
            log.LogInfo($"BRIDGE_STOCK_GRID_OCCUPANCY checked={grid.Cells.Length} repaired={repaired} mismatches={mismatches.Count}");
        if (mismatches.Count > 0)
            log.LogWarning($"BRIDGE_STOCK_GRID_MISMATCH {string.Join(" | ", mismatches.Take(12))}");
    }

    private static object CloneStockStation(StockStation station, object parent, ILookup<string, object> roots, object entities,
        object components, object[] entityBaseline, object[] componentBaseline)
    {
        object source;
        DonorPrototype prototype = null;
        if (station.Scene == KevinScene)
            source = ResolveSceneObject(roots, station.Path);
        else if (station.Scene == "asset")
            source = Get(stockLoad.Ingredients[station.Path], "transform");
        else
        {
            prototype = stockLoad.Prototypes[(station.Scene, station.Path)];
            source = prototype.Transform;
        }

        var clone = kitchenInstantiate.Invoke(null, new[] { Get(source, "gameObject"), parent, false });
        Set(clone, "name", $"{station.Role} ({station.Id})");
        SetActive(clone, true);
        var transform = Get(clone, "transform");
        if (station.Scene == "asset")
        {
            AddStockRegistry(entities, kitchenObjectType, new[] { clone });
            AddStockRegistry(components, kitchenComponentType,
                Enumerate(stockMonoBehaviours.Invoke(clone, new object[] { true })).Where(item => item != null));
        }
        else
        {
            var copies = new Dictionary<int, object>();
            MapKitchenClone(source, transform, copies);
            var entityRows = RemapRegistry(prototype?.Entities ?? entityBaseline, copies);
            var componentRows = RemapRegistry(prototype?.Components ?? componentBaseline, copies);
            if (entityRows.Length == 0 && station.Role != "travelator")
                throw new InvalidDataException($"Native donor is missing entity registration: {station.Path}.");
            AddStockRegistry(entities, kitchenObjectType, entityRows);
            AddStockRegistry(components, kitchenComponentType, componentRows);
        }
        return transform;
    }
}
