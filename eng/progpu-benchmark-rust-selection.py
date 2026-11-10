#!/usr/bin/env python3
"""Baseline original native selection and shared managed-policy enabled lookups."""
from pathlib import Path
import datetime,hashlib,json,os,platform,re,struct,subprocess,tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
CPP=['src/ProGPU.Rust/tests/native_selection_benchmark.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_features.cpp']
def capture(cmd,cwd=ROOT):
    r=subprocess.run(cmd,cwd=cwd,capture_output=True,text=True)
    if r.returncode:raise RuntimeError(f'{cmd}:\n{r.stdout}\n{r.stderr}')
    return r.stdout.strip()
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def words(values):return struct.pack('>'+str(len(values))+'H',*values)
def u16(b,p,v):struct.pack_into('>H',b,p,v)
def u32(b,p,v):struct.pack_into('>I',b,p,v)
def fixture(count,varied):
    b=bytearray(words([1,int(varied),14,0,0,0,0]))
    b.extend(words([1])+b'DFLT'+words([8,4,0,0,65535,count])+words(list(range(count))))
    base=len(b);u16(b,6,base);b.extend(words([count]));b.extend(bytes(count*6))
    for i in range(count):
        b[base+2+i*6:base+6+i*6]=f'N{i:03}'.encode();u16(b,base+6+i*6,len(b)-base)
        b.extend(words([0,3,3*i+2,3*i,3*i+1]))
    u16(b,8,len(b));lookups=count*3+2;b.extend(words([lookups]+[2+lookups*2]*lookups+[1,0,0]))
    if varied:
        u32(b,10,len(b));b.extend(words([1,0,0,1,0,0,0,16,1,0,1,0,0,12,0,2,count*3+1,count*3]))
    return bytes(b)
def sfnt(layout):
    # Actual original TtfFont receives the same layout in a minimal owned font.
    head=bytearray(54);u16(head,18,1000);hhea=bytearray(36);u16(hhea,34,1);maxp=words([1,0,1])
    tables=sorted([(b'GSUB',layout),(b'cmap',words([0,0])),(b'head',bytes(head)),(b'hhea',bytes(hhea)),(b'hmtx',words([500,0])),(b'maxp',maxp)])
    b=bytearray(struct.pack('>IHHHH',0x10000,len(tables),0,0,0)+bytes(len(tables)*16))
    for i,(tag,data) in enumerate(tables):
        b.extend(bytes((-len(b))%4));at=12+i*16;b[at:at+4]=tag;u32(b,at+8,len(b));u32(b,at+12,len(data));b.extend(data)
    return bytes(b)
def main():
    cc=os.environ.get('CXX','clang++');dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-selection-baseline-') as tmp:
        d=Path(tmp).resolve();native=d/'native';rust=ROOT/'target/release/examples/selection_benchmark'
        capture(['cargo','build','--locked','--release','--no-default-features','--example','selection_benchmark'])
        capture([cc,'-std=c++20','-O2','-Wall','-Wextra','-Werror','-Isrc/ProGPU.Native/include',*CPP,'-o',str(native)])
        project=d/'Control.csproj';project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_selection_benchmark.cs"))}/><ProjectReference Include={quoteattr(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}/></ItemGroup></Project>')
        capture([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],d)
        managed=d/'artifacts/bin/Control/release/Control.dll';sources=CPP+['src/ProGPU.Rust/examples/selection_benchmark.rs','src/ProGPU.Rust/tests/managed_selection_benchmark.cs','src/ProGPU.Rust/src/font/layout/selection.rs','src/ProGPU.Text/OpenTypeTextShaper.cs']
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),
            'method':'5 warmups + 40 rounds. Rust/C++ native selection, reused output, no timed allocation; C# full original enabled-lookup method including value/ownership metadata, optional accelerator attempts and allocation. Matching indices/hashes qualify shared outputs, not equivalent work or steady-state JIT. Source/header/feature settings construction and output hashing outside timing. Diagnostic baseline only.',
            'background_compilers':[s.strip() for s in capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
            'rust':capture(['rustc','--version']),'cpp':capture([cc,'--version']),'dotnet':capture([dotnet,'--version'],d),
            'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{n:sha(p) for n,p in [('rust',rust),('cpp',native),('managed',managed)]},'inputs':[]}
        for count in [4,32,128,512]:
            for varied in [False,True]:
                data=fixture(count,varied);table=d/'layout.bin';font=d/'font.ttf';table.write_bytes(data);font.write_bytes(sfnt(data));item={'features':count,'feature_variation':varied,'input_sha256':sha(table),'font_sha256':sha(font),'runs':[]};identity=None
                for name,cmd in [('rust',[str(rust),str(table)]),('cpp',[str(native),str(table)]),('managed',[dotnet,str(managed),str(table),str(font)])]:
                    output=capture(cmd);fields=dict(re.findall(r'(\w+)=([0-9.]+)',output));current={k:fields[k] for k in ['lookups','checksum']}
                    if identity is not None and current!=identity:raise ValueError(f'{name} mismatch: {item}')
                    identity=current;item['runs'].append({'implementation':name,'output':output})
                result['inputs'].append(item);print(count,varied,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/selection-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
