from copy import deepcopy
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

from bridge.assets import ImportErrorDetail, load_bundles, member_name, rotate
from bridge.__main__ import by_path, campaign_rules, make_demo, map_recipes, move, package


CHOP = 'Design/Gameplay Stations/ChoppingBoard_Side 5 (1)'
COUNTER = 'Design/Work Surfaces/countertop_01_standard_edge (23)'


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
