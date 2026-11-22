import argparse
from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import re
import sys
from zipfile import BadZipFile

from .addressables import native_recipe_locations
from .assets import Bundles, ImportErrorDetail, load_bundles, scene_data, xyz
from .batches import migration_batches
from .inventory import catalog_recipe_paths, inspect_corpus
from .kitchen import kevin2_layout
from .static_layout import prepare_static_batch
from .stock_batch import create_stock_batch


TEMPLATE_RECIPE_ASSETS = {
    's_sushi_1_1': {
        'assets/data/orderdefinitions/recipeitems/sushi/sushi_plainfish.asset': 'Sushi_PlainFish',
        'assets/data/orderdefinitions/recipeitems/sushi/sushi_plainprawn.asset': 'Sushi_PlainPrawn',
    },
    's_city_h2': {
        'assets/data/orderdefinitions/recipeitems/steamedspecials/steamedspecial_carrot.asset': 'SteamedSpecial_Carrot',
        'assets/data/orderdefinitions/recipeitems/steamedspecials/steamedspecial_fish.asset': 'SteamedSpecial_Fish',
        'assets/data/orderdefinitions/recipeitems/steamedspecials/steamedspecial_meat.asset': 'SteamedSpecial_Meat',
        'assets/data/orderdefinitions/recipeitems/steamedspecials/steamedspecial_prawns.asset': 'SteamedSpecial_Prawns',
    },
}


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


def inspect(path, dependencies, selected_scene=None, include_layout=False, *, loaded_dependencies=None, include_recipe_graph=False):
    files, ignored = load_bundles(path)
    names = {name.lower(): name for name in files}
    if loaded_dependencies is None:
        loaded_dependencies = dependency_assets(dependencies)
    bundles = Bundles(files, loaded_dependencies)
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
                recipe_pointers = [p for p in ld.get('recipes', []) if p.get('m_PathID')]
                item['recipes'] = [bundles.recipe(level, p) for p in recipe_pointers]
                if include_recipe_graph:
                    item['recipeGraph'] = bundles.recipe_graph(level, recipe_pointers)
                scene_file = names.get(scene.lower()) or names.get(scene.lower() + '.bundle')
                item['scene_bundle'] = scene_file
                item.update(scene_data(bundles, scene_file, include_layout=include_layout) if scene_file else {'stations': [], 'mechanics': []})
                if not scene_file:
                    item['diagnostic'] = 'Scene bundle is missing from this release'
                source['levels'].append(item)
            if source['levels']:
                report['sets'].append(source)
    if not report['sets']:
        raise ImportErrorDetail('No matching OC2DIYLevel LevelSetInfoSO and scene found')
    report['unreadable_serialized_objects'] = dict(bundles.unreadable)
    return report


def target(game, scene, include_layout=False, dependencies=()):
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
    data = scene_data(Bundles(sources, dependency_assets(dependencies)), bundle.name,
                      include_layout=include_layout, native=True)
    if not data['stations']:
        raise ImportErrorDetail('No supported station transforms in target scene')
    return {'format': 'ayce-target/1', 'scene': scene.lower(), 'build': match[1], 'gameSha256': fingerprint(live),
            'recipeAssets': TEMPLATE_RECIPE_ASSETS.get(scene.lower(), {}), **data}


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
    scene = profile.get('scene')
    if scene not in TEMPLATE_RECIPE_ASSETS or len(moves) > 26 or (not moves and not recipes):
        raise ImportErrorDetail('Overlay must use a supported AYCE template and include moves or recipes')
    if scene == 's_city_h2' and moves:
        raise ImportErrorDetail('Kevin 2 currently supports recipe overlays only')
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


def map_recipes(level, profile, mapping):
    explicit = mapping.get('recipeMap', {})
    use_asset_matches = mapping.get('matchRecipes', False)
    if not isinstance(explicit, dict) or not isinstance(use_asset_matches, bool):
        raise ImportErrorDetail('recipeMap must be an object and matchRecipes must be a boolean')
    known = {r['name'] for r in level['recipes'] if r.get('name') and 'definition' not in r}
    if any(name not in known or not isinstance(target_name, str) or not target_name for name, target_name in explicit.items()):
        raise ImportErrorDetail('Recipe mapping requires a resolved non-custom source recipe and an exact AYCE recipe name')
    if not use_asset_matches:
        return explicit

    target_assets = TEMPLATE_RECIPE_ASSETS.get(profile.get('scene'), {})
    automatically_matched = {}
    for recipe in level['recipes']:
        asset = recipe.get('assetPath', '').replace('\\', '/').casefold()
        if recipe.get('name') in known and asset in target_assets:
            automatically_matched[recipe['name']] = target_assets[asset]
    if not automatically_matched:
        raise ImportErrorDetail('No source recipe asset matches this AYCE template. Supply the source dependencies or choose a compatible template.')
    return {**automatically_matched, **explicit}


