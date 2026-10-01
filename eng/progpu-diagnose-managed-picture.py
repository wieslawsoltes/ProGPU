#!/usr/bin/env python3
"""Bounded EventPipe rerun after the original managed-picture gate has failed."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import select
import signal
import subprocess
import sys
import tempfile
import time


ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj"
APP = PROJECT.parent / "bin/Release/net10.0/ProGPU.Native.Benchmarks.dll"
ARGUMENTS = ["--managed-picture", "--rectangles", "384", "--warmup", "4", "--iterations", "8"]
COLLECTOR_VERSION = "9.0.661903"  # .config/dotnet-tools.json
# Same sampled allocation/type/managed-stack providers as the existing memory profiler.
PROVIDERS = "Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x8000300201b:5"
TOTAL_SECONDS = 180
COLLECTION_SECONDS = 90
FINALIZATION_SECONDS = 10
MAX_TRACE_BYTES = 64 * 1024 * 1024
STOP_TRACE_BYTES = 48 * 1024 * 1024
MAX_LOG_BYTES = 4 * 1024 * 1024
LIMITATION = (
    "This is an instrumented rerun, not the original failed measurement. Allocation events "
    "and managed stacks are sampled; absent samples cannot prove zero allocations or "
    "exclude a 12,336-byte spike. Raw trace events/stacks have not been validated. A timeout, "
    "collector failure, lost events or truncation can leave incomplete/unresolved evidence. "
    "No rerun outcome changes the original gate failure."
)


class TraceRelay:
    """Adapted from ProGPU-owned eng/progpu-test-system-drawing.py's bounded relay."""

    def __init__(self, fifo, trace):
        os.mkfifo(fifo, 0o600)
        self.reader = os.open(fifo, os.O_RDONLY | os.O_NONBLOCK)
        try:
            self.output = trace.open("xb", buffering=0)
        except Exception:
            os.close(self.reader)
            raise
        self.received = 0
        self.written = 0

    def drain(self):
        drained = 0
        deadline = time.monotonic() + 0.02
        while drained < 2 * 1024 * 1024 and time.monotonic() < deadline:
            try:
                chunk = os.read(self.reader, 64 * 1024)
            except BlockingIOError:
                select.select([self.reader], [], [], 0.001)
                continue
            if not chunk:
                break  # The collector may not have opened its writer yet.
            drained += len(chunk)
            self.received += len(chunk)
            retained = memoryview(chunk)[:max(0, MAX_TRACE_BYTES - self.written)]
            while retained:
                count = self.output.write(retained)
                if not count:
                    raise OSError("Trace writer made no progress.")
                self.written += count
                retained = retained[count:]
            # Drain excess bytes so the collector can stop; mark them explicitly.
        return drained

    def close(self):
        os.close(self.reader)
        self.output.close()


def signal_group(process, signum):
    try:
        os.killpg(process.pid, signum)
    except ProcessLookupError:
        pass


def run_owned(command, log_path, deadline, relay=None):
    """Bound setup/collector time and output, cleaning only this invocation's group."""
    record = {"command": command, "exitCode": None, "logBytes": 0}
    stop_deadline = None
    with log_path.open("xb") as log:
        process = subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                   start_new_session=True)
        previous = {}
        try:
            os.set_blocking(process.stdout.fileno(), False)
            def interrupted(signum, _frame):
                signal_group(process, signum)
                raise KeyboardInterrupt(f"Diagnostic interrupted by signal {signum}.")

            for signum in (signal.SIGINT, signal.SIGTERM):
                previous[signum] = signal.signal(signum, interrupted)
            while True:
                chunk = process.stdout.read(64 * 1024)
                if chunk:
                    remaining = max(0, MAX_LOG_BYTES - log.tell())
                    log.write(chunk[:remaining])
                    record["logBytes"] += len(chunk)
                drained = relay.drain() if relay is not None else 0
                if relay is not None and relay.received >= STOP_TRACE_BYTES and stop_deadline is None:
                    record["stopReason"] = "trace-byte-budget"
                    stop_deadline = time.monotonic() + FINALIZATION_SECONDS
                    signal_group(process, signal.SIGINT)
                if process.poll() is not None and not chunk and not drained:
                    record["exitCode"] = process.returncode
                    break
                now = time.monotonic()
                if now >= deadline and stop_deadline is None:
                    record["stopReason"] = "diagnostic-timeout"
                    stop_deadline = now + FINALIZATION_SECONDS
                    signal_group(process, signal.SIGINT)
                if stop_deadline is not None and now >= stop_deadline:
                    signal_group(process, signal.SIGKILL)
                    process.wait(timeout=5)
                    record["exitCode"] = process.returncode
                    record["finalizationTimedOut"] = True
                    break
                time.sleep(0.01)
        finally:
            # Includes a diagnostic benchmark left alive by an early collector exit.
            signal_group(process, signal.SIGKILL)
            process.wait(timeout=5)
            process.stdout.close()
            for signum, handler in previous.items():
                signal.signal(signum, handler)
    record["logTruncated"] = record["logBytes"] > MAX_LOG_BYTES
    return record


