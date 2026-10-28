"""Cross-platform controls for the real Windows runtime verifier/stager."""
import copy
import importlib.util
import json
from pathlib import Path
import struct
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SPEC = importlib.util.spec_from_file_location("runtime", Path(__file__).parents[1] / "progpu-wgpu-windows-runtime.py")
runtime = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(runtime)


class WindowsRuntimeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.inputs = runtime.load_inputs()

    def dependency(self, rid="win-x64"):
        root = self.root / rid
        root.mkdir()
        for name in runtime.DEPENDENCY_FILES:
            (root / name).write_text(f"original notice {name}\n")
        pe = bytearray(256)
        pe[:2] = b"MZ"
        struct.pack_into("<I", pe, 60, 64)
        pe[64:68] = b"PE\0\0"
        struct.pack_into("<H", pe, 68, runtime.MACHINES[rid])
        struct.pack_into("<HHH", pe, 84, 112, 0x2000, 0x20B)
        (root / "wgpu_native.dll").write_bytes(pe)
        native, build = self.inputs["native"], self.inputs["build"]
        manifest = {"schemaVersion": 2, "rid": rid, "backendAbi": native["backendAbi"],
                    "nativeRevision": native["revision"], "headersRevision": native["webGpuHeadersRevision"],
                    "wgpuRevision": build["wgpuRevision"], "compilerFeature": build["feature"],
                    "lockSha256": build["lockSha256"], "shaderInputs": self.inputs["shader"],
                    "exports": sorted(runtime.REQUIRED_EXPORTS),
                    "files": {name: runtime.sha(root / name) for name in runtime.DEPENDENCY_FILES},
                    "librarySha256": runtime.sha(root / "wgpu_native.dll")}
        (root / "wgpu-native-build.json").write_text(json.dumps(manifest))
        return root

    def test_windows_autocrlf_checkout_preserves_pinned_patch_bytes(self):
        checkout = self.root / "checkout"
        patch_dir = checkout / "eng/wgpu-hlsl"
        patch_dir.mkdir(parents=True)
        (checkout / ".gitattributes").write_bytes((runtime.ROOT / ".gitattributes").read_bytes())
        patch_file = patch_dir / "naga-workgroup-zero-init.patch"
        patch_file.write_bytes((runtime.ROOT / "eng/wgpu-hlsl/naga-workgroup-zero-init.patch").read_bytes())
        def git(*args):
            subprocess.run(["git", "-C", str(checkout), "-c", "core.autocrlf=true", *args], check=True, capture_output=True)
        git("init")
        git("add", ".")
        git("-c", "user.name=Control", "-c", "user.email=control@example.invalid", "commit", "-m", "reviewed bytes")
        patch_file.unlink()
        git("checkout", "--", "eng/wgpu-hlsl/naga-workgroup-zero-init.patch")
        self.assertEqual(runtime.sha(patch_file), self.inputs["shader"]["patchSha256"])

    def test_both_architectures_stage_same_exact_bytes_and_original_notices(self):
        for rid in runtime.MACHINES:
            with self.subTest(rid=rid):
                source = self.dependency(rid)
                target = self.root / f"{rid}-published"
                runtime.stage(source, target, rid)
                receipt = runtime.verify_payload(target, rid)
                self.assertEqual(set(receipt["files"]), runtime.PAYLOAD_FILES)
                self.assertEqual({p.name for p in target.iterdir()}, runtime.PAYLOAD_FILES | {runtime.RECEIPT})
                for name in runtime.PAYLOAD_FILES:
                    self.assertEqual((source / name).read_bytes(), (target / name).read_bytes())
                self.assertFalse((target / "dxcompiler.dll").exists())

    def test_wrong_architecture_rejected_even_with_matching_receipts(self):
        source = self.dependency()
        manifest = json.loads((source / "wgpu-native-build.json").read_text())
        manifest["rid"] = "win-arm64"
        (source / "wgpu-native-build.json").write_text(json.dumps(manifest))
        with self.assertRaisesRegex(ValueError, "architecture"):
            runtime.verify_dependency(source, "win-arm64")

    def test_modified_notice_and_dll_are_rejected_before_publication(self):
        for name in ("LICENSE.MIT", "wgpu_native.dll"):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as sub:
                source = self.dependency()
                (source / name).write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "differs from producer"):
                    runtime.stage(source, self.root / "output", "win-x64")
                self.assertFalse((self.root / "output").exists())
                import shutil
                shutil.rmtree(source)

    def test_old_unpatched_receipt_and_wrong_patch_are_rejected(self):
        source = self.dependency()
        path = source / "wgpu-native-build.json"
        original = json.loads(path.read_text())
        for change in ("schema", "patch", "exports"):
            manifest = copy.deepcopy(original)
            if change == "schema": manifest["schemaVersion"] = 1
            elif change == "patch": manifest["shaderInputs"]["patchSha256"] = "0" * 64
            else: manifest["exports"].remove("wgpuCreateInstance")
            path.write_text(json.dumps(manifest))
            with self.subTest(change=change), self.assertRaises(ValueError):
                runtime.verify_dependency(source, "win-x64")

    def test_existing_output_is_preserved(self):
        source = self.dependency()
        target = self.root / "output"
        target.mkdir()
        (target / "caller-data").write_text("preserve")
        with self.assertRaisesRegex(ValueError, "new directory"):
            runtime.stage(source, target, "win-x64")
        self.assertEqual((target / "caller-data").read_text(), "preserve")

    def test_links_and_unrecorded_compilers_rejected(self):
        source = self.dependency()
        original = source / "LICENSE.MIT"
        saved = self.root / "notice"
        original.rename(saved)
        original.symlink_to(saved)
        with self.assertRaises(ValueError): runtime.verify_dependency(source, "win-x64")
        original.unlink()
        saved.rename(original)
        (source / "dxcompiler.dll").write_bytes(b"foreign")
        with self.assertRaisesRegex(ValueError, "unrecorded"):
            runtime.verify_dependency(source, "win-x64")

    def test_changed_copy_fails_atomically_and_cleans_only_pending_directory(self):
        source = self.dependency()
        target = self.root / "output"
        real_copy = runtime.shutil.copyfile
        def changed_copy(src, dst):
            result = real_copy(src, dst)
            if Path(src).name == "LICENSE.MIT": Path(dst).write_text("changed during copy")
            return result
        with patch.object(runtime.shutil, "copyfile", changed_copy), self.assertRaises(ValueError):
            runtime.stage(source, target, "win-x64")
        self.assertFalse(target.exists())
        self.assertEqual({p.name for p in self.root.iterdir()}, {"win-x64"})

    def test_package_exact_inventory_and_duplicate_rejection(self):
        source = self.dependency()
        output = self.root / "output"
        runtime.stage(source, output, "win-x64")
        package = self.root / "runtime.nupkg"
        with zipfile.ZipFile(package, "w") as archive:
            for file in output.iterdir(): archive.write(file, f"runtimes/win-x64/native/{file.name}")
        runtime.verify_package(package, "win-x64")
        with zipfile.ZipFile(package, "a") as archive:
            archive.writestr("runtimes/win-x64/native/foreign.dll", b"not owned")
        with self.assertRaisesRegex(ValueError, "inventory"):
            runtime.verify_package(package, "win-x64")

    def test_truncated_header_and_executable_rejected(self):
        source = self.dependency()
        dll = source / "wgpu_native.dll"
        original = dll.read_bytes()
        for mode in ("truncated", "exe"):
            data = bytearray(original)
            if mode == "truncated": struct.pack_into("<I", data, 60, 0xFFFFFFFF)
            else: struct.pack_into("<H", data, 86, 0)
            dll.write_bytes(data)
            with self.subTest(mode=mode), self.assertRaises(ValueError):
                runtime.pe_machine(dll, "win-x64")


class SourceOwnershipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "external"
        self.source.mkdir()
        def git(*args):
            return subprocess.check_output(["git", "-C", str(self.source), *args], text=True, stderr=subprocess.DEVNULL).strip()
        self.git = git
        git("init")
        git("config", "core.autocrlf", "false")
        (self.source / "writer.rs").write_bytes(b"original\n")
        (self.source / "unrelated.rs").write_text("untouched\n")
        git("add", ".")
        git("-c", "user.name=Control", "-c", "user.email=control@example.invalid", "commit", "-m", "original")
        revision = git("rev-parse", "HEAD")
        original_sha = runtime.sha(self.source / "writer.rs")
        (self.source / "writer.rs").write_bytes(b"reviewed\n")
        patched_sha = runtime.sha(self.source / "writer.rs")
        patch_dir = self.root / "eng/wgpu-hlsl"
        patch_dir.mkdir(parents=True)
        (patch_dir / "naga-workgroup-zero-init.patch").write_bytes((git("diff") + "\n").encode("utf-8"))
        git("checkout", "--", "writer.rs")
        inputs = {"shader": {"wgpuRevision": revision, "modifiedFiles": [{"path": "writer.rs", "originalSha256": original_sha, "patchedSha256": patched_sha}]}}
        self.addCleanup(patch.stopall)
        patch.object(runtime, "ROOT", self.root).start()
        patch.object(runtime, "load_inputs", return_value=inputs).start()

    def test_exact_original_applies_once_and_retry_preserves_reviewed_source(self):
        runtime.prepare_source(self.source)
        self.assertEqual((self.source / "writer.rs").read_text(), "reviewed\n")
        runtime.prepare_source(self.source)
        runtime.prepare_source(self.source, apply=False)
        self.assertEqual((self.source / "unrelated.rs").read_text(), "untouched\n")

    def test_apply_under_windows_autocrlf_keeps_exact_reviewed_lf_bytes(self):
        self.git("config", "core.autocrlf", "true")
        runtime.prepare_source(self.source)
        self.assertEqual((self.source / "writer.rs").read_bytes(), b"reviewed\n")
        runtime.prepare_source(self.source, apply=False)

    def test_read_only_postbuild_check_never_repairs_original_source(self):
        with self.assertRaisesRegex(ValueError, "patched bytes"):
            runtime.prepare_source(self.source, apply=False)
        self.assertEqual((self.source / "writer.rs").read_text(), "original\n")

    def test_foreign_changes_and_untracked_files_remain_untouched(self):
        (self.source / "unrelated.rs").write_text("caller edit\n")
        with self.assertRaisesRegex(ValueError, "unrelated"):
            runtime.prepare_source(self.source)
        self.assertEqual((self.source / "unrelated.rs").read_text(), "caller edit\n")
        self.git("checkout", "--", "unrelated.rs")
        (self.source / "caller.txt").write_text("caller data\n")
        with self.assertRaisesRegex(ValueError, "untracked"):
            runtime.prepare_source(self.source)
        self.assertEqual((self.source / "caller.txt").read_text(), "caller data\n")


if __name__ == "__main__":
    unittest.main()
