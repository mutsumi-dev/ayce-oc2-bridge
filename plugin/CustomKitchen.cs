using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace AyceOc2Bridge;

public sealed partial class Plugin
{
    private static readonly Dictionary<string, int> KitchenRoles = new(StringComparer.Ordinal)
    {
        ["spawn"] = 4, ["counter"] = 17, ["chopping"] = 3, ["dispenser"] = 4, ["mixer"] = 3,
        ["plate_return"] = 1, ["serving"] = 1, ["sink"] = 1, ["stove"] = 3, ["trash"] = 1,
        ["plate"] = 4, ["extinguisher"] = 1, ["mixing_bowl"] = 3, ["steamer"] = 3,
    };
    private static readonly Dictionary<string, string> KitchenSupport = new(StringComparer.Ordinal)
    {
        ["plate"] = "counter", ["extinguisher"] = "counter", ["mixing_bowl"] = "mixer", ["steamer"] = "stove",
    };
    private static readonly HashSet<string> InactiveTemplateObjects = new(StringComparer.Ordinal)
    {
        "Design/Work Surfaces/countertop_01_standard_wood",
        "Design/Work Surfaces/countertop_01_standard_wood (1)",
        "Design/Animated Objects/Kitchen/Block_4/DispenserCrate 3 (1)",
    };

    private static Type kitchenObjectType;
    private static Type kitchenComponentType;
    private static Type kitchenTransformType;
    private static MethodInfo kitchenSceneMove;
    private static MethodInfo kitchenSetParent;
    private static MethodInfo kitchenInstantiate;
    private static MethodInfo kitchenDestroy;
    private static MethodInfo kitchenComponents;
    private static MethodInfo kitchenCreatePrimitive;
    private static MethodInfo kitchenLevelData;
    private static MethodInfo kitchenGrid;
    private static MethodInfo kitchenGridLocations;
    private static MethodInfo kitchenUnclampedCell;
    private static MethodInfo kitchenClampedCell;
    private static MethodInfo kitchenAttachStation;
    private static MethodInfo kitchenAttachObject;
    private static MethodInfo kitchenBoxCollider;
    private static MethodInfo kitchenMeshRenderer;
    private static object kitchenCube;
    private static bool applyingKitchen;
    private static int? kitchenHandle;
    private static int? failedKitchenHandle;
    private static Dictionary<int, KitchenProp> kitchenProps = new();

    private sealed record KitchenProp(string Source, string Support, object Item);

