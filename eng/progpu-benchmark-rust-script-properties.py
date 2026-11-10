#!/usr/bin/env python3
"""Matched original API/property latency baseline; no optimization claim."""
from pathlib import Path
import datetime
import hashlib
import importlib.util
import json
import os
import platform
import statistics
import struct
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('capture', ROOT / 'eng/progpu-capture-rust-script-properties.py')
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)
patterns = {
    'latin': [0x41, 0x20, 0x30, 0x301, 0x7f, 0xa0, 0x2010, 0x200e],
    'devanagari': [0x915, 0x930, 0x94d, 0x93f, 0x93c, 0x905, 0x903, 0x20],
    'khmer': [0x1780, 0x17d2, 0x179a, 0x17b7, 0x17c1, 0x17c9, 0x25cc, 0x200d],
    'myanmar': [0x1000, 0x1039, 0x103a, 0x1031, 0x103b, 0x104e, 0xaa7b, 0x20],
    'use': [0x11003, 0x11046, 0x1112c, 0x11f02, 0x13430, 0x1b44, 0x200c, 0x2060],
    'invalid': [0xd800, 0xdfff, 0x110000, 0x80000905, 0xffffffff, 0x7fffffff, 0x800017d2, 0x8010ffff],
}
cases, wire = [], bytearray()
for name, pattern in patterns.items():
    for size in (256, 4096):
        for mode in range(3):
            points = pattern * (size // len(pattern))
            repetitions = 8192 // size
            cases.append(dict(pattern=name, size=size, mode=mode, repetitions=repetitions))
            wire.extend(struct.pack('<III', mode, repetitions, size))
            wire.extend(struct.pack('<' + 'I' * size, *points))
wire = struct.pack('<I', len(cases)) + wire
subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                '--example', 'script_property_benchmark'], cwd=ROOT, check=True)
binary = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target')) / 'release/examples/script_property_benchmark'
if not binary.is_absolute():
    binary = ROOT / binary
if os.name == 'nt':
    binary = binary.with_suffix('.exe')
load = os.getloadavg() if hasattr(os, 'getloadavg') else None
with tempfile.TemporaryDirectory(prefix='progpu-property-benchmark-') as directory:
    command = capture.original_command(Path(directory), capture.RUST / 'tests/managed_script_property_benchmark.cs')
    trials = {'csharp': [], 'rust': []}
    for trial in range(3):
        for name in (['csharp', 'rust'] if trial % 2 == 0 else ['rust', 'csharp']):
            result = subprocess.run(command if name == 'csharp' else [str(binary)],
                                    input=wire, capture_output=True, check=True)
            if name == 'csharp':
                runtime = result.stderr.decode().strip()
            rows = [json.loads(line) for line in result.stdout.splitlines()]
            assert len(rows) == len(cases)
            trials[name].append(rows)
    output = bytearray()
    for i, case in enumerate(cases):
        expected = trials['csharp'][0][i]['values']
        for name in trials:
            for trial in trials[name]:
                assert trial[i]['values'] == expected, (case, name)
        output.extend(struct.pack('<' + 'H' * len(expected), *expected))
        case['trials'] = {name: [{k: v for k, v in trial[i].items() if k != 'values'}
                                for trial in trials[name]] for name in trials}
        case['rust_over_csharp_p50'] = statistics.median(t[i]['p50_ns'] for t in trials['rust']) / statistics.median(t[i]['p50_ns'] for t in trials['csharp'])
    assembly_hash = hashlib.sha256(Path(command[1]).read_bytes()).hexdigest()
receipt = dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
               host=platform.platform(), load_average=load, source_runtime=runtime,
               scope='New-feature scalar lookup baseline: three rotating process trials, five warmups and forty samples; identical indirect typed calls, immutable inputs and reused outputs. Full outputs compared outside timer. Source allocation counters only; Rust allocation events and end-to-end shaping remain unqualified. Not an optimization comparison.',
               input_sha256=hashlib.sha256(wire).hexdigest(), output_sha256=hashlib.sha256(output).hexdigest(),
               rust_binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
               csharp_assembly_sha256=assembly_hash, cases=cases,
               lower=sum(c['rust_over_csharp_p50'] < 1 for c in cases),
               higher=sum(c['rust_over_csharp_p50'] >= 1 for c in cases))
(capture.RUST / 'perf/script-properties-baseline.json').write_text(json.dumps(receipt, indent=2) + '\n')
print(json.dumps({key: receipt[key] for key in ('load_average', 'lower', 'higher')}))
