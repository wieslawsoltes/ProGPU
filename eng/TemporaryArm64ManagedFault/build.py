"""One bounded hosted comparison; never substitutes for native package qualification."""
from pathlib import Path
import hashlib, json, os, platform, re, shutil, struct, subprocess, sys, time, zipfile
import owned_process

ROOT = Path(__file__).resolve().parent
HEAD = '3735757cbb05e66290e14e6fe4225613da73d892'
PACKAGE = 'ProGPU.Backend.Dx12.0.1.0-preview.3703.ci.nupkg'
PACKAGE_SHA = '4eb082a293a620d752aaa09a9857ed206a8312d42229411b9063b4ebad5ca5c9'
SHADER = 'src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'
SHADER_SHA = 'c9011aa3f9761ca28001ae85fc53e6ea6fd4db3d02d850bd9ebc2bec7eeeecc7'

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

def main():
    require(platform.system() == 'Windows' and platform.machine().lower() == 'arm64', 'Windows ARM64 only')
    require(os.environ.get('GITHUB_ACTIONS') == 'true' and os.environ.get('RUNNER_ENVIRONMENT') == 'github-hosted', 'Disposable hosted runner only')
    require(os.environ.get('GITHUB_RUN_ATTEMPT') == '1', 'No unchanged retry admitted')
    repository = Path(os.environ['GITHUB_WORKSPACE']).resolve()
    changes = subprocess.check_output(['git','diff','--name-only',HEAD,'HEAD'],cwd=repository,text=True).splitlines()
    require(all(p == '.github/workflows/build.yml' or p.startswith('eng/TemporaryArm64ManagedFault/') for p in changes), 'Unexpected product source difference')
    manifest = json.loads((ROOT/'source-manifest.json').read_text())
    actual = {p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file() and '__pycache__' not in p.parts and p.name != 'source-manifest.json'}
    require(actual == set(manifest), 'Unexpected diagnostic source inventory')
    for name,expected in manifest.items(): require(digest(ROOT/name) == expected, 'Diagnostic source changed: '+name)
    temporary = Path(os.environ['RUNNER_TEMP']).resolve()
    work = temporary/'managed-arm64-first-fault'; work.mkdir(exist_ok=False)
    evidence = work/'evidence'; evidence.mkdir()
    save(evidence/'source-manifest.json',manifest)
    save(evidence/'host.json',{k:os.environ.get(k) for k in ('GITHUB_SHA','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','RUNNER_ARCH','RUNNER_OS','ImageOS','ImageVersion')})
    package = temporary/'binary-shader-artifact'/PACKAGE
    require(digest(package) == PACKAGE_SHA, 'Original failed-package identity changed')
    feed = work/'feed'; feed.mkdir(); shutil.copy2(package,feed/PACKAGE)
    source = work/'source'
    command(['git','worktree','add','--detach',str(source),HEAD],evidence/'source-checkout.log')
    app = work/'app'; shutil.copytree(ROOT/'app',app)
    env = os.environ.copy(); env['NUGET_PACKAGES'] = str(work/'nuget')
    dotnet = str(Path(shutil.which('dotnet')).resolve())
    command([dotnet,'--info'],evidence/'dotnet-info.log',env)
    sdk = subprocess.check_output([dotnet,'--version'],cwd=source,env=env,text=True,timeout=30).strip()
    require(sdk == '10.0.403', 'Exact SDK is not preinstalled')
    output = work/'execution'
    data = (ROOT/'canonical.wgsl').read_bytes()
    require(hashlib.sha256(data).hexdigest() == SHADER_SHA and data == subprocess.check_output(['git','show',HEAD+':'+SHADER],cwd=repository), 'Original canonical shader changed')
    # Match the canonical LF resource bytes independently of Git for Windows checkout conversion.
    (source/SHADER).write_bytes(data)
    require(not subprocess.check_output(['git','diff','--name-only'],cwd=source,text=True).splitlines(), 'Unexpected product source edit')
    command([dotnet,'publish',str(app/'Probe.csproj'),'-c','Release','-r','win-arm64','--self-contained','false',
        '-p:UseAppHost=false','-p:ProjectSourceRoot='+str(source),'-p:DiagnosticFeed='+str(feed),'-o',str(output)],evidence/'build.log',env)
    payload = verify_payload(output,package)
    require((output/'ProGPU.Backend.dll').read_bytes().count(data) == 1, 'Canonical shader not embedded exactly once')
    warp = Path(os.environ['WINDIR'])/'System32/d3d10warp.dll'
    files = {p.relative_to(output).as_posix():{'sha256':digest(p),'size':p.stat().st_size} for p in sorted(output.rglob('*')) if p.is_file()}
    closure = {'head':HEAD,'qualified':False,'shaderSha256':SHADER_SHA,'dotnet':dotnet,'dotnetSha256':digest(dotnet),'sdk':sdk,
        'systemWarpSha256':digest(warp),'files':files,'payload':payload}
    path = evidence/'execution-closure.json'; save(path,closure)
    with Path(os.environ['GITHUB_ENV']).open('a',encoding='utf-8') as f:
        f.write('STAGED_CLOSURE_SHA256='+digest(path)+'\n')
        f.write('CAPTURE_OUTPUT='+str(evidence)+'\n')
        f.write('EXECUTION='+str(output)+'\n')
    save(evidence/'prepared.json',{'compiled':True,'executed':False,'qualified':False,'closureSha256':digest(path)})

if __name__ == '__main__': main()
