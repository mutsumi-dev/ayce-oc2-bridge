from collections import Counter
from hashlib import sha256
import json
from pathlib import Path
import re
from zipfile import BadZipFile

from .assets import ImportErrorDetail
from .kitchen import kevin2_layout


INSPECTION_ERRORS = (OSError, BadZipFile, ValueError, KeyError, TypeError, RuntimeError,
                     EOFError, IndexError, AttributeError, NotImplementedError)


def catalog_recipe_paths(catalog):
    paths = catalog.get('m_InternalIds')
    if not isinstance(paths, list) or any(not isinstance(path, str) for path in paths):
        raise ImportErrorDetail('Expected an AYCE Addressables catalog with m_InternalIds')
    recipe_paths = {}
    for path in paths:
        key = path.replace('\\', '/').casefold()
        if '/orderdefinitions/recipeitems/' in key and key.endswith('.asset'):
            recipe_paths[key] = path
    if not recipe_paths:
        raise ImportErrorDetail('AYCE catalog contains no stock recipe item paths')
    return recipe_paths


def assess_level(level, profile, recipe_assets, make_rules, catalog_paths):
    layout = level.get('layout') or {}
    recipes = []
    for recipe in level['recipes']:
        asset = recipe.get('assetPath', '').replace('\\', '/').casefold()
        target = recipe_assets.get(asset)
        if 'definition' in recipe:
            status = 'custom-definition'
        elif not asset or not recipe.get('name'):
            status = 'unresolved-reference'
        elif not target:
            status = 'outside-donor-menu'
        else:
            status = 'matched-donor-recipe'
        recipes.append({
            **recipe, 'status': status,
            'target': target if status == 'matched-donor-recipe' else None,
            'ayceCatalogPath': catalog_paths.get(asset) if 'definition' not in recipe else None,
        })

    blockers = {}
    omissions = []
    if not level['scene_bundle']:
        blockers['source'] = level.get('diagnostic', 'Missing scene bundle')
    else:
        try:
            _, omissions = kevin2_layout(level, profile)
        except ImportErrorDetail as exc:
            blockers['layout'] = str(exc)
    if not recipes or any(item['target'] is None for item in recipes):
        blockers['recipes'] = 'The current AYCE donor does not cover every source order'
    try:
        make_rules(level)
    except ImportErrorDetail as exc:
        blockers['rules'] = str(exc)
    if 'source' in blockers:
        status = 'scene-unavailable'
    elif blockers:
        status = 'requires-adaptation'
    else:
        status = 'preview-exportable'

    return {
        'scene': level['sceneName'],
        'name': level.get('levelNameZH') or level.get('levelName'),
        'type': level.get('levelType'),
        'status': status,
        'blockers': blockers,
        'stations': dict(Counter(item['role'] for item in level['stations'] if item['active'])),
        'placements': len(layout.get('placements', [])),
        'boxColliders': len(layout.get('boxColliders', [])),
        'layoutDiagnostics': layout.get('unresolved', []),
        'mechanics': level['mechanics'],
        'omittedFromPreview': omissions,
        'recipes': recipes,
    }


def export_preview(directory, report_path, archive, level_set, level, compile_preview):
    manifest = compile_preview(archive, level_set, level)
    identity = json.dumps([archive, level_set.get('uid'), level_set.get('version'), level['sceneName']])
    suffix = sha256(identity.encode('utf-8')).hexdigest()[:12]
    name = re.sub(r'[^a-zA-Z0-9_]+', '_', level['sceneName'])[:50]
    destination = directory / f'{name}-{suffix}.json'
    if destination.resolve() == report_path.resolve():
        raise ImportErrorDetail('Preview path collides with the inventory report')
    contents = json.dumps(manifest, ensure_ascii=False, indent=2, allow_nan=False) + '\n'
    try:
        with destination.open('x', encoding='utf-8', newline='\n') as stream:
            stream.write(contents)
    except FileExistsError:
        if destination.read_text(encoding='utf-8') != contents:
            raise ImportErrorDetail(f'Existing preview differs: {destination.name}') from None
    return destination.name


def inspect_corpus(inputs, output, inspect_archive, profile, recipe_assets, make_rules, catalog_paths,
                   export_directory, compile_preview):
    archives = set()
    for entry in map(Path, inputs):
        if entry.is_dir():
            archives.update(path.resolve() for path in entry.glob('*') if path.is_file() and path.suffix.lower() == '.zip')
        elif entry.is_file() and entry.suffix.lower() == '.zip':
            archives.add(entry.resolve())
        else:
            raise ImportErrorDetail(f'Expected an existing ZIP or directory: {entry}')
    if not archives:
        raise ImportErrorDetail('No ZIP files found')
    if Path(output).resolve() in archives:
        raise ImportErrorDetail('Inventory output must not overwrite an input ZIP')
    previews = Path(export_directory) if export_directory else None
    if previews is not None:
        previews.mkdir(parents=True, exist_ok=True)

    results, level_counts = [], Counter()
    for archive in sorted(archives):
        row = {'file': archive.name, 'status': 'inspected', 'sets': []}
        try:
            report = inspect_archive(archive)
            row['ignoredExecutables'] = report['ignored_executables']
            row['unreadableSerializedObjects'] = report['unreadable_serialized_objects']
            for level_set in report['sets']:
                levels = []
                for level in level_set['levels']:
                    assessment = assess_level(level, profile, recipe_assets, make_rules, catalog_paths)
                    if previews is not None and assessment['status'] == 'preview-exportable':
                        try:
                            assessment['previewFile'] = export_preview(previews, Path(output), archive.name,
                                                                       level_set, level, compile_preview)
                        except INSPECTION_ERRORS as exc:
                            assessment['previewExportError'] = str(exc)
                    levels.append(assessment)
                level_counts.update(level['status'] for level in levels)
                row['sets'].append({**level_set, 'levels': levels})
        except INSPECTION_ERRORS as exc:
            row.update(status='inspection-failed', error=f'{type(exc).__name__}: {exc}')
        results.append(row)
    return {
        'format': 'ayce-oc2-corpus/1',
        'ayceBuild': profile['build'],
        'donorScene': profile['scene'],
        'ayceCatalogRecipeCount': len(catalog_paths),
        'archiveCounts': dict(Counter(row['status'] for row in results)),
        'levelCounts': dict(level_counts),
        'archives': results,
    }
