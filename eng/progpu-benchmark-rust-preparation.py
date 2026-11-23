#!/usr/bin/env python3
"""Matched complete owned text preparation, with actual source and independent profiles."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import platform
import re
import struct
import subprocess
import tempfile
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]


def word(value):
    return struct.pack('<I', value)


def font(space_only):
    head = bytearray(54)
    struct.pack_into('>H', head, 18, 1000)
    nominal = struct.pack('>HHIIIIII', 13, 0, 28, 0, 1,
                          0x20 if space_only else 0, 0x20 if space_only else 0x10ffff,
                          3 if space_only else 7)
    selector = struct.pack('>HII', 14, 30, 1) + bytes.fromhex('00fe0f') + struct.pack('>III', 0, 21, 1) + bytes.fromhex('0000410000')
    cmap = struct.pack('>HHHHIHHI', 0, 2, 0, 4, 20, 0, 5, 48) + nominal + selector
    result = bytearray(44)
    struct.pack_into('>IH', result, 0, 0x10000, 2)
    for n, (tag, data) in enumerate([(b'head', head), (b'cmap', cmap)]):
        struct.pack_into('>4sIII', result, 12 + n * 16, tag, 0, len(result), len(data))
        result += data
    return result


def request(script, points, count, level):
    pattern = b''.join(chr(p).encode('utf-16-le', errors='surrogatepass') for p in points)
    text = (pattern * ((count * 2 + len(pattern) - 1) // len(pattern)))[:count * 2]
    units = list(struct.unpack('<' + 'H' * count, text))
    script = list(script.encode('ascii'))
    values = [len(script), *script, level, 255, len(units), *units, 2, 0xd800, 65, 2, 0x301, 0xdc00]
    return word(len(values)) + b''.join(map(word, values))


def corpus():
    fonts = [font(False), font(True)]
    header = word(len(fonts)) + b''.join(word(len(f)) + f for f in fonts)
    data = bytearray(header)
    cases = []
    patterns = [
        ('ascii', 'latn', list(map(ord, 'ProGPU sample 123 '))),
        ('canonical', 'latn', [65, 0x301, 0x334, 0x344, 0xc0]),
        ('indic', 'beng', [0x995, 0x9cd, 0x200c, 0x9cc, 0x9cc]),
        ('khmer', 'khmr', [0x1780, 0x17d2, 0x1781, 0x17c4, 0x200d]),
        ('hangul', 'hang', [0x1100, 0x1160, 0x11a8, 0xac00]),
        ('use', 'tibt', [0xf40, 0xf71, 0xf73, 0x200c, 0xf74]),
        ('malformed', 'latn', [65, 0xd800, 0x301, 0xdc00, 0x600]),
        ('selectors', 'latn', [65, 0xfe0f, 0xfe0e, 0x1f469, 0xe0100]),
        ('spaces', 'latn', [0x20, 0xa0, 0x2000, 0x2007, 0x2011, 0x202f, 0x3000]),
    ]
    for index in range(len(fonts)):
        for name, script, points in patterns:
            for count in [128, 4096]:
                data += word(index) + request(script, points, count, 1)
                cases.append(dict(font='space-only' if index else 'full', pattern=name, units=count, script=script))
    profile_input = header + (word(0) + request('latn', patterns[0][2], 4096, 1)) * 20
    return data, cases, profile_input


def main():
    data, cases, profile_input = corpus()
    if output := os.environ.get('PROGPU_RUST_PREPARATION_PROFILE_INPUT'):
        Path(output).write_bytes(profile_input)
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'managed_preparation_benchmark'], cwd=ROOT, check=True)
    binary = ROOT / 'target/release/examples/managed_preparation_benchmark'
    with tempfile.TemporaryDirectory(prefix='progpu-preparation-benchmark-') as directory:
        work = Path(directory)
        (work / 'Control.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
            f'<Compile Include="{escape(str(ROOT / "src/ProGPU.Rust/tests/managed_preparation.cs"))}"/>'
            f'<ProjectReference Include="{escape(str(ROOT / "src/ProGPU.Text/ProGPU.Text.csproj"))}"/>'
            '</ItemGroup></Project>')
        subprocess.run([dotnet, 'build', 'Control.csproj', '-c', 'Release', '--nologo', '-v:q',
                        '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={work / "artifacts"}'], cwd=work, check=True)
        assembly = work / 'artifacts/bin/Control/release/Control.dll'
        observed = subprocess.run([dotnet, str(assembly)], input=word(0), capture_output=True, check=True)
        candidates = [profile for profile in [151, 160, 170] if subprocess.run(
            [str(binary), '--witness', str(profile)], capture_output=True, check=True).stdout == observed.stdout]
        assert len(candidates) == 1, 'unsupported or ambiguous actual source normalization'
        profile = str(candidates[0])
        commands = [[str(binary), profile], [dotnet, str(assembly)]]
        before = os.environ.get('PROGPU_RUST_PREPARATION_BEFORE')
        if before:
            commands.append([before, profile])
        trials = []
        for trial in range(3):
            results = [None] * len(commands)
            for index in [(trial + offset) % len(commands) for offset in range(len(commands))]:
                results[index] = subprocess.run(commands[index], input=data, capture_output=True, check=True,
                    env=dict(os.environ, PROGPU_RUST_PREPARATION_BENCHMARK='1'))
            trials.append(results)
        assert all(result.stdout == trials[0][0].stdout for trial in trials for result in trial), 'complete original preparation outputs differ'
        names = ['rust', 'csharp', 'rust_before']
        for index, case in enumerate(cases):
            case['trials'] = []
            for trial in trials:
                row = {}
                for name, result in zip(names, trial):
                    lines = [line for line in result.stderr.decode().splitlines() if line.startswith('first_us=')]
                    assert len(lines) == len(cases)
                    row[name] = {k: float(v) for k, v in re.findall(r'(\w+)=([\d.]+)', lines[index])}
                case['trials'].append(row)
        receipt = dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(), host=platform.platform(),
            load_average=os.getloadavg(), normalization_profile=int(profile), source_runtime=observed.stderr.decode().strip(),
            scope='Three process trials with rotating order; first creation, five warmups, 40 samples. Input/font construction, normalization witnesses and serialization excluded. Actual owned buffer creation timed; retirement timed separately; final complete 31-field records and retained context/state matched. Source GC allocations observed; Rust allocator counts require Instruments. Not full shaping/layout/frame qualification.',
            input_sha256=hashlib.sha256(data).hexdigest(), output_sha256=hashlib.sha256(trials[0][0].stdout).hexdigest(),
            rust_binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
            csharp_assembly_sha256=hashlib.sha256(assembly.read_bytes()).hexdigest(),
            source_hashes={p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in
                ['src/ProGPU.Text/OpenTypeTextShaper.cs','src/ProGPU.Rust/src/text/preparation.rs',
                 'src/ProGPU.Rust/src/text/preparation/pool.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/buffer.rs',
                 'src/ProGPU.Rust/examples/managed_preparation_benchmark.rs','src/ProGPU.Rust/tests/managed_preparation.cs']},workloads=cases)
        if before:
            receipt['rust_before_binary_sha256'] = hashlib.sha256(Path(before).read_bytes()).hexdigest()
        output = ROOT / 'src/ProGPU.Rust/perf/managed-preparation-baseline.json'
        output.write_text(json.dumps(receipt, indent=2)+'\n')
        print(f'{len(cases)} complete-output matched workloads, three trials; {output}')


if __name__ == '__main__':
    main()
