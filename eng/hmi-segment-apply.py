"""Apply one bounded, integrity-checked HMI source patch after verifying every original file."""
import base64
import hashlib
import json
import lzma
from pathlib import Path, PurePosixPath
import subprocess
import tempfile

root = Path.cwd().resolve()
parts = [Path(f'eng/hmi-segment-payload.{i}') for i in range(3)]
encoded = ''.join(p.read_text(encoding='ascii') for p in parts)
if len(encoded) != 32252:
    raise RuntimeError('Unexpected payload length')
compressed = base64.b64decode(encoded, validate=True)
if hashlib.sha256(compressed).hexdigest() != '8489a1b457c89875e750c720af3f9d5e050265808c096decf981d43b0aa9bcbc':
    raise RuntimeError('Source payload integrity failure')
decoder = lzma.LZMADecompressor(memlimit=128 * 1024 * 1024)
raw = decoder.decompress(compressed, max_length=2 * 1024 * 1024)
if not decoder.eof or decoder.unused_data:
    raise RuntimeError('Unbounded or trailing payload')
payload = json.loads(raw)
files = payload['files']
if len(files) != 24 or len({f['path'] for f in files}) != len(files):
    raise RuntimeError('Unexpected source manifest')
allowed = ('src/ProGPU.Hmi/Diagram/', 'src/ProGPU.WinUI.Hmi/', 'src/ProGPU.WinUI.Hmi.Designer/', 'tests/ProGPU.Hmi.Tests/', 'tests/ProGPU.Hmi.VisualSmoke/', 'tests/ProGPU.Hmi.SerializationSmoke/', 'docs/hmi-')
for item in files:
    name = item['path']
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or not name.startswith(allowed) or not name.endswith(('.cs', '.md')):
        raise RuntimeError(f'Unadmitted source path: {name}')
    local = root / name
    if local.resolve() != local.absolute():
        raise RuntimeError(f'Symlink source path: {name}')
    old = item['old']
    if old is None:
        if local.exists():
            raise RuntimeError(f'New source already exists: {name}')
    elif not local.is_file() or hashlib.sha256(local.read_bytes()).hexdigest() != old:
        raise RuntimeError(f'Source changed since the validated snapshot: {name}')
subprocess.run(['git', 'diff', '--exit-code'], check=True)
subprocess.run(['git', 'diff', '--cached', '--exit-code'], check=True)
with tempfile.NamedTemporaryFile(suffix='.patch') as temporary:
    temporary.write(payload['patch'].encode('utf-8'))
    temporary.flush()
    subprocess.run(['git', 'apply', '--check', '--index', temporary.name], check=True)
    subprocess.run(['git', 'apply', '--index', temporary.name], check=True)
changed = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], text=True).splitlines()
if set(changed) != {f['path'] for f in files}:
    raise RuntimeError('Patch changed an unlisted path')
for item in files:
    if hashlib.sha256((root / item['path']).read_bytes()).hexdigest() != item['new']:
        raise RuntimeError(f'Unexpected applied bytes: {item["path"]}')
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
subprocess.run(['git', 'rm', '--', 'eng/hmi-segment-apply.py', *map(str, parts)], check=True)
print('Applied 24 exact HMI source/test/documentation changes; transfer files removed.')
