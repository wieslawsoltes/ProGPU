#!/usr/bin/env python3
"""Exact-output original managed variation-data admission/selection baselines."""
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
    run(['cargo','build','--locked','--release','--no-default-features','--example','managed_variations_benchmark'])
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    sources=['src/ProGPU.Rust/src/font/managed_variations.rs','src/ProGPU.Rust/src/font/managed_variations/parse.rs','src/ProGPU.Rust/src/font/managed_variations/glyph.rs','src/ProGPU.Rust/src/font/layout/managed_variations.rs','src/ProGPU.Rust/src/font/item_variation.rs','src/ProGPU.Rust/tests/managed_variations.cs','src/ProGPU.Rust/tests/support/managed_variations_fixture.rs','src/ProGPU.Rust/tests/support/managed_variation_replay.rs','src/ProGPU.Rust/tests/support/gpos_dispatch_fixture.rs','src/ProGPU.Rust/examples/managed_variations_benchmark.rs','src/ProGPU.Text/OpenTypeVariationData.cs','src/ProGPU.Text/TtfFont.Variations.cs','eng/progpu-benchmark-rust-managed-variations.py']
    with tempfile.TemporaryDirectory(prefix='progpu-managed-variation-baseline-') as tmp:
        d=Path(tmp).resolve();rust=ROOT/'target/release/examples/managed_variations_benchmark'
        from xml.sax.saxutils import escape
        (d/'Control.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="{escape(str(ROOT/"src/ProGPU.Rust/tests/managed_variations.cs"))}"/><ProjectReference Include="{escape(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}"/></ItemGroup></Project>')
        run([dotnet,'build',str(d/'Control.csproj'),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d);dll=d/'artifacts/bin/Control/release/Control.dll'
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Admission parses all original managed variation metadata and owns lazy glyph-cache slots; instance selection uses an already-parsed owner and freezes settings/normalized coordinates/HVAR scalars. Source SFNT ownership setup, fixture name callback construction, input transport and complete output serialization are untimed. Production allocations, Rust replacement drops and C# typed-delegate overhead remain timed; C# GC retains its original policy. Diagnostic baseline only; cold/warm gvar query throughput, complete TtfFont wrapper/cache, shaping and frame performance remain separate. Rust allocation counts are not instrumented.', 'rust':run(['rustc','--version']).stdout.decode().strip(),'dotnet':run([dotnet,'--info']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{'rust':sha(rust),'managed_control':sha(dll)},'inputs':[]}
        for axes in [1,2,3]:
            for variant in range(8):
                for mode in ['admission','instance']:
                    data=run([str(rust),mode,str(axes),str(variant),'input']).stdout;item={'axes':axes,'variant':variant,'mode':mode,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                    for name,cmd in [('rust',[str(rust),mode,str(axes),str(variant)]),('managed',[dotnet,str(dll)])]:
                        env=os.environ.copy();env['PROGPU_RUST_INSTANCE_BENCHMARK']=mode;r=run(cmd,input=data,env=env)
                        if expected is not None and r.stdout!=expected:raise ValueError(f'complete instance output mismatch {axes} {variant} {mode}')
                        expected=r.stdout;metrics={k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())};assert 'execute_p50_us' in metrics;item['runs'].append({'implementation':name,'metrics':metrics})
                    item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(axes,variant,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/managed-variations-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
