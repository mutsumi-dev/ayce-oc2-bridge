using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BepInEx;

namespace AyceOc2Bridge;

internal sealed class ArtScene
{
    [JsonPropertyName("format")] public int Format { get; set; }
    [JsonPropertyName("nodes")] public ArtNode[] Nodes { get; set; }
    [JsonPropertyName("materials")] public ArtMaterial[] Materials { get; set; }
}

internal sealed class ArtNode
{
    [JsonPropertyName("name")] public string Name { get; set; }
    [JsonPropertyName("parent")] public int Parent { get; set; }
    [JsonPropertyName("position")] public float[] Position { get; set; }
    [JsonPropertyName("rotation")] public float[] Rotation { get; set; }
    [JsonPropertyName("scale")] public float[] Scale { get; set; }
    [JsonPropertyName("mesh")] public string Mesh { get; set; }
    [JsonPropertyName("primitive")] public string Primitive { get; set; }
    [JsonPropertyName("nativeAsset")] public string NativeAsset { get; set; }
    [JsonPropertyName("materials")] public int[] Materials { get; set; }
}

internal sealed class ArtMaterial
{
    [JsonPropertyName("color")] public float[] Color { get; set; }
    [JsonPropertyName("tiling")] public float[] Tiling { get; set; }
    [JsonPropertyName("offset")] public float[] Offset { get; set; }
    [JsonPropertyName("texture")] public string Texture { get; set; }
    [JsonPropertyName("mode")] public int Mode { get; set; }
}

public sealed partial class Plugin
{
    private static bool stockArtReady;
    private static Type artMesh, artMaterial, artTexture, artColor, artVector2, artPrimitive;
    private static MethodInfo artShaderFind, artAddFilter, artAddRenderer, artReadFilter, artCollider, artLoadImage;
    private static MethodInfo artSetTriangles, artRenderers, artHasProperty, artGetTexture, artGetColor;
    private static PropertyInfo artVertices, artUv, artNormals, artMaterials, artIndexFormat;

