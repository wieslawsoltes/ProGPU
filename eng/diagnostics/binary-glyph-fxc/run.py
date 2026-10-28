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
    assert receipt['parent'] == manifest['productHead']
    for name, expected in manifest['sha256'].items():
        actual = sha(ROOT / name)
        receipt['inputs'][name] = actual
        assert actual == expected, name
    original = subprocess.check_output(['git', 'show', manifest['productHead'] + ':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'], cwd=ROOT)
    assert hashlib.sha256(original).hexdigest() == manifest['sha256']['original-45981.wgsl']
finally:
    (OUT / 'identity.json').write_text(json.dumps(receipt, indent=2))

with (OUT / 'build.log').open('w') as log:
    build = subprocess.run(['dotnet','build', str(ROOT / 'Probe.csproj'), '-c', 'Release', '-o', str(ROOT / 'compiled'), '--nologo'],
                           cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=120)
assert build.returncode == 0, 'Compile-only harness build failed'
(OUT / 'harness.json').write_text(json.dumps({'sha256':sha(ROOT / 'compiled/Probe.dll')}, indent=2))
started = time.monotonic()
with (OUT / 'compile.log').open('w') as log:
    try:
        run = subprocess.run(['dotnet',str(ROOT / 'compiled/Probe.dll'),str(ROOT),str(OUT)], cwd=ROOT,
                             stdout=log, stderr=subprocess.STDOUT, timeout=60)
        result = {'returncode': run.returncode, 'timedOut':False}
    except subprocess.TimeoutExpired:
        result = {'returncode':None, 'timedOut':True}
result['elapsedSeconds'] = time.monotonic() - started
result['deadlineSeconds'] = 60
(OUT / 'process.json').write_text(json.dumps(result,indent=2))
assert not result['timedOut'] and result['returncode'] == 0, result
print('Original failure and candidate success recorded for both entrypoints; compile-only, no GPU/pixel qualification.')
