from collections import Counter

from .assets import ImportErrorDetail


STATIC_BATCH = 'stock-recipes-static-layout'
ADAPTATION_BATCH = 'stock-recipes-layout-or-rules'
BATCHES = (
    'current-preview',
    STATIC_BATCH,
    ADAPTATION_BATCH,
    'custom-recipe-definitions',
    'unmatched-recipe-assets',
    'unresolved-recipe-references',
    'missing-source-scene',
)


def migration_batches(inventory):
    if inventory.get('format') != 'ayce-oc2-corpus/1' or not inventory.get('ayceCatalogRecipeCount'):
        raise ImportErrorDetail('Build an inventory with --ayce-catalog before classifying migration batches')

    groups = {batch: [] for batch in BATCHES}
    unavailable_archives = []
    for archive in inventory['archives']:
        if archive['status'] != 'inspected':
            unavailable_archives.append({'archive': archive['file'], 'error': archive.get('error')})
        for level_set in archive['sets']:
            for level in level_set['levels']:
                batch, details = classify_level(level)
                groups[batch].append({
                    'archive': archive['file'],
                    'setUid': level_set.get('uid'),
                    'setVersion': level_set.get('version'),
                    'scene': level['scene'],
                    'name': level.get('name'),
                    **details,
                })
    return {
        'format': 'ayce-oc2-migration-batches/1',
        'ayceBuild': inventory['ayceBuild'],
        'levelCount': sum(map(len, groups.values())),
        'unavailableArchives': unavailable_archives,
        'unavailableReleases': inventory.get('unavailableReleases', []),
        'batches': [{'id': batch, 'count': len(rows), 'levels': rows} for batch, rows in groups.items()],
    }


def classify_level(level):
    native, custom, unmatched, unresolved = set(), Counter(), set(), set()
    for recipe in level['recipes']:
        if recipe['status'] == 'custom-definition':
            custom[str(recipe['definition'].get('type', 'unknown'))] += 1
        elif recipe['status'] == 'unresolved-reference':
            unresolved.add(recipe.get('ref') or recipe.get('name') or '?')
        elif recipe.get('ayceCatalogPath'):
            native.add(recipe['ayceCatalogPath'])
        else:
            unmatched.add(recipe.get('assetPath') or recipe.get('name') or '?')

    stations = level['stations']
    layout_issues = sorted({issue['reason'] for issue in level['layoutDiagnostics']})
    missing_stations = [role for role in ('spawn', 'dispenser', 'serving') if not stations.get(role)]
    flags = []
    if level['mechanics']:
        flags.append('detected-mechanics')
    if not level['boxColliders']:
        flags.append('no-supported-box-colliders')
    if layout_issues:
        flags.append('layout-diagnostics')
    if missing_stations or level['placements'] <= stations.get('spawn', 0):
        flags.append('incomplete-station-coverage')
    if 'rules' in level['blockers']:
        flags.append('campaign-rules')

    if level['status'] == 'scene-unavailable':
        batch = 'missing-source-scene'
    elif level['status'] == 'preview-exportable':
        batch = 'current-preview'
    elif unresolved or not level['recipes']:
        batch = 'unresolved-recipe-references'
    elif custom:
        batch = 'custom-recipe-definitions'
    elif unmatched:
        batch = 'unmatched-recipe-assets'
    else:
        batch = ADAPTATION_BATCH if flags else STATIC_BATCH

    return batch, {
        'layoutFlags': flags,
        'mechanics': level['mechanics'],
        'missingStationRoles': missing_stations,
        'layoutIssues': layout_issues,
        'stockRecipePaths': sorted(native),
        'customRecipeTypes': dict(sorted(custom.items())),
        'unmatchedRecipeAssets': sorted(unmatched),
        'unresolvedRecipeReferences': sorted(unresolved),
        'orderCount': len(level['recipes']),
    }