    private static void TryInstallStockArt(Assembly unity)
    {
        try
        {
            artMesh = unity.GetType("UnityEngine.Mesh", true);
            artMaterial = unity.GetType("UnityEngine.Material", true);
            artTexture = unity.GetType("UnityEngine.Texture2D", true);
            artColor = unity.GetType("UnityEngine.Color", true);
            artVector2 = unity.GetType("UnityEngine.Vector2", true);
            artPrimitive = unity.GetType("UnityEngine.PrimitiveType", true);
            artShaderFind = Find(unity.GetType("UnityEngine.Shader", true), "Find", new[] { typeof(string) });
            var filter = unity.GetType("UnityEngine.MeshFilter", true);
            var renderer = unity.GetType("UnityEngine.MeshRenderer", true);
            artReadFilter = GenericGameObjectMethod("GetComponent", filter);
            artAddFilter = GenericGameObjectMethod("AddComponent", filter);
            artAddRenderer = GenericGameObjectMethod("AddComponent", renderer);
            artCollider = GenericGameObjectMethod("GetComponent", Assembly.Load("UnityEngine.PhysicsModule").GetType("UnityEngine.Collider", true));
            artLoadImage = Assembly.Load("UnityEngine.ImageConversionModule").GetType("UnityEngine.ImageConversion", true)
                .GetMethods().Single(method => method.Name == "LoadImage" && method.GetParameters().Length == 3);
            artVertices = RequireProperty(artMesh, "vertices");
            artUv = RequireProperty(artMesh, "uv");
            artNormals = RequireProperty(artMesh, "normals");
            var rendererBase = unity.GetType("UnityEngine.Renderer", true);
            artMaterials = RequireProperty(rendererBase, "sharedMaterials");
            artRenderers = kitchenObjectType.GetMethods().Single(method => method.Name == "GetComponentsInChildren" &&
                method.IsGenericMethodDefinition && method.GetParameters() is var args &&
                args.Length == 1 && args[0].ParameterType == typeof(bool)).MakeGenericMethod(rendererBase);
            artHasProperty = Find(artMaterial, "HasProperty", new[] { typeof(string) });
            artGetTexture = Find(artMaterial, "GetTexture", new[] { typeof(string) });
            artGetColor = Find(artMaterial, "GetColor", new[] { typeof(string) });
            artIndexFormat = RequireProperty(artMesh, "indexFormat");
            artSetTriangles = artMesh.GetMethods().Single(method => method.Name == "SetTriangles" &&
                method.GetParameters() is var args && args.Length == 3 && args[1].ParameterType == typeof(int) &&
                args[2].ParameterType == typeof(bool) && args[0].ParameterType.IsGenericType &&
                args[0].ParameterType.Name.StartsWith("Il2CppStructArray", StringComparison.Ordinal) &&
                args[0].ParameterType.GetGenericArguments()[0] == typeof(int));
            stockArtReady = true;
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_API_FAILED {error}"); }
    }

    private static string ArtPath(StockKitchen kitchen) => Path.Combine(Paths.PluginPath, "AyceOc2Bridge", "art", kitchen.Art);

    private static ArtScene ReadArtManifest(string path)
    {
        if (new FileInfo(path).Length > 96L * 1024 * 1024)
            throw new InvalidDataException("The art archive is too large.");
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count > 500 || archive.Entries.Sum(entry => entry.Length) > 256L * 1024 * 1024)
            throw new InvalidDataException("The art archive exceeds the import budget.");
        using var stream = ArtEntry(archive, "scene.json", 4 * 1024 * 1024);
        var data = JsonSerializer.Deserialize<ArtScene>(stream);
        if (data?.Format != 1 || data.Nodes?.Length is not (> 0 and <= 2400) || data.Materials == null || data.Materials.Length > 300)
            throw new InvalidDataException("Invalid art description.");
        for (var index = 0; index < data.Nodes.Length; ++index)
        {
            var node = data.Nodes[index];
            if (node?.Name == null || node.Name.Length > 120 || node.Parent < -1 || node.Parent >= index ||
                !ValidVector(node.Position, 3) || !LayoutRotation(node.Rotation) || !ValidVector(node.Scale, 3) ||
                node.Position.Any(value => Math.Abs(value) > 5000f) || node.Scale.Any(value => Math.Abs(value) > 500f) ||
                node.Mesh != null && !Regex.IsMatch(node.Mesh, "^mesh-[0-9]{1,4}\\.bin$") ||
                node.Primitive is not (null or "Cube" or "Sphere" or "Capsule" or "Cylinder" or "Plane" or "Quad") ||
                node.NativeAsset != null && !new[] { ".fbx", ".prefab", ".obj" }.Any(ext => SafeAssetPath(node.NativeAsset, ext)) ||
                node.Materials != null && node.Materials.Any(material => material < 0 || material >= data.Materials.Length))
                throw new InvalidDataException($"Invalid art transform at index {index}.");
        }
        foreach (var material in data.Materials)
            if (material == null || !ValidVector(material.Color, 4) || !ValidVector(material.Tiling, 2) || !ValidVector(material.Offset, 2) ||
                material.Mode is < 0 or > 3 || material.Texture != null && !Regex.IsMatch(material.Texture, "^tex-[0-9]{1,4}\\.png$"))
                throw new InvalidDataException("Invalid authored art material.");
        return data;
    }

