#!/usr/bin/env python3
"""Compare independent USE parsing with original API results, never engine tables."""
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
    alphabet = [0, 1, 4, 5, 6, 11, 12, 13, 14, 16, 18, 22, 23, 24, 25, 26,
                27, 28, 29, 30, 31, 32, 33, 34, 35, 37, 38, 39, 41, 42, 43,
                44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 54, 55, 56, 255]
    for length in range(5):
        yield from itertools.product(alphabet, repeat=length)
    yield from itertools.product(range(256), repeat=2)
    for raw in range(256):
        for left, right in itertools.product(alphabet, repeat=2):
            yield raw, left, right
            yield left, raw, right
            yield left, right, raw
    # Unbounded sections, failed hieroglyph prefixes and source serial rollover.
    parts = [[31], [32], [12, 1], [44, 1], [48, 1], [53, 1], [11], [22],
             [33], [34], [35], [23], [37], [38], [39], [24], [25], [26],
             [45], [46], [47], [13, 4], [41], [42], [51], [52], [50],
             [49, 55, 54, 52, 50, 51], [6], [255]]
    for n in [0, 1, 2, 3, 7, 15, 16, 31, 64, 255, 1024, 4096]:
        for prefix in [[], [0], [1], [4], [5], [18], [43], [49], [1, 22]]:
            for part in parts:
                for tail in [[], [1], [14], [49], [48]]:
                    yield prefix + part * n + tail
    rng = random.Random(0x555345)
    for index in range(30000):
        pool = alphabet if index % 3 else range(256)
        yield [rng.choice(pool) for _ in range(rng.randrange(257))]
    # Independently authored complete public-grammar groups and mutations,
    # exercising long valid combinations that uniform random bytes rarely reach.
    for index in range(10000):
        c = rng.choice([[], [1], [5], [18, 1], [43, 5], [0], [18]])[:]
        for _ in range(rng.randrange(6)):
            c += [31] * rng.randrange(4) + [32] * rng.randrange(4)
            c += rng.choice([[], [12, 1], [44, 1], [48, 1], [53, 1], [11]])
        for category in [30, 27, 28, 29]:
            c += [category] * rng.randrange(2)
        for category in [22, 33, 34, 35]:
            c += [category] * rng.randrange(4)
        c += [53] * rng.randrange(2)
        for category in [23, 37, 38, 39]:
            c += [category] * rng.randrange(4)
        c += [48, 1] * rng.randrange(4)
        for category in [24, 25, 26, 45, 46]:
            c += [category] * rng.randrange(4)
        c += [14] * rng.randrange(2)
        if index % 3 == 0 and c:
            c[rng.randrange(len(c))] = rng.randrange(256)
        yield c


def main():
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'use_syllable_control'], cwd=ROOT, check=True)
    target = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target'))
    if not target.is_absolute():
        target = ROOT / target
    rust = target / 'release/examples/use_syllable_control'
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
    with tempfile.TemporaryDirectory(prefix='progpu-use-syllables-') as directory:
        command = capture.original_command(Path(directory), ROOT / 'src/ProGPU.Rust/tests/managed_use_syllables.cs')
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
