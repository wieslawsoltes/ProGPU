"""Apply an integrity-checked source-only HMI patch against exact Git preimages."""
import base64
import hashlib
import json
import lzma
from pathlib import Path, PurePosixPath
import subprocess
import tempfile


def git(*args):
    return subprocess.check_output(['git', *args], text=True).strip()


parts = [Path(f'eng/hmi-authoring-payload.{i}') for i in range(3)]
encoded = ''.join(p.read_text(encoding='ascii') for p in parts)
if len(encoded) > 65536:
    raise RuntimeError('Source payload budget exceeded')
compressed = base64.b64decode(encoded, validate=True)
if hashlib.sha256(compressed).hexdigest() != '619e27777b763140477dcd15f4206120be6078966fa3db18160aa76bc2809e6d':
    raise RuntimeError('Source payload integrity mismatch')
decoder = lzma.LZMADecompressor(memlimit=128 * 1024 * 1024)
raw = decoder.decompress(compressed, max_length=512001)
if not decoder.eof or decoder.unused_data or len(raw) > 512000:
    raise RuntimeError('Invalid or oversized source archive')
change = json.loads(raw)
if git('rev-parse', 'HEAD^') != change['base']:
    raise RuntimeError('The source transfer is not based on the reviewed feature head')
allowed = ('src/ProGPU.WinUI.Designer/', 'src/ProGPU.WinUI.Hmi.Designer/', 'tests/ProGPU.Hmi.Tests/', 'tests/ProGPU.Hmi.VisualSmoke/')
for path, expected in change['old'].items():
    value = PurePosixPath(path)
    if value.is_absolute() or '..' in value.parts or not (path == 'docs/hmi-canvas-authoring.md' or path.startswith(allowed) and path.endswith('.cs')):
        raise RuntimeError(f'Unexpected source path: {path}')
    if expected is None:
        if Path(path).exists():
            raise RuntimeError(f'New path already exists: {path}')
    elif git('rev-parse', 'HEAD:' + path) != expected:
        raise RuntimeError(f'Source preimage changed: {path}')
with tempfile.NamedTemporaryFile(suffix='.patch') as patch:
    patch.write(change['patch'].encode('utf-8'))
    patch.flush()
    subprocess.run(['git', 'apply', '--check', '--index', patch.name], check=True)
    subprocess.run(['git', 'apply', '--index', patch.name], check=True)
if set(git('diff', '--cached', '--name-only').splitlines()) != set(change['old']):
    raise RuntimeError('The applied path inventory differs from the reviewed source')
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
subprocess.run(['git', 'rm', '--', str(Path(__file__)), *map(str, parts)], check=True)
print(f"Applied {len(change['old'])} exact source/test/documentation changes; temporary payload and script removed.")
