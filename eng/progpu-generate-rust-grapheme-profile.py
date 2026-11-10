#!/usr/bin/env python3
"""Pin public Unicode facts and project the original managed grapheme repertoire."""
from pathlib import Path
import argparse
import hashlib
import re
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'src/ProGPU.Rust/src/text/unicode/grapheme_profile_data.rs'
PACKED = OUTPUT.with_name('grapheme_properties_16.bin')
FILES = {
    ('16.0.0', 'auxiliary/GraphemeBreakProperty.txt'): 'c29360bd6f7132811d701d29069541e827eb44bfc4c8fbde8c370d6982689dc1',
    ('16.0.0', 'emoji/emoji-data.txt'): 'f1365a5173eee18e1f98b240cdc492e84a25f1ce7e0c9d1094eb29c41a22696a',
    ('17.0.0', 'auxiliary/GraphemeBreakProperty.txt'): 'd6b51d1d2ae5c33b451b7ed994b48f1f4dc62b2272a5831e7fd418514a6bae89',
    ('17.0.0', 'emoji/emoji-data.txt'): '2cb2bb9455cda83e8481541ecf5b6dfda66a3bb89efa3fa7c5297eccf607b72b',
}
CLASSES = {'CR': 1, 'LF': 2, 'Control': 3, 'Extend': 4, 'ZWJ': 5,
           'Regional_Indicator': 6, 'Prepend': 7, 'SpacingMark': 8,
           'L': 9, 'V': 10, 'T': 11, 'LV': 12, 'LVT': 13}


def generate(directory):
    inventories = {}
    lines = ['//! Generated public Unicode 16 facts and verified 17 differences, not implementation code.',
             '//! Source terms: src/ProGPU.Rust/UNICODE-LICENSE.txt.',
             '//! Regenerate with eng/progpu-generate-rust-grapheme-profile.py.']
    for (version, name), digest in FILES.items():
        url = f'https://www.unicode.org/Public/{version}/ucd/{name}'
        path = directory / (version + '-' + name.split('/')[-1])
        if not path.exists():
            path.write_bytes(urllib.request.urlopen(url).read())
        data = path.read_bytes()
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError('Unicode fact hash mismatch: ' + url)
        values, assigned = bytearray(0x110000), set()
        for line in data.decode().splitlines():
            line = line.split('#')[0].strip()
            if not line:
                continue
            extent, value = map(str.strip, line.split(';'))
            emoji = name.startswith('emoji/')
            if emoji and value != 'Extended_Pictographic':
                continue
            bounds = extent.split('..')
            start, end = int(bounds[0], 16), int(bounds[-1], 16)
            if not 0 <= start <= end < 0x110000 or any(cp in assigned for cp in range(start, end + 1)):
                raise ValueError('invalid/overlapping Unicode property range')
            assigned.update(range(start, end + 1))
            values[start:end + 1] = bytes([1 if emoji else CLASSES[value]]) * (end - start + 1)
        inventories[version, name] = values
        lines += [f'// {url}', f'// SHA-256 {digest}']
    source = (ROOT / 'src/ProGPU.Rust/src/text/unicode/data.rs').read_text()
    for name, tag in [('auxiliary/GraphemeBreakProperty.txt', 'GRAPHEME_RANGES'),
                      ('emoji/emoji-data.txt', 'PICTOGRAPHIC_RANGES')]:
        block = re.search(tag + r':.*?= \[(.*?)\];', source, re.S).group(1)
        numbers = list(map(int, re.findall(r'\d+', block)))
        original = bytearray(0x110000)
        for start, end, value in zip(numbers[::3], numbers[1::3], numbers[2::3]):
            original[start:end + 1] = bytes([value]) * (end - start + 1)
        if original != inventories['17.0.0', name]:
            raise ValueError('original frozen Unicode 17 inventory differs: ' + tag)
        earlier = inventories['16.0.0', name]
        differences = [cp for cp in range(len(original)) if original[cp] != earlier[cp]]
        lines += [f'// {tag}: {len(differences)} differing source code points.']
    classes = inventories['16.0.0', 'auxiliary/GraphemeBreakProperty.txt']
    emoji = inventories['16.0.0', 'emoji/emoji-data.txt']
    combined = bytes(value | (flag << 4) for value, flag in zip(classes, emoji))
    # Authored factual representation: ordered 128-code-point pages, deduplicated
    # by exact byte identity. No runtime or external project's table is read.
    pages, indices = {}, bytearray()
    for start in range(0, len(combined), 128):
        page = combined[start:start + 128]
        if page not in pages:
            pages[page] = len(pages)
        if pages[page] > 255:
            raise ValueError('source page index exceeds the explicit byte layout')
        indices.append(pages[page])
    packed = indices + b''.join(pages)
    assert len(indices) == 8704
    for cp, value in enumerate(combined):
        assert packed[8704 + packed[cp >> 7] * 128 + (cp & 127)] == value
    lines += [f'// Complete Unicode 16 facts: {len(pages)} unique pages; {len(packed)} bytes.',
              f'// Packed SHA-256 {hashlib.sha256(packed).hexdigest()}',
              f'pub(super) const SOURCE_PROPERTIES: &[u8; {len(packed)}] = include_bytes!("grapheme_properties_16.bin");']
    return '\n'.join(lines) + '\n', packed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true')
    parser.add_argument('--data-dir', type=Path)
    args = parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-grapheme-facts-') as temporary:
        result, packed = generate(args.data_dir or Path(temporary))
    if args.check:
        if OUTPUT.read_text() != result or PACKED.read_bytes() != packed:
            raise ValueError('managed grapheme facts are stale')
    else:
        OUTPUT.write_text(result)
        PACKED.write_bytes(packed)
    print('Complete Unicode 17 grapheme/emoji inventories verified; explicit Unicode 16 differences current')


if __name__ == '__main__':
    main()
