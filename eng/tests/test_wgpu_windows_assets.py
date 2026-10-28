"""Execute real MSBuild asset targets over synthetic files; never execute native bytes."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[2]
FILES = ('wgpu_native.dll', 'wgpu-native-build.json', 'LICENSE.MIT', 'LICENSE.APACHE',
         'wgpu-LICENSE.MIT', 'wgpu-LICENSE.APACHE', 'webgpu-headers-LICENSE', 'wgpu-native-windows.json')
LISTS = ('NativeCopyLocalItems', 'RuntimeTargetsCopyLocalItems', '_ResolvedCopyLocalPublishAssets')

class WindowsAssetSelectionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.default = self.root / 'default'
        self.optional = self.root / 'optional/buildTransitive'
        self.build = self.default / 'buildTransitive'
        self.build.mkdir(parents=True)
        self.optional.mkdir(parents=True)
        shutil.copyfile(ROOT / 'src/ProGPU.Backend/buildTransitive/ProGPU.Backend.Windows.targets', self.build / 'ProGPU.Backend.Windows.targets')
        shutil.copyfile(ROOT / 'src/ProGPU.Backend/buildTransitive/ProGPU.Backend.targets', self.build / 'backend.targets')
        shutil.copyfile(ROOT / 'src/ProGPU.Backend.Dx12/buildTransitive/ProGPU.Backend.Dx12.targets', self.optional / 'dx12.targets')
        for rid in ('win-x64', 'win-arm64'):
            for root in (self.default / 'runtimes', self.optional / 'runtimes'):
                directory = root / rid / 'native'
                directory.mkdir(parents=True)
                for name in FILES:
                    (directory / name).write_bytes((rid + ':' + name).encode())
            (self.optional / 'runtimes' / rid / 'native/progpu-dx12-runtime.json').write_text('{}')
            for name in ('dxcompiler.dll', 'dxil.dll'):
                (self.optional / 'runtimes' / rid / 'native' / name).write_bytes(b'compiler fixture')

    def run_targets(self, rid='win-x64', source=False, optional=False, reverse=False, missing=False, directory_import=False, default_assets=True, targets=None):
        source_root = str(self.default / 'runtimes') if source else ''
        if missing:
            (self.default / 'runtimes/win-x64/native/wgpu-native-windows.json').unlink()
        props = f'<RuntimeIdentifier>{rid}</RuntimeIdentifier><OutputType>Exe</OutputType><ProGpuWgpuWindowsSourceRuntimeRoot>{source_root}</ProGpuWgpuWindowsSourceRuntimeRoot>'
        items = []
        for name in LISTS:
            for arch in ('win-x64', 'win-arm64'):
                for package, root, names in (('Silk.NET.WebGPU.Native.WGPU', self.root / 'silk/runtimes', ('wgpu_native.dll',)), ('ProGPU.Backend', self.default / 'runtimes', FILES)):
                    if package == 'ProGPU.Backend' and not default_assets:
                        continue
                    for file in names:
                        path = root / arch / 'native' / file
                        items.append(f'<{name} Include="{path}" NuGetPackageId="{package}" />')
            # Same basename in arbitrary caller/library locations is never ours.
            items.append(f'<{name} Include="{self.root}/caller/runtimes/win-x64/native/wgpu_native.dll" NuGetPackageId="Caller.Library" />')
            items.append(f'<{name} Include="{self.root}/silk/runtimes/linux-x64/native/libwgpu_native.so" NuGetPackageId="Silk.NET.WebGPU.Native.WGPU" />')
        if directory_import:
            props += '<ProGpuWgpuWindowsRuntimeRoot>' + str(self.default / 'runtimes') + '</ProGpuWgpuWindowsRuntimeRoot>'
        imports = [ROOT / 'Directory.Build.targets' if directory_import else self.build / 'backend.targets']
        if optional: imports.append(self.optional / 'dx12.targets')
        if reverse: imports.reverse()
        project = '<Project><PropertyGroup>' + props + '</PropertyGroup><ItemGroup>' + ''.join(items) + '</ItemGroup>'
        project += ''.join(f'<Import Project="{escape(str(p))}" />' for p in imports)
        project += '<Target Name="ResolvePackageAssets" /><Target Name="_ResolveCopyLocalAssetsForPublish" /><Target Name="AssignTargetPaths" /></Project>'
        path = self.root / 'test.proj'
        path.write_text(project)
        command = [os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet'), 'msbuild', str(path), '-nologo',
                   '-target:' + (targets or 'ResolvePackageAssets;_ResolveCopyLocalAssetsForPublish;AssignTargetPaths;ValidateProGpuWgpuWindowsSourceSelection'),
                   '-getItem:' + ','.join(LISTS + ('Content', 'NativeFileReference'))]
        result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        if result.returncode: return result, None
        output = json.loads(result.stdout[result.stdout.index('{'):])['Items']
        for values in output.values():
            for item in values:
                item['Identity'] = item['Identity'].replace('\\', '/')
        return result, output

    def require_success(self, **kwargs):
        result, items = self.run_targets(**kwargs)
        self.assertEqual(0, result.returncode, result.stdout)
        return items

    def test_default_package_replaces_only_exact_silk_windows_assets(self):
        items = self.require_success()
        for name in LISTS:
            identities = [x['Identity'] for x in items[name]]
            self.assertFalse(any('/silk/runtimes/win-' in p for p in identities))
            self.assertTrue(any('/caller/' in p for p in identities))
            self.assertTrue(any('linux-x64' in p for p in identities))
            self.assertEqual(16, sum('/default/runtimes/' in p for p in identities))

    def test_absent_receipt_preserves_stock_asset(self):
        items = self.require_success(missing=True)
        for name in LISTS:
            self.assertTrue(any('/silk/runtimes/win-x64/' in x['Identity'] for x in items[name]))

    def test_selected_source_requires_receipt(self):
        result, _ = self.run_targets(source=True, missing=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('source runtime is missing', result.stdout)

    def test_source_stages_exact_rid_inventory_without_compilers(self):
        for rid in ('win-x64', 'win-arm64'):
            with self.subTest(rid=rid):
                items = self.require_success(source=True, rid=rid)
                contents = items['Content']
                self.assertEqual(set(FILES), {Path(x['Identity']).name for x in contents})
                self.assertTrue(all('/' + rid + '/' in x['Identity'] for x in contents))
                self.assertTrue(all(x['ExcludeFromSingleFile'] == 'true' for x in contents))

    def test_directory_import_uses_explicit_default_runtime_root_without_xlib(self):
        items = self.require_success(directory_import=True)
        self.assertEqual(set(FILES), {Path(x['Identity']).name for x in items['Content']})

    def test_portable_source_stages_both_rids(self):
        items = self.require_success(source=True, rid='')
        self.assertEqual(16, len(items['Content']))

    def test_non_windows_source_stages_no_windows_content(self):
        items = self.require_success(source=True, rid='linux-x64')
        self.assertEqual([], items['Content'])

    def test_browser_stub_stays_package_only_with_windows_source_import(self):
        source = self.require_success(source=True, rid='browser-wasm')
        package = self.require_success(rid='browser-wasm')
        self.assertEqual([], source['NativeFileReference'])
        self.assertEqual(1, len(package['NativeFileReference']))

    def test_optional_package_preserves_unrelated_and_other_rid(self):
        for rid in ('win-x64', 'win-arm64'):
            with self.subTest(rid=rid):
                items = self.require_success(optional=True, rid=rid)
                for name in LISTS:
                    own = [x for x in items[name] if x.get('NuGetPackageId') == 'ProGPU.Backend' and '/' + rid + '/' in x['Identity']]
                    self.assertEqual(['wgpu-native-windows.json'], [Path(x['Identity']).name for x in own])
                    self.assertTrue(any('/caller/' in x['Identity'] for x in items[name]))
                    self.assertTrue(any('/linux-x64/' in x['Identity'] for x in items[name]))

    def test_optional_source_deduplicates_in_both_import_orders(self):
        for reverse in (False, True):
            with self.subTest(reverse=reverse):
                items = self.require_success(source=True, optional=True, reverse=reverse)
                inherited = [x for x in items['Content'] if x.get('ProGpuWgpuWindowsOwnedAsset') == 'true']
                self.assertEqual(['wgpu-native-windows.json'], [Path(x['Identity']).name for x in inherited])

    def test_optional_source_only_mismatch_rejects_at_content_boundary(self):
        (self.optional / 'runtimes/win-x64/native/wgpu_native.dll').write_bytes(b'different')
        result, _ = self.run_targets(source=True, optional=True, default_assets=False, targets='AssignTargetPaths')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('must have identical bytes', result.stdout)

    def test_optional_publish_only_mismatch_rejects_at_publish_boundary(self):
        (self.optional / 'runtimes/win-x64/native/wgpu_native.dll').write_bytes(b'different')
        result, _ = self.run_targets(optional=True, targets='_ResolveCopyLocalAssetsForPublish')
        self.assertNotEqual(0, result.returncode)
        self.assertIn('must have identical bytes', result.stdout)

    def test_optional_different_dll_rejects_before_asset_removal(self):
        (self.optional / 'runtimes/win-x64/native/wgpu_native.dll').write_bytes(b'different')
        result, _ = self.run_targets(optional=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('must have identical bytes', result.stdout)

class NativePackageInventoryTests(unittest.TestCase):
    def test_default_windows_files_have_one_package_owner(self):
        with tempfile.TemporaryDirectory() as temporary:
            payload = Path(temporary).resolve()
            staged = set()
            independent = ('progpu_native.dll', 'progpu_native_dawn.dll', 'progpu-native-build.json',
                           'native-freetype.json', 'freetype-LICENSE', 'sdk/lib/static.lib', 'sdk/private.dll')
            for rid in ('win-x64', 'win-arm64', 'linux-x64'):
                for name in FILES + independent:
                    path = payload / 'runtimes' / rid / 'native' / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(b'synthetic package inventory only')
                    staged.add(str(path))
            for project in ('ProGPU.Backend.Native', 'ProGPU.Native.Sdk'):
                with self.subTest(project=project):
                    command = [os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet'), 'msbuild',
                        str(ROOT / 'src' / project / (project + '.csproj')), '-nologo',
                        '-p:ProGpuNativeRuntimeRoot=' + str(payload), '-getItem:None,ProjectReference']
                    result = subprocess.run(command, cwd=ROOT, text=True, stdout=subprocess.PIPE,
                        stderr=subprocess.STDOUT, timeout=30)
                    self.assertEqual(0, result.returncode, result.stdout)
                    items = json.loads(result.stdout[result.stdout.index('{'):])['Items']
                    packed = {str(Path(x['Identity']).resolve()) for x in items['None']
                              if x.get('Pack', '').lower() == 'true' and str(payload) in x['Identity']}
                    expected = set(staged)
                    for rid in ('win-x64', 'win-arm64'):
                        expected.difference_update(str(payload / 'runtimes' / rid / 'native' / f) for f in FILES)
                    if project == 'ProGPU.Backend.Native':
                        expected = {p for p in expected if '/sdk/' not in p.replace('\\', '/')}
                        references = [(ROOT / 'src' / project / x['Identity']).resolve() for x in items['ProjectReference']]
                        self.assertIn((ROOT / 'src/ProGPU.Backend/ProGPU.Backend.csproj').resolve(), references)
                        self.assertIn((ROOT / 'src/ProGPU.Native.Sdk/ProGPU.Native.Sdk.csproj').resolve(), references)
                    else:
                        expected = {p for p in expected if not (Path(p).suffix in ('.dll', '.dylib', '.so')
                                                               and Path(p).parent.name == 'native')}
                    self.assertEqual(expected, packed)
            self.assertEqual(staged, {str(p) for p in payload.rglob('*') if p.is_file()})

if __name__ == '__main__':
    unittest.main()
