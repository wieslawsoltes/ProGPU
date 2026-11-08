#!/usr/bin/env python3
"""Project ProGPU's checked-in native Unicode property facts into Rust arrays."""
from __future__ import annotations
import argparse
import hashlib
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCES = [
    ROOT / 'src/ProGPU.Native/src/Text/progpu_native_unicode_data.generated.hpp',
    ROOT / 'src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_categories.generated.hpp',
]
SELECTED = {
    'unicode_script_tags': ('SCRIPT_TAGS', 1),
    'unicode_script_ranges': ('SCRIPT_RANGES', 3),
    'unicode_combining_class_ranges': ('COMBINING_RANGES', 3),
    'unicode_bidi_class_ranges': ('BIDI_RANGES', 1),
    'unicode_bidi_bracket_records': ('BIDI_BRACKETS', 1),
    'unicode_mirror_pairs': ('MIRROR_PAIRS', 2),
    'unicode_vertical_pairs': ('VERTICAL_PAIRS', 2),
    'unicode_general_category_ranges': ('CATEGORY_RANGES', 3),
    'unicode_decimal_digit_zeros': ('DIGIT_ZEROS', 1),
    'unicode_grapheme_ranges': ('GRAPHEME_RANGES', 3),
    'unicode_extended_pictographic_ranges': ('PICTOGRAPHIC_RANGES', 3),
    'unicode_indic_conjunct_ranges': ('CONJUNCT_RANGES', 3),
}
OUTPUT = ROOT / 'src/ProGPU.Rust/src/text/unicode/data.rs'

def generate() -> str:
    lines = ['//! Generated from the original ProGPU native Unicode tables.',
             '//! Regenerate: python3 eng/progpu-generate-rust-unicode.py --write',
             '//! Source tables retain their own Unicode/.NET versions and contracts.',
             '//! Script/CCC/directional facts inherit the original managed generators\' provenance,',
             '//! including Unicode data sourced through HarfBuzz; no shaping engine is included.']
    arrays = {}
    for path in SOURCES:
        source = path.read_bytes()
        lines.append(f'// {path.relative_to(ROOT)} sha256={hashlib.sha256(source).hexdigest()}')
        for name, (rust_name, stride) in SELECTED.items():
            match = re.search(r'inline constexpr std::array<std::uint(32|64)_t,\s*(\d+)>\s+' + name + r'\s*\{(.*?)\};', source.decode(), re.S)
            if match is None:
                continue
            if name in arrays:
                raise ValueError(f'Duplicate array {name}')
            bits, count, body = int(match[1]), int(match[2]), match[3]
            token = r'(?:0x[0-9A-Fa-f]+|\d+)(?:ULL|U)'
            if re.sub(token, '', body).replace(',', '').strip():
                raise ValueError(f'Unexpected initializer for {name}')
            values = [int(v.rstrip('UL'), 0) for v in re.findall(token, body)]
            if len(values) != count or count % stride or any(v >= 1 << bits for v in values):
                raise ValueError(f'Invalid extent/value for {name}')
            arrays[name] = values
            if stride > 1:
                previous_end = -1
                for i in range(0, count, stride):
                    start, end = (values[i], values[i + 1]) if stride == 3 else (values[i], values[i])
                    if start <= previous_end or start > end:
                        raise ValueError(f'Unordered original ranges in {name}')
                    previous_end = end
            lines.extend(['', '#[rustfmt::skip]', f'pub(super) static {rust_name}: [u{bits}; {count}] = ['])
            width = 4 if bits == 64 else 12
            for i in range(0, count, width):
                parts = [f'0x{v:016x}' if bits == 64 else str(v) for v in values[i:i + width]]
                lines.append('    ' + ', '.join(parts) + ',')
            lines.append('];')
    if set(arrays) != set(SELECTED):
        raise ValueError(f'Missing arrays: {set(SELECTED) - set(arrays)}')
    if any(v >= len(arrays['unicode_script_tags']) for v in arrays['unicode_script_ranges'][2::3]):
        raise ValueError('Invalid script tag reference')
    if any(v > 255 for v in arrays['unicode_combining_class_ranges'][2::3]):
        raise ValueError('Invalid combining class')
    if any(v >= 30 for v in arrays['unicode_general_category_ranges'][2::3]):
        raise ValueError('Invalid original category')
    for name, maximum in [('unicode_grapheme_ranges', 13), ('unicode_extended_pictographic_ranges', 1), ('unicode_indic_conjunct_ranges', 3)]:
        if any(v > maximum for v in arrays[name][2::3]):
            raise ValueError(f'Invalid original property in {name}')
    mask = (1 << 21) - 1
    for name, maximum in [('unicode_bidi_class_ranges', 22), ('unicode_bidi_bracket_records', 2)]:
        previous = -1
        for value in arrays[name]:
            start = value & mask
            end = (value >> 21) & mask if name.endswith('ranges') else start
            if start <= previous or end < start or value >> 42 > maximum:
                raise ValueError(f'Invalid packed {name}')
            previous = end
    zeros = arrays['unicode_decimal_digit_zeros']
    if any(b <= a + 9 for a, b in zip(zeros, zeros[1:])):
        raise ValueError('Overlapping original decimal inventories')
    return '\n'.join(lines) + '\n'

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--write', action='store_true')
    mode.add_argument('--check', action='store_true')
    args = parser.parse_args()
    text = generate()
    if args.write:
        OUTPUT.parent.mkdir(parents=True, exist_ok=True)
        OUTPUT.write_text(text)
    elif not OUTPUT.exists() or OUTPUT.read_text() != text:
        raise SystemExit('Rust Unicode tables are stale; run with --write')
    print(f'Rust Unicode tables: {len(text.encode())} bytes, {len(SELECTED)} exact source arrays')

if __name__ == '__main__':
    main()
