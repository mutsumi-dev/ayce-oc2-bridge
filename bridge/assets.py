from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from zipfile import ZipFile
import math
import re

import UnityPy


UNITY_SIGNATURES = (b'UnityFS\0', b'UnityRaw\0', b'UnityWeb\0')
MAX_MEMBER = 96 * 1024 * 1024
MAX_TOTAL = 384 * 1024 * 1024
MAX_ENTRIES = 2048
EXECUTABLE = {'.dll', '.exe', '.bat', '.cmd', '.ps1', '.js', '.vbs', '.scr', '.lnk', '.msi'}
ROLE_NAMES = {
    'chopping': r'chopping|chopboard',
    'dispenser': r'dispenser',
    'plate_stack': r'cleanplatestack|plate.?stack|plate_station',
    'plate_return': r'plate.?return',
    'serving': r'serving|service.?hatch|delivery',
    'sink': r'(^|[^a-z])sink',
    'oven': r'oven',
    'stove': r'cooker|stove|hob',
    'trash': r'(^|[^a-z])bin|trash',
    'counter': r'counter',
}
OBJECT_TYPES = {'GameObject', 'Transform', 'RectTransform'}


class ImportErrorDetail(ValueError):
    pass


def member_name(name):
    name = name.replace('\\', '/')
    path = PurePosixPath(name)
    if path.is_absolute() or ':' in name or '..' in path.parts or not path.name:
        raise ImportErrorDetail(f'Unsafe archive path: {name!r}')
    return path


def load_bundles(path, required=True):
    files, ignored, seen = {}, [], set()
    path = Path(path)

    def add(name, size, read):
        nonlocal total
        leaf = member_name(name)
        if leaf.suffix.lower() in EXECUTABLE:
            ignored.append(str(leaf))
            return
        candidate = leaf.name.lower().startswith(('info_', 's_', 'common')) or leaf.suffix.lower() in ('', '.bundle', '.unity3d')
        if not candidate:
            return
        with read() as stream:
            header = stream.read(12)
            if not header.startswith(UNITY_SIGNATURES):
                return
            if size > MAX_MEMBER or total + size > MAX_TOTAL:
                raise ImportErrorDetail(f'Asset bundle exceeds the import size limit: {leaf}')
            if leaf.name.lower() in seen:
                raise ImportErrorDetail(f'Duplicate asset-bundle filename: {leaf.name}')
            seen.add(leaf.name.lower())
            total += size
            files[leaf.name] = header + stream.read(MAX_MEMBER + 1)
            if len(files[leaf.name]) != size:
                raise ImportErrorDetail(f'Unexpected file size for {leaf}')

    total = 0
    if path.is_dir():
        for index, p in enumerate(path.rglob('*'), 1):
            if index > MAX_ENTRIES:
                raise ImportErrorDetail('Too many files or directories in input')
            if p.is_symlink() or getattr(p, 'is_junction', lambda: False)() or not p.is_file():
                continue
            add(p.relative_to(path).as_posix(), p.stat().st_size, lambda: p.open('rb'))
    elif path.suffix.lower() == '.zip':
        with ZipFile(path) as archive:
            entries = archive.infolist()
            if len(entries) > MAX_ENTRIES:
                raise ImportErrorDetail('Too many entries in input ZIP')
            for entry in entries:
                if entry.is_dir():
                    continue
                add(entry.filename, entry.file_size, lambda: archive.open(entry))
    else:
        add(path.name, path.stat().st_size, lambda: path.open('rb'))
    if required and not files:
        raise ImportErrorDetail(f'No Unity AssetBundles found in {path.name}')
    return files, ignored


@dataclass(frozen=True)
class Asset:
    obj: object
    tree: dict
    script: str | None

    @property
    def key(self):
        return self.obj.assets_file.name, self.obj.path_id