    private static IEnumerable<object> PrepareStockArt(StockLoad state, StockKitchen kitchen)
    {
        if (!stockArtReady)
            yield break;
        try { if (kitchen.Art != null) state.Art = ReadArtManifest(ArtPath(kitchen)); }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_MANIFEST_FAILED {error.Message}"); }

        var assets = (state.Art?.Nodes.Select(node => node.NativeAsset).Where(key => key != null).Distinct().ToArray() ?? Array.Empty<string>());
        if (assets.Length > 180)
        {
            log.LogWarning("BRIDGE_STOCK_ART_MODEL_LIMIT");
            assets = assets.Take(180).ToArray();
        }
        var tasks = new List<(string Key, object Operation, bool Floor)>();
        void Start(string key, Type type, bool floor)
        {
            try { tasks.Add((key, StartAddressable(state, type, key), floor)); }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_ASSET_FAILED {key}: {error.Message}"); }
        }
        if (kitchen.FloorTexture != null)
            Start(kitchen.FloorTexture, artTexture, true);
        foreach (var asset in assets)
            Start(asset, kitchenObjectType, false);
        foreach (var (key, operation, floor) in tasks)
        {
            while (!(bool)Get(operation, "IsDone")) yield return null;
            try
            {
                var value = ReadAddressable(operation, key);
                if (floor) state.FloorTexture = value;
                else state.VisualModels.Add(key, value);
            }
            catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_ASSET_FAILED {key}: {error.Message}"); }
        }
    }

    private static Stream ArtEntry(ZipArchive archive, string name, long limit)
    {
        var item = archive.GetEntry(name);
        if (item == null || item.Length <= 0 || item.Length > limit)
            throw new InvalidDataException($"Missing or invalid art data: {name}.");
        return item.Open();
    }

    private static object NativeArray(Type wrapper, Array values) => Activator.CreateInstance(wrapper, new object[] { values });
    private static object ArtworkVector2(float[] xy) => Activator.CreateInstance(artVector2, xy[0], xy[1]);

    private static object OwnedArt(Type type, params object[] args)
    {
        var result = Activator.CreateInstance(type, args);
        stockLoad.OwnedArt.Add(result);
        return result;
    }

    private static object ReadArtMesh(ZipArchive archive, string key)
    {
        using var reader = new BinaryReader(ArtEntry(archive, key, 16 * 1024 * 1024));
        int count = reader.ReadInt32(), fields = reader.ReadInt32(), subMeshes = reader.ReadInt32();
        if (count is < 1 or > 240_000 || fields is < 0 or > 3 || subMeshes is < 1 or > 30)
            throw new InvalidDataException($"Invalid art mesh header: {key}.");
        float ReadValue()
        {
            var value = reader.ReadSingle();
            if (!float.IsFinite(value) || Math.Abs(value) > 10000f)
                throw new InvalidDataException($"Invalid art vertex: {key}.");
            return value;
        }
        void Fill(object mesh, PropertyInfo member, Type element, int dimensions)
        {
            var values = Array.CreateInstance(element, count);
            for (int i = 0; i < count; ++i)
            {
                var x = ReadValue();
                var y = ReadValue();
                values.SetValue(dimensions == 2 ? Activator.CreateInstance(element, x, y) : Activator.CreateInstance(element, x, y, ReadValue()), i);
            }
            member.SetValue(mesh, NativeArray(member.PropertyType, values));
        }
        var result = OwnedArt(artMesh);
        if (count > 65535)
            artIndexFormat.SetValue(result, Enum.Parse(artIndexFormat.PropertyType, "UInt32"));
        Fill(result, artVertices, vectorType, 3);
        if ((fields & 1) != 0) Fill(result, artUv, artVector2, 2);
        if ((fields & 2) != 0) Fill(result, artNormals, vectorType, 3);
        Set(result, "subMeshCount", subMeshes);
        int totalIndices = 0;
        for (int sub = 0; sub < subMeshes; ++sub)
        {
            var size = reader.ReadInt32();
            if (size < 0 || size % 3 != 0 || size > 1_200_000 - totalIndices)
                throw new InvalidDataException($"Invalid artwork index count: {key}.");
            totalIndices += size;
            var indices = new int[size];
            for (int i = 0; i < size; ++i)
            {
                indices[i] = reader.ReadInt32();
                if (indices[i] < 0 || indices[i] >= count)
                    throw new InvalidDataException($"Invalid art index: {key}.");
            }
            artSetTriangles.Invoke(result, new[] { NativeArray(artSetTriangles.GetParameters()[0].ParameterType, indices), sub, true });
        }
        if ((fields & 2) == 0)
            Call(result, "RecalculateNormals");
        return result;
    }

