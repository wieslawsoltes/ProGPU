#!/usr/bin/env python3
"""Synthetic parser/integrity tests, not Android binary or emulator qualification."""

import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile
import zlib


SPEC = importlib.util.spec_from_file_location("android_evidence", Path(__file__).resolve().parents[1] / "progpu-verify-android-evidence.py")
EVIDENCE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(EVIDENCE)


def log(message, pid=123, level="I", tag="ProGPU.Android"):
    return f"09-26 10:15:30.123  {pid}  {pid} {level} {tag}: {message}\n"


FRAME = ("First frame: adapter='Android Emulator Vulkan', backend=Vulkan, format=Bgra8Unorm, "
         "physical=1080x1920, scale=2.750, surface=0.001ms, compositor=4.000ms, "
         "present=0.005ms, total=5.000ms, draws=21, vectors=700, text=300, content=392.7x680.0.")


def synthetic_elf(machine=62, elf_class=2, needed="libc.so"):
    # Synthetic headers/dynamic strings are never offered to an Android loader.
    strings = b"\0libwgpu_native.so\0" + needed.encode("ascii") + b"\0"
    header = bytearray(256 + len(strings))
    header[:7] = b"\x7fELF" + bytes([elf_class, 1, 1])
    struct.pack_into("<HH", header, 16, 3, machine)
    struct.pack_into("<Q", header, 32, 64)
    struct.pack_into("<HH", header, 54, 56, 2)
    struct.pack_into("<IIQQQQQQ", header, 64, 1, 4, 0, 0, 0, len(header), len(header), 16384)
    struct.pack_into("<IIQQQQQQ", header, 120, 2, 4, 176, 176, 176, 80, 80, 8)
    for index, entry in enumerate(((5, 256), (10, len(strings)), (14, 1), (1, len(b"\0libwgpu_native.so\0")), (0, 0))):
        struct.pack_into("<qQ", header, 176 + index * 16, *entry)
    header[256:] = strings
    return bytes(header)


def png(width=2, height=2):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress((b"\0" + b"\x20\x30\x40\xff" * width) * height)) + chunk(b"IEND", b""))