def diagnose(output, original_exit, original_command):
    output.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="run-", dir=output)).resolve()
    deadline = time.monotonic() + TOTAL_SECONDS
    status = {"schemaVersion": 1, "originalExitCode": original_exit,
              "originalCommand": original_command, "benchmarkArguments": ARGUMENTS,
              "traceCommand": [original_command[0], "exec", str(APP), *ARGUMENTS],
              "providers": PROVIDERS, "collectorVersion": COLLECTOR_VERSION,
              "totalBudgetSeconds": TOTAL_SECONDS, "maxTraceBytes": MAX_TRACE_BYTES,
              "finalizationGraceSeconds": FINALIZATION_SECONDS, "terminationWaitSeconds": 5,
              "sampledNegativeLimitation": LIMITATION, "eventsValidated": False,
              "stages": {}}
    print(f"[ManagedPictureDiagnostic] Evidence: {directory}", flush=True)
    trace = directory / "allocations.nettrace"
    relay = None
    try:
        if original_command[1:] != ["run", "--project", str(PROJECT), "-c", "Release", "--", *ARGUMENTS]:
            raise ValueError("Diagnostic requires the unchanged managed-picture Release command.")
        status["head"] = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT,
                                                text=True, timeout=5).strip()
        status["nativeBuildDirectory"] = os.environ.get("PROGPU_NATIVE_BUILD_DIR")
        status["dynamicLibraryPath"] = os.environ.get("DYLD_LIBRARY_PATH")
        dotnet = original_command[0]
        for name, command, seconds in (
            ("runtime", [dotnet, "--info"], 15),
            ("restore", [dotnet, "tool", "restore"], 60),
            ("version", [dotnet, "tool", "run", "dotnet-trace", "--", "--version"], 15),
        ):
            record = run_owned(command, directory / f"{name}.log", min(deadline, time.monotonic() + seconds))
            status["stages"][name] = record
            if record["exitCode"] != 0 or "stopReason" in record:
                raise RuntimeError(f"Diagnostic {name} failed: {record}.")
        version = (directory / "version.log").read_text().strip()
        status["collectorVersionOutput"] = version
        if version.split("+", 1)[0] != COLLECTOR_VERSION:
            raise RuntimeError(f"Unexpected collector version: {version}.")
        if not APP.is_file():
            raise RuntimeError(f"Original Release benchmark output is missing: {APP}.")
        status["runtimeConfig"] = json.loads(APP.with_suffix(".runtimeconfig.json").read_text())
        with APP.open("rb") as stream:
            status["benchmarkSha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
        # Tracing `dotnet run` can select the CLI instead of the benchmark. Launch
        # its already-built DLL directly with exactly the original benchmark args.
        with tempfile.TemporaryDirectory(prefix="pg-picture-", dir="/tmp") as temporary:
            fifo = Path(temporary) / "capture.fifo"
            relay = TraceRelay(fifo, trace)
            command = [dotnet, "tool", "run", "dotnet-trace", "--", "collect",
                       "--show-child-io", "--buffersize", "64", "--providers", PROVIDERS,
                       "--output", str(fifo), "--", *status["traceCommand"]]
            status["stages"]["collection"] = run_owned(
                command, directory / "collector-and-benchmark.log",
                min(deadline, time.monotonic() + COLLECTION_SECONDS), relay)
    except (Exception, KeyboardInterrupt) as error:
        status["diagnosticError"] = str(error)
        print(f"[ManagedPictureDiagnostic] {error}", file=sys.stderr, flush=True)
    finally:
        if relay is not None:
            status["traceBytesReceived"] = relay.received
            status["traceBytesRetained"] = relay.written
            status["traceTruncated"] = relay.received > relay.written
            relay.close()
        if trace.is_file():
            status["traceBytes"] = trace.stat().st_size
            with trace.open("rb") as stream:
                status["traceSha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
        (directory / "status.json").write_text(json.dumps(status, indent=2) + "\n")
        (directory / "README.txt").write_text(
            LIMITATION + "\n\nRetain allocations.nettrace for allocation type and managed stack analysis.\n"
            "The runtime/collector logs and status.json retain head, runtime and both commands.\n"
            "https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace\n"
            "https://github.com/dotnet/runtime/blob/main/docs/design/features/RandomizedAllocationSampling.md\n")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--original-exit-code", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    if not 1 <= args.original_exit_code <= 255:
        parser.error("A nonzero original gate exit code is required.")
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    try:
        if not command:
            raise ValueError("The original command is required.")
        diagnose(args.output, args.original_exit_code, command)
    except (Exception, KeyboardInterrupt) as error:
        print(f"[ManagedPictureDiagnostic] Could not retain diagnostics: {error}", file=sys.stderr)
    return args.original_exit_code


if __name__ == "__main__":
    sys.exit(main())
