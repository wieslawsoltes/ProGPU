import datetime, hashlib, json, os, pathlib, subprocess, sys, time
ROOT = pathlib.Path(__file__).resolve().parent
OUT = ROOT / 'evidence'
OUT.mkdir(exist_ok=True)

def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
def git(*args): return subprocess.check_output(['git', *args], cwd=ROOT, text=True).strip()

manifest = json.loads((ROOT / 'inputs.json').read_text())
receipt = {'utc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
           'actualHead': git('rev-parse', 'HEAD'), 'eventHead': os.environ['EXPECTED_HEAD'],
           'eventMergeSha': os.environ['GITHUB_SHA'], 'runId': os.environ['GITHUB_RUN_ID'],
           'parent': git('rev-parse', 'HEAD^'), 'inputs': {}}
try:
    assert receipt['actualHead'] == receipt['eventHead']
    assert receipt['parent'] == manifest['diagnosticParent']
    for name, expected in manifest['sha256'].items():
        actual = sha(ROOT / name)
        receipt['inputs'][name] = actual
        assert actual == expected, name
    if subprocess.run(['git','cat-file','-e',manifest['productHead']+'^{commit}'],cwd=ROOT,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode:
        subprocess.run(['git','fetch','--no-tags','--depth=1','origin',manifest['productHead']],cwd=ROOT,check=True,timeout=60)
    original = subprocess.check_output(['git', 'show', manifest['productHead'] + ':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'], cwd=ROOT)
    assert hashlib.sha256(original).hexdigest() == manifest['originalWgslSha256']
finally:
    (OUT / 'identity.json').write_text(json.dumps(receipt, indent=2))

with (OUT / 'build.log').open('w') as log:
    build = subprocess.run(['dotnet','build', str(ROOT / 'Probe.csproj'), '-c', 'Release', '-o', str(ROOT / 'compiled'), '--nologo'],
                           cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=120)
assert build.returncode == 0, 'Compile-only harness build failed'
(OUT / 'harness.json').write_text(json.dumps({'sha256':sha(ROOT / 'compiled/Probe.dll')}, indent=2))
import runpy
sample = runpy.run_path(str(ROOT / 'process-metrics.py'))['sample']
if len(sys.argv) != 2 or sys.argv[1] not in ['stub-curve','stub-continuation','distributed-zero']:
    raise SystemExit('Expected one reviewed compiler variant.')
results = []
for variant in [sys.argv[1]]:
    evidence = OUT / variant
    evidence.mkdir()
    started = time.monotonic()
    with (evidence / 'compile.log').open('w') as log:
        child = subprocess.Popen(['dotnet',str(ROOT / 'compiled/Probe.dll'),str(ROOT),str(evidence),variant],
                                 cwd=ROOT,stdout=log,stderr=subprocess.STDOUT)
        result = {'variant':variant,'returncode':None,'timedOut':False}
        try:
            try:
                child.wait(timeout=max(0,55-(time.monotonic()-started)))
            except subprocess.TimeoutExpired:
                try: metrics = sample(int(child._handle) if hasattr(child, '_handle') else None, child.pid)
                except Exception as error: metrics = {'available':False,'error':repr(error)}
                metrics['observedElapsedSeconds'] = time.monotonic()-started
                (evidence / 'pre-deadline-metrics.json').write_text(json.dumps(metrics,indent=2))
                child.wait(timeout=max(0,60-(time.monotonic()-started)))
            result['returncode'] = child.returncode
        except subprocess.TimeoutExpired:
            result['timedOut'] = True
        finally:
            if child.poll() is None:
                child.kill()
            child.wait()
    result['elapsedSeconds'] = time.monotonic() - started
    result['deadlineSeconds'] = 60
    (evidence / 'process.json').write_text(json.dumps(result,indent=2))
    results.append(result)
(OUT / 'processes.json').write_text(json.dumps(results,indent=2))
# Diagnostic outcomes include preserved compiler failure/timeout; none qualify product.
print(json.dumps(results))
assert all(not r['timedOut'] and r['returncode'] == 0 for r in results), results