    private static void ValidateKitchen(Overlay value)
    {
        var kitchen = value.Kitchen;
        if (kitchen == null)
            return;
        if (value.Scene != KevinScene || value.CampaignRules == null || value.Moves.Length != 0 ||
            kitchen.Placements?.Length != KitchenRoles.Values.Sum() || kitchen.Boxes?.Length != 9 ||
            kitchen.Disabled?.Length != InactiveTemplateObjects.Count || !InactiveTemplateObjects.SetEquals(kitchen.Disabled))
            throw new InvalidDataException("The kitchen layout does not match the supported AYCE template.");

        var source = new Dictionary<string, KitchenPlacement>(StringComparer.Ordinal);
        var original = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var move in kitchen.Placements)
        {
            if (move == null || !LayoutPath(move.Source) || !LayoutPath(move.Target) ||
                move.Role == null || !KitchenRoles.ContainsKey(move.Role) || !source.TryAdd(move.Source, move) ||
                (!move.Clone && !original.TryAdd(move.Target, move.Role)) || InactiveTemplateObjects.Contains(move.Target) ||
                !ValidVector(move.Position, 3) || move.Position.Any(n => Math.Abs(n) > 32f) || !LayoutRotation(move.Rotation) ||
                move.Reparent != move.Target.StartsWith("Design/Animated Objects/", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid kitchen placement: {move?.Source}.");
        }
        foreach (var (role, count) in KitchenRoles)
        {
            var rows = kitchen.Placements.Where(move => move.Role == role).ToArray();
            var copies = role == "counter" ? 2 : role is "mixer" or "mixing_bowl" ? 1 : 0;
            if (rows.Length != count || rows.Count(move => move.Clone) != copies)
                throw new InvalidDataException($"Unexpected object count for {role}.");
        }
        foreach (var move in kitchen.Placements)
        {
            if (move.Clone && (!original.TryGetValue(move.Target, out var donorRole) || donorRole != move.Role))
                throw new InvalidDataException($"Missing native donor for {move.Source}.");
            if (!KitchenSupport.TryGetValue(move.Role, out var supportRole))
            {
                if (move.Support != null)
                    throw new InvalidDataException($"Unexpected support for {move.Source}.");
                continue;
            }
            if (move.Support == null || !source.TryGetValue(move.Support, out var support) || support.Role != supportRole ||
                Math.Abs(support.Position[0] - move.Position[0]) > .01f || Math.Abs(support.Position[2] - move.Position[2]) > .01f)
                throw new InvalidDataException($"Missing or mismatched station for {move.Source}.");
        }
        if (kitchen.Boxes.Count(box => box?.Layer == 9) != 1 || kitchen.Boxes.Any(box =>
                box == null || box.Layer is not (9 or 10) || !ValidVector(box.Position, 3) || !LayoutRotation(box.Rotation) ||
                !ValidVector(box.Size, 3) || box.Size.Any(n => n is <= 0f or > 32f) ||
                box.Path == null || !box.Path.StartsWith("Design/Collision/", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid kitchen collision boxes.");
    }

    private static bool LayoutPath(string path) => path?.Length is > 6 and <= 160 &&
        (path.StartsWith("Design/", StringComparison.Ordinal) || path.StartsWith("Chefs/", StringComparison.Ordinal)) &&
        path.Split('/').All(part => part.Length != 0 && part is not ("." or ".."));

    private static bool LayoutRotation(float[] rotation) => ValidVector(rotation, 4) &&
        Math.Abs(rotation.Sum(value => value * value) - 1f) < .005f;

    private static void InstallKitchenHooks(Harmony harmony, Assembly game, Assembly unity)
    {
        kitchenObjectType = unity.GetType("UnityEngine.GameObject", throwOnError: true);
        kitchenComponentType = unity.GetType("UnityEngine.Component", throwOnError: true);
        kitchenTransformType = unity.GetType("UnityEngine.Transform", throwOnError: true);
        var unityObject = unity.GetType("UnityEngine.Object", throwOnError: true);
        var scene = unity.GetType("UnityEngine.SceneManagement.Scene", throwOnError: true);
        var scenes = unity.GetType("UnityEngine.SceneManagement.SceneManager", throwOnError: true);
        var primitive = unity.GetType("UnityEngine.PrimitiveType", throwOnError: true);
        kitchenCube = Enum.Parse(primitive, "Cube");
        kitchenCreatePrimitive = Find(kitchenObjectType, "CreatePrimitive", new[] { primitive });
        kitchenSceneMove = Find(scenes, "MoveGameObjectToScene", new[] { kitchenObjectType, scene });
        kitchenSetParent = Find(kitchenTransformType, "SetParent", new[] { kitchenTransformType, typeof(bool) });
        kitchenDestroy = Find(unityObject, "Destroy", new[] { unityObject });
        kitchenInstantiate = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Instantiate" && method.IsGenericMethodDefinition &&
                              method.GetParameters() is var args && args.Length == 3 && args[1].ParameterType == kitchenTransformType && args[2].ParameterType == typeof(bool))
            .MakeGenericMethod(kitchenObjectType);
        kitchenComponents = GenericGameObjectMethod("GetComponents", kitchenComponentType);

        var level = game.GetType("LevelData", throwOnError: true);
        kitchenLevelData = GenericGameObjectMethod("GetComponent", level);
        var grid = game.GetType("QuadGridManager", throwOnError: true);
        kitchenGrid = GenericGameObjectMethod("GetComponent", grid);
        var location = game.GetType("StaticGridLocation", throwOnError: true);
        kitchenGridLocations = kitchenObjectType.GetMethods()
            .Single(method => method.Name == "GetComponentsInChildren" && method.IsGenericMethodDefinition &&
                              method.GetParameters() is var args && args.Length == 1 && args[0].ParameterType == typeof(bool))
            .MakeGenericMethod(location);
        kitchenUnclampedCell = Find(grid, "GetUnclampedGridLocationFromPos", new[] { vectorType });
        kitchenClampedCell = Find(grid, "GetGridLocationFromPos", new[] { vectorType });
        kitchenAttachStation = GenericGameObjectMethod("GetComponent", game.GetType("AttachStation", throwOnError: true));
        var serverAttach = game.GetType("ServerAttachStation", throwOnError: true);
        kitchenAttachObject = Find(serverAttach, "AttachObject", new[] { kitchenObjectType, game.GetType("PlacementContext", throwOnError: true) });
        kitchenMeshRenderer = GenericGameObjectMethod("GetComponent", unity.GetType("UnityEngine.MeshRenderer", throwOnError: true));
        var physics = Assembly.Load("UnityEngine.PhysicsModule");
        kitchenBoxCollider = GenericGameObjectMethod("GetComponent", physics.GetType("UnityEngine.BoxCollider", throwOnError: true));
        syncPhysics = Find(physics.GetType("UnityEngine.Physics", throwOnError: true), "SyncTransforms", Type.EmptyTypes);
        Hook(harmony, Find(level, "Awake", Type.EmptyTypes), nameof(BeforeLayoutLevelAwake));
        Hook(harmony, Find(serverAttach, "AttachInitialObjects", Type.EmptyTypes), nameof(BeforeAttachKitchenProps));
    }

    private static MethodInfo GenericGameObjectMethod(string name, Type target) => kitchenObjectType.GetMethods()
        .Single(method => method.Name == name && method.IsGenericMethodDefinition && method.GetParameters().Length == 0)
        .MakeGenericMethod(target);

    private static void BeforeLayoutLevelAwake(object __instance)
    {
        if (!CustomLaunchPending || applyingKitchen)
            return;
        var scene = SceneOf(__instance);
        var handle = Handle(scene);
        if (SceneName(scene) != overlay.Scene || refusedHandle == handle)
            return;
        try
        {
            if ((bool)isSteamLoggedOn.Invoke(null, null))
                throw new InvalidOperationException("Steam Offline Mode is required for the custom kitchen.");
            ApplyKitchenOnce(scene);
        }
        catch (Exception error)
        {
            refusedHandle = handle;
            log.LogError($"BRIDGE_KITCHEN_REJECTED {error}");
        }
    }

    private static void ApplyKitchenOnce(object scene)
    {
        var handle = Handle(scene);
        if (kitchenHandle == handle)
            return;
        applyingKitchen = true;
        try
        {
            PlaceCustomKitchen(scene, overlay.Kitchen);
            kitchenHandle = handle;
        }
        catch
        {
            failedKitchenHandle = handle;
            launchExpires = 0;
            throw;
        }
        finally
        {
            applyingKitchen = false;
        }
    }

    private static void PlaceCustomKitchen(object scene, KitchenLayout kitchen)
    {
        var roots = Enumerate(Call(scene, "GetRootGameObjects")).ToLookup(item => (string)Get(item, "name"), StringComparer.Ordinal);
        object Resolve(string path)
        {
            var parts = path.Split('/', 2);
            var root = Get(roots[parts[0]].Single(), "transform");
            return parts.Length == 1 ? root : (Find(kitchenTransformType, "Find", new[] { typeof(string) })
                .Invoke(root, new object[] { parts[1] }) ?? throw new InvalidDataException($"Missing AYCE object: {path}."));
        }

        var template = kitchen.Placements.Select(move => move.Target).Distinct(StringComparer.Ordinal)
            .ToDictionary(path => path, Resolve, StringComparer.Ordinal);
        var turnOff = kitchen.Disabled.Concat(new[] { "Design/Collision", "Design/Animated Objects" })
            .Select(Resolve).Concat(Children(Resolve("Art")).Where(child => (string)Get(child, "name") is not
                ("Lights" or "AnchorOverride_Chefs" or "AnchorOverride_Plates" or "LP_ProbeAnchor"))).ToArray();
        var level = kitchenLevelData.Invoke(Get(Resolve("Level Data"), "gameObject"), null);
        var grid = kitchenGrid.Invoke(Get(Resolve("CampaignGameEnvironment/GridManager"), "gameObject"), null);
        var entities = Get(level, "m_EntityRegistry");
        var components = Get(level, "m_EntityRegistryComponents");
        var entityBaseline = SnapshotNativeList(entities);
        var componentBaseline = SnapshotNativeList(components);
        if (entityBaseline.Length != 58 || componentBaseline.Length != 369)
            throw new InvalidDataException("The native Kevin 2 entity lists differ from the supported template.");
        var rollback = new Stack<Action>();
        var room = Activator.CreateInstance(kitchenObjectType, "DTsimple Kitchen");
        try
        {
            var roomTransform = Get(room, "transform");
            if (Handle(Get(room, "scene")) != Handle(scene))
                kitchenSceneMove.Invoke(null, new[] { room, scene });
            Parent(roomTransform, Resolve("Design"));

            var staging = Activator.CreateInstance(kitchenObjectType, "Inactive template copies");
            var stagingTransform = Get(staging, "transform");
            Parent(stagingTransform, roomTransform);
            SetActive(staging, false);
            var resolved = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var move in kitchen.Placements)
            {
                var donor = template[move.Target];
                if (!move.Clone)
                {
                    resolved.Add(move.Source, donor);
                    continue;
                }
                var clone = kitchenInstantiate.Invoke(null, new[] { Get(donor, "gameObject"), stagingTransform, false });
                SetActive(clone, false);
                Set(clone, "name", move.Source.Split('/')[^1]);
                var transform = Get(clone, "transform");
                AppendKitchenClone(donor, transform, entities, components, entityBaseline, componentBaseline);
                resolved.Add(move.Source, transform);
            }

            foreach (var box in kitchen.Boxes)
                CreateKitchenCube(roomTransform, box.Path.Split('/')[^1], box.Position, box.Rotation, box.Size, box.Layer, collision: true, visible: false);
            CreateKitchenCube(roomTransform, "Kitchen floor", new[] { 10.8f, -1.08f, 3.6f }, new[] { 0f, 0f, 0f, 1f },
                new[] { 18f, .12f, 13.2f }, 9, collision: false, visible: true);
            foreach (var box in kitchen.Boxes.Where(box => box.Layer == 10))
                CreateKitchenCube(roomTransform, "Wall edge", new[] { box.Position[0], -.79f, box.Position[2] }, box.Rotation,
                    new[] { box.Size[0], .42f, box.Size[2] }, 10, collision: false, visible: true);

            foreach (var move in kitchen.Placements.OrderBy(move => move.Support != null))
            {
                var transform = resolved[move.Source];
                if (!move.Clone)
                {
                    var oldParent = Get(transform, "parent");
                    var oldPosition = Get(transform, "position");
                    var oldRotation = Get(transform, "rotation");
                    rollback.Push(() =>
                    {
                        if (move.Reparent)
                            Parent(transform, oldParent);
                        SetTransform(transform, oldPosition, oldRotation);
                    });
                }
                if (move.Clone || move.Reparent)
                    Parent(transform, roomTransform);
                var position = move.Position;
                if (move.Support != null)
                {
                    var station = kitchenAttachStation.Invoke(Get(resolved[move.Support], "gameObject"), null);
                    var anchor = Get(Get(station, "m_attachPoint"), "position");
                    position = new[] { (float)Get(anchor, "x"), (float)Get(anchor, "y") + .04f, (float)Get(anchor, "z") };
                }
                SetTransform(transform, Vector(position), Rotation(move.Rotation));
            }
            // The template origin is relative to the grid object's transform. Keep it in native space.
            ValidateKitchenGrid(grid, kitchen.Placements, resolved);

            var serving = kitchen.Placements.Single(move => move.Role == "serving");
            CreateKitchenCube(roomTransform, "Serving surface", new[] { serving.Position[0], serving.Position[1] + .25f, serving.Position[2] },
                serving.Rotation, new[] { 1.1f, .5f, .9f }, 0, collision: false, visible: true);

            foreach (var transform in turnOff)
            {
                var item = Get(transform, "gameObject");
                var oldActive = (bool)Get(item, "activeSelf");
                rollback.Push(() => SetActive(item, oldActive));
                SetActive(item, false);
            }
            var authoredProps = ReserveKitchenProps(kitchen, resolved);
            foreach (var move in kitchen.Placements.Where(move => move.Clone))
                SetActive(Get(resolved[move.Source], "gameObject"), true);
            syncPhysics.Invoke(null, null);
            kitchenProps = authoredProps;
            log.LogInfo($"BRIDGE_KITCHEN_APPLIED placements={resolved.Count} boxes={kitchen.Boxes.Length} mixerCount=3 " +
                        $"addedEntities={(int)Get(entities, "Count") - entityBaseline.Length} addedComponents={(int)Get(components, "Count") - componentBaseline.Length}");
        }
        catch
        {
            while (rollback.TryPop(out var restore))
            {
                try { restore(); }
                catch (Exception error) { log.LogError($"BRIDGE_KITCHEN_ROLLBACK_ERROR {error}"); }
            }
            try
            {
                Truncate(components, componentBaseline.Length);
                Truncate(entities, entityBaseline.Length);
                SetActive(room, false);
                kitchenDestroy.Invoke(null, new[] { room });
                syncPhysics.Invoke(null, null);
            }
            catch (Exception error) { log.LogError($"BRIDGE_KITCHEN_ROLLBACK_ERROR {error}"); }
            throw;
        }
    }

    private static void ValidateKitchenGrid(object grid, IEnumerable<KitchenPlacement> placements, Dictionary<string, object> resolved)
    {
        var cells = new Dictionary<(int X, int Y, int Z), string>();
        foreach (var move in placements.Where(move => move.Support == null && move.Role != "spawn"))
        {
            var item = Get(resolved[move.Source], "gameObject");
            var locations = Enumerate(kitchenGridLocations.Invoke(item, new object[] { true }));
            foreach (var location in locations)
            {
                var position = Get(Get(location, "transform"), "position");
                var expected = GridCell(kitchenUnclampedCell.Invoke(grid, new[] { position }));
                var actual = GridCell(kitchenClampedCell.Invoke(grid, new[] { position }));
                if (expected != actual)
                    throw new InvalidDataException($"Kitchen grid clipped {move.Source} from {expected} to {actual}.");
                if (!cells.TryAdd(actual, move.Source))
                    throw new InvalidDataException($"Kitchen grid cell {actual} is shared by {cells[actual]} and {move.Source}.");
            }
        }
        if (cells.Count != 34)
            throw new InvalidDataException($"Expected 34 native kitchen grid cells; found {cells.Count}.");
        log.LogInfo($"BRIDGE_KITCHEN_GRID cells={cells.Count} clipped=0 overlaps=0");
    }

    private static (int X, int Y, int Z) GridCell(object index) => ((int)Get(index, "X"), (int)Get(index, "Y"), (int)Get(index, "Z"));

    private static Dictionary<int, KitchenProp> ReserveKitchenProps(KitchenLayout kitchen, Dictionary<string, object> resolved)
    {
        var props = kitchen.Placements.Where(move => move.Support != null).ToLookup(move => move.Support, StringComparer.Ordinal);
        var planned = new Dictionary<int, KitchenProp>();
        foreach (var move in kitchen.Placements.Where(move => move.Support == null && move.Role != "spawn"))
        {
            var support = Get(resolved[move.Source], "gameObject");
            var item = props[move.Source].SingleOrDefault();
            var component = kitchenAttachStation.Invoke(support, null);
            if (component == null)
            {
                if (item != null)
                    throw new InvalidDataException($"Missing native attach station for {item.Source}.");
                continue;
            }
            planned.Add((int)Call(support, "GetInstanceID"), item == null ? null :
                new KitchenProp(item.Source, move.Source, Get(resolved[item.Source], "gameObject")));
        }
        return planned;
    }

    private static bool BeforeAttachKitchenProps(object __instance)
    {
        if (kitchenHandle == null || kitchenProps.Count == 0)
            return true;
        try
        {
            var station = Get(Get(__instance, "m_station"), "gameObject");
            if (!kitchenProps.TryGetValue((int)Call(station, "GetInstanceID"), out var prop))
                return true;
            if (prop == null)
                return false;

            kitchenAttachObject.Invoke(__instance, new[] { prop.Item, null });
            var placed = Call(__instance, "InspectItem");
            if (placed != null && (int)Call(placed, "GetInstanceID") == (int)Call(prop.Item, "GetInstanceID"))
                log.LogInfo($"BRIDGE_KITCHEN_PROP_ATTACHED item={prop.Source} station={prop.Support}");
            else
                log.LogWarning($"BRIDGE_KITCHEN_PROP_REJECTED item={prop.Source} station={prop.Support}");
            return false;
        }
        catch (Exception error)
        {
            log.LogWarning($"BRIDGE_KITCHEN_PROP_ERROR {error}");
            return true;
        }
    }

    private static void AppendKitchenClone(object donor, object clone, object entities, object components,
        object[] entityBaseline, object[] componentBaseline)
    {
        var copies = new Dictionary<int, object>();
        MapKitchenClone(donor, clone, copies);
        var originalEntityId = (int)Call(Get(donor, "gameObject"), "GetInstanceID");
        if (!entityBaseline.Any(item => item != null && (int)Call(item, "GetInstanceID") == originalEntityId))
            throw new InvalidDataException("A clone donor is missing from the native entity list.");

        void Append(object list, Type type, object[] baseline)
        {
            var add = Find(list.GetType(), "Add", new[] { type });
            foreach (var item in baseline)
            {
                if (item != null && copies.TryGetValue((int)Call(item, "GetInstanceID"), out var copy))
                    add.Invoke(list, new[] { copy });
            }
        }
        Append(entities, kitchenObjectType, entityBaseline);
        Append(components, kitchenComponentType, componentBaseline);
    }

    private static void MapKitchenClone(object donor, object clone, Dictionary<int, object> copies)
    {
        var original = Get(donor, "gameObject");
        var copy = Get(clone, "gameObject");
        copies.Add((int)Call(original, "GetInstanceID"), copy);
        var originalComponents = Enumerate(kitchenComponents.Invoke(original, null)).ToArray();
        var cloneComponents = Enumerate(kitchenComponents.Invoke(copy, null)).ToArray();
        var originalChildren = Children(donor).ToArray();
        var cloneChildren = Children(clone).ToArray();
        if (originalComponents.Length != cloneComponents.Length || originalChildren.Length != cloneChildren.Length)
            throw new InvalidDataException("A native clone has a different component or transform count.");
        for (var index = 0; index < originalComponents.Length; index++)
        {
            if (originalComponents[index] == null && cloneComponents[index] == null)
                continue;
            if (originalComponents[index] == null || cloneComponents[index] == null)
                throw new InvalidDataException("A native clone lost a component.");
            copies.Add((int)Call(originalComponents[index], "GetInstanceID"), cloneComponents[index]);
        }
        for (var index = 0; index < originalChildren.Length; index++)
            MapKitchenClone(originalChildren[index], cloneChildren[index], copies);
    }

    private static void Truncate(object list, int baseline)
    {
        var remove = Find(list.GetType(), "RemoveAt", new[] { typeof(int) });
        for (var index = (int)Get(list, "Count") - 1; index >= baseline; index--)
            remove.Invoke(list, new object[] { index });
    }

    private static object[] SnapshotNativeList(object list)
    {
        var item = Find(list.GetType(), "get_Item", new[] { typeof(int) });
        var snapshot = new object[(int)Get(list, "Count")];
        for (var index = 0; index < snapshot.Length; index++)
            snapshot[index] = item.Invoke(list, new object[] { index });
        return snapshot;
    }

    private static IEnumerable<object> Enumerate(object items) => ((IEnumerable)items).Cast<object>();

    private static IEnumerable<object> Children(object transform)
    {
        var getChild = Find(kitchenTransformType, "GetChild", new[] { typeof(int) });
        var count = (int)Get(transform, "childCount");
        for (var index = 0; index < count; index++)
            yield return getChild.Invoke(transform, new object[] { index });
    }

    private static object Vector(float[] values) => Activator.CreateInstance(vectorType, values.Cast<object>().ToArray());
    private static object Rotation(float[] values) => Activator.CreateInstance(rotationType, values.Cast<object>().ToArray());

    private static void Parent(object transform, object parent) => kitchenSetParent.Invoke(transform, new[] { parent, true });
    private static void SetActive(object item, bool active) => Find(kitchenObjectType, "SetActive", new[] { typeof(bool) })
        .Invoke(item, new object[] { active });

    private static void Set(object owner, string member, object value)
    {
        var type = owner.GetType();
        if (AccessTools.Property(type, member) is PropertyInfo property)
            property.SetValue(owner, value);
        else
            (AccessTools.Field(type, member) ?? throw new MissingMemberException(type.FullName, member)).SetValue(owner, value);
    }

    private static void CreateKitchenCube(object parent, string name, float[] position, float[] rotation, float[] size,
        int layer, bool collision, bool visible)
    {
        var cube = kitchenCreatePrimitive.Invoke(null, new[] { kitchenCube });
        var transform = Get(cube, "transform");
        Parent(transform, parent);
        Set(cube, "name", name);
        Set(cube, "layer", layer);
        Set(transform, "localScale", Vector(size));
        SetTransform(transform, Vector(position), Rotation(rotation));
        if (!collision)
            Set(kitchenBoxCollider.Invoke(cube, null), "enabled", false);
        if (!visible)
            Set(kitchenMeshRenderer.Invoke(cube, null), "enabled", false);
    }
}
