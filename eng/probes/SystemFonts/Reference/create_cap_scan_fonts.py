"""Owned flat-cap observations: two strip frames, eleven ends, five scan modes.

Original fonts/fixtures stay immutable. Forward/reversed derivatives share exact
coordinates and instructions; only their contour traversal differs.
"""
import argparse
import json
import struct
from create_reversed_scan_font import SHARED, SOURCE_HASH, FIXTURE_HASH, checksum, digest, renamed
from create_all_direction_scan_font import points

DELTAS = [-33, -32, -31, -16, -1, 0, 1, 16, 24, 31, 32]


def build(reverse):
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
    count_glyphs = struct.unpack_from('>H',tables[b'maxp'],4)[0]
    assert count_glyphs == 441
    metrics_count = struct.unpack_from('>H',tables[b'hhea'],34)[0]
    metrics = bytearray(tables[b'hmtx'])
    assert 1 <= metrics_count <= count_glyphs
    assert len(metrics) == 4*metrics_count+2*(count_glyphs-metrics_count)
    offsets = [v*2 for v in struct.unpack('>'+'H'*(count_glyphs+1),tables[b'loca'])]
    assert offsets[0] == 0 and offsets[-1] == len(tables[b'glyf'])
    selected = {c['index']:c for c in json.loads(fixture)['cases']
                if c['gasp']==1 and c['kind'] in ('horizontal-dropout','top-edge')}
    assert len(selected) == 110
    output = bytearray(); locations = []; records = []
    for glyph in range(count_glyphs):
        locations.append(len(output)//2)
        raw = tables[b'glyf'][offsets[glyph]:offsets[glyph+1]]
        if glyph in selected:
            original, flags, instructions = points(raw); row = selected[glyph]
            assert len(original) == 4 and flags == [1]*4
            assert instructions == bytes([0xb8,1,255,0x85,0xb0,row['mode'],0x8d])
            delta = DELTAS[row['phase']+5]
            # First strip is the original half-pixel dropout thickness. The
            # second independently varies the end of a thicker fractional strip.
            # These are explicit authored inputs, never adjusted source pixels.
            if row['kind']=='horizontal-dropout':
                left, right, bottom, top = 64, 640+delta, 240, 272
            else:
                left, right, bottom, top = 134, 320+delta, 481, 538
            assert left < right and bottom < top
            # Keep the new authored outline's declared sidebearing coherent.
            # Advance widths stay original; stale LSBs would shift the contour
            # through phantom-point processing before any raster observation.
            metric_offset = glyph*4+2 if glyph < metrics_count else metrics_count*4+(glyph-metrics_count)*2
            assert struct.unpack_from('>h',metrics,metric_offset)[0] == min(p[0] for p in original)
            struct.pack_into('>h',metrics,metric_offset,left)
            authored = [(left,bottom),(right,bottom),(right,top),(left,top)]
            if reverse: authored = [authored[i] for i in (0,3,2,1)]
            replacement = struct.pack('>hhhhhHH',1,left,bottom,right,top,3,len(instructions))
            replacement += instructions+bytes([1]*4)
            for axis in (0,1):
                previous = 0
                for point in authored:
                    replacement += struct.pack('>h',point[axis]-previous);previous=point[axis]
            replacement += bytes((-len(replacement))%4)
            assert points(replacement) == (authored,[1]*4,instructions)
            records.append({'glyph':glyph,'family':row['kind'],'mode':row['mode'],'phase':row['phase'],
                'capShift26_6':delta,'points26_6':authored,'instructions':instructions.hex(),
                'originalGlyphSha256':digest(raw),'authoredGlyphSha256':digest(replacement)})
            raw = replacement
        output.extend(raw)
    locations.append(len(output)//2)
    tables[b'glyf'] = bytes(output)
    tables[b'loca'] = struct.pack('>'+'H'*len(locations),*locations)
    tables[b'hmtx'] = bytes(metrics)
    # The selected new coordinates remain inside the unchanged global bounds.
    xmin,ymin,xmax,ymax = struct.unpack_from('>hhhh',tables[b'head'],36)
    assert all(xmin <= x <= xmax and ymin <= y <= ymax for r in records for x,y in r['points26_6'])
    tables[b'name'] = renamed(tables[b'name'], '-Caps-Reversed' if reverse else '-Caps')
    directory = bytearray(source[:12]); payload = bytearray(); base = 12+16*count; head = None
    for tag,data in sorted(tables.items()):
        offset = base+len(payload);directory.extend(struct.pack('>4sIII',tag,checksum(data),offset,len(data)))
        if tag == b'head': head=offset
        payload.extend(data);payload.extend(bytes((-len(data))%4))
    result = directory+payload
    struct.pack_into('>I',result,head+8,(0xb1b0afba-checksum(result)) & 0xffffffff)
    assert checksum(result) == 0xb1b0afba
    return bytes(result), {'sha256':digest(result),'bytes':len(result),'reversed':reverse,
                           'glyphs':count_glyphs,'selected':records}


if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--write',action='store_true');args=parser.parse_args()
    outputs=[];fonts=[]
    for reverse,name in [(False,'ProGPU-Authored-Scan-Caps.ttf'),(True,'ProGPU-Authored-Scan-Caps-Reversed.ttf')]:
        font,receipt=build(reverse);outputs.append((SHARED/name,font));fonts.append({'file':name,**receipt})
        print(receipt['sha256'],len(font),name)
    receipt={'sourceSha256':SOURCE_HASH,'originalFixtureSha256':FIXTURE_HASH,'qualified':False,'rendered':False,
             'scope':'Two authored strip frames, eleven finite cap coordinates, five scan modes, both directions. Existing source and fixtures unchanged.',
             'capDeltas26_6':DELTAS,'fonts':fonts}
    outputs.append((SHARED/'ProGPU-Authored-Scan-Caps.json',(json.dumps(receipt,indent=2)+'\n').encode()))
    for path,data in outputs:
        if args.write:path.write_bytes(data)
        else:assert path.read_bytes()==data,str(path)
