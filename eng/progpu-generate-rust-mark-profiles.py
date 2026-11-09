#!/usr/bin/env python3
"""Verify original frozen mark categories and emit explicit source-version facts."""
from pathlib import Path
import argparse,hashlib,re,tempfile,urllib.request
ROOT=Path(__file__).resolve().parents[1]
FILES={'15.1.0':'2fc713e6a31a87c4850a37fe2caffa4218180fadb5de86b43a143ddb4581fb86','16.0.0':'ff58e5823bd095166564a006e47d111130813dcf8bf234ef79fa51a870edb48f','17.0.0':'2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c'}
OUTPUT=ROOT/'src/ProGPU.Rust/src/text/unicode/mark_profile_data.rs'
def generate(directory):
    source=(ROOT/'src/ProGPU.Rust/src/text/unicode/data.rs').read_text();block=re.search(r'CATEGORY_RANGES:.*?= \[(.*?)\];',source,re.S).group(1);values=list(map(int,re.findall(r'\d+',block)))
    original={cp for a,b,value in zip(values[::3],values[1::3],values[2::3]) if value in [5,6,7] for cp in range(a,b+1)}
    out=['//! Generated Unicode mark-repertoire differences, not an implementation.','//! Source facts use src/ProGPU.Rust/UNICODE-LICENSE.txt.','//! Regenerate with eng/progpu-generate-rust-mark-profiles.py.']
    for version,digest in FILES.items():
        path=directory/(version+'.txt');url=f'https://www.unicode.org/Public/{version}/ucd/UnicodeData.txt'
        if not path.exists():path.write_bytes(urllib.request.urlopen(url).read())
        data=path.read_bytes()
        if hashlib.sha256(data).hexdigest()!=digest:raise ValueError('Unicode facts hash mismatch: '+version)
        marks=set();first=None
        for line in data.decode().splitlines():
            f=line.split(';');cp=int(f[0],16)
            if f[1].endswith(', First>'):first=(cp,f[2]);continue
            if f[1].endswith(', Last>'):
                if first is None or first[1]!=f[2]:raise ValueError('invalid First/Last range')
                if f[2] in ['Mn','Mc','Me']:marks.update(range(first[0],cp+1))
                first=None;continue
            if first is not None:raise ValueError('unterminated range')
            if f[2] in ['Mn','Mc','Me']:marks.add(cp)
        if version=='16.0.0' and marks!=original:raise ValueError('original frozen mark inventory differs from Unicode 16')
        absent=sorted(original-marks);added=sorted(marks-original)
        out += [f'// {url}',f'// SHA-256 {digest}; {len(marks)} source marks, {len(absent)} removed, {len(added)} added.']
        for name,values in [('ABSENT',absent),('ADDED',added)]:
            out += ['#[rustfmt::skip]',f'pub(super) const {name}_{version.replace(".","_")}: &[u32] = &[']
            out += ['    '+', '.join(str(v) for v in values[i:i+12])+',' for i in range(0,len(values),12)];out+= ['];']
    return '\n'.join(out)+'\n'
def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--check',action='store_true');p.add_argument('--data-dir',type=Path);args=p.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-mark-profile-facts-') as tmp:out=generate(args.data_dir or Path(tmp))
    if args.check:
        if OUTPUT.read_text()!=out:raise ValueError('Mark profile facts are stale')
    else:OUTPUT.write_text(out)
    print('Complete original frozen Unicode 16 marks verified; explicit profile facts current')
if __name__=='__main__':main()
