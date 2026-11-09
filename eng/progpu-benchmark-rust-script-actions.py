#!/usr/bin/env python3
"""Matched complete original script actions, preserving full glyph/state outputs."""
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


def font(space_only, identity=False):
    head = bytearray(54)
    struct.pack_into('>H', head, 18, 1000)
    nominal = struct.pack('>HHIIIIII', 12 if identity else 13, 0, 28, 0, 1,
                          0x20 if space_only else 0, 0x20 if space_only else (0xffff if identity else 0x10ffff),
                          3 if space_only else (0 if identity else 7))
    selector = struct.pack('>HII', 14, 30, 1) + bytes.fromhex('00fe0f') + struct.pack('>III', 0, 21, 1) + bytes.fromhex('0000410000')
    cmap = struct.pack('>HHHHIHHI', 0, 2, 0, 4, 20, 0, 5, 48) + nominal + selector
    result = bytearray(44)
    struct.pack_into('>IH', result, 0, 0x10000, 2)
    for n, (tag, data) in enumerate([(b'head', head), (b'cmap', cmap)]):
        struct.pack_into('>4sIII', result, 12 + n * 16, tag, 0, len(result), len(data))
        result += data
    return result


def request(script, points, count, actions, fallback=False):
    records=[]
    for i in range(count):
        values=[((i+1)*(n+3)*7) & 255 for n in range(31)]
        values[0]=7;values[1]=i;values[2]=(i//len(points))*len(points);values[3]=i;values[4]=points[i%len(points)]
        if fallback:
            values[0]=values[4];values[10]=6
        records.extend(values)
    script=list(script.encode('ascii'))
    values=[len(script),*script,1,1,count,*([65]*count),0,0,len(actions),*actions,count,*records]
    return word(len(values))+b''.join(map(word,values))


def corpus():
    fallback_only = os.environ.get('PROGPU_RUST_ACTIONS_FALLBACK_ONLY') == '1'
    vowel_only = os.environ.get('PROGPU_RUST_ACTIONS_VOWEL_ONLY') == '1'
    khmer_only = os.environ.get('PROGPU_RUST_ACTIONS_KHMER_ONLY') == '1'
    fonts=[font(False, fallback_only),font(True)]
    header=word(len(fonts))+b''.join(word(len(f))+f for f in fonts)
    data=bytearray(header);cases=[]
    patterns=[
        ('beginning','latn',[0x301,65],[0]),
        ('directional','latn',[0x28,0x29,0x3008,0x3009,0x2014],[7]),
        ('hangul','hang',[0x1100,0x1161,0x11a8,0xac00,0xd7cb],[9]),
        ('combining','arab',[65,0x301,0x327,0x654,0x655,0x8ca],[10]),
        ('long-combining','arab',[0x301,0x327,0x654,0x655,0x8ca],[10]),
        ('hebrew','hebr',[0x5e9,0x5bc,0x5c1],[11]),
        ('use','tibt',[0xf73,0x344,0x301,65],[14]),
        ('thai','thai',[0xe01,0xe48,0xe33],[15]),
        ('fractions','latn',[0x31,0x32,0x2044,0x33,0x34,65],[16]),
        ('stage-state','latn',[65,66,67],[17,18,19,20]),
        ('joining-arabic','arab',[0x628,0x301,0x627,0x628,0x200c,0x628],[21]),
        ('joining-syriac','syrc',[0x715,0x710,0x301,0x710,0x628,0x710],[21]),
        ('joining-transparent','arab',[0x628]+[0x301]*31,[21]),
        ('joining-disabled','latn',[0x628,0x710,0x715],[21]),
    ]
    if fallback_only:
        patterns = [
            ('fallback-forms','arab',[0x628,0x644,0x647,0x678],[47]),
            ('fallback-triples','arab',[0xfedf,0xfee0,0xfeea,0xfedf,0xfee4,0xfea0],[48]),
            ('fallback-pairs','arab',[0xfe91,0xfea0,0xfedf,0xfe8e],[48]),
            ('fallback-marks','arab',[0x651,0x64b,0x651,0x64c,0x651,0x64e],[48]),
            ('fallback-skipped-marks','arab',[0xfedf]+[0x301]*15+[0xfe8e],[48]),
            ('fallback-joining-sequence','arab',[0x644,0x644,0x647,0x301,0x644,0x627],[21,63]),
        ]
    if vowel_only:
        patterns = [
            ('vowel-pairs','deva',[0x905,0x93e],[68]),
            ('vowel-triples','deva',[0x930,0x94d,0x907],[68]),
            ('vowel-pair-shadow','gujr',[0xa85,0xabe,0xac5],[68]),
            ('vowel-miss','deva',[0x905,0x93f],[68]),
            ('vowel-disabled-script','latn',[0x905,0x93e],[68]),
        ]
    joining_only = os.environ.get('PROGPU_RUST_ACTIONS_JOINING_ONLY') == '1'
    if khmer_only:
        patterns = [
            ('khmer-base', 'khmr', [0x1780, 0x17b7, 0x17c6, 0x17c7], [69]),
            ('khmer-ro', 'khmr', [0x1780, 0x17d2, 0x179a, 0x17c1, 0x17b7], [69]),
            ('khmer-broken', 'khmr', [0x17c1, 0x17c1, 0x17c7, 0x17c6], [69]),
            ('khmer-long-subscripts', 'khmr', [0x17d2, 0x179a, 0x17d2, 0x1780], [69]),
            ('khmer-joiners', 'khmr', [0x200c, 0x200d], [69]),
            ('khmer-joined-marks', 'khmr', [0x1780]+[0x200d]*15+[0x17c6, 0x17c1], [69]),
            ('khmer-disabled-script', 'latn', [0x1780, 0x17d2, 0x179a, 0x17c1], [69]),
        ]
    if joining_only:
        patterns = [pattern for pattern in patterns if pattern[0].startswith('joining-')]
    for index in range(len(fonts)):
        for name,script,points,actions in patterns:
            for count in [128,4096]:
                data+=word(index)+request(script,points,count,actions,fallback_only)
                cases.append(dict(font='space-only' if index else ('identity-bmp' if fallback_only else 'full'),pattern=name,records=count,script=script,actions=actions))
    selected = patterns[0 if joining_only or fallback_only or vowel_only or khmer_only else 4]
    profile_input=header+(word(0)+request(selected[1],selected[2],4096,selected[3],fallback_only))*20
    return data,cases,profile_input


def main():
    data, cases, profile_input = corpus()
    if output := os.environ.get('PROGPU_RUST_ACTIONS_PROFILE_INPUT'):
        Path(output).write_bytes(profile_input)
    dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
    subprocess.run(['cargo', 'build', '--locked', '--release', '--no-default-features',
                    '--example', 'managed_script_actions_benchmark'], cwd=ROOT, check=True)
    binary = ROOT / 'target/release/examples/managed_script_actions_benchmark'
    with tempfile.TemporaryDirectory(prefix='progpu-script-actions-benchmark-') as directory:
        work = Path(directory)
        (work / 'Control.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>'
            '<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
            f'<Compile Include="{escape(str(ROOT / "src/ProGPU.Rust/tests/managed_script_actions.cs"))}"/>'
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
        before = os.environ.get('PROGPU_RUST_ACTIONS_BEFORE')
        if before:
            commands.append([before, profile])
        trials = []
        for trial in range(3):
            results = [None] * len(commands)
            for index in [(trial + offset) % len(commands) for offset in range(len(commands))]:
                results[index] = subprocess.run(commands[index], input=data, capture_output=True, check=True,
                    env=dict(os.environ, PROGPU_RUST_ACTIONS_BENCHMARK='1'))
            trials.append(results)
        assert all(result.stdout == trials[0][0].stdout for trial in trials for result in trial), 'complete original script-actions outputs differ'
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
            scope='Three rotating-order process trials; first action, five warmups, 40 samples. Actual owned buffer preparation, typed raw-record restoration, witnesses and serialization excluded. Selected production actions timed; complete 31-field records, state and source context matched. Source GC allocation counters observed; Rust allocator events and full shaping/layout/frame qualification remain open.',
            input_sha256=hashlib.sha256(data).hexdigest(), output_sha256=hashlib.sha256(trials[0][0].stdout).hexdigest(),
            rust_binary_sha256=hashlib.sha256(binary.read_bytes()).hexdigest(),
            csharp_assembly_sha256=hashlib.sha256(assembly.read_bytes()).hexdigest(),
            source_hashes={p:hashlib.sha256((ROOT/p).read_bytes()).hexdigest() for p in
                ['src/ProGPU.Text/OpenTypeTextShaper.cs','src/ProGPU.Rust/src/text/script_actions.rs',
                 'src/ProGPU.Rust/src/text/script_actions/joining.rs',
                 'src/ProGPU.Rust/src/text/script_actions/joining_data.rs',
                 'src/ProGPU.Rust/src/text/script_actions/arabic_fallback.rs',
                 'src/ProGPU.Rust/src/text/script_actions/arabic_fallback_data.rs',
                 'src/ProGPU.Rust/src/text/script_actions/vowel.rs',
                 'src/ProGPU.Rust/src/text/script_actions/vowel_data.rs',
                 'src/ProGPU.Rust/src/text/script_actions/khmer.rs',
                 'src/ProGPU.Rust/src/text/script_actions/khmer_syllables.rs',
                 'src/ProGPU.Rust/src/text/preparation.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/buffer.rs',
                 'src/ProGPU.Rust/examples/managed_script_actions_benchmark.rs','src/ProGPU.Rust/tests/managed_script_actions.cs']},workloads=cases)
        if before:
            receipt['rust_before_binary_sha256'] = hashlib.sha256(Path(before).read_bytes()).hexdigest()
        if os.environ.get('PROGPU_RUST_ACTIONS_KHMER_ONLY') == '1':
            receipt['measurement_limits'] = 'Four disabled-script no-op workloads are at the observed 0..0.042 microsecond clock/format resolution; their relative latency is not qualified. Retain original samples and complete outputs.'
        name = 'managed-khmer-baseline.json' if os.environ.get('PROGPU_RUST_ACTIONS_KHMER_ONLY') == '1' else 'managed-vowel-baseline.json' if os.environ.get('PROGPU_RUST_ACTIONS_VOWEL_ONLY') == '1' else 'managed-arabic-fallback-baseline.json' if os.environ.get('PROGPU_RUST_ACTIONS_FALLBACK_ONLY') == '1' else 'managed-joining-baseline.json' if os.environ.get('PROGPU_RUST_ACTIONS_JOINING_ONLY') == '1' else 'managed-script-actions-baseline.json'
        output = ROOT / 'src/ProGPU.Rust/perf' / name
        output.write_text(json.dumps(receipt, indent=2)+'\n')
        print(f'{len(cases)} complete-output matched workloads, three trials; {output}')


if __name__ == '__main__':
    main()
