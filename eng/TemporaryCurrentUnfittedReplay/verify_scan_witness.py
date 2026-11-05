"""Independent byte comparison of captured diagnostic arrays, never rendering input."""
import base64
import json
import struct

SCAN_MASK = 7 | 512 | (0x38 << 16)
SEGMENT_BYTES = 48


def compare_records(original, witness, applied):
    if applied.get('qualified') is not False or applied.get('changedMetadataOnly') is not True:
        raise ValueError('Missing diagnostic-only classification')
    if applied.get('scanMask') != SCAN_MASK:
        raise ValueError('Scan mask changed')
    a, b, c = [base64.b64decode(x['segmentRecordBytes'], validate=True) for x in (original, witness, applied)]
    if len(a) != len(b) or len(a) != len(c) or len(a) % SEGMENT_BYTES:
        raise ValueError('Segment record inventory changed')
    changed = 0
    for i in range(0, len(a), SEGMENT_BYTES):
        # Four Vector2 records, then kind, Pad0, Pad1 and Pad2. Compare raw
        # geometry bits, including signed zero, not JSON floating-point values.
        if a[i:i+36] != c[i:i+36] or a[i+40:i+48] != c[i+40:i+48]:
            raise ValueError('Original geometry or contour-range bits changed')
        old, source, actual = [struct.unpack_from('<I', x, i+36)[0] for x in (a,b,c)]
        if actual != ((old & ~SCAN_MASK) | (source & SCAN_MASK)):
            raise ValueError('Applied metadata differs from the exact captured witness')
        changed += old != actual
    return {'segments':len(a)//SEGMENT_BYTES, 'changedScanRecords':changed,
            'allOriginalNonScanBitsPreserved':True, 'qualified':False}


def compare(directory):
    rows = []
    for index in range(2):
        original = json.loads((directory/f'font-{index}-raw.json').read_text())
        witness = json.loads((directory/f'font-{index}-scan-witness.json').read_text())
        applied = json.loads((directory/f'font-{index}-scan-applied.json').read_text())
        if original['fontIndex'] != index or witness['fontIndex'] != index or original['fontSha256'] != witness['fontSha256']:
            raise ValueError('Source font witness changed')
        if original['em26_6'] != witness['em26_6'] or witness['phaseX'] != 0 or witness['phaseY'] != 0 or witness['interpreter'] != 35:
            raise ValueError('Source device witness changed')
        rows.append({'font':index, **compare_records(original,witness,applied)})
    return {'qualified':False, 'fonts':rows}
