#!/usr/bin/env python3
"""Capture original public glyph-mapping behavior over an authored identity-PUA font.

No source mapping arrays or third-party implementation text are transcribed.
The original managed API is the behavioral oracle; nonidentity nominal outputs
over the complete BMP form the two immutable data inventories.
"""
from pathlib import Path
import hashlib,json,os,struct,subprocess,tempfile
from xml.sax.saxutils import escape
ROOT=Path(__file__).resolve().parents[1]
def font(page,hole=None):
    def short(b,at,v):struct.pack_into('>H',b,at,v)
    def word(b,at,v):struct.pack_into('>I',b,at,v)
    head=bytearray(54);short(head,18,1000)
    os2=bytearray(64);short(os2,62,page)
    segments=[(0xf000,0xf2ff,0)] if hole is None else [(a,b,0) for a,b in [(0xf000,hole-1),(hole+1,0xf2ff)] if a<=b]
    segments.append((0xffff,0xffff,1));count=len(segments);sub=bytearray(16+count*8)
    for at,v in [(0,4),(2,len(sub)),(6,count*2)]:short(sub,at,v)
    for n,(start,end,delta) in enumerate(segments):short(sub,14+n*2,end);short(sub,16+count*2+n*2,start);short(sub,16+count*4+n*2,delta)
    cmap=struct.pack('>HHHHI',0,1,3,0,12)+sub
    tables=[(b'head',head),(b'OS/2',os2),(b'cmap',cmap)];data=bytearray(12+16*len(tables));word(data,0,0x10000);short(data,4,len(tables))
    for n,(tag,b) in enumerate(tables):at=12+n*16;data[at:at+4]=tag;word(data,at+8,len(data));word(data,at+12,len(b));data+=b
    return data
def main():
    import argparse
    parser=argparse.ArgumentParser();parser.add_argument('--check',action='store_true');args=parser.parse_args()
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet');records=[]
    with tempfile.TemporaryDirectory(prefix='progpu-symbol-capture-') as tmp:
        d=Path(tmp).resolve();project=d/'Control.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/"src/ProGPU.Rust/tests/managed_mapping.cs"))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        subprocess.run([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d,check=True)
        for name,page in [('simplified',0xb200),('traditional',0xb300)]:
            data=font(page);request=struct.pack('<I',len(data))+data+struct.pack('<I',65536)+b''.join(struct.pack('<III',1,c,0) for c in range(65536))
            result=subprocess.run([dotnet,str(d/'artifacts/bin/Control/release/Control.dll')],input=request,capture_output=True,check=True).stdout
            assert len(result)==4+65536*12 and result[:4]==b'\0'*4
            pairs={}
            for c in range(65536):
                error,found,glyph=struct.unpack_from('<III',result,4+c*12);assert error==0 and found==1
                if glyph and glyph!=c:pairs[c]=glyph
            # A direct PUA mapping could conceal a legacy remap for that same
            # code. Independently remove each PUA key before observing fallback.
            requests=[]
            for c in range(0xf000,0xf300):
                data=font(page,c);requests.append(struct.pack('<I',len(data))+data+struct.pack('<IIII',1,1,c,0))
            holes=subprocess.run([dotnet,str(d/'artifacts/bin/Control/release/Control.dll')],input=b''.join(requests),capture_output=True,check=True).stdout
            assert len(holes)==768*16
            for n,c in enumerate(range(0xf000,0xf300)):
                constructor,error,found,glyph=struct.unpack_from('<IIII',holes,n*16);assert constructor==error==0 and found==1
                if glyph:pairs[c]=glyph
            binary=b''.join(struct.pack('>HH',c,g) for c,g in sorted(pairs.items()));path=ROOT/f'src/ProGPU.Rust/src/font/data/managed-symbol-{name}.bin'
            if args.check:assert path.read_bytes()==binary,path
            else:path.write_bytes(binary)
            records.append({'page':hex(page),'queries':65536+768,'mappings':len(pairs),'sha256':hashlib.sha256(binary).hexdigest(),'captured_output_sha256':hashlib.sha256(result+holes).hexdigest()})
    print(json.dumps({'method':'Actual TtfFont.GetGlyphIndex over authored identity-PUA cmap and complete BMP; nonzero nonidentity observations only. No source-array transcription.','records':records}))
if __name__=='__main__':main()