class Bundles:
    def __init__(self, files, dependencies=None):
        self.env = UnityPy.Environment()
        self.objects = {}
        self.cache = {}
        self.unreadable = defaultdict(int)
        for name, data in {**(dependencies or {}), **files}.items():
            self.env.load_file(data, name=name)
        for root in self.env.files.values():
            for serialized in getattr(root, 'files', {}).values():
                for obj in getattr(serialized, 'objects', {}).values():
                    self.objects[(serialized.name.casefold(), obj.path_id)] = obj

    def ref(self, obj, ptr):
        if not ptr or not ptr.get('m_PathID'):
            return None, None
        file_id = ptr.get('m_FileID', 0)
        if file_id < 0 or file_id > len(obj.assets_file.externals):
            raise ImportErrorDetail('Bundle contains an invalid external reference')
        file = obj.assets_file.name
        if file_id:
            ext = obj.assets_file.externals[file_id - 1]
            file = PurePosixPath(ext.path.replace('\\', '/')).name
        key = file.casefold(), ptr['m_PathID']
        return self.objects.get(key), f'{file}:{ptr["m_PathID"]}'

    def read(self, obj):
        key = obj.assets_file.name.casefold(), obj.path_id
        if key in self.cache:
            return self.cache[key]
        tree = self.tree(obj)
        if tree is None:
            self.cache[key] = None
            return None
        if obj.type.name == 'MonoBehaviour':
            raw, _ = self.ref(obj, tree.get('m_Script'))
            script = raw.read().m_ClassName if raw is not None else None
        else:
            script = None
        value = Asset(obj, tree, script)
        self.cache[key] = value
        return value

    def tree(self, obj):
        try:
            return obj.read_typetree()
        except (ValueError, RuntimeError, EOFError, TypeError, IndexError, AttributeError):
            self.unreadable[obj.assets_file.name] += 1
            return None

    def entry(self, name):
        root = self.env.files[name]
        for serialized in root.files.values():
            for obj in getattr(serialized, 'objects', {}).values():
                if obj.type.name in OBJECT_TYPES | {'MonoBehaviour'}:
                    asset = self.read(obj)
                    if asset is not None:
                        yield asset

    def describe_reference(self, asset, ptr):
        obj, key = self.ref(asset.obj, ptr)
        if key is None:
            return None
        result = {'ref': key}
        if obj is None:
            return result
        target = self.read(obj)
        if target and target.tree.get('m_Name'):
            result['name'] = target.tree['m_Name']
        if target and target.script:
            result['type'] = target.script
        if target and target.script in {'PseudoPrefabSO', 'PseudoPrefabSORecipe'}:
            for field in ('prefabName', 'bundleName', 'assetPath'):
                if target.tree.get(field):
                    result[field] = target.tree[field]
        return result

    def recipe(self, level, ptr):
        result = self.describe_reference(level, ptr)
        obj, _ = self.ref(level.obj, ptr)
        target = self.read(obj) if obj is not None else None
        if target is None or not (target.script or '').startswith('CustomRecipe'):
            return result
        result['definition'] = {k: target.tree[k] for k in ('type', 'recipeName', 'uID', 'score', 'cookingProgress', 'mixingProgress') if k in target.tree}
        for name in ('compositionSOs', 'optionalSOs'):
            result['definition'][name] = [self.describe_reference(target, p) for p in target.tree.get(name, []) if p.get('m_PathID')]
        for name in ('platingStepSO', 'modelSO', 'model', 'iconSO', 'icon', 'cookingStepSO', 'cookingStepIconSO'):
            if target.tree.get(name, {}).get('m_PathID'):
                result['definition'][name] = self.describe_reference(target, target.tree[name])
        return result


def xyz(data, fields='xyz'):
    result = [round(float(data[key]), 5) for key in fields]
    if not all(math.isfinite(value) for value in result):
        raise ImportErrorDetail('Non-finite transform in scene')
    return result


