import ctypes as C
from ctypes import wintypes as W
import hashlib,json,os,pathlib,re
from owned_process import run
out=pathlib.Path(os.environ['EVIDENCE']).resolve();root=pathlib.Path(os.environ['EXECUTION']).resolve();mode=os.environ['ROUTE']
receipt=json.loads((out/'private-overlay.json').read_text());expected=receipt['after'];observed={};errors=[]
ps=C.WinDLL('psapi',use_last_error=True)
ps.EnumProcessModulesEx.argtypes=[W.HANDLE,C.POINTER(W.HMODULE),W.DWORD,C.POINTER(W.DWORD),W.DWORD];ps.EnumProcessModulesEx.restype=W.BOOL
ps.GetModuleFileNameExW.argtypes=[W.HANDLE,W.HMODULE,W.LPWSTR,W.DWORD];ps.GetModuleFileNameExW.restype=W.DWORD

def observe(handle):
 modules=(W.HMODULE*2048)();needed=W.DWORD()
 if not ps.EnumProcessModulesEx(handle,modules,C.sizeof(modules),C.byref(needed),3):
  errors.append({'api':'EnumProcessModulesEx','error':C.get_last_error()});return
 if needed.value>C.sizeof(modules):raise RuntimeError('Module inventory exceeded bound')
 for module in modules[:needed.value//C.sizeof(W.HMODULE)]:
  text=C.create_unicode_buffer(32768);n=ps.GetModuleFileNameExW(handle,module,text,len(text))
  if not n or n>=len(text):continue
  p=pathlib.Path(text.value);key=str(p).lower()
  # Collect private runtime and selected driver/compiler paths; hash only after retirement.
  if os.path.normcase(str(p.parent))!=os.path.normcase(str(root)) and p.name.lower() not in ('d3d10warp.dll','d3dcompiler_47.dll','dxcompiler.dll','dxil.dll','d3d12core.dll'):continue
  if key in observed:continue
  observed[key]={'path':str(p)}
assert mode in ('fxc','dxc','dawn')
assert os.environ.get('PROGPU_DX12_SHADER_COMPILER','')==('dxc' if mode=='dxc' else '')
assert os.environ['PROGPU_HIT_TEST_EXECUTION']=='ordered-stages'
actualFiles=[p for p in root.rglob('*') if p.is_file()]
assert not any(p.is_symlink() for p in root.rglob('*'))
assert {str(p.relative_to(root)).replace('\\','/') for p in actualFiles}==set(expected)
for name,entry in expected.items():assert hashlib.sha256((root/name).read_bytes()).hexdigest()==entry['sha256']
arg='--binary-glyph-originals-dawn-only' if mode=='dawn' else '--binary-glyph-originals-only'
try:
 result=run([str(root/'ProGPU.Native.PackageConsumer.exe'),arg,'--software-adapter'],out/'runtime.log',out/'process.json',60,observe)
finally:
 # run() has already terminated/reaped the child and closed its owned job.
 # Filesystem hashing cannot extend the live process deadline.
 for entry in observed.values():
  try:entry['sha256']=hashlib.sha256(pathlib.Path(entry['path']).read_bytes()).hexdigest()
  except OSError as failure:entry['hashError']=str(failure)
 (out/'loaded-modules.json').write_text(json.dumps({'modules':list(observed.values()),'observationErrors':errors},indent=2)+'\n')
text=(out/'runtime.log').read_text(errors='replace')
rows=re.findall(r'^binary-originals provider=(\S+) source=(\S+) cases=880 failedCases=0 pixels=0\s*$',text,re.M)
assert not result['timedOut'] and result['exitUnsigned']==0
assert len(rows)==6 and len({r[1] for r in rows})==6
required=['progpu_native_dawn.dll','progpu_dawn_system_warp.dll'] if mode=='dawn' else ['progpu_native.dll','wgpu_native.dll']
if mode=='dxc':required+=['dxcompiler.dll','dxil.dll']
for name in required:
 path=(root/name).resolve();record=observed.get(str(path).lower());assert record and record['sha256']==expected[name]['sha256'],name
assert any(pathlib.Path(v['path']).name.lower()=='d3d10warp.dll' for v in observed.values())
(out/'result.json').write_text(json.dumps({'route':mode,'records':rows,'process':result,'qualification':False,'candidate':receipt['candidate']},indent=2)+'\n')
