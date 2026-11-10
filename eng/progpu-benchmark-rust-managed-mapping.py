#!/usr/bin/env python3
"""Full-output original nominal, cached ASCII and variation-selector query baseline."""
from pathlib import Path
import datetime,hashlib,json,os,platform,re,subprocess,tempfile
ROOT=Path(__file__).resolve().parents[1]
def run(args,**kw):
    result=subprocess.run(args,cwd=kw.pop('cwd',ROOT),capture_output=True,**kw)
    if result.returncode:
        print(result.stdout.decode(errors='replace'));print(result.stderr.decode(errors='replace'));result.check_returncode()
    return result
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def main():
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_mapping_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font.rs','src/ProGPU.Rust/src/font/managed_cmap.rs','src/ProGPU.Rust/src/font/managed_font.rs','src/ProGPU.Rust/src/font/data/managed-symbol-simplified.bin','src/ProGPU.Rust/src/font/data/managed-symbol-traditional.bin','src/ProGPU.Rust/tests/managed_mapping.cs','src/ProGPU.Rust/tests/support/managed_mapping_fixture.rs','src/ProGPU.Rust/tests/support/managed_mapping_replay.rs','src/ProGPU.Rust/examples/managed_mapping_benchmark.rs','src/ProGPU.Text/TtfFont.cs','src/ProGPU.Text/SfntFontFace.cs','src/ProGPU.Text/ArabicFallbackData.Generated.cs','eng/progpu-benchmark-rust-managed-mapping.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-mapping-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_mapping_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/"src/ProGPU.Rust/tests/managed_mapping.cs"))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d);dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'Actual full-result nominal, cached-font and variation-selector batches. First call plus 5 warmups and 40 measured rounds. SFNT/font construction, input transport, request/result allocation and serialization are untimed; original first-use ASCII cache allocation and production query work remain timed. C# typed raw delegates and normal font calls preserve original runtime behavior. Rust allocations are uninstrumented. Initial diagnostic baseline, not optimization or full shaping/frame qualification.','rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for variant in range(7):
            for kind in range(3):
                for count in [128,4096]:
                    args=[str(variant),str(kind),str(count)];data=run([str(rust),*args,'input']).stdout;item={'variant':variant,'kind':kind,'queries':count,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                    for name,cmd in [('rust',[str(rust),*args]),('managed',[dotnet,str(dll)])]:
                        env=os.environ.copy();env['PROGPU_RUST_MAPPING_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                        if expected is not None and r.stdout!=expected:raise ValueError(f'complete mapping output mismatch {args}')
                        expected=r.stdout;metrics={k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())};assert 'execute_p50_us' in metrics;item['runs'].append({'implementation':name,'metrics':metrics})
                    item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(variant,kind,count,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-mapping-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
