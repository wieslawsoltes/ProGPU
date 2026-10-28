import hashlib,json,os,pathlib,subprocess,sys,struct
from owned_process import run
from comparison import admit,metrics,require
root=pathlib.Path(__file__).resolve().parent;out=root/'evidence';entry=sys.argv[1]
require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','One disposable hosted attempt required')
inputs=json.loads((root/'inputs.json').read_text())
for f in inputs['files']:require(hashlib.sha256((root/f['path']).read_bytes()).hexdigest()==f['sha256'],'Diagnostic file changed: '+f['path'])
head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();require(head==os.environ['EXPECTED_HEAD'],'Exact diagnostic head required')
original=subprocess.check_output(['git','show',inputs['baselineProductHead']+':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'])
require(hashlib.sha256(original).hexdigest()==inputs['baselineWgslSha256'],'Actual product baseline changed')
record=dict(head=head,entry=entry,qualification=False,noDeviceCreated=True,noInputActions=True,baselineReexecuted=False,records=[])
try:
 baseline=json.loads((root/'retained-baseline.json').read_text())
 selected=admit(root,entry,json.loads((out/'system-compiler.json').read_text(encoding='utf-8-sig')),json.loads((out/'compiler-inputs.json').read_text()),baseline)
 record['retainedBaseline']=selected;record['compilerIdentityMatched']=True
 probe=out/'probe.exe';build=['cl','/nologo','/std:c++17','/EHsc','/O2','/W4','/I'+str(out/'compiler'),str(root/'CompilerProbe.cpp'),'/Fe:'+str(probe),'/Fo:'+str(out/'probe.obj')]
 with (out/'build.log').open('wb') as log:subprocess.run(build,check=True,stdout=log,stderr=subprocess.STDOUT,timeout=120)
 probe_bytes=probe.read_bytes();pe=struct.unpack_from('<I',probe_bytes,0x3c)[0];require(probe_bytes[pe:pe+4]==b'PE\0\0' and struct.unpack_from('<H',probe_bytes,pe+4)[0]==0xaa64,'Actual ARM64 probe required')
 record['probeSha256']=hashlib.sha256(probe_bytes).hexdigest();ok=True
 for kind in ('fxc','dxc'):
  dest=out/kind;dest.mkdir();src=root/('candidate-'+kind+'.hlsl')
  result=run([str(probe),kind,str(src),str(dest),entry,str(out/'compiler')],dest/'process.log',dest/'process.json',60)
  files=[{'path':f.name,'bytes':f.stat().st_size,'sha256':hashlib.sha256(f.read_bytes()).hexdigest()} for f in sorted(dest.iterdir()) if f.is_file()]
  assembly=(dest/'disassembly.txt').read_text(errors='replace') if (dest/'disassembly.txt').exists() else ''
  before=(root/selected['records'][kind]['retainedDisassembly']).read_text(errors='replace')
  observed=dict(compiler=kind,entry=entry,sourceSha256=hashlib.sha256(src.read_bytes()).hexdigest(),process=result,files=files,baselineMetrics=metrics(before),candidateMetrics=metrics(assembly))
  record['records'].append(observed);ok=ok and not result['timedOut'] and result['exitUnsigned']==0
 record['allCompilesSucceeded']=ok
 if not ok:raise RuntimeError('Candidate compile failed; evidence retained, no retry')
except BaseException as error:record['error']=repr(error);raise
finally:(out/'comparison.json').write_text(json.dumps(record,indent=2)+'\n')
