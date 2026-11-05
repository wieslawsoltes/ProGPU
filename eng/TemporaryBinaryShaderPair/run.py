"""One bounded hosted comparison; never substitutes for native package qualification."""
from pathlib import Path
import hashlib, json, os, platform, re, shutil, struct, subprocess, sys, time, zipfile
import owned_process

ROOT = Path(__file__).resolve().parent
HEAD = '3735757cbb05e66290e14e6fe4225613da73d892'
PACKAGE = 'ProGPU.Backend.Dx12.0.1.0-preview.3703.ci.nupkg'
PACKAGE_SHA = '4eb082a293a620d752aaa09a9857ed206a8312d42229411b9063b4ebad5ca5c9'
SHADER = 'src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'
MAX_LOG = 16 * 1024 * 1024

def require(value, message):
    if not value: raise ValueError(message)

def digest(path):
    with Path(path).open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()

def save(path, data):
    with Path(path).open('x', encoding='utf-8') as f: json.dump(data, f, indent=2)

def command(args, path, env=None, seconds=240):
    started = time.monotonic()
    with path.open('xb') as log:
        process = subprocess.Popen(args, stdout=log, stderr=subprocess.STDOUT, env=env)
        try: result = process.wait(timeout=seconds)
        except subprocess.TimeoutExpired:
            if process.poll() is None:
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], stdout=log, stderr=subprocess.STDOUT, timeout=10)
            process.wait(timeout=10)
            raise
    save(path.with_suffix('.json'), {'argv':args,'exit':result,'elapsed':time.monotonic()-started,'bound':seconds,'sha256':digest(path)})
    require(result == 0, 'Preparation command failed: '+str(path))

def verify_payload(output, package):
    rows = []
    with zipfile.ZipFile(package) as z:
        for name in ('wgpu_native.dll', 'dxcompiler.dll', 'dxil.dll'):
            data = z.read('buildTransitive/runtimes/win-arm64/native/'+name)
            p = output/name
            require(p.read_bytes() == data, 'Runtime differs from original package: '+name)
            require(len(data) >= 64 and data[:2] == b'MZ', 'Missing native PE header')
            offset = struct.unpack_from('<I',data,60)[0]
            require(64 <= offset <= len(data)-6 and data[offset:offset+4] == b'PE\0\0' and struct.unpack_from('<H',data,offset+4)[0] == 0xaa64, 'Runtime is not pure ARM64')
            rows.append({'file':name,'bytes':len(data),'sha256':hashlib.sha256(data).hexdigest()})
    return rows

def review_process(receipt, log, shader):
    require(not receipt['timedOut'] and not receipt['logLimitExceeded'], 'Workload exceeded its original bound')
    text = log.read_text(encoding='utf-8', errors='replace')
    require('shader-identity='+shader in text, 'Missing actual embedded shader identity')
    require('runtime=10.0.14 architecture=Arm64' in text, 'Unexpected actual managed runtime')
    wgpu = re.findall(r'owned-wgpu path=(.*?); sha256=([0-9a-f]{64}); rid=win-arm64; backend=D3D12; compiler=Dxc;',text)
    warp = re.findall(r'original-warp path=(.*?); sha256=([0-9a-f]{64});',text)
    require(wgpu and warp, 'Actual DXC/WARP admission did not complete')
    require(len({h for _,h in wgpu}) == 1 and len({(p.lower(),h) for p,h in warp}) == 1, 'Runtime changed between fixture contexts')
    require(receipt['exitUnsigned'] in (0,0xc0000409,0xc0000005), 'Unexpected diagnostic process failure')
    rows = json.loads((ROOT/'preparation.json').read_text())['fixtureRows']
    expected = [name+' cases='+cases for name,_,cases,_ in rows]
    starts = re.findall(r'^fixture-start (.+)$',text,re.M)
    passes = re.findall(r'^fixture-passed (.+)$',text,re.M)
    require(starts == expected[:len(starts)] and passes == expected[:len(passes)], 'Original fixture order or identity changed')
    require(len(passes) <= len(starts) <= len(passes)+1, 'Fixture completion order changed')
    passed = len(passes)
    if receipt['exitUnsigned'] == 0:
        require(passed == 8 and text.count('completed=True fixtures=8 cases=2537 originalNativePackageQualified=False') == 1, 'Incomplete original fixture inventory')
    return {'exit':receipt['exitHex'],'wgpuSha256':wgpu[0][1],'warpPath':warp[0][0],'warpSha256':warp[0][1],
            'passedFixtures':passed,'managedFailFastReproduced':receipt['exitUnsigned']==0xc0000409,
            'nativeAccessViolationObserved':receipt['exitUnsigned']==0xc0000005,'nativePackageQualified':False}

