from collections import Counter
from hashlib import sha256
import json
from math import dist
from pathlib import Path
import re

import UnityPy

from .addressables import native_order_assets, native_prefab_addresses, native_visual_models, scene_dependencies
from .assets import Bundles, ImportErrorDetail, load_bundles, rotate
from .food_audit import StockFoodAudit
from .scene_art import export_scene_art


def write_dormant_scene(source, destination):
    env = UnityPy.load(str(source))
    bundle, = env.files.values()
    if bundle.version_engine != '2019.4.11f1':
        raise ImportErrorDetail(f'Unexpected Unity version in donor {source.name}')
    root_count = 0
    for asset in env.objects:
        if asset.type.name not in ('Transform', 'RectTransform'):
            continue
        tree = asset.read_typetree()
        if tree['m_Father']['m_PathID']:
            continue
        obj = asset.assets_file.objects[tree['m_GameObject']['m_PathID']]
        state = obj.read_typetree()
        state['m_IsActive'] = False
        obj.save_typetree(state)
        root_count += 1
    if root_count < 5:
        raise ImportErrorDetail(f'No complete native scene in {source.name}')

    temporary = destination.with_suffix('.partial')
    temporary.write_bytes(bundle.save(packer='lz4'))
    check = UnityPy.load(temporary.read_bytes())
    checked = 0
    for asset in check.objects:
        if asset.type.name not in ('Transform', 'RectTransform'):
            continue
        tree = asset.read_typetree()
        if tree['m_Father']['m_PathID']:
            continue
        obj = asset.assets_file.objects[tree['m_GameObject']['m_PathID']]
        if obj.read_typetree()['m_IsActive']:
            raise ImportErrorDetail(f'Active donor root after export: {source.name}')
        checked += 1
    if checked != root_count or set(check.container) != set(env.container):
        raise ImportErrorDetail(f'Donor scene layout changed while exporting {source.name}')
    temporary.replace(destination)


def validate_scene_templates(source, required_paths, travelator_paths=()):
    raw, _ = load_bundles(source)
    bundles = Bundles(raw)
    entries = list(bundles.entry(source.name))
    [level] = [asset for asset in entries if asset.script == 'LevelData']
    registered = set()
    for pointer in level.tree['m_EntityRegistry']:
        obj, _ = bundles.ref(level.obj, pointer)
        if obj is not None:
            registered.add((obj.assets_file.name, obj.path_id))
    travelators = {(obj.assets_file.name, obj.path_id) for asset in entries if asset.script == 'Travelator'
                   if (obj := bundles.ref(asset.obj, asset.tree['m_GameObject'])[0]) is not None}

    paths = set()
    for entry in entries:
        if entry.obj.type.name not in ('Transform', 'RectTransform'):
            continue
        transform = entry
        trail = []
        for _ in range(80):
            obj, _ = bundles.ref(transform.obj, transform.tree.get('m_GameObject'))
            if obj is None:
                break
            trail.append(bundles.read(obj).tree['m_Name'])
            parent, _ = bundles.ref(transform.obj, transform.tree.get('m_Father'))
            if parent is None:
                obj, _ = bundles.ref(entry.obj, entry.tree.get('m_GameObject'))
                name = '/'.join(reversed(trail))
                key = (obj.assets_file.name, obj.path_id)
                if key in registered or (name in travelator_paths and key in travelators):
                    paths.add(name)
                break
            transform = bundles.read(parent)
    missing = sorted(path for path in required_paths if not any(native == path or native.startswith(path + '/') for native in paths))
    if missing:
        raise ImportErrorDetail(f'{source.name} has no registered native donor for: {", ".join(missing)}')


