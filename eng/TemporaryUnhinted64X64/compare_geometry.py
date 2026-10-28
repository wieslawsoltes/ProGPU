"""Read-only exact GGO_NATIVE/producer contour comparison; never creates render inputs."""
import base64
import hashlib
import json
from pathlib import Path
import struct
import sys


def original_segments(record):
    data = base64.b64decode(record['nativeOutlineBase64'], validate=True)
    if len(data) != record['bytes'] or hashlib.sha256(data).hexdigest() != record['sha256']:
        raise ValueError('Original outline identity changed')
    result = []
    offset = 0
    def point(at):
        x, y = struct.unpack_from('<ii', data, at)
        # GGO and captured NativeGlyphOutline segments are both Y-up.
        # The native renderer performs its own target conversion later.
        return (x / 65536, y / 65536)
    while offset < len(data):
        if offset + 16 > len(data): raise ValueError('Truncated polygon')
        size, kind = struct.unpack_from('<II', data, offset)
        if kind != 24 or size < 16 or offset + size > len(data): raise ValueError('Invalid polygon')
        end = offset + size
        start = previous = point(offset + 8)
        cursor = offset + 16
        while cursor < end:
            if cursor + 4 > end: raise ValueError('Truncated curve')
            curve, count = struct.unpack_from('<HH', data, cursor)
            cursor += 4
            if count == 0 or cursor + count * 8 > end: raise ValueError('Invalid curve extent')
            points = [point(cursor + i * 8) for i in range(count)]
            cursor += count * 8
            if curve == 1:
                for following in points:
                    result.append((0, previous, following)); previous = following
            elif curve == 2 and count >= 2:
                for i in range(count - 1):
                    following = points[i + 1] if i == count - 2 else tuple((a + b) / 2 for a, b in zip(points[i], points[i + 1]))
                    result.append((1, previous, points[i], following)); previous = following
            elif curve == 3 and count % 3 == 0:
                for i in range(0, count, 3):
                    result.append((2, previous, *points[i:i+3])); previous = points[i+2]
            else: raise ValueError('Unsupported original curve')
        if previous != start: result.append((0, previous, start))
        offset = end
    return result


def actual_segments(raw, index):
    descriptor = raw['descriptors'][index]
    outline_index = descriptor['OutlineIndex']
    if outline_index == 4294967295: return []
    outline = raw['outlines'][outline_index]
    values = raw['segments'][outline['offset']:outline['offset']+outline['count']]
    if len(values) != outline['count']: raise ValueError('Incomplete captured segment inventory')
    result = []
    for segment in values:
        kind = segment['Kind']
        if kind not in (0,1,2): raise ValueError('Unexpected captured segment kind')
        result.append((kind, *[(segment['P'+str(i)]['X'], segment['P'+str(i)]['Y']) for i in range(kind+2)]))
    return result


def compare(inputs, captures):
    original = json.loads((inputs/'original64.json').read_text())
    rows = []
    for font in range(2):
        raw = json.loads((captures/f'font-{font}-raw.json').read_text())
        if raw['fontSha256'] != original['fonts'][font]['sha256']: raise ValueError('Source font changed')
        if len(raw['descriptors']) != 32: raise ValueError('Descriptor inventory changed')
        for row in [c for c in original['cases'] if c['font'] == font]:
            index = row['sourceIndex']
            if raw['descriptors'][index]['GlyphIndex'] != row['glyphId']: raise ValueError('Source glyph changed')
            expected = original_segments(row['originalUnhintedOutline'])
            actual = actual_segments(raw,index)
            rows.append({'font':font,'sourceIndex':index,'glyphId':row['glyphId'],
                         'originalSegments':expected,'actualSegments':actual,'exactOrderedSegments':expected==actual})
    return {'qualified':False,'cases':rows,'exactOrderedCount':sum(r['exactOrderedSegments'] for r in rows),
            'scope':'Exact ordered coordinates only; no cyclic/reversal/zero-edge normalization. Raw scan flags remain independent. These comparison arrays never enter rendering.'}


if __name__ == '__main__':
    result = compare(Path(sys.argv[1]),Path(sys.argv[2]))
    with Path(sys.argv[3]).open('x') as stream: json.dump(result,stream,indent=2)
    print(f"Exact ordered outlines: {result['exactOrderedCount']}/64; numeric differences preserved.")
