from collections import defaultdict
from pathlib import Path

from .assets import ImportErrorDetail
from .inventory import INSPECTION_ERRORS
from .kitchen import PREFAB_ROLES, prefab_name


STATION_PREFABS = {
    **PREFAB_ROLES,
    'workstation_blender_01': 'blender', 'workstation_campfire_01': 'campfire',
    'workstation_barbeque_01': 'barbecue', 'workstation_glass_return_01': 'glass_return',
    'cleanglassstack': 'glass_stack', 'dlc08_cleantraystack': 'tray_stack', 'fryingstation': 'fryer', 'frierbasket': 'fryer_basket',
    'utensil_blender_01': 'blender_cup', 'utensil_frying_pan_01': 'pan', 'utensil_pot_01': 'pot',
    'utensil_griddlepan': 'griddle', 'utensil_skewer_01': 'skewer',
    'utensil_roasting_tray': 'roasting_tray', 'utensil_toasting_fork_01': 'toasting_fork',
    'utensil_bellows_01': 'bellows', 'utensil_water_gun_01': 'water_gun',
}


def prepare_static_layout(level, native_recipes, make_rules):
    layout = level.get('layout') or {}
    if not level['scene_bundle'] or not layout.get('boxColliders') or layout['unresolved'] or level['mechanics']:
        raise ImportErrorDetail('Static conversion requires an available scene, resolved collision boxes and no detected mechanics')

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
    for row in layout['placements']:
        if row.get('moving_parent'):
            raise ImportErrorDetail(f"Placement has an animated parent: {row['path']}")
        path = row['prefab']['assetPath']
        role = STATION_PREFABS.get(prefab_name(path), row['role'])
        if role is None and row['stub'] == 'PseudoPrefabCookingUtensilStub':
            role = 'unmapped_utensil'
        placement = {**row, 'role': role}
        if role is None:
            other.append(placement)
        else:
            kitchen.append(placement)
    roles = {row['role'] for row in kitchen}
    if not {'spawn', 'dispenser', 'serving'} <= roles:
        raise ImportErrorDetail('Source layout has no complete spawn, dispenser and serving placement set')
    return {
        'orders': orders,
        'campaignRules': make_rules(level),
        'kitchenPlacements': kitchen,
        'otherPlacements': other,
        'boxColliders': layout['boxColliders'],
    }


def prepare_static_batch(batches, source_directory, inspect_archive, native_recipes, make_rules):
    if batches.get('format') != 'ayce-oc2-migration-batches/1':
        raise ImportErrorDetail('Expected a migration batch report from the batches command')
    static = next((batch for batch in batches['batches'] if batch['id'] == 'stock-recipes-static-layout'), None)
    if static is None:
        raise ImportErrorDetail('Static stock recipe batch is missing from the report')
    root = Path(source_directory).resolve(strict=True)
    if not root.is_dir():
        raise ImportErrorDetail('Expected a directory containing the inventoried release ZIPs')
    queues = defaultdict(list)
    for row in static['levels']:
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
                layout = prepare_static_layout(matches[0], native_recipes, make_rules)
                prepared.append({**identity, **layout})
            except INSPECTION_ERRORS as exc:
                rejected.append({**identity, 'error': str(exc)})
    return {
        'format': 'ayce-oc2-static-layout-inputs/1', 'ayceBuild': batches['ayceBuild'],
        'preparedCount': len(prepared), 'rejectedCount': len(rejected), 'levels': prepared, 'rejected': rejected,
    }
