"""One no-input hosted diagnostic; requires a separately reviewed qualified closure."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import platform
import subprocess
import time
from compare_geometry import compare

ROOT=Path(__file__).resolve().parent
def digest(path):
    with path.open('rb') as stream: return hashlib.file_digest(stream,'sha256').hexdigest()
def save(path,value):
    with path.open('x') as stream: json.dump(value,stream,indent=2)

def verified_closure(path):
    value=json.loads(path.read_text())
    producer=value['producer']
    if producer['wholeBuildConclusion']!='success' or not producer['wholeBuildRunId'] or len(producer['head'])!=40:
        raise ValueError('Whole-qualified producer required; unqualified archives cannot execute')
    if not producer['packageRepositoryCommits'] or set(producer['packageRepositoryCommits'])!={producer['head']}:
        raise ValueError('All selected package sources must match the qualified head')
    for item in value['files']:
        candidate=Path(item['absolutePath'])
        if not candidate.is_absolute() or digest(candidate)!=item['sha256']: raise ValueError('Closure file identity changed')
    names={str(Path(f['absolutePath']).resolve()).casefold() for f in value['files']}
    for key in ('dotnet','referenceAssembly','nativeAssembly'):
        if str(Path(value[key]).resolve()).casefold() not in names: raise ValueError('Executable absent from verified closure')
    return value

def run(command,output,name):
    start=time.monotonic()
    receipt={'command':command,'deadlineSeconds':60,'attempts':1,'qualified':False}
    with (output/(name+'.log')).open('xb') as log:
        process=subprocess.Popen(command,stdout=log,stderr=subprocess.STDOUT)
        receipt['processId']=process.pid
        try: receipt['exitCode']=process.wait(timeout=60)
        except subprocess.TimeoutExpired:
            # This exact owned Popen handle is the sole termination authority.
            receipt['timedOut']=True
            process.kill()
            process.wait(timeout=5)
            receipt['exitCode']=process.returncode
        finally:
            receipt['elapsedSeconds']=time.monotonic()-start
            save(output/(name+'-process.json'),receipt)
    return receipt

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--closure',type=Path,required=True)
    args=parser.parse_args()
    if platform.system()!='Windows' or platform.machine().lower() not in ('amd64','x86_64') or os.getenv('GITHUB_ACTIONS')!='true' or os.getenv('RUNNER_ENVIRONMENT')!='github-hosted':
        raise RuntimeError('Disposable hosted x64 Windows 2025 required; no local VM use')
    if os.getenv('UNHINTED64_RUNNER_LABEL')!='windows-2025': raise RuntimeError('Exact reviewed official runner label required')
    if os.getenv('GITHUB_RUN_ATTEMPT')!='1': raise RuntimeError('No unchanged retry admitted')
    closure=verified_closure(args.closure)
    for name,expected in json.loads((ROOT/'input-manifest.json').read_text()).items():
        if digest(ROOT/name)!=expected: raise RuntimeError('Original input or reference source modified')
    temporary=Path(os.environ['RUNNER_TEMP']).resolve()
    fonts=temporary/'text-raster-font-inputs'
    if fonts.exists(): raise RuntimeError('Private original-font directory already exists')
    output=temporary/'unhinted64-x64-owned-results'
    output.mkdir(exist_ok=False)
    save(output/'closure.json',closure)
    result={'qualified':False,'inputEvents':0,'desktopWindows':0,'attempts':1,'providers':{},'cleanupErrors':[]}
    failed=False
    try:
        reference=output/'original72'
        observed=run([closure['dotnet'],closure['referenceAssembly'],str(reference)],output,'original72')
        if observed['exitCode']!=0 or observed.get('timedOut'): raise RuntimeError('Original reference failed; retained without retry')
        for provider in ('wgpu','dawn'):
            observed=run([closure['dotnet'],closure['nativeAssembly'],provider,str(ROOT/'inputs'),str(reference),str(output/provider),str(output/'closure.json')],output,provider)
            result['providers'][provider]=observed
            if observed['exitCode'] not in (0,2) or observed.get('timedOut'): raise RuntimeError(provider+' failed before full inventory')
            actual=json.loads((output/provider/'result.json').read_text())
            if len(actual['rows'])!=128 or actual['elapsedSeconds']>45: raise RuntimeError('Incomplete or late original workload')
            geometry=compare(ROOT/'inputs',output/provider)
            save(output/provider/'geometry-comparison.json',geometry)
            failed |= observed['exitCode']!=0
        result['completed']=True
        result['pixelFailure']=failed
    except Exception as error:
        failed=True
        result['error']=repr(error)
    finally:
        # Only the two private files created by this owned reference are removed.
        # No font bytes enter the output directory or artifact inventory.
        expected={f['sha256'] for f in json.loads((ROOT/'inputs/original64.json').read_text())['fonts']}
        if fonts.exists():
            for path in fonts.iterdir():
                try:
                    if path.name not in {h+'.ttf' for h in expected} or digest(path) not in expected:
                        raise RuntimeError('Unexpected private file; refused deletion')
                    path.unlink()
                except Exception as error: result['cleanupErrors'].append(repr(error))
            try: fonts.rmdir()
            except Exception as error: result['cleanupErrors'].append(repr(error))
        save(output/'execution.json',result)
        save(output/'hashes.json',{str(p.relative_to(output)):{'bytes':p.stat().st_size,'sha256':digest(p)} for p in output.rglob('*') if p.is_file()})
    return 1 if failed or result['cleanupErrors'] else 0

if __name__=='__main__': raise SystemExit(main())
