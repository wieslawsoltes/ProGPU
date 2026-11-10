#!/usr/bin/env python3
"""Test the real CMake package resolver with non-runnable layout controls."""

from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent
VERSION = "0.1.0-preview.67"


class NativeSdkCMakeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="progpu-sdk-cmake.")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.runtime = self.root / "packages/progpu.backend.native" / VERSION
        self.sdk = self.root / "packages/progpu.native.sdk" / VERSION
        self.config = self.runtime / "build/native/cmake"
        shutil.copytree(ROOT / "src/ProGPU.Native/cmake", self.config)
        (self.runtime / "build/native/include").mkdir()
        for package in [self.runtime, self.sdk]:
            marker = package / "build/native/ProGPUNativeSdkVersion.txt"
            marker.parent.mkdir(parents=True, exist_ok=True)
            marker.write_text(VERSION + "\n")
        for rid in ["osx-arm64", "osx-x64", "linux-x64", "linux-arm64", "win-x64", "win-arm64"]:
            library = self.sdk / "runtimes" / rid / "native/sdk"
            library.mkdir(parents=True)
            for component in ["compression", "hit_testing", "image", "mil", "geometry", "direct2d_core", "text", "scene_builder"]:
                name = f"progpu_native_{component}.lib" if rid.startswith("win-") else f"libprogpu_native_{component}.a"
                (library / name).write_bytes(b"Packaging-only control; not a native library")
        self.source = self.root / "source"
        self.source.mkdir()
        (self.source / "CMakeLists.txt").write_text('''cmake_minimum_required(VERSION 3.28)
project(ProGpuSdkLayoutControl LANGUAGES CXX)
find_package(ProGPUNative CONFIG REQUIRED)
get_target_property(location ProGPU::native_text IMPORTED_LOCATION)
file(WRITE "${CMAKE_BINARY_DIR}/selected-library.txt" "${location}")
''')

    def configure(self, explicit=None):
        command = ["cmake", "-S", str(self.source), "-B", str(self.root / "build"),
                   "-DProGPUNative_DIR=" + str(self.config)]
        if explicit is not None:
            command.append("-DPROGPU_NATIVE_SDK_ROOT=" + str(explicit))
        return subprocess.run(command, text=True, stdout=subprocess.PIPE,
                              stderr=subprocess.STDOUT, timeout=60)

    def test_normal_nuget_cache_resolves_exact_sdk(self):
        result = self.configure()
        self.assertEqual(result.returncode, 0, result.stdout)
        selected = (self.root / "build/selected-library.txt").read_text()
        self.assertTrue(selected.startswith(str(self.sdk) + "/runtimes/"), selected)

    def test_manual_extraction_resolves_explicit_sdk(self):
        manual = self.root / "manually-extracted-sdk"
        self.sdk.rename(manual)
        result = self.configure(manual)
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertTrue((self.root / "build/selected-library.txt").read_text().startswith(str(manual)))

    def test_different_version_is_rejected_before_targets_are_published(self):
        (self.sdk / "build/native/ProGPUNativeSdkVersion.txt").write_text("0.1.0-preview.66\n")
        result = self.configure()
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertIn("must match exactly", result.stdout)
        self.assertFalse((self.root / "build/selected-library.txt").exists())

    def test_missing_exact_sdk_does_not_admit_another_version(self):
        self.sdk.rename(self.sdk.with_name("0.1.0-preview.68"))
        result = self.configure()
        self.assertNotEqual(result.returncode, 0, result.stdout)
        self.assertFalse((self.root / "build/selected-library.txt").exists())


if __name__ == "__main__":
    unittest.main()