def campaign_rules(level):
    def positive_int(value, label):
        if not isinstance(value, int) or isinstance(value, bool) or not 1 <= value <= 3600:
            raise ImportErrorDetail(f'Invalid source timing for {label}')
        return value

    rules = {}
    for count, source in level['players'].items():
        if count not in ('1', '2', '3', '4'):
            raise ImportErrorDetail(f'Unsupported source player count: {count}')
        stars = [source.get(f'm_{key}StarScore') for key in ('One', 'Two', 'Three', 'Four')]
        if any(not isinstance(n, int) or isinstance(n, bool) or n < 0 for n in stars) or stars != sorted(stars):
            raise ImportErrorDetail(f'Invalid source star thresholds for {count} players')
        rules[count] = {
            'roundSeconds': positive_int(source.get('roundTime'), 'roundTime'),
            'orderSeconds': positive_int(source.get('orderLifeTime'), 'orderLifeTime'),
            'intervalSeconds': positive_int(source.get('timeBetweenOrders'), 'timeBetweenOrders'),
            'starPoints': stars,
        }
    if '1' not in rules:
        raise ImportErrorDetail('A custom campaign needs a one-player source configuration')
    return rules


def convert(source_path, dependencies, mapping, profile, allow_partial):
    if profile.get('format') != 'ayce-target/1' or profile.get('scene') != mapping.get('targetScene'):
        raise ImportErrorDetail('Mapping target scene and target profile differ')
    want_layout = mapping.get('kitchenLayout') is True
    report = inspect(source_path, dependencies, mapping['sourceScene'], include_layout=want_layout)
    owned = [(s, l) for s in report['sets'] for l in s['levels']]
    if len(owned) != 1 or not owned[0][1]['scene_bundle']:
        raise ImportErrorDetail('Select exactly one source level with a scene bundle')
    source, level = owned[0]
    return convert_level(report['input'], source, level, mapping, profile, allow_partial)


def convert_level(input_name, source, level, mapping, profile, allow_partial):
    want_layout = mapping.get('kitchenLayout') is True
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
    recipes = map_recipes(level, profile, mapping)
    if not applied and (moves or not recipes):
        raise ImportErrorDetail('Map at least one community station or export a menu without station moves')
    unported = {'stations': [s['path'] for s in level['stations'] if s['active'] and s['path'] not in applied],
                'recipes': [r for r in level['recipes'] if r.get('name') not in recipes], 'mechanics': level['mechanics'],
                'playerSettings': level['players'], 'levelGeometry': 'AYCE template geometry retained'}
    if not allow_partial:
        raise ImportErrorDetail('Overlay retains AYCE geometry and player settings. Review inspection and pass --allow-partial to export it.')
    provenance = {'input': input_name, 'author': source.get('author'), 'sourceVersion': source.get('version'), 'sourceScene': level['sceneName']}
    result = package(profile, level.get('levelNameZH') or level['levelName'], moves, dict.fromkeys(recipes.values()), provenance)
    if mapping.get('customEntry'):
        if profile['scene'] != 's_city_h2' or not recipes:
            raise ImportErrorDetail('The test entry requires a Kevin 2 menu overlay')
        result['campaignRules'] = campaign_rules(level)
        unported['playerSettings'] = {
            count: {k: v for k, v in row.items() if k in ('plateReturnTime', 'survivalTimeMultiplier')}
            for count, row in level['players'].items()
        }
    if want_layout:
        if not mapping.get('customEntry'):
            raise ImportErrorDetail('A kitchen layout requires the F8 custom entry')
        result['kitchen'], unported['layoutPrefabs'] = kevin2_layout(level, profile)
        converted = {row['source'] for row in result['kitchen']['placements']}
        unported['stations'] = [path for path in unported['stations'] if path not in converted]
        unported['levelGeometry'] = 'Source box colliders and layout; runtime floor and wall visuals; AYCE kitchen props'
        applied.update(converted)
    return result, {'format': 'ayce-compatibility/1', 'status': 'partial-overlay', 'provenance': provenance,
                    'convertedStations': sorted(applied), 'recipeMappings': recipes, 'notPorted': unported}


