#!/usr/bin/env python3
"""Match actual original raw GPOS and attachment execution with all record fields."""
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
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_gpos_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font/layout/managed_gpos.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/buffer.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/context.rs','src/ProGPU.Rust/src/font/layout/managed_gpos/primitive.rs','src/ProGPU.Rust/tests/managed_gpos.cs','src/ProGPU.Rust/examples/managed_gpos_benchmark.rs','src/ProGPU.Rust/tests/support/gpos_fixture.rs','src/ProGPU.Rust/tests/support/managed_glyph_fixture.rs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-gpos.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-gpos-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_gpos_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/sources[4]))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d)
        dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Original raw lookup at every position, plus final attachment resolution for attachment workloads. Separate resolve/late-zero workloads execute their original buffer methods. Plan/font construction, buffer reset and serialization are untimed. Reusable buffer scratch is warm after five rounds. Production allocations and C# typed delegate overhead remain timed. All 31 fields, classification and restored mark state must match. Diagnostic baseline only; full GPOS feature/variation dispatch, preparation, full shaping and frame qualification are separate.',
          'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['single','pair','cursive','base','ligature','mark','context','chain','resolve','zero']:
                data=run([str(rust),mode,str(count),'input']).stdout;item={'mode':mode,'glyphs':count,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(count)]),('managed',[dotnet,str(dll)])]:
                    env=os.environ.copy();env['PROGPU_RUST_GPOS_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete output mismatch {mode} {count}')
                    expected=r.stdout;item['runs'].append({'implementation':name,'metrics':{k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())}})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-gpos-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
