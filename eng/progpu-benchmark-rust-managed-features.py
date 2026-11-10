#!/usr/bin/env python3
"""Baseline original managed feature metadata and Rust's immutable selection."""
from pathlib import Path
import datetime,importlib.util,json,os,platform,re,tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
spec=importlib.util.spec_from_file_location('selection_fixture',ROOT/'eng/progpu-benchmark-rust-selection.py')
base=importlib.util.module_from_spec(spec);spec.loader.exec_module(base)
capture,sha=base.capture,base.sha
def main():
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-managed-feature-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_feature_benchmark'
        capture(['cargo','build','--locked','--release','--no-default-features','--example','managed_feature_benchmark'])
        project=d/'Control.csproj';project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_feature_benchmark.cs"))}/><ProjectReference Include={quoteattr(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}/></ItemGroup></Project>')
        capture([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],d)
        managed=d/'artifacts/bin/Control/release/Control.dll'
        sources=['src/ProGPU.Rust/src/font/layout/managed_selection.rs','src/ProGPU.Rust/examples/managed_feature_benchmark.rs','src/ProGPU.Rust/tests/managed_feature_benchmark.cs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-features.py','eng/progpu-benchmark-rust-selection.py']
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),
            'method':'5 warmups + 40 rounds; original compiler/JIT defaults. Hash every selected ID, signed value, tag, required flag, digest presence and uses-variation. Timed Rust owns/copies its immutable request and retains source; C# additionally attempts context preparation. Fixtures have empty lookup payloads (both optional accelerators absent). No full shaping or steady-state JIT/speedup qualification. Rust allocations not measured; C# reports actual thread allocations. Output hashing and input construction outside timing.',
            'background_compilers':[s.strip() for s in capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
            'rust':capture(['rustc','--version']),'dotnet':capture([dotnet,'--version'],d),
            'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{n:sha(p) for n,p in [('rust',rust),('managed',managed)]},'inputs':[]}
        for count in [4,32,128,512]:
            for varied in [False,True]:
                data=base.fixture(count,varied);table=d/'layout.bin';font=d/'font.ttf';table.write_bytes(data);font.write_bytes(base.sfnt(data));item={'features':count,'feature_variation':varied,'input_sha256':sha(table),'font_sha256':sha(font),'runs':[]};identity=None
                for name,cmd in [('rust',[str(rust),str(table)]),('managed',[dotnet,str(managed),str(table),str(font)])]:
                    output=capture(cmd);fields=dict(re.findall(r'(\w+)=([0-9.]+)',output));current={k:fields[k] for k in ['lookups','checksum']}
                    if identity is not None and current!=identity:raise ValueError(f'{name} mismatch: {item}')
                    identity=current;item['runs'].append({'implementation':name,'output':output})
                result['inputs'].append(item);print(count,varied,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-features-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
