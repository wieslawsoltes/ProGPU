#!/usr/bin/env python3
"""Matched original Indic reordering with actual selected font GSUB plans."""
from pathlib import Path
import importlib.util
import os
import struct

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('actions', ROOT / 'eng/progpu-benchmark-rust-script-actions.py')
actions = importlib.util.module_from_spec(spec)
spec.loader.exec_module(actions)
word = actions.word


def corpus():
    # Authored font inputs already retained with their original full-output tests.
    frozen = (ROOT / 'src/ProGPU.Rust/browser/managed-script-actions.bin').read_bytes()
    assert frozen[:4] == b'PSA1'
    count, = struct.unpack_from('<I', frozen, 4)
    offset = 8
    fonts = []
    for _ in range(count):
        length, = struct.unpack_from('<I', frozen, offset)
        offset += 4
        fonts.append(frozen[offset:offset+length])
        offset += length
    fonts = [fonts[0], fonts[-16+5], fonts[-16+11]]
    header = word(3) + b''.join(word(len(f)) + f for f in fonts)
    facts = (ROOT / 'src/ProGPU.Rust/tests/support/script_properties.bin').read_bytes()
    properties = {cp: indic for cp, indic, _, _, _ in struct.iter_unpack('<IHBHB', facts[8:])}
    patterns = [
        ('initial-deva', 'deva', [0x930,0x94d,0x915,0x93f,0x900], [83]),
        ('initial-old', 'deva', [0x930,0x94d,0x915,0x94d,0x93f,0x900], [84]),
        ('initial-telu', 'telu', [0x930,0x94d,0x200d,0x915,0x93f,0x900], [83]),
        ('initial-knda', 'knda', [0x930,0x94d,0x200d,0x915,0x94d,0x93f,0x900], [84]),
        ('initial-mlym', 'mlym', [0xd4e,0x200d,0x915,0x94d,0x93f,0x900], [83]),
        ('preparation-reordering', 'deva', [0x930,0x94d,0x915,0x93f,0x900], [80,83,81]),
        ('initial-broken', 'deva', [0x93f,0x900], [83]),
        ('final-matras', 'taml', [0x93f,0x915,0x94d,0x930], [81]),
        ('final-reph', 'beng', [0xd4e,0x915,0x93f,0x900], [81]),
        ('final-pref', 'deva', [0x915,0x94d,0x930], [81]),
        ('initial-long-sort', 'deva', [0x900,0x93f,0x915,0x93c], [83]),
    ]

    def request(pattern, count):
        name, script, points, codes = pattern
        records = []
        for i in range(count):
            cp = points[i % len(points)]
            values = [0] * 31
            values[0] = [10,20,30][i % len(points) % 3]
            values[1] = values[2] = values[3] = i
            values[4] = cp
            values[10] = 7
            values[14] = 3
            values[16] = 0x10 if name == 'initial-long-sort' else ((i // len(points)) % 15 + 1) << 4
            if name == 'initial-broken': values[16] |= 4
            values[17], values[18] = properties[cp] & 255, properties[cp] >> 8
            if name == 'final-reph' and i % len(points) == 0: values[18] = 1
            if name == 'final-pref' and i % len(points) == 2:
                values[13] = 2
                values[18] = 11
                values[21] = values[22] = 1
            records.extend(values)
        script = list(script.encode('ascii'))
        values = [len(script),*script,1,1,count,*([65]*count),0,0,len(codes),*codes,count,*records]
        return word(len(values)) + b''.join(map(word, values))

    data = bytearray(header)
    cases = []
    for index, font_name in enumerate(['missing-gsub', 'mutating-pref-probe', 'combined-gsub']):
        for pattern in patterns:
            for count in [128,4096]:
                data += word(index) + request(pattern, count)
                cases.append(dict(font=font_name,pattern=pattern[0],records=count,script=pattern[1],actions=pattern[3]))
    profile = header + word(2) + request(patterns[0],4096)
    return data, cases, profile


if __name__ == '__main__':
    os.environ['PROGPU_RUST_ACTIONS_INDIC_REORDER_ONLY'] = '1'
    actions.corpus = corpus
    actions.main()
