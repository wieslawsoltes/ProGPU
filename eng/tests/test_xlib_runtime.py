"""Dependency admission controls; synthetic bytes are never packed or executed."""
import copy
import importlib.util
import json
from pathlib import Path
import struct
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "build-wgpu-native-linux.py"
spec = importlib.util.spec_from_file_location("xlib_runtime", SCRIPT)
runtime = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runtime)


class RuntimeAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.payload = Path(self.temp.name)
        self.pin = runtime.load_inputs()
        for name in runtime.PAYLOAD_FILES:
            (self.payload / name).write_bytes(b"synthetic admission control, not a runtime")
        self.header = bytearray(64)
        self.header[:7] = b"\x7fELF\x02\x01\x01"
        struct.pack_into("<HH", self.header, 16, 3, 62)
        (self.payload / "libwgpu_native.so").write_bytes(self.header)
        self.receipt = {"schemaVersion": 1, "rid": "linux-x64", "inputs": self.pin,
                        "abiTestsPassed": True, "extensionExported": self.pin["extension"],
                        "files": {name: runtime.sha(self.payload / name) for name in runtime.PAYLOAD_FILES}}
        self.write_receipt()

    def write_receipt(self):
        (self.payload / "wgpu-native-linux.json").write_text(json.dumps(self.receipt))

    def rejects(self):
        with self.assertRaises((ValueError, FileNotFoundError)):
            runtime.verify_payload(self.payload, "linux-x64", self.pin)

    def test_original_inventory(self):
        self.assertEqual(self.receipt, runtime.verify_payload(self.payload, "linux-x64", self.pin))

    def test_changed_bytes_reject(self):
        for name in runtime.PAYLOAD_FILES:
            with self.subTest(name=name):
                path = self.payload / name
                original = path.read_bytes()
                path.write_bytes(original + b"changed")
                self.rejects()
                path.write_bytes(original)

    def test_missing_files_reject(self):
        for name in runtime.PAYLOAD_FILES:
            with self.subTest(name=name):
                path = self.payload / name
                original = path.read_bytes()
                path.unlink()
                self.rejects()
                path.write_bytes(original)

    def test_extra_inventory_rejects(self):
        (self.payload / "unrecorded.so").write_bytes(b"unrecorded")
        self.rejects()

    def test_symbolic_link_rejects_even_matching_bytes(self):
        target = self.payload / "libwgpu_native.so"
        sibling = self.payload.parent / (self.payload.name + "-library")
        sibling.write_bytes(target.read_bytes())
        self.addCleanup(sibling.unlink)
        target.unlink()
        target.symlink_to(sibling)
        self.rejects()

    def test_original_receipt_fields_are_required(self):
        for key in ("schemaVersion", "rid", "inputs", "abiTestsPassed", "extensionExported", "files"):
            with self.subTest(key=key):
                original = self.receipt.pop(key)
                self.write_receipt()
                self.rejects()
                self.receipt[key] = original

    def test_different_source_patch_lock_or_toolchain_rejects(self):
        for key in ("nativeRevision", "headersRevision", "wgpuRevision", "rustVersion", "libclangMajor", "lockSha256", "files"):
            with self.subTest(key=key):
                self.receipt["inputs"] = copy.deepcopy(self.pin)
                self.receipt["inputs"][key] = "different"
                self.write_receipt()
                self.rejects()

    def test_wrong_architecture_rejects_even_with_matching_hash(self):
        struct.pack_into("<H", self.header, 18, 183)
        (self.payload / "libwgpu_native.so").write_bytes(self.header)
        self.receipt["files"]["libwgpu_native.so"] = runtime.sha(self.payload / "libwgpu_native.so")
        self.write_receipt()
        self.rejects()

    def test_elf_identity_type_and_truncation_reject(self):
        for offset, value in ((0, 0), (4, 1), (5, 2), (6, 0), (16, 2)):
            with self.subTest(offset=offset):
                changed = bytearray(self.header)
                changed[offset] = value
                (self.payload / "libwgpu_native.so").write_bytes(changed)
                with self.assertRaises(ValueError):
                    runtime.elf_machine(self.payload / "libwgpu_native.so", "linux-x64")
        (self.payload / "libwgpu_native.so").write_bytes(self.header[:63])
        with self.assertRaises(ValueError):
            runtime.elf_machine(self.payload / "libwgpu_native.so", "linux-x64")


if __name__ == "__main__":
    unittest.main()
