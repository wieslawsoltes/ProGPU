"""Bounded Instruments comparison of two whole-qualified product artifacts.

Uses the unchanged original corpus and provider, never a replacement workload.
No product setting, deadline, pixels, upload or submission checks are relaxed.
"""
import hashlib, json, os, re, shutil, signal, statistics, subprocess, time, zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

EARLIER = '5853d27635ccf50f261b8451716750121633e371'
BASELINE = '1f01d674a245717751c49b9c8d3dcf3b69eba664'
CANDIDATE = '22bbd4a138735a1098d8a700bb2b81980a06f7d4'
# Filled only from the final exact successful whole producer artifact.
CANDIDATE_ARTIFACT_ID = 11869325513
CANDIDATE_ARTIFACT_DIGEST = 'sha256:daef9c26e582bc942d5ba67532cd7d0d0c19af10a053f75e8feb0c68414f0c3c'
CANDIDATE_LIBRARY_SHA256 = 'ddf4b52ef8085a5d3752528527fc294dfbe3c4f1a090368ae09a1f67632d09a7'
assert CANDIDATE_ARTIFACT_ID and CANDIDATE_ARTIFACT_DIGEST and CANDIDATE_LIBRARY_SHA256
CORPUS = '8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb'
RUNS = {'baseline': 38493974048, 'candidate': 38506023874}
assert all(os.environ.get(k) == v for k, v in {
    'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
    'RUNNER_OS': 'macOS', 'GITHUB_REPOSITORY': 'wieslawsoltes/ProGPU'}.items())
kind = os.environ['CAPTURE_KIND']
assert kind in ('time', 'memory', 'metal')
repo = Path.cwd()
root = Path(os.environ['RUNNER_TEMP']) / ('path-products-' + kind)
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
MASK_PARENT = '340cd628d7b05417b370f35a6f10e24cba9b6197'
assert git('diff', '--name-only', CORPUS, MASK_PARENT, '--', *paths).decode().splitlines() == [
    'src/ProGPU.Native/tests/progpu_native_mask_pipeline_demand_fixture.hpp', test_path]
assert git('diff', '--name-only', MASK_PARENT, EARLIER, '--', *paths).decode().splitlines() == [
    'src/ProGPU.Native/tests/progpu_native_picture_pipeline_reuse_fixture.hpp', test_path]
parent_source = git('show', MASK_PARENT + ':' + test_path).decode()
baseline_source = git('show', EARLIER + ':' + test_path).decode()
assert remove_observer(parent_source, 'mask_pipeline_demand') == git('show', CORPUS + ':' + test_path).decode()
assert remove_observer(baseline_source, 'picture_pipeline_reuse') == parent_source
assert git('show', BASELINE + ':' + test_path).decode() == baseline_source
assert git('show', CANDIDATE + ':' + test_path).decode() == baseline_source
assert git('diff', '--name-only', EARLIER, BASELINE, '--', *paths).decode().splitlines() == [
    'src/ProGPU.Native/tests/progpu_native_direct2d_webgpu_tests.cpp',
    'src/ProGPU.Native/tests/progpu_native_internal_tests.cpp',
    'src/ProGPU.Native/tests/progpu_native_path_pixel_mapping_fixture.hpp']
assert git('diff', '--name-only', BASELINE, CANDIDATE, '--', *paths).decode().splitlines() == []
# These additions are exercised by the final whole producer, while profiling
# executes the byte-frozen complete original corpus, never a narrowed new subset.
for path in ['eng/build-progpu-native.sh', 'eng/progpu-stage-native-runtime.sh', 'eng/native-edit-word-icu.json']:
    assert git('show', BASELINE + ':' + path) == git('show', CANDIDATE + ':' + path)

inputs = root / 'inputs'
inputs.mkdir()
producers = {}
variants = {}
for label, commit in [('baseline', BASELINE), ('candidate', CANDIDATE)]:
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
        selected = next(x for x in candidates if x['id'] == 11863578702)
        expected_digest = 'sha256:2809e3c7160133ac37995a5b52c08c14f6c3e4a27d0506f2eb5de26e373a5c9c'
    else:
        assert len(candidates) == 1
        selected = candidates[0]
        assert selected['id'] == CANDIDATE_ARTIFACT_ID
        expected_digest = CANDIDATE_ARTIFACT_DIGEST
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
    expected_library = {'baseline': 'b7a8552fc7fbabb5f10eaf1f49cd3dd93bb74ec70d249258e112b4cfb596d585', 'candidate': CANDIDATE_LIBRARY_SHA256}[label]
    assert sha(folder / 'libprogpu_native_dawn.dylib') == expected_library
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
assert sha(app) == '0d5c2f5f7d7c9cc19df7cd1d5460fccfda639dd13e024c6c860c2e473775cbe4'
assert sha(provider) == '895daca0a51ba4b1018489c17eec4919df1615bc511d93ca5f889f6106785332'
input_hashes = {str(p.relative_to(root)): sha(p) for p in inputs.rglob('*') if p.is_file()}
receipt = {'diagnosticOnly': True, 'qualified': False, 'kind': kind,
           'baseline': BASELINE, 'candidate': CANDIDATE, 'runs': RUNS,
           'originalExecutableSource': CORPUS, 'originalCorpusUnchanged': True,
           'applicationLimitSeconds': 60, 'outerProfilerLimitSeconds': 150,
           'inputSha256': input_hashes, 'results': [],
           'eventPipe': 'Native C++ process has no CLR. Full managed renderer gates are separate.',
           'selection': 'Original whole-qualified product libraries, both with the exact private ICU dictionary; same original executable/provider.'}

