#!/usr/bin/env python3
"""Offline fake-collector controls, never renderer or allocation qualification."""

import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


SCRIPT = Path(__file__).resolve().parents[1] / "progpu-diagnose-managed-picture.py"
SPEC = importlib.util.spec_from_file_location("picture_diagnostic", SCRIPT)
TRACE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(TRACE)

STUB = r'''
import json, os, pathlib, signal, sys, time
args = sys.argv[1:]
mode = os.environ.get("PICTURE_STUB_MODE", "success")
if args == ["--info"]:
    print("synthetic offline runtime; no .NET was executed")
    sys.exit(0)
if args == ["tool", "restore"]:
    sys.exit(23 if mode == "restore-error" else 0)
if args == ["tool", "run", "dotnet-trace", "--", "--version"]:
    print("0.0.0+wrong" if mode == "version-error" else "9.0.661903+synthetic")
    sys.exit(0)
assert args[:5] == ["tool", "run", "dotnet-trace", "--", "collect"], args
assert args[args.index("--buffersize") + 1] == "64"
assert "--show-child-io" in args
application = args[args.index("--", 4) + 1:]
assert application[1] == "exec", application
assert application[3:] == ["--managed-picture", "--rectangles", "384", "--warmup", "4", "--iterations", "8"], application
print("synthetic collector command " + json.dumps(args), flush=True)
if mode == "collector-error":
    sys.exit(29)
if mode == "timeout":
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    time.sleep(5)
else:
    output = pathlib.Path(args[args.index("--output") + 1])
    with output.open("wb", buffering=0) as stream:
        stream.write(b"synthetic bytes, not a real nettrace")
        if mode == "overflow":
            stream.write(b"x" * 4096)
    if mode == "overflow":
        time.sleep(5)
sys.exit(37 if mode == "rerun-failed" else 0)
'''


