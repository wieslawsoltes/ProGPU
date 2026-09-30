"""Apply an integrity-checked, locally tested source-only HMI change and retire its transfer files."""
import hashlib
import lzma
from pathlib import Path
import subprocess
import tempfile

parts = [Path(f'eng/hmi-diagram-payload.{i}') for i in range(4)]
packed = b''.join(path.read_bytes() for path in parts)
if len(packed) != 35388 or hashlib.sha256(packed).hexdigest() != '2f23a88cced8048cabc96407812717e8e7b6bc7c41809fd23a384e200cf57c1d':
    raise SystemExit('Unexpected HMI source transfer bytes')
patch = lzma.decompress(packed, memlimit=256 * 1024 * 1024)
if len(patch) != 166055 or hashlib.sha256(patch).hexdigest() != '9024694db6a4a7814f0d8fa3a696a3cfa3fde9fc2ecd15c3b5cd34b500c6a3d2':
    raise SystemExit('HMI source patch integrity check failed')
with tempfile.NamedTemporaryFile(suffix='.patch') as file:
    file.write(patch)
    file.flush()
    subprocess.run(['git', 'apply', '--check', file.name], check=True)
    subprocess.run(['git', 'apply', '--index', file.name], check=True)
changed = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], text=True).splitlines()
allowed = ('src/ProGPU.Hmi/', 'src/ProGPU.WinUI.Hmi/', 'src/ProGPU.WinUI.Hmi.Designer/',
           'src/ProGPU.WinUI.Designer/', 'tests/ProGPU.Hmi.Tests/',
           'tests/ProGPU.Hmi.VisualSmoke/', 'tests/ProGPU.Hmi.SerializationSmoke/', 'docs/hmi-')
exact = {'README.md', 'samples/HmiDesigner/README.md'}
if len(changed) != 40 or any(path not in exact and not path.startswith(allowed) for path in changed):
    raise SystemExit('Unexpected path outside the reviewed HMI source patch')
subprocess.run(['git', 'rm', '--', *(str(path) for path in parts), 'eng/hmi-diagram-apply.py'], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
print('Applied the exact 40-file diagram implementation; transfer files removed. Workflows are owner-managed.')
