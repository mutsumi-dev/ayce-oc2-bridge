from collections import defaultdict
from math import isfinite
from pathlib import Path

from .assets import ATTACHED_SPAWNER_STUB, BELT_STUBS, SWITCH_STUB, ImportErrorDetail
from .batches import ADAPTATION_BATCH, STATIC_BATCH
from .inventory import INSPECTION_ERRORS
from .kitchen import PREFAB_ROLES, prefab_name


STATION_PREFABS = {
    **PREFAB_ROLES,
    'countertop_01_chopping_board_wood_no_edge': 'chopping',
    'workstation_blender_01': 'blender', 'workstation_campfire_01': 'campfire',
    'workstation_barbeque_01': 'barbecue', 'workstation_glass_return_01': 'glass_return',
    'cleanglassstack': 'glass_stack', 'cleanmugstack': 'mug_stack', 'dlc08_cleantraystack': 'tray_stack',
    'dlc08_workstation_tray_return': 'plate_return', 'dlc08_equipment_tray': 'serving_tray',
    'workstation_mixer_01 (1)': 'mixer', 'fryingstation': 'fryer', 'frierbasket': 'fryer_basket',
    'utensil_blender_01': 'blender_cup', 'utensil_frying_pan_01': 'pan', 'utensil_pot_01': 'pot',
    'utensil_griddlepan': 'griddle', 'utensil_skewer_01': 'skewer',
    'utensil_roasting_tray': 'roasting_tray', 'utensil_toasting_fork_01': 'toasting_fork',
    'utensil_bellows_01': 'bellows', 'utensil_water_gun_01': 'water_gun',
}
SUPPORTED_PORTAL_COLORS = frozenset((0, 1, 2, 3, 4, 6, 8))
# The Moon Festival release omits these shared editor assets. The serialized references
# and station roles identify standard tiles, and their layout data is still present.
MOON_FESTIVAL_STATIONS = {
    -7513137428261227982: ('chopping', 'DLC13_countertop_01_chopping_board_wood_no_edge'),
    355823697495294191: ('counter', 'DLC13_countertop_01_standard_wood'),
    -8005216682675316297: ('serving', 'workstation_plate_station'),
    47294852285951182: ('sink', 'DLC13_workstation_sink_01_wood'),
    2221866460707262266: ('trash', 'DLC13_workstation_bin_01'),
}


def restore_moon_festival_station(row):
    prefab = row.get('prefab') or {}
    cab, _, identifier = prefab.get('ref', '').partition(':')
    if prefab.get('assetPath') or cab != 'cab-760ce37c9e5aea3c7d27df2755f830f8':
        return row
    if not identifier.removeprefix('-').isdigit() or int(identifier) not in MOON_FESTIVAL_STATIONS:
        return row
    role, name = MOON_FESTIVAL_STATIONS[int(identifier)]
    if row.get('role') != role:
        return row
    return {**row, 'prefab': {**prefab, 'assetPath': f'Assets/Prefabs/Shared_Kitchen/{name}.prefab'}}


def adapt_condiment_placements(placements):
    switches = [row for row in placements if row['stub'] == SWITCH_STUB]
    dispensers = {row['sourceObject']: row for row in placements if row.get('condiments')}
    if (not switches or any(row.get('moving_parent') or row.get('scaled_parent') or row['scale'] != [1, 1, 1]
                   for row in (*switches, *dispensers.values()))):
        return None
    for row in dispensers.values():
        paths = [(choice or {}).get('assetPath', '').replace('\\', '/').casefold() for choice in row['condiments']]
        valid = {f'assets/downloadablecontent/dlc08/dlc_assets/data/orderdefinitions/ingredients/{name}.asset'
                 for name in ('ketchup', 'mustard')}
        if len(paths) != 2 or set(paths) != valid:
            return None
    links = {}
    by_name = defaultdict(list)
    for key, row in dispensers.items():
        by_name[row['path'].rsplit('/', 1)[-1]].append(key)
    for switch in switches:
        targets = switch.get('condimentTargets', [])
        if names := switch.get('condimentTargetNames'):
            if any(len(by_name[name]) != 1 for name in names):
                return None
            targets = [by_name[name][0] for name in names]
        if not targets or len(set(targets)) != len(targets) or not set(targets) <= dispensers.keys():
            return None
        links[id(switch)] = targets
    if {target for targets in links.values() for target in targets} != dispensers.keys():
        return None

    return [
        {**row, 'role': 'condiment_switch', 'condimentTargets': links[id(row)]} if row['stub'] == SWITCH_STUB else
        {**row, 'role': 'condiment_dispenser'} if row.get('condiments') else row
        for row in placements if row.get('sourceObject') not in dispensers or row.get('condiments')
    ]


