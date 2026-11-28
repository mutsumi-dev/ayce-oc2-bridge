from copy import deepcopy
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

from bridge.assets import ImportErrorDetail, load_bundles, member_name, rotate
from bridge.__main__ import by_path, campaign_rules, make_demo, map_recipes, move, package
from bridge.static_layout import prepare_static_layout
from bridge.stock_batch import LEGACY_CARROT, OC2_CARROT, ROAST_CARROT, dispenser_prefab


CHOP = 'Design/Gameplay Stations/ChoppingBoard_Side 5 (1)'
COUNTER = 'Design/Work Surfaces/countertop_01_standard_edge (23)'


class StockIngredientTests(unittest.TestCase):
    def test_roast_menu_uses_the_roasting_carrot(self):
        assets = {ROAST_CARROT: 'native roast carrot', LEGACY_CARROT: 'existing legacy carrot'}
        roast = {'addressKey': 'Assets/DownloadableContent/DLC07/DLC_Assets/Data/OrderDefinitions/RecipeItems/BeefPotatoCarrotRoast.asset'}
        cake = {'addressKey': 'Assets/Data/OrderDefinitions/RecipeItems/Desserts/Cake_Carrot.asset'}
        self.assertEqual(dispenser_prefab(OC2_CARROT, assets, [roast]), 'native roast carrot')
        self.assertEqual(dispenser_prefab(OC2_CARROT, assets, [cake]), 'existing legacy carrot')


def station(path, position, role):
    return {'path': path, 'position': position, 'role': role, 'rotation': [0, 0, 0, 1],
            'active': True, 'scaled_parent': False}


class BundleBoundaryTests(unittest.TestCase):
    def test_refuses_zip_parent_and_absolute_paths(self):
        for name in ('../info_bad', '/s_bad', 'C:/info_bad', 'nested\\..\\s_bad'):
            with self.subTest(name=name), self.assertRaises(ImportErrorDetail):
                member_name(name)

    def test_zip_ignores_oc2_plugin(self):
        with tempfile.TemporaryDirectory() as temp:
            original = Path(temp) / 'map.zip'
            with ZipFile(original, 'w') as archive:
                archive.writestr('BepInEx/plugins/Source.dll', b'MZignored')
                archive.writestr('levels/s_sample', b'UnityFS\0bundle')
            files, ignored = load_bundles(original)
            self.assertEqual(list(files), ['s_sample'])
            self.assertEqual(ignored, ['BepInEx/plugins/Source.dll'])

    def test_world_rotation(self):
        self.assertEqual(rotate([0, 1, 0, 0], [2, 0, 3]), [-2, 0, -3])


class OverlayTests(unittest.TestCase):
    def setUp(self):
        self.a = station(CHOP, [9.6, 0, -7.2], 'chopping')
        self.b = station(COUNTER, [9.6, 0, 1.2], 'counter')
        self.profile = {'scene': 's_sushi_1_1', 'build': '22894075', 'gameSha256': 'dummy', 'stations': [self.a, self.b]}

    def test_swaps_stations_without_altering_profile(self):
        old = deepcopy(self.profile)
        demo = make_demo(self.profile)
        self.assertEqual(demo['moves'][0]['position'], self.b['position'])
        self.assertEqual(demo['moves'][1]['position'], self.a['position'])
        self.assertEqual(old, self.profile)

    def test_refuses_occupied_destination(self):
        with self.assertRaisesRegex(ImportErrorDetail, 'Destination is occupied'):
            package(self.profile, 'invalid', [move(self.a, self.b['position'], self.b['rotation'])])

    def test_refuses_ambiguous_station(self):
        with self.assertRaisesRegex(ImportErrorDetail, 'ambiguous'):
            by_path([self.a, self.a], CHOP)

    def test_kevin_recipe_overlay_accepts_zero_moves(self):
        self.profile['scene'] = 's_city_h2'
        manifest = package(self.profile, 'Steamed', [], ['SteamedSpecial_Meat'])
        self.assertEqual(manifest['recipeAllowlist'], ['SteamedSpecial_Meat'])
        with self.assertRaisesRegex(ImportErrorDetail, 'recipe overlays only'):
            package(self.profile, 'Invalid', [move(self.a, self.b['position'], self.b['rotation'])])

    def test_recipe_assets_match_without_renaming_unrelated_entries(self):
        self.profile['scene'] = 's_city_h2'
        steamed = {'name': 'Steamed_Meat_SO', 'assetPath': r'Assets\Data\OrderDefinitions\RecipeItems\SteamedSpecials\SteamedSpecial_Meat.asset'}
        custom = {**steamed, 'name': 'Custom_Dumpling', 'definition': {'score': 44}}
        missing = {'ref': 'cab-reference:123'}
        result = map_recipes({'recipes': [steamed, custom, missing]}, self.profile, {'matchRecipes': True})
        self.assertEqual(result, {'Steamed_Meat_SO': 'SteamedSpecial_Meat'})
        self.assertEqual(map_recipes({'recipes': [steamed]}, self.profile, {}), {})
        self.profile['scene'] = 's_sushi_1_1'
        with self.assertRaisesRegex(ImportErrorDetail, 'No source recipe asset matches'):
            map_recipes({'recipes': [steamed]}, self.profile, {'matchRecipes': True})