    private static object MakeArtMaterial(object shader, float[] rgba, object texture, float[] repeat, float[] offset = null, int mode = 0)
    {
        var material = OwnedArt(artMaterial, shader);
        Set(material, "color", Activator.CreateInstance(artColor, rgba[0], rgba[1], rgba[2], rgba[3]));
        if (texture != null)
        {
            Set(material, "mainTexture", texture);
            Set(material, "mainTextureScale", ArtworkVector2(repeat));
            if (offset != null) Set(material, "mainTextureOffset", ArtworkVector2(offset));
        }
        if (mode > 0 && (bool)Find(artMaterial, "HasProperty", new[] { typeof(string) }).Invoke(material, new object[] { "_Mode" }))
        {
            var change = Find(artMaterial, "SetInt", new[] { typeof(string), typeof(int) });
            change.Invoke(material, new object[] { "_Mode", mode });
            change.Invoke(material, new object[] { "_SrcBlend", mode == 2 ? 5 : 1 });
            change.Invoke(material, new object[] { "_DstBlend", mode >= 2 ? 10 : 0 });
            change.Invoke(material, new object[] { "_ZWrite", mode == 1 ? 1 : 0 });
            Find(artMaterial, "EnableKeyword", new[] { typeof(string) }).Invoke(material, new object[]
                { mode == 1 ? "_ALPHATEST_ON" : mode == 2 ? "_ALPHABLEND_ON" : "_ALPHAPREMULTIPLY_ON" });
            Set(material, "renderQueue", mode == 1 ? 2450 : 3000);
        }
        return material;
    }

    private static bool StockShaderSupported(object shader) => shader != null &&
        !((string)Get(shader, "name")).Contains("InternalErrorShader", StringComparison.OrdinalIgnoreCase) &&
        (bool)Get(shader, "isSupported");

    private static object FindStockShader()
    {
        if (stockLoad.ArtShader != null)
            return stockLoad.ArtShader;
        foreach (var name in new[] { "Standard", "Legacy Shaders/Diffuse", "Universal Render Pipeline/Lit", "Unlit/Texture" })
        {
            var shader = artShaderFind.Invoke(null, new object[] { name });
            if (StockShaderSupported(shader))
                return stockLoad.ArtShader = shader;
        }
        return null;
    }

    private static object RepairStockMaterial(object material)
    {
        if (material == null || (StockShaderSupported(Get(material, "shader")) && (int)Get(material, "passCount") > 0))
            return material;
        var id = KitchenObjectId(material);
        if (stockLoad.RepairedMaterials.TryGetValue(id, out var replacement))
            return replacement;
        var shader = FindStockShader();
        if (shader == null)
            return material;

        object Texture(string name) => (bool)artHasProperty.Invoke(material, new object[] { name })
            ? artGetTexture.Invoke(material, new object[] { name }) : null;
        object Tint(string name) => (bool)artHasProperty.Invoke(material, new object[] { name })
            ? artGetColor.Invoke(material, new object[] { name }) : null;
        var texture = Texture("_BaseMap") ?? Texture("_MainTex") ?? Texture("_DiffuseMap");
        var color = Tint("_BaseColor") ?? Tint("_Color");
        var rgba = color == null ? new[] { .72f, .72f, .72f, 1f } :
            new[] { (float)Get(color, "r"), (float)Get(color, "g"), (float)Get(color, "b"), (float)Get(color, "a") };
        var scale = texture == null ? null : Get(material, "mainTextureScale");
        var offset = texture == null ? null : Get(material, "mainTextureOffset");
        var repeat = scale == null ? new[] { 1f, 1f } : new[] { (float)Get(scale, "x"), (float)Get(scale, "y") };
        var shift = offset == null ? null : new[] { (float)Get(offset, "x"), (float)Get(offset, "y") };
        replacement = MakeArtMaterial(shader, rgba, texture, repeat, shift);
        stockLoad.RepairedMaterials.Add(id, replacement);
        log.LogInfo($"BRIDGE_STOCK_MATERIAL_REPAIRED name={Get(material, "name")} texture={texture != null}");
        return replacement;
    }

