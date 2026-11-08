#!/usr/bin/env python3
"""Observe the original managed generator under current runtime defaults.

This diagnostic never replaces the frozen resource, alters ICU/runtime selection,
or changes an oracle outcome. It retains compact facts and removes temporary
build/plan output. It is not full normalization/shaping qualification.
"""
from pathlib import Path
import argparse
import datetime
import hashlib
import json
import os
import platform
import shutil
import struct
import subprocess
import tempfile
from xml.sax.saxutils import quoteattr

ROOT = Path(__file__).resolve().parents[1]
GENERATOR = ROOT / 'eng/ProGPU.UnicodeNormalizationGenerator/Program.cs'
RESOURCE = ROOT / 'src/ProGPU.Text/UnicodeNormalizationData.bin'

def sha(data):
    return hashlib.sha256(data).hexdigest()

def parse(data):
    magic, version, decompositions, scalars, compositions = struct.unpack_from('<IIIII', data)
    if magic != 0x4e554750 or version != 1:
        raise ValueError('Unknown original normalization plan')
    scalar_offset = 20 + decompositions * 12
    composition_offset = scalar_offset + scalars * 4
    if len(data) != composition_offset + compositions * 12:
        raise ValueError('Incomplete original normalization plan')
    records = {}
    for i in range(decompositions):
        cp, start, count = struct.unpack_from('<III', data, 20 + i * 12)
        if start + count > scalars or cp in records:
            raise ValueError('Invalid original decomposition inventory')
        records[cp] = list(struct.unpack_from('<' + 'I' * count, data, scalar_offset + start * 4))
    pairs = {}
    for i in range(compositions):
        first, second, composed = struct.unpack_from('<III', data, composition_offset + i * 12)
        if (first, second) in pairs:
            raise ValueError('Duplicate original composition pair')
        pairs[first, second] = composed
    return records, pairs, [decompositions, scalars, compositions]

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--dotnet', default=os.environ.get('PROGPU_RUST_DOTNET', 'dotnet'))
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-normalization-runtime-') as temp:
        directory = Path(temp).resolve()
        def capture(command):
            r = subprocess.run(command, cwd=directory, capture_output=True, text=True)
            if r.returncode:
                raise RuntimeError(f'{command}:\n{r.stdout}\n{r.stderr}')
            return r.stdout.strip()
        project = directory / 'Control.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(GENERATOR))}/></ItemGroup></Project>')
        runtime = capture([args.dotnet, '--info'])
        capture([args.dotnet, 'build', 'Control.csproj', '-c', 'Release', '-o', 'out', '--nologo', '-v:q'])
        plan = directory / 'runtime.bin'
        capture([args.dotnet, str(directory / 'out/Control.dll'), str(plan)])
        original = RESOURCE.read_bytes()
        generated = plan.read_bytes()
        original_records, original_pairs, original_counts = parse(original)
        runtime_records, runtime_pairs, runtime_counts = parse(generated)
        receipt = {
            'captured_utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'host': platform.platform(), 'dotnet_info': runtime,
            'method': 'Unchanged original managed generator, current runtime defaults, isolated temporary build; complete decomposition and composition inventory comparison.',
            'qualification': 'Diagnostic data provenance only; no gate replacement or runtime/compiler changes; not a complete canonical ordering or shaping comparison.',
            'generator_sha256': sha(GENERATOR.read_bytes()),
            'resource_sha256': sha(original), 'runtime_plan_sha256': sha(generated),
            'resource_bytes': len(original), 'runtime_plan_bytes': len(generated),
            'resource_counts': original_counts, 'runtime_counts': runtime_counts,
            'decomposition_differences': [
                {'code_point': cp, 'resource': original_records.get(cp), 'runtime': runtime_records.get(cp)}
                for cp in sorted(original_records.keys() | runtime_records.keys())
                if original_records.get(cp) != runtime_records.get(cp)],
            'composition_differences': [
                {'pair': list(pair), 'resource': original_pairs.get(pair), 'runtime': runtime_pairs.get(pair)}
                for pair in sorted(original_pairs.keys() | runtime_pairs.keys())
                if original_pairs.get(pair) != runtime_pairs.get(pair)],
            'temporary_logical_bytes_removed': sum(p.stat().st_size for p in directory.rglob('*') if p.is_file()),
        }
        if platform.system() == 'Linux' and shutil.which('dpkg-query'):
            # Read installed ICU package metadata without changing the runtime.
            query = subprocess.run(['dpkg-query', '-W', 'libicu*'], capture_output=True, text=True)
            receipt['installed_icu_packages'] = query.stdout.strip()
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(receipt, indent=2) + '\n')
        print(f'decomposition differences={len(receipt["decomposition_differences"])}; composition differences={len(receipt["composition_differences"])}; receipt={args.output}')

if __name__ == '__main__':
    main()
