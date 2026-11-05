"""Stage exact failed packages with unchanged source for diagnosis, never qualification."""
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import struct
import subprocess
import sys
import zipfile

HEAD = 'd0d5e9e0c2cfd15e880b0e0259b2a1e2f1d45f43'
HERE = Path(__file__).resolve().parent

def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def inventory(root):
    paths = sorted(root.rglob('*'))
    if any(p.is_symlink() for p in paths):
        raise ValueError('Linked execution/package content rejected')
    return {p.relative_to(root).as_posix(): {'sha256': sha(p), 'size': p.stat().st_size}
            for p in paths if p.is_file()}

def arm64(data):
    if len(data) < 64 or data[:2] != b'MZ':
        return False
    offset = struct.unpack_from('<I', data, 0x3c)[0]
    return offset + 6 <= len(data) and data[offset:offset+4] == b'PE\0\0' and struct.unpack_from('<H', data, offset+4)[0] == 0xaa64

def verify_packages(feed, receipt):
    expected = receipt['packages']
    if set(inventory(feed)) != set(expected):
        raise ValueError('Package artifact inventory differs')
    native = {}
    for name, info in expected.items():
        path = feed / name
        if path.stat().st_size != info['bytes'] or sha(path) != info['sha256']:
            raise ValueError('Package identity differs: ' + name)
        with zipfile.ZipFile(path) as package:
            for entry, digest in info['nativeArm64Dlls'].items():
                data = package.read(entry)
                if hashlib.sha256(data).hexdigest() != digest or not arm64(data):
                    raise ValueError('Native package member differs: ' + entry)
                filename = Path(entry).name
                if filename in native and native[filename] != digest:
                    raise ValueError('Contradictory package DLL identity')
                native[filename] = digest
    return native

def git(root, *args):
    return subprocess.check_output(['git', '-C', str(root), *args], timeout=30)

def save(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')

def main():
    if os.name != 'nt' or platform.machine().lower() != 'arm64':
        raise ValueError('Native Windows ARM64 required')
    if os.environ.get('GITHUB_RUN_ATTEMPT') != '1' or os.environ.get('RUNNER_ENVIRONMENT') != 'github-hosted':
        raise ValueError('One fresh disposable hosted attempt required')
    original = Path(os.environ['ORIGINAL']).resolve()
    out = Path(os.environ['CAPTURE_OUTPUT']).resolve()
    execution = Path(os.environ['EXECUTION']).resolve()
    receipt = json.loads((HERE / 'package-inventory.json').read_text())
    if receipt['head'] != HEAD or receipt['qualified'] is not False:
        raise ValueError('Invalid diagnostic provenance')
    if git(original, 'rev-parse', 'HEAD').decode().strip() != HEAD:
        raise ValueError('Wrong source commit')
    if git(original, 'status', '--porcelain', '--untracked-files=no').strip():
        raise ValueError('Modified original source')
    feed = original / 'artifacts/packages-native/Release'
    native = verify_packages(feed, receipt)
    if sys.argv[1] == 'preflight':
        out.mkdir(parents=True, exist_ok=False)
        save(out / 'source-and-package.json', {
            'head': HEAD, 'qualified': False, 'packageInventory': receipt,
            'trackedTree': git(original, 'ls-tree', '-r', 'HEAD').decode(),
            'scope': 'Fresh compilation of exact source/package, not byte-identical unretained failed application output.'})
        return
    if sys.argv[1] != 'stage':
        raise ValueError('Unknown staging mode')
    source = original / 'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64'
    if execution.exists():
        raise ValueError('Execution closure must be fresh')
    inventory(source)
    shutil.copytree(source, execution)
    required = {'wgpu_native.dll', 'progpu_native.dll', 'progpu_native_dawn.dll', 'progpu_native_direct2d.dll', 'dxcompiler.dll', 'dxil.dll'}
    for name in required:
        path = execution / name
        if sha(path) != native[name] or not arm64(path.read_bytes()):
            raise ValueError('Execution DLL differs from original package: ' + name)
    if not arm64((execution / 'ProGPU.Native.PackageConsumer.exe').read_bytes()):
        raise ValueError('Wrong apphost architecture')
    warp = Path(os.environ['WINDIR']) / 'System32/d3d10warp.dll'
    if not arm64(warp.read_bytes()):
        raise ValueError('Wrong system WARP architecture')
    closure = out / 'execution-closure.json'
    save(closure, {'head': HEAD, 'qualified': False, 'files': inventory(execution),
         'systemWarpSha256': sha(warp), 'systemWarpPath': str(warp),
         'originalFailedHostWarpHashKnown': False,
         'sourceAndPackageReceiptSha256': sha(out / 'source-and-package.json'),
         'sdk': subprocess.check_output(['dotnet', '--info'], cwd=original, timeout=30).decode(errors='replace')})
    with open(os.environ['GITHUB_ENV'], 'a', encoding='utf-8') as stream:
        stream.write('STAGED_CLOSURE_SHA256=' + sha(closure) + '\n')

if __name__ == '__main__':
    main()
