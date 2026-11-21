import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import re
import sys
from zipfile import BadZipFile

from .assets import Bundles, ImportErrorDetail, load_bundles, scene_data, xyz


def write_json(path, data):
    Path(path).write_text(json.dumps(data, ensure_ascii=False, indent=2, allow_nan=False) + '\n', encoding='utf-8')


def read_json(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def fingerprint(path):
    with Path(path).open('rb') as stream:
        return sha256_stream(stream)


def sha256_stream(stream):
    hasher = sha256()
    for part in iter(lambda: stream.read(2**20), b''):
        hasher.update(part)
    return hasher.hexdigest()


def dependency_assets(paths):
    found = {}
    for path in paths:
        files, _ = load_bundles(path, required=False)
        for name, contents in files.items():
            if name.lower() in found and found[name.lower()][1] != contents:
                raise ImportErrorDetail(f'Conflicting dependency bundle: {name}')
            found[name.lower()] = (name, contents)
    return dict(found.values())


def inspect(path, dependencies, selected_scene=None):
    files, ignored = load_bundles(path)
    names = {name.lower(): name for name in files}
    bundles = Bundles(files, dependency_assets(dependencies))
    report = {'format': 'oc2diy-inspection/1', 'input': Path(path).name, 'ignored_executables': ignored, 'sets': []}
    for file in files:
        if not file.lower().startswith('info_'):
            continue
        entries = list(bundles.entry(file))
        by_key = {entry.key: entry for entry in entries}
        for entry in (a for a in entries if a.script == 'LevelSetInfoSO'):
            data = entry.tree
            source = {k: data.get(k) for k in ('levelSetName', 'levelSetNameZH', 'author', 'uid', 'version')}
            source['levels'] = []
            for p in data.get('levelInfos', []):
                obj, _ = bundles.ref(entry.obj, p)
                level = by_key.get((obj.assets_file.name, obj.path_id)) if obj is not None else None
                if level is None or level.script != 'LevelInfoSO':
                    raise ImportErrorDetail(f'{file}: unresolved LevelInfoSO')
                ld = level.tree
                scene = ld['sceneName']
                if selected_scene and scene.casefold() != selected_scene.casefold():
                    continue
                item = {k: ld[k] for k in ('sceneName', 'levelName', 'levelNameZH', 'levelType', 'dependencies', 'roundTimes', 'minOrderCount', 'maxOrderCount') if k in ld}
                item['players'] = {}
                for count in range(1, 5):
                    player, _ = bundles.ref(level.obj, ld.get(f'config_{count}p'))
                    config = by_key.get((player.assets_file.name, player.path_id)) if player is not None else None
                    if config is not None:
                        item['players'][str(count)] = {k: v for k, v in config.tree.items() if k in ('roundTime', 'orderLifeTime', 'timeBetweenOrders', 'plateReturnTime', 'survivalTimeMultiplier') or k.endswith('StarScore')}
                item['recipes'] = [bundles.recipe(level, p) for p in ld.get('recipes', []) if p.get('m_PathID')]
                scene_file = names.get(scene.lower()) or names.get(scene.lower() + '.bundle')
                item['scene_bundle'] = scene_file
                item.update(scene_data(bundles, scene_file) if scene_file else {'stations': [], 'mechanics': []})
                if not scene_file:
                    item['diagnostic'] = 'Scene bundle is missing from this release'
                source['levels'].append(item)
            if source['levels']:
                report['sets'].append(source)
    if not report['sets']:
        raise ImportErrorDetail('No matching OC2DIYLevel LevelSetInfoSO and scene found')
    report['unreadable_serialized_objects'] = dict(bundles.unreadable)
    return report


def target(game, scene):
    if not re.fullmatch(r'[a-zA-Z0-9_]+', scene):
        raise ImportErrorDetail('Invalid target scene name')
    game = Path(game)
    manifest = game.parent.parent / 'appmanifest_1243830.acf'
    match = re.search(r'"buildid"\s+"(\d+)"', manifest.read_text(encoding='utf-8-sig'))
    if match is None or match[1] != '22894075':
        raise ImportErrorDetail('Target requires AYCE Steam build 22894075')
    live = game / 'GameAssembly.dll'
    directory = game / 'Overcooked All You Can Eat_Data/StreamingAssets/aa/Windows/StandaloneWindows64'
    bundle = directory / f'{scene.lower()}_scenes_all.bundle'
    sources, _ = load_bundles(bundle)
    data = scene_data(Bundles(sources), bundle.name)
    if not data['stations']:
        raise ImportErrorDetail('No supported station transforms in target scene')
    return {'format': 'ayce-target/1', 'scene': scene.lower(), 'build': match[1], 'gameSha256': fingerprint(live), **data}


def by_path(items, path):
    matches = [item for item in items if item['path'] == path]
    if len(matches) != 1:
        raise ImportErrorDetail(f'Object path missing or ambiguous: {path}')
    if not matches[0]['active'] or matches[0]['scaled_parent']:
        raise ImportErrorDetail(f'Object is inactive or has a scaled parent: {path}')
    if matches[0].get('moving_parent'):
        raise ImportErrorDetail(f'Object is attached to an animated hierarchy: {path}')
    return matches[0]


def package(profile, name, moves, recipes=(), provenance=None):
    if profile.get('scene') != 's_sushi_1_1' or len(moves) not in range(1, 27):
        raise ImportErrorDetail('0.1.0 packages support up to 26 moves on the s_sushi_1_1 template')
    counts = Counter(m['path'] for m in moves)
    if any(count > 1 for count in counts.values()):
        raise ImportErrorDetail('A target station can only be moved once')
    all_occupied = [s for s in profile['stations'] if s['active'] and s['role'] != 'spawn' and s['path'] not in counts]
    for move in moves:
        for other in all_occupied:
            if sum((a-b)**2 for a, b in zip(move['position'], other['position'])) < 0.25:
                raise ImportErrorDetail(f"Destination is occupied: {move['path']} / {other['path']}")
        all_occupied.append(move)
    return {'schemaVersion': 1, 'name': name, 'targetBuild': profile['build'], 'targetGameSha256': profile['gameSha256'],
            'targetScene': profile['scene'], 'moves': moves, 'recipeAllowlist': list(recipes), 'provenance': provenance}


MOVABLE_ROLES = {'chopping', 'counter', 'trash'}


def move(old, position, rotation):
    if old['role'] not in MOVABLE_ROLES:
        raise ImportErrorDetail(f"Moving {old['role']} is not supported in this milestone")
    if abs(old['position'][1] - position[1]) > 0.05:
        raise ImportErrorDetail('Target changes the elevation of the station')
    if sum((a-b)**2 for a, b in zip(old['position'], position)) > 100:
        raise ImportErrorDetail('Target position is too far from the template station')
    return {'path': old['path'], 'expectedPosition': old['position'], 'position': position, 'rotation': rotation}


def make_demo(profile):
    if profile.get('scene') != 's_sushi_1_1':
        raise ImportErrorDetail('Demo requires the OC2 1-1 AYCE template')
    stations = profile['stations']
    a = by_path(stations, 'Design/Gameplay Stations/ChoppingBoard_Side 5 (1)')
    b = by_path(stations, 'Design/Work Surfaces/countertop_01_standard_edge (23)')
    return package(profile, 'AYCE bridge smoke test', [move(a, b['position'], b['rotation']), move(b, a['position'], a['rotation'])])


def convert(source_path, dependencies, mapping, profile, allow_partial):
    if profile.get('format') != 'ayce-target/1' or profile.get('scene') != mapping.get('targetScene'):
        raise ImportErrorDetail('Mapping target scene and target profile differ')
    report = inspect(source_path, dependencies, mapping['sourceScene'])
    owned = [(s, l) for s in report['sets'] for l in s['levels']]
    if len(owned) != 1 or not owned[0][1]['scene_bundle']:
        raise ImportErrorDetail('Select exactly one source level with a scene bundle')
    source, level = owned[0]
    offset = xyz(dict(zip('xyz', mapping.get('offset', [0, 0, 0]), strict=True)))
    applied, moves = set(), []
    for row in mapping.get('stationMap', []):
        before = by_path(level['stations'], row['source'])
        old = by_path(profile['stations'], row['target'])
        if before['role'] != old['role'] or row['source'] in applied:
            raise ImportErrorDetail('Station roles differ or source station was mapped twice')
        if any(abs(v-1) > 0.001 for v in before['scale']):
            raise ImportErrorDetail(f"Scaled source object: {before['path']}")
        applied.add(row['source'])
        position = [round(a+b, 5) for a, b in zip(before['position'], offset)]
        moves.append(move(old, position, before['rotation']))
    for row in mapping.get('vacate', []):
        old = by_path(profile['stations'], row['target'])
        destination = by_path(profile['stations'], row['toOriginalPositionOf'])
        moves.append(move(old, destination['position'], destination['rotation']))
    if not applied:
        raise ImportErrorDetail('Map at least one community station to a template station')
    recipes = mapping.get('recipeMap', {})
    known = {r['name'] for r in level['recipes'] if r.get('name') and 'definition' not in r}
    if any(name not in known or not isinstance(target_name, str) or not target_name for name, target_name in recipes.items()):
        raise ImportErrorDetail('Recipe mapping requires a resolved non-custom source recipe and an exact AYCE recipe name')
    unported = {'stations': [s['path'] for s in level['stations'] if s['active'] and s['path'] not in applied],
                'recipes': [r for r in level['recipes'] if r.get('name') not in recipes], 'mechanics': level['mechanics'],
                'playerSettings': level['players'], 'levelGeometry': 'AYCE template geometry retained'}
    if not allow_partial:
        raise ImportErrorDetail('Overlay retains AYCE geometry and player settings. Review inspection and pass --allow-partial to export it.')
    provenance = {'input': report['input'], 'author': source.get('author'), 'sourceVersion': source.get('version'), 'sourceScene': level['sceneName']}
    result = package(profile, level.get('levelNameZH') or level['levelName'], moves, dict.fromkeys(recipes.values()), provenance)
    return result, {'format': 'ayce-compatibility/1', 'status': 'partial-overlay', 'provenance': provenance,
                    'convertedStations': sorted(applied), 'recipeMappings': recipes, 'notPorted': unported}


def main(argv=None):
    parser = argparse.ArgumentParser(description='OC2DIYLevel to AYCE development bridge')
    commands = parser.add_subparsers(dest='command', required=True)
    read = commands.add_parser('inspect', help='inspect an OC2DIYLevel ZIP or folder')
    read.add_argument('input')
    read.add_argument('--dependency', action='append', default=[])
    read.add_argument('--scene')
    read.add_argument('--out', required=True)
    scan = commands.add_parser('target', help='inspect one AYCE scene in a local Steam install')
    scan.add_argument('game')
    scan.add_argument('scene')
    scan.add_argument('--out', required=True)
    demo = commands.add_parser('demo', help='create the AYCE 1-1 dry-run manifest')
    demo.add_argument('profile')
    demo.add_argument('--out', required=True)
    migrate = commands.add_parser('convert', help='apply an explicit mapping and write an AYCE overlay')
    migrate.add_argument('input')
    migrate.add_argument('--profile', required=True)
    migrate.add_argument('--mapping', required=True)
    migrate.add_argument('--dependency', action='append', default=[])
    migrate.add_argument('--out', required=True)
    migrate.add_argument('--report', required=True)
    migrate.add_argument('--allow-partial', action='store_true')
    args = parser.parse_args(argv)
    try:
        if args.command == 'inspect':
            data = inspect(args.input, args.dependency, args.scene)
        elif args.command == 'target':
            data = target(args.game, args.scene)
        elif args.command == 'demo':
            data = make_demo(read_json(args.profile))
        else:
            paths = [Path(p).resolve() for p in (args.input, args.profile, args.mapping, args.out, args.report)]
            if len(set(paths)) != len(paths):
                raise ImportErrorDetail('Input, profile, mapping and both output files must have distinct paths')
            data, report = convert(args.input, args.dependency, read_json(args.mapping), read_json(args.profile), args.allow_partial)
            write_json(args.report, report)
        write_json(args.out, data)
    except (ImportErrorDetail, OSError, KeyError, BadZipFile, TypeError, ValueError) as ex:
        parser.exit(2, f'Bridge: {ex}\n')


if __name__ == '__main__':
    sys.exit(main())