class AndroidEvidenceTests(unittest.TestCase):
    def test_exact_vulkan_frame(self):
        result = EVIDENCE.first_frame(log(FRAME), 123)
        self.assertEqual([1080, 1920], result["physical"])
        self.assertEqual(21, result["draws"])

    def test_wrong_pid_and_tag_are_not_frame_evidence(self):
        self.assertIsNone(EVIDENCE.first_frame(log(FRAME, pid=456) + log(FRAME, tag="Unrelated"), 123))

    def test_empty_log_is_pending(self):
        self.assertIsNone(EVIDENCE.first_frame("", 123))

    def test_non_vulkan_or_empty_frame_rejected(self):
        for original, replacement in [("backend=Vulkan", "backend=OpenGLES"), ("draws=21", "draws=0"),
                                      ("physical=1080", "physical=0"), ("content=392.7", "content=0.0"),
                                      ("scale=2.750", "scale=NaN"), ("vectors=700, text=300", "vectors=0, text=0")]:
            with self.subTest(replacement=replacement), self.assertRaises(ValueError):
                EVIDENCE.first_frame(log(FRAME.replace(original, replacement)), 123)

    def test_source_error_after_frame_is_rejected(self):
        with self.assertRaises(ValueError):
            EVIDENCE.first_frame(log(FRAME) + log("Application failed while resuming: failure", level="E"), 123)

    def test_unrelated_process_error_is_ignored(self):
        self.assertIsNotNone(EVIDENCE.first_frame(log("FATAL EXCEPTION", pid=456, level="E") + log(FRAME), 123))

    def test_app_fatal_and_duplicate_frame_rejected(self):
        for contents in (log(FRAME) * 2, log("Fatal signal 11", tag="libc"), log("FATAL EXCEPTION", tag="AndroidRuntime", level="E")):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.first_frame(contents, 123)

    def test_png_structure_and_pixels(self):
        result = EVIDENCE.verify_png(png())
        self.assertEqual((2, 2), (result["width"], result["height"]))

    def test_truncated_corrupt_and_error_text_screenshots_rejected(self):
        valid = png()
        for contents in (valid[:-1], valid[:40] + b"x" + valid[41:], b"adb: device offline"):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.verify_png(contents)

    def test_wrong_elf_abi_rejected(self):
        for contents in (synthetic_elf(machine=183), synthetic_elf(elf_class=1), b"not a binary"):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.elf_x64(contents, "test.so")

    def test_desktop_needed_library_and_truncated_segments_rejected(self):
        unterminated = bytearray(synthetic_elf())
        struct.pack_into("<qQ", unterminated, 240, 1, 19)
        for contents in (synthetic_elf(needed="libc.so.6"), synthetic_elf(needed=""), synthetic_elf()[:-1], unterminated):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.elf_x64(contents, "test.so")

    def make_provider(self, root):
        provider = root / "wgpu"
        (provider / "lib/x86_64").mkdir(parents=True)
        (provider / "lib/x86_64/libwgpu_native.so").write_bytes(synthetic_elf())
        (provider / "BUILD-MANIFEST.txt").write_text(f"wgpu-native-commit={EVIDENCE.WGPU_COMMIT}\nsilk-net-webgpu-abi=2.23.0\nandroid-abis=x86_64\nruntime-backend=vulkan\n")
        self.write_hashes(provider)
        return provider

    def write_hashes(self, provider):
        (provider / "SHA256SUMS").write_text("".join(f"{EVIDENCE.sha256((provider / name).read_bytes())}  {name}\n" for name in ("BUILD-MANIFEST.txt", "lib/x86_64/libwgpu_native.so")))

    def write_apk(self, path, extras=None, provider=None):
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("lib/x86_64/libwgpu_native.so", synthetic_elf() if provider is None else provider)
            archive.writestr("lib/x86_64/libmonodroid.so", synthetic_elf())
            archive.writestr("assemblies/assemblies.blob", b"synthetic archive entry")
            for name, data in (extras or {}).items():
                archive.writestr(name, data)

    def test_matching_apk_provider_and_normal_android_runtime(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            provider = self.make_provider(root)
            self.write_apk(root / "sample.apk")
            result = EVIDENCE.verify_apk(root / "sample.apk", provider)
            self.assertEqual(2, len(result["native_libraries"]))

    def test_different_provider_bytes_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            provider = self.make_provider(root)
            self.write_apk(root / "sample.apk", provider=synthetic_elf() + b"different")
            with self.assertRaisesRegex(ValueError, "differs"):
                EVIDENCE.verify_apk(root / "sample.apk", provider)

    def test_wrong_native_abi_desktop_location_and_dawn_rejected(self):
        for name in ("lib/arm64-v8a/libother.so", "runtimes/linux-x64/native/libother.so", "lib/x86_64/libwebgpu_dawn.so", "lib/x86_64/libprogpu_native_dawn.so"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                provider = self.make_provider(root)
                self.write_apk(root / "sample.apk", {name: synthetic_elf()})
                with self.assertRaises(ValueError):
                    EVIDENCE.verify_apk(root / "sample.apk", provider)

    def test_tampered_native_provenance_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            provider = self.make_provider(root)
            self.write_apk(root / "sample.apk")
            manifest = provider / "BUILD-MANIFEST.txt"
            manifest.write_text(manifest.read_text().replace(EVIDENCE.WGPU_COMMIT, "wrong"))
            with self.assertRaises(ValueError):
                EVIDENCE.verify_apk(root / "sample.apk", provider)

    def test_signed_apk_relative_msbuild_path_uses_project_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "bin").mkdir()
            (root / "bin/sample-Signed.apk").write_bytes(b"signed fixture")
            for path in ("bin/sample-Signed.apk", "bin\\sample-Signed.apk"):
                with self.subTest(path=path):
                    output = "Build output\n" + json.dumps({"Properties": {"ApkFileSigned": path}, "Items": {}})
                    EVIDENCE.stage_apk(output, root / "staged.apk", root)
                    self.assertEqual(b"signed fixture", (root / "staged.apk").read_bytes())

    def test_missing_or_ambiguous_apk_metadata_rejected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for output in ("Build failed", '{"Properties":{"ApkFileSigned":"missing.apk"}}', '{"Properties":{"ApkFileSigned":"one.apk"}}\n{"Properties":{"ApkFileSigned":"two.apk"}}'):
                with self.subTest(output=output), self.assertRaises(ValueError):
                    EVIDENCE.stage_apk(output, root / "staged.apk", root)


if __name__ == "__main__":
    unittest.main()