PORTABLE_PROPS = {
    'plate': {'counter', 'chopping'}, 'plate_stack': {'counter', 'chopping'}, 'tray_stack': {'counter', 'chopping'},
    'serving_tray': {'counter', 'chopping'},
    'glass_stack': {'counter', 'chopping'}, 'mug_stack': {'counter', 'chopping'},
    'extinguisher': {'counter', 'chopping'}, 'water_gun': {'counter', 'chopping'},
    'bellows': {'counter', 'chopping'}, 'mixing_bowl': {'mixer', 'counter', 'stove'},
    'blender_cup': {'blender', 'counter'}, 'fryer_basket': {'fryer', 'counter'},
    'pan': {'stove', 'counter'}, 'pot': {'stove', 'counter'}, 'steamer': {'stove', 'counter'},
    'griddle': {'campfire', 'stove', 'counter'}, 'skewer': {'barbecue', 'counter'},
    'toasting_fork': {'campfire', 'counter'}, 'roasting_tray': {'stove', 'oven', 'counter'},
    'ingredient_spray': {'counter', 'chopping'},
}
OPENING_STACKS = ('plate_stack', 'glass_stack', 'mug_stack', 'tray_stack')


FLOOR_ART = {
    'city': 'Assets/Models/Shared Kitchen/Meshes/Textures/512/t_sk_floor_3x3_01_d.tga',
    'camp': 'Assets/DownloadableContent/DLC05/DLC_Assets/Models/Dressing Assets/Textures/t_dlc5_forestfloor_01.tga',
    'coast': 'Assets/DownloadableContent/DLC08/DLC_Assets/Models/Dressing Assets/Textures/t_dlc08_planks_01_d.tga',
}
# The original foreground walls and canopy can block the stock kitchen camera.
# Keep the two authored floors and the lava that sits beneath the play area.
SCENE_ART_ROOTS = {
    's_the_ever_peckish_rises_4': (('Art', 'Ground'), ('Art', 'Lava')),
    's_shz_custommaps_1_3': tuple(('Art', 'Ground', f'Floor ({index})') for index in (7, 8, 9, 11)),
}
HIDDEN_SCENE_ART = {
    's_young_2': (('Art', 'Decoration', 'wizard_picture_01'),),
    's_lbcy_5': (
        ('Art', 'Icecliff', 'm_dlc3_icecliff_straight_01 (26)'),
        ('Art', 'Icecliff', 'm_dlc3_icecliff_straight_02 (25)'),
        ('Art', 'Icecliff', 'm_dlc3_icecliff_straight_02 (26)'),
        ('Art', 'Icecliff (1)', 'm_dlc3_icecliff_straight_01 (4)'),
        ('Art', 'Icecliff (1)', 'm_dlc3_icecliff_straight_02 (4)'),
        ('Art', 'Icecliff (1)', 'm_dlc3_icecliff_straight_02 (5)'),
    ),
}
CLEAN_PLATE_STACK = 'Assets/Prefabs/Overcooked_Legacy/CleanPlateStack.prefab'
LEGACY_CARROT = 'assets/overcooked1/downloadablecontent/dlc2/dlc_assets/prefabs/stewcarrot.prefab'
OC2_CARROT = 'assets/downloadablecontent/overcooked_legacy/dlc2/dlc_assets/prefabs/carrot.prefab'
ROAST_CARROT = 'assets/downloadablecontent/dlc07/dlc_assets/prefabs/ingredients/dlc07_carrot.prefab'
CAKE_CHOCOLATE = 'assets/prefabs/overcooked_legacy/ingredients/chocolate.prefab'
COCOA_CHOCOLATE = 'assets/downloadablecontent/dlc03/dlc_assets/prefabs/ingredients/dlc03_chocolate.prefab'


def stock_prefab(path, assets):
    path = path.replace('\\', '/').casefold()
    if path.endswith('/prefabs/carrot.prefab') and LEGACY_CARROT in assets:
        return assets[LEGACY_CARROT]
    if path in assets:
        return assets[path]
    if path:
        leaf = path.rsplit('/', 1)[-1]
        candidates = [value for key, value in assets.items() if key.rsplit('/', 1)[-1] == leaf]
        if len(candidates) == 1:
            return candidates[0]
    return None