    private static void CopyStockMaterials(object source, object target)
    {
        var original = artMaterials.GetValue(source);
        if (original == null)
            return;
        var entries = Enumerate(original).ToArray();
        var repaired = entries.Select(RepairStockMaterial).ToArray();
        if (!entries.SequenceEqual(repaired))
        {
            var array = Array.CreateInstance(artMaterial, repaired.Length);
            for (int index = 0; index < repaired.Length; index++)
                array.SetValue(repaired[index], index);
            artMaterials.SetValue(target, NativeArray(artMaterials.PropertyType, array));
        }
        else if (source != target)
            artMaterials.SetValue(target, original);
    }

    private static void RepairStockStationMaterials(object item)
    {
        if (!stockArtReady)
            return;
        foreach (var renderer in Enumerate(artRenderers.Invoke(item, new object[] { true })))
            if (renderer != null)
                CopyStockMaterials(renderer, renderer);
    }

    private static int CopyVisualModel(object source, object target, int depth = 0)
    {
        if (depth >= 40) return 0;
        var count = 0;
        var original = Get(source, "gameObject");
        var visual = kitchenMeshRenderer.Invoke(original, null);
        var filter = artReadFilter.Invoke(original, null);
        if (visual != null && filter != null && Get(filter, "sharedMesh") is object mesh && (bool)Get(visual, "enabled"))
        {
            var targetObject = Get(target, "gameObject");
            Set(artAddFilter.Invoke(targetObject, null), "sharedMesh", mesh);
            CopyStockMaterials(visual, artAddRenderer.Invoke(targetObject, null));
            count++;
        }
        foreach (var node in Children(source))
        {
            if (!(bool)Get(Get(node, "gameObject"), "activeSelf")) continue;
            var instance = Activator.CreateInstance(kitchenObjectType, (string)Get(node, "name"));
            var transform = Get(instance, "transform");
            Parent(transform, target);
            Set(transform, "localPosition", Get(node, "localPosition"));
            Set(transform, "localScale", Get(node, "localScale"));
            Set(transform, "localRotation", Get(node, "localRotation"));
            count += CopyVisualModel(node, transform, depth + 1);
        }
        return count;
    }

    private static void BuildStockPortalBack(object portal)
    {
        if (!stockArtReady)
            return;
        var find = Find(kitchenTransformType, "Find", new[] { typeof(string) });
        var front = find.Invoke(portal, new object[] { "Portal" });
        if (front == null)
            return;
        var back = Activator.CreateInstance(kitchenObjectType, "Portal back");
        var transform = Get(back, "transform");
        Parent(transform, portal);
        Set(transform, "localPosition", Get(front, "localPosition"));
        Set(transform, "localScale", Get(front, "localScale"));
        Set(transform, "localRotation", Rotation(new[] { 0f, .7071068f, 0f, .7071068f }));
        foreach (var rim in Children(front).Where(child => (string)Get(child, "name") is "m_wizard_portal_01" or "m_wizard_portal_02"))
        {
            var piece = Activator.CreateInstance(kitchenObjectType, (string)Get(rim, "name"));
            var child = Get(piece, "transform");
            Parent(child, transform);
            Set(child, "localPosition", Get(rim, "localPosition"));
            Set(child, "localScale", Get(rim, "localScale"));
            Set(child, "localRotation", Get(rim, "localRotation"));
            CopyVisualModel(rim, child);
        }
    }