def quat_product(a, b):
    x, y, z, w = a
    u, v, t, s = b
    return [w*u+x*s+y*t-z*v, w*v-x*t+y*s+z*u, w*t+x*v-y*u+z*s, w*s-x*u-y*v-z*t]


def rotate(q, v):
    return quat_product(quat_product(q, [*v, 0.0]), [-q[0], -q[1], -q[2], q[3]])[:3]


def classify(name, scripts):
    if 'PseudoPrefabPlayerStub' in scripts or 'PlayerIDProvider' in scripts:
        return 'spawn'
    if not (any(s.startswith('PseudoPrefab') and s.endswith('Stub') for s in scripts) or 'StaticGridLocation' in scripts or 'PlateStation' in scripts):
        return None
    for role, pattern in ROLE_NAMES.items():
        if re.search(pattern, name, re.IGNORECASE):
            return role
    return None


def scene_data(bundles, name):
    entries = list(bundles.entry(name))
    game = {a.key: a for a in entries if a.obj.type.name == 'GameObject'}
    transforms, components = {}, defaultdict(list)
    for asset in entries:
        obj, _ = bundles.ref(asset.obj, asset.tree.get('m_GameObject'))
        if obj is None:
            continue
        if asset.obj.type.name in ('Transform', 'RectTransform'):
            transforms[(obj.assets_file.name, obj.path_id)] = asset
        elif asset.script:
            components[(obj.assets_file.name, obj.path_id)].append(asset)

    def hierarchy(transform, depth=0):
        if depth > 80:
            raise ImportErrorDetail('Invalid transform hierarchy')
        data = transform.tree
        p, q = xyz(data['m_LocalPosition']), xyz(data['m_LocalRotation'], 'xyzw')
        scale = xyz(data['m_LocalScale'])
        go, _ = bundles.ref(transform.obj, data['m_GameObject'])
        label = game[(go.assets_file.name, go.path_id)].tree['m_Name']
        parent, _ = bundles.ref(transform.obj, data.get('m_Father'))
        if parent is None:
            return [label], p, q, scale, False
        parent_asset = bundles.read(parent)
        base, pos, rot, size, scaled = hierarchy(parent_asset, depth + 1)
        delta = rotate(rot, [x*y for x, y in zip(p, size)])
        return [*base, label], [x+y for x, y in zip(pos, delta)], quat_product(rot, q), [x*y for x, y in zip(size, scale)], scaled or any(abs(x-1) > 0.001 for x in size)

    stations = []
    mechanics = set()
    for key, go in game.items():
        scripts = {a.script: a for a in components[key]}
        mechanics.update(s for s in scripts if s.startswith('PseudoPrefab') and s.endswith('Stub') and any(word in s for word in ('Portal', 'Switch', 'Burner', 'Terminal', 'Travelator', 'Conveyor', 'Lockable', 'Spawner', 'AutoWorkstation', 'Pressure')))
        role = classify(go.tree['m_Name'], scripts)
        if role is None or key not in transforms:
            continue
        path, pos, rot, scale, inherited_scale = hierarchy(transforms[key])
        if any('/' in part for part in path):
            continue
        row = {'path': '/'.join(path), 'role': role, 'position': [round(v, 5) for v in pos],
               'rotation': [round(v, 6) for v in rot], 'scale': scale,
               'scaled_parent': inherited_scale, 'active': bool(go.tree['m_IsActive'])}
        if any(part.casefold() == 'animated objects' for part in path[:-1]):
            row['moving_parent'] = True
        if 'PseudoPrefabDispenserStub' in scripts:
            dispenser = scripts['PseudoPrefabDispenserStub']
            row['ingredient'] = bundles.describe_reference(dispenser, dispenser.tree.get('spawnerItemPrefabSO'))
        stations.append(row)
    return {'stations': sorted(stations, key=lambda r: (r['path'], r['position'])), 'mechanics': sorted(mechanics)}
