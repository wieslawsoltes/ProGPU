#!/usr/bin/env python3
"""Compare complete original managed boundary walks with explicit Rust source facts."""
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


def main():
    patterns = {
        'ascii': [ord(c) for c in 'ProGPU sample 123 '],
        'mixed': [65, 0x301, 0x600, 66, 13, 10, 0x915, 0x94d, 0x915, 0x1100, 0x1160, 0x11a8],
        'reserved-emoji': [0x2388, 0x301, 0x200d, 0x2605, 65, 0xd83d, 0xdc69, 0x200d, 0x2607],
        'malformed': [0xd800, 0x301, 0xdc00, 0x600, 0xd800, 65, 0xd800, 0xdc00, 0xdc00],
        'combining': [0x301], 'prepend': [0x600], 'regional': [0xd83c, 0xdde6],
    }
    cases, data = [], bytearray()
    for name, pattern in patterns.items():
        for count in [128, 4096]:
            units = (pattern * ((count + len(pattern) - 1) // len(pattern)))[:count]
            data += struct.pack('<I', count) + struct.pack('<' + 'H' * count, *units)
            cases.append(dict(pattern=name, units=count))
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'managed_grapheme_benchmark'], cwd=ROOT, check=True)
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-managed-grapheme-benchmark-') as temporary:
        work = Path(temporary)
        includes = ''.join(f'<Compile Include="{escape(str(ROOT / p))}"/>' for p in
                           ['src/ProGPU.Rust/tests/managed_grapheme_profile.cs', 'src/ProGPU.Text/OriginalGraphemePolicy.cs'])
        (work / 'Control.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems><TreatWarningsAsErrors>true</TreatWarningsAsErrors>'
            f'</PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>')
        subprocess.run([dotnet, 'build', 'Control.csproj', '-c', 'Release', '-o', 'out', '--nologo', '-v:q'], cwd=work, check=True)
        binary = ROOT / 'target/release/examples/managed_grapheme_benchmark'
        assembly = work / 'out/Control.dll'
        environment = dict(os.environ, PROGPU_RUST_GRAPHEME_BENCHMARK='1')
        commands = [[str(binary)], [dotnet, str(assembly)]]
        before = os.environ.get('PROGPU_RUST_GRAPHEME_BEFORE')
        if before:
            commands.append([before])
        trials = []
        for trial in range(3 if before else 1):
            results = [None] * len(commands)
            for index in [(trial + offset) % len(commands) for offset in range(len(commands))]:
                results[index] = subprocess.run(commands[index], input=data, capture_output=True,
                                                env=environment, check=True)
            trials.append(results)
        results = trials[0]
        assert all(result.stdout == results[0].stdout for trial in trials for result in trial), 'trial outputs differ'
        assert all(result.stdout == results[0].stdout for result in results), 'complete source/before/after boundaries differ'
        cursor = 0
        timings = [[dict((k, float(v)) for k, v in re.findall(r'(\w+)=([\d.]+)', line))
                    for line in result.stderr.decode().splitlines() if line.startswith('first_us=')]
                   for result in results]
        assert all(len(values) == len(cases) for values in timings)
        for index, case in enumerate(cases):
            count = struct.unpack_from('<I', results[0].stdout, cursor)[0]
            encoded = results[0].stdout[cursor:cursor + 4 + 4 * count];cursor += len(encoded)
            case.update(boundaries=count, output_sha256=hashlib.sha256(encoded).hexdigest(),
                        rust=timings[0][index], csharp=timings[1][index])
            if before:
                case['rust_before'] = timings[2][index]
                case['timing_trials'] = [
                    {version: dict((k, float(v)) for k, v in re.findall(r'(\w+)=([\d.]+)',
                        [line for line in result.stderr.decode().splitlines() if line.startswith('first_us=')][index]))
                     for version, result in zip(['rust', 'csharp', 'rust_before'], trial)}
                    for trial in trials]
        assert cursor == len(results[0].stdout)
        receipt = dict(utc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
            scope=('Three matched process trials with rotating implementation order. ' if before else
                   'Initial diagnostic baseline. ') + 'No full shaping/frame qualification. '
                  'Input/result allocation and serialization outside timers; source GetLength calls, '
                  'boundary writes, original JIT/first-use work and exact original UTF-16 remain timed. '
                  'One first call, five warmups and 40 measured rounds. Rust allocations uninstrumented.',
            host=platform.platform(), load_average=os.getloadavg(),
            source_runtime=results[1].stderr.decode().splitlines()[0],
            input_sha256=hashlib.sha256(data).hexdigest(), rust_binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
            csharp_assembly_sha256=hashlib.sha256(assembly.read_bytes()).hexdigest(),
            source_sha256={p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in
                ['src/ProGPU.Text/OriginalGraphemePolicy.cs','src/ProGPU.Rust/tests/managed_grapheme_profile.cs',
                 'src/ProGPU.Rust/examples/managed_grapheme_benchmark.rs','src/ProGPU.Rust/src/text/unicode/grapheme.rs',
                 'src/ProGPU.Rust/src/text/unicode/grapheme_profile_data.rs',
                 'src/ProGPU.Rust/src/text/unicode/grapheme_properties_16.bin']}, workloads=cases)
        if before:
            receipt['rust_before_binary_sha256'] = hashlib.sha256(Path(before).read_bytes()).hexdigest()
        output = ROOT / ('src/ProGPU.Rust/perf/managed-grapheme-paired.json' if before
                         else 'src/ProGPU.Rust/perf/managed-grapheme-baseline.json')
        output.write_text(json.dumps(receipt, indent=2)+'\n')
        print(f'{len(cases)} complete-output matched workloads; {output}')


if __name__ == '__main__':
    main()
