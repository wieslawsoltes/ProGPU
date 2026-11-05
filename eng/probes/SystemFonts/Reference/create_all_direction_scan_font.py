"""Reverse all 440 original owned contours without changing their instructions.

The seven-contour derivative and every original font/fixture remain immutable.
This uses only the public SFNT/glyf formats and the existing owned generator.
"""
import argparse
import json
import struct
from create_reversed_scan_font import SHARED, SOURCE_HASH, FIXTURE_HASH, checksum, digest, renamed


def points(raw):
    assert struct.unpack_from('>h', raw)[0] == 1
    count = struct.unpack_from('>H', raw, 10)[0] + 1
    size = struct.unpack_from('>H', raw, 12)[0]
    instructions = raw[14:14+size]
    cursor = 14+size
    flags = []
    while len(flags) < count:
        flag = raw[cursor]; cursor += 1
        repeat = 1
        if flag & 8:
            repeat += raw[cursor]; cursor += 1
        flags.extend([flag]*repeat)
    assert len(flags) == count and flags[0] & 1
    assert all(f & ~0x3f == 0 for f in flags)
    axes = []
    for short_bit, same_bit in [(2,16),(4,32)]:
        position = 0; values = []
        for flag in flags:
            if flag & short_bit:
                delta = raw[cursor]; cursor += 1
                if not flag & same_bit: delta = -delta
            elif flag & same_bit: delta = 0
            else:
                delta = struct.unpack_from('>h',raw,cursor)[0]; cursor += 2
            position += delta; values.append(position)
        axes.append(values)
    assert not any(raw[cursor:])
    return list(zip(*axes)), [f & 1 for f in flags], instructions


def build():
    source = (SHARED/'ProGPU-Authored-Scan-1.ttf').read_bytes()
    fixture = (SHARED/'BinaryGlyphOriginals.json').read_bytes()
    assert digest(source) == SOURCE_HASH and digest(fixture) == FIXTURE_HASH
    assert checksum(source) == 0xb1b0afba
    count = struct.unpack_from('>H',source,4)[0]; tables = {}
    for i in range(count):
        tag, expected, offset, length = struct.unpack_from('>4sIII',source,12+16*i)
        assert tag not in tables and offset+length <= len(source)
        data = source[offset:offset+length]
        if tag == b'head': data = data[:8]+bytes(4)+data[12:]
        assert checksum(data) == expected
        tables[tag] = data
    assert struct.unpack_from('>H',tables[b'head'],18)[0] == 2048
    assert struct.unpack_from('>h',tables[b'head'],50)[0] == 0
    glyph_count = struct.unpack_from('>H',tables[b'maxp'],4)[0]
    assert glyph_count == 441
    offsets = [v*2 for v in struct.unpack('>'+'H'*(glyph_count+1),tables[b'loca'])]
    assert offsets[0] == 0 and offsets[-1] == len(tables[b'glyf'])
    cases = {c['index']:c for c in json.loads(fixture)['cases'] if c['gasp']==1}
    assert set(cases) == set(range(1,441))
    output = bytearray(); locations = []; changed = []
    for glyph in range(glyph_count):
        locations.append(len(output)//2)
        raw = tables[b'glyf'][offsets[glyph]:offsets[glyph+1]]
        if glyph:
            original, flags, instructions = points(raw)
            assert instructions == bytes([0xb8,1,255,0x85,0xb0,cases[glyph]['mode'],0x8d])
            order = [0]+list(range(len(original)-1,0,-1))
            reverse = [original[i] for i in order]; reverse_flags = [flags[i] for i in order]
            replacement = raw[:12]+struct.pack('>H',len(instructions))+instructions+bytes(reverse_flags)
            for axis in (0,1):
                previous = 0
                for point in reverse:
                    replacement += struct.pack('>h',point[axis]-previous); previous=point[axis]
            replacement += bytes((-len(replacement))%4)
            assert points(replacement) == (reverse,reverse_flags,instructions)
            changed.append({'glyph':glyph,'kind':cases[glyph]['kind'],'mode':cases[glyph]['mode'],
                            'phase':cases[glyph]['phase'],'points':len(original),
                            'originalGlyphSha256':digest(raw),'reversedGlyphSha256':digest(replacement)})
            raw = replacement
        output.extend(raw)
    locations.append(len(output)//2)
    tables[b'glyf'] = bytes(output)
    tables[b'loca'] = struct.pack('>'+'H'*len(locations),*locations)
    tables[b'name'] = renamed(tables[b'name'], '-AllDirections')
    directory = bytearray(source[:12]); payload = bytearray(); base = 12+16*count; head = None
    for tag,data in sorted(tables.items()):
        offset = base+len(payload)
        directory.extend(struct.pack('>4sIII',tag,checksum(data),offset,len(data)))
        if tag == b'head': head=offset
        payload.extend(data); payload.extend(bytes((-len(data))%4))
    result = directory+payload
    struct.pack_into('>I',result,head+8,(0xb1b0afba-checksum(result)) & 0xffffffff)
    assert checksum(result) == 0xb1b0afba
    receipt = {'sourceSha256':SOURCE_HASH,'fixtureSha256':FIXTURE_HASH,'sha256':digest(result),
        'bytes':len(result),'glyphs':glyph_count,'changedGlyphs':changed,'rendered':False,'qualified':False,
        'scope':'All 440 original owned contours reversed with exact coordinates, tags, instructions and metrics. Original font, seven-contour derivative and pixel fixtures unchanged.'}
    return bytes(result), (json.dumps(receipt,indent=2)+'\n').encode()


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--write',action='store_true');args=parser.parse_args()
    font, receipt = build()
    for path,data in [(SHARED/'ProGPU-Authored-Scan-AllDirections.ttf',font),
                      (SHARED/'ProGPU-Authored-Scan-AllDirections.json',receipt)]:
        if args.write: path.write_bytes(data)
        else: assert path.read_bytes() == data, str(path)
    print(digest(font),len(font),'bytes; all 440 owned contours reversed; original instructions preserved')