class ManagedPictureDiagnosticTests(unittest.TestCase):
    def exercise(self, mode="success", original_exit=17, limits=None, fault=None):
        temporary = tempfile.TemporaryDirectory(prefix="picture-offline-control-")
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        executable = root / "synthetic-dotnet"
        executable.write_text(f"#!{sys.executable}\n" + STUB)
        executable.chmod(0o755)
        project = root / "ProGPU.Native.Benchmarks.csproj"
        app = root / "ProGPU.Native.Benchmarks.dll"
        app.write_bytes(b"synthetic application identity; never executed")
        app.with_suffix(".runtimeconfig.json").write_text('{"runtimeOptions":{"tfm":"net10.0"}}')
        output = root / "evidence with spaces"
        output.mkdir()
        caller_trace = output / "caller.nettrace"
        caller_trace.write_bytes(b"caller-owned")
        command = [str(executable), "run", "--project", str(project), "-c", "Release", "--", *TRACE.ARGUMENTS]
        patches = {"ROOT": root, "PROJECT": project, "APP": app,
                   "TOTAL_SECONDS": 10, "COLLECTION_SECONDS": 2,
                   "FINALIZATION_SECONDS": 0.05, **(limits or {})}
        processes = []
        original_popen = TRACE.subprocess.Popen

        def owned_process(*args, **kwargs):
            process = original_popen(*args, **kwargs)
            processes.append(process)
            return process

        with contextlib.ExitStack() as stack:
            for name, value in patches.items():
                stack.enter_context(patch.object(TRACE, name, value))
            stack.enter_context(patch.object(TRACE.subprocess, "check_output", return_value="synthetic-head\n"))
            stack.enter_context(patch.object(TRACE.subprocess, "Popen", side_effect=owned_process))
            stack.enter_context(patch.dict(os.environ, {"PICTURE_STUB_MODE": mode}))
            if fault == "post-spawn":
                stack.enter_context(patch.object(TRACE.os, "set_blocking", side_effect=OSError("synthetic post-spawn failure")))
            elif fault == "relay":
                stack.enter_context(patch.object(TRACE.TraceRelay, "drain", side_effect=OSError("synthetic trace I/O failure")))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            stack.enter_context(contextlib.redirect_stderr(io.StringIO()))
            result = TRACE.main(["--original-exit-code", str(original_exit), "--output", str(output), "--", *command])
        directory, = output.glob("run-*")
        status = json.loads((directory / "status.json").read_text())
        self.assertEqual(original_exit, result)
        self.assertEqual(original_exit, status["originalExitCode"])
        self.assertEqual(command, status["originalCommand"])
        self.assertFalse(status["eventsValidated"])
        self.assertIn("absent samples cannot prove zero allocations", status["sampledNegativeLimitation"])
        self.assertEqual(b"caller-owned", caller_trace.read_bytes())
        self.assertTrue(all(process.poll() is not None for process in processes))
        return directory, status

    def test_successful_rerun_still_preserves_original_failure(self):
        directory, status = self.exercise(original_exit=41)
        self.assertEqual(0, status["stages"]["collection"]["exitCode"])
        self.assertEqual("synthetic-head", status["head"])
        self.assertEqual("net10.0", status["runtimeConfig"]["runtimeOptions"]["tfm"])
        self.assertEqual(b"synthetic bytes, not a real nettrace", (directory / "allocations.nettrace").read_bytes())
        self.assertEqual(64, len(status["traceSha256"]))
        self.assertEqual(status["traceCommand"], status["stages"]["collection"]["command"][-len(status["traceCommand"]):])
        self.assertIn("synthetic offline runtime", (directory / "runtime.log").read_text())

    def test_failed_rerun_does_not_replace_original_exit(self):
        _, status = self.exercise("rerun-failed", original_exit=42)
        self.assertEqual(37, status["stages"]["collection"]["exitCode"])

    def test_setup_and_version_errors_do_not_launch_benchmark(self):
        for mode in ("restore-error", "version-error"):
            with self.subTest(mode=mode):
                directory, status = self.exercise(mode)
                self.assertIn("diagnosticError", status)
                self.assertNotIn("collection", status["stages"])
                self.assertFalse((directory / "allocations.nettrace").exists())

    def test_collector_startup_error_preserves_original_failure(self):
        _, status = self.exercise("collector-error")
        self.assertEqual(29, status["stages"]["collection"]["exitCode"])
        self.assertEqual(0, status["traceBytes"])

    def test_timeout_terminates_only_owned_diagnostic_group(self):
        _, status = self.exercise("timeout", limits={"COLLECTION_SECONDS": 0.1})
        self.assertEqual("diagnostic-timeout", status["stages"]["collection"]["stopReason"])
        self.assertTrue(status["stages"]["collection"]["finalizationTimedOut"])
        self.assertLess(status["stages"]["collection"]["exitCode"], 0)

    def test_trace_storage_cap_is_explicit(self):
        directory, status = self.exercise("overflow", limits={"STOP_TRACE_BYTES": 128, "MAX_TRACE_BYTES": 256})
        self.assertEqual("trace-byte-budget", status["stages"]["collection"]["stopReason"])
        self.assertTrue(status["traceTruncated"])
        self.assertEqual(256, (directory / "allocations.nettrace").stat().st_size)

    def test_evidence_creation_failure_still_returns_original_exit(self):
        with tempfile.TemporaryDirectory(prefix="picture-offline-io-") as temporary:
            occupied = Path(temporary) / "existing-file"
            occupied.write_text("caller-owned")
            with contextlib.redirect_stderr(io.StringIO()):
                result = TRACE.main(["--original-exit-code", "53", "--output", str(occupied), "--", "synthetic-dotnet"])
            self.assertEqual(53, result)
            self.assertEqual("caller-owned", occupied.read_text())

    def test_post_spawn_setup_failure_retires_owned_process(self):
        _, status = self.exercise(fault="post-spawn")
        self.assertIn("synthetic post-spawn failure", status["diagnosticError"])

    def test_relay_failure_retains_explicit_partial_trace_metadata(self):
        _, status = self.exercise(fault="relay")
        self.assertIn("synthetic trace I/O failure", status["diagnosticError"])
        self.assertEqual(0, status["traceBytesReceived"])
        self.assertEqual(0, status["traceBytesRetained"])
        self.assertFalse(status["traceTruncated"])

    def test_hosted_shell_gate_runs_original_first_and_preserves_its_exit(self):
        source = (SCRIPT.parent / "build-progpu-native.sh").read_text()
        start = source.index('  if DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}')
        end = source.index("\n  fi", start) + len("\n  fi")
        gate = source[start:end]
        for original_exit, diagnostic_exit, hosted in ((37, 0, "true"), (37, 91, "true"),
                                                      (37, 0, "false"), (0, 91, "true")):
            with self.subTest(original=original_exit, diagnostic=diagnostic_exit, hosted=hosted):
                prelude = f'''set -euo pipefail
repo_root=/synthetic-repo
build_dir=/synthetic-build
runtime_dir=/synthetic-runtime
GITHUB_ACTIONS={hosted}
dotnet() {{ printf 'ORIGINAL %s\\n' "$*"; return {original_exit}; }}
python3() {{ printf 'DIAGNOSTIC %s\\n' "$*"; return {diagnostic_exit}; }}
'''
                result = subprocess.run(["bash", "-c", prelude + gate], capture_output=True,
                                        text=True, timeout=3)
                self.assertEqual(original_exit, result.returncode)
                lines = result.stdout.splitlines()
                self.assertTrue(lines[0].startswith("ORIGINAL run --project "))
                self.assertIn("-c Release -- --managed-picture --rectangles 384 --warmup 4 --iterations 8", lines[0])
                self.assertEqual(2 if hosted == "true" and original_exit else 1, len(lines))


if __name__ == "__main__":
    unittest.main()