class StaticLayoutTests(unittest.TestCase):
    def test_preserves_water_gun_as_playable_equipment(self):
        recipe_path = 'Assets/Data/OrderDefinitions/RecipeItems/Pancake.asset'
        prefab_path = 'Assets/DownloadableContent/DLC02/utensil_water_gun_01.prefab'
        placements = [
            {'role': role, 'path': 'Design/' + role, 'prefab': {'assetPath': 'Assets/' + role + '.prefab'}}
            for role in ('spawn', 'dispenser', 'serving')
        ]
        placements.append({'role': None, 'path': 'Design/Utensils/WaterGun', 'stub': 'PseudoPrefabStub',
                           'prefab': {'assetPath': prefab_path}})
        level = {
            'scene_bundle': 'level.bundle', 'mechanics': [],
            'recipes': [{'name': 'Pancake', 'assetPath': recipe_path, 'score': 60}],
            'layout': {'placements': placements, 'boxColliders': [{}], 'unresolved': []},
        }
        result = prepare_static_layout(level, {recipe_path.casefold(): {'addressKey': recipe_path}}, lambda _: {})
        self.assertEqual(result['otherPlacements'], [])
        self.assertEqual(result['kitchenPlacements'][-1]['role'], 'water_gun')
        self.assertEqual(result['kitchenPlacements'][-1]['prefab']['assetPath'], prefab_path)

    def test_chopping_prefab_overrides_generic_counter_name(self):
        path = 'assets/prefabs/shared_kitchen/countertop_01_chopping_board_wood_no_edge.prefab'
        placements = [{'role': role, 'path': 'Design/' + role, 'prefab': {'assetPath': 'Assets/' + role + '.prefab'}}
                      for role in ('spawn', 'dispenser', 'serving')]
        placements.append({'role': 'counter', 'path': 'Design/Counters/Counter (11)/Counter',
                           'prefab': {'assetPath': path}})
        level = {'scene_bundle': 'level.bundle', 'mechanics': [],
                 'recipes': [{'name': 'Salad', 'assetPath': 'Assets/Salad.asset', 'score': 20}],
                 'layout': {'placements': placements, 'boxColliders': [{}], 'unresolved': []}}
        result = prepare_static_layout(level, {'assets/salad.asset': {'addressKey': 'Assets/Salad.asset'}}, lambda _: {})
        self.assertEqual(result['kitchenPlacements'][-1]['role'], 'chopping')

    def test_adaptation_preserves_teleportal_links_and_rejects_missing_destinations(self):
        prefab = {'assetPath': 'Assets/Prefabs/Teleportal.prefab'}
        stations = [{'role': role, 'path': 'Design/' + role, 'prefab': prefab}
                    for role in ('spawn', 'dispenser', 'serving')]
        portals = [{'stub': 'PseudoPrefabTeleportalStub', 'path': 'Design/Portal ' + source, 'role': None,
                    'prefab': prefab, 'scale': [1.0, 1.0, 1.0], 'scaled_parent': False, 'portalColor': 8,
                    'portalDoubleSided': 0, 'portalId': source, 'portalExit': target}
                   for source, target in (('a', 'b'), ('b', 'a'))]
        level = {'scene_bundle': 'level.bundle', 'mechanics': ['PseudoPrefabTeleportalStub'],
                 'recipes': [{'name': 'Pancake', 'assetPath': 'Assets/Pancake.asset', 'score': 60}],
                 'layout': {'placements': stations + portals, 'boxColliders': [{'layer': 9}], 'unresolved': []}}
        recipes = {'assets/pancake.asset': {'addressKey': 'Assets/Pancake.asset'}}
        rules = lambda _: {}
        with self.assertRaisesRegex(ImportErrorDetail, 'unsupported mechanics'):
            prepare_static_layout(level, recipes, rules)
        prepared = prepare_static_layout(level, recipes, rules, allow_mechanics=True)
        self.assertEqual({p['portalId']: p['portalExit'] for p in prepared['kitchenPlacements']
                          if p['role'] == 'teleportal'}, {'a': 'b', 'b': 'a'})
        portals[0]['portalExit'] = 'missing'
        with self.assertRaisesRegex(ImportErrorDetail, 'no distinct active destination'):
            prepare_static_layout(level, recipes, rules, allow_mechanics=True)

    def test_condiments_require_direct_switch_link_and_exact_ingredients(self):
        ingredient = 'Assets/DownloadableContent/DLC08/DLC_Assets/Data/OrderDefinitions/Ingredients/'
        placements = [{'role': role, 'path': 'Design/' + role, 'stub': '',
                       'prefab': {'assetPath': 'Assets/' + role + '.prefab'}}
                      for role in ('spawn', 'dispenser', 'serving')]
        placements.extend((
            {'role': None, 'path': 'Design/Button', 'stub': 'PseudoPrefabSwitchStub',
             'prefab': {'assetPath': 'Assets/Switch.prefab'}, 'scale': [1, 1, 1], 'condimentTargets': ['a']},
            {'role': 'dispenser', 'path': 'Design/Condiment', 'stub': 'PseudoPrefabStub',
             'prefab': {'assetPath': 'Assets/DLC08_condiment_dispenser.prefab'}, 'scale': [1, 1, 1],
             'sourceObject': 'a', 'condiments': [{'assetPath': ingredient + name + '.asset'}
                                               for name in ('Ketchup', 'Mustard')]},
        ))
        level = {'scene_bundle': 'test.bundle', 'mechanics': ['PseudoPrefabSwitchStub'],
                 'recipes': [{'name': 'Hotdog', 'assetPath': 'Assets/Hotdog.asset', 'score': 40}],
                 'layout': {'placements': placements, 'boxColliders': [{'layer': 9}], 'unresolved': []}}
        recipes = {'assets/hotdog.asset': {'addressKey': 'Assets/Hotdog.asset'}}
        prepare = lambda: prepare_static_layout(level, recipes, lambda _: {}, allow_mechanics=True)
        rows = prepare()['kitchenPlacements']
        self.assertEqual([row['role'] for row in rows[-2:]], ['condiment_switch', 'condiment_dispenser'])
        placements[-2]['condimentTargets'] = ['other']
        with self.assertRaisesRegex(ImportErrorDetail, 'unsupported mechanics'):
            prepare()


