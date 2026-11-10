#!/usr/bin/env python3
"""Linux-only, isolated browser identity/backtrace probe, not a rendering gate.

Pass an existing Chrome executable and an output receipt. The probe owns a new
temporary profile and process group; it never attaches to an existing browser.
"""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import signal
import subprocess
import sys
import tempfile
import time

sys.dont_write_bytecode = True
module = Path(__file__).with_name('capture-gpu.py')
spec = importlib.util.spec_from_file_location('capture_gpu', module)
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--executable', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if sys.platform != 'linux':
        parser.error('this probe requires Linux /proc')
    executable = args.executable.resolve(strict=True)
    flags = ['--headless=new', '--no-sandbox', '--no-first-run', '--no-default-browser-check',
             '--enable-unsafe-webgpu', '--use-angle=swiftshader', '--disable-webgpu-shader-disk-cache',
             '--enable-features=Vulkan', '--use-vulkan=swiftshader', '--disable-vulkan-surface']
    with tempfile.TemporaryDirectory(prefix='progpu-gpu-identity-') as temporary:
        work = Path(temporary)
        with (work / 'browser.log').open('wb') as log:
            browser = subprocess.Popen([str(executable), *flags, '--user-data-dir=' + str(work / 'profile'),
                                        'about:blank'], stdout=log, stderr=log, start_new_session=True)
            try:
                deadline = time.monotonic() + 15
                candidates = []
                while time.monotonic() < deadline and browser.poll() is None:
                    candidates = capture.gpu_processes(os.getpid(), executable)
                    if len(candidates) == 1:
                        break
                    time.sleep(0.1)
                if len(candidates) != 1:
                    raise RuntimeError('no unique owned GPU process: ' +
                                       json.dumps(capture.owned_process_inventory(os.getpid())))
                source, = candidates
                assert capture.same_generation(source, os.getpid(), executable)
                subprocess.run([sys.executable, str(module), '--parent', str(os.getpid()),
                                '--executable', str(executable), '--output', str(work / 'capture.json')],
                               check=True, timeout=20)
                observed = json.loads((work / 'capture.json').read_text())
                assert observed['status'] == 'observed', observed['status']
                actual, = observed['candidates']
                for key in ('pid', 'start_ticks', 'selection', 'executable_device', 'executable_inode'):
                    assert actual[key] == source[key], key
                receipt = dict(scope='Isolated live /proc and debugger diagnostic only; no ProGPU shader, '
                                     'pixel, browser reliability or performance qualification.',
                               platform=platform.platform(), architecture=platform.machine(), flags=flags,
                               browser_version=subprocess.check_output([str(executable), '--version'],
                                                                       text=True, stderr=subprocess.DEVNULL).strip(),
                               observer_sha256=hashlib.sha256(module.read_bytes()).hexdigest(),
                               result=observed)
                args.output.parent.mkdir(parents=True, exist_ok=True)
                args.output.write_text(json.dumps(receipt, indent=2) + '\n')
                print(json.dumps(dict(selection=source['selection'], status=observed['status'],
                                      samples=len(observed['samples']),
                                      backtrace_exit=observed['backtrace'].get('exit_code'))))
            finally:
                # Only the new isolated process group belongs to this probe.
                try:
                    os.killpg(browser.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
                try:
                    browser.wait(timeout=8)
                except subprocess.TimeoutExpired:
                    os.killpg(browser.pid, signal.SIGKILL)
                    browser.wait(timeout=2)


if __name__ == '__main__':
    main()
