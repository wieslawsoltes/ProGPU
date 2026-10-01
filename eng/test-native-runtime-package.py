#!/usr/bin/env python3
"""Exercise the real native-runtime Pack item with synthetic, non-runnable files."""

from pathlib import Path
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
            for rid in RIDS:
                for relative in ("licenses/edit-word-icu/LICENSE", "licenses/freetype/LICENSE.TXT",
                                 "sdk/progpu-native-edit-word-dependency.json", "sdk/fixture.a"):
                    name = f"runtimes/{rid}/native/{relative}"
                    data = f"Synthetic packaging-only control: {name}\n".encode()
                    path = runtime / name
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_bytes(data)
                    expected[name] = data

            # Use the unchanged production item, not an imitation of its glob or
            # NuGet's file/directory interpretation. No renderer or dependency is built.
            project = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
            properties = ET.SubElement(project, "PropertyGroup")
            for name, value in {
                "TargetFramework": "net10.0", "PackageId": "ProGPU.Native.Runtime.PackContract",
                "PackageVersion": "1.0.0", "IsPackable": "true", "IncludeBuildOutput": "false",
                "SuppressDependenciesWhenPacking": "true", "EnableDefaultItems": "false",
                "NuGetAudit": "false", "ProGpuNativeRuntimeRoot": str(runtime),
            }.items():
                ET.SubElement(properties, name).text = value
            ET.SubElement(project, "ItemGroup").append(items[0])
            project_path = root / "RuntimePack.csproj"
            ET.ElementTree(project).write(project_path, encoding="utf-8", xml_declaration=True)
            for arguments in (["restore", str(project_path), "--ignore-failed-sources"],
                              ["pack", str(project_path), "--no-build", "--output", str(root / "output")]):
                result = subprocess.run(["dotnet", *arguments], cwd=root, text=True,
                                        stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
                self.assertEqual(result.returncode, 0, result.stdout)
            with zipfile.ZipFile(root / "output/ProGPU.Native.Runtime.PackContract.1.0.0.nupkg") as archive:
                actual = [name for name in archive.namelist() if name.startswith("runtimes/")]
                self.assertCountEqual(actual, expected)
                for name, data in expected.items():
                    self.assertEqual(archive.read(name), data, name)

            # Also exercise the exact production admission target. Missing
            # runtimes must fail before NuGet creates even a local package.
            missing = root / "missing-payload"
            missing.mkdir()
            properties.find("ProGpuNativeRuntimeRoot").text = str(missing)
            target = source.find("./Target[@Name='ValidateProGpuNativePackageRuntimes']")
            self.assertIsNotNone(target)
            project.append(target)
            ET.SubElement(project.find("ItemGroup"), "None", Include=str(project_path),
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