def source_approval(reference, prefabs, orders):
    path = reference.get('assetPath', '').replace('\\', '/').casefold()
    if path.endswith('.prefab'):
        name = path.rsplit('/', 1)[-1].removesuffix('.prefab')
        group = next((part for part in path.split('/') if re.fullmatch(r'dlc\d+', part)), None)
        candidates = [key for key in orders if key.endswith(f'/ingredients/{name}.asset')]
        matching = [key for key in candidates
                    if next((part for part in key.split('/') if re.fullmatch(r'dlc\d+', part)), None) == group]
        if len(matching) == 1:
            return orders[matching[0]]
        return stock_prefab(path, prefabs)
    if path in orders:
        return orders[path]
    definition = reference.get('definition', {})
    if definition.get('type') == 3:
        name = (definition.get('recipeName') or reference.get('name') or '').casefold()
    elif '/mixedingredients/' in path:
        name = path.rsplit('/', 1)[-1].removesuffix('.asset').removeprefix('dlc13_')
    else:
        return None
    candidates = [value for key, value in orders.items() if key.endswith(f'/mixedingredients/{name}.asset')]
    return candidates[0] if len(candidates) == 1 else None


def dispenser_prefab(path, assets, menu):
    if path.casefold() == OC2_CARROT:
        carrot_orders = [row['addressKey'].casefold() for row in menu if 'carrot' in row['addressKey'].casefold()]
        if carrot_orders and all('/dlc07/' in key and key.endswith('roast.asset') for key in carrot_orders):
            return assets.get(ROAST_CARROT)
    return stock_prefab(path, assets)


def add_lbcy_floor_passage(boxes, row_z):
    floors = sorted((box for box in boxes if box['layer'] == 9), key=lambda box: box['position'][0])
    if len(floors) != 2 or any(box['rotation'] != [0, 0, 0, 1] for box in floors):
        raise ImportErrorDetail('lbcy_test_4: expected two aligned kitchen floors')
    left, right = floors
    left_edge = left['position'][0] + left['size'][0] / 2
    right_edge = right['position'][0] - right['size'][0] / 2
    half_lane = 1.2
    if (not 0 < right_edge - left_edge <= 4
            or abs(left['position'][1] + left['size'][1] / 2 - right['position'][1] - right['size'][1] / 2) > .01
            or any(abs(row_z - box['position'][2]) + half_lane > box['size'][2] / 2 for box in floors)):
        raise ImportErrorDetail('lbcy_test_4: the floor edges no longer match the crossing')
    boxes.append({'path': 'Bridge/Floor Passage', 'layer': 9,
                  'position': [round((left_edge + right_edge) / 2, 5), left['position'][1], row_z],
                  'rotation': [0, 0, 0, 1],
                  'size': [round(right_edge - left_edge + .24, 5), left['size'][1], half_lane * 2]})


