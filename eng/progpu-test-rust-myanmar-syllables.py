#!/usr/bin/env python3
"""Compare independent Myanmar parsing with original API results, never engine tables."""
from pathlib import Path
import hashlib
import importlib.util
import itertools
import json
import os
import random
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('capture', ROOT / 'eng/progpu-capture-rust-script-properties.py')
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


def corpus():
    alphabet = [0, 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 15, 18, 20, 21, 22, 23,
                32, 35, 36, 37, 38, 39, 40, 41, 57]
    rng = random.Random(0x4d594d52)
    for length in range(6):
        yield from itertools.product(alphabet, repeat=length)
    yield from itertools.product(range(256), repeat=2)
    for raw in range(256):
        for left, right in itertools.product(alphabet, repeat=2):
            yield raw, left, right
            yield left, raw, right
            yield left, right, raw
    # Long optional sections, incomplete prefixes and serial rollover.
    for n in [0, 1, 2, 3, 4, 7, 15, 16, 31, 64, 255, 1024, 4096]:
        for prefix in [[], [1], [18], [15, 32, 4], [15, 32, 4, 1], [4, 1]]:
            for part in [[40], [4, 1, 40], [32], [38, 32], [37, 35, 41, 32],
                         [22, 40], [20], [21], [9], [3, 32],
                         [23, 35, 41, 32, 20, 9, 3, 32], [39, 9, 3, 32],
                         [57], [8], [5], [0]]:
                for tail in [[], [1], [4], [5], [9], [32], [40], [57]]:
                    yield prefix + part * n + tail
    for i in range(30000):
        pool = alphabet if i % 3 else range(256)
        yield [rng.choice(pool) for _ in range(rng.randrange(257))]


def main():
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'myanmar_syllable_control'], cwd=ROOT, check=True)
    target = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target'))
    if not target.is_absolute():
        target = ROOT / target
    rust = target / 'release/examples/myanmar_syllable_control'
    if os.name == 'nt':
        rust = rust.with_suffix('.exe')
    font_path = ROOT / 'tests/Shared/ProGPU-Authored-Scan-1.ttf'
    font = font_path.read_bytes()
    request = bytearray(struct.pack('<I', len(font)) + font)
    count = size = 0
    for case in corpus():
        request.extend(struct.pack('<I', len(case)))
        request.extend(case)
        count += 1
        size += len(case)
    with tempfile.TemporaryDirectory(prefix='progpu-myanmar-syllables-') as directory:
        command = capture.original_command(Path(directory), ROOT / 'src/ProGPU.Rust/tests/managed_khmer_syllables.cs')
        original = subprocess.run(command + ["myanmar"], input=request, capture_output=True, check=True).stdout
        actual = subprocess.run([str(rust)], input=request, capture_output=True, check=True).stdout
    assert len(original) == size and len(actual) == size
    if original != actual:
        offset = 0
        for index, case in enumerate(corpus()):
            end = offset + len(case)
            if original[offset:end] != actual[offset:end]:
                raise AssertionError((index, case, list(original[offset:end]), list(actual[offset:end])))
            offset = end
    print(json.dumps(dict(cases=count, exact_bytes=size, font_sha256=hashlib.sha256(font).hexdigest(),
                          request_sha256=hashlib.sha256(request).hexdigest(),
                          response_sha256=hashlib.sha256(original).hexdigest())))


if __name__ == '__main__':
    main()
