from pathlib import Path
import os,subprocess,json,time
root=Path(__file__).resolve().parent;out=Path(os.environ['RUNNER_TEMP'])/'packed-raster-loop';out.mkdir(exist_ok=False)
repo=Path(os.environ['GITHUB_WORKSPACE']);env=dict(os.environ,PROGPU_BACKEND_DIAGNOSTICS='1')
def run(name,args,limit):
 start=time.monotonic()
 with (out/(name+'.log')).open('xb') as log:
  p=subprocess.run(args,stdout=log,stderr=subprocess.STDOUT,timeout=limit,env=env)
 (out/(name+'-process.json')).write_text(json.dumps({'args':args,'exit':p.returncode,'seconds':time.monotonic()-start},indent=2))
 if p.returncode: raise RuntimeError(name+' failed')
run('build',['dotnet','build',str(root/'Probe.csproj'),'-c','Release','--output',str(out/'probe'),'--nologo','-v:q'],180)
for index,variant in enumerate(['baseline','candidate','candidate','baseline']):
 run(str(index)+'-'+variant,['dotnet',str(out/'probe/Probe.dll'),variant,str(root),str(out/(str(index)+'-'+variant+'.json'))],60)
run('coverage-build',['dotnet','build',str(repo/'tests/ProGPU.PathRasterization.Conformance/ProGPU.PathRasterization.Conformance.csproj'),'-c','Release','--output',str(out/'conformance'),'--nologo','-v:q'],180)
arch='Arm64' if os.environ['PROCESSOR_ARCHITECTURE'].upper()=='ARM64' else 'X64'
run('coverage',['dotnet',str(out/'conformance/ProGPU.PathRasterization.Conformance.dll'),arch,'coverage'],120)
