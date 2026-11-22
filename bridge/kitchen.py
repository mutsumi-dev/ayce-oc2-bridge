from collections import defaultdict
from pathlib import PurePosixPath

from .assets import ImportErrorDetail


OFFSET = (6.0, -1.0, 7.2)
SINK = 'Design/Animated Objects/Kitchen/Block_1/workstation_sink_01_wood'
PREFAB_ROLES = {
    'equipment_plate_01': 'plate',
    'utensil_fire_extinguisher_01': 'extinguisher',
    'utensil_mixer_01': 'mixing_bowl',
    'utensil_steamer_01': 'steamer',
    'workstation_mixer_01': 'mixer',
}
EXPECTED = {'spawn': 4, 'counter': 17, 'chopping': 3, 'dispenser': 4, 'mixer': 3,
            'plate_return': 1, 'serving': 1, 'sink': 1, 'stove': 3, 'trash': 1,
            'plate': 4, 'extinguisher': 1, 'mixing_bowl': 3, 'steamer': 3}
SUPPORTED_BY = {'mixing_bowl': 'mixer', 'steamer': 'stove', 'extinguisher': 'counter', 'plate': 'counter'}


def prefab_name(path):
    return PurePosixPath(path.replace('\\', '/')).stem.casefold()


def offset(position, height=0.0):
    return [round(position[0] + OFFSET[0], 5), round(position[1] + OFFSET[1] + height, 5),
            round(position[2] + OFFSET[2], 5)]


def kevin2_layout(level, profile):
    if level['sceneName'] != 's_dtsimple_4' or profile['scene'] != 's_city_h2':
        raise ImportErrorDetail('Full layout mapping currently supports DTsimple 4 on AYCE Kevin 2')
    source = level.get('layout') or {}
    target = profile.get('layout') or {}
    if not source or not target:
        raise ImportErrorDetail('Generate the AYCE target profile with --layout and the ingredient dependencies')
    if source['unresolved'] or len(source['boxColliders']) != 9:
        raise ImportErrorDetail('The source boundary geometry differs from the supported nine box colliders')

    source_roles = defaultdict(list)
    target_roles = defaultdict(list)
    unsupported = []
    for item in source['placements']:
        role = item['role'] or PREFAB_ROLES.get(prefab_name(item['prefab']['assetPath']))
        if role in EXPECTED and not item.get('moving_parent') and not item['scaled_parent'] and item['scale'] == [1, 1, 1]:
            source_roles[role].append(item)
        else:
            unsupported.append(item['path'])
    for item in target['placements']:
        target_roles[item['role']].append(item)
    if {role: len(items) for role, items in source_roles.items()} != EXPECTED:
        raise ImportErrorDetail('Source station counts differ from DTsimple 4')

    placements = []
    disabled = []
    for role, count in EXPECTED.items():
        originals = sorted(source_roles[role], key=lambda row: row['path'])
        candidates = sorted(target_roles[role], key=lambda row: row['path'])
        if not candidates:
            raise ImportErrorDetail(f'No native prefab candidates for {role}')
        if role == 'counter':
            disabled.extend(row['path'] for row in candidates if not row.get('moving_parent'))
            candidates = [row for row in candidates if row.get('moving_parent')]
            if len(candidates) != 15:
                raise ImportErrorDetail('Expected fifteen movable native counters')
        if role == 'plate_return':
            candidates = [row for row in candidates if row['path'].endswith('/workstation_plate_return')]
        if role == 'dispenser':
            by_ingredient = {row['ingredient'].get('name', '').casefold(): row for row in candidates}
            if set(by_ingredient) != {'fish', 'flour', 'meat', 'prawn', 'stewcarrot'}:
                raise ImportErrorDetail('Kevin 2 ingredient references are unresolved. Include the native asset dependencies.')
            disabled.append(by_ingredient['fish']['path'])
            candidates = [by_ingredient[{'carrot': 'stewcarrot'}.get(name, name)]
                          for name in (prefab_name(row['ingredient']['assetPath']) for row in originals)]
        copies = {'counter': 2, 'mixer': 1, 'mixing_bowl': 1}.get(role, 0)
        if len(candidates) != count - copies:
            raise ImportErrorDetail(f'Unexpected native count for {role}')
        candidates += candidates[:copies]
        for index, (original, native) in enumerate(zip(originals, candidates, strict=True)):
            path = native['path']
            if role == 'sink':
                if path != SINK + '/WashingPart':
                    raise ImportErrorDetail('Unexpected native sink assembly')
                path = SINK
            support = None
            if role in SUPPORTED_BY:
                x, _, z = original['position']
                matches = [row['path'] for row in source_roles[SUPPORTED_BY[role]]
                           if row['position'][0] == x and row['position'][2] == z]
                if len(matches) != 1:
                    raise ImportErrorDetail(f'Expected one work surface for {original["path"]}')
                support = matches[0]
            placement = {
                'source': original['path'], 'target': path, 'role': role,
                'clone': index >= count - copies, 'support': support,
                'reparent': bool(native.get('moving_parent')),
                'position': offset(original['position'], -0.5 if role == 'spawn' else 0.0),
                'rotation': original['rotation'],
            }
            if role in ('mixing_bowl', 'steamer') and 'capacity' in original:
                placement['capacity'] = original['capacity']
            placements.append(placement)

    boxes = [{**box, 'position': offset(box['position'])} for box in source['boxColliders']]
    if sorted(box['layer'] for box in boxes) != [9] + [10] * 8:
        raise ImportErrorDetail('Source ground and wall layers differ from the supported AYCE layer mapping')
    return {'placements': placements, 'disabled': sorted(disabled), 'boxColliders': boxes}, sorted(unsupported)
