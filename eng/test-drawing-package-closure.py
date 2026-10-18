#!/usr/bin/env python3
"""Check the real drawing package source graph without building or staging runtimes."""

from pathlib import Path
import os
import subprocess
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def drawing_packages():
    result = subprocess.run(
        ["bash", "-c", '''
set -euo pipefail
source "$1/eng/progpu-package-list.sh"
for index in "${!progpu_drawing_runtime_package_ids[@]}"; do
  printf '%s\\t%s\\n' "${progpu_drawing_runtime_package_ids[$index]}" "${progpu_drawing_runtime_package_projects[$index]}"
done
''', "drawing-package-closure", str(ROOT)],
        check=True, text=True, capture_output=True)
    return [tuple(line.split("\t")) for line in result.stdout.splitlines()]


def verify_source_closure(packages):
    seen = set()
    ids = set()
    for package_id, relative_project in packages:
        project = ROOT / relative_project
        for repository, property_name in (("LibreWPF", "LibreWpfSourceRoot"), ("LibreWinForms", "LibreWinFormsSourceRoot")):
            prefix = f"external/{repository}/"
            if relative_project.startswith(prefix) and os.environ.get(property_name):
                project = Path(os.environ[property_name]) / relative_project[len(prefix):]
        project = project.resolve()
        if project in seen or package_id in ids:
            raise ValueError(f"Duplicate drawing package: {package_id}")
        document = ET.parse(project)
        actual_id = document.findtext("./PropertyGroup/PackageId") or project.stem
        if actual_id != package_id:
            raise ValueError(f"Package identity mismatch: {package_id} != {actual_id}")
        # Include conditional source references as well. This group owns the
        # default source closure; it must not omit a potentially shipped package.
        for reference in document.findall("./ItemGroup/ProjectReference"):
            source = reference.attrib["Include"].replace("\\", "/").replace("$(ProGpuSourceRoot)", str(ROOT) + "/")
            if "$(" in source:
                raise ValueError(f"Unresolved drawing source dependency: {source}")
            dependency = (project.parent / source).resolve()
            if dependency not in seen:
                raise ValueError(f"{package_id} dependency must appear earlier: {dependency.stem}")
        seen.add(project)
        ids.add(package_id)


class DrawingPackageClosureTests(unittest.TestCase):
    def test_actual_source_graph_is_complete_and_topologically_ordered(self):
        verify_source_closure(drawing_packages())

    def test_native_backends_are_real_shipping_projects(self):
        packages = dict(drawing_packages())
        for name in ("Dawn", "Native"):
            package_id = f"ProGPU.Backend.{name}"
            self.assertEqual(packages[package_id], f"src/{package_id}/{package_id}.csproj")

    def test_native_scene_bridge_is_a_real_shipping_project(self):
        packages = dict(drawing_packages())
        self.assertEqual(packages["ProGPU.Scene.Native"],
                         "src/ProGPU.Scene.Native/ProGPU.Scene.Native.csproj")

    def test_native_scene_before_managed_scene_is_rejected(self):
        packages = drawing_packages()
        bridge = next(index for index, entry in enumerate(packages) if entry[0] == "ProGPU.Scene.Native")
        bridge_package = packages.pop(bridge)
        scene = next(index for index, entry in enumerate(packages) if entry[0] == "ProGPU.Scene")
        packages.insert(scene, bridge_package)
        with self.assertRaisesRegex(ValueError, "ProGPU.Scene.Native.*ProGPU.Scene"):
            verify_source_closure(packages)

    def test_missing_native_dependency_is_rejected(self):
        packages = [entry for entry in drawing_packages() if entry[0] != "ProGPU.Backend.Native"]
        with self.assertRaisesRegex(ValueError, "ProGPU.Scene.Native.*ProGPU.Backend.Native"):
            verify_source_closure(packages)

    def test_missing_static_sdk_dependency_is_rejected(self):
        packages = [entry for entry in drawing_packages() if entry[0] != "ProGPU.Native.Sdk"]
        with self.assertRaisesRegex(ValueError, "ProGPU.Backend.Native.*ProGPU.Native.Sdk"):
            verify_source_closure(packages)

    def test_missing_dawn_dependency_is_rejected(self):
        packages = [entry for entry in drawing_packages() if entry[0] != "ProGPU.Backend.Dawn"]
        with self.assertRaisesRegex(ValueError, "ProGPU.Backend.Native.*ProGPU.Backend.Dawn"):
            verify_source_closure(packages)

    def test_native_before_dawn_is_rejected(self):
        packages = drawing_packages()
        native = next(index for index, entry in enumerate(packages) if entry[0] == "ProGPU.Backend.Native")
        dawn = next(index for index, entry in enumerate(packages) if entry[0] == "ProGPU.Backend.Dawn")
        dawn_package = packages.pop(dawn)
        native = next(index for index, entry in enumerate(packages) if entry[0] == "ProGPU.Backend.Native")
        packages.insert(native + 1, dawn_package)
        with self.assertRaisesRegex(ValueError, "ProGPU.Backend.Native.*ProGPU.Backend.Dawn"):
            verify_source_closure(packages)

    def test_wrong_package_identity_is_rejected(self):
        packages = drawing_packages()
        packages[0] = ("ProGPU.WrongBackend", packages[0][1])
        with self.assertRaisesRegex(ValueError, "Package identity mismatch"):
            verify_source_closure(packages)

    def test_duplicate_project_is_rejected(self):
        packages = drawing_packages()
        with self.assertRaisesRegex(ValueError, "Duplicate drawing package"):
            verify_source_closure(packages + packages[:1])


if __name__ == "__main__":
    unittest.main()