def prepare_static_layout(level, native_recipes, make_rules, *, allow_mechanics=False):
    layout = level.get('layout') or {}
    blockers = []
    if not level['scene_bundle']:
        blockers.append('source scene is unavailable')
    if not layout.get('boxColliders'):
        blockers.append('no supported collision boxes')
    portal_stub = 'PseudoPrefabTeleportalStub'
    spray_stub = 'PseudoPrefabIngredientSprayStub'
    supported = {portal_stub, spray_stub, ATTACHED_SPAWNER_STUB, *BELT_STUBS} if allow_mechanics else set()
    placements = layout.get('placements', [])
    if allow_mechanics and SWITCH_STUB in level['mechanics']:
        condiment_layout = adapt_condiment_placements(placements)
        if condiment_layout is not None:
            supported.add(SWITCH_STUB)
            placements = condiment_layout
    unsupported = [name for name in level['mechanics'] if name not in supported]
    if unsupported:
        blockers.append('unsupported mechanics: ' + ', '.join(unsupported))
    has_portals = portal_stub in level['mechanics']
    if (allow_mechanics and (has_portals or ATTACHED_SPAWNER_STUB in level['mechanics']
                             or any(name in BELT_STUBS for name in level['mechanics']))
            and not any(box.get('layer') == 9 for box in layout.get('boxColliders', []))):
        blockers.append('no supported floor collider for the mechanism')
    recovered = set()
    if allow_mechanics:
        original = placements
        placements = [restore_moon_festival_station(row) for row in original]
        recovered = {(old['path'], f"{old['stub']} has no resolved prefab")
                     for old, new in zip(original, placements, strict=True) if new is not old}
    issues = [issue for issue in layout.get('unresolved', [])
              if (issue['path'], issue['reason']) not in recovered]
    if issues:
        reasons = ', '.join(sorted({issue['reason'] for issue in issues}))
        blockers.append(f'{len(issues)} unresolved placements or colliders: {reasons}')
    if blockers:
        raise ImportErrorDetail('; '.join(blockers))

    if has_portals:
        portals = [row for row in placements if row.get('stub') == portal_stub]
        portal_ids = {row.get('portalId') for row in portals}
        if len(portals) < 2 or len(portal_ids) != len(portals) or None in portal_ids:
            raise ImportErrorDetail('Missing or ambiguous authored portal endpoints')
        for row in portals:
            if (row.get('portalExit') not in portal_ids or row['portalExit'] == row['portalId']):
                raise ImportErrorDetail(f"Portal has no distinct active destination: {row['path']}")
            if (row.get('portalColor') not in SUPPORTED_PORTAL_COLORS or row.get('portalDoubleSided') not in (0, 1)
                    or row['scale'] != [1.0, 1.0, 1.0] or row['scaled_parent']
                    or prefab_name(row['prefab']['assetPath']) != 'teleportal'):
                raise ImportErrorDetail(f"Unsupported portal color, sidedness, transform or prefab: {row['path']}")

    orders = []
    for recipe in level['recipes']:
        source_path = recipe.get('assetPath', '').replace('\\', '/').casefold()
        location = native_recipes.get(source_path)
        if not location or 'definition' in recipe or not recipe.get('name'):
            raise ImportErrorDetail(f"Order has no exact native recipe: {recipe.get('name') or recipe.get('ref')}")
        score = recipe.get('score')
        if type(score) is not int or not 0 <= score <= 1023:
            raise ImportErrorDetail(f"Order has no valid source score: {recipe['name']}")
        orders.append({'sourceName': recipe['name'], 'score': score, **location})
    if not orders:
        raise ImportErrorDetail('Static conversion requires an explicit order list')

    kitchen, other = [], []
    for row in placements:
        if row.get('moving_parent'):
            raise ImportErrorDetail(f"Placement has an animated parent: {row['path']}")
        path = row['prefab']['assetPath']
        role = row['role'] if row['role'] in ('condiment_switch', 'condiment_dispenser') else STATION_PREFABS.get(prefab_name(path), row['role'])
        if row.get('stub') == portal_stub:
            role = 'teleportal'
        if row.get('stub') == spray_stub:
            role = 'ingredient_spray'
            if prefab_name(path) != 'utensil_ingredient_spray_01' or not (row.get('ingredient') or {}).get('assetPath'):
                raise ImportErrorDetail(f"Unsupported ingredient spray: {row['path']}")
        if row.get('stub') == ATTACHED_SPAWNER_STUB:
            role = 'attached_spawner'
            items = row.get('spawnItems', [])
            weights = row.get('spawnWeights', [])
            interval = row.get('spawnInterval')
            if (prefab_name(path) != 'attachingfoodspawner' or not 1 <= len(items) <= 32
                    or any(not (item or {}).get('assetPath') for item in items)
                    or weights and (len(weights) != len(items) or not all(
                        type(w) in (int, float) and isfinite(w) and w > 0 for w in weights))
                    or type(interval) not in (int, float) or not isfinite(interval) or not .1 <= interval <= 300
                    or row.get('spawnInOrder') not in (0, 1) or row.get('spawnAtStart') not in (0, 1)
                    or row['scale'] != [1.0, 1.0, 1.0] or row['scaled_parent']):
                raise ImportErrorDetail(f"Unsupported automatic food source: {row['path']}")
        if row.get('stub') in BELT_STUBS:
            role, required_prefab, _ = BELT_STUBS[row['stub']]
            speed = row.get('motionSpeed')
            if (prefab_name(path) != required_prefab or type(speed) not in (int, float)
                    or not isfinite(speed) or not -20 <= speed <= 20
                    or row['scale'] != [1.0, 1.0, 1.0] or row['scaled_parent']):
                raise ImportErrorDetail(f"Unsupported conveyor or travelator: {row['path']}")
        if role is None and row['stub'] == 'PseudoPrefabCookingUtensilStub':
            role = 'unmapped_utensil'
        placement = {**row, 'role': role}
        if role is None:
            other.append(placement)
        else:
            kitchen.append(placement)
    roles = {row['role'] for row in kitchen}
    if not {'spawn', 'serving'} <= roles or not roles.intersection(('dispenser', 'attached_spawner')):
        raise ImportErrorDetail('Source layout has no complete spawn, dispenser and serving placement set')
    return {
        'orders': orders,
        'campaignRules': make_rules(level),
        'kitchenPlacements': kitchen,
        'otherPlacements': other,
        'boxColliders': layout['boxColliders'],
    }


