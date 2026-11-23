#!/usr/bin/env python3
"""Compare unchanged native route, explicit profiles, and original C++/C#.

The pre-change Rust executable is built from a specified owned-repository commit.
Temporary source snapshots, binaries and managed/native builds retire afterward.
Each executable retains its original five warmups / forty-round measurement.
"""
from pathlib import Path
import argparse
import datetime
import hashlib
import io
import json
import os
import platform
import re
import subprocess
import tarfile
import tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
CPP=['src/ProGPU.Rust/tests/native_normalization_benchmark.cpp','src/ProGPU.Native/src/Text/progpu_native_unicode.cpp','src/ProGPU.Native/src/Text/progpu_native_unicode_normalization.cpp']
def sha(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def capture(command,cwd=ROOT,env=None):
    r=subprocess.run(command,cwd=cwd,env=env,capture_output=True,text=True)
    if r.returncode:raise RuntimeError(f'{command}:\n{r.stdout}\n{r.stderr}')
    return r.stdout.strip()
def main():
    parser=argparse.ArgumentParser(description=__doc__);parser.add_argument('--base',default='3119468aeedc49bad66fd3517f18973458aabc90');args=parser.parse_args()
    dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet');cc=os.environ.get('CXX','clang++')
    with tempfile.TemporaryDirectory(prefix='progpu-normalization-profiles-baseline-') as temporary:
        d=Path(temporary).resolve();base=d/'base';base.mkdir()
        commit=capture(['git','rev-parse','--verify',args.base+'^{commit}'])
        paths=['Cargo.toml','Cargo.lock','LICENSE','src/ProGPU.Rust','src/ProGPU.Backend/Shaders','src/ProGPU.Text/UnicodeNormalizationData.bin']
        archive=subprocess.run(['git','archive',commit,*paths],cwd=ROOT,capture_output=True,check=True).stdout
        with tarfile.open(fileobj=io.BytesIO(archive)) as tar:tar.extractall(base,filter='data')
        env=os.environ.copy();env['CARGO_TARGET_DIR']=str(d/'targets/before')
        capture(['cargo','build','--locked','--release','--no-default-features','--example','normalization_benchmark'],cwd=base,env=env)
        before=d/'targets/before/release/examples/normalization_benchmark'
        env['CARGO_TARGET_DIR']=str(d/'targets/after')
        rust=d/'targets/after/release/examples/normalization_benchmark'
        capture(['cargo','build','--locked','--release','--no-default-features','--example','normalization_benchmark'],env=env)
        cpp=d/'cpp';capture([cc,'-std=c++20','-O2','-Wall','-Wextra','-Werror','-Isrc/ProGPU.Native/include','-Isrc/ProGPU.Native/src/Text',*CPP,'-o',str(cpp)])
        project=d/'Control.csproj';project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_normalization_benchmark.cs"))}/></ItemGroup></Project>')
        capture([dotnet,'build',str(project),'-c','Release','-o',str(d/'out'),'--nologo','-v:q'],cwd=d)
        managed=d/'out/Control.dll'
        sources=CPP+['src/ProGPU.Rust/tests/managed_normalization_benchmark.cs','src/ProGPU.Rust/examples/normalization_benchmark.rs','src/ProGPU.Rust/src/text/unicode/normalization.rs','src/ProGPU.Rust/src/text/unicode/normalization/profile.rs','src/ProGPU.Rust/src/text/unicode/normalization/profile_data.rs']
        r={'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'base_commit':commit,
           'method':'Five warmups, forty serial rounds per executable; original defaults. Rust/C++ reuse source/output records; C# String.Normalize allocation is inside its timer. All compare code-point count and hash. Common-character workloads exercise all three profiles without claiming matched full shaping or steady-state JIT.',
           'load_average':os.getloadavg(),'background_compilers':[s.strip() for s in capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
           'rust':capture(['rustc','--version']),'cpp':capture([cc,'--version']),'dotnet':capture([dotnet,'--version'],cwd=d),
           'binary_sha256':{k:sha(v) for k,v in [('before',before),('native',rust),('cpp',cpp),('managed',managed)]},'source_sha256':{p:sha(ROOT/p) for p in sources},'inputs':[]}
        for kind in ['ascii','composed','reordered']:
            for form in ['nfd','nfc']:
                for repeats in [1,256,2048]:
                    params=[kind,form,str(repeats)];runs=[('before',[str(before),*params]),('native',[str(rust),*params])]
                    runs.extend((f'profile{p}',[str(rust),*params,str(p)]) for p in [151,160,170])
                    runs.extend([('cpp',[str(cpp),*params,str(ROOT/'src/ProGPU.Text/UnicodeNormalizationData.bin')]),('managed',[dotnet,str(managed),*params])])
                    item={'kind':kind,'form':form,'repeats':repeats,'runs':[]};identity=None
                    for name,command in runs:
                        output=capture(command);fields=dict(re.findall(r'(\w+)=([0-9.]+)',output));current={k:fields[k] for k in ['units','scalars','output','checksum']}
                        if identity is not None and current!=identity:raise ValueError(f'Output mismatch: {name}, {item}')
                        identity=current;item['runs'].append({'implementation':name,'output':output})
                    r['inputs'].append(item);print(kind,form,repeats,item['runs'],flush=True)
        r['temporary_logical_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file())
        (ROOT/'src/ProGPU.Rust/perf/normalization-profiles-baseline.json').write_text(json.dumps(r,indent=2)+'\n')
if __name__=='__main__':main()
