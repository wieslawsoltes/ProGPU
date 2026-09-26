#!/usr/bin/env python3
"""Offline wrapper controls; the fake dotnet is not product-test evidence."""

import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / "progpu-test-system-drawing.py"
SPEC = importlib.util.spec_from_file_location("drawing_trace", SCRIPT)
TRACE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRACE)

STUB = r'''
import json, os, pathlib, signal, sys, xml.etree.ElementTree as ET
if sys.argv[1:] == ["--info"]:
    print("synthetic dotnet identity for offline wrapper testing")
    sys.exit(0)
args = sys.argv[1:]
assert args[:6] == ["test", "src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj",
                     "--configuration", "Release", "--verbosity", "normal"], args
assert len(args) == 12, args
settings = pathlib.Path(args[args.index("--settings") + 1])
values = {node.tag: node.text for node in ET.parse(settings).findall("./RunConfiguration/EnvironmentVariables/*")}
assert values["DOTNET_EnableEventPipe"] == "1"
assert values["DOTNET_EventPipeCircularMB"] == "40"
assert values["DOTNET_EventPipeOutputStreaming"] == "0"
assert len(values) == 5
output = pathlib.Path(values["DOTNET_EventPipeOutputPath"].replace("{pid}", str(os.getpid())))
(settings.parent / "observed.json").write_text(json.dumps({"args": args, "values": values}))
if os.environ.get("STUB_TRACE", "1") == "1":
    output.write_bytes(b"synthetic trace control, not a real nettrace")
(settings.parent / "quality.trx").write_text("synthetic TRX control")
print("synthetic child stdout", flush=True)
print("synthetic child stderr", file=sys.stderr, flush=True)
code = int(os.environ["STUB_EXIT"])
if code < 0:
    os.kill(os.getpid(), -code)
sys.exit(code)
'''


class DrawingTraceTests(unittest.TestCase):
    def run_stub(self, exit_code, trace=True):
        temporary = tempfile.TemporaryDirectory(prefix="drawing-trace-control-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        executable = root / "synthetic-dotnet"
        executable.write_text(f"#!{sys.executable}\n" + STUB)
        executable.chmod(0o755)
        output = root / "evidence & <escaped>"
        output.mkdir()
        unrelated = output / "testhost-caller.nettrace"
        unrelated.write_bytes(b"caller-owned")
        environment = os.environ | {"STUB_EXIT": str(exit_code), "STUB_TRACE": "1" if trace else "0"}
        result = subprocess.run([sys.executable, str(SCRIPT), "--dotnet", str(executable), "--output", str(output)],
                                env=environment, text=True, capture_output=True, timeout=15)
        directory, = output.glob("run-*")
        status = json.loads((directory / "status.json").read_text())
        self.assertEqual(b"caller-owned", unrelated.read_bytes())
        self.assertIn("synthetic child stdout", (directory / "quality.log").read_text())
        self.assertIn("synthetic child stderr", result.stdout)
        self.assertTrue((directory / "quality.trx").is_file())
        self.assertNotIn("--filter", status["command"])
        self.assertNotIn("--no-build", status["command"])
        return result, directory, status

    def test_success_removes_only_owned_trace_and_preserves_metadata(self):
        result, directory, status = self.run_stub(0)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual(0, status["testExitCode"])
        self.assertEqual([], list(directory.glob("*.nettrace")))
        self.assertEqual("removed-after-success", status["traces"][0]["disposition"])

    def test_failing_child_exact_codes_are_retained_with_hashed_trace(self):
        for code in (1, 7, 127):
            with self.subTest(code=code):
                result, directory, status = self.run_stub(code)
                self.assertEqual(code, result.returncode, result.stdout + result.stderr)
                self.assertEqual(code, status["testExitCode"])
                record, = status["traces"]
                self.assertEqual("retained-after-failure", record["disposition"])
                self.assertEqual(64, len(record["sha256"]))
                self.assertTrue((directory / record["name"]).is_file())

    def test_signal_failure_keeps_shell_status_and_trace(self):
        result, _, status = self.run_stub(-signal.SIGTERM)
        self.assertEqual(128 + signal.SIGTERM, result.returncode)
        self.assertEqual(128 + signal.SIGTERM, status["testExitCode"])

    def test_missing_trace_cannot_replace_original_failure(self):
        result, _, status = self.run_stub(7, trace=False)
        self.assertEqual(7, result.returncode)
        self.assertIn("No testhost trace", status["diagnosticError"])

    def test_missing_trace_is_not_silently_successful(self):
        result, _, status = self.run_stub(0, trace=False)
        self.assertEqual(2, result.returncode)
        self.assertEqual(0, status["testExitCode"])

    def test_retention_budget_removes_oversize_without_truncating_valid_trace(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            huge = root / "testhost-1.nettrace"
            with huge.open("wb") as stream:
                stream.truncate(TRACE.MAX_TRACE_BYTES + 1)
            valid = root / "testhost-2.nettrace"
            valid.write_bytes(b"valid-control")
            records = TRACE.finish_traces(root, 7)
            self.assertFalse(huge.exists())
            self.assertEqual(b"valid-control", valid.read_bytes())
            self.assertEqual("removed-empty-or-over-budget", records[0]["disposition"])
            self.assertEqual("retained-after-failure", records[1]["disposition"])

    def test_settings_do_not_change_test_policy(self):
        with tempfile.TemporaryDirectory() as name:
            root = ET.parse(TRACE.settings(Path(name))).getroot()
            self.assertEqual(["RunConfiguration"], [child.tag for child in root])
            self.assertEqual(["EnvironmentVariables"], [child.tag for child in root[0]])
            self.assertEqual(TRACE.PROVIDERS, root.findtext("./RunConfiguration/EnvironmentVariables/DOTNET_EventPipeConfig"))


if __name__ == "__main__":
    unittest.main()
