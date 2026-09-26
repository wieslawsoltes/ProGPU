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
from unittest.mock import Mock, patch
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / "progpu-test-system-drawing.py"
SPEC = importlib.util.spec_from_file_location("drawing_trace", SCRIPT)
TRACE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRACE)

STUB = r'''
import json, os, pathlib, signal, socket, sys, time, xml.etree.ElementTree as ET
if sys.argv[1:] == ["--info"]:
    print("synthetic dotnet identity for offline wrapper testing")
    sys.exit(0)
args = sys.argv[1:]
if args == ["tool", "restore"] or args[0] == "build":
    sys.exit(0)
if args == ["tool", "run", "dotnet-trace", "--", "--version"]:
    print("9.0.661903+synthetic-offline-control")
    sys.exit(0)
if args[:5] == ["tool", "run", "dotnet-trace", "--", "collect"]:
    endpoint = args[args.index("--diagnostic-port") + 1]
    output = pathlib.Path(args[args.index("--output") + 1])
    assert args[args.index("--buffersize") + 1] == "64"
    def stop(*_):
        (output.parent / "collector-stopped").touch()
        sys.exit(0)
    signal.signal(signal.SIGINT, stop)
    server = socket.socket(socket.AF_UNIX)
    server.bind(endpoint)
    server.listen(1)
    if os.environ.get("STUB_DIE_BEFORE_CONNECT") == "1":
        time.sleep(0.1)
        sys.exit(23)
    peer, _ = server.accept()
    if os.environ.get("STUB_TRACE", "1") == "1":
        output.write_bytes(b"synthetic trace control, not a real nettrace")
        if os.environ.get("STUB_BUDGET") == "1":
            with output.open("r+b") as stream:
                stream.truncate(96 * 1024 * 1024)
    peer.sendall(b"started")
    while True:
        time.sleep(0.01)
if pathlib.Path(args[0]).name == "ProGPU.SampleMemoryProfiler.dll":
    assert args[1] == "verify-drawing-trace"
    valid = os.environ.get("STUB_INVALID") != "1"
    pid = int((pathlib.Path(args[3]).parent / "testhost-session-ended").read_text())
    if os.environ.get("STUB_WRONG_PID") == "1":
        pid += 1
    pathlib.Path(args[3]).write_text(json.dumps({"complete": valid, "processId": pid}))
    if not valid:
        print("synthetic EOF finalization failure")
    sys.exit(0 if valid else 29)
assert args[:6] == ["test", "src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj",
                     "--configuration", "Release", "--verbosity", "normal"], args
assert len(args) == 12, args
settings = pathlib.Path(args[args.index("--settings") + 1])
values = {node.tag: node.text for node in ET.parse(settings).findall("./RunConfiguration/EnvironmentVariables/*")}
assert len(values) == 2
collector = ET.parse(settings).find("./InProcDataCollectionRunSettings/InProcDataCollectors/InProcDataCollector")
assert pathlib.Path(collector.attrib["codebase"]).name == "ProGPU.TestTraceLifetime.dll"
assert collector.attrib["assemblyQualifiedName"] == "ProGPU.Diagnostics.SessionTraceLifetime, ProGPU.TestTraceLifetime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"
assert pathlib.Path(values["PROGPU_TEST_TRACE_LIFETIME"]) == settings.parent
endpoint, connect, suspend = values["DOTNET_DiagnosticPorts"].split(",")
assert connect == "connect" and suspend == "nosuspend"
peer = socket.socket(socket.AF_UNIX)
if os.environ.get("STUB_DIE_BEFORE_CONNECT") == "1":
    time.sleep(0.3)
    try:
        peer.connect(endpoint)
        raise AssertionError("collector should already be dead")
    except ConnectionRefusedError:
        pass
else:
    peer.connect(endpoint)
    assert peer.recv(7) == b"started"
(settings.parent / "observed.json").write_text(json.dumps({"args": args, "values": values}))
(settings.parent / "quality.trx").write_text("synthetic TRX control")
print("synthetic child stdout", flush=True)
print("synthetic child stderr", file=sys.stderr, flush=True)
code = int(os.environ["STUB_EXIT"])
if os.environ.get("STUB_BUDGET") == "1":
    deadline = time.monotonic() + 3
    while not (settings.parent / "collector-stopped").exists() and time.monotonic() < deadline:
        time.sleep(0.01)
    assert (settings.parent / "collector-stopped").is_file()
(settings.parent / "test-completed").touch()
(settings.parent / "testhost-session-ended").write_text(str(os.getpid()))
deadline = time.monotonic() + 3
while not (settings.parent / "collector-finalized").exists() and time.monotonic() < deadline:
    time.sleep(0.01)
if os.environ.get("STUB_OUTPUT_FAILURE") != "1":
    assert (settings.parent / "collector-finalized").is_file()
if code < 0:
    os.kill(os.getpid(), -code)
sys.exit(code)
'''


