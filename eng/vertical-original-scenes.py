"""One fixed ABBA comparison; original input, oracle, GPU and process deadlines."""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import time
import urllib.request
import zipfile

BASELINE = '22bbd4a138735a1098d8a700bb2b81980a06f7d4'
CANDIDATE = 'c254ada12050e934ec7954a8a6d27a0fb76df07b'
RUNS = {'baseline': 38506023874, 'candidate': 38522657518}
SCENES = {
    1: 'f9fb7c73acb86d730add0e812b82219658811829e63f45bdc3ca1066b0cc773c',
    2: '4c54ccf28fd236ff7b1193e4680f6cf49a804a3e3ce64a3e74df373e21a2e8ed',
}
RGB = {
    1: 'f30a1a5872001308c611389e7f2382dfe551aafe62adeb798ea6388a5cee3cbb',
    2: 'f7b87f03257fc37daf345505794f852c6f69ac2fe93ecd51b673dce29c93226d',
}
WGPU = '9f73e41536b3bd96a0a44692ea65888c9de004b19fbf5de90489768667fbbdbc'
repo = Path.cwd()
assert all(os.environ.get(k) == v for k, v in {
    'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
    'RUNNER_OS': 'Windows', 'RUNNER_ARCH': 'ARM64',
    'GITHUB_REPOSITORY': 'wieslawsoltes/ProGPU'}.items())
root = Path(os.environ['RUNNER_TEMP']) / 'vertical-original-scenes'
root.mkdir(exist_ok=False)
inputs = root / 'inputs'
inputs.mkdir()
receipt = {'baseline': BASELINE, 'candidate': CANDIDATE, 'runs': RUNS,
           'diagnosticOnly': True, 'qualified': False, 'processDeadlineSeconds': 60,
           'gpuDeadlineSeconds': 30, 'fixedOrder': ['baseline', 'candidate', 'candidate', 'baseline'],
           'cases': [], 'complete': False}


