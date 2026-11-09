#!/usr/bin/env python3
"""Capture original ProGPU preparation decisions; no foreign source-table transcription."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/ProGPU.Rust/src/text/preparation/khmer_facts.rs'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    args = parser.parse_args()
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-preparation-facts-') as directory:
        work = Path(directory)
        harness = ROOT / 'src/ProGPU.Rust/tests/managed_preparation_facts.cs'
        source = ROOT / 'src/ProGPU.Text/ProGPU.Text.csproj'
        (work / 'Control.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
            f'<Compile Include="{escape(str(harness))}"/><ProjectReference Include="{escape(str(source))}"/>'
            '</ItemGroup></Project>')
        subprocess.run([dotnet, 'build', 'Control.csproj', '-c', 'Release', '--nologo', '-v:q',
                        '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={work / "artifacts"}'],
                       cwd=work, check=True)
        data = subprocess.run([dotnet, str(work / 'artifacts/bin/Control/release/Control.dll')],
                              capture_output=True, check=True).stdout
    assert len(data) == 0x110000 and set(data) == {0, 1}
    lines = ['//! Original ProGPU IsKhmerBaseCategory observations for all valid scalars.',
             '//! Regenerate/check with eng/progpu-capture-rust-preparation.py.',
             '//! No source property table or external implementation is transcribed.',
             'pub(super) const KHMER_BASE_RANGES: &[(u32, u32)] = &[']
    point = count = 0
    while point < len(data):
        if data[point] == 0:
            point += 1
            continue
        end = point
        while end + 1 < len(data) and data[end + 1] == 1:
            end += 1
        lines.append(f'    (0x{point:x}, 0x{end:x}),')
        count += 1
        point = end + 1
    generated = '\n'.join(lines + ['];']) + '\n'
    if args.check:
        assert OUTPUT.read_text() == generated, 'original preparation predicate changed'
    else:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(generated)
    print(json.dumps({'queries': 1112064, 'positive_observations': sum(data), 'ranges': count,
                      'captured_output_sha256': hashlib.sha256(data).hexdigest(),
                      'generated_source_sha256': hashlib.sha256(generated.encode()).hexdigest()}))


if __name__ == '__main__':
    main()
