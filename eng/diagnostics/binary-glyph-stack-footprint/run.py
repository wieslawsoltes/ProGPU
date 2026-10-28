import hashlib,json,os,pathlib,re,subprocess,sys,struct
from owned_process import run
root=pathlib.Path(__file__).resolve().parent;out=root/'evidence';entry=sys.argv[1]
assert entry in ('cs_binary_horizontal','cs_binary_vertical')
inputs=json.loads((root/'inputs.json').read_text())
for f in inputs['files']:assert hashlib.sha256((root/f['path']).read_bytes()).hexdigest()==f['sha256']
head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();assert head==os.environ['EXPECTED_HEAD']
original=subprocess.check_output(['git','show','686a34bb092ada9eec73b3ce1381dcb592e276b6:src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'])
assert hashlib.sha256(original).hexdigest()==inputs['originalWgslSha256']
(out/'identity.json').write_text(json.dumps({'head':head,'product':'686a34bb092ada9eec73b3ce1381dcb592e276b6','entry':entry,'qualification':False},indent=2)+'\n')
probe=out/'probe.exe';build=['cl','/nologo','/std:c++17','/EHsc','/O2','/W4','/I'+str(out/'compiler'),str(root/'CompilerProbe.cpp'),'/Fe:'+str(probe),'/Fo:'+str(out/'probe.obj')]
with (out/'build.log').open('wb') as log:
 subprocess.run(build,check=True,stdout=log,stderr=subprocess.STDOUT,timeout=120)
probe_bytes=probe.read_bytes();pe=struct.unpack_from('<I',probe_bytes,0x3c)[0];assert probe_bytes[pe:pe+4]==b'PE\0\0' and struct.unpack_from('<H',probe_bytes,pe+4)[0]==0xaa64
(out/'probe-sha256.txt').write_text(hashlib.sha256(probe_bytes).hexdigest()+'\n')
receipts=[];ok=True
for kind in ('fxc','dxc'):
 for variant in ('baseline','candidate'):
  dest=out/(kind+'-'+variant);dest.mkdir();src=root/(variant+'-'+kind+'.hlsl')
  result=run([str(probe),kind,str(src),str(dest),entry,str(out/'compiler')],dest/'process.log',dest/'process.json',60)
  files=[{'path':f.name,'bytes':f.stat().st_size,'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in sorted(dest.iterdir()) if f.is_file()]
  assembly=(dest/'disassembly.txt').read_text(errors='replace') if (dest/'disassembly.txt').exists() else ''
  # Counts are compiler-output observations, never a WARP stack-size guarantee.
  metrics={'textBytes':len(assembly.encode()),'lines':len(assembly.splitlines()),'fxcTemps':re.findall(r'dcl_temps\s+(\d+)',assembly),'fxcLoopInstructions':len(re.findall(r'^loop\b',assembly,re.M)),'dxilAllocaInstructions':len(re.findall(r'\balloca\b',assembly))}
  receipts.append({'compiler':kind,'variant':variant,'entry':entry,'sourceSha256':hashlib.sha256(src.read_bytes()).hexdigest(),'process':result,'files':files,'metrics':metrics})
  ok=ok and not result['timedOut'] and result['exitUnsigned']==0
(out/'comparison.json').write_text(json.dumps({'records':receipts,'qualification':False,'noDeviceCreated':True,'noInputActions':True},indent=2)+'\n')
if not ok:raise SystemExit('One compile-only case failed; original evidence retained, no retry.')
