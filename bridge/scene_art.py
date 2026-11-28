from array import array
from collections import Counter
from io import BytesIO
import json
import math
from pathlib import PurePosixPath
import re
from struct import pack
from zipfile import ZIP_DEFLATED, ZipFile

from UnityPy.helpers.MeshHelper import MeshHandler

from .assets import ImportErrorDetail, xyz


BUILTIN_MESHES = {10202: 'Cube', 10206: 'Cylinder', 10207: 'Sphere', 10208: 'Capsule', 10209: 'Plane', 10210: 'Quad'}
NON_ART_ROOTS = {'Design', 'Chefs', 'Debug', 'CampaignGameEnvironment', 'MultiplayerGameCamera', 'PseudoPrefabManager'}
MESH_ERRORS = (ValueError, KeyError, TypeError, RuntimeError, IndexError, AttributeError, NotImplementedError)
CITY_MODEL_ALIASES = {
    'exteriorroad01': 'Assets/Models/Shared Kitchen/Meshes/m_city_road_01.fbx',
    'exteriorroadcentre01': 'Assets/Models/Shared Kitchen/Meshes/m_city_road_01.fbx',
    'exteriorpath01': 'Assets/Models/Themes/City_Sushi/Meshes/m_city_path_01.fbx',
    'exteriorpathside01': 'Assets/Models/Themes/City_Sushi/Meshes/m_city_path_side_01.fbx',
    'decorationbamboo01': 'Assets/Models/Themes/City_Sushi/Meshes/m_city_bambooplant_01.fbx',
}

THEMED_MODEL_ALIASES = {
    'beachball01': 'Assets/DownloadableContent/DLC02/DLC_Assets/Models/Beach Theme/m_dlc2_beachball_01.fbx',
    'dlc2sandcastle01': 'Assets/DownloadableContent/DLC02/DLC_Assets/Models/Beach Theme/m_dlc2_sandcastle_01.fbx',
}


def asset_key(obj):
    return obj.assets_file.name, obj.path_id


def visual_name(path):
    name = PurePosixPath(path.replace('\\', '/')).stem.casefold()
    name = re.sub(r'^(?:model_|mesh_|m_|p_)', '', name)
    return re.sub(r'[^a-z0-9]', '', name)


def matching_visual_model(source_path, models):
    normalized = source_path.replace('\\', '/').casefold()
    if normalized in models:
        return models[normalized]
    token = visual_name(normalized)
    if len(token) < 6:
        return None
    if '/city_sushi/' in normalized and token in CITY_MODEL_ALIASES:
        alias = CITY_MODEL_ALIASES[token].casefold()
        if alias in models:
            return models[alias]
    if '/beach theme/' in normalized and token in THEMED_MODEL_ALIASES:
        alias = THEMED_MODEL_ALIASES[token].casefold()
        if alias in models:
            return models[alias]
    candidates = [value for path, value in models.items() if visual_name(path) == token and '/map/' not in path]
    if len(candidates) == 1:
        return candidates[0]
    domains = set(re.findall(r'(?:dlc\d+|space|beach|wizard|city|raft|balloon)', normalized))
    same_theme = [name for name in candidates if any(domain in name.casefold() for domain in domains)]
    return same_theme[0] if len(same_theme) == 1 else None


def mesh_bytes(obj):
    mesh = MeshHandler(obj.read())
    mesh.process()
    vertices = mesh.m_Vertices
    if not vertices or not 1 <= len(vertices) <= 240_000:
        raise ImportErrorDetail('Mesh has an unsupported vertex count')
    uv = mesh.m_UV0 if mesh.m_UV0 and len(mesh.m_UV0) == len(vertices) else None
    normals = mesh.m_Normals if mesh.m_Normals and len(mesh.m_Normals) == len(vertices) else None
    triangles = [list(submesh) for submesh in mesh.get_triangles()]
    if not triangles or len(triangles) > 30 or sum(map(len, triangles)) > 400_000:
        raise ImportErrorDetail('Mesh has an unsupported triangle count')

    def attributes(rows, count):
        return array('f', (value if math.isfinite(value) else 0.0 for row in rows for value in row[:count])).tobytes()

    encoded = BytesIO()
    encoded.write(pack('<iii', len(vertices), int(uv is not None) + 2 * int(normals is not None), len(triangles)))
    encoded.write(attributes(vertices, 3))
    if uv is not None:
        encoded.write(attributes(uv, 2))
    if normals is not None:
        encoded.write(attributes(normals, 3))
    for submesh in triangles:
        indices = array('i', (index for triangle in submesh for index in triangle))
        if any(index < 0 or index >= len(vertices) for index in indices):
            raise ImportErrorDetail('Mesh contains invalid indices')
        encoded.write(pack('<i', len(indices)))
        encoded.write(indices.tobytes())
    return encoded.getvalue()