def prepare_static_batch(batches, source_directory, inspect_archive, native_recipes, make_rules, batch_id=STATIC_BATCH):
    if batches.get('format') != 'ayce-oc2-migration-batches/1':
        raise ImportErrorDetail('Expected a migration batch report from the batches command')
    batch = next((row for row in batches['batches'] if row['id'] == batch_id), None)
    if batch is None or batch.get('count') != len(batch['levels']):
        raise ImportErrorDetail(f'Missing or inconsistent batch: {batch_id}')
    root = Path(source_directory).resolve(strict=True)
    if not root.is_dir():
        raise ImportErrorDetail('Expected a directory containing the inventoried release ZIPs')
    queues = defaultdict(list)
    for row in batch['levels']:
        filename = row['archive']
        if Path(filename).name != filename or '/' in filename or '\\' in filename or ':' in filename or not filename.lower().endswith('.zip'):
            raise ImportErrorDetail(f'Invalid archive filename: {filename!r}')
        queues[filename].append(row)

    prepared, rejected = [], []
    for filename, rows in queues.items():
        try:
            archive = (root / filename).resolve(strict=True)
            if archive.parent != root:
                raise ImportErrorDetail(f'Archive resolves outside the source directory: {filename}')
            report = inspect_archive(archive)
            source = defaultdict(list)
            for level_set in report['sets']:
                for level in level_set['levels']:
                    source[level_set.get('uid'), level_set.get('version'), level['sceneName']].append(level)
        except INSPECTION_ERRORS as exc:
            rejected.extend({'archive': filename, 'scene': row['scene'], 'error': str(exc)} for row in rows)
            continue

        for row in rows:
            identity = {key: row[key] for key in ('archive', 'setUid', 'setVersion', 'scene', 'name')}
            try:
                matches = source[row['setUid'], row['setVersion'], row['scene']]
                if len(matches) != 1:
                    raise ImportErrorDetail('Source set, version and scene are missing or ambiguous')
                layout = prepare_static_layout(matches[0], native_recipes, make_rules,
                                               allow_mechanics=batch_id == ADAPTATION_BATCH)
                prepared.append({**identity, **layout})
            except INSPECTION_ERRORS as exc:
                rejected.append({**identity, 'error': str(exc)})
    return {
        'format': 'ayce-oc2-static-layout-inputs/1', 'ayceBuild': batches['ayceBuild'], 'sourceBatch': batch_id,
        'preparedCount': len(prepared), 'rejectedCount': len(rejected), 'levels': prepared, 'rejected': rejected,
    }
