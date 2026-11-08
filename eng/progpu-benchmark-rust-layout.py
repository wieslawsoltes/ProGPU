#!/usr/bin/env python3
"""Baseline original common OpenType query methods; no foreign engine code."""
from pathlib import Path
import datetime
import hashlib
import json
import os
import platform
import re
import struct
import subprocess
import tempfile
from xml.sax.saxutils import quoteattr
ROOT=Path(__file__).resolve().parents[1]
CPP=['src/ProGPU.Rust/tests/native_layout_benchmark.cpp','src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_layout.cpp']
def capture(command, cwd=ROOT):
    result=subprocess.run(command,cwd=cwd,capture_output=True,text=True)
    if result.returncode:
        raise RuntimeError(f'{command}:\n{result.stdout}\n{result.stderr}')
    return result.stdout.strip()
def sha(p): return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def words(values): return struct.pack('>'+str(len(values))+'H',*values)
def main():
    cc=os.environ.get('CXX','clang++');dotnet=os.environ.get('PROGPU_RUST_DOTNET','dotnet')
    with tempfile.TemporaryDirectory(prefix='progpu-layout-baseline-') as tmp:
        d=Path(tmp).resolve();native=d/'native';rust=ROOT/'target/release/examples/layout_benchmark'
        capture(['cargo','build','--locked','--release','--no-default-features','--example','layout_benchmark'])
        capture([cc,'-std=c++20','-O2','-Wall','-Wextra','-Werror','-Isrc/ProGPU.Native/include',*CPP,'-o',str(native)])
        project=d/'Control.csproj'
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include={quoteattr(str(ROOT/"src/ProGPU.Rust/tests/managed_layout_benchmark.cs"))}/><ProjectReference Include={quoteattr(str(ROOT/"src/ProGPU.Text/ProGPU.Text.csproj"))}/></ItemGroup></Project>')
        capture([dotnet,'build',str(project),'-c','Release','--nologo','-v:q','-p:UseArtifactsOutput=true',f'-p:ArtifactsPath={d/"artifacts"}'],cwd=d)
        managed=d/'artifacts/bin/Control/release/Control.dll'
        sources=CPP+['src/ProGPU.Rust/examples/layout_benchmark.rs','src/ProGPU.Rust/tests/managed_layout_benchmark.cs','src/ProGPU.Rust/src/font/layout/coverage.rs','src/ProGPU.Text/OpenTypeTextShaper.cs']
        result={'method':'5 warmups + 40 serial rounds, 65536 permuted glyph queries; validate/allocate outside timer, checksum outside timer; original defaults, managed span delegate call per query. Not shaping or steady-state JIT qualification.',
                'captured_utc':datetime.datetime.now(datetime.timezone.utc).isoformat(),'host':platform.platform(),'load_average':os.getloadavg(),
                'background_compilers':[s.strip() for s in capture(['ps','-Ao','pid=,pcpu=,comm=']).splitlines() if any(n in s for n in ['rustc','clang','dotnet'])],
                'rust':capture(['rustc','--version']),'cpp':capture([cc,'--version']),'dotnet':capture([dotnet,'--version'],cwd=d),
                'source_sha256':{p:sha(ROOT/p) for p in sources},'binary_sha256':{n:sha(p) for n,p in [('rust',rust),('cpp',native),('managed',managed)]},'inputs':[]}
        for count in [256,4096]:
            for kind,fmt in [(0,1),(0,2),(1,1),(1,2)]:
                if fmt==1: data=words(([1,count] if kind==0 else [1,0,count])+[i*16 if kind==0 else i%7 for i in range(count)])
                else: data=words([2,count]+[n for i in range(count) for n in [i*16,i*16+3,i*4 if kind==0 else i%7]])
                file=d/'table.bin';file.write_bytes(data);item={'kind':kind,'format':fmt,'count':count,'input_sha256':sha(file),'runs':[]};identity=None
                for name,cmd in [('rust',[str(rust)]),('cpp',[str(native)]),('managed',[dotnet,str(managed)])]:
                    output=capture(cmd+[str(kind),str(file)]);fields=dict(re.findall(r'(\w+)=([0-9.]+)',output));current={k:fields[k] for k in ['queries','checksum']}
                    if identity is not None and current!=identity: raise ValueError(f'{name} result mismatch: {item}')
                    identity=current;item['runs'].append({'implementation':name,'output':output})
                result['inputs'].append(item);print(kind,fmt,count,item['runs'],flush=True)
        result['temporary_bytes_removed']=sum(p.stat().st_size for p in d.rglob('*') if p.is_file())
        (ROOT/'src/ProGPU.Rust/perf/layout-baseline.json').write_text(json.dumps(result,indent=2)+'\n')
if __name__=='__main__':main()
