"""First-launch CPU diagnosis of the unchanged original WebScene corpus.

Uses the unchanged original corpus and provider, never a replacement workload.
No product setting, deadline, pixels, upload or submission checks are relaxed.
"""
import hashlib, json, os, re, shutil, signal, statistics, subprocess, time, zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

BASELINE = '340cd628d7b05417b370f35a6f10e24cba9b6197'
CANDIDATE = 'e584cb56dc353de07f78a9d226eb528ffbc771e2'
CORPUS = '8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb'
RUNS = {'baseline': 38395585615, 'candidate': 38420884031}
assert all(os.environ.get(k) == v for k, v in {
    'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
    'RUNNER_OS': 'macOS', 'GITHUB_REPOSITORY': 'wieslawsoltes/ProGPU'}.items())
kind = 'pipeline-observer'
repo = Path.cwd()
root = Path(os.environ['RUNNER_TEMP']) / 'webscene-pipeline-observer'
root.mkdir(exist_ok=False)

def gh(*args):
    return json.loads(subprocess.check_output(['gh', *args], timeout=120))

def git(*args):
    return subprocess.check_output(['git', *args], cwd=repo)

def sha(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()

def artifact(artifact_id, run_id, commit, digest=None):
    meta = gh('api', f'repos/wieslawsoltes/ProGPU/actions/artifacts/{artifact_id}')
    assert meta['workflow_run']['id'] == run_id
    assert meta['workflow_run']['head_sha'] == commit and not meta['expired']
    assert re.fullmatch(r'sha256:[0-9a-f]{64}', meta['digest'])
    if digest is not None:
        assert meta['digest'] == digest
    archive = root / (str(artifact_id) + '.zip')
    with archive.open('xb') as output:
        subprocess.run(['gh', 'api', f'repos/wieslawsoltes/ProGPU/actions/artifacts/{artifact_id}/zip'],
                       stdout=output, check=True, timeout=120)
    assert archive.stat().st_size == meta['size_in_bytes']
    assert sha(archive) == meta['digest'][7:]
    with zipfile.ZipFile(archive) as entries:
        assert entries.testzip() is None
        assert all(not Path(n).is_absolute() and '..' not in Path(n).parts for n in entries.namelist())
    return archive, meta

# Both additions are separate argv modes. Prove that the complete original
# executable source and all original fixture/ABI/helper files remain identical.
paths = ['src/ProGPU.Native/tests', 'src/ProGPU.Native/include',
         'src/ProGPU.Native/src/Direct2D', 'src/ProGPU.Native/src/SceneBuilder',
         'src/ProGPU.Native/src/Text', 'eng/progpu-native-dawn.version.json']
test_path = 'src/ProGPU.Native/tests/progpu_native_webscene_provider_tests.cpp'
def remove_observer(source, family):
    header = '#include "progpu_native_' + family + '_fixture.hpp"\n'
    mode, function = {'mask_pipeline_demand': ('--mask-pipelines', 'verify_mask_pipeline_demand'),
                      'picture_pipeline_reuse': ('--picture-pipelines', 'verify_picture_pipeline_reuse')}[family]
    addition = ('    if (argc == 4 && (std::strcmp(argv[2], "' + mode + '") == 0 ||\n'
                '                     std::strcmp(argv[2], "' + mode + '-baseline") == 0)) {\n'
                '        return ' + function + '(argv[1], argv[3],\n'
                '            std::strcmp(argv[2], "' + mode + '-baseline") == 0);\n'
                '    }\n')
    assert source.count(header) == 1 and source.count(addition) == 1
    return source.replace(header, '', 1).replace(addition, '', 1)
assert git('diff', '--name-only', CORPUS, BASELINE, '--', *paths).decode().splitlines() == [
    'src/ProGPU.Native/tests/progpu_native_mask_pipeline_demand_fixture.hpp', test_path]
assert git('diff', '--name-only', BASELINE, CANDIDATE, '--', *paths).decode().splitlines() == [
    'src/ProGPU.Native/tests/progpu_native_picture_pipeline_reuse_fixture.hpp', test_path]
baseline_source = git('show', BASELINE + ':' + test_path).decode()
candidate_source = git('show', CANDIDATE + ':' + test_path).decode()
assert remove_observer(baseline_source, 'mask_pipeline_demand') == git('show', CORPUS + ':' + test_path).decode()
assert remove_observer(candidate_source, 'picture_pipeline_reuse') == baseline_source
for path in ['eng/build-progpu-native.sh', 'eng/progpu-stage-native-runtime.sh', 'eng/native-edit-word-icu.json']:
    assert git('show', BASELINE + ':' + path) == git('show', CANDIDATE + ':' + path)

inputs = root / 'inputs'
inputs.mkdir()
producers = {}
variants = {}
for label, commit in [('baseline', BASELINE)]:
    run_id = RUNS[label]
    producer = gh('run', 'view', str(run_id), '--repo', 'wieslawsoltes/ProGPU',
                  '--json', 'headSha,status,conclusion,jobs')
    assert producer['headSha'] == commit and producer['status'] == 'completed' and producer['conclusion'] == 'success'
    assert len(producer['jobs']) == 59 and all(x['status'] == 'completed' and x['conclusion'] == 'success' for x in producer['jobs'])
    producers[label] = producer
    (root / (label + '-whole-producer.json')).write_text(json.dumps(producer, indent=2) + '\n')
    artifacts = gh('api', f'repos/wieslawsoltes/ProGPU/actions/runs/{run_id}/artifacts?per_page=100')['artifacts']
    candidates = [x for x in artifacts if x['name'] == 'progpu-native-runtime-osx-arm64' and not x['expired']]
    if label == 'baseline':
        selected = next(x for x in candidates if x['id'] == 11814642328)
        expected_digest = 'sha256:f02d3f77ef4f675b44904124def67c148619f75b13bca2363c06eb72be8ffa96'
    else:
        assert len(candidates) == 1
        selected = candidates[0]
        expected_digest = selected['digest']
    archive, meta = artifact(selected['id'], run_id, commit, expected_digest)
    (root / (label + '-artifact.json')).write_text(json.dumps(meta, indent=2) + '\n')
    folder = inputs / label
    folder.mkdir()
    with zipfile.ZipFile(archive) as entries:
        name = 'runtimes/osx-arm64/native/libprogpu_native_dawn.dylib'
        (folder / 'libprogpu_native_dawn.dylib').write_bytes(entries.read(name))
        for dependency in ['progpu-native-font-dependency.json', 'progpu-native-edit-word-dependency.json']:
            (folder / dependency).write_bytes(entries.read('runtimes/osx-arm64/native/sdk/' + dependency))
    symbols = subprocess.check_output(['nm', '-a', str(folder / 'libprogpu_native_dawn.dylib')], text=True)
    assert '_icudt_progpu_edit78_dat' in symbols
    variants[label] = folder

archive, meta = artifact(11785614402, 38336442559, '5cdbf72e9c995f0d9c91305e861562a9a6ab989b',
                         'sha256:a10f128d33881c160b3d7c131210f2f0b3b978e045f6f7ca4feb91bbaf1a6f82')
with zipfile.ZipFile(archive) as entries:
    prior = json.loads(entries.read('receipt.json'))
    assert prior['candidate'] == CORPUS and prior['inputsUnchanged']
    for name in ['progpu_native_webscene_provider_tests', 'libwebscene_native_gpu.dylib']:
        path = inputs / name
        path.write_bytes(entries.read('inputs/' + name))
        assert sha(path) == prior['inputSha256']['inputs/' + name]
    (root / 'original-corpus-receipt.json').write_text(json.dumps(prior, indent=2) + '\n')
app = inputs / 'progpu_native_webscene_provider_tests'
app.chmod(0o755)
provider = inputs / 'libwebscene_native_gpu.dylib'
reference = prior['results'][0]['pictures']
assert len(reference) == 14
observer = inputs / 'pipeline-observer.dylib'
headers = repo / 'eng/webscene-pipeline-observer'
assert sha(headers / 'webgpu.h') == '791eca8e9b9dffcd6cde63bb11b000e84541d827ecd1dd22e0c2a48fa81bdac2'
assert sha(headers / 'webscene_gpu_provider.h') == '638defa903a8eb7466df19cdeb8c2edc9fb10b0d9bbed6fb2e7db7e563d159f7'
subprocess.run(['clang++', '-std=c++20', '-O2', '-dynamiclib', '-I' + str(headers),
    '-DPROGPU_PROVIDER_PATH=' + json.dumps(str(provider)),
    str(headers / 'pipeline-observer.cpp'), '-Wl,-reexport_library,' + str(provider),
    '-Wl,-rpath,' + str(inputs), '-o', str(observer)], check=True, timeout=60)
input_hashes = {str(p.relative_to(root)): sha(p) for p in inputs.rglob('*') if p.is_file()}
receipt = {'diagnosticOnly': True, 'qualified': False, 'kind': kind,
           'loadedProduct': BASELINE, 'sourceUnderInvestigation': CANDIDATE, 'candidateBinaryUsed': False, 'runs': {'baseline': RUNS['baseline']},
           'originalExecutableSource': CORPUS, 'originalCorpusUnchanged': True,
           'applicationLimitSeconds': 60, 'samplerIntervalMilliseconds': 10, 'samplerLimitSeconds': 60,
           'inputSha256': input_hashes, 'results': [],
           'eventPipe': 'Native C++ process has no CLR. Full managed renderer gates are separate.',
           'selection': 'Last whole-qualified baseline only, exact original executable/provider and private ICU. Cold/warm are diagnostics, never qualification of the failed candidate.'}

# Do not copy workflow credentials or unrelated host settings into traces.
safe_env = {k: os.environ[k] for k in ['PATH', 'HOME', 'TMPDIR', 'LANG', 'LC_ALL', 'DEVELOPER_DIR'] if k in os.environ}
safe_env['DYLD_PRINT_LIBRARIES'] = '1'

# Fixed plan: one first launch and one subsequent reference, with no other GPU
# process or warmup before the first launch. Neither outcome qualifies a release.
for phase in ['first-launch', 'subsequent-reference']:
    out = root / phase
    out.mkdir()
    env = dict(safe_env, DYLD_LIBRARY_PATH=str(variants['baseline']))
    started = time.monotonic()
    deadline = started + 60
    sampler = None
    timed_out = False
    with (out / 'target.log').open('wb') as target_log, (out / 'sampler.log').open('wb') as sampler_log:
        process = subprocess.Popen([str(app), str(observer), str(out / 'provider.ppm')],
            cwd=out, env=env, stdout=target_log, stderr=subprocess.STDOUT, start_new_session=True)
        target_pid = process.pid
        try:
            sampler = subprocess.Popen(['/usr/bin/sample', str(target_pid), '60', '10',
                '-file', str(out / 'cpu.sample.txt')], stdout=sampler_log,
                stderr=subprocess.STDOUT, env=safe_env, start_new_session=True)
            try:
                code = process.wait(timeout=max(0.001, deadline - time.monotonic()))
            except subprocess.TimeoutExpired:
                timed_out = True
                os.killpg(target_pid, signal.SIGKILL)
                code = process.wait(timeout=5)
            elapsed = time.monotonic() - started
        finally:
            if process.poll() is None:
                os.killpg(target_pid, signal.SIGKILL)
                process.wait(timeout=5)
            if sampler is not None:
                try:
                    sampler_code = sampler.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(sampler.pid, signal.SIGKILL)
                    sampler_code = sampler.wait(timeout=5)
    log = (out / 'target.log').read_text(errors='replace')
    pictures = {p.name: sha(p) for p in sorted(out.glob('*.ppm'))}
    library_lines = [line for line in log.splitlines()
                     if line.startswith('dyld[') and 'libprogpu_native_dawn.dylib' in line]
    selected = str(variants['baseline'] / 'libprogpu_native_dawn.dylib')
    loaded_exact = bool(library_lines) and all(selected in line for line in library_lines)
    sample_path = out / 'cpu.sample.txt'
    row = {'phase': phase, 'targetPid': target_pid, 'exitCode': code,
           'timedOut': timed_out, 'wallSeconds': elapsed, 'samplerExitCode': sampler_code,
           'sampleAvailable': sample_path.is_file() and 'Call graph:' in sample_path.read_text(errors='replace'),
           'exactLibraryLoaded': loaded_exact, 'pictures': pictures,
           'allOriginalFrames': pictures == reference, 'completeSubmissions': 'submission=11852' in log,
           'inputsUnchanged': all(sha(root / name) == value for name, value in input_hashes.items())}
    receipt['results'].append(row)
    (root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({k: v for k, v in row.items() if k != 'pictures'}), flush=True)
    assert loaded_exact and row['inputsUnchanged'], 'Diagnostic provenance failed'
receipt['fixedPlanCompleted'] = len(receipt['results']) == 2
receipt['scope'] = 'First-launch attribution only; a passing or failed sampled process does not replace any original failed gate.'
(root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')

