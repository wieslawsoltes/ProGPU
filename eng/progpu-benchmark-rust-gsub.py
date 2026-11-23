#!/usr/bin/env python3
"""Compare unchanged native GSUB and Rust with complete result bytes retained."""
from pathlib import Path
import datetime,hashlib,json,os,platform,re,subprocess,tempfile
ROOT=Path(__file__).resolve().parents[1]
def run(args,**kw):return subprocess.run(args,cwd=ROOT,check=True,capture_output=True,**kw)
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def main():
    run(['cargo','build','--locked','--release','--no-default-features','--example','gsub_benchmark'])
    sources=['src/ProGPU.Rust/tests/native_gsub.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gsub.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gdef.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_digest.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_unicode_shaping.cpp','src/ProGPU.Native/src/Text/Unicode/progpu_native_unicode_category.cpp']
    with tempfile.TemporaryDirectory(prefix='progpu-gsub-baseline-') as tmp:
        d=Path(tmp);native=d/'native';rust=ROOT/'target/release/examples/gsub_benchmark';cxx=os.environ.get('CXX','clang++')
        run([cxx,'-std=c++20','-O2','-Wall','-Wextra','-Werror','-Isrc/ProGPU.Native/include','-Isrc/ProGPU.Native/src/Text',*sources,'-o',str(native)])
        all_sources=sources+['src/ProGPU.Rust/src/font/layout/gsub.rs','src/ProGPU.Rust/src/font/layout/gsub/context.rs','src/ProGPU.Rust/src/text/shaping.rs','src/ProGPU.Rust/examples/gsub_benchmark.rs','src/ProGPU.Rust/tests/support/gsub_fixture.rs','eng/progpu-benchmark-rust-gsub.py']
        result={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),'method':'5 warmups + 40 rounds. Original full lookup execution only; parsing, buffer allocation/reset and full-output serialization outside timing. Both executors use reusable caller capacity and allocate nothing during execution. Exact complete state and storage bytes must match. Default compiler policies. Diagnostic baseline, not an optimization, full managed shaping or frame qualification.',
            'background_compilers':[s.strip() for s in run(['ps','-Ao','pid=,pcpu=,comm=']).stdout.decode().splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
            'rust':run(['rustc','--version']).stdout.decode().strip(),'cxx':run([cxx,'--version']).stdout.decode().strip(),'source_sha256':{p:sha(ROOT/p) for p in all_sources},'binary_sha256':{'rust':sha(rust),'native':sha(native)},'inputs':[]}
        for count in [32,512,4096]:
            for mode in ['single','multiple','delete','ligature','context','chain','reverse','random']:
                data=run([str(rust),mode,str(count),'input']).stdout;item={'mode':mode,'glyphs':count,'input_sha256':hashlib.sha256(data).hexdigest(),'runs':[]};expected=None
                for name,cmd in [('rust',[str(rust),mode,str(count)]),('native',[str(native)])]:
                    env=os.environ.copy();env['PROGPU_RUST_GSUB_BENCHMARK']='1';r=run(cmd,input=data,env=env)
                    if expected is not None and r.stdout!=expected:raise ValueError(f'complete output mismatch {mode} {count}')
                    expected=r.stdout;item['runs'].append({'implementation':name,'metrics':{k:float(v) for k,v in re.findall(r'(\w+)=([0-9.]+)',r.stderr.decode())}})
                item['exact_output_bytes']=len(expected);item['output_sha256']=hashlib.sha256(expected).hexdigest();result['inputs'].append(item);print(count,mode,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file());(ROOT/'src/ProGPU.Rust/perf/gsub-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
