from struct import unpack_from

from .addressables import catalog_tables, scene_dependencies, string_key
from .assets import Bundles, ImportErrorDetail


MIXING_ROLES = ('mixing_bowl', 'blender_cup')
AUDITED_COOKWARE = ('fryer_basket', *MIXING_ROLES)


def station_ingredients(station):
    if station.get('ingredient'):
        yield station['ingredient']
    if station.get('attachedSpawn'):
        yield from station['attachedSpawn']['items']
    yield from station.get('condimentItems', ())


class StockFoodAudit:
    def __init__(self, tables, catalog, overlays):
        addresses = set()
        appliance_scenes = set()
        for overlay in overlays:
            kitchen = overlay['stockKitchen']
            addresses.update(row['addressKey'].casefold() for row in kitchen['recipes'])
            addresses.update(path.casefold() for row in kitchen['stations'] for path in station_ingredients(row))
            addresses.update(path.casefold() for row in kitchen['stations']
                             if row['role'] in AUDITED_COOKWARE
                             for path in row.get('allowedIngredients', []))
            appliance_scenes.update(row['scene'] for row in kitchen['stations'] if row['role'] in AUDITED_COOKWARE)
        keys, data, buckets, entries = catalog_tables(catalog)
        names = catalog['m_InternalIds']
        dependencies = {f'{scene}_scenes_all.bundle' for scene in appliance_scenes}
        for row in scene_dependencies(catalog, appliance_scenes).values():
            dependencies.update(row['dependencies'])
        for index, _, dep, _, _, _, _ in entries:
            if dep < 0 or names[index].casefold() not in addresses:
                continue
            _, start, size = buckets[dep]
            for other in unpack_from(f'<{size}i', data, start):
                name = string_key(keys, buckets[entries[other][5]][0])
                if name and name.endswith('.bundle'):
                    dependencies.add(name)
        self.bundles = Bundles({name: (tables / 'StandaloneWindows64' / name).read_bytes() for name in dependencies})
        self.assets = {}
        self.inputs = {}
        self.appliances = {}
        for obj in self.bundles.objects.values():
            if obj.type.name != 'AssetBundle':
                continue
            owner = self.bundles.read(obj)
            for path, info in owner.tree.get('m_Container', []):
                key = path.casefold()
                if key in addresses:
                    self.assets[key] = self.reference(owner, info['asset'])

    def reference(self, owner, pointer):
        obj, _ = self.bundles.ref(owner.obj, pointer)
        return self.bundles.read(obj) if obj is not None else None

    def read_asset(self, path):
        if (asset := self.assets.get(path.casefold())) is None:
            raise ImportErrorDetail(f'Native food audit cannot resolve {path}')
        return asset

    def ingredient_states(self, path):
        if path in self.inputs:
            return self.inputs[path]
        current = self.read_asset(path)
        seen, states = set(), set()
        while current is not None and current.key not in seen:
            seen.add(current.key)
            if current.obj.type.name != 'GameObject' and 'm_uID' in current.tree:
                states.add(current.tree['m_uID'])
                break
            next_item = None
            for item in current.tree.get('m_Component', []):
                component = self.reference(current, item['component'])
                if component is None:
                    continue
                if component.script in ('IngredientPropertiesComponent', 'PreparationContainer', 'CookablePreparationContainer'):
                    node = self.reference(component, component.tree.get('m_ingredientOrderNode'))
                    if node is not None:
                        states.add(node.tree['m_uID'])
                elif component.script == 'WorkableItem':
                    next_item = self.reference(component, component.tree.get('m_nextPrefab'))
            current = next_item
        self.inputs[path] = states
        return states

    def recipe_inputs(self, node, leaves, fry, mixes, seen):
        if node.key in seen:
            return
        seen.add(node.key)
        if node.script == 'IngredientOrderNode':
            leaves[node.tree['m_uID']] = node.tree['m_Name']
            return
        step = self.reference(node, node.tree.get('m_cookingStep'))
        mixed_inputs = set()
        for pointer in node.tree.get('m_composition', []):
            child = self.reference(node, pointer)
            if child is None:
                raise ImportErrorDetail(f"Missing required recipe input for {node.tree['m_Name']}")
            if step is not None and step.tree['m_Name'].casefold().endswith('deepfatfryer'):
                fry[child.tree['m_uID']] = child.tree['m_Name']
            if node.script == 'MixedCompositeOrderNode':
                mixed_inputs.add(child.tree['m_uID'])
            self.recipe_inputs(child, leaves, fry, mixes, seen)
        if mixed_inputs:
            mixes[node.tree['m_Name']] = mixed_inputs

    def appliance_inputs(self, station):
        key = station['scene'], station['path']
        if key in self.appliances:
            return self.appliances[key]
        is_fryer = station['role'] == 'fryer_basket'
        component = 'CookableContainer' if is_fryer else 'MixableContainer'
        entries = list(self.bundles.entry(key[0] + '_scenes_all.bundle'))
        transforms = {}
        for entry in entries:
            if entry.obj.type.name != 'Transform':
                continue
            owner = self.reference(entry, entry.tree['m_GameObject'])
            if owner is not None:
                transforms[owner.key] = entry

        def path(owner):
            names = []
            while owner is not None and len(names) < 80:
                names.append(owner.tree['m_Name'])
                transform = transforms[owner.key]
                parent = self.reference(transform, transform.tree.get('m_Father'))
                owner = self.reference(parent, parent.tree.get('m_GameObject')) if parent is not None else None
            return '/'.join(reversed(names))

        for entry in entries:
            if entry.script != component:
                continue
            owner = self.reference(entry, entry.tree['m_GameObject'])
            if owner is None or owner.tree['m_Name'] != key[1].rsplit('/', 1)[-1] or path(owner) != key[1]:
                continue
            if is_fryer:
                lookup = self.reference(entry, entry.tree.get('m_approvedContentsList'))
                nodes = [self.reference(lookup, item.get('m_content')) for item in lookup.tree['m_lookupArray']] if lookup else []
            else:
                nodes = [self.reference(entry, item) for item in entry.tree['m_ApprovedIngredients']]
            self.appliances[key] = {node.tree['m_uID'] for node in nodes if node is not None}
            return self.appliances[key]
        raise ImportErrorDetail(f'Cannot audit the cookware donor {key[0]}/{key[1]}')

    def verify(self, overlay):
        kitchen = overlay['stockKitchen']
        supplied, baskets, mixers = set(), set(), []
        for station in kitchen['stations']:
            for path in station_ingredients(station):
                supplied.update(self.ingredient_states(path))
            if station['role'] not in AUDITED_COOKWARE:
                continue
            approvals = set()
            if not station.get('allowedIngredients') or (station['role'] == 'fryer_basket' and station.get('unresolvedIngredients')):
                approvals.update(self.appliance_inputs(station))
            for path in station.get('allowedIngredients', []):
                approvals.update(self.ingredient_states(path))
            if station['role'] == 'fryer_basket':
                baskets.update(approvals)
            else:
                mixers.append(approvals)
        leaves, fry, mixes, seen = {}, {}, {}, set()
        for row in kitchen['recipes']:
            self.recipe_inputs(self.read_asset(row['addressKey']), leaves, fry, mixes, seen)
        missing = sorted(name for uid, name in leaves.items() if uid not in supplied)
        unmatched_fry = sorted(name for uid, name in fry.items() if uid not in baskets)
        unmatched_mix = sorted(name for name, required in mixes.items() if not any(required <= mixer for mixer in mixers))
        return {'ingredientsChecked': len(leaves), 'fryerInputsChecked': len(fry),
                'mixedRecipesChecked': len(mixes), 'missingIngredients': missing,
                'unsupportedFryerInputs': unmatched_fry, 'unsupportedMixerRecipes': unmatched_mix}