class DrawingTraceTests(unittest.TestCase):
    def run_stub(self, exit_code, trace=True, extra=None, fault=None):
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
        environment = os.environ | {"STUB_EXIT": str(exit_code), "STUB_TRACE": "1" if trace else "0"} | (extra or {})
        launch = [sys.executable, str(SCRIPT)]
        if fault:
            bootstrap = '''
import importlib.util, sys
path = sys.argv.pop(1)
kind = sys.argv.pop(1)
spec = importlib.util.spec_from_file_location("drawing_fault_control", path)
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
def broken(*args, **kwargs):
    raise OSError("synthetic diagnostic I/O failure")
if kind == "cleanup":
    m.finish_traces = broken
elif kind in ("output", "persistent-output"):
    original = sys.stdout
    class Proxy:
        def __getattr__(self, name): return getattr(original, name)
        @property
        def buffer(self): return self
        def write(self, data):
            if kind == "persistent-output": broken()
            if isinstance(data, bytes) and data: broken()
            return original.write(data) if isinstance(data, str) else len(data)
    sys.stdout = Proxy()
sys.exit(m.main())
'''
            launch = [sys.executable, "-c", bootstrap, str(SCRIPT), fault]
        result = subprocess.run(launch + ["--dotnet", str(executable), "--output", str(output)],
                                env=environment, text=True, capture_output=True, timeout=15)
        directory, = output.glob("run-*")
        status = json.loads((directory / "status.json").read_text())
        self.assertEqual(b"caller-owned", unrelated.read_bytes())
        self.assertIn("synthetic child stdout", (directory / "quality.log").read_text())
        if fault not in ("output", "persistent-output"):
            self.assertIn("synthetic child stderr", result.stdout)
        self.assertTrue((directory / "quality.trx").is_file())
        self.assertTrue((directory / "test-completed").is_file())
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
        self.assertIn("Trace is missing", status["diagnosticError"])

    def test_missing_trace_is_not_silently_successful(self):
        result, _, status = self.run_stub(0, trace=False)
        self.assertEqual(2, result.returncode)
        self.assertEqual(0, status["testExitCode"])

    def test_failed_finalization_preserves_failure_and_keeps_trace(self):
        for code in (0, 7):
            result, directory, status = self.run_stub(code, extra={"STUB_INVALID": "1"})
            self.assertEqual(code if code else 2, result.returncode)
            self.assertEqual(code, status["testExitCode"])
            self.assertIn("validation failed", status["diagnosticError"])
            self.assertEqual(1, len(list(directory.glob("*.nettrace"))))

    def test_budget_stops_only_collection_and_preserves_test_failure(self):
        result, directory, status = self.run_stub(7, extra={"STUB_BUDGET": "1"})
        self.assertEqual(7, result.returncode, result.stdout + result.stderr)
        self.assertEqual(7, status["testExitCode"])
        self.assertTrue((directory / "collector-stopped").is_file())
        self.assertTrue((directory / "test-completed").is_file())
        self.assertIn("collection-stop budget", status["diagnosticError"])

    def test_trace_pid_must_match_actual_testhost_lifetime(self):
        result, directory, status = self.run_stub(7, extra={"STUB_WRONG_PID": "1"})
        self.assertEqual(7, result.returncode)
        self.assertIn("actual testhost lifetime", status["diagnosticError"])
        self.assertEqual(1, len(list(directory.glob("*.nettrace"))))

    def test_collector_death_before_connection_does_not_suspend_or_replace_test(self):
        result, directory, status = self.run_stub(7, extra={"STUB_DIE_BEFORE_CONNECT": "1"})
        self.assertEqual(7, result.returncode, result.stdout + result.stderr)
        self.assertEqual(7, status["testExitCode"])
        self.assertEqual(23, status["collectorExitCode"])
        self.assertTrue((directory / "test-completed").is_file())

    def test_diagnostic_output_and_cleanup_errors_preserve_observed_test_failure(self):
        for fault in ("output", "persistent-output", "cleanup"):
            result, _, status = self.run_stub(7, extra={"STUB_OUTPUT_FAILURE": "1"}, fault=fault)
            self.assertEqual(7, result.returncode, result.stdout + result.stderr)
            self.assertEqual(7, status["testExitCode"])
            self.assertIn("synthetic diagnostic I/O failure", status["diagnosticError"])

    def test_unresponsive_collector_cleanup_targets_only_collector_group(self):
        collector = Mock(pid=424242)
        collector.poll.return_value = None
        collector.wait.side_effect = [subprocess.TimeoutExpired("collector", 30), -signal.SIGKILL]
        with patch.object(TRACE.os, "killpg") as kill:
            with self.assertRaisesRegex(RuntimeError, "testhost was not terminated"):
                TRACE.finalize_collector(collector)
        self.assertEqual([(424242, signal.SIGINT), (424242, signal.SIGKILL)],
                         [entry.args for entry in kill.call_args_list])

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
            root = ET.parse(TRACE.settings(Path(name), Path(name) / "testhost.sock")).getroot()
            self.assertEqual(["RunConfiguration", "InProcDataCollectionRunSettings"], [child.tag for child in root])
            self.assertEqual(["EnvironmentVariables"], [child.tag for child in root[0]])
            self.assertEqual(["DOTNET_DiagnosticPorts", "PROGPU_TEST_TRACE_LIFETIME"],
                             [node.tag for node in root[0][0]])


if __name__ == "__main__":
    unittest.main()
