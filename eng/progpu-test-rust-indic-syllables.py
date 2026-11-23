#!/usr/bin/env python3
"""Compare independent Indic parsing with original API results, never engine tables."""
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
    alphabet = list(range(19)) + [57, 255]
    for length in range(6):
        yield from itertools.product(alphabet, repeat=length)
    yield from itertools.product(range(256), repeat=2)
    for raw in range(256):
        for left, right in itertools.product(alphabet, repeat=2):
            yield raw, left, right
            yield left, raw, right
            yield left, right, raw
    parts = [[3], [7], [8], [57], [9], [12], [13], [16], [5], [6], [5, 6],
             [4, 1], [5, 4, 15], [4, 6, 3, 1], [1, 6, 5, 12, 3, 3],
             [7, 3, 4], [8, 13], [57, 13], [8, 13, 3, 4], [18, 10], [15, 4, 2], [255]]
    for count in [0, 1, 2, 3, 7, 15, 16, 31, 64, 255, 1024, 4096]:
        for prefix in [[], [1], [2], [10], [11], [14], [15, 4], [17], [18], [1, 16]]:
            for part in parts:
                for suffix in [[], [1], [5], [7], [9], [13]]:
                    yield prefix + part * count + suffix
    for part in [[5], [6], [5, 6]]:
        for suffix in [[], [7], [8, 13], [12], [1]]:
            yield [14] + part * 100000 + suffix
    rng = random.Random(0x494e4449)
    for index in range(30000):
        pool = alphabet if index % 3 else range(256)
        yield [rng.choice(pool) for _ in range(rng.randrange(257))]
    # Authored public grammar components and mutations. Real combinations cover
    # deep marks/conjuncts that uniform random input rarely reaches.
    for index in range(30000):
        c = rng.choice([[], [14], [15, 4], [18]])[:]
        c += rng.choice([[], [1], [15], [2], [10], [11], [17]])
        c += [6] * rng.randrange(2)
        for link in range(rng.randrange(6)):
            c += rng.choice([[], [12], [5, 12]]) + [3] * rng.randrange(4)
            c += rng.choice([[4], [5, 4], [6, 4], [4, 6], [4, 6, 3]])
            c += rng.choice([[1], [15], [1, 6], [15, 6]])
        c += [16] * rng.randrange(2)
        for matra in range(rng.randrange(6)):
            c += [rng.choice([5, 6]) for _ in range(rng.randrange(4))]
            c += rng.choice([[7], [13], [8, 13]])
            c += [3] * rng.randrange(2) + [4] * rng.randrange(2)
        c += rng.choice([[], [4], [4, 5], [4, 6, 3], [5, 4], [6, 4]])
        c += [8] * rng.randrange(4) + [5] * rng.randrange(2) + [9] * rng.randrange(8)
        if index % 3 == 0 and c:
            c[rng.randrange(len(c))] = rng.randrange(256)
        yield c


def main():
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'indic_syllable_control'], cwd=ROOT, check=True)
    target = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target'))
    if not target.is_absolute():
        target = ROOT / target
    rust = target / 'release/examples/indic_syllable_control'
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
    with tempfile.TemporaryDirectory(prefix='progpu-indic-syllables-') as directory:
        command = capture.original_command(Path(directory), ROOT / 'src/ProGPU.Rust/tests/managed_indic_syllables.cs')
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