def save():
    (root / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')


def sha(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


def gh(*args):
    return json.loads(subprocess.check_output(['gh', *args], timeout=120))


def artifact(repository, artifact_id, run_id, commit, expected_digest=None):
    meta = gh('api', f'repos/{repository}/actions/artifacts/{artifact_id}')
    assert meta['workflow_run']['id'] == run_id
    assert meta['workflow_run']['head_sha'] == commit and not meta['expired']
    assert re.fullmatch(r'sha256:[0-9a-f]{64}', meta['digest'])
    if expected_digest:
        assert meta['digest'] == expected_digest
    archive = inputs / (str(artifact_id) + '.zip')
    with archive.open('xb') as output:
        subprocess.run(['gh', 'api', f'repos/{repository}/actions/artifacts/{artifact_id}/zip'],
                       stdout=output, check=True, timeout=120)
    assert archive.stat().st_size == meta['size_in_bytes']
    assert sha(archive) == meta['digest'][7:]
    with zipfile.ZipFile(archive) as entries:
        assert entries.testzip() is None
        assert all(not Path(n).is_absolute() and '..' not in Path(n).parts for n in entries.namelist())
    (root / f'artifact-{artifact_id}.json').write_text(json.dumps(meta, indent=2) + '\n')
    return archive


save()
try:
    # Qualification precedes any native execution. A successful artifact alone
    # is not admission when another job in its whole producer failed.
    variants = {}
    for label, commit in [('baseline', BASELINE), ('candidate', CANDIDATE)]:
        run_id = RUNS[label]
        producer = gh('run', 'view', str(run_id), '--repo', 'wieslawsoltes/ProGPU',
                      '--json', 'headSha,status,conclusion,jobs')
        assert producer['headSha'] == commit and producer['status'] == 'completed'
        assert producer['conclusion'] == 'success' and len(producer['jobs']) == 59
        assert all(j['status'] == 'completed' and j['conclusion'] == 'success' for j in producer['jobs'])
        (root / (label + '-whole-producer.json')).write_text(json.dumps(producer, indent=2) + '\n')
        artifacts = gh('api', f'repos/wieslawsoltes/ProGPU/actions/runs/{run_id}/artifacts?per_page=100')['artifacts']
        selected = [a for a in artifacts if a['name'] == 'progpu-native-runtime-win-arm64' and not a['expired']]
        assert len(selected) == 1
        selected = selected[0]
        digest = None
        if label == 'baseline':
            assert selected['id'] == 11868654411
            digest = 'sha256:321f2e4a49e299ac9fe5277b189e660d6847bf2d01bdaba8b62dc8bb27d4b87a'
        archive = artifact('wieslawsoltes/ProGPU', selected['id'], run_id, commit, digest)
        folder = inputs / label
        folder.mkdir()
        with zipfile.ZipFile(archive) as entries:
            (folder / 'progpu_native.dll').write_bytes(entries.read('runtimes/win-arm64/native/progpu_native.dll'))
        if label == 'baseline':
            assert sha(folder / 'progpu_native.dll') == '617597478521fb2944aa52618cbca4c6ab66ce61fbe2418b83099a3c77839dd8'
        variants[label] = folder

    # The original recorded inputs stay in their producer artifact, never in
    # this diagnostic source branch. Cross-repository authorization errors stop.
    scene_meta = gh('api', 'repos/wieslawsoltes/LibreWPF/actions/artifacts/11861089232')
    assert scene_meta['workflow_run']['id'] == 38489878685
    archive = artifact('wieslawsoltes/LibreWPF', 11861089232, 38489878685,
                       scene_meta['workflow_run']['head_sha'],
                       'sha256:b0d00231a42e473ca22368312f079d87fb9bd492ef700fd09c9f9e68dc8469f2')
    with zipfile.ZipFile(archive) as entries:
        for frame in (1, 2):
            scene = inputs / f'scene-frame-{frame}.bin'
            scene.write_bytes(entries.read(f'runtime/scene-frame-{frame}.bin'))
            assert sha(scene) == SCENES[frame]

    package = inputs / 'silk.net.webgpu.native.wgpu.2.23.0.nupkg'
    urllib.request.urlretrieve('https://api.nuget.org/v3-flatcontainer/silk.net.webgpu.native.wgpu/2.23.0/silk.net.webgpu.native.wgpu.2.23.0.nupkg', package)
    with zipfile.ZipFile(package) as entries:
        names = {n.lower(): n for n in entries.namelist()}
        (inputs / 'wgpu_native.dll').write_bytes(entries.read(names['runtimes/win-arm64/native/wgpu_native.dll']))
    assert sha(inputs / 'wgpu_native.dll') == WGPU
    headers = {
        'wgpu.h': ('https://raw.githubusercontent.com/gfx-rs/wgpu-native/33133da4ec5a0174cb21539ef2d3346f75200411/ffi/wgpu.h',
                   'a8dd002ecfa4d3ca6b8ca2a74bf7733544b68deec279ae1cde3c33acfbeef8bc'),
        'webgpu.h': ('https://raw.githubusercontent.com/webgpu-native/webgpu-headers/aef5e428a1fdab2ea770581ae7c95d8779984e0a/webgpu.h',
                     'defb965756966d04186f80fb193994cfa70b375247da7a34d20608662216a50f'),
    }
    for name, (url, digest) in headers.items():
        urllib.request.urlretrieve(url, inputs / name)
        assert sha(inputs / name) == digest
    header = 'src/ProGPU.Native/include/progpu_native.h'
    original = subprocess.check_output(['git', 'show', BASELINE + ':' + header])
    assert original == subprocess.check_output(['git', 'show', CANDIDATE + ':' + header])
    (inputs / 'progpu_native.h').write_bytes(original)
    assert sha(inputs / 'progpu_native.h') == '6249c702f179fec0f0b736af5bb4f0613c5f3cbf22d082b954ab17ef2ee8578a'
    # Git for Windows may convert a working-copy text file to CRLF. Stage the
    # original immutable blob bytes, preserving the independently frozen hash.
    (inputs / 'replay-warp.cpp').write_bytes(subprocess.check_output([
        'git', 'show', 'HEAD:eng/vertical-replay-warp.cpp']))
    assert sha(inputs / 'replay-warp.cpp') == '3a6c93c6e7fd6b1dbb39b242c974a893ed40f659178636284374e498d7be68ec'
    shutil.copyfile(variants['baseline'] / 'progpu_native.dll', inputs / 'progpu_native.dll')
    with (root / 'compile.log').open('wb') as output:
        subprocess.run(['pwsh', '-NoProfile', '-File', str(repo / 'eng/vertical-compile-probe.ps1'), '-Root', str(inputs)],
                       stdout=output, stderr=subprocess.STDOUT, check=True, timeout=120)
    for folder in variants.values():
        for name in ('replay-warp.exe', 'wgpu_native.dll', 'scene-frame-1.bin', 'scene-frame-2.bin'):
            shutil.copyfile(inputs / name, folder / name)
    immutable = {str(p.relative_to(root)): sha(p) for p in inputs.rglob('*') if p.is_file()}
    receipt['inputSha256'] = immutable
    receipt['originalProbeSourceUnchanged'] = True
    save()
    safe_env = {k: os.environ[k] for k in ('SystemRoot', 'WINDIR', 'PATH', 'TEMP', 'TMP', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA') if k in os.environ}
    safe_env['PROGPU_NATIVE_TRACE_COMPUTE'] = '1'
    for index, label in enumerate(receipt['fixedOrder'], 1):
        for frame in (1, 2):
            folder = variants[label]
            case = root / f'case-{index}-{label}-frame{frame}'
            case.mkdir()
            start = time.monotonic()
            timed_out = False
            with (case / 'stdout.log').open('wb') as out, (case / 'stderr.log').open('wb') as err:
                process = subprocess.Popen([str(folder / 'replay-warp.exe'), str(folder / f'scene-frame-{frame}.bin'), str(case / 'image')],
                                           cwd=folder, env=safe_env, stdout=out, stderr=err)
                try:
                    code = process.wait(timeout=60)
                except subprocess.TimeoutExpired:
                    timed_out = True
                    process.kill()
                    code = process.wait(timeout=5)
            log = (case / 'stdout.log').read_text(errors='replace')
            submit = re.search(r'submitted seconds=([\d.]+) commands=(\d+) draws=(\d+) submissions=(\d+) cpuMs=([\d.]+)', log)
            complete = re.search(r'completed seconds=([\d.]+) token=(\d+)', log)
            loaded = re.findall(r'^loaded=(.+)$', log, re.M)
            expected_paths = {str(folder / n).lower() for n in ('progpu_native.dll', 'wgpu_native.dll')}
            loaded_exact = {s.strip().lower() for s in loaded} == expected_paths
            image = case / 'image.ppm'
            rgb = None
            full_image = None
            if image.exists():
                data = image.read_bytes()
                assert data.startswith(b'P6\n884 861\n255\n') and len(data) == 2283387
                rgb = hashlib.sha256(data[15:]).hexdigest()
                full_image = hashlib.sha256(data).hexdigest()
            expected_counts = {1: (708, 59, 2), 2: (724, 61, 3)}[frame]
            counts = tuple(int(submit[n]) for n in (2, 3, 4)) if submit else None
            item = {'index': index, 'variant': label, 'frame': frame, 'exit': code,
                    'pid': process.pid, 'timedOut': timed_out, 'wallSeconds': time.monotonic() - start,
                    'rgbSha256': rgb, 'imageSha256': full_image, 'exactLibraryLoaded': loaded_exact,
                    'counts': counts, 'submittedSeconds': float(submit[1]) if submit else None,
                    'completedSeconds': float(complete[1]) if complete else None,
                    'afterSubmitSeconds': float(complete[1]) - float(submit[1]) if submit and complete else None,
                    'inputsUnchanged': all(sha(root / path) == digest for path, digest in immutable.items())}
            item['complete'] = (code == 0 and not timed_out and loaded_exact and rgb == RGB[frame]
                                and counts == expected_counts and bool(complete) and item['inputsUnchanged']
                                and 'adapter=Microsoft Basic Render Driver backend=D3D12' in log)
            receipt['cases'].append(item)
            (case / 'receipt.json').write_text(json.dumps(item, indent=2) + '\n')
            save()
            print(json.dumps(item), flush=True)
            if not item['complete']:
                raise RuntimeError('Original case failed; all evidence retained without retry')
    receipt['complete'] = len(receipt['cases']) == 8 and all(c['complete'] for c in receipt['cases'])
    save()
except BaseException as error:
    receipt['error'] = repr(error)
    save()
    raise