def stock_overlay(level, host, donors, placements, asset_paths, order_assets):
    source = []
    counter_cells = set()
    for row in level['kitchenPlacements']:
        if row['role'] == 'counter':
            cell = tuple(round(value, 2) for value in row['position'])
            if cell in counter_cells:
                continue
            counter_cells.add(cell)
        source.append(row)
    rules = level['campaignRules']
    if level['scene'] in ('xqy_danrenlvtu_2', 'xqy_danrenlvtu_3'):
        # These two solo designs have an off-kitchen or missing second chef. The
        # stock AYCE solo host still needs two switchable chefs on playable ground.
        primary, = [row for row in source if row['role'] == 'spawn' and row.get('playerIndex') == 0]
        x, y, z = primary['position']
        source = [row for row in source if row['role'] != 'spawn' or row is primary]
        for player, (dx, dz) in enumerate(((0, -1.2), (1.2, 0), (-1.2, 0)), 1):
            source.append({**primary, 'playerIndex': player, 'position': [x + dx, y, z + dz]})
        rules = {'1': rules['1']}
    fixed = [row for row in source if row['role'] not in PORTABLE_PROPS and row['role'] != 'spawn']
    # Keep the authored distance between cells, but center each layout on the host camera.
    center = [(min(row['position'][i] for row in fixed) + max(row['position'][i] for row in fixed)) / 2 for i in (0, 2)]
    shift = [round((10.8 - center[0]) / 1.2) * 1.2, -1.0, round((3.6 - center[1]) / 1.2) * 1.2]

    def translated(position, spawn=False):
        values = [round(a + b, 5) for a, b in zip(position, shift, strict=True)]
        if spawn:
            values[1] -= .5
        return values

    ids = {id(row): f'p{index + 1}' for index, row in enumerate(source)}
    returns = {row.get('sourceObject'): ids[id(row)] for row in source if row['role'] in ('plate_return', 'glass_return')}
    portals = {row['portalId']: ids[id(row)] for row in source if row['role'] == 'teleportal'}
    condiment_dispensers = {row['sourceObject']: ids[id(row)] for row in source if row['role'] == 'condiment_dispenser'}
    occupied = set()
    output = []
    spawns = 0
    assigned_players = {row['playerIndex'] for row in source if row['role'] == 'spawn' and row.get('playerIndex') in range(4)}
    spare_players = iter(sorted(set(range(4)) - assigned_players))
    used_donors = set()
    legacy_nuggets = any('/chickennuggetsandchips/' in row['addressKey'].casefold() for row in level['orders'])
    for row in source:
        role = row['role']
        if role == 'spawn':
            spawns += 1
            player = row.get('playerIndex')
            if player not in range(4):
                player = next(spare_players, 4)
            donor = {'scene': host['scene'], 'path': f'Chefs/Player {int(player) + 1}'}
        else:
            arm = row['prefab']['assetPath'].replace('\\', '/').rsplit('/', 1)[-1].casefold()
            donor_role = f"teleportal_{row['portalColor']}" if role == 'teleportal' and row['portalColor'] else role
            if role == 'fryer_basket' and legacy_nuggets and not row.get('allowedIngredients'):
                donor_role = 'nugget_basket'
            if role == 'pot' and any(ref.get('assetPath', '').replace('\\', '/').casefold().endswith(
                    '/dlc03/dlc_assets/prefabs/ingredients/milk.prefab') for ref in row.get('allowedIngredients', [])):
                donor_role = 'cocoa_pot'
            donor = placements.get(arm, placements.get(donor_role))
            if donor is None:
                raise ImportErrorDetail(f"{level['scene']}: no native donor for {role}/{arm}")
            if donor['scene'] == 'asset' and asset_paths.get(donor['path'].casefold()) != donor['path']:
                raise ImportErrorDetail(f"{level['scene']}: unavailable native prefab {donor['path']}")
            if donor['scene'] not in (host['scene'], 'asset'):
                used_donors.add(donor['scene'])

        support = None
        if role in PORTABLE_PROPS:
            x, y, z = row['position']
            options = []
            for station in fixed:
                if station['role'] not in PORTABLE_PROPS[role] or ids[id(station)] in occupied:
                    continue
                sx, sy, sz = station['position']
                distance = (x - sx)**2 + (z - sz)**2
                if distance <= .31**2 and abs(y - sy) <= 1.75:
                    options.append((distance, abs(y - sy), ids[id(station)]))
            if options:
                support = min(options)[2]
                occupied.add(support)
        item = None
        if role in ('dispenser', 'ingredient_spray'):
            ingredient = (row.get('ingredient') or {}).get('assetPath', '').replace('\\', '/')
            if level['scene'] == 's_lbcy_5':
                # Place each chocolate on the side with the appliance and recipe that use it.
                if ingredient.casefold() == CAKE_CHOCOLATE:
                    ingredient = COCOA_CHOCOLATE
                elif ingredient.casefold() == COCOA_CHOCOLATE:
                    ingredient = CAKE_CHOCOLATE
            item = dispenser_prefab(ingredient, asset_paths, level['orders'])
            if not item:
                raise ImportErrorDetail(f"{level['scene']}: unmatched ingredient {ingredient}")
        return_stack = None
        if role in ('plate_return', 'glass_return'):
            stack_path = (row.get('returnStackPrefab') or {}).get('assetPath')
            if not stack_path and row.get('returnClean') and role == 'plate_return':
                stack_path = CLEAN_PLATE_STACK
            if stack_path:
                return_stack = stock_prefab(stack_path, asset_paths)
                if not return_stack:
                    raise ImportErrorDetail(f"{level['scene']}: missing native return stack {stack_path}")
        return_links = None
        if role == 'serving':
            linked = row.get('returnLinks', [])
            if any(key not in returns for key in linked):
                raise ImportErrorDetail(f"{level['scene']}: serving station references an unavailable return point")
            return_links = [returns[key] for key in linked] if linked else list(returns.values())
        approvals = row.get('allowedIngredients', [])
        allowed = [source_approval(asset, asset_paths, order_assets) for asset in approvals]
        unresolved = [ref.get('name') or ref.get('ref') for ref, result in zip(approvals, allowed, strict=True) if not result]
        feed = {}
        if role == 'attached_spawner':
            x, _, z = row['position']
            dx, _, dz = rotate(row['rotation'], [1.2, 0, 0])
            target = [station for station in fixed if station['role'] == 'conveyor'
                      and dist((x + dx, z + dz), (station['position'][0], station['position'][2])) <= .32]
            if len(target) != 1:
                raise ImportErrorDetail(f"{level['scene']}: no unique conveyor cell in front of {row['path']}")
            items = [dispenser_prefab(ref['assetPath'], asset_paths, level['orders']) for ref in row['spawnItems']]
            if not all(items):
                raise ImportErrorDetail(f"{level['scene']}: unavailable item for {row['path']}")
            feed = {'attachedSpawn': {'items': items, 'weights': row['spawnWeights'] or [1.0] * len(items),
                                      'seconds': row['spawnInterval'], 'target': ids[id(target[0])],
                                      'inOrder': bool(row['spawnInOrder']), 'atStart': bool(row['spawnAtStart'])}}
        if role == 'condiment_dispenser':
            keys = [choice['assetPath'].replace('\\', '/').casefold() for choice in row['condiments']]
            if any(key not in order_assets for key in keys):
                raise ImportErrorDetail(f"{level['scene']}: unavailable sauce for {row['path']}")
            feed = {'condimentItems': [order_assets[key] for key in keys]}
        if role == 'condiment_switch':
            feed = {'condimentTargets': [condiment_dispensers[key] for key in row['condimentTargets']]}
        output.append({'id': ids[id(row)], 'role': role, 'scene': donor['scene'], 'path': donor['path'],
                       'position': translated(row['position'], role == 'spawn'), 'rotation': row['rotation'],
                       'support': support, 'ingredient': item, 'capacity': row.get('capacity', 0),
                       'plateCount': row.get('plateCount', 0), 'returnStack': return_stack, 'returnStations': return_links,
                       'portalExit': portals[row['portalExit']] if role == 'teleportal' else None,
                       **({'portalDoubleSided': True} if role == 'teleportal' and row['portalDoubleSided'] else {}),
                       **({'motionSpeed': row['motionSpeed']} if role in ('conveyor', 'travelator') else {}),
                       'allowedIngredients': list(dict.fromkeys(key for key in allowed if key)), 'unresolvedIngredients': unresolved,
                       'allowedCookingStationTypes': row.get('allowedCookingStationTypes', []), 'sourcePath': row['path'],
                       **feed})

    grills = [row['position'] for row in output if row['role'] == 'barbecue']
    if grills and not any(row['role'] == 'extinguisher' for row in output):
        free = [row for row in output if row['role'] == 'counter' and row['id'] not in occupied]
        if free and (donor := placements.get('extinguisher')):
            min_x = min(row['position'][0] for row in output if row['support'] is None and row['role'] != 'spawn')
            max_x = max(row['position'][0] for row in output if row['support'] is None and row['role'] != 'spawn')
            centre_x = (min_x + max_x) / 2
            counter = min(free, key=lambda row: (abs(row['position'][0] - centre_x), min(
                (row['position'][0] - x)**2 + (row['position'][2] - z)**2 for x, _, z in grills)))
            x, y, z = counter['position']
            output.append({'id': f'p{len(source) + 1}', 'role': 'extinguisher', **donor,
                           'position': [x, y + .5, z], 'rotation': counter['rotation'], 'support': counter['id'],
                           'capacity': 0, 'sourcePath': None})
            if donor['scene'] not in (host['scene'], 'asset'):
                used_donors.add(donor['scene'])

    if spawns != 4:
        raise ImportErrorDetail(f"{level['scene']}: expected four authored spawn positions")
    if len({row['path'] for row in output if row['role'] == 'spawn'}) != 4:
        raise ImportErrorDetail(f"{level['scene']}: spawn IDs are ambiguous")
    # The native return station creates and synchronises pre-stocked plate, glass and tray stacks.
    # Keep the authored tile as the supply point and keep serving links associated with the actual returns.
    by_id = {row['id']: row for row in output}
    supplied = set()
    supply_template = placements['plate_return']
    for row in output:
        if row['role'] not in OPENING_STACKS or row['plateCount'] <= 0:
            continue
        counter = by_id.get(row['support'])
        if level['scene'] == 's_lbcy_5' and row['role'] == 'mug_stack':
            counter = next((station for station in output
                            if station['sourcePath'] == 'Design/Counters/Counter (12)'
                            and station['id'] not in occupied), None)
        elif level['scene'] == 's_lbcy_6' and row['role'] == 'plate_stack':
            # Both published clean stacks occupy dirty returns. Reserve one existing counter
            # in each respective kitchen so washing and serving can use the original returns.
            counters = {'Design/Utensils/CleanPlateStack': 'Design/Counters/Counter (31)',
                        'Design/Utensils/CleanPlateStack (1)': 'Design/Counters/Counter (37)'}
            counter = next((station for station in output if station['sourcePath'] == counters.get(row['sourcePath'])
                            and station['id'] not in occupied), None)
        elif level['scene'] == 'xqy_danrenlvtu_3' and row['role'] == 'plate_stack':
            counter = next((station for station in output if station['sourcePath'] == 'Design/Counters/Counter (1)'
                            and station['id'] not in occupied), None)
        elif counter is None:
            x, y, z = row['position']
            # Dirty vessel returns and opening clean stacks cannot share one native station.
            on_return = any(station['role'] in ('plate_return', 'glass_return')
                            and dist((x, z), (station['position'][0], station['position'][2])) <= .31
                            and abs(y - station['position'][1]) <= 1.0 for station in output)
            if on_return:
                nearby = [(dist((x, z), (station['position'][0], station['position'][2])), station['id'], station)
                          for station in output if station['role'] == 'counter' and station['id'] not in occupied
                          and abs(y - station['position'][1]) <= 1.0]
                if nearby and min(nearby)[0] <= 1.25:
                    counter = min(nearby)[2]
        if counter is None or counter['role'] != 'counter':
            raise ImportErrorDetail(f"{level['scene']}: no counter for the opening supply at {row['sourcePath']}")
        counter.update(role='plate_supply', scene=supply_template['scene'], path=supply_template['path'],
                       returnStack=row['path'], plateCount=row['plateCount'])
        supplied.add(row['id'])
    output = [row for row in output if row['id'] not in supplied]
    boxes = [{**box, 'position': translated(box['position'])} for box in level['boxColliders']]
    if level['scene'] == 's_lbcy_5':
        add_lbcy_floor_passage(boxes, translated([0, 0, -2.4])[2])
    orders = list({order['addressKey']: order for order in level['orders']}.values())
    roles = {row['role'] for row in source}
    theme = 'camp' if 'campfire' in roles else 'coast' if 'blender' in roles or 'barbecue' in roles else 'city'
    return {'schemaVersion': 1, 'name': level['name'] or level['scene'], 'targetBuild': host['build'],
            'targetGameSha256': host['gameSha256'], 'targetScene': host['scene'], 'moves': [],
            'recipeAllowlist': [order['addressKey'] for order in orders], 'campaignRules': rules,
            'provenance': {'sourceScene': level['scene'], 'sourceVersion': level['setVersion']},
            'stockKitchen': {'stations': output, 'boxColliders': boxes, 'recipes': orders,
                             'artOffset': shift, 'artTheme': theme, 'floorTexture': FLOOR_ART[theme],
                             'donors': {scene: donors[scene] for scene in sorted(used_donors)}}}


