#!/usr/bin/env python3
"""Measure the two original glyph filter contracts independently; baseline only."""
from pathlib import Path
import datetime,hashlib,json,os,platform,re,struct,subprocess,tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
CPP=['src/ProGPU.Rust/tests/native_digest_benchmark.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_digest.cpp']
def capture(cmd,cwd=ROOT):
    r=subprocess.run(cmd,cwd=cwd,capture_output=True,text=True)
    if r.returncode:raise RuntimeError(f'{cmd}:\n{r.stdout}\n{r.stderr}')
    return r.stdout.strip()
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def words(values):return struct.pack('>'+str(len(values))+'H',*values)
def main():
    cc=os.environ.get('CXX','clang++');dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-digest-baseline-') as tmp:
        d=Path(tmp).resolve();native=d/'native';rust=ROOT/'target/release/examples/digest_benchmark'
        capture(['cargo','build','--locked','--release','--no-default-features','--example','digest_benchmark'])
        capture([cc,'-std=c++20','-O2','-Wall','-Wextra','-Werror','-Isrc/ProGPU.Native/include',*CPP,'-o',str(native)])
        project=d/'Control.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_digest_benchmark.cs"))}/><ProjectReference Include={quoteattr(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}/></ItemGroup></Project>')
        capture([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],d)
        managed=d/'artifacts/bin/Control/release/Control.dll'
        sources=CPP+['src/ProGPU.Rust/src/font/layout/digest.rs','src/ProGPU.Rust/examples/digest_benchmark.rs','src/ProGPU.Rust/tests/managed_digest_benchmark.cs','src/ProGPU.Text/OpenTypeTextShaper.cs']
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),
            'method':'Two distinct native five-mask and managed three-mask contracts, matched separately. 5 warmups + 40 serial rounds, 65536 permuted glyph queries, allocation/construction/hash outside timing. One cold preparation observation includes first managed JIT; not a stable startup comparison. Managed typed delegate overhead per query. Not complete shaping or optimization evidence.',
            'background_compilers':[s.strip() for s in capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
            'rust':capture(['rustc','--version']),'cpp':capture([cc,'--version']),'dotnet':capture([dotnet,'--version'],d),
            'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{n:sha(p) for n,p in [('rust',rust),('cpp',native),('managed',managed)]},'inputs':[]}
        for count in [1,8,64,256,4096]:
            for ranges in [False,True]:
                values=[i*16 for i in range(count)]
                coverage=words([2,count]+[v for i,g in enumerate(values) for v in [g,g+3,i*4]]) if ranges else words([1,count]+values)
                data=words([1,0,10,12,14,0,0,1,4,1,0,1,8,1,6,0])+coverage
                p=d/'table.bin';p.write_bytes(data);item={'records':count,'ranges':ranges,'input_sha256':sha(p),'runs':[]};identities={}
                for name,contract,cmd in [('rust_native',5,[str(rust),str(p),'0']),('cpp',5,[str(native),str(p)]),('rust_managed',3,[str(rust),str(p),'1']),('managed',3,[dotnet,str(managed),str(p)])]:
                    output=capture(cmd);fields=dict(re.findall(r'(\w+)=([0-9.]+)',output));identity={k:fields[k] for k in ['queries','checksum']}
                    if contract in identities and identity!=identities[contract]:raise ValueError(f'{name} mismatch: {item}')
                    identities[contract]=identity;item['runs'].append({'implementation':name,'masks':contract,'output':output})
                result['inputs'].append(item);print(count,ranges,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file())
        (ROOT/'src/ProGPU.Rust/perf/digest-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
