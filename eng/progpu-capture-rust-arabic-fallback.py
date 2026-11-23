#!/usr/bin/env python3
"""Verify public Arabic forms and observe original fallback rules through API calls.

No generated source array or external implementation is read. Candidate sequences
come from public Unicode facts; owned fonts witness selection and collision order.
"""
from pathlib import Path
import argparse
import hashlib
import itertools
import json
import os
import struct
import subprocess
import tempfile
import urllib.request
from xml.sax.saxutils import escape

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/ProGPU.Rust/src/text/script_actions/arabic_fallback_data.rs'
INVENTORY = ROOT / 'src/ProGPU.Rust/tests/support/arabic_fallback_inventory.txt'
URL = 'https://www.unicode.org/Public/17.0.0/ucd/UnicodeData.txt'
DIGEST = '2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c'
KINDS = ['<initial>', '<medial>', '<final>', '<isolated>']


def word(value):
    return struct.pack('<I', value)


def font(mappings=None):
    head = bytearray(54)
    struct.pack_into('>H', head, 18, 1000)
    groups = [(1, 0xffff, 1)] if mappings is None else [(cp, cp, value) for cp, value in sorted(mappings.items())]
    nominal = struct.pack('>HHIII', 12 if mappings is None else 13, 0, 16 + 12 * len(groups), 0, len(groups))
    nominal += b''.join(struct.pack('>III', *group) for group in groups)
    cmap = struct.pack('>HHHHI', 0, 1, 0, 4, 12) + nominal
    result = bytearray(44)
    struct.pack_into('>IH', result, 0, 0x10000, 2)
    for index, (tag, data) in enumerate([(b'head', head), (b'cmap', cmap)]):
        struct.pack_into('>4sIII', result, 12 + index * 16, tag, 0, len(result), len(data))
        result += data
    return bytes(result)


class Source:
    def __init__(self, work):
        self.work = work
        self.dotnet = os.environ.get('PROGPU_RUST_DOTNET', 'dotnet')
        self.assembly = work / 'artifacts/bin/Control/release/Control.dll'
        self.requests = self.records = 0
        self.input_hash = hashlib.sha256()
        self.output_hash = hashlib.sha256()
        harness = ROOT / 'src/ProGPU.Rust/tests/managed_arabic_fallback_capture.cs'
        project = ROOT / 'src/ProGPU.Text/ProGPU.Text.csproj'
        (work / 'Control.csproj').write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
            '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
            f'<Compile Include="{escape(str(harness))}"/><ProjectReference Include="{escape(str(project))}"/>'
            '</ItemGroup></Project>')
        subprocess.run([self.dotnet, 'build', 'Control.csproj', '-c', 'Release', '--nologo', '-v:q',
                        '-p:UseArtifactsOutput=true', f'-p:ArtifactsPath={work / "artifacts"}'],
                       cwd=work, check=True)

    def query(self, fonts, requests):
        data = bytearray(word(len(fonts)))
        for value in fonts:
            data += word(len(value)) + value
        for selected, mask, records in requests:
            data += struct.pack('<III', selected, mask, len(records))
            self.records += len(records)
            for record in records:
                data += struct.pack('<III', *record)
        self.requests += len(requests)
        self.input_hash.update(data)
        result = subprocess.run([self.dotnet, str(self.assembly)], input=data, capture_output=True, check=True)
        self.output_hash.update(result.stdout)
        at = 0
        outputs = []
        for _ in requests:
            count = struct.unpack_from('<I', result.stdout, at)[0]
            at += 4
            outputs.append(struct.unpack_from('<' + 'I' * count, result.stdout, at))
            at += 4 * count
        assert at == len(result.stdout)
        return outputs


def public_candidates(data):
    forms, variants = {}, {}
    sequences, targets, marks = [], set(), []
    for line in data.decode().splitlines():
        fields = line.split(';')
        point = int(fields[0], 16)
        if 'ARABIC LIGATURE' in fields[1]:
            targets.add(point)
        if 0x600 <= point <= 0x8ff and fields[2] in ('Mn', 'Mc', 'Me'):
            marks.append(point)
        decomposition = fields[5].split()
        if not decomposition or decomposition[0] not in KINDS:
            continue
        sequence = tuple(int(cp, 16) for cp in decomposition[1:])
        sequences.append(sequence)
        if len(sequence) == 1:
            forms.setdefault(sequence[0], [0] * 4)[KINDS.index(decomposition[0])] = point
            variants.setdefault(sequence[0], set()).add(point)
        if len(sequence) == 2 and sequence[0] in (0x20, 0x640):
            variants.setdefault(sequence[1], set()).add(point)
    queries = set(itertools.product(marks, repeat=2))
    for source in sequences:
        for count in (2, 3):
            for start in range(len(source) - count + 1):
                for sequence in set(itertools.permutations(source[start:start + count])):
                    queries.update(itertools.product(*([cp, *sorted(variants.get(cp, set()))] for cp in sequence)))
    return forms, sorted(queries), targets


