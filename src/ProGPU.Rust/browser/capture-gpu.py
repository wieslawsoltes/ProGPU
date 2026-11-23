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


def gpu_identity(pid, executable, root=Path('/proc')):
    """Recognize a fixed GPU role only on the selected browser executable.

    Linux exposes mutable argv storage. Chromium can pack its entire title into
    one argument; accept only the exact executable prefix and first role token,
    never an embedded substring or a shell-style split of arbitrary values.
    """
    directory = root / str(pid)
    expected = executable.stat()
    actual = (directory / 'exe').stat()
    if (expected.st_dev, expected.st_ino) != (actual.st_dev, actual.st_ino):
        return None
    identity = dict(executable_device=actual.st_dev, executable_inode=actual.st_ino)
    raw = (directory / 'cmdline').read_bytes()
    args = raw.rstrip(b'\0').split(b'\0')
    kinds = [arg for arg in args[1:] if arg.startswith(b'--type=')]
    if kinds == [b'--type=gpu-process']:
        return dict(**identity, selection='separate-argument')
    prefix = os.fsencode(executable) + b' --type=gpu-process'
    if len(args) == 1 and (args[0] == prefix or args[0].startswith(prefix + b' ')):
        return dict(**identity, selection='packed-executable-prefix')
    return None


def gpu_processes(parent, executable, root=Path('/proc')):
    result = []
    for directory in root.iterdir():
        if not directory.name.isdecimal():
            continue
        pid = int(directory.name)
        if pid == parent or not descendant(pid, parent, root):
            continue
        try:
            before = process_stat(pid, root)
            selection = gpu_identity(pid, executable, root)
            if not selection:
                continue
            after = process_stat(pid, root)
            if before['start_ticks'] == after['start_ticks'] and descendant(pid, parent, root):
                result.append(dict(**after, **selection))
        except (OSError, ValueError, IndexError):
            continue  # Concurrent child exit is an observation, not a test result.
    return sorted(result, key=lambda value: value['pid'])


def owned_process_inventory(parent, root=Path('/proc'), limit=128):
    """Bounded identity/type observations only; never retain full argv or paths."""
    result = []
    for directory in sorted(root.iterdir(), key=lambda value: value.name):
        if not directory.name.isdecimal():
            continue
        pid = int(directory.name)
        if pid == parent or not descendant(pid, parent, root):
            continue
        try:
            value = process_stat(pid, root)
            try:
                raw = (directory / 'cmdline').read_bytes()
                args = [part for part in raw.split(b'\0') if part]
                # Preserve only fixed process-kind flags. An embedded marker
                # alone never supplies the executable/role proof for attachment.
                kinds = [kind for kind in ('gpu-process', 'renderer', 'zygote', 'utility')
                         if ('--type=' + kind).encode() in args]
                value.update(argument_count=len(args), process_types=kinds,
                             embedded_gpu_marker=b'--type=gpu-process' in raw,
                             command_line_status='read')
            except OSError as error:
                value.update(command_line_status=type(error).__name__)
            if not descendant(pid, parent, root):
                continue
            result.append(value)
            if len(result) == limit:
                return dict(processes=result, truncated=True)
        except (OSError, ValueError, IndexError):
            continue
    return dict(processes=result, truncated=False)


def same_generation(source, parent, executable, root=Path('/proc')):
    try:
        current = process_stat(source['pid'], root)
        if current['start_ticks'] != source['start_ticks'] or not descendant(source['pid'], parent, root):
            return False
        identity = gpu_identity(source['pid'], executable, root)
        if not identity or any(source.get(key) != value for key, value in identity.items()):
            return False
        return (process_stat(source['pid'], root)['start_ticks'] == source['start_ticks']
                and descendant(source['pid'], parent, root))
    except (OSError, ValueError, IndexError):
        return False


def sample(source, parent, executable):
    if not same_generation(source, parent, executable):
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
    if not same_generation(source, parent, executable):
        return dict(retired=True)
    return dict(process=process_stat(source['pid']), threads=threads)


def backtrace(source, parent, executable):
    if not same_generation(source, parent, executable):
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
    parser.add_argument('--executable', type=Path, required=True)
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
        executable = args.executable.resolve(strict=True)
        candidates = gpu_processes(args.parent, executable)
        receipt['candidates'] = candidates
        if len(candidates) != 1:
            receipt['status'] = 'no unique live descendant GPU process'
            receipt['owned_process_inventory'] = owned_process_inventory(args.parent)
        else:
            source = candidates[0]
            for index in range(3):
                if index:
                    time.sleep(2)
                receipt['samples'].append(dict(elapsed_seconds=time.monotonic() - started,
                                               **sample(source, args.parent, executable)))
            if same_generation(source, args.parent, executable):
                maps = (Path('/proc') / str(source['pid']) / 'maps').read_text()
                if same_generation(source, args.parent, executable):
                    receipt['executable_mappings'] = [line for line in maps.splitlines()
                                                       if 'r-x' in line.split()[1]]
            receipt['backtrace'] = backtrace(source, args.parent, executable)
            receipt['status'] = 'observed'
    except (OSError, ValueError, IndexError) as error:
        receipt.update(status='observation unavailable', error=str(error))
    receipt['elapsed_seconds'] = time.monotonic() - started
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'GPU process observation: {receipt["status"]}; {args.output}', flush=True)


if __name__ == '__main__':
    main()
