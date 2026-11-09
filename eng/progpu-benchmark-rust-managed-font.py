#!/usr/bin/env python3
"""Exact-output original managed font construction/selection/palette baselines."""
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
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_font_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font/managed_font.rs','src/ProGPU.Rust/src/font/managed_font/metadata.rs','src/ProGPU.Rust/src/font/managed_font/metrics.rs','src/ProGPU.Rust/src/font/managed_variations.rs','src/ProGPU.Rust/tests/managed_font.cs','src/ProGPU.Rust/tests/support/managed_font_fixture.rs','src/ProGPU.Rust/tests/support/managed_font_replay.rs','src/ProGPU.Rust/examples/managed_font_benchmark.rs','src/ProGPU.Text/TtfFont.cs','src/ProGPU.Text/TtfFont.Variations.cs','src/ProGPU.Text/OpenTypeVariationData.cs','eng/progpu-benchmark-rust-managed-font.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-font-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_font_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/"src/ProGPU.Rust/tests/managed_font.cs"))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d);dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),"method":'First call plus 5 warmups and 40 measured rounds. Construction includes original SFNT selection and complete metadata admission, keeping variation data lazy. Cache-hit and rotating requests use a prepared root/data generation; rotation preserves the original 32-entry FIFO. Palette cloning includes the original owned source copy, constructor, selected variation and overrides. Request arrays, input transport and full output serialization are untimed. Production allocations, Rust replacement drops and original C# GC behavior remain timed; Rust allocation counts are uninstrumented. Initial diagnostic baseline, not complete shaping/frame performance or optimization qualification.' , 'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for variant in range(8):
            for mode in ['construction','hit','rotating','palette']:
                data=run([str(rust),mode,str(variant),'input']).stdout;item={'variant':variant,'mode':mode,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(variant)]),('managed',[dotnet,str(dll)])]:
                    env=os.environ.copy();env['PROGPU_RUST_FONT_BENCHMARK']=mode;r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete font output mismatch {variant} {mode}')
                    expected=r.stdout;metrics={k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())};assert 'execute_p50_us' in metrics;item['runs'].append({'implementation':name,'metrics':metrics})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(variant,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-font-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
