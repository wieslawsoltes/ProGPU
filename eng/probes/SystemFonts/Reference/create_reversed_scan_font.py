"""Deterministic owned-font direction control; never alters original fixtures.

Uses the OpenType glyf/loca/name/SFNT formats, not a renderer implementation.
Only seven four-point right-edge contours reverse; original glyph instructions,
coordinates, metrics and every other glyph remain byte-for-byte intact.
"""
from pathlib import Path
import argparse
import hashlib
import json
import struct

ROOT = Path(__file__).resolve().parents[4]
SHARED = ROOT / 'tests/Shared'
SOURCE_HASH = '60d952f14b770aec08221b8adee1a1b7d82a8abdc259fb2484de9666a2f45832'
FIXTURE_HASH = '6d13e632b5f260217bc17d9c86b61e77506f463644eeee923280400a52f3b092'
SELECTED = {6, 93, 94, 95, 182, 270, 358}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def checksum(data):
    padded = data + bytes((-len(data)) % 4)
    return sum(struct.unpack('>' + 'I' * (len(padded)//4), padded)) & 0xffffffff


def simple_points(raw):
    assert struct.unpack_from('>h', raw)[0] == 1
    assert struct.unpack_from('>H', raw, 10)[0] == 3
    size = struct.unpack_from('>H', raw, 12)[0]
    instructions = raw[14:14+size]
    cursor = 14 + size
    flags = []
    while len(flags) < 4:
        flag = raw[cursor]; cursor += 1
        repeat = 1
        if flag & 8:
            repeat += raw[cursor]; cursor += 1
        flags.extend([flag] * repeat)
    assert len(flags) == 4 and all(f & 1 for f in flags)
    axes = []
    for short_bit, same_bit in [(2,16),(4,32)]:
        position = 0; values = []
        for flag in flags:
            if flag & short_bit:
                delta = raw[cursor]; cursor += 1
                if not flag & same_bit: delta = -delta
            elif flag & same_bit:
                delta = 0
            else:
                delta = struct.unpack_from('>h', raw, cursor)[0]; cursor += 2
            position += delta; values.append(position)
        axes.append(values)
    assert not any(raw[cursor:])
    return list(zip(*axes)), instructions


def renamed(table):
    version, count, start = struct.unpack_from('>HHH', table)
    assert version == 0
    records = []; strings = bytearray()
    for i in range(count):
        platform, encoding, language, name, length, offset = struct.unpack_from('>HHHHHH',table,6+i*12)
        value = table[start+offset:start+offset+length]
        assert len(value) == length and platform in (1,3)
        if name in (1,3,4,6,16):
            codec = 'utf-16-be' if platform == 3 else 'mac_roman'
            value = (value.decode(codec) + '-Reversed').encode(codec)
        records.append(struct.pack('>HHHHHH',platform,encoding,language,name,len(value),len(strings)))
        strings.extend(value)
    return struct.pack('>HHH',0,count,6+12*count)+b''.join(records)+strings


def build():
    source = (SHARED/'ProGPU-Authored-Scan-1.ttf').read_bytes()
    fixture = (SHARED/'BinaryGlyphOriginals.json').read_bytes()
    assert digest(source) == SOURCE_HASH and digest(fixture) == FIXTURE_HASH
    assert checksum(source) == 0xb1b0afba
    count = struct.unpack_from('>H',source,4)[0]
    tables = {}
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
    offsets = [v*2 for v in struct.unpack('>'+'H'*(glyph_count+1),tables[b'loca'])]
    assert offsets[0] == 0 and offsets[-1] == len(tables[b'glyf'])
    cases = {c['index']:c for c in json.loads(fixture)['cases'] if c['gasp']==1 and c['index'] in SELECTED}
    assert set(cases) == SELECTED
    output = bytearray(); locations = []; changed = []
    for glyph in range(glyph_count):
        locations.append(len(output)//2)
        raw = tables[b'glyf'][offsets[glyph]:offsets[glyph+1]]
        if glyph in SELECTED:
            points, instructions = simple_points(raw)
            case = cases[glyph]
            assert case['kind'] == 'right-edge'
            assert points == [(s[1],s[2]) for s in case['segments']]
            assert instructions == bytes([0xb8,1,255,0x85,0xb0,case['mode'],0x8d])
            reverse = [points[i] for i in (0,3,2,1)]
            replacement = raw[:12]+struct.pack('>H',len(instructions))+instructions+bytes([1]*4)
            for axis in (0,1):
                previous = 0
                for point in reverse:
                    replacement += struct.pack('>h',point[axis]-previous); previous=point[axis]
            replacement += bytes((-len(replacement))%4)
            assert simple_points(replacement) == (reverse,instructions)
            changed.append({'glyph':glyph,'mode':case['mode'],'phase':case['phase'],
                            'originalPoints':points,'reversedPoints':reverse,'instructions':instructions.hex(),
                            'originalGlyphSha256':digest(raw),'reversedGlyphSha256':digest(replacement)})
            raw = replacement
        output.extend(raw)
    locations.append(len(output)//2)
    tables[b'glyf'] = bytes(output)
    tables[b'loca'] = struct.pack('>'+'H'*len(locations),*locations)
    tables[b'name'] = renamed(tables[b'name'])
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
               'bytes':len(result),'unitsPerEm':2048,'glyphs':glyph_count,'changedGlyphs':changed,
               'rendered':False,'qualified':False,
               'scope':'Contour direction control only; original font/fixture unchanged. No pixel expectation replacement.'}
    return bytes(result), (json.dumps(receipt,indent=2)+'\n').encode()


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--write',action='store_true');args=parser.parse_args()
    font, receipt = build()
    for path,data in [(SHARED/'ProGPU-Authored-Scan-Reversed.ttf',font),
                      (SHARED/'ProGPU-Authored-Scan-Reversed.json',receipt)]:
        if args.write: path.write_bytes(data)
        else: assert path.read_bytes() == data, str(path)
    print(digest(font),len(font),'bytes; seven owned contours reversed; original instructions preserved')
