from base64 import b64decode
from struct import iter_unpack, unpack_from

from .assets import ImportErrorDetail
from .inventory import catalog_recipe_paths


NATIVE_ORDER_TYPES = {'CompositeOrderNode', 'CookedCompositeOrderNode', 'MixedCompositeOrderNode'}


def native_recipe_locations(catalog):
    recipe_paths = catalog_recipe_paths(catalog)
    keys, buckets_data, buckets, entries = catalog_tables(catalog)
    internal_ids = catalog['m_InternalIds']
    types = catalog['m_resourceTypes']
    providers = catalog['m_ProviderIds']
    locations = {}
    ambiguous = set()
    for index, (internal_id, provider, _, _, _, primary, type_id) in enumerate(entries):
        if not 0 <= internal_id < len(internal_ids):
            raise ImportErrorDetail('Invalid Addressables internal ID')
        name = internal_ids[internal_id]
        normalized = name.replace('\\', '/').casefold()
        if normalized not in recipe_paths:
            continue
        if not (0 <= primary < len(buckets) and 0 <= type_id < len(types) and 0 <= provider < len(providers)):
            raise ImportErrorDetail(f'Invalid Addressables location for {name}')
        type_name = types[type_id]['m_ClassName']
        if type_name not in NATIVE_ORDER_TYPES or not providers[provider].endswith('.BundledAssetProvider'):
            continue
        key_offset, start, count = buckets[primary]
        if count != 1 or unpack_from('<i', buckets_data, start)[0] != index:
            continue
        key = string_key(keys, key_offset)
        if not key:
            continue
        if normalized in locations:
            ambiguous.add(normalized)
        locations[normalized] = {'assetPath': name, 'addressKey': key, 'nativeType': type_name}
    return {path: location for path, location in locations.items() if path not in ambiguous}


def catalog_tables(catalog):
    keys = catalog_bytes(catalog, 'm_KeyDataString')
    buckets_data = catalog_bytes(catalog, 'm_BucketDataString')
    entries = catalog_bytes(catalog, 'm_EntryDataString')
    entry_count, = unpack_from('<i', entries)
    if entry_count < 0 or len(entries) != 4 + entry_count * 28:
        raise ImportErrorDetail('Unsupported Addressables entry table')

    bucket_count, = unpack_from('<i', buckets_data)
    key_count, = unpack_from('<i', keys)
    if bucket_count != key_count or not 0 <= bucket_count <= (len(buckets_data) - 4) // 8:
        raise ImportErrorDetail('Unsupported Addressables key table')
    buckets = []
    cursor = 4
    for _ in range(bucket_count):
        if cursor + 8 > len(buckets_data):
            raise ImportErrorDetail('Truncated Addressables bucket')
        key_offset, count = unpack_from('<ii', buckets_data, cursor)
        start = cursor + 8
        cursor = start + count * 4
        if not 4 <= key_offset < len(keys) or count < 0 or cursor > len(buckets_data):
            raise ImportErrorDetail('Invalid Addressables bucket')
        buckets.append((key_offset, start, count))
    if cursor != len(buckets_data):
        raise ImportErrorDetail('Unexpected Addressables bucket data')

    return keys, buckets_data, buckets, list(iter_unpack('<7i', entries[4:]))


