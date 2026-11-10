#!/usr/bin/env python3
"""Compare original C++/C# and Rust line resolution; retain compact receipts only.

No engine implementation is imported. Reused arrays, original compiler defaults,
five warmups and forty timed rounds per process; managed JIT defaults are retained.
This baseline does not claim complete layout/frame or steady-state JIT parity.
"""
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import tempfile
from xml.sax.saxutils import quoteattr

ROOT = Path(__file__).resolve().parents[1]
PATTERNS = {'ascii': 'ProGPU sample ($123.45) - end ',
            'mixed': 'A\u0301 العربية 日本語\r\n क्क ',
            'emoji': '🇵🇱👩\u0301\u200d👧👍🏽🇦 \u1100\u1161\u11a8 '}
CPP = ['src/ProGPU.Rust/tests/native_line_break_benchmark.cpp',
       'src/ProGPU.Native/src/Text/progpu_native_unicode.cpp',
       'src/ProGPU.Native/src/Text/progpu_native_unicode_grapheme.cpp',
       'src/ProGPU.Native/src/Text/progpu_native_unicode_line_break.cpp']
CS = ['src/ProGPU.Rust/tests/managed_line_break_benchmark.cs',
      'src/ProGPU.Text/UnicodeLineBreakResolver.cs',
      'src/ProGPU.Text/UnicodeLineBreakData.Generated.cs',
      'src/ProGPU.Text/UnicodeGraphemeData.Generated.cs']

def capture(command):
    r = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, check=True)
    return r.stdout.strip()

def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()

def main():
    cc = os.environ.get('CXX', 'clang++')
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-line-baseline-') as temporary:
        directory = Path(temporary)
        capture(['cargo', 'build', '--locked', '--release', '--no-default-features', '--example', 'line_break_benchmark'])
        cpp = directory / 'native'
        capture([cc, '-std=c++20', '-O2', '-Wall', '-Wextra', '-Werror',
                 '-Isrc/ProGPU.Native/include', '-Isrc/ProGPU.Native/src/Text', *CPP, '-o', str(cpp)])
        includes = ''.join(f'<Compile Include={quoteattr(str(ROOT / p))}/>' for p in CS)
        project = directory / 'Control.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup>{includes}</ItemGroup></Project>')
        capture([dotnet, 'build', str(project), '-c', 'Release', '-o', str(directory / 'out'), '--nologo', '-v:q'])
        dll = directory / 'out/Control.dll'
        rust = ROOT / 'target/release/examples/line_break_benchmark'
        sources = CPP + CS + ['src/ProGPU.Rust/examples/line_break_benchmark.rs', 'src/ProGPU.Rust/src/text/unicode/line_break.rs', 'src/ProGPU.Rust/src/text/unicode/data.rs']
        receipt = {'method': '5 warmups, 40 serial measured rounds; resolution plus identical interleaved class/boundary hashing; decoding/allocation outside timing',
                   'qualification': 'Baseline only; original default compiler/JIT settings; no full-stack, worst-case scaling or steady-state managed JIT claim. Further optimization requires Instruments evidence.',
                   'rust': capture(['rustc', '--version']), 'cpp': capture([cc, '--version']), 'dotnet': capture([dotnet, '--version']),
                   'cpp_flags': '-std=c++20 -O2 -Wall -Wextra -Werror',
                   'source_sha256': {p: sha(ROOT / p) for p in sources},
                   'binary_sha256': {'rust': sha(rust), 'cpp': sha(cpp), 'managed': sha(dll)}, 'inputs': []}
        for kind, pattern in PATTERNS.items():
            for mode in ['unicode', 'edit']:
                for repeats in [1, 256, 2048]:
                    commands = [('rust', [str(rust), kind, mode, str(repeats)]), ('cpp', [str(cpp), kind, mode, str(repeats)])]
                    if mode == 'unicode':
                        commands.append(('managed', [dotnet, str(dll), kind, str(repeats)]))
                    item = {'kind': kind, 'mode': mode, 'repeats': repeats, 'utf16_le_sha256': hashlib.sha256((pattern*repeats).encode('utf-16le')).hexdigest(), 'runs': []}
                    identity = None
                    for name, command in commands:
                        output = capture(command)
                        fields = dict(re.findall(r'(\w+)=([0-9.]+)', output))
                        current = {k: fields[k] for k in ['units', 'scalars', 'scratch_bytes', 'checksum']}
                        if identity is not None and identity != current:
                            raise ValueError(f'Baseline source mismatch: {kind} {mode} {repeats} {name}')
                        identity = current
                        item['runs'].append({'implementation': name, 'output': output})
                    receipt['inputs'].append(item)
                    print(kind, mode, repeats, ' | '.join(r['implementation']+': '+r['output'] for r in item['runs']), flush=True)
        receipt['temporary_bytes_removed'] = sum(p.stat().st_size for p in directory.rglob('*') if p.is_file())
        (ROOT / 'src/ProGPU.Rust/perf/line-break-baseline.json').write_text(json.dumps(receipt, indent=2)+'\n')

if __name__ == '__main__':
    main()