def export_scene_art(bundles, scene_file, output, native_models=(), *, include_roots=None, exclude_roots=()):
    scene = list(bundles.entry(scene_file, ('MeshFilter', 'MeshRenderer')))
    games = {a.key: a for a in scene if a.obj.type.name == 'GameObject'}
    transforms, filters, renderers, stubs = {}, {}, {}, []
    for asset in scene:
        obj, _ = bundles.ref(asset.obj, asset.tree.get('m_GameObject'))
        if obj is None:
            continue
        key = asset_key(obj)
        kind = asset.obj.type.name
        if kind in ('Transform', 'RectTransform'):
            transforms[key] = asset
        elif kind == 'MeshFilter':
            filters[key] = asset
        elif kind == 'MeshRenderer' and asset.tree.get('m_Enabled', True):
            renderers[key] = asset
        elif (asset.script or '').startswith('PseudoPrefab') and (asset.script or '').endswith('Stub'):
            stubs.append((key, asset))

    def chain(key):
        lineage = []
        while key in transforms:
            if len(lineage) >= 80 or key in lineage:
                raise ImportErrorDetail('Invalid art transform hierarchy')
            lineage.append(key)
            tf = transforms[key]
            parent, _ = bundles.ref(tf.obj, tf.tree.get('m_Father'))
            if parent is None:
                return list(reversed(lineage))
            parent_go, _ = bundles.ref(parent, bundles.tree(parent)['m_GameObject'])
            if parent_go is None:
                return []
            key = asset_key(parent_go)
        return []

    nodes, node_ids, materials, material_ids, geometry, texture_ids = [], {}, [], {}, {}, {}
    statistics = Counter()
    models = {name.casefold(): name for name in native_models}
    model_cache = {}

    def visible_path(lineage):
        if not lineage or not all(games[key].tree['m_IsActive'] for key in lineage):
            return None
        names = tuple(games[key].tree['m_Name'] for key in lineage)
        if ((include_roots is not None and not any(names[:len(root)] == root for root in include_roots))
                or any(names[:len(root)] == root for root in exclude_roots)):
            statistics['excludedByPath'] += 1
            return None
        return names

    def ensure_nodes(lineage):
        parent = -1
        for key in lineage:
            if key in node_ids:
                parent = node_ids[key]
                continue
            tf = transforms[key].tree
            node = {'name': games[key].tree['m_Name'][:120], 'parent': parent,
                    'position': xyz(tf['m_LocalPosition']), 'rotation': xyz(tf['m_LocalRotation'], 'xyzw'),
                    'scale': xyz(tf['m_LocalScale'])}
            parent = node_ids[key] = len(nodes)
            nodes.append(node)
        return nodes[parent]

    def bounded(lineage):
        for key in lineage:
            local = transforms[key].tree
            if max(map(abs, xyz(local['m_LocalPosition']))) > 5000 or max(map(abs, xyz(local['m_LocalScale']))) > 500:
                statistics['oversizedTransforms'] += 1
                return False
        return True

    temporary = output.with_suffix('.partial')
    with ZipFile(temporary, 'w', compression=ZIP_DEFLATED, compresslevel=4) as archive:
        def image(texture):
            if texture is None or texture.type.name != 'Texture2D':
                return None
            key = asset_key(texture)
            if key in texture_ids:
                return texture_ids[key]
            try:
                source = texture.read()
                if not 0 < source.m_Width <= 4096 or not 0 < source.m_Height <= 4096:
                    raise ImportErrorDetail('Unsupported source art texture size')
                data = source.image
                data.thumbnail((2048, 2048))
                encoded = BytesIO()
                data.save(encoded, format='PNG')
                filename = f'tex-{len(texture_ids)}.png'
                archive.writestr(filename, encoded.getvalue())
                texture_ids[key] = filename
                statistics['textures'] += 1
            except MESH_ERRORS + (OSError,) as exc:
                statistics['unreadableTextures'] += 1
                texture_ids[key] = None
            return texture_ids[key]

        def material(owner, ptr):
            obj, _ = bundles.ref(owner, ptr)
            key = asset_key(obj) if obj is not None else None
            if key in material_ids:
                return material_ids[key]
            row = {'color': [.74, .73, .70, 1], 'tiling': [1, 1], 'offset': [0, 0], 'mode': 0, 'texture': None}
            if obj is not None and obj.type.name == 'Material':
                tree = bundles.tree(obj) or {}
                props = tree.get('m_SavedProperties', {})
                textures = dict(props.get('m_TexEnvs', []))
                colors = dict(props.get('m_Colors', []))
                floats = dict(props.get('m_Floats', []))
                color = colors.get('_BaseColor', colors.get('_Color'))
                if color is not None:
                    row['color'] = [max(0.0, min(4.0, float(color.get(channel, 1.0)))) for channel in 'rgba']
                row['mode'] = int(floats.get('_Mode', 0)) if floats.get('_Mode', 0) in (1, 2, 3) else 0
                for name in ('_BaseMap', '_MainTex', '_DiffuseMap', '_AlbedoMap'):
                    setting = textures.get(name)
                    if not setting:
                        continue
                    texture, _ = bundles.ref(obj, setting.get('m_Texture'))
                    row['texture'] = image(texture)
                    if row['texture']:
                        row['tiling'] = xyz(setting['m_Scale'], 'xy')
                        row['offset'] = xyz(setting['m_Offset'], 'xy')
                        break
            material_ids[key] = len(materials)
            materials.append(row)
            return material_ids[key]

        for key, renderer in renderers.items():
            mesh_filter = filters.get(key)
            if mesh_filter is None:
                continue
            lineage = chain(key)
            names = visible_path(lineage)
            if names is None:
                continue
            if names[0] in NON_ART_ROOTS or any('airwall' in name.casefold() or
                    re.fullmatch(r'shadows?(?: \(\d+\))?', name.casefold()) for name in names):
                continue
            if not bounded(lineage):
                continue
            obj, reference = bundles.ref(mesh_filter.obj, mesh_filter.tree.get('m_Mesh'))
            primitive = None
            filename = None
            if obj is not None and obj.type.name == 'Mesh':
                mesh_id = asset_key(obj)
                if mesh_id not in geometry:
                    try:
                        filename = f'mesh-{len(geometry)}.bin'
                        archive.writestr(filename, mesh_bytes(obj))
                        geometry[mesh_id] = filename
                    except MESH_ERRORS:
                        geometry[mesh_id] = None
                filename = geometry[mesh_id]
            elif reference and reference.casefold().startswith('unity default resources:'):
                primitive = BUILTIN_MESHES.get(int(reference.rsplit(':', 1)[1]))
            if not filename and not primitive:
                statistics['unresolvedRenderers'] += 1
                continue
            assignments = [material(renderer.obj, ptr) for ptr in renderer.tree.get('m_Materials', [])]
            if not assignments:
                assignments = [material(renderer.obj, None)]
            if ((primitive == 'Plane' and names[0] == 'Art') or any(name.casefold().endswith('collider') for name in names[:-1])) and not any(
                    materials[i]['texture'] for i in assignments):
                statistics['placeholderPlanes'] += 1
                continue
            ensure_nodes(lineage).update(mesh=filename, primitive=primitive, materials=assignments)
            statistics['renderers'] += 1
            if primitive == 'Plane' and any(materials[i]['texture'] for i in assignments):
                statistics['texturedPlanes'] += 1

        for key, stub in stubs:
            lineage = chain(key)
            names = visible_path(lineage)
            if names is None:
                continue
            source = bundles.describe_reference(stub, stub.tree.get('pseudoPrefabSO')) or {}
            path = source.get('assetPath', '')
            path_lower = path.replace('\\', '/').casefold()
            parent = names[0]
            if not path or (parent != 'Art' and not any(part in path_lower for part in ('/dressing', '/beach theme/', '/themes/'))):
                continue
            if not bounded(lineage):
                continue
            if key in node_ids and (nodes[node_ids[key]].get('mesh') or nodes[node_ids[key]].get('primitive')):
                continue
            if path not in model_cache:
                model_cache[path] = matching_visual_model(path, models)
            if model_cache[path] is None:
                statistics['unmatchedVisualPrefabs'] += 1
                continue
            # These effect meshes depend on animated or projected materials in the source scene.
            model = PurePosixPath(model_cache[path]).stem.casefold()
            if model.startswith(('m_raft_wake_', 'm_dlc5_shadowcast_', 'm_dlc5_forestfog_')):
                statistics['omittedRuntimeEffects'] += 1
                continue
            ensure_nodes(lineage)['nativeAsset'] = model_cache[path]
            statistics['nativeModels'] += 1
        statistics['meshes'] = sum(bool(filename) for filename in geometry.values())
        archive.writestr('scene.json', json.dumps({'format': 1, 'nodes': nodes, 'materials': materials}, allow_nan=False, separators=(',', ':')))
    temporary.replace(output)
    return dict(statistics)
