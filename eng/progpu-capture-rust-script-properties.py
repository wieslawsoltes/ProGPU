#!/usr/bin/env python3
"""Observe original ProGPU property APIs; never read foreign generated tables."""
from pathlib import Path
import argparse
import array
import hashlib
import json
import os
import struct
import subprocess
import sys
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
RUST = ROOT / 'src/ProGPU.Rust'
OUTPUT = RUST / 'src/text/unicode/script_property_data.rs'
FIXTURE = RUST / 'tests/support/script_properties.bin'
RECEIPT = RUST / 'browser/script-properties.json'


def requests():
    # Every raw Unicode-domain value (including surrogates), first out-of-range,
    # and every high-bit alias. Never narrow before original API invocation.
    points = array.array('I', range(0x110001))
    points.extend(0x80000000 | cp for cp in range(0x110000))
    points.extend([0x110001, 0x7fffffff, 0xffffffff])
    if sys.byteorder != 'little':
        points.byteswap()
    return points.tobytes()


def original_command(work, harness=RUST / 'tests/managed_script_property_facts.cs'):
    source = ROOT / 'src/ProGPU.Text/ProGPU.Text.csproj'
    (work / 'Control.csproj').write_text(
        '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
        f'<Compile Include="{escape(str(harness))}"/><ProjectReference Include="{escape(str(source))}"/>'
        '</ItemGroup></Project>')
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    subprocess.run([dotnet, 'build', 'Control.csproj', '-c', 'Release', '--nologo', '-v:q',
                    '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={work / "artifacts"}'],
                   cwd=work, check=True)
    return [dotnet, str(work / 'artifacts/bin/Control/release/Control.dll')]


def ranges(values, column, default):
    result = []
    point = 0
    while point < 0x110000:
        value = values[point][column]
        end = point
        while end + 1 < 0x110000 and values[end + 1][column] == value:
            end += 1
        if value != default:
            result.append((point, end, value))
        point = end + 1
    return result


def publish(path, data, check):
    if check:
        # Source/JSON are text; Git may check them out with platform newlines.
        actual = path.read_bytes() if path.suffix == '.bin' else path.read_text().encode()
        assert actual == data, f'{path.relative_to(ROOT)} is stale'
    else:
        path.write_bytes(data)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--rust', action='store_true', help='compare every original output byte with Rust')
    args = parser.parse_args()
    request = requests()
    with tempfile.TemporaryDirectory(prefix='progpu-script-properties-') as directory:
        actual = subprocess.run(original_command(Path(directory)), input=request,
                                capture_output=True, check=True)
        print(actual.stderr.decode().strip())
        actual = actual.stdout
    assert len(actual) == 0x20000 + len(request) // 4 * 6
    # Extraction is a separate observable API over all caller-authored u16s.
    assert actual[:0x20000] == b''.join(struct.pack('<H', v) for v in range(0x10000))
    values = list(struct.iter_unpack('<HBHB', actual[0x20000:]))
    for (point,), (indic, use, public_indic, public_use) in zip(struct.iter_unpack('<I', request), values):
        valid = point <= 0x10ffff and not 0xd800 <= point <= 0xdfff
        assert public_indic == (indic if valid else 0), hex(point)
        assert public_use == use, hex(point)
        if not valid:
            assert (indic, use) == (0xe00, 0), hex(point)
    inventories = [('INDIC', ranges(values, 0, 0xe00)), ('USE', ranges(values, 1, 0))]
    lines = ['//! Independently observed original ProGPU API results; no engine tables read.',
             '//! Regenerate/check: eng/progpu-capture-rust-script-properties.py.',
             '//! Provenance, public research and remaining gates: DESIGN.md.',
             f'// Original response SHA-256: {hashlib.sha256(actual).hexdigest()}']
    samples = {0, 0xd7ff, 0xd800, 0xdfff, 0xe000, 0x10ffff, 0x110000}
    for name, inventory in inventories:
        lines.append(f'pub(super) const {name}: [u64; {len(inventory)}] = [')
        for start, end, value in inventory:
            lines.append(f'    0x{start | end << 21 | value << 42:016x},')
            samples.update(v for v in (start - 1, start, (start + end) // 2, end, end + 1)
                           if 0 <= v <= 0x110000)
        lines.append('];')
    generated = ('\n'.join(lines) + '\n').encode()
    # Exact boundary/interior observations; deliberately a different wire format
    # from production ranges. Ordinary native tests require no managed runtime.
    samples = sorted(samples)
    indexes = samples + [0x110001 + cp for cp in samples if cp < 0x110000]
    indexes += list(range(len(values) - 3, len(values)))
    fixture = bytearray(b'PSP1' + struct.pack('<I', len(indexes)))
    for index in indexes:
        fixture.extend(request[index * 4:index * 4 + 4])
        fixture.extend(actual[0x20000 + index * 6:0x20000 + index * 6 + 6])
    receipt = dict(protocol='all-u16-extractions-then-HBHB-properties-v1',
                   raw_domain_end_inclusive=0x110000, high_alias_or=0x80000000,
                   high_alias_end_exclusive=0x110000, extra_points=[0x110001, 0x7fffffff, 0xffffffff],
                   raw_queries=len(values), extraction_queries=0x10000,
                   response_bytes=len(actual), fixture_cases=len(indexes),
                   input_sha256=hashlib.sha256(request).hexdigest(),
                   output_sha256=hashlib.sha256(actual).hexdigest(),
                   fixture_sha256=hashlib.sha256(fixture).hexdigest(),
                   ranges={name: len(inventory) for name, inventory in inventories})
    publish(OUTPUT, generated, args.check)
    publish(FIXTURE, fixture, args.check)
    publish(RECEIPT, (json.dumps(receipt, indent=2) + '\n').encode(), args.check)
    if args.rust:
        subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                        '--example', 'script_property_control'], cwd=ROOT, check=True)
        target = Path(os.environ.get('CARGO_TARGET_DIR', ROOT / 'target'))
        if not target.is_absolute():
            target = ROOT / target
        binary = target / 'release/examples/script_property_control'
        if os.name == 'nt':
            binary = binary.with_suffix('.exe')
        rust = subprocess.run([str(binary)], input=request, capture_output=True, check=True).stdout
        assert rust == actual, 'Rust differs from complete original response'
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