def main(argv=None):
    parser = argparse.ArgumentParser(description='OC2DIYLevel to AYCE development bridge')
    commands = parser.add_subparsers(dest='command', required=True)
    read = commands.add_parser('inspect', help='inspect an OC2DIYLevel ZIP or folder')
    read.add_argument('input')
    read.add_argument('--dependency', action='append', default=[])
    read.add_argument('--scene')
    read.add_argument('--layout', action='store_true', help='include resolved prefab placements and authored box colliders')
    read.add_argument('--recipe-graph', action='store_true', help='include reusable ingredients, cooked or mixed subrecipes and other referenced resources')
    read.add_argument('--out', required=True)
    inventory = commands.add_parser('inventory', help='assess each level in a collection of OC2DIYLevel ZIPs')
    inventory.add_argument('inputs', nargs='+', help='ZIPs or directories that contain ZIPs')
    inventory.add_argument('--dependency', action='append', default=[])
    inventory.add_argument('--profile', required=True, help='AYCE Kevin 2 target profile generated with --layout')
    inventory.add_argument('--ayce-catalog', help='local AYCE Addressables catalog.json for exact stock recipe matches')
    inventory.add_argument('--export-previews', help='directory for layouts the bridge can currently export')
    inventory.add_argument('--out', required=True)
    batches = commands.add_parser('batches', help='group inventoried levels by shared migration requirements')
    batches.add_argument('inventory', help='inventory JSON generated with --ayce-catalog')
    batches.add_argument('--out', required=True)
    prepare = commands.add_parser('prepare-static', help='extract static kitchen and native recipe inputs for a whole batch')
    prepare.add_argument('batches', help='migration report generated by the batches command')
    prepare.add_argument('archive_directory')
    prepare.add_argument('--dependency', action='append', default=[])
    prepare.add_argument('--ayce-catalog', required=True)
    prepare.add_argument('--out', required=True)
    stock = commands.add_parser('pack-static', help='build the playable test package from prepared static inputs')
    stock.add_argument('game')
    stock.add_argument('inputs')
    stock.add_argument('--profile', required=True)
    stock.add_argument('--stations', required=True)
    stock.add_argument('--archives', help='directory containing original community scene ZIPs for art export')
    stock.add_argument('--dependency', action='append', default=[], help='original OC2DIYLevel shared data needed by scene art')
    stock.add_argument('--out', required=True, help='directory for the new test package')
    scan = commands.add_parser('target', help='inspect one AYCE scene in a local Steam install')
    scan.add_argument('game')
    scan.add_argument('scene')
    scan.add_argument('--layout', action='store_true', help='include native station and collision candidates')
    scan.add_argument('--dependency', action='append', default=[], help='resolve references from a local AYCE asset bundle')
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
            data = inspect(args.input, args.dependency, args.scene, include_layout=args.layout, include_recipe_graph=args.recipe_graph)
        elif args.command == 'batches':
            if Path(args.inventory).resolve() == Path(args.out).resolve():
                raise ImportErrorDetail('Batch output must not overwrite its inventory')
            data = migration_batches(read_json(args.inventory))
        elif args.command == 'prepare-static':
            output = Path(args.out).resolve()
            if output.suffix.lower() != '.json' or output in {Path(path).resolve() for path in (args.batches, args.ayce_catalog, *args.dependency)}:
                raise ImportErrorDetail('Static batch output must be a separate JSON file')
            bundles = dependency_assets(args.dependency)

            def read_source(path):
                return inspect(path, (), include_layout=True, loaded_dependencies=bundles)

            data = prepare_static_batch(read_json(args.batches), args.archive_directory, read_source,
                                        native_recipe_locations(read_json(args.ayce_catalog)), campaign_rules)
        elif args.command == 'pack-static':
            destination = Path(args.out).resolve()
            game = Path(args.game).resolve(strict=True)
            if destination.is_relative_to(game):
                raise ImportErrorDetail('Generate the package outside the installed game directory')
            archives = Path(args.archives) if args.archives else None
            result = create_stock_batch(game, read_json(args.inputs), read_json(args.profile), read_json(args.stations), destination,
                                        archives, dependency_assets(args.dependency) if archives else None)
            write_json(destination / 'manifest.json', result)
            return
        elif args.command == 'inventory':
            sources = [args.profile, *args.dependency]
            if args.ayce_catalog:
                sources.append(args.ayce_catalog)
            if Path(args.out).resolve() in {Path(path).resolve() for path in sources}:
                raise ImportErrorDetail('Inventory output must not overwrite a profile, catalog or dependency')
            profile = read_json(args.profile)
            if profile.get('format') != 'ayce-target/1' or profile.get('scene') != 's_city_h2' or not profile.get('layout'):
                raise ImportErrorDetail('Inventory requires an AYCE Kevin 2 target profile generated with --layout')
            bundles = dependency_assets(args.dependency)

            def read_archive(path):
                return inspect(path, (), include_layout=True, loaded_dependencies=bundles)

            def compile_preview(filename, level_set, level):
                mapping = {'matchRecipes': True, 'customEntry': True, 'kitchenLayout': True}
                manifest, _ = convert_level(filename, level_set, level, mapping, profile, allow_partial=True)
                return manifest

            catalog_paths = catalog_recipe_paths(read_json(args.ayce_catalog)) if args.ayce_catalog else {}
            data = inspect_corpus(args.inputs, args.out, read_archive, profile,
                                  TEMPLATE_RECIPE_ASSETS['s_city_h2'], campaign_rules, catalog_paths,
                                  args.export_previews, compile_preview)
        elif args.command == 'target':
            data = target(args.game, args.scene, include_layout=args.layout, dependencies=args.dependency)
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