    private static int DrawAuthoredArt(object room, StockKitchen kitchen, object shader)
    {
        var art = stockLoad.Art;
        if (art == null || shader == null)
            return 0;
        using var archive = ZipFile.OpenRead(ArtPath(kitchen));
        var root = Activator.CreateInstance(kitchenObjectType, "Authored scene art");
        var parent = Get(root, "transform");
        Parent(parent, room);
        Set(parent, "localPosition", Vector(kitchen.ArtOffset));
        var transforms = new List<object>(art.Nodes.Length);
        var meshCache = new Dictionary<string, object>(StringComparer.Ordinal);
        var textureCache = new Dictionary<string, object>(StringComparer.Ordinal);
        var materials = new object[art.Materials.Length];
        object MaterialFor(int id)
        {
            if (materials[id] != null) return materials[id];
            var data = art.Materials[id];
            object tex = null;
            if (data.Texture != null && !textureCache.TryGetValue(data.Texture, out tex))
            {
                using var image = ArtEntry(archive, data.Texture, 24L * 1024 * 1024);
                using var buffer = new MemoryStream();
                image.CopyTo(buffer);
                tex = OwnedArt(artTexture, 2, 2);
                var bytes = NativeArray(artLoadImage.GetParameters()[1].ParameterType, buffer.ToArray());
                if (!(bool)artLoadImage.Invoke(null, new[] { tex, bytes, true }))
                    throw new InvalidDataException($"Unity could not decode {data.Texture}.");
                textureCache.Add(data.Texture, tex);
            }
            return materials[id] = MakeArtMaterial(shader, data.Color, tex, data.Tiling, data.Offset, data.Mode);
        }

        var drawn = 0;
        var errors = 0;
        foreach (var node in art.Nodes)
        {
            var item = node.Primitive == null ? Activator.CreateInstance(kitchenObjectType, node.Name) :
                kitchenCreatePrimitive.Invoke(null, new[] { Enum.Parse(artPrimitive, node.Primitive) });
            var transform = Get(item, "transform");
            Parent(transform, node.Parent < 0 ? parent : transforms[node.Parent]);
            Set(transform, "localPosition", Vector(node.Position));
            Set(transform, "localScale", Vector(node.Scale));
            Set(transform, "localRotation", Rotation(node.Rotation));
            transforms.Add(transform);
            if (node.Primitive != null && artCollider.Invoke(item, null) is object collider)
                Set(collider, "enabled", false);
            try
            {
                if (node.NativeAsset != null && stockLoad.VisualModels.TryGetValue(node.NativeAsset, out var native))
                {
                    if (CopyVisualModel(Get(native, "transform"), transform) > 0) drawn++;
                }
                else if (node.Mesh != null || node.Primitive != null)
                {
                    object renderer = kitchenMeshRenderer.Invoke(item, null);
                    if (node.Mesh != null)
                    {
                        if (!meshCache.TryGetValue(node.Mesh, out var mesh))
                            meshCache.Add(node.Mesh, mesh = ReadArtMesh(archive, node.Mesh));
                        Set(artAddFilter.Invoke(item, null), "sharedMesh", mesh);
                        renderer = artAddRenderer.Invoke(item, null);
                    }
                    var assignments = Array.CreateInstance(artMaterial, node.Materials.Length);
                    for (int i = 0; i < node.Materials.Length; ++i)
                        assignments.SetValue(MaterialFor(node.Materials[i]), i);
                    artMaterials.SetValue(renderer, NativeArray(artMaterials.PropertyType, assignments));
                    drawn++;
                }
            }
            catch (Exception error)
            {
                if (++errors <= 5) log.LogWarning($"BRIDGE_STOCK_ART_PART_FAILED {node.Name}: {error}");
            }
        }
        log.LogInfo($"BRIDGE_STOCK_AUTHORED_ART drawn={drawn} errors={errors} meshes={meshCache.Count} textures={textureCache.Count}");
        return drawn;
    }