def observed_order(source, rows):
    inputs = {cp for sequence, _ in rows for cp in sequence}
    outputs = {result for _, result in rows}
    assert len(outputs) == len(rows) and not inputs & outputs, 'new ambiguity needs an independent order witness'
    mapping = {cp: 1 for cp in inputs}
    mapping.update({cp: cp for cp in outputs})
    ordered = []
    while outputs:
        result = source.query([font(mapping)], [(0, 16, [(65, 1, 7)] * len(rows[0][0]))])[0]
        assert len(result) == 1 and result[0] in outputs, 'unresolved original collision order'
        point = result[0]
        ordered.append(next(row for row in rows if row[1] == point))
        mapping[point] = 0
        outputs.remove(point)
    return ordered


def generate(source, data):
    forms, queries, targets = public_candidates(data)
    observed = {}
    for slot, action in enumerate((6, 4, 1, 0)):
        requests, points = [], []
        for start in range(0, 0x110001, 8192):
            block = list(range(start, min(0x110001, start + 8192)))
            points.append(block)
            requests.append((0, 1 << slot, [(cp, cp & 0xffff, action) for cp in block]))
        points.append([0xffffffff])
        requests.append((0, 1 << slot, [(0xffffffff, 0xffff, action)]))
        for block, result in zip(points, source.query([font()], requests)):
            assert len(block) == len(result)
            for cp, glyph in zip(block, result):
                if glyph != cp & 0xffff:
                    observed.setdefault(cp, [0] * 4)[slot] = glyph
    assert observed == forms, 'original presentation forms differ from pinned public facts'
    results = source.query([font()], [(0, 16, [(cp, cp, 7) for cp in sequence]) for sequence in queries])
    rows = [(sequence, result[0]) for sequence, result in zip(queries, results) if len(result) == 1]
    triples = observed_order(source, [row for row in rows if len(row[0]) == 3])
    pairs = observed_order(source, [row for row in rows if len(row[0]) == 2])
    ordinary, marks = [], []
    results = source.query([font()], [(0, 16, [(a, a, 7), (0x301, 0x301, 7), (b, b, 7)]) for (a, b), _ in pairs])
    for row, result in zip(pairs, results):
        if len(result) == 3:
            assert result == (row[0][0], 0x301, row[0][1])
            marks.append(row)
        else:
            assert result == (row[1], 0x301)
            ordinary.append(row)
    assert pairs == ordinary + marks, 'source pass order changed'
    # Broader output-hole witness: all BMP non-targets alias one input glyph.
    # Public ligature and private-use targets retain unique identities, then all
    # observed results are disabled. An unseen result must fail this capture.
    mapping = {cp: 1 for cp in range(1, 0x10000)}
    mapping.update({cp: cp for cp in targets | set(range(0xe000, 0xf900))})
    mapping.update({result: 0 for _, result in rows})
    for count in (2, 3):
        assert source.query([font(mapping)], [(0, 16, [(65, 1, 7)] * count)])[0] == (1,) * count
    lines = ['//! Pinned Unicode forms and independently observed original ProGPU fallback rules.',
             '//! No foreign source table or implementation is transcribed.',
             '//! Regenerate/check: eng/progpu-capture-rust-arabic-fallback.py.',
             '//! Public facts license: src/ProGPU.Rust/UNICODE-LICENSE.txt.',
             f'// {URL}', f'// SHA-256 {DIGEST}',
             'pub(super) const FORMS: &[(u32, [u16; 4])] = &[']
    inventory = ['# Independently observed original API inputs and results; public form facts.']
    for point, values in sorted(forms.items()):
        lines.append(f'    (0x{point:x}, [{", ".join(f"0x{v:x}" for v in values)}]),')
        inventory.append('form ' + ' '.join(f'{v:x}' for v in [point, *values]))
    lines.append('];')
    for name, rows, count, label in [('TRIPLES', triples, 3, 'triple'), ('PAIRS', ordinary, 2, 'pair'), ('MARKS', marks, 2, 'mark')]:
        lines.append(f'pub(super) const {name}: &[([u16; {count}], u16)] = &[')
        for sequence, result in rows:
            lines.append(f'    ([{", ".join(f"0x{v:x}" for v in sequence)}], 0x{result:x}),')
            inventory.append(label + ' ' + ' '.join(f'{v:x}' for v in [*sequence, result]))
        lines.append('];')
    return '\n'.join(lines) + '\n', '\n'.join(inventory) + '\n', dict(
        form_bases=len(forms), form_raw_queries=4 * 0x110002, public_candidate_sequences=len(queries),
        triple_rules=len(triples), pair_rules=len(ordinary), mark_rules=len(marks),
        requests=source.requests, records=source.records,
        request_sha256=source.input_hash.hexdigest(), response_sha256=source.output_hash.hexdigest())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--data-dir', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-arabic-fallback-facts-') as directory:
        work = Path(directory)
        path = (args.data_dir or work) / 'UnicodeData.txt'
        if not path.exists():
            path.write_bytes(urllib.request.urlopen(URL).read())
        data = path.read_bytes()
        assert hashlib.sha256(data).hexdigest() == DIGEST, 'public facts hash mismatch'
        generated, inventory, receipt = generate(Source(work), data)
    for path, content in [(OUTPUT, generated), (INVENTORY, inventory)]:
        if args.check:
            assert path.read_text() == content, f'stale original fallback capture: {path}'
        else:
            path.write_text(content)
    print(json.dumps(receipt))


if __name__ == '__main__':
    main()