def main():
    require(platform.system() == 'Windows' and platform.machine().lower() == 'arm64', 'Windows ARM64 only')
    require(os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted', 'Disposable hosted runner only')
    require(os.environ.get('GITHUB_RUN_ATTEMPT') == '1', 'No unchanged retry admitted')
    repository = Path(os.environ['GITHUB_WORKSPACE']).resolve()
    changes = subprocess.check_output(['git','diff','--name-only',HEAD,'HEAD'],cwd=repository,text=True).splitlines()
    require(all(p == '.github/workflows/build.yml' or p.startswith('eng/TemporaryBinaryShaderPair/') for p in changes), 'Unexpected product source difference')
    manifest = json.loads((ROOT/'source-manifest.json').read_text())
    actual = {p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name != 'source-manifest.json'}
    require(actual == set(manifest), 'Unexpected diagnostic source inventory')
    for name,expected in manifest.items(): require(digest(ROOT/name) == expected, 'Diagnostic source changed: '+name)
    temporary = Path(os.environ['RUNNER_TEMP']).resolve()
    work = temporary/'binary-shader-pair'; work.mkdir(exist_ok=False)
    evidence = work/'evidence'; evidence.mkdir()
    save(evidence/'source-manifest.json',manifest)
    save(evidence/'host.json',{k:os.environ.get(k) for k in ('GITHUB_SHA','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','RUNNER_ARCH','RUNNER_OS','ImageOS','ImageVersion')})
    package = temporary/'binary-shader-artifact'/PACKAGE
    require(digest(package) == PACKAGE_SHA, 'Failed-package identity changed')
    feed = work/'feed'; feed.mkdir(); shutil.copy2(package,feed/PACKAGE)
    source = work/'source'
    command(['git','worktree','add','--detach',str(source),HEAD],evidence/'source-checkout.log')
    app = work/'app'; shutil.copytree(ROOT/'app',app)
    env = os.environ.copy(); env['NUGET_PACKAGES'] = str(work/'nuget')
    dotnet = str(Path(shutil.which('dotnet')).resolve())
    command([dotnet,'--info'],evidence/'dotnet-info.log',env)
    sdk = subprocess.check_output([dotnet,'--version'],cwd=source,env=env,text=True,timeout=30).strip()
    save(evidence/'selected-sdk.json',{'version':sdk,'dotnet':dotnet,'installation':'Existing hosted SDK; no installer or runtime override'})
    require(sdk == '10.0.403', 'Original exact SDK is not preinstalled')
    outputs = {}
    try:
        for variant in ('baseline','candidate'):
            data = (ROOT/(variant+'.wgsl')).read_bytes()
            if variant == 'baseline':
                require(data == subprocess.check_output(['git','show',HEAD+':'+SHADER],cwd=repository), 'Original baseline shader changed')
            else:
                require(hashlib.sha256(data).hexdigest() == '3b98a0f04cb68ba842c005025095da3b5599ee23e63bcf5c4715fcabc8777a5c', 'Reviewed candidate shader changed')
            (source/SHADER).write_bytes(data)
            changed = subprocess.check_output(['git','diff','--name-only'],cwd=source,text=True).splitlines()
            require(changed == ([] if variant == 'baseline' else [SHADER]), 'Unexpected source edit')
            output = work/variant
            command([dotnet,'publish',str(app/'Probe.csproj'),'-c','Release','-r','win-arm64','--self-contained','false',
                '-p:UseAppHost=false','-p:ProjectSourceRoot='+str(source),'-p:DiagnosticFeed='+str(feed),'-o',str(output)],evidence/(variant+'-build.log'),env)
            payload = verify_payload(output,package)
            require((output/'ProGPU.Backend.dll').read_bytes().count(data) == 1, 'Canonical shader not present exactly once in actual backend')
            inventory = [{'file':str(p.relative_to(output)), 'bytes':p.stat().st_size, 'sha256':digest(p)} for p in sorted(output.rglob('*')) if p.is_file()]
            save(evidence/(variant+'-build-receipt.json'),{'sourceHead':HEAD,'shaderSha256':hashlib.sha256(data).hexdigest(),'payload':payload,'inventory':inventory})
            outputs[variant] = output
        # These independent CPU controls prove the unchanged native ownership
        # path and the added observation-only log cap before either GPU process.
        control = owned_process.run([sys.executable,'-c','print("paired-control")'],evidence/'control.log',evidence/'control.json',5)
        require(control['exitUnsigned']==0 and not control['timedOut'] and not control['logLimitExceeded'], 'CPU control failed')
        timeout = owned_process.run([sys.executable,'-c','import time; time.sleep(60)'],evidence/'timeout-control.log',evidence/'timeout-control.json',1)
        require(timeout['timedOut'] and timeout['exitUnsigned']==124, 'Owned deadline control failed')
        limit = owned_process.run([sys.executable,'-c','import sys,time; sys.stdout.buffer.write(b"x"*(17*1024*1024)); sys.stdout.flush(); time.sleep(60)'],evidence/'log-control.log',evidence/'log-control.json',5)
        require(limit['logLimitExceeded'] and limit['exitUnsigned']==125, 'Log bound control failed')
        save(evidence/'log-control-bytes.json',{'bytes':(evidence/'log-control.log').stat().st_size,'sha256':digest(evidence/'log-control.log'),'content':'Generated constant x bytes; no application evidence'})
        (evidence/'log-control.log').unlink()
        os.environ['PROGPU_DX12_SHADER_COMPILER'] = 'dxc'
        os.environ.pop('PROGPU_DX12_COMPILER_DIRECTORY',None)
        results = {}
        for variant in ('baseline','candidate'):
            shader = digest(ROOT/(variant+'.wgsl'))
            receipt = owned_process.run([dotnet,str(outputs[variant]/'ProGPU.Tests.dll'),shader],evidence/(variant+'.log'),evidence/(variant+'-process.json'),60)
            results[variant] = review_process(receipt,evidence/(variant+'.log'),shader)
            save(evidence/(variant+'-review.json'),results[variant])
        require(results['baseline']['wgpuSha256'] == results['candidate']['wgpuSha256'] and
            results['baseline']['warpSha256'] == results['candidate']['warpSha256'] and
            results['baseline']['warpPath'].lower() == results['candidate']['warpPath'].lower(), 'Original loaded runtime changed across the pair')
        save(evidence/'result.json',{'completed':True,'nativePackageQualified':False,'results':results})
        require(results['candidate']['exit'] == '0x00000000', 'Candidate did not pass original managed pixels')
    finally:
        # Only this diagnostic's own detached checkout and generated build/cache
        # outputs are removed. Original receipts/logs remain for artifact upload.
        (source/SHADER).write_bytes((ROOT/'baseline.wgsl').read_bytes())
        subprocess.run(['git','worktree','remove','--force',str(source)],cwd=repository,check=True)
        for p in (app,feed,work/'nuget',work/'baseline',work/'candidate'):
            if p.exists(): shutil.rmtree(p)

if __name__ == '__main__': main()