profile_app = app
if kind == 'memory':
    profile_app = inputs / 'profiling-launcher' / app.name
    profile_app.parent.mkdir()
    shutil.copyfile(app, profile_app)
    profile_app.chmod(0o755)
    subprocess.run(['codesign', '--force', '--sign', '-', '--entitlements',
                    str(repo / 'eng/path-products-profiling.entitlements'), '--timestamp=none',
                    str(profile_app)], check=True, timeout=20)
    # Exact-byte identity with the locally reviewed signature-only launcher.
    # Every file-backed code/data section and all non-signature bytes were proven identical.
    assert sha(profile_app) == '044188b9c39965ec9a81fcf1145b4c31cb05f0729c6433c69ce3f246c2894b55'
    identity = json.loads((repo / 'eng/path-products-launcher-identity.json').read_text())
    assert identity['sourceSha256'] == sha(app) and identity['launcherSha256'] == sha(profile_app)
    assert identity['signatureOnlyTransformVerified'] and identity['everyFileBackedCodeAndDataSectionIdentical']
    receipt.update(profilingLauncher=identity, launchExecutableByteIdenticalToProducer=False,
                   launchExecutableCodeAndDataIdenticalToProducer=True)
    input_hashes[str(profile_app.relative_to(root))] = sha(profile_app)
    (root / 'profiling-launcher-identity-proof.json').write_text(json.dumps(identity, indent=2) + '\n')

# Do not copy workflow credentials or unrelated host settings into traces.
safe_env = {k: os.environ[k] for k in ['PATH', 'HOME', 'TMPDIR', 'LANG', 'LC_ALL', 'DEVELOPER_DIR'] if k in os.environ}
safe_env['DYLD_PRINT_LIBRARIES'] = '1'

def bounded(command, out, env=None, limit=150):
    start = time.monotonic()
    timed = False
    with out.open('wb') as output:
        process = subprocess.Popen(command, cwd=out.parent, stdout=output,
                                   stderr=subprocess.STDOUT, env=env or safe_env, start_new_session=True)
        try:
            code = process.wait(timeout=limit)
        except subprocess.TimeoutExpired:
            timed = True
            os.killpg(process.pid, signal.SIGKILL)
            code = process.wait(timeout=5)
    return {'exit': code, 'timedOut': timed, 'wallSeconds': time.monotonic() - start}

def pictures(folder):
    return {p.name: sha(p) for p in sorted(folder.glob('*.ppm'))}

