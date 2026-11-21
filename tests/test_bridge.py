from copy import deepcopy
from pathlib import Path
import tempfile
import unittest
from zipfile import ZipFile

from bridge.assets import ImportErrorDetail, load_bundles, member_name, rotate
from bridge.__main__ import by_path, make_demo, move, package


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
