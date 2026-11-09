#!/usr/bin/env python3
"""Compare actual original context preparation and repeated ordered matching."""
from pathlib import Path
import datetime,importlib.util,json,os,platform,re,struct,subprocess,tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('selection_fixture',ROOT/'eng/progpu-benchmark-rust-selection.py');base=importlib.util.module_from_spec(spec);spec.loader.exec_module(base)
def packet(count,mode):
    covers=[list(range(count))]*2 if mode=='binary_coverages' else [[10],[20]]
    sub=bytearray(base.words([3,2,0,0,0]))
    for i,coverage in enumerate(covers):base.u16(sub,6+i*2,len(sub));sub.extend(base.words([1,len(coverage),*coverage]))
    data=base.words([1,0,10,12,14,0,0,1,4,5,16 if mode=='flags' else 0,1,8])+sub
    if mode=='ordered_end':glyphs=[(10,65)]*count+[(20,66)]
    elif mode in ['reversed','flags']:glyphs=[(20,66)]+[(10,65)]*count
    elif mode=='ignored':glyphs=[(10,65)]+[(99,0x34f)]*count+[(20,66)]
    else:glyphs=[(10,65)]*count
    font=base.sfnt(data);queries=[0,10,20,65535]
    out=bytearray(struct.pack('<I',len(font))+font+struct.pack('<5I',len(data),0,7,len(queries),1)+data+struct.pack('<4I',*queries)+struct.pack('<4I',0,0,len(glyphs),0))
    for glyph,cp in glyphs:out.extend(struct.pack('<6I',glyph,cp,0,0,0,0))
    return bytes(out),len(glyphs)
def main():
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-context-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_context_benchmark'
        base.capture(['cargo','build','--locked','--release','--no-default-features','--example','managed_context_benchmark'])
        project=d/'Control.csproj';project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_context.cs"))}/><ProjectReference Include={quoteattr(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}/></ItemGroup></Project>')
        base.capture([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],d)
        managed=d/'artifacts/bin/Control/release/Control.dll';sources=['src/ProGPU.Rust/src/font/layout/managed_context.rs','src/ProGPU.Rust/src/font/layout/managed_selection.rs','src/ProGPU.Rust/tests/managed_context.cs','src/ProGPU.Rust/examples/managed_context_benchmark.rs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-context.py']
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Preparation retains original table (Rust) or copies original coverage values (C#). Query times divide 100 calls per round; source glyph buffer/digest construction, reflection transport, serialization, queries and hashing are outside timing. Original compiler/JIT defaults; C# reports actual thread allocation. Full exact serialized context/mask/query/result bytes must match. No full shaping, steady-state JIT, stable speedup or final frame qualification.',
            'background_compilers':[s.strip() for s in base.capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
            'rust':base.capture(['rustc','--version']),'dotnet':base.capture([dotnet,'--version'],d),'source_sha256':{p:base.sha(ROOT/p) for p in sources},'binary_sha256':{'rust':base.sha(rust),'managed':base.sha(managed)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['ordered_end','reversed','ignored','digest_reject','flags','binary_coverages']:
                data,glyphs=packet(count,mode);path=d/'input.bin';path.write_bytes(data);item={'size':count,'mode':mode,'glyphs':glyphs,'input_sha256':base.sha(path),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),str(path)]),('managed',[dotnet,str(managed)])]:
                    env=os.environ.copy();env['PROGPU_RUST_CONTEXT_BENCHMARK']='1'
                    r=subprocess.run(cmd,input=data,capture_output=True,env=env,check=True)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'exact output mismatch: {mode} {count}')
                    expected=r.stdout;fields={k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())};item['runs'].append({'implementation':name,'metrics':fields})
                import hashlib
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();item['matches']=bool(struct.unpack('<I',expected[-4:])[0]);result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-context-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
