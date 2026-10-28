"""Exercise source pinning and preservation with real temporary Git repositories."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch


SPEC = importlib.util.spec_from_file_location(
    "framework_sources", Path(__file__).resolve().parents[1] / "progpu-prepare-framework-sources.py")
SOURCES = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(SOURCES)
GIT = SOURCES.git


class FrameworkSourceTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="progpu-framework-sources-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.producer = self.root / "producer"
        self.producer.mkdir()
        self.origin = self.root / "original"
        self.origin.mkdir()
        GIT(self.origin, "init", "--quiet")
        GIT(self.origin, "config", "user.name", "Source pin controls")
        GIT(self.origin, "config", "user.email", "source-pin-controls@example.invalid")
        for relative in SOURCES.SOURCES["LibreWPF"]:
            directory = self.origin / relative
            directory.mkdir(parents=True)
            (directory / "original.txt").write_text("original retained source\n")
        (self.origin / "unselected").mkdir()
        (self.origin / "unselected/private.txt").write_text("unselected source\n")
        GIT(self.origin, "add", ".")
        GIT(self.origin, "commit", "--quiet", "-m", "Original source")
        self.revision = GIT(self.origin, "rev-parse", "HEAD")
        self.checkout = self.producer / "external/LibreWPF"
        self.addCleanup(patch.stopall)
        patch.object(SOURCES, "ROOT", self.producer).start()

        def local_fetch(path, *arguments):
            # All status, sparse checkout, revision and preservation operations
            # are real Git. Only this test's transport is redirected locally.
            if arguments[0] == "fetch":
                arguments = tuple(str(self.origin) if value == "origin" else value
                                  for value in arguments if value != "--filter=blob:none")
            return GIT(path, *arguments)

        patch.object(SOURCES, "git", local_fetch).start()

    def prepare(self):
        SOURCES.prepare("LibreWPF", self.revision)

    def test_exact_detached_source_without_unselected_or_recursive_checkout(self):
        self.prepare()
        self.assertEqual(GIT(self.checkout, "rev-parse", "HEAD"), self.revision)
        self.assertEqual(GIT(self.checkout, "branch", "--show-current"), "")
        self.assertFalse((self.checkout / "unselected").exists())
        self.assertFalse((self.checkout / "external/ProGPU/.git").exists())
        self.prepare()
        self.assertEqual(GIT(self.checkout, "status", "--porcelain"), "")

    def test_drawing_sparse_checkout_retains_owned_native_runtime_imports(self):
        # Current Drawing targets import these source-owned files. They must be
        # present even though the nested ProGPU submodule stays uninitialized.
        project = self.origin / "src/ProGPU.Drawing"
        project.mkdir(parents=True)
        (project / "Directory.Build.targets").write_text(
            '<Project><Import Project="../../eng/LibreWinForms.NativeRuntime.targets" /></Project>')
        (self.origin / "eng/LibreWinForms.NativeRuntime.targets").write_text(
            '<Project><Import Project="LibreWinForms.WindowsRuntime.props" /></Project>')
        (self.origin / "eng/LibreWinForms.WindowsRuntime.props").write_text('<Project />')
        GIT(self.origin, "add", ".")
        GIT(self.origin, "commit", "--quiet", "-m", "Owned Drawing import closure")
        revision = GIT(self.origin, "rev-parse", "HEAD")
        SOURCES.prepare("LibreWinForms", revision)
        checkout = self.producer / "external/LibreWinForms"
        for relative in ("src/ProGPU.Drawing/Directory.Build.targets",
                         "eng/LibreWinForms.NativeRuntime.targets",
                         "eng/LibreWinForms.WindowsRuntime.props"):
            self.assertEqual((checkout / relative).read_bytes(), (self.origin / relative).read_bytes())
        self.assertFalse((checkout / "external/ProGPU/.git").exists())
        self.assertFalse((checkout / "unselected").exists())
        self.assertEqual(GIT(checkout, "rev-parse", "HEAD"), revision)

    def test_local_changes_and_untracked_files_are_preserved(self):
        self.prepare()
        for relative in ("src/ProGPU.Wpf.Interop/original.txt", "uncommitted.txt"):
            file = self.checkout / relative
            original = file.read_bytes() if file.exists() else None
            file.write_bytes(b"keep this local work")
            with self.assertRaisesRegex(RuntimeError, "Preserve local changes"):
                self.prepare()
            self.assertEqual(file.read_bytes(), b"keep this local work")
            if original is None:
                file.unlink()
            else:
                file.write_bytes(original)

    def test_unexpected_repository_is_not_repurposed(self):
        self.prepare()
        GIT(self.checkout, "remote", "set-url", "origin", str(self.origin))
        with self.assertRaisesRegex(RuntimeError, "Unexpected framework source remote"):
            self.prepare()
        self.assertEqual(GIT(self.checkout, "remote", "get-url", "origin"), str(self.origin))

    def test_nonempty_directory_and_symlink_are_preserved(self):
        self.checkout.mkdir(parents=True)
        (self.checkout / "keep.txt").write_text("keep")
        with self.assertRaisesRegex(RuntimeError, "nonempty"):
            self.prepare()
        self.assertEqual((self.checkout / "keep.txt").read_text(), "keep")
        (self.checkout / "keep.txt").unlink()
        self.checkout.rmdir()
        self.checkout.symlink_to(self.origin, target_is_directory=True)
        with self.assertRaisesRegex(RuntimeError, "symbolic-link"):
            self.prepare()
        self.assertTrue(self.checkout.is_symlink())

    def test_invalid_second_pin_changes_neither_checkout(self):
        (self.producer / "eng").mkdir()
        (self.producer / "eng/framework-sources.json").write_text(json.dumps({
            "schemaVersion": 1,
            "repositories": {"LibreWPF": self.revision, "LibreWinForms": "main"},
        }))
        with self.assertRaisesRegex(ValueError, "full commit SHAs"):
            SOURCES.main()
        self.assertFalse((self.producer / "external").exists())


if __name__ == "__main__":
    unittest.main()
