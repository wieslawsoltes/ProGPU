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
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_gsub_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font/layout/managed_gsub.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/buffer.rs','src/ProGPU.Rust/src/font/layout/managed_gsub/context.rs','src/ProGPU.Rust/src/text/managed_shaping.rs','src/ProGPU.Rust/tests/managed_gsub_execution.cs','src/ProGPU.Rust/examples/managed_gsub_benchmark.rs','src/ProGPU.Rust/tests/support/managed_glyph_fixture.rs','src/ProGPU.Rust/tests/support/gsub_fixture.rs','src/ProGPU.Text/OpenTypeTextShaper.cs','eng/progpu-benchmark-rust-managed-gsub.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-gsub-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_gsub_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/sources[4]))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d)
        dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Descending original glyph-10 positions invoke the original nested lookup or reverse subtable. Font construction, full-record buffer reset/preallocation and serialization are outside timing. Actual production allocations remain in timing. All 31 fields, classes, return values and retained state must match. This is a diagnostic baseline, not full staged shaping, an optimization or frame qualification.',
          'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['single','multiple','delete','ligature','context','chain','reverse','alternate']:
                data=run([str(rust),mode,str(count),'input']).stdout;item={'mode':mode,'glyphs':count,'nested_or_reverse_calls':count//4,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(count)]),('managed',[dotnet,str(dll)])]:
                    env=os.environ.copy();env['PROGPU_RUST_MANAGED_GSUB_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete output mismatch {mode} {count}')
                    expected=r.stdout;item['runs'].append({'implementation':name,'metrics':{k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())}})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-gsub-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
