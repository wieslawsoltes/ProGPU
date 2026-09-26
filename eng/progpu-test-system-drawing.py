#!/usr/bin/env python3
"""Run the unchanged Drawing gate with testhost-only, failure-retained EventPipe."""

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
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
# The runtime providers already used by tools/profile-sample-memory.sh. Allocation
# events are sampled: absence of a stack is not evidence of zero allocations.
PROVIDERS = "Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x8000300201b:5"
MAX_TRACE_BYTES = 128 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024
STOP_TRACE_BYTES = 96 * 1024 * 1024
COLLECTOR_VERSION = "9.0.661903"
PROFILER_PROJECT = ROOT / "tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj"
PROFILER_DLL = ROOT / "tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"
LIFETIME_PROJECT = ROOT / "tools/ProGPU.TestTraceLifetime/ProGPU.TestTraceLifetime.csproj"
LIFETIME_DLL = ROOT / "tools/ProGPU.TestTraceLifetime/bin/Release/net10.0/ProGPU.TestTraceLifetime.dll"


def diagnostic_print(message, error=False):
    try:
        print(message, file=sys.stderr if error else sys.stdout, flush=True)
    except (OSError, ValueError):
        pass


def settings(directory, socket):
    root = ET.Element("RunSettings")
    variables = ET.SubElement(ET.SubElement(root, "RunConfiguration"), "EnvironmentVariables")
    values = {"DOTNET_DiagnosticPorts": f"{socket},connect,nosuspend",
              "PROGPU_TEST_TRACE_LIFETIME": str(directory)}
    for name, value in values.items():
        ET.SubElement(variables, name).text = value
    collectors = ET.SubElement(ET.SubElement(root, "InProcDataCollectionRunSettings"), "InProcDataCollectors")
    ET.SubElement(collectors, "InProcDataCollector", {
        "friendlyName": "ProGPU trace lifetime", "enabled": "True",
        "uri": "InProcDataCollector://ProGPU/TraceLifetime/1.0", "codebase": str(LIFETIME_DLL),
        "assemblyQualifiedName": "ProGPU.Diagnostics.SessionTraceLifetime, ProGPU.TestTraceLifetime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"})
    path = directory / "allocation.runsettings"
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
    return path