def save(item):
    receipt['results'].append(item)
    receipt['inputsUnchanged'] = all(sha(root / p) == digest for p, digest in input_hashes.items())
    (root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
    print(json.dumps({k: v for k, v in item.items() if k not in ('pictures', 'target')}), flush=True)

def verify_output(out, label):
    log = (out / 'target.log').read_text(errors='replace') if (out / 'target.log').exists() else ''
    actual = pictures(out)
    # xctrace documents stdout redirection only; retain the original stderr
    # stream in its recorder log as well when checking dyld's actual selection.
    recorder = (out / 'profiler.log').read_text(errors='replace') if (out / 'profiler.log').exists() else ''
    loaded = [line for line in (log + '\n' + recorder).splitlines() if 'libprogpu_native_dawn.dylib' in line and line.startswith('dyld[')]
    selected = str(variants[label] / 'libprogpu_native_dawn.dylib')
    loaded_exact = bool(loaded) and all(selected in line for line in loaded)
    return log, actual, loaded_exact

# Predetermined ABBA blocks, not repeated attempts to rescue failures.
for index, label in enumerate(['baseline', 'candidate', 'candidate', 'baseline'] * 3):
    out = root / f'control-{index:02d}-{label}'
    out.mkdir()
    env = dict(safe_env, DYLD_LIBRARY_PATH=str(variants[label]))
    result = bounded(['/usr/bin/time', '-l', '/usr/bin/env', 'DYLD_LIBRARY_PATH=' + str(variants[label]), 'DYLD_PRINT_LIBRARIES=1', str(app), str(provider), str(out / 'provider.ppm')], out / 'target.log', env, 60)
    log, frames, selected = verify_output(out, label)
    rss = re.search(r'(\d+)\s+maximum resident set size', log)
    footprint = re.search(r'(\d+)\s+peak memory footprint', log)
    item = {'phase': 'control', 'index': index, 'variant': label, **result,
            'pictures': frames, 'exactLibraryLoaded': selected,
            'maximumResidentBytes': int(rss[1]) if rss else None,
            'peakFootprintBytes': int(footprint[1]) if footprint else None,
            'complete': result['exit'] == 0 and not result['timedOut'] and selected and frames == reference and 'submission=11852' in log}
    save(item)
    if not item['complete']:
        raise SystemExit('Original control failed; evidence retained without retry')

template, extra = {'time': ('Time Profiler', []), 'memory': ('Allocations', ['--instrument', 'VM Tracker']), 'metal': ('Metal System Trace', [])}[kind]
selectors = {'time': {s: "/trace-toc/run[@number='1']/data/table[@schema='" + s + "']" for s in ['time-profile', 'time-sample', 'dyld-library-load']},
    'memory': {'allocations': "/trace-toc/run[@number='1']/tracks/track[@name='Allocations']/details/detail[@name='Statistics']", 'vm-map': "/trace-toc/run[@number='1']/tracks/track[@name='VM Tracker']/details/detail[@name='Regions Map']"},
    'metal': {s: "/trace-toc/run[@number='1']/data/table[@schema='" + s + "']" for s in ['metal-current-allocated-size', 'metal-resource-allocations', 'metal-kernel-resource-allocations', 'metal-application-command-buffer-submissions', 'metal-command-buffer-completed', 'metal-command-buffer-error', 'graphics-compiler-activity-intervals']}}[kind]
for index, label in enumerate(['baseline', 'candidate', 'candidate', 'baseline']):
    out = root / f'{kind}-{index}-{label}'
    out.mkdir()
    command = ['xcrun', 'xctrace', 'record', '--template', template, *extra, '--time-limit', '60s',
               '--output', str(out / 'capture.trace'), '--target-stdout', str(out / 'target.log'),
               '--env', 'DYLD_LIBRARY_PATH=' + str(variants[label]), '--env', 'DYLD_PRINT_LIBRARIES=1',
               '--launch', '--', str(profile_app), str(provider), str(out / 'provider.ppm')]
    result = bounded(command, out / 'profiler.log')
    log, frames, selected = verify_output(out, label)
    item = {'phase': kind, 'index': index, 'variant': label, **result, 'pictures': frames,
            'exactLibraryLoaded': selected, 'complete': False}
    if result['exit'] == 0 and not result['timedOut']:
        exported = bounded(['xcrun', 'xctrace', 'export', '--input', str(out / 'capture.trace'), '--toc', '--output', str(out / 'toc.xml')], out / 'toc-export.log', limit=60)
        item['tocExport'] = exported
        if exported['exit'] == 0 and not exported['timedOut']:
            toc = ET.parse(out / 'toc.xml')
            target = toc.find('./run/info/target/process')
            summary = toc.find('./run/info/summary')
            item['target'] = target.attrib if target is not None else {}
            item['endReason'] = summary.findtext('end-reason') if summary is not None else None
            item['traceDuration'] = summary.findtext('duration') if summary is not None else None
            item['complete'] = (item['target'].get('return-exit-status') == '0' and item['endReason'] == 'Target app exited'
                                and selected and frames == reference and 'submission=11852' in log)
            item['tables'] = {}
            for name, xpath in selectors.items():
                item['tables'][name] = bounded(['xcrun', 'xctrace', 'export', '--input', str(out / 'capture.trace'), '--xpath', xpath,
                                               '--output', str(out / (name + '.xml'))], out / (name + '-export.log'), limit=90)
            item['complete'] &= all(v['exit'] == 0 and not v['timedOut'] for v in item['tables'].values())
    save(item)
    if not item['complete']:
        raise SystemExit('Instrumented capture incomplete; no retry or qualification claim')

def quantile(values, fraction):
    ordered = sorted(values)
    offset = (len(ordered) - 1) * fraction
    lower = int(offset)
    upper = min(lower + 1, len(ordered) - 1)
    return ordered[lower] + (ordered[upper] - ordered[lower]) * (offset - lower)

receipt['controlStatistics'] = {}
for label in ['baseline', 'candidate']:
    controls = [x for x in receipt['results'] if x['phase'] == 'control' and x['variant'] == label]
    receipt['controlStatistics'][label] = {metric: {'n': len(controls), **{name: quantile([x[metric] for x in controls], q)
        for name, q in [('p50', .5), ('p95', .95), ('p99', .99)]}} for metric in ['wallSeconds', 'maximumResidentBytes', 'peakFootprintBytes']}
receipt['allPlannedCapturesComplete'] = len(receipt['results']) == 16 and all(x['complete'] for x in receipt['results']) and receipt['inputsUnchanged']
receipt['analysisRequired'] = True
(root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
