#!/usr/bin/env python3
"""Parser tests with synthetic positives and a real negative; not device qualification."""

import importlib.util
import base64
import json
from pathlib import Path
import struct
import subprocess
import sys
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


def png(width=2, height=2, channels=4, rows=None, filters=(0,), filtered=None):
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    if filtered is None:
        rows = rows if rows is not None else [b"\x20\x30\x40\xff"[:channels] * width] * height
        previous = bytes(width * channels)
        filtered = bytearray()
        for y, row in enumerate(rows):
            kind = filters[y % len(filters)]
            filtered.append(kind)
            for index, value in enumerate(row):
                a = row[index - channels] if index >= channels else 0
                b = previous[index]
                c = previous[index - channels] if index >= channels else 0
                if kind == 4:
                    # Independent test encoder over ORIGINAL, not decoded, bytes.
                    estimate = a + b - c
                    predictor = min((a, b, c), key=lambda candidate: abs(estimate - candidate))
                else:
                    predictor = (0, a, b, (a + b) // 2)[kind]
                filtered.append((value - predictor) % 256)
            previous = row
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6 if channels == 4 else 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(filtered)) + chunk(b"IEND", b""))


def gallery_pixels(width=80, height=100, channels=4):
    # Synthetic contrast coverage, never a substitute for a rendered APK image.
    return [bytes(component for x in range(width)
                  for component in ((30, 40, 50, 255) if (x // 4 + y // 4) % 2 else (210, 220, 230, 255))[:channels])
            for y in range(height)]


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

    def test_typed_gpu_errors_and_device_loss_after_frame_are_rejected(self):
        for message in ("WebGPU error (Validation): invalid command", "WebGPU device lost (Unknown): lost device"):
            with self.subTest(message=message), self.assertRaisesRegex(ValueError, "App error"):
                EVIDENCE.first_frame(log(FRAME) + log(message, level="E"), 123)

    def test_unrelated_process_error_is_ignored(self):
        self.assertIsNotNone(EVIDENCE.first_frame(log("FATAL EXCEPTION", pid=456, level="E") + log(FRAME), 123))

    def test_app_fatal_and_duplicate_frame_rejected(self):
        for contents in (log(FRAME) * 2, log("Fatal signal 11", tag="libc"), log("FATAL EXCEPTION", tag="AndroidRuntime", level="E")):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.first_frame(contents, 123)

    def test_png_structure_and_pixels(self):
        width, height, channels, rows = EVIDENCE.decode_png(png())
        self.assertEqual((2, 2, 4), (width, height, channels))
        self.assertEqual([b"\x20\x30\x40\xff" * 2] * 2, list(rows))

    def test_png_filter_known_bytes_with_wraparound(self):
        expected = [bytes([10, 250, 0, 30, 5, 255]), bytes([8, 240, 5, 250, 4, 3])]
        fixtures = {
            0: ([10, 250, 0, 30, 5, 255], [8, 240, 5, 250, 4, 3]),
            1: ([10, 250, 0, 20, 11, 255], [8, 240, 5, 242, 20, 254]),
            2: ([10, 250, 0, 30, 5, 255], [254, 246, 5, 220, 255, 4]),
            3: ([10, 250, 0, 25, 136, 255], [3, 115, 5, 231, 138, 129]),
            4: ([10, 250, 0, 20, 11, 255], [254, 246, 5, 220, 255, 4]),
        }
        for kind, encoded_rows in fixtures.items():
            with self.subTest(kind=kind):
                data = png(2, 2, channels=3, filtered=b"".join(bytes([kind] + row) for row in encoded_rows))
                self.assertEqual(expected, list(EVIDENCE.decode_png(data)[3]))

    def test_rgb_rgba_all_filters_decode_identical_content(self):
        for channels in (3, 4):
            expected = gallery_pixels(channels=channels)
            for filters in ((0,), (1,), (2,), (3,), (4,), (0, 1, 2, 3, 4)):
                with self.subTest(channels=channels, filters=filters):
                    data = png(80, 100, channels, expected, filters)
                    self.assertEqual(expected, list(EVIDENCE.decode_png(data)[3]))
                    result = EVIDENCE.verify_png(data)
                    self.assertEqual(9, result["content_tiles"])
                    self.assertTrue(result["distributed_content"])
                    self.assertEqual([8, 20, 72, 80], result["interior"])

    def test_invalid_row_filter_rejected_when_decoding(self):
        with self.assertRaisesRegex(ValueError, "row filter"):
            list(EVIDENCE.decode_png(png(filtered=b"\x05" + b"\0" * 8 + b"\0" * 9))[3])

    def test_uniform_near_uniform_and_system_bars_only_rejected(self):
        for variant in ("black", "white", "clear", "noise", "bars", "one-pixel", "one-tile", "two-adjacent", "four-adjacent"):
            rows = []
            for y in range(100):
                row = bytearray()
                for x in range(80):
                    value = 0
                    if variant == "white":
                        value = 255
                    elif variant == "clear":
                        value = 31
                    elif variant == "noise":
                        value = (x + y) % 4
                    elif variant == "bars" and (y < 20 or y >= 80):
                        value = 255 if x % 4 else 64
                    elif variant == "one-pixel" and (x, y) == (20, 30):
                        value = 255
                    elif variant == "one-tile" and 10 <= x < 20 and 22 <= y < 32:
                        value = 255
                    elif variant == "two-adjacent" and 26 <= x < 34 and 28 <= y < 36:
                        value = 255
                    elif variant == "four-adjacent" and 26 <= x < 34 and 36 <= y < 44:
                        value = 255
                    row.extend((value, value, value, 255))
                rows.append(row)
            with self.subTest(variant=variant), self.assertRaisesRegex(ValueError, "interior is blank"):
                EVIDENCE.verify_png(png(80, 100, rows=rows, filters=(0, 1, 2, 3, 4)))

    def test_transparent_color_and_alpha_only_evidence_rejected(self):
        for colorful in (False, True):
            rows = gallery_pixels() if colorful else [b"\x20\x20\x20\xff" * 80] * 100
            rows = [bytearray(row) for row in rows]
            for y, row in enumerate(rows):
                row[3::4] = bytes([0 if y % 2 else 128]) * 80
            with self.subTest(colorful=colorful), self.assertRaisesRegex(ValueError, "not opaque"):
                EVIDENCE.verify_png(png(80, 100, rows=rows, filters=(4,)))

    def test_two_spatially_separated_content_tiles_are_required(self):
        for second in ((54, 24), (12, 64)):
            rows = [bytearray(b"\0\0\0\xff" * 80) for _ in range(100)]
            for left, top in ((12, 24), second):
                for y in range(top, top + 8):
                    for x in range(left, left + 8):
                        rows[y][x * 4:x * 4 + 4] = b"\xff\xff\xff\xff"
            with self.subTest(second=second):
                result = EVIDENCE.verify_png(png(80, 100, rows=rows))
                self.assertEqual(2, result["content_tiles"])
                self.assertTrue(result["distributed_content"])

    def test_real_aosp_black_client_artifact_rejected(self):
        # Exact screencap from PR187 head4c1a43248, run36236752156.
        path = Path(__file__).parent / "fixtures/android-x64-black-client.png.base64"
        data = base64.b64decode(path.read_text(), validate=False)
        self.assertEqual("a7e182491946f15a4f02297b99987cd0d25108d2c887ec45f4a9d82e003f882a", EVIDENCE.sha256(data))
        with self.assertRaisesRegex(ValueError, "0/9 contrasting tiles"):
            EVIDENCE.verify_png(data)

    def test_screenshot_cli_only_blank_valid_focused_images_are_pending(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            activity = "topResumedActivity=ActivityRecord{123 u0 com.progpu.samples/test.MainActivity t8}"
            window = "mCurrentFocus=Window{123 u0 com.progpu.samples/test.MainActivity}\n"
            (root / "activity.txt").write_text(activity)
            (root / "window.txt").write_text(window)
            for variant, expected in (("blank", 1), ("content", 0), ("corrupt", 2), ("lost-focus", 2)):
                with self.subTest(variant=variant):
                    (root / "screenshot.png").write_bytes(
                        b"adb: device offline" if variant == "corrupt" else
                        png(80, 100, rows=gallery_pixels() if variant == "content" else None))
                    (root / "window-after.txt").write_text("mCurrentFocus=null\n" if variant == "lost-focus" else window)
                    output = root / f"{variant}.json"
                    result = subprocess.run([
                        sys.executable, SPEC.origin, "screenshot", "--png", str(root / "screenshot.png"),
                        "--activity", str(root / "activity.txt"), "--window", str(root / "window.txt"),
                        "--window-after", str(root / "window-after.txt"), "--output", str(output)],
                        capture_output=True, text=True, timeout=10)
                    self.assertEqual(expected, result.returncode, result.stderr)
                    self.assertEqual(expected <= 1, output.exists())
                    if expected <= 1:
                        self.assertEqual(9 if expected == 0 else 0, json.loads(output.read_text())["content_tiles"])

    def test_command_deadline_terminates_pending_work(self):
        result = subprocess.run([
            sys.executable, SPEC.origin, "run-command", "--timeout", "1", "--", sys.executable,
            "-c", "import time; time.sleep(30)"], capture_output=True, text=True, timeout=5)
        self.assertEqual(124, result.returncode, result.stderr)

    def test_truncated_corrupt_and_error_text_screenshots_rejected(self):
        valid = png()
        for contents in (valid[:-1], valid[:40] + b"x" + valid[41:], b"adb: device offline"):
            with self.subTest(contents=contents), self.assertRaises(ValueError):
                EVIDENCE.verify_png(contents)

    def test_resumed_and_focused_sample(self):
        EVIDENCE.verify_foreground(
            "topResumedActivity=ActivityRecord{123 u0 com.progpu.samples/test.MainActivity t8}",
            "  mCurrentFocus=Window{abc123 u0 com.progpu.samples/test.MainActivity}\n")

    def test_resumed_sample_behind_dialog_or_another_window_is_rejected(self):
        activity = "topResumedActivity=ActivityRecord{123 u0 com.progpu.samples/test.MainActivity t8}"
        for focus in ("null", "Window{123 u0 Application Not Responding: com.google.android.apps.nexuslauncher}",
                      "Window{123 u0 NotificationShade}", "Window{123 u0 com.android.launcher/.Launcher}",
                      "Window{123 u0 com.progpu.samples.other/.MainActivity}"):
            with self.subTest(focus=focus), self.assertRaises(ValueError):
                EVIDENCE.verify_foreground(activity, "mCurrentFocus=" + focus)

    def test_window_inventory_without_focus_is_not_foreground_evidence(self):
        with self.assertRaises(ValueError):
            EVIDENCE.verify_foreground(
                "mResumedActivity: ActivityRecord{123 u0 com.progpu.samples/test.MainActivity t8}",
                "Window #1 Window{123 u0 com.progpu.samples/test.MainActivity}:\n    isVisible=true\n")

    def test_focused_sample_without_resumed_activity_is_rejected(self):
        with self.assertRaises(ValueError):
            EVIDENCE.verify_foreground(
                "mLastPausedActivity: ActivityRecord{123 u0 com.progpu.samples/test.MainActivity t8}",
                "mCurrentFocus=Window{123 u0 com.progpu.samples/test.MainActivity}\n")

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