def finish_traces(directory, child_status, verified=False):
    records = []
    retained = 0
    for path in sorted(directory.glob("testhost-*.nettrace")):
        record = {"name": path.name}
        if path.is_symlink() or not path.is_file():
            raise RuntimeError(f"Unexpected trace entry: {path}")
        size = path.stat().st_size
        record["bytes"] = size
        if 0 < size <= MAX_TRACE_BYTES:
            with path.open("rb") as stream:
                record["sha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
        if child_status == 0 and verified:
            record["disposition"] = "removed-after-success"
            path.unlink()
        elif size == 0 or size > MAX_TRACE_BYTES or retained + size > MAX_TOTAL_BYTES:
            record["disposition"] = "removed-empty-or-over-budget"
            path.unlink()
        else:
            record["disposition"] = "retained-after-failure" if child_status else "retained-after-diagnostic-failure"
            retained += size
        records.append(record)
    return records


class TraceRelay:
    """Bound only the owned trace file, never the runtime's memory-backed files."""

    def __init__(self, fifo, output):
        os.mkfifo(fifo, 0o600)
        self.reader = os.open(fifo, os.O_RDONLY | os.O_NONBLOCK)
        try:
            self.output = output.open("xb", buffering=0)
        except Exception:
            os.close(self.reader)
            raise
        self.received = 0
        self.written = 0

    def drain(self):
        # A busy writer must not starve test polling or finalization deadlines.
        drained = 0
        deadline = time.monotonic() + 0.02
        while drained < 4 * 1024 * 1024 and time.monotonic() < deadline:
            try:
                chunk = os.read(self.reader, 64 * 1024)
            except BlockingIOError:
                # Give the blocked writer a scheduling opportunity without
                # making throughput depend on one pipe buffer per test poll.
                select.select([self.reader], [], [], min(0.002, max(0, deadline - time.monotonic())))
                continue
            if not chunk:
                # Before the collector opens its end this is expected, not EOF
                # of the eventual EventPipe stream. Only collector exit ends it.
                break
            drained += len(chunk)
            self.received += len(chunk)
            retained = chunk[:max(0, MAX_TRACE_BYTES - self.written)]
            offset = 0
            while offset < len(retained):
                written = self.output.write(memoryview(retained)[offset:])
                if not written:
                    raise OSError("Trace writer made no progress.")
                offset += written
                self.written += written
            # Overflow is drained/discarded so collection can stop cleanly. It
            # always fails diagnostics; it is never parsed as complete evidence.
        return drained

    def close(self):
        os.close(self.reader)
        self.output.close()


def stop_collector(collector):
    if collector.poll() is None:
        # stdin Enter is disabled by dotnet-trace when stdin is redirected.
        # Its supported Ctrl+C handler stops only the attached collection session.
        os.killpg(collector.pid, signal.SIGINT)


def finalize_collector(collector, relay=None, deadline=None):
    if deadline is None:
        deadline = time.monotonic() + 30
    stop_collector(collector)
    if relay is not None:
        while True:
            drained = relay.drain()
            if collector.poll() is not None and drained == 0:
                return collector.returncode
            if time.monotonic() >= deadline:
                if collector.poll() is None:
                    os.killpg(collector.pid, signal.SIGKILL)
                    collector.wait(timeout=5)
                raise RuntimeError("Collector failed to finalize/drain within 30 seconds; testhost was not terminated.")
            time.sleep(0.01)
    try:
        return collector.wait(timeout=max(0, deadline - time.monotonic()))
    except subprocess.TimeoutExpired:
        # This is the separately created collector group, not the test command.
        os.killpg(collector.pid, signal.SIGKILL)
        collector.wait(timeout=5)
        raise RuntimeError("Collector failed to finalize within 30 seconds; testhost was not terminated.")


def run(dotnet, output):
    output.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="run-", dir=output)).resolve()
    # Keep the private Unix socket path below the platform's length limit even
    # when the repository/evidence directory has a long checkout path.
    socket_directory = tempfile.TemporaryDirectory(prefix="pg-drawing-", dir="/tmp")
    socket = Path(socket_directory.name) / "testhost.sock"
    runsettings = settings(directory, socket)
    trace = directory / "testhost-capture.nettrace"
    command = [dotnet, "test", "src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj",
               "--configuration", "Release", "--verbosity", "normal", "--settings", str(runsettings),
               "--logger", "trx;LogFileName=quality.trx", "--results-directory", str(directory)]
    status = {"schemaVersion": 2, "command": command, "providers": PROVIDERS,
              "circularBufferMiB": 64, "externalCollectorVersion": COLLECTOR_VERSION,
              "testExitCode": None, "traces": []}
    diagnostic_print(f"[DrawingTrace] Evidence: {directory}")
    with (directory / "dotnet-info.txt").open("w") as stream:
        # Bounded metadata capture only; this does not alter the test deadline.
        subprocess.run([dotnet, "--info"], cwd=ROOT, stdout=stream,
                       stderr=subprocess.STDOUT, timeout=30, check=True)
    child_status = None
    child = None
    collector = None
    collector_log = None
    relay = None
    collector_deadline = None
    try:
        with (directory / "collector-setup.log").open("w") as setup:
            subprocess.run([dotnet, "tool", "restore"], cwd=ROOT, stdout=setup,
                           stderr=subprocess.STDOUT, timeout=120, check=True)
            version = subprocess.check_output([dotnet, "tool", "run", "dotnet-trace", "--", "--version"],
                                              cwd=ROOT, text=True, timeout=30).strip()
            status["collectorVersionOutput"] = version
            if version.split("+", 1)[0] != COLLECTOR_VERSION:
                raise RuntimeError(f"Unexpected collector version: {version}")
            subprocess.run([dotnet, "build", str(PROFILER_PROJECT), "--configuration", "Release", "--verbosity", "quiet"],
                           cwd=ROOT, stdout=setup, stderr=subprocess.STDOUT, timeout=120, check=True)
            subprocess.run([dotnet, "build", str(LIFETIME_PROJECT), "--configuration", "Release", "--verbosity", "quiet"],
                           cwd=ROOT, stdout=setup, stderr=subprocess.STDOUT, timeout=120, check=True)
        fifo = Path(socket_directory.name) / "capture.fifo"
        relay = TraceRelay(fifo, trace)
        collector_command = [dotnet, "tool", "run", "dotnet-trace", "--", "collect", "--diagnostic-port", str(socket),
                             "--buffersize", "64", "--providers", PROVIDERS, "--output", str(fifo)]
        status["collectorCommand"] = collector_command
        collector_log = (directory / "collector.log").open("wb")
        collector = subprocess.Popen(collector_command, cwd=ROOT, stdout=collector_log,
                                     stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                                     start_new_session=True)
        ready_deadline = time.monotonic() + 15
        while not socket.exists():
            relay.drain()
            if collector.poll() is not None:
                status["collectorExitCode"] = collector.returncode
                raise RuntimeError(f"Collector exited {collector.returncode} before creating its private diagnostic socket.")
            if time.monotonic() >= ready_deadline:
                raise RuntimeError("Collector did not create its private diagnostic socket within 15 seconds.")
            time.sleep(0.05)
        # No shell, pipeline, retry, filter, warmup, JIT or parallelism override.
        # Only testhost sees the private diagnostic port; official dotnet-trace
        # completes the reverse-port handshake. Testhost is never suspended:
        # collector failure must not prevent it from running its original tests.
        with (directory / "quality.log").open("wb") as log:
            with subprocess.Popen(command, cwd=ROOT, stdout=log,
                                  stderr=subprocess.STDOUT, start_new_session=True) as child:
                previous = {}

                def forward(signum, _frame):
                    # Only the process group created by this invocation is owned.
                    try:
                        os.killpg(child.pid, signum)
                    except ProcessLookupError:
                        pass

                try:
                    for signum in (signal.SIGTERM, signal.SIGINT):
                        previous[signum] = signal.signal(signum, forward)
                    with (directory / "quality.log").open("rb") as reader:
                        while child.poll() is None:
                            relay.drain()
                            sys.stdout.buffer.write(reader.read())
                            sys.stdout.buffer.flush()
                            if collector_deadline is None and (directory / "testhost-session-ended").exists():
                                status["collectorStopReason"] = "test-session-end"
                                collector_deadline = time.monotonic() + 30
                                stop_collector(collector)
                            if relay.received >= STOP_TRACE_BYTES:
                                status["diagnosticError"] = "Trace reached the 96-MiB collection-stop budget."
                                if collector_deadline is None:
                                    collector_deadline = time.monotonic() + 30
                                    stop_collector(collector)
                            if collector_deadline is not None and collector.poll() is None and time.monotonic() >= collector_deadline:
                                os.killpg(collector.pid, signal.SIGKILL)
                                collector.wait(timeout=5)
                                status["diagnosticError"] = "Collector exceeded its 30-second finalization budget."
                            if collector.poll() is not None and (directory / "testhost-session-ended").exists():
                                acknowledged = directory / "collector-finalized"
                                if not acknowledged.exists():
                                    acknowledged.write_text("collector process exited; strict parsing follows\n")
                            time.sleep(0.1)
                        sys.stdout.buffer.write(reader.read())
                        sys.stdout.buffer.flush()
                    child_status = child.wait()
                finally:
                    for signum, handler in previous.items():
                        signal.signal(signum, handler)
        # Shell-compatible signal exit, otherwise preserve the exact child code.
        status["testExitCode"] = child_status if child_status >= 0 else 128 - child_status
        if collector_deadline is None:
            collector_deadline = time.monotonic() + 30
        status["collectorExitCode"] = finalize_collector(collector, relay, collector_deadline)
        if relay.received >= STOP_TRACE_BYTES:
            status["diagnosticError"] = "Trace reached the 96-MiB collection-stop budget."
        if status["collectorExitCode"] != 0:
            status["diagnosticError"] = f"Collector exited {status['collectorExitCode']}."
        if not trace.is_file() or trace.stat().st_size == 0 or trace.stat().st_size > MAX_TRACE_BYTES:
            status["diagnosticError"] = "Trace is missing, empty or over the hard evidence budget."
        elif "diagnosticError" not in status:
            with (directory / "trace-validation.log").open("w") as validation:
                verified = subprocess.run([dotnet, str(PROFILER_DLL), "verify-drawing-trace", str(trace),
                                           str(directory / "trace-validation.json")], cwd=ROOT, stdout=validation,
                                          stderr=subprocess.STDOUT, timeout=60).returncode
            if verified != 0:
                status["diagnosticError"] = f"Strict complete-trace validation failed with exit {verified}."
            else:
                report = json.loads((directory / "trace-validation.json").read_text())
                pid = int((directory / "testhost-session-ended").read_text())
                status["testhostProcessId"] = pid
                if status.get("collectorStopReason") != "test-session-end" or pid <= 0 or report.get("processId") != pid:
                    status["diagnosticError"] = "Parsed trace did not match the actual testhost lifetime handshake."
    except Exception as error:
        status["diagnosticError"] = str(error)
        # Popen.__exit__ waits even if diagnostic polling/output throws. Preserve
        # the returncode it observed, rather than replacing the test failure.
        if child is not None and child.returncode is not None:
            child_status = child.returncode
        if child_status is not None:
            status["testExitCode"] = child_status if child_status >= 0 else 128 - child_status
    finally:
        if collector is not None and collector.poll() is None:
            try:
                if collector_deadline is None:
                    collector_deadline = time.monotonic() + 30
                finalize_collector(collector, relay, collector_deadline)
            except Exception as error:
                status["diagnosticError"] = str(error)
        try:
            if collector is not None:
                status["collectorExitCode"] = collector.returncode
            if relay is not None:
                status["traceBytesReceived"] = relay.received
                status["traceBytesDiscarded"] = relay.received - relay.written
                relay.close()
            if collector_log is not None:
                collector_log.close()
            socket_directory.cleanup()
            status["traces"] = finish_traces(directory, child_status, verified="diagnosticError" not in status)
        except Exception as error:
            # Even a diagnostic I/O failure cannot replace an observed test failure.
            status["diagnosticError"] = str(error)
            diagnostic_print(f"[DrawingTrace] Evidence finalization failed: {error}", error=True)
        try:
            (directory / "status.json").write_text(json.dumps(status, indent=2) + "\n")
        except Exception as error:
            status["diagnosticError"] = str(error)
            diagnostic_print(f"[DrawingTrace] Status write failed: {error}", error=True)
    diagnostic_print(f"[DrawingTrace] Test exit: {status['testExitCode']}; evidence: {directory}")
    # Diagnostics must never hide or replace a failing gate's exit status.
    if status["testExitCode"]:
        return status["testExitCode"]
    return 2 if "diagnosticError" in status else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/system-drawing-quality")
    args = parser.parse_args()
    try:
        return run(args.dotnet, args.output)
    except (OSError, subprocess.SubprocessError) as error:
        diagnostic_print(f"[DrawingTrace] Setup failed: {error}", error=True)
        return 2


if __name__ == "__main__":
    result = main()
    for name in ("stdout", "stderr"):
        try:
            getattr(sys, name).flush()
        except (OSError, ValueError):
            # Avoid Python's final buffered-I/O flush replacing the child code.
            setattr(sys, name, open(os.devnull, "w"))
    sys.exit(result)
