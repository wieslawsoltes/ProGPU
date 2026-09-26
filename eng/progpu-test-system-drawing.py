#!/usr/bin/env python3
"""Run the unchanged Drawing gate with testhost-only, failure-retained EventPipe."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
# The runtime providers already used by tools/profile-sample-memory.sh. Allocation
# events are sampled: absence of a stack is not evidence of zero allocations.
PROVIDERS = "Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x8000300201b:5"
MAX_TRACE_BYTES = 128 * 1024 * 1024
MAX_TOTAL_BYTES = 256 * 1024 * 1024


def settings(directory):
    root = ET.Element("RunSettings")
    variables = ET.SubElement(ET.SubElement(root, "RunConfiguration"), "EnvironmentVariables")
    values = {
        "DOTNET_EnableEventPipe": "1",
        "DOTNET_EventPipeOutputPath": str(directory / "testhost-{pid}.nettrace"),
        "DOTNET_EventPipeConfig": PROVIDERS,
        # The runtime interprets this number as hexadecimal: 0x40 = 64 MiB.
        "DOTNET_EventPipeCircularMB": "40",
        # Bounded in-memory collection, flushed on normal runtime shutdown.
        "DOTNET_EventPipeOutputStreaming": "0",
    }
    for name, value in values.items():
        ET.SubElement(variables, name).text = value
    path = directory / "allocation.runsettings"
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)
    return path


def finish_traces(directory, child_status):
    records = []
    retained = 0
    for path in sorted(directory.glob("testhost-*.nettrace")):
        record = {"name": path.name}
        if path.is_symlink() or not path.is_file():
            raise RuntimeError(f"Unexpected trace entry: {path}")
        size = path.stat().st_size
        record["bytes"] = size
        if child_status == 0:
            record["disposition"] = "removed-after-success"
            path.unlink()
        elif size == 0 or size > MAX_TRACE_BYTES or retained + size > MAX_TOTAL_BYTES:
            record["disposition"] = "removed-empty-or-over-budget"
            path.unlink()
        else:
            record["disposition"] = "retained-after-failure"
            with path.open("rb") as stream:
                record["sha256"] = hashlib.file_digest(stream, "sha256").hexdigest()
            retained += size
        records.append(record)
    return records


def run(dotnet, output):
    output.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="run-", dir=output)).resolve()
    runsettings = settings(directory)
    command = [dotnet, "test", "src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj",
               "--configuration", "Release", "--verbosity", "normal", "--settings", str(runsettings),
               "--logger", "trx;LogFileName=quality.trx", "--results-directory", str(directory)]
    status = {"schemaVersion": 1, "command": command, "providers": PROVIDERS,
              "circularBufferMiB": 64, "outputStreaming": False,
              "testExitCode": None, "traces": []}
    print(f"[DrawingTrace] Evidence: {directory}", flush=True)
    with (directory / "dotnet-info.txt").open("w") as stream:
        # Bounded metadata capture only; this does not alter the test deadline.
        subprocess.run([dotnet, "--info"], cwd=ROOT, stdout=stream,
                       stderr=subprocess.STDOUT, timeout=30, check=True)
    child_status = None
    try:
        # No shell, pipeline, retry, filter, warmup, JIT or parallelism override.
        # Runsettings scopes EventPipe to testhost, not restore/MSBuild/VSTest.
        with (directory / "quality.log").open("wb") as log:
            with subprocess.Popen(command, cwd=ROOT, stdout=subprocess.PIPE,
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
                    for line in child.stdout:
                        log.write(line)
                        log.flush()
                        sys.stdout.buffer.write(line)
                        sys.stdout.buffer.flush()
                    child_status = child.wait()
                finally:
                    for signum, handler in previous.items():
                        signal.signal(signum, handler)
        # Shell-compatible signal exit, otherwise preserve the exact child code.
        status["testExitCode"] = child_status if child_status >= 0 else 128 - child_status
        status["traces"] = finish_traces(directory, child_status)
        if not status["traces"]:
            status["diagnosticError"] = "No testhost trace was produced (a crash may prevent buffer flush)."
    except Exception as error:
        status["diagnosticError"] = str(error)
        if child_status is not None:
            status["testExitCode"] = child_status if child_status >= 0 else 128 - child_status
    finally:
        (directory / "status.json").write_text(json.dumps(status, indent=2) + "\n")
    print(f"[DrawingTrace] Test exit: {status['testExitCode']}; evidence: {directory}", flush=True)
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
        print(f"[DrawingTrace] Setup failed: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
