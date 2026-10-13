from pathlib import Path
import os,subprocess,json,time,hashlib,sys
root=Path(__file__).resolve().parent
repo=Path(sys.argv[1]) if len(sys.argv)>1 else Path(os.environ['GITHUB_WORKSPACE'])
out=Path(sys.argv[2]) if len(sys.argv)>2 else Path(os.environ['RUNNER_TEMP'])/'raster-root-selection'
out.mkdir(exist_ok=False)
env=dict(os.environ,PROGPU_BACKEND_DIAGNOSTICS='1')
manifest=json.loads((root/'input-manifest.json').read_text())
for item in manifest['files']:
 p=root/item['path'];assert hashlib.sha256(p.read_bytes()).hexdigest()==item['sha256'],item['path']
(out/'input-manifest.json').write_bytes((root/'input-manifest.json').read_bytes())
def run(name,args,limit):
 start=time.monotonic();record={'args':args,'limitSeconds':limit}
 try:
  with (out/(name+'.log')).open('xb') as log:
   p=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=limit,env=env)
  record['exitCode']=p.returncode
  if p.returncode: raise RuntimeError(name+' failed')
 except BaseException as e:
  record['error']=str(e);raise
 finally:
  record['seconds']=time.monotonic()-start
  (out/(name+'-process.json')).write_text(json.dumps(record,indent=2))
run('build',['dotnet','build',str(root/'Probe.csproj'),'-c','Release','--output',str(out/'probe'),'--nologo','-v:q','-p:ProGPUProbeRepository='+str(repo)],180)
for index,variant in enumerate(['baseline','index','select','select','index','baseline']):
 run(str(index)+'-'+variant,['dotnet',str(out/'probe/Probe.dll'),variant,'timing',str(root),str(out/(str(index)+'-'+variant+'.json'))],60)
for variant in ['baseline','index','select']:
 run(variant+'-coverage',['dotnet',str(out/'probe/Probe.dll'),variant,'coverage',str(root),str(out/(variant+'-coverage.json'))],120)
