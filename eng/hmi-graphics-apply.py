"""Apply an integrity-checked source-only HMI patch and retire its transfer files."""
import hashlib
import lzma
from pathlib import Path
import subprocess
import tempfile

parts = [Path(f'eng/hmi-graphics-payload.{i}') for i in range(4)]
packed = b''.join(path.read_bytes() for path in parts)
if len(packed) != 31428:
    raise SystemExit('Unexpected compressed source length')
patch = lzma.decompress(packed, memlimit=256 * 1024 * 1024)
if len(patch) != 146440 or hashlib.sha256(patch).hexdigest() != '9b3236cc94ef2d0708eb5413eb31f89ed62ecf74eb8efb27b68e2ba3857846d8':
    raise SystemExit('HMI source integrity check failed')
with tempfile.NamedTemporaryFile(suffix='.patch') as file:
    file.write(patch)
    file.flush()
    subprocess.run(['git', 'apply', '--check', file.name], check=True)
    subprocess.run(['git', 'apply', '--index', file.name], check=True)
changed = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], text=True).splitlines()
allowed = ('src/ProGPU.Hmi/', 'src/ProGPU.WinUI.Hmi/', 'src/ProGPU.WinUI.Hmi.Designer/',
           'src/ProGPU.WinUI.Designer/', 'tests/ProGPU.Hmi.', 'docs/hmi-')
if len(changed) != 53 or any(path != 'README.md' and not path.startswith(allowed) for path in changed):
    raise SystemExit('Unexpected source path outside reviewed HMI patch')
subprocess.run(['git', 'rm', '--', *(str(path) for path in parts), 'eng/hmi-graphics-apply.py'], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
print('Applied 53 validated HMI source/test/documentation changes. Transfer files removed; workflows remain owner-managed.')