class CampaignRulesTests(unittest.TestCase):
    def setUp(self):
        self.solo = {'roundTime': 270, 'orderLifeTime': 180, 'timeBetweenOrders': 15,
                     'm_OneStarScore': 100, 'm_TwoStarScore': 200, 'm_ThreeStarScore': 300, 'm_FourStarScore': 1600}

    def test_exports_distinct_rules_by_player_count(self):
        group = {**self.solo, 'orderLifeTime': 135, 'timeBetweenOrders': 10, 'm_FourStarScore': 2400}
        rules = campaign_rules({'players': {'1': self.solo, '2': group}})
        self.assertEqual(rules['1'], {'roundSeconds': 270, 'orderSeconds': 180, 'intervalSeconds': 15,
                                      'starPoints': [100, 200, 300, 1600]})
        self.assertEqual(rules['2']['starPoints'][-1], 2400)
        self.assertEqual(rules['2']['intervalSeconds'], 10)

    def test_rejects_bad_timing_or_unordered_star_thresholds(self):
        for field, value in [('roundTime', 0), ('orderLifeTime', True), ('m_ThreeStarScore', 50)]:
            with self.subTest(field=field), self.assertRaises(ImportErrorDetail):
                campaign_rules({'players': {'1': {**self.solo, field: value}}})

    def test_stock_adaptation_preserves_a_four_player_only_variant(self):
        source = {'players': {'1': {**self.solo, 'roundTime': 0}, '4': self.solo}}
        rules = campaign_rules(source, allow_disabled_counts=True)
        self.assertEqual(set(rules), {'4'})
        self.assertEqual(rules['4']['roundSeconds'], 270)
        with self.assertRaises(ImportErrorDetail):
            campaign_rules(source)
        with self.assertRaises(ImportErrorDetail):
            campaign_rules({'players': {'1': {**self.solo, 'roundTime': 0}}}, allow_disabled_counts=True)
