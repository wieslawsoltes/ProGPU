#!/usr/bin/env python3
"""Match actual original cached positioning plans with active layout variations."""
from pathlib import Path
import datetime,hashlib,json,os,platform,re,subprocess,tempfile
ROOT=Path(__file__).resolve().parents[1]
def run(args,**kw):
    result=subprocess.run(args,cwd=kw.pop("cwd",ROOT),capture_output=True,**kw)
    if result.returncode:
        print(result.stdout.decode(errors='replace'));print(result.stderr.decode(errors='replace'))
        result.check_returncode()
    return result
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def main():
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_positioning_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/tests/support/layout_selection_fixture.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/dispatch.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/variations.rs','src/ProGPU.Rust/src/font/layout/managed_variations.rs','src/ProGPU.Rust/tests/support/gpos_dispatch_fixture.rs','src/ProGPU.Text/OpenTypeVariationData.cs','src/ProGPU.Rust/src/font/layout/managed_gpos.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/buffer.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/context.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/primitive.rs','src/ProGPU.Rust/tests/managed_positioning.cs','src/ProGPU.Rust/examples/managed_positioning_benchmark.rs','src/ProGPU.Rust/tests/support/gpos_fixture.rs','src/ProGPU.Rust/tests/support/managed_glyph_fixture.rs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-positioning.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-positioning-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_positioning_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/"src/ProGPU.Rust/tests/managed_positioning.cs"))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d)
        dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Complete cached positioning plan: original feature/digest filtering, raw lookup, active layout variation pass, and final attachment resolution for attachment workloads. Font instance/plan construction, buffer reset, coordinate capture and serialization are untimed. Production allocations and C# typed delegate overhead remain timed. All 31 fields, classification, mark state and actual normalized capture must match. Diagnostic baseline only; Rust allocation counts not instrumented. Full variation-instance creation, metric preparation, shaping and frame qualification remain separate.',
          'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['single','single-static','single-long','pair','pair-class','cursive','base','mark','context','chain']:
                data=run([str(rust),mode,str(count),'input']).stdout;item={'mode':mode,'glyphs':count,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(count)]),('managed',[dotnet,str(dll)])]:
                    env=os.environ.copy();env['PROGPU_RUST_POSITIONING_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete output mismatch {mode} {count}')
                    expected=r.stdout;item['runs'].append({'implementation':name,'metrics':{k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())}})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-positioning-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
