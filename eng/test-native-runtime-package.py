#!/usr/bin/env python3
"""Exercise the real native-runtime Pack item with synthetic, non-runnable files."""

from pathlib import Path
from copy import deepcopy
import shutil
import subprocess
import tempfile
import unittest
import xml.etree.ElementTree as ET
import zipfile


ROOT = Path(__file__).resolve().parent.parent
RIDS = ("linux-x64", "linux-arm64", "osx-x64", "osx-arm64", "win-x64", "win-arm64")


class NativeRuntimePackageTests(unittest.TestCase):
    maxDiff = None

    def test_webscene_private_feed_covers_the_actual_source_dependency_graph(self):
        source = (ROOT / "eng/progpu-verify-native-webscene-provider.sh").read_text()
        commands = source[source.index("managed_package_projects=("):
                          source.index('NUGET_PACKAGES="${managed_package_cache}" dotnet restore')]
        # Execute the actual packaging commands with a recorder. This checks the
        # private feed's source closure without qualifying a runtime or renderer.
        script = '''set -euo pipefail
repo_root="$1"
managed_packages=/unused
managed_package_version=0.0.0-package-list-control
dotnet() { [[ "$1" == pack ]]; printf '%s\\n' "$2"; }
''' + commands
        result = subprocess.run(["bash", "-c", script, "webscene-feed-control", str(ROOT)],
                                check=True, text=True, capture_output=True)
        projects = [Path(path).resolve() for path in result.stdout.splitlines()]
        self.assertEqual(len(projects), len(set(projects)))
        native = ROOT / "src/ProGPU.Backend.Native/ProGPU.Backend.Native.csproj"
        sdk = ROOT / "src/ProGPU.Native.Sdk/ProGPU.Native.Sdk.csproj"
        self.assertIn(native, projects)

        def verify_closure(feed):
            for project in feed:
                for reference in ET.parse(project).findall("./ItemGroup/ProjectReference"):
                    dependency = (project.parent / reference.attrib["Include"].replace("\\", "/")).resolve()
                    if dependency not in feed:
                        raise ValueError(f"{project.stem}: missing private-feed dependency {dependency.stem}")

        verify_closure(set(projects))
        with self.assertRaisesRegex(ValueError, "ProGPU.Backend.Native.*ProGPU.Native.Sdk"):
            verify_closure(set(projects) - {sdk})

    def test_pack_preserves_exact_paths_and_bytes_including_extensionless_notices(self):
        source = ET.parse(ROOT / "src/ProGPU.Backend.Native/ProGPU.Backend.Native.csproj")
        items = [item for item in source.findall("./ItemGroup/None")
                 if item.get("Include") == "$(ProGpuNativeRuntimeRoot)\\**\\*"]
        self.assertEqual(len(items), 1, "Exercise the single actual production runtime Pack item")
        with tempfile.TemporaryDirectory(prefix="progpu-native-pack-contract.") as directory:
            root = Path(directory)
            shutil.copyfile(ROOT / "global.json", root / "global.json")
            runtime = root / "payload"
            expected = {}
            backend_owned = set()
            for rid in RIDS:
                for relative in ("licenses/edit-word-icu/LICENSE", "licenses/freetype/LICENSE.TXT",
                                 "sdk/progpu-native-edit-word-dependency.json", "sdk/fixture.a",
                                 "libprogpu_native.so", "libprogpu_native.dylib", "progpu_native.dll"):
                    name = f"runtimes/{rid}/native/{relative}"
                    data = f"Synthetic packaging-only control: {name}\n".encode()
                    path = runtime / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                    expected[name] = data

            for rid in ("win-x64", "win-arm64"):
                for relative in ("wgpu_native.dll", "wgpu-native-build.json", "LICENSE.MIT", "LICENSE.APACHE",
                                 "wgpu-LICENSE.MIT", "wgpu-LICENSE.APACHE", "webgpu-headers-LICENSE", "wgpu-native-windows.json"):
                    name = f"runtimes/{rid}/native/{relative}"
                    data = f"Synthetic Backend-owned package control: {name}\n".encode()
                    (runtime / name).write_bytes(data)
                    expected[name] = data
                    backend_owned.add(name)

            # Use the unchanged production item, not an imitation of its glob or
            # NuGet's file/directory interpretation. No renderer or dependency is built.
            project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(project, "PropertyGroup")
            for name, value in {
                "TargetFramework": "net10.0", "PackageId": "ProGPU.Native.Runtime.PackContract",
                "PackageVersion": "1.0.0", "IsPackable": "true", "IncludeBuildOutput": "false",
                "SuppressDependenciesWhenPacking": "false", "EnableDefaultItems": "false",
                "NuGetAudit": "false", "ProGpuNativeRuntimeRoot": str(runtime),
            }.items():
                ET.SubElement(properties, name).text = value
            owned_inventory = source.find("./ItemGroup[_ProGpuBackendOwnedWindowsRid]")
            self.assertIsNotNone(owned_inventory)
            owned_group = deepcopy(owned_inventory)
            project.append(owned_group)
            runtime_group = ET.SubElement(project, "ItemGroup")
            runtime_group.append(items[0])
            sdk_reference = source.find("./ItemGroup/ProjectReference[@Include='..\\ProGPU.Native.Sdk\\ProGPU.Native.Sdk.csproj']")
            self.assertIsNotNone(sdk_reference)
            # Preserve the real project/reference metadata while keeping restore
            # outputs inside this fixture instead of changing the product's obj.
            sdk_source = ROOT / "src/ProGPU.Native.Sdk/ProGPU.Native.Sdk.csproj"
            sdk_copy = ET.parse(sdk_source)
            for imported in sdk_copy.findall("./Import"):
                imported.set("Project", str((sdk_source.parent / imported.get("Project")).resolve()))
            sdk_copy_path = root / "sdk-project/ProGPU.Native.Sdk.csproj"
            sdk_copy_path.parent.mkdir()
            sdk_copy.write(sdk_copy_path, encoding="utf-8", xml_declaration=True)
            sdk_reference.set("Include", str(sdk_copy_path))
            runtime_group.append(sdk_reference)
            project_path = root / "RuntimePack.csproj"
            ET.ElementTree(project).write(project_path, encoding="utf-8", xml_declaration=True)
            for arguments in (["restore", str(project_path), "--ignore-failed-sources"],
                              ["pack", str(project_path), "--no-build", "--output", str(root / "output")]):
                result = subprocess.run(["dotnet", *arguments, "-p:PackageVersion=1.0.0"], cwd=root, text=True,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout)
            with zipfile.ZipFile(root / "output/ProGPU.Native.Runtime.PackContract.1.0.0.nupkg") as archive:
                actual = [name for name in archive.namelist() if name.startswith("runtimes/")]
                runtime_expected = {k: v for k, v in expected.items() if "/native/sdk/" not in k and k not in backend_owned}
                self.assertCountEqual(actual, runtime_expected)
                for name, data in runtime_expected.items():
                    self.assertEqual(archive.read(name), data, name)
                nuspec = ET.fromstring(archive.read("ProGPU.Native.Runtime.PackContract.nuspec"))
                dependencies = nuspec.findall(".//{*}dependency")
                self.assertTrue(any(d.get("id") == "ProGPU.Native.Sdk" and d.get("version") == "1.0.0"
                                    for d in dependencies), "The actual source reference must become a NuGet dependency")

            sdk = ET.parse(ROOT / "src/ProGPU.Native.Sdk/ProGPU.Native.Sdk.csproj")
            sdk_items = [item for item in sdk.findall("./ItemGroup/None")
                         if item.get("Include") == "$(ProGpuNativeRuntimeRoot)\\**\\*"]
            self.assertEqual(len(sdk_items), 1)
            group = runtime_group
            project.remove(owned_group)
            sdk_owned_inventory = sdk.find("./ItemGroup[_ProGpuBackendOwnedWindowsRid]")
            self.assertIsNotNone(sdk_owned_inventory)
            project.insert(1, deepcopy(sdk_owned_inventory))
            group.remove(items[0])
            group.remove(sdk_reference)
            properties.find("SuppressDependenciesWhenPacking").text = "true"
            group.append(sdk_items[0])
            properties.find("PackageId").text = "ProGPU.Native.Sdk.PackContract"
            ET.SubElement(project, "Import", Project=str(ROOT / "eng/ProGPU.Native.SdkVersion.targets"))
            ET.ElementTree(project).write(project_path, encoding="utf-8", xml_declaration=True)
            result = subprocess.run(["dotnet", "pack", str(project_path), "--no-build", "--output",
                                     str(root / "output")], cwd=root, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
            self.assertEqual(result.returncode, 0, result.stdout)
            with zipfile.ZipFile(root / "output/ProGPU.Native.Sdk.PackContract.1.0.0.nupkg") as archive:
                actual = [name for name in archive.namelist() if name.startswith("runtimes/")]
                sdk_expected = {k: v for k, v in expected.items()
                                if "/native/sdk/" in k or "/native/licenses/" in k}
                self.assertCountEqual(actual, sdk_expected)
                for name, data in sdk_expected.items():
                    self.assertEqual(archive.read(name), data, name)
                self.assertEqual(archive.read("build/native/ProGPUNativeSdkVersion.txt").decode().strip(), "1.0.0")
            self.assertEqual(set(runtime_expected) | set(sdk_expected), set(expected) - backend_owned)

            # Also exercise the exact production admission target. Missing
            # runtimes must fail before NuGet creates even a local package.
            missing = root / "missing-payload"
            missing.mkdir()
            properties.find("ProGpuNativeRuntimeRoot").text = str(missing)
            validation = ET.parse(ROOT / "eng/ProGPU.Native.PackageValidation.targets")
            target = validation.find("./Target[@Name='ValidateProGpuNativePackageRuntimes']")
            self.assertIsNotNone(target)
            project.append(target)
            ET.SubElement(runtime_group, "None", Include=str(project_path),
                          Pack="true", PackagePath="contract/RuntimePack.csproj")
            ET.ElementTree(project).write(project_path, encoding="utf-8", xml_declaration=True)
            rejected_output = root / "rejected-output"
            result = subprocess.run(["dotnet", "pack", str(project_path), "--no-build", "--output",
                                     str(rejected_output)], cwd=root, text=True,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("linux-x64 ProGPU native runtime has not been staged", result.stdout)
            self.assertFalse(list(rejected_output.glob("*.nupkg")), result.stdout)


if __name__ == "__main__":
    unittest.main()
