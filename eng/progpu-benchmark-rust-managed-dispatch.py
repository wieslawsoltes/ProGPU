#!/usr/bin/env python3
"""Match current original C# nested GSUB execution and every Rust record field."""
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
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_dispatch_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font/layout/managed_gsub.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/buffer.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/context.rs','src/ProGPU.Rust/src/text/managed_shaping.rs','src/ProGPU.Rust/tests/managed_dispatch.cs','src/ProGPU.Rust/examples/managed_dispatch_benchmark.rs','src/ProGPU.Rust/tests/support/managed_glyph_fixture.rs','src/ProGPU.Rust/tests/support/gsub_fixture.rs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-dispatch.py','src/ProGPU.Rust/src/font/layout/managed_gsub/dispatch.rs','src/ProGPU.Rust/src/font/layout/managed_features.rs','src/ProGPU.Rust/src/font/layout/managed_stage.rs','src/ProGPU.Rust/src/font/layout/managed_context.rs','src/ProGPU.Rust/tests/support/managed_dispatch_fixture.rs']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-dispatch-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_dispatch_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/sources[4]))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d)
        dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Complete original raw All-stage dispatch with prepared records, selection and feature policy. Original plan/font construction, buffer reset/preallocation and serialization are untimed. Actual production allocations remain timed. All 31 fields and context/random/mark/depth/traversal/fraction state must match. This is a diagnostic stage baseline, not script preparation, complete shaping, an optimization or frame qualification.',
          'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['single','multiple','delete','ligature','context','chain','reverse','alternate']:
                data=run([str(rust),mode,str(count),'input']).stdout;item={'mode':mode,'glyphs':count,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(count)]),('managed',[dotnet,str(dll)])]:
                    env=os.environ.copy();env['PROGPU_RUST_MANAGED_GSUB_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete output mismatch {mode} {count}')
                    expected=r.stdout;item['runs'].append({'implementation':name,'metrics':{k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())}})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-dispatch-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
