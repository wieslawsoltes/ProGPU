"""Observe the same finite upper strips with actual connected lower ink."""
import argparse
import json
from create_cap_scan_fonts import build, DELTAS
from create_reversed_scan_font import SHARED, SOURCE_HASH, FIXTURE_HASH

if __name__ == '__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--write',action='store_true');args=parser.parse_args()
    outputs=[];fonts=[]
    for reverse in (False,True):
        name='ProGPU-Authored-Scan-Connected-Caps'+('-Reversed' if reverse else '')+'.ttf'
        data,receipt=build(reverse,connected=True)
        outputs.append((SHARED/name,data));fonts.append({'file':name,**receipt})
        print(receipt['sha256'],len(data),name)
    receipt={'sourceSha256':SOURCE_HASH,'originalFixtureSha256':FIXTURE_HASH,'qualified':False,'rendered':False,
             'scope':'Same eleven authored fractional upper-strip ends, five scan modes and both directions, with an explicitly connected stem. Original source pixels remain unchanged.',
             'capDeltas26_6':DELTAS,'fonts':fonts}
    outputs.append((SHARED/'ProGPU-Authored-Scan-Connected-Caps.json',(json.dumps(receipt,indent=2)+'\n').encode()))
    for path,data in outputs:
        if args.write:path.write_bytes(data)
        else:assert path.read_bytes()==data,str(path)
