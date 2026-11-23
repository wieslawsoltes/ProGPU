#!/usr/bin/env python3
"""Project pinned Unicode facts for explicit source normalization profiles.

Original ProGPU normalization code remains the implementation. This generator
verifies the complete frozen decomposition inventory against public Unicode
facts and emits only version-specific combining classes and absent record keys.
"""
from pathlib import Path
import argparse
import hashlib
import re
import struct
import tempfile
import urllib.request

ROOT=Path(__file__).resolve().parents[1]
FILES={
 '15.1.0':'2fc713e6a31a87c4850a37fe2caffa4218180fadb5de86b43a143ddb4581fb86',
 '16.0.0':'ff58e5823bd095166564a006e47d111130813dcf8bf234ef79fa51a870edb48f',
 '17.0.0':'2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c',
}
OUTPUT=ROOT/'src/ProGPU.Rust/src/text/unicode/normalization/profile_data.rs'
def generate(directory):
    frozen=(ROOT/'src/ProGPU.Text/UnicodeNormalizationData.bin').read_bytes()
    if hashlib.sha256(frozen).hexdigest()!='da3f8b751ac289987e400e4ea7daacd66ab912dbf868569fb50ce0fd8d9d7dff':
        raise ValueError('Original shared resource changed: requalify the complete profiles')
    count,scalar_count,pair_count=struct.unpack_from('<III',frozen,8)
    scalars=struct.unpack_from('<'+'I'*scalar_count,frozen,20+count*12)
    records={}
    for i in range(count):
        cp,start,length=struct.unpack_from('<III',frozen,20+i*12);records[cp]=list(scalars[start:start+length])
    pairs=[struct.unpack_from('<III',frozen,20+count*12+scalar_count*4+i*12) for i in range(pair_count)]
    lines=['//! Generated factual source profiles; no foreign normalization implementation.',
           '//! Regenerate with eng/progpu-generate-rust-normalization-profiles.py.',
           '//! Unicode data terms are in src/ProGPU.Rust/UNICODE-LICENSE.txt.']
    for version,digest in FILES.items():
        path=directory/f'{version}.txt';url=f'https://www.unicode.org/Public/{version}/ucd/UnicodeData.txt'
        if not path.exists():path.write_bytes(urllib.request.urlopen(url).read())
        raw=path.read_bytes()
        if hashlib.sha256(raw).hexdigest()!=digest:raise ValueError(f'{version}: Unicode facts hash mismatch')
        ccc={};decompositions={}
        for line in raw.decode().splitlines():
            f=line.split(';');cp=int(f[0],16)
            if int(f[3]):ccc[cp]=int(f[3])
            if f[5] and not f[5].startswith('<'):decompositions[cp]=[int(v,16) for v in f[5].split()]
        def expand(cp):
            return [cp] if cp not in decompositions else [v for child in decompositions[cp] for v in expand(child)]
        absent=[cp for cp in records if cp not in decompositions and not 0xac00<=cp<=0xd7a3]
        for cp in decompositions:
            if records.get(cp)!=expand(cp):raise ValueError(f'{version}: original decomposition differs at {cp:x}')
        # UCD stores Hangul as a First/Last range. Validate its complete factual
        # 19-leading x 21-vowel x 28-trailing syllable inventory independently.
        for cp in range(0xac00,0xd7a4):
            index=cp-0xac00
            expected=[0x1100+index//588,0x1161+(index%588)//28]
            if index%28:expected.append(0x11a7+index%28)
            if records.get(cp)!=expected:raise ValueError(f'{version}: original Hangul facts differ at {cp:x}')
        if version=='15.1.0':
            # Exact producer evidence from the unchanged original .NET generator
            # on Ubuntu/ICU 74.2. Rebuild all bytes, not just the first witness.
            retained=[(cp,values) for cp,values in records.items() if cp not in absent]
            retained_pairs=[p for p in pairs if p[2] not in absent]
            out=bytearray(struct.pack('<IIIII',0x4e554750,1,len(retained),sum(len(v) for _,v in retained),len(retained_pairs)))
            offset=0
            for cp,values in retained:out.extend(struct.pack('<III',cp,offset,len(values)));offset+=len(values)
            for _,values in retained:out.extend(struct.pack('<'+'I'*len(values),*values))
            for p in retained_pairs:out.extend(struct.pack('<III',*p))
            if hashlib.sha256(out).hexdigest()!='3657473d5565b37d68549b431f2acc50c5d0080147403356c3eff8c9428e40d5':raise ValueError('Original runtime inventory changed')
        ranges=[]
        for cp,value in sorted(ccc.items()):
            if ranges and ranges[-2]+1==cp and ranges[-1]==value:ranges[-2]=cp
            else:ranges.extend([cp,cp,value])
        if version=='17.0.0':
            source=(ROOT/'src/ProGPU.Rust/src/text/unicode/data.rs').read_text()
            block=re.search(r'COMBINING_RANGES:.*?= \[(.*?)\];',source,re.S).group(1)
            if list(map(int,re.findall(r'\d+',block)))!=ranges:raise ValueError('Original Unicode 17 classes differ')
        lines += [f'// {url}',f'// SHA-256 {digest}']
        suffix=version.replace('.','_')
        for name,values in [(f'ABSENT_{suffix}',absent)]+([] if version=='17.0.0' else [(f'CLASSES_{suffix}',ranges)]):
            lines.append('#[rustfmt::skip]')
            lines.append(f'pub(super) const {name}: &[u32] = &[')
            lines += ['    '+', '.join(str(v) for v in values[i:i+15])+',' for i in range(0,len(values),15)]
            lines.append('];')
    return '\n'.join(lines)+'\n'
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--check',action='store_true');p.add_argument('--data-dir',type=Path);args=p.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-normalization-profile-facts-') as temp:
        result=generate(args.data_dir or Path(temp))
    if args.check:
        if OUTPUT.read_text()!=result:raise ValueError('Generated profile facts are stale')
    else:OUTPUT.parent.mkdir(parents=True,exist_ok=True);OUTPUT.write_text(result)
    print('Complete original decomposition inventories and Unicode 17 classes verified; profile facts current')
if __name__=='__main__':main()
