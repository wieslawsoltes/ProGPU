#!/usr/bin/env python3
"""Public Unicode joining facts, independently checked against original ProGPU."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import tempfile
import urllib.request
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/ProGPU.Rust/src/text/script_actions/joining_data.rs'
FILES = {
    'ArabicShaping.txt': '39afa01e680e27d0fd10b67a9b27be13fbaa3d0efecfb5be45991de9a0d267d0',
    'extracted/DerivedJoiningType.txt': 'f39ebe974825d6736aee15582250307aa532b2cfab3caf3f86bd23fddc9c5c4d',
}


def public_facts(directory):
    contents = {}
    for name, expected in FILES.items():
        path = directory / Path(name).name
        url = 'https://www.unicode.org/Public/17.0.0/ucd/' + name
        if not path.exists():
            path.write_bytes(urllib.request.urlopen(url).read())
        data = path.read_bytes()
        assert hashlib.sha256(data).hexdigest() == expected, name
        contents[name] = data.decode()
    values = bytearray(0x110002)  # Complete raw inventory plus U+110000 and UINT_MAX.
    seen = set()
    for line in contents['extracted/DerivedJoiningType.txt'].splitlines():
        line = line.split('#')[0].strip()
        if not line:
            continue
        interval, kind = map(str.strip, line.split(';'))
        bounds = interval.split('..')
        start, end = int(bounds[0], 16), int(bounds[-1], 16)
        value = {'U': 0, 'L': 1, 'R': 2, 'D': 3, 'C': 3, 'T': 6}[kind]
        for point in range(start, end + 1):
            assert point not in seen and point <= 0x10ffff
            seen.add(point)
            values[point] = value
    # These distinct Syriac groups select original independent state columns.
    for line in contents['ArabicShaping.txt'].splitlines():
        line = line.split('#')[0].strip()
        if not line:
            continue
        point, _, _, group = map(str.strip, line.split(';'))
        if group in ('ALAPH', 'DALATH RISH'):
            values[int(point, 16)] = 4 if group == 'ALAPH' else 5
    return values


def original_facts(work):
    harness = ROOT / 'src/ProGPU.Rust/tests/managed_joining_facts.cs'
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
    return subprocess.run([dotnet, str(work / 'artifacts/bin/Control/release/Control.dll')],
                          capture_output=True, check=True).stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--data-dir', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-joining-facts-') as directory:
        work = Path(directory)
        values = public_facts(args.data_dir or work)
        actual = original_facts(work)
        assert len(actual) == len(values), 'original query inventory changed'
        differences = [hex(i) for i, (a, b) in enumerate(zip(actual, values)) if a != b]
        assert not differences, f'original differs from public facts: {differences[:20]}'
    lines = ['//! Hash-pinned public Unicode 17 joining facts, not external implementation.',
             '//! Independently compared with every original ProGPU raw query.',
             '//! License: src/ProGPU.Rust/UNICODE-LICENSE.txt.',
             '//! Regenerate/check: eng/progpu-generate-rust-joining.py.']
    for name, digest in FILES.items():
        lines += [f'// https://www.unicode.org/Public/17.0.0/ucd/{name}', f'// SHA-256 {digest}']
    lines += ['pub(super) const JOINING: &[(u32, u32, u8)] = &[']
    point = ranges = 0
    while point < 0x110000:
        value = values[point]
        end = point
        while end + 1 < 0x110000 and values[end + 1] == value:
            end += 1
        if value:
            lines.append(f'    (0x{point:x}, 0x{end:x}, {value}),')
            ranges += 1
        point = end + 1
    generated = '\n'.join(lines + ['];']) + '\n'
    if args.check:
        assert OUTPUT.read_text() == generated, 'joining facts are stale'
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(generated)
    print(json.dumps(dict(raw_queries=len(values), ranges=ranges,
                          original_output_sha256=hashlib.sha256(actual).hexdigest(),
                          generated_source_sha256=hashlib.sha256(generated.encode()).hexdigest())))


if __name__ == '__main__':
    main()
