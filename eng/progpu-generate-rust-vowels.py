#!/usr/bin/env python3
"""Generate public Microsoft vowel constraints and verify the original ProGPU API.

Reads specification facts only, never generated engine arrays or implementation.
"""
from pathlib import Path
import argparse
import hashlib
import itertools
import json
import os
import struct
import subprocess
import tempfile
import urllib.request
from xml.sax.saxutils import escape

ROOT=Path(__file__).resolve().parents[1]
OUTPUT=ROOT/'src/ProGPU.Rust/src/text/script_actions/vowel_data.rs'
INVENTORY=ROOT/'src/ProGPU.Rust/tests/support/vowel_inventory.txt'
MICROSOFT='https://raw.githubusercontent.com/microsoft/font-tools/d06068439ff23fff99bfd058a5ab44a43183ecd9/'
FILES={
    'IndicShapingInvalidCluster.txt':(MICROSOFT+'USE/IndicShapingInvalidCluster.txt','ae9af46acaa14c0f6e991ab063cdf9501e09c04161b1e3857941de32a57b08db'),
    'Microsoft-FONT-TOOLS-LICENSE.txt':(MICROSOFT+'LICENSE','9906940f61b1f0b533fa7d99baf55178b2808fbe113ea51dfbfad8572ccd5f2b'),
    'Scripts.txt':('https://www.unicode.org/Public/17.0.0/ucd/Scripts.txt','9f5e50d3abaee7d6ce09480f325c706f485ae3240912527e651954d2d6b035bf'),
    'PropertyValueAliases.txt':('https://www.unicode.org/Public/17.0.0/ucd/PropertyValueAliases.txt','64e9a5f76f7a1e8b5a47d6a1f9a26522a251208f5276bdfa1559dac7cf2e827a'),
}

def word(v):return struct.pack('<I',v)
def facts(directory):
    contents={}
    for name,(url,digest) in FILES.items():
        path=directory/name
        if not path.exists():path.write_bytes(urllib.request.urlopen(url).read())
        data=path.read_bytes();assert hashlib.sha256(data).hexdigest()==digest,name
        contents[name]=data.decode()
    aliases={}
    for line in contents['PropertyValueAliases.txt'].splitlines():
        fields=[f.strip() for f in line.split('#')[0].split(';')]
        if len(fields)>=3 and fields[0]=='sc':aliases[fields[2]]=fields[1].lower()
    scripts=[]
    for line in contents['Scripts.txt'].splitlines():
        line=line.split('#')[0].strip()
        if not line:continue
        interval,name=map(str.strip,line.split(';'));span=interval.split('..')
        scripts.append((int(span[0],16),int(span[-1],16),aliases[name]))
    rows=[]
    for line in contents['IndicShapingInvalidCluster.txt'].splitlines():
        line=line.split('#')[0].split(';')[0].strip()
        if not line:continue
        points=tuple(int(v,16) for v in line.split());assert len(points) in (2,3)
        script=next(name for start,end,name in scripts if start<=points[0]<=end)
        assert len(script)==4 and script.isascii()
        rows.append((script,*points,*([0] if len(points)==2 else [])))
    assert len(set(rows))==len(rows)
    return sorted(rows),contents

def original(work):
    harness=ROOT/'src/ProGPU.Rust/tests/managed_vowel_facts.cs';project=ROOT/'src/ProGPU.Text/ProGPU.Text.csproj'
    (work/'Control.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
        '<TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>'
        '<EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup>'
        f'<Compile Include="{escape(str(harness))}"/><ProjectReference Include="{escape(str(project))}"/>'
        '</ItemGroup></Project>')
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    subprocess.run([dotnet,'build','Control.csproj','-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={work/"artifacts"}'],cwd=work,check=True)
    return [dotnet,str(work/'artifacts/bin/Control/release/Control.dll')]

