"""Synthetic verifier controls, not renderer qualification."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPEC = importlib.util.spec_from_file_location("svg_recursion", Path(__file__).resolve().parents[1] / "progpu-verify-svg-recursion.py")
VERIFY = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY)


class RecursionReferenceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        for provider, architecture in VERIFY.PROVIDERS:
            folder = self.root / provider
            folder.mkdir()
            native = provider.startswith("Reference")
            token = "cc7b13ffcd2ddd51" if native else "c29c9752855ee183"
            drawing = {"Name": "System.Drawing.Common, Version=10.0.0.0, Culture=neutral, PublicKeyToken=" + token,
                       "Path": "C:/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12/System.Drawing.Common.dll" if native else "/portable/System.Drawing.Common.dll",
                       "Sha256": ("a" if native else "b") * 64}
            self.write(provider, "workers.json", [dict(Key=key, ExitCode=0, TimedOut=False, HasResult=True)
                                                 for key, _, _ in VERIFY.FIXTURES])
            for index, (key, width, height) in enumerate(VERIFY.FIXTURES):
                self.write(provider, f"{index}-identity.json", dict(
                    Key=key, Width=width, Height=height, Architecture=architecture, Runtime=".NET 10.0.12",
                    FixtureSha256=("c" if native else "d") * 64, SourceTextSha256="e" * 64,
                    ReferenceSha256="f" * 64, Drawing=drawing, Svg=dict(Name="Svg", Path="/Svg.dll", Sha256="1" * 64),
                    Fonts=dict(SourceFileCount=21 if index == 0 else 22, LoadedFaceCount=21 if index == 0 else 22,
                               InventorySha256=VERIFY.FONT_HASHES[0 if index == 0 else 1])))
                error = dict(Type="System.ArgumentException", Parameter=None,
                             Detail="at System.Drawing.Graphics.ScaleTransform(Single sx, Single sy, MatrixOrder order)")
                self.write(provider, f"{index}-result.json", dict(Outcome="Exception", Exception=error))
                if not native:
                    error["Detail"] = "at System.Drawing.Graphics.ValidateWorldTransform(Matrix3x2 transform)"
                self.write(provider, f"{index}-first-drawing-exception.json", error)

    def write(self, provider, name, data):
        (self.root / provider / name).write_text(json.dumps(data), encoding="utf-8")

    def change(self, name, mutate, provider="Portable-linux-x64"):
        path = self.root / provider / name
        data = json.loads(path.read_text(encoding="utf-8"))
        mutate(data)
        self.write(provider, name, data)
        with self.assertRaises(ValueError):
            VERIFY.verify(self.root)

    def test_matching_failures_with_different_raw_line_endings(self):
        VERIFY.verify(self.root)

    def test_null_cleanup_is_not_scale_rejection(self):
        self.change("0-result.json", lambda data: data["Exception"].update(Type="System.ArgumentNullException"))

    def test_correct_type_from_wrong_operation_is_rejected(self):
        self.change("0-result.json", lambda data: data["Exception"].update(Detail="at System.Drawing.Region.Transform("))

    def test_masked_earlier_region_error_is_rejected(self):
        self.change("0-first-drawing-exception.json", lambda data: data.update(Parameter="matrix"))

    def test_earlier_wrong_operation_without_parameter_is_rejected(self):
        self.change("0-first-drawing-exception.json", lambda data: data.update(Detail="at System.Drawing.Region.Transform("))

    def test_missing_worker_is_rejected(self):
        self.change("workers.json", lambda data: data.pop())

    def test_reordered_workers_are_rejected(self):
        self.change("workers.json", lambda data: data.reverse())

    def test_timeout_is_not_an_exception(self):
        self.change("workers.json", lambda data: data[0].update(TimedOut=True))

    def test_crash_is_not_an_exception(self):
        self.change("workers.json", lambda data: data[0].update(ExitCode=134))

    def test_false_exit_code_is_not_zero(self):
        self.change("workers.json", lambda data: data[0].update(ExitCode=False))

    def test_source_text_must_match(self):
        self.change("0-identity.json", lambda data: data.update(SourceTextSha256="2" * 64))

    def test_missing_hash_is_rejected(self):
        self.change("0-identity.json", lambda data: data.update(SourceTextSha256=""))

    def test_reference_image_must_match(self):
        self.change("0-identity.json", lambda data: data.update(ReferenceSha256="2" * 64))

    def test_font_environment_must_match(self):
        self.change("0-identity.json", lambda data: data["Fonts"].update(LoadedFaceCount=20))

    def test_real_reference_is_required(self):
        self.change("0-identity.json", lambda data: data["Drawing"].update(Path="/portable/System.Drawing.Common.dll"), "Reference-win-x64")

    def test_both_architectures_are_required(self):
        self.change("0-identity.json", lambda data: data.update(Architecture="X64"), "Reference-win-arm64")

    def test_rendered_result_requires_inventory_review(self):
        self.change("0-result.json", lambda data: data.update(Outcome="Rendered"))


if __name__ == "__main__":
    unittest.main()