def create_stock_batch(game, inputs, host, station_map, output, source_directory=None, source_dependencies=None, *,
                       only_prepared=False):
    manifest = game.parent.parent / 'appmanifest_1243830.acf'
    build = re.search(r'"buildid"\s+"(\d+)"', manifest.read_text(encoding='utf-8-sig'))
    game_hash = sha256((game / 'GameAssembly.dll').read_bytes()).hexdigest()
    if build is None or build[1] != '22894075' or host['scene'] != 's_city_h2' or game_hash != host['gameSha256']:
        raise ImportErrorDetail('Stock batch requires the pinned AYCE Steam game and Kevin 2 profile')
    if (inputs.get('format') != 'ayce-oc2-static-layout-inputs/1' or not inputs['levels']
            or inputs['preparedCount'] != len(inputs['levels']) or inputs['rejectedCount'] != len(inputs['rejected'])):
        raise ImportErrorDetail('Expected static batch inputs with consistent counts and at least one prepared level')
    if inputs['rejectedCount'] and not only_prepared:
        raise ImportErrorDetail('Some levels are blocked; use --only-prepared to package the prepared subset')
    tables = game / 'Overcooked All You Can Eat_Data/StreamingAssets/aa/Windows'
    catalog = json.loads((tables / 'catalog.json').read_text(encoding='utf-8-sig'))
    names = {row['scene'] for row in station_map.values()} - {host['scene'], 'asset'}
    donors = scene_dependencies(catalog, names)
    asset_paths = native_prefab_addresses(catalog)
    order_assets = native_order_assets(catalog)
    packed_levels, overlays = [], []
    rejected = list(inputs['rejected'])
    for level in inputs['levels']:
        try:
            overlay = stock_overlay(level, host, donors, station_map, asset_paths, order_assets)
        except ImportErrorDetail as error:
            if not only_prepared:
                raise
            rejected.append({key: level[key] for key in ('archive', 'scene', 'name')} | {'error': str(error)})
            continue
        overlays.append(overlay)
        packed_levels.append(level)
    if not overlays:
        raise ImportErrorDetail('No levels passed native overlay compilation')
    auditor = StockFoodAudit(tables, catalog, overlays)
    audited_levels, audited_overlays, food_audit = [], [], {}
    for level, overlay in zip(packed_levels, overlays, strict=True):
        try:
            checks = auditor.verify(overlay)
            food_audit[level['scene']] = checks
            problems = [(label, checks[key]) for key, label in (
                ('missingIngredients', 'missing native ingredients'),
                ('unsupportedFryerInputs', 'fryer inputs not accepted'),
                ('unsupportedMixerRecipes', 'no single compatible mixer'),
            ) if checks[key]]
            if problems:
                raise ImportErrorDetail('; '.join(f"{label}: {', '.join(names)}" for label, names in problems))
        except ImportErrorDetail as error:
            if not only_prepared:
                raise ImportErrorDetail(f"{level['scene']}: {error}") from error
            rejected.append({key: level[key] for key in ('archive', 'scene', 'name')} | {'error': str(error)})
            continue
        audited_levels.append(level)
        audited_overlays.append(overlay)
    packed_levels, overlays = audited_levels, audited_overlays
    if not overlays:
        raise ImportErrorDetail('No levels passed the native food audit')
    del auditor
    use = Counter(scene for entry in overlays for scene in entry['stockKitchen']['donors'])
    template_paths = {}
    travelator_paths = {}
    for entry in overlays:
        for station in entry['stockKitchen']['stations']:
            scene = station['scene']
            if scene != 'asset':
                template_paths.setdefault(scene, set()).add(station['path'])
            if station['role'] == 'travelator':
                travelator_paths.setdefault(scene, set()).add(station['path'])
    donor_files = {}
    for scene, paths in template_paths.items():
        if not re.fullmatch(r's_[a-z0-9_]+', scene):
            raise ImportErrorDetail(f'Invalid donor scene: {scene}')
        source = tables / 'StandaloneWindows64' / f'{scene}_scenes_all.bundle'
        validate_scene_templates(source, paths, travelator_paths.get(scene, ()))
        donor_files[scene] = source
    (output / 'donors').mkdir(parents=True, exist_ok=True)
    (output / 'levels').mkdir(exist_ok=True)
    file_hashes = {}
    art_results = {}
    if source_directory is not None:
        model_addresses = native_visual_models(catalog)
        source_directory = source_directory.resolve(strict=True)
        (output / 'art').mkdir(exist_ok=True)
        packed = {}
        for layout, overlay in zip(packed_levels, overlays, strict=True):
            source = (source_directory / layout['archive']).resolve(strict=True)
            if source.parent != source_directory or source.suffix.lower() != '.zip':
                raise ImportErrorDetail('A visual source must be a release ZIP within the archive directory')
            packed.setdefault(source, []).append((layout, overlay))
        for source, levels in packed.items():
            files, _ = load_bundles(source)
            bundles = Bundles(files, source_dependencies)
            for layout, overlay in levels:
                scene = layout['scene']
                if scene == 's_yizhijuan_1':
                    # The background in this scene occludes the play area in the static camera.
                    continue
                if not re.fullmatch(r'[A-Za-z0-9_-]+', scene):
                    raise ImportErrorDetail(f'Invalid visual scene name: {scene}')
                original = next((name for name in (scene, scene + '.bundle') if name in files), None)
                if not original:
                    raise ImportErrorDetail(f'Missing original visual scene: {scene}')
                filename = f'art/{scene}.zip'
                statistics = export_scene_art(bundles, original, output / filename, model_addresses,
                                              include_roots=SCENE_ART_ROOTS.get(scene),
                                              exclude_roots=HIDDEN_SCENE_ART.get(scene, ()))
                art_results[scene] = statistics
                if statistics.get('renderers', 0) or statistics.get('nativeModels', 0):
                    overlay['stockKitchen']['art'] = scene + '.zip'
                    file_hashes[filename] = sha256((output / filename).read_bytes()).hexdigest()
    for scene in sorted(use):
        filename = f'donors/{scene}.bundle'
        write_dormant_scene(donor_files[scene], output / filename)
        file_hashes[filename] = sha256((output / filename).read_bytes()).hexdigest()
    for entry in overlays:
        scene = entry['provenance']['sourceScene']
        if not re.fullmatch(r'[A-Za-z0-9_-]+', scene):
            raise ImportErrorDetail(f'Invalid batch scene name: {scene}')
        filename = f'levels/{scene}.json'
        if filename in file_hashes:
            raise ImportErrorDetail(f'Duplicate test level name: {scene}')
        path = output / filename
        path.write_text(json.dumps(entry, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')
        file_hashes[filename] = sha256(path.read_bytes()).hexdigest()
    return {'format': 'ayce-oc2-stock-test/1', 'build': build[1], 'gameSha256': game_hash,
            'levels': len(overlays), 'donors': len(use), 'placements': sum(len(x['stockKitchen']['stations']) for x in overlays),
            'art': art_results, 'sha256': file_hashes, 'sourceBatch': inputs.get('sourceBatch'),
            'rejected': rejected, 'foodAudit': food_audit}
