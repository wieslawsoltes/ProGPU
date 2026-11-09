#!/usr/bin/env python3
"""Bounded Linux observation of this browser test's descendant GPU process.

Used only by a separate failure diagnostic, never by the original browser gate.
No driver, compiler, browser flag, shader, watchdog or deadline is changed. GDB
attachment pauses the target briefly, so its result is explicitly diagnostic.
No core files, heap dumps, environment variables or user browser data are read.
"""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time


def process_stat(pid, root=Path('/proc')):
    """Read identity and CPU counters, allowing spaces/parentheses in comm."""
    text = (root / str(pid) / 'stat').read_text()
    begin, end = text.index('('), text.rindex(')')
    fields = text[end + 2:].split()
    return dict(pid=int(text[:begin].strip()), name=text[begin + 1:end],
                state=fields[0], parent=int(fields[1]), user_ticks=int(fields[11]),
                system_ticks=int(fields[12]), start_ticks=int(fields[19]))


def descendant(pid, parent, root=Path('/proc')):
    seen = set()
    while pid != parent:
        if pid <= 1 or pid in seen:
            return False
        seen.add(pid)
        try:
            pid = process_stat(pid, root)['parent']
        except (OSError, ValueError, IndexError):
            return False
    return True


def gpu_processes(parent, root=Path('/proc')):
    result = []
    for directory in root.iterdir():
        if not directory.name.isdecimal():
            continue
        try:
            args = (directory / 'cmdline').read_bytes().split(b'\0')
            if b'--type=gpu-process' not in args:
                continue
            pid = int(directory.name)
            if descendant(pid, parent, root):
                result.append(process_stat(pid, root))
        except (OSError, ValueError, IndexError):
            continue  # Concurrent child exit is an observation, not a test result.
    return sorted(result, key=lambda value: value['pid'])


def same_generation(source, parent):
    try:
        current = process_stat(source['pid'])
        return current['start_ticks'] == source['start_ticks'] and descendant(source['pid'], parent)
    except (OSError, ValueError, IndexError):
        return False


def sample(source, parent):
    if not same_generation(source, parent):
        return dict(retired=True)
    directory = Path('/proc') / str(source['pid'])
    threads = []
    for task in (directory / 'task').iterdir():
        try:
            value = process_stat(int(task.name), directory / 'task')
            value['wait_channel'] = (task / 'wchan').read_text().strip()
            threads.append(value)
        except (OSError, ValueError, IndexError):
            continue
    return dict(process=process_stat(source['pid']), threads=threads)


def backtrace(source, parent):
    if not same_generation(source, parent):
        return dict(status='retired before attachment')
    gdb, timeout = shutil.which('gdb'), shutil.which('timeout')
    if not gdb or not timeout:
        return dict(status='observation unavailable', reason='gdb or timeout is not installed')
    command = [timeout, '--signal=TERM', '--kill-after=1s', '5s', gdb, '--nx', '--nh', '--batch',
               '-iex', 'set auto-load off', '-iex', 'set debuginfod enabled off',
               '-ex', 'set pagination off', '-ex', 'set print thread-events off',
               '-ex', f'attach {source["pid"]}', '-ex', 'info sharedlibrary',
               '-ex', 'thread apply all bt 24', '-ex', 'detach']
    if os.geteuid() != 0:
        sudo = shutil.which('sudo')
        if not sudo:
            return dict(status='observation unavailable', reason='noninteractive sudo is unavailable')
        command = [sudo, '-n', *command]
    started = time.monotonic()
    # The root-owned timeout bounds the debugger even if its caller is retired.
    # Capture only a bounded text prefix; no separate large trace is retained.
    with tempfile.TemporaryFile() as output:
        try:
            process = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=output,
                                     stderr=subprocess.STDOUT, timeout=8, check=False)
            status = dict(exit_code=process.returncode)
        except (OSError, subprocess.TimeoutExpired) as error:
            status = dict(error=str(error))
        length = output.tell()
        output.seek(0)
        status.update(elapsed_seconds=time.monotonic() - started, output_bytes=length,
                      truncated=length > 1048576,
                      text=output.read(1048576).decode('utf-8', errors='replace'))
    return status


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--parent', type=int, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    started = time.monotonic()
    receipt = dict(scope='Failure-only observation; debugger attachment perturbs timing. '
                         'Never replaces the original browser gate.', parent=args.parent,
                   clock_ticks_per_second=os.sysconf('SC_CLK_TCK'), samples=[])
    try:
        # Requiring our actual direct parent prevents a caller selecting another
        # user's browser or a sibling CI workload for debugger attachment.
        if args.parent != os.getppid():
            raise ValueError('parent must be the actual spawning test process')
        candidates = gpu_processes(args.parent)
        receipt['candidates'] = candidates
        if len(candidates) != 1:
            receipt['status'] = 'no unique live descendant GPU process'
        else:
            source = candidates[0]
            for index in range(3):
                if index:
                    time.sleep(2)
                receipt['samples'].append(dict(elapsed_seconds=time.monotonic() - started,
                                               **sample(source, args.parent)))
            if same_generation(source, args.parent):
                maps = (Path('/proc') / str(source['pid']) / 'maps').read_text()
                receipt['executable_mappings'] = [line for line in maps.splitlines()
                                                   if 'r-x' in line.split()[1]]
            receipt['backtrace'] = backtrace(source, args.parent)
            receipt['status'] = 'observed'
    except (OSError, ValueError, IndexError) as error:
        receipt.update(status='observation unavailable', error=str(error))
    receipt['elapsed_seconds'] = time.monotonic() - started
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'GPU process observation: {receipt["status"]}; {args.output}', flush=True)


if __name__ == '__main__':
    main()