def verify(command,rows):
    expected={(s,a,b,c) for s,a,b,c in rows}
    observed_hash=hashlib.sha256();input_hash=hashlib.sha256();queries=0
    def run(groups):
        nonlocal queries
        request=bytearray(word(len(groups)));answers=[]
        for script,points in groups:
            units=script.encode('utf-16-le');request+=word(len(units)//2)
            request+=b''.join(word(v[0]) for v in struct.iter_unpack('<H',units))
            request+=word(len(points));queries+=len(points)
            for a,b,c in points:
                request+=struct.pack('<III',a,b,c)
                answers.append(2 if (script,a,b,0) in expected else 3 if c and (script,a,b,c) in expected else 0)
        response=subprocess.run(command,input=request,capture_output=True,check=True).stdout
        input_hash.update(request);observed_hash.update(response)
        count=struct.unpack_from('<I',response)[0];assert count==len(rows),'original inventory count differs'
        source=[]
        for tag,a,b,c in struct.iter_unpack('<IIII',response[4:4+16*count]):
            source.append((tag.to_bytes(4,'big').decode('ascii'),a,b,c))
        assert sorted(source)==rows,'original public property inventory differs from specification'
        values=[v[0] for v in struct.iter_unpack('<I',response[4+16*count:])]
        assert len(values)==len(answers)
        if values!=answers:
            n=next(i for i,(a,b) in enumerate(zip(values,answers)) if a!=b)
            raise AssertionError(f'original match policy differs at request {n}: {values[n]} vs {answers[n]}')
    # Cross-script, ordinal spelling, near misses, raw scalar boundaries and full
    # triples prove the public constraints and the original pair-before-triple policy.
    scripts=sorted({r[0] for r in rows})
    points=set()
    boundaries=[0,1,0xd800,0xdfff,0x10ffff,0x110000,0xffffffff]
    for _,a,b,c in rows:
        for x,y,z in itertools.product([a-1,a,a+1],[b-1,b,b+1],[0,c,c+1,*boundaries]):points.add((x,y,z))
        for cp in boundaries:points.update([(cp,b,c),(a,cp,c)])
    points=sorted(points)
    for script in scripts:
        run([(script,points),(script.upper(),points),(script+' ',points),(script[:3],points)])
    for script in ['','DFLT','latn','dev2','bng2','lao ','\U0001f600']:
        run([(script,points)])
    # Every third raw value for each three-scalar prefix, including pair overlap.
    for script,a,b,_ in [r for r in rows if r[3]]:
        for start in range(0,0x110001,65536):
            run([(script,[(a,b,c) for c in range(start,min(start+65536,0x110001))])])
        run([(script,[(a,b,0xffffffff)])])
    return dict(constraints=len(rows),scripts=len(scripts),matching_queries=queries,input_sha256=input_hash.hexdigest(),original_output_sha256=observed_hash.hexdigest())

def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--check',action='store_true');parser.add_argument('--data-dir',type=Path);args=parser.parse_args()
    with tempfile.TemporaryDirectory(prefix='progpu-vowel-facts-') as directory:
        work=Path(directory);rows,contents=facts(args.data_dir or work);receipt=verify(original(work),rows)
    lines=['//! Public Microsoft invalid-cluster facts, independently verified with ProGPU.',
           '//! No generated foreign engine table or implementation is transcribed.',
           '//! Regenerate/check: eng/progpu-generate-rust-vowels.py.',
           '//! Licenses: MICROSOFT-FONT-TOOLS-LICENSE.txt and UNICODE-LICENSE.txt.']
    for name,(url,digest) in FILES.items():lines += [f'// {url}',f'// SHA-256 {digest}']
    lines+=['pub(super) const CONSTRAINTS: &[(u32, u32, u32, u32)] = &[']
    inventory=['# Public specification facts; independently compared with the original API.']
    for script,a,b,c in rows:
        tag=int.from_bytes(script.encode(),'big');lines.append(f'    (0x{tag:08x}, 0x{a:x}, 0x{b:x}, 0x{c:x}),')
        inventory.append(f'{script} {a:x} {b:x} {c:x}')
    outputs={OUTPUT:'\n'.join(lines+['];'])+'\n',INVENTORY:'\n'.join(inventory)+'\n',ROOT/'src/ProGPU.Rust/MICROSOFT-FONT-TOOLS-LICENSE.txt':contents['Microsoft-FONT-TOOLS-LICENSE.txt'].replace('\r\n','\n')}
    for path,content in outputs.items():
        if args.check:assert path.read_text(encoding='utf-8')==content,f'stale vowel facts: {path}'
        else:path.write_bytes(content.encode())
    receipt['generated_source_sha256']=hashlib.sha256(outputs[OUTPUT].encode()).hexdigest();print(json.dumps(receipt))
if __name__=='__main__':main()
