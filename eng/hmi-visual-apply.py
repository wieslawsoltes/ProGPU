"""Apply the exact locally tested HMI sources. Workflow updates are committed separately by the repository owner."""
import hashlib
import lzma
from pathlib import Path
import subprocess
import tempfile

parts = [Path(f'eng/hmi-visual-payload.{i}') for i in range(4)]
packed = b''.join(path.read_bytes() for path in parts)
if len(packed) != 45544:
    raise SystemExit('Unexpected compressed source length')
patch = lzma.decompress(packed, memlimit=256 * 1024 * 1024)
if len(patch) != 225331 or hashlib.sha256(patch).hexdigest() != 'e2c0d39893fe629edcf94484647568ca254e5aa44c0f892034a4da5c021eda67':
    raise SystemExit('HMI patch integrity check failed')
with tempfile.NamedTemporaryFile(suffix='.patch') as file:
    file.write(patch)
    file.flush()
    subprocess.run(['git', 'apply', '--check', '--exclude=.github/workflows/*', file.name], check=True)
    subprocess.run(['git', 'apply', '--index', '--exclude=.github/workflows/*', file.name], check=True)
changed = subprocess.check_output(['git', 'diff', '--cached', '--name-only'], text=True).splitlines()
allowed = ('src/ProGPU.Hmi/', 'src/ProGPU.WinUI.Hmi/', 'src/ProGPU.WinUI.Hmi.Designer/',
           'src/ProGPU.WinUI.Designer/', 'tests/ProGPU.Hmi.', 'samples/HmiDesigner/', 'docs/hmi-')
exact = {'README.md', 'src/ProGPU.Samples/Pages/VisualDesignerPage.cs',
         'src/ProGPU.WinUI/Controls/DataGrid.cs', 'src/ProGPU.WinUI/Controls/TextBox.cs'}
if len(changed) != 62 or any(path not in exact and not path.startswith(allowed) for path in changed):
    raise SystemExit('Unexpected source path outside reviewed HMI patch')
subprocess.run(['git', 'rm', '--', *(str(path) for path in parts), 'eng/hmi-visual-apply.py'], check=True)
subprocess.run(['git', 'diff', '--cached', '--check'], check=True)
print('Verified and applied all 62 source/test/doc changes; temporary payload removed. Workflow updates remain owner-managed.')
