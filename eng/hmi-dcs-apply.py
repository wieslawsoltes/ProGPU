"""One-use, hash-verified source transfer. No network or arbitrary extraction."""
import base64
import gzip
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile


def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()


root = Path.cwd()
if git('rev-parse', 'HEAD') != os.environ['GITHUB_SHA']:
    raise RuntimeError('Unexpected source checkout.')
remote = git('ls-remote', 'origin', 'refs/heads/feat/hmi-designer').split()[0]
if remote != os.environ['GITHUB_SHA']:
    raise RuntimeError('Feature branch moved; refusing to overwrite work.')
parts = sorted(Path('eng').glob('hmi-dcs-transfer.*'))
if len(parts) != 13:
    raise RuntimeError('Incomplete source transfer.')
encoded = ''.join(p.read_text(encoding='ascii') for p in parts)
data = base64.b64decode(encoded, validate=True)
if hashlib.sha256(data).hexdigest() != '74f697a491c0f63cbc55500a907bf1bb96358163ff99c980a3865913133b71f9':
    raise RuntimeError('Source transfer hash mismatch.')
with gzip.GzipFile(fileobj=io.BytesIO(data)) as compressed:
    patch = compressed.read(1_000_001)
if len(patch) != 146489:
    raise RuntimeError('Unexpected expanded patch size.')
manifest_path = Path('eng/hmi-dcs-preimages.json')
manifest = json.loads(manifest_path.read_text())
for name, expected in manifest.items():
    path = Path(name)
    if path.is_absolute() or '..' in path.parts or path.parts[0] == '.github':
        raise RuntimeError('Invalid transfer target.')
    actual = hashlib.sha256(path.read_bytes()).hexdigest() if path.exists() else None
    if actual != expected:
        raise RuntimeError(f'Source preimage mismatch: {name}')
with tempfile.NamedTemporaryFile(suffix='.patch') as output:
    output.write(patch)
    output.flush()
    stat = git('apply', '--numstat', output.name)
    targets = {line.split('\t', 2)[2] for line in stat.splitlines()}
    if targets != set(manifest):
        raise RuntimeError('Patch target list differs from the reviewed manifest.')
    subprocess.run(['git', 'apply', '--check', '--index', output.name], check=True)
    subprocess.run(['git', 'apply', '--index', output.name], check=True)
subprocess.run(['git', 'rm', '--', *map(str, parts), str(manifest_path), 'eng/hmi-dcs-apply.py'], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
print(f'Applied {len(manifest)} integrity-checked source files; temporary transfer files removed.')
