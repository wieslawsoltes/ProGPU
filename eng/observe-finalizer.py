"""Observe one unchanged Linux command; capture at most four slow collector stacks.

Diagnostic only. This does not alter the original test's five-second join or
make a successful instrumented execution qualification for a failed run.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

TRIGGER_SECONDS = 2.0
STACK_SECONDS = 30.0
MAX_CAPTURES = 4


def identity(pid):
    try:
        # comm may contain spaces or parentheses; fields after the final ')'
        # start at state (field 3), making starttime field 22 index 19.
        fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
        return int(fields[19])
    except (FileNotFoundError, ProcessLookupError):
        return None


def descendants(root):
    result, pending = [], [root]
    while pending:
        pid = pending.pop()
        if pid in result:
            continue
        result.append(pid)
        # A process may be created by any native thread (as in VSTest).
        for task in Path(f'/proc/{pid}/task').glob('*'):
            try:
                pending.extend(int(p) for p in (task / 'children').read_text().split())
            except (FileNotFoundError, ProcessLookupError):
                pass
    return result


def thread_snapshot(pid):
    result = []
    for path in sorted(Path(f'/proc/{pid}/task').glob('*')):
        row = {'tid': int(path.name), 'starttime': identity(path.name)}
        for name in ['comm', 'wchan', 'status', 'stack']:
            try:
                row[name] = (path / name).read_text()
            except OSError as error:
                row[name + 'Error'] = str(error)
        result.append(row)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--stack-tool', required=True)
    parser.add_argument('--thread-name-file', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--host-dll', default='testhost.dll')
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    assert sys.platform == 'linux'
    command = args.command
    assert command[0] == '--' and len(command) > 1
    command = command[1:]
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=False)
    name_file = Path(args.thread_name_file)
    record = dict(startedAt=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  command=command, diagnosticOnly=True, qualified=False,
                  originalTestInputsAndDeadlinesUnchanged=True,
                  triggerSeconds=TRIGGER_SECONDS, stackDeadlineSeconds=STACK_SECONDS,
                  maxCaptures=MAX_CAPTURES, observations=[], captures=[])
    observed, captured, active = {}, set(), []
    process = subprocess.Popen(command, stdin=subprocess.DEVNULL, start_new_session=True)
    root_identity = identity(process.pid)
    record.update(rootPid=process.pid, rootStarttime=root_identity)
    pending_error = None
    try:
        while process.poll() is None:
            now = time.monotonic()
            current = set()
            if name_file.exists() and identity(process.pid) == root_identity:
                thread_name = name_file.read_text().strip()
                assert thread_name and '\n' not in thread_name
                for pid in descendants(process.pid):
                    try:
                        cmdline = Path(f'/proc/{pid}/cmdline').read_bytes().split(b'\0')
                        if not any(Path(os.fsdecode(arg)).name == args.host_dll for arg in cmdline if arg):
                            continue
                        starttime = identity(pid)
                        for task in Path(f'/proc/{pid}/task').glob('*'):
                            try:
                                if (task / 'comm').read_text().strip() != thread_name:
                                    continue
                                key = (pid, starttime, int(task.name), identity(task.name))
                            except (FileNotFoundError, ProcessLookupError):
                                continue
                            current.add(key)
                            if key not in observed:
                                observed[key] = now
                                record['observations'].append(dict(pid=pid, processStarttime=starttime,
                                    tid=int(task.name), threadStarttime=key[3], comm=thread_name, firstSeenMonotonic=now))
                            age = now - observed[key]
                            if age < TRIGGER_SECONDS or key in captured or len(captured) >= MAX_CAPTURES:
                                continue
                            # Revalidate ownership and identity immediately before attaching.
                            if identity(pid) != starttime or pid not in descendants(process.pid):
                                continue
                            captured.add(key)
                            index = len(captured)
                            snapshot = thread_snapshot(pid)
                            (output / f'{index}-proc-threads.json').write_text(json.dumps(snapshot, indent=2) + '\n')
                            capture = dict(pid=pid, processStarttime=starttime, collectorTid=int(task.name),
                                threadStarttime=key[3], observedAgeSeconds=age, startedMonotonic=time.monotonic())
                            log = (output / f'{index}-managed-stacks.txt').open('w')
                            tool = subprocess.Popen([args.stack_tool, 'report', '--process-id', str(pid)],
                                stdin=subprocess.DEVNULL, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
                            active.append((tool, log, capture))
                            record['captures'].append(capture)
                    except (FileNotFoundError, ProcessLookupError):
                        pass
            for key in list(observed):
                if key not in current:
                    del observed[key]
            for tool, log, capture in active:
                if 'exitCode' in capture:
                    continue
                if tool.poll() is None and now - capture['startedMonotonic'] > STACK_SECONDS:
                    os.killpg(tool.pid, signal.SIGKILL)
                    capture['timedOut'] = True
                if tool.poll() is not None:
                    capture.update(exitCode=tool.returncode, completedMonotonic=time.monotonic())
                    log.close()
            time.sleep(0.1)
    except BaseException as error:
        # A diagnostic failure must not terminate or replace the original suite.
        pending_error = repr(error)
        record['observerError'] = pending_error
        process.wait()
    finally:
        record['testExitCode'] = process.wait()
        for tool, log, capture in active:
            if tool.poll() is None:
                remaining = max(0.01, STACK_SECONDS - (time.monotonic() - capture['startedMonotonic']))
                try:
                    tool.wait(timeout=remaining)
                except subprocess.TimeoutExpired:
                    os.killpg(tool.pid, signal.SIGKILL)
                    tool.wait(timeout=5)
                    capture['timedOut'] = True
            capture.update(exitCode=tool.returncode, completedMonotonic=time.monotonic())
            log.close()
        record['completedAt'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        (output / 'receipt.json').write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps(record), flush=True)
    return record['testExitCode'] or (2 if pending_error else 0)


if __name__ == '__main__':
    sys.exit(main())