def scene_dependencies(catalog, scene_names):
    keys, data, buckets, entries = catalog_tables(catalog)
    paths = catalog['m_InternalIds']
    results = {}
    for internal, _, dep, _, _, primary, type_id in entries:
        if catalog['m_resourceTypes'][type_id]['m_ClassName'] != 'UnityEngine.ResourceManagement.ResourceProviders.SceneInstance':
            continue
        path = paths[internal]
        name = path.rsplit('/', 1)[-1].casefold().removesuffix('.unity')
        if name not in scene_names:
            continue
        expected_bundle = f'{name}_scenes_all.bundle'
        if dep < 0 or name in results:
            raise ImportErrorDetail(f'Missing or ambiguous scene dependencies: {path}')
        _, start, count = buckets[dep]
        dependencies = []
        for index in unpack_from(f'<{count}i', data, start):
            _, _, _, _, _, key_index, asset_type = entries[index]
            if catalog['m_resourceTypes'][asset_type]['m_ClassName'] != 'UnityEngine.ResourceManagement.ResourceProviders.IAssetBundleResource':
                raise ImportErrorDetail(f'Unexpected dependency type for {path}')
            key = string_key(keys, buckets[key_index][0])
            if not key or not key.endswith('.bundle') or any(c in key for c in ('/', '\\', ':')):
                raise ImportErrorDetail(f'Unexpected dependency key for {path}')
            dependencies.append(key)
        if expected_bundle not in dependencies:
            raise ImportErrorDetail(f'Addressables scene has no expected bundle: {path}')
        results[name] = {'path': path, 'dependencies': sorted(set(dependencies) - {expected_bundle})}
    if set(results) != set(scene_names):
        raise ImportErrorDetail('Some donor scenes are missing from the installed AYCE catalog')
    return results


def native_prefab_addresses(catalog):
    keys, data, buckets, entries = catalog_tables(catalog)
    paths = catalog['m_InternalIds']
    types = catalog['m_resourceTypes']
    results = {}
    for index, (internal, _, _, _, _, primary, type_id) in enumerate(entries):
        path = paths[internal]
        if not path.endswith('.prefab') or types[type_id]['m_ClassName'] != 'UnityEngine.GameObject':
            continue
        key_offset, start, count = buckets[primary]
        if count == 1 and unpack_from('<i', data, start)[0] == index and string_key(keys, key_offset) == path:
            results[path.casefold()] = path
    return results


def native_order_assets(catalog):
    keys, data, buckets, entries = catalog_tables(catalog)
    paths = catalog['m_InternalIds']
    types = catalog['m_resourceTypes']
    results = {}
    for index, (internal, _, _, _, _, primary, type_id) in enumerate(entries):
        path = paths[internal]
        native_type = types[type_id]['m_ClassName']
        if native_type not in NATIVE_ORDER_TYPES | {'IngredientOrderNode'} or not path.endswith('.asset'):
            continue
        key_offset, start, count = buckets[primary]
        if count == 1 and unpack_from('<i', data, start)[0] == index and string_key(keys, key_offset) == path:
            results[path.casefold()] = path
    return results


def native_visual_models(catalog):
    keys, data, buckets, entries = catalog_tables(catalog)
    types = catalog['m_resourceTypes']
    internal_ids = catalog['m_InternalIds']
    models = set()
    for index, (internal, _, _, _, _, primary, type_id) in enumerate(entries):
        name = internal_ids[internal]
        if not name.casefold().endswith(('.fbx', '.prefab', '.obj')) or types[type_id]['m_ClassName'] != 'UnityEngine.GameObject':
            continue
        key_offset, start, count = buckets[primary]
        if string_key(keys, key_offset) != name:
            continue
        members = unpack_from(f'<{count}i', data, start)
        matches = [member for member in members if types[entries[member][6]]['m_ClassName'] == 'UnityEngine.GameObject']
        if matches == [index]:
            models.add(name)
    return models


def catalog_bytes(catalog, field):
    encoded = catalog.get(field)
    if not isinstance(encoded, str) or len(encoded) > 32 * 1024 * 1024:
        raise ImportErrorDetail(f'Expected the full AYCE Addressables catalog with {field}')
    data = b64decode(encoded, validate=True)
    if len(data) < 4:
        raise ImportErrorDetail(f'Truncated AYCE Addressables field: {field}')
    return data


def string_key(keys, offset):
    tag = keys[offset]
    if tag not in (0, 1) or offset + 5 > len(keys):
        return None
    size, = unpack_from('<i', keys, offset + 1)
    start = offset + 5
    if size < 1 or start + size > len(keys):
        return None
    return keys[start:start + size].decode('ascii' if tag == 0 else 'utf-16le')
