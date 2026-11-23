#!/usr/bin/env python3
"""Compare independent Khmer parsing with original API results, never engine tables."""
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
    alphabet = [0, 1, 2, 4, 5, 6, 10, 11, 15, 20, 21, 22, 23, 25, 26, 27]
    for length in range(6):
        yield from itertools.product(alphabet, repeat=length)
    yield from itertools.product(range(256), repeat=2)
    # Every raw category in each position of two admitted-category neighbors.
    for raw in range(256):
        for left, right in itertools.product(alphabet, repeat=2):
            yield raw, left, right
            yield left, raw, right
            yield left, right, raw
    # Long paths exercise each optional section, modifier and serial rollover.
    for n in [0, 1, 2, 3, 4, 7, 15, 16, 31, 64, 255, 1024, 4096]:
        for tail in [[], [20], [26], [25], [4, 1], [27], [0]]:
            for joiner in [[5], [6], [5, 6]]:
                for prefix in [[], [1], [10], [22], [23], [27]]:
                    yield prefix + joiner * n + tail
        for base in [1, 2, 10, 11, 15]:
            for modifier in [[], [25], [5, 25], [6, 25]]:
                yield [base] + modifier + ([4, 1] + modifier) * n + [26, 22, 26, 21, 5, 20, 26, 23, 4, 15, 27, 27]
        yield [0] * n
        yield [25] * n
        yield [1, 27] * n
    rng = random.Random(0x4b484d52)
    for i in range(16000):
        pool = alphabet if i % 3 else range(256)
        yield [rng.choice(pool) for _ in range(rng.randrange(257))]


def main():
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'khmer_syllable_control'], cwd=ROOT, check=True)
    target = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target'))
    if not target.is_absolute():
        target = ROOT / target
    rust = target / 'release/examples/khmer_syllable_control'
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
    with tempfile.TemporaryDirectory(prefix='progpu-khmer-syllables-') as directory:
        command = capture.original_command(Path(directory), ROOT / 'src/ProGPU.Rust/tests/managed_khmer_syllables.cs')
        original = subprocess.run(command, input=request, capture_output=True, check=True).stdout
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