    private static void BuildStockVisuals(object parent, StockKitchen kitchen)
    {
        var floors = kitchen.Boxes.Where(box => box.Layer == 9 &&
            (box.Size[1] < Math.Min(box.Size[0], box.Size[2]) * .75f ||
             (box.Path?.Contains("/Col_Floor", StringComparison.Ordinal) == true && box.Size[1] <= .45f &&
              Math.Max(box.Size[0], box.Size[2]) >= 1.2f))).ToArray();
        var groundCount = 0;
        var shader = stockArtReady ? FindStockShader() : null;
        var tint = kitchen.ArtTheme switch
        {
            "camp" => new[] { .77f, .72f, .62f, 1f }, "coast" => new[] { .89f, .91f, .91f, 1f },
            _ => new[] { .89f, .89f, .86f, 1f }
        };
        var floorMaterials = new Dictionary<(float, float), object>();
        var floorMaterialFailed = false;
        foreach (var box in floors)
        {
            var position = (float[])box.Position.Clone();
            // Authored floor planes sit .05 below the collision surface in several community maps.
            position[1] -= stockLoad.Art == null ? .025f : .11f;
            var tile = CreateKitchenCube(parent, "Visual ground", position, box.Rotation, box.Size, 0, collision: false, visible: true);
            groundCount++;
            var renderer = kitchenMeshRenderer.Invoke(tile, null);
            if (shader == null && stockArtReady && Get(renderer, "sharedMaterial") is object native)
            {
                var nativeShader = Get(native, "shader");
                if (StockShaderSupported(nativeShader))
                    shader = stockLoad.ArtShader = nativeShader;
            }
            if (shader != null && !floorMaterialFailed)
            {
                try
                {
                    var scale = (box.Size[0], box.Size[2]);
                    if (!floorMaterials.TryGetValue(scale, out var material))
                        floorMaterials.Add(scale, material = MakeArtMaterial(shader, tint, stockLoad.FloorTexture,
                            new[] { Math.Max(1f, box.Size[0] / 3.6f), Math.Max(1f, box.Size[2] / 3.6f) }));
                    Set(renderer, "sharedMaterial", material);
                }
                catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_FLOOR_FALLBACK {error.Message}"); floorMaterialFailed = true; }
            }
        }

        try
        {
            var drawn = DrawAuthoredArt(parent, kitchen, shader);
            var stoneWalls = kitchen.Boxes.Where(box => box.Layer != 9 &&
                box.Path?.Split('/')[^1].StartsWith("Beach_stonewall_", StringComparison.OrdinalIgnoreCase) == true).ToArray();
            if (floors.Length > 0 && stoneWalls.Length > 0)
            {
                var stone = shader == null ? null : MakeArtMaterial(shader, new[] { .45f, .41f, .35f, 1f }, null, new[] { 1f, 1f });
                var baseline = floors.Min(box => box.Position[1] + box.Size[1] / 2);
                foreach (var wall in stoneWalls)
                {
                    var marker = CreateKitchenCube(parent, "Stone wall", new[] { wall.Position[0], baseline + .24f, wall.Position[2] },
                        wall.Rotation, new[] { wall.Size[0], .48f, wall.Size[2] }, 0, collision: false, visible: true);
                    if (stone != null)
                        Set(kitchenMeshRenderer.Invoke(marker, null), "sharedMaterial", stone);
                }
            }
            if (shader != null && floors.Length > 0)
            {
                var edgeMaterial = MakeArtMaterial(shader, new[] { .25f, .24f, .21f, 1f }, null, new[] { 1f, 1f });
                var baseline = floors.Min(box => box.Position[1] + box.Size[1] / 2);
                foreach (var box in kitchen.Boxes.Where(box => box.Layer != 9 && !((box.Path ?? "").Contains("AirWall", StringComparison.OrdinalIgnoreCase)) &&
                                                               box.Size[1] >= .4f && Math.Max(box.Size[0], box.Size[2]) >= 1.2f))
                {
                    var trim = CreateKitchenCube(parent, "Low visual border", new[] { box.Position[0], baseline + .075f, box.Position[2] },
                        new[] { 0f, box.Rotation[1], 0f, box.Rotation[3] }, new[] { box.Size[0], .15f, box.Size[2] }, 0, collision: false, visible: true);
                    Set(kitchenMeshRenderer.Invoke(trim, null), "sharedMaterial", edgeMaterial);
                }
            }
            log.LogInfo($"BRIDGE_STOCK_VISUALS ground={groundCount} sourceArt={drawn} theme={kitchen.ArtTheme}");
        }
        catch (Exception error) { log.LogWarning($"BRIDGE_STOCK_ART_FAILED {error}"); }
    }
}
