#!/usr/bin/env python3
"""Exercise the actual pinned HLSL writer; no graphics device or shader execution."""
import argparse
import hashlib
import itertools
import json
from pathlib import Path
import re
import subprocess
import tempfile


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--translator', type=Path, required=True)
    ap.add_argument('--original-translator', type=Path, required=True)
    ap.add_argument('--output', type=Path, required=True)
    args = ap.parse_args()
    results = []
    # Lengths below, equal to, above and not divisible by the complete XYZ size.
    cases = [(f'xyz-{n}', (4, 3, 2), n, 'f32', '') for n in (1, 7, 24, 25, 53)]
    cases += [('binary-shape', (256, 1, 1), 512, 'vec4<u32>', ''),
              ('atomic', (2, 3, 4), 25, 'atomic<u32>', ''),
              ('nested', (2, 2, 2), 17, 'array<vec2<i32>, 3>', ''),
              ('struct-element', (2, 3, 2), 29, 'Record',
               'struct Record { a: f32, b: i32, c: vec2<u32>, }'),
              ('scalar-only', (3, 2, 1), 0, 'u32', '')]
    with tempfile.TemporaryDirectory(prefix='progpu-hlsl-generation-') as tmp:
        temp = Path(tmp)
        for name, dims, count, element, declaration in cases:
            globals_text = 'var<workgroup> scalar: u32;\n'
            if count:
                globals_text += f'var<workgroup> values: array<{element}, {count}>;\n'
                access = ('atomicStore(&values[0], atomicLoad(&values[0]));'
                          if element.startswith('atomic') else 'values[0] = values[0];')
            else:
                access = ''
            # Collide with suggested generated names through both a local and a global.
            globals_text += 'var<workgroup> workgroup_zero_local_index: u32;\n'
            body = ('let workgroup_zero_index = id.x; scalar = scalar + workgroup_zero_index; '
                    'workgroup_zero_local_index = workgroup_zero_local_index; ' + access)
            source = declaration + '\n' + globals_text + '\n'.join(
                f'@compute @workgroup_size({dims[0]}, {dims[1]}, {dims[2]}) '
                f'fn {entry}(@builtin(local_invocation_id) id: vec3<u32>) {{ {body} }}'
                for entry in ('main', 'other'))
            wgsl = temp / f'{name}.wgsl'; hlsl = temp / f'{name}.hlsl'
            wgsl.write_text(source)
            subprocess.run([str(args.translator), str(wgsl), str(hlsl)], check=True,
                           capture_output=True, timeout=10)
            output = hlsl.read_text()
            bodies = re.findall(r'void (?:main|other)\([^)]*\)\s*\{(.*?)\n\}', output, re.S)
            assert len(bodies) == 2, (name, 'entrypoint bodies')
            for body in bodies:
                before, separator, _ = body.partition('GroupMemoryBarrierWithGroupSync();')
                assert separator and body.count('GroupMemoryBarrierWithGroupSync();') == 1
                assert 'scalar = (uint)0;' in before
                assert 'if (all(__local_invocation_id == uint3(0u, 0u, 0u)))' in before
                if count:
                    match = re.search(r'uint (\w+) = __local_invocation_id.x \+ (\d+)u \* '
                                      r'\(__local_invocation_id.y \+ (\d+)u \* __local_invocation_id.z\);', before)
                    assert match and tuple(map(int, match.groups()[1:])) == dims[:2]
                    local = match.group(1)
                    loop = re.search(r'for \(uint (\w+) = ' + local + r'; \1 < (\d+)u; \) \{\s*'
                                     r'values\[\1\] = ([^;]+);\s*\1 \+= (\d+)u;', before)
                    assert loop and int(loop.group(2)) == count
                    stride = int(loop.group(4)); assert stride == dims[0] * dims[1] * dims[2]
                    # Interpret the emitted index/stride for every real invocation, not a
                    # test-side copy of the writer's partitioning loop.
                    visits = []
                    for x, y, z in itertools.product(*(range(d) for d in dims)):
                        first = x + int(match.group(2)) * (y + int(match.group(3)) * z)
                        visits.extend(range(first, int(loop.group(2)), stride))
                    assert sorted(visits) == list(range(count)), (name, 'coverage/uniqueness')
                    assert 'values = ' not in before
                    assert loop.group(3).endswith('0'), (name, 'typed zero', loop.group(3))
                    assert local != 'workgroup_zero_local_index', (name, 'global name collision')
                else:
                    assert 'workgroup_zero_index' not in before
            if not count:
                original = temp / 'original.hlsl'
                subprocess.run([str(args.original_translator), str(wgsl), str(original)],
                               check=True, capture_output=True, timeout=10)
                assert original.read_bytes() == hlsl.read_bytes(), 'Non-array output changed'
            results.append({'case': name, 'workgroupSize': dims, 'arrayLength': count,
                            'wgslSha256': sha(wgsl), 'hlslSha256': sha(hlsl), 'passed': True})
    args.output.write_text(json.dumps({'translatorSha256': sha(args.translator),
                                      'originalTranslatorSha256': sha(args.original_translator),
                                      'gpuExecution': False, 'cases': results}, indent=2) + '\n')
    print(f'{len(results)} actual writer generation controls passed')


if __name__ == '__main__':
    main()
