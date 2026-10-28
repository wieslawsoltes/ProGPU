"""One fixed same-host ABBA experiment; never an acceptance retry."""
from pathlib import Path
import json,os,platform,shutil,subprocess,sys,time
from prepare import ROOT,PIN,NAMES,sha,inventory,expected_variant,validate_variant,require
out=Path(os.environ['EVIDENCE']).resolve();out.mkdir(exist_ok=True);require(not (out/'result.json').exists(),'Preserve previous finite-set evidence');execution=Path(os.environ['EXECUTION']).resolve();require(not execution.exists(),'Fresh constant execution path required')
require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','One disposable hosted attempt required')
require(os.environ.get('RUNNER_OS')=='Windows' and os.environ.get('RUNNER_ARCH')=='ARM64','Native Windows ARM64 host required')
original=Path(os.environ['ORIGINAL'])/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64';before=inventory(original);require(before==PIN['originalConsumerInventory'],'Exact original consumer inventory required')
shutil.copytree(original,execution)
record={'qualification':False,'original419TimeoutPreserved':True,'order':PIN['order'],'records':[],'host':{'system':platform.system(),'machine':platform.machine(),'version':platform.version()},'timing':'Original whole-process monotonic timings; no live stream-reader or fixture changes. Driver/OS caches are not cleared or claimed controlled.'}
module_reference=None
try:
 for i,label in enumerate(PIN['order']):
  name=f'{i+1}-{label}';evidence=out/name;evidence.mkdir()
  for file in NAMES:shutil.copy2(Path(os.environ['PAYLOAD_'+label])/'runtimes/win-arm64/native'/file,execution/file)
  after=inventory(execution);validate_variant(before,after,label)
  overlay={'before':before,'after':after,'changed':sorted(NAMES),'candidate':{'label':label,**PIN['sources'][label]},'qualification':False}
  (evidence/'private-overlay.json').write_text(json.dumps(overlay,indent=2)+'\n')
  env=dict(os.environ,ROUTE='dawn',EVIDENCE=str(evidence),EXECUTION=str(execution),PROGPU_DX12_SHADER_COMPILER='',PROGPU_HIT_TEST_EXECUTION='ordered-stages',PROGPU_BACKEND_DIAGNOSTICS='1')
  with (evidence/'wrapper.log').open('xb') as log:
   wrapper=subprocess.run([sys.executable,str(ROOT/'run_one.py')],env=env,stdout=log,stderr=subprocess.STDOUT,timeout=90)
  item={'index':i+1,'label':label,'wrapperExit':wrapper.returncode};record['records'].append(item)
  require((evidence/'process.json').is_file(),'Preparation failure; no missing workload record may be treated as measurement')
  item['process']=json.loads((evidence/'process.json').read_text());require(item['process']['seconds']==60,'Original process bound required')
  item['passedOriginalGroups']=(evidence/'result.json').is_file()
  require(inventory(execution)==after and inventory(original)==before,'Original or active private closure mutated')
  loaded=json.loads((evidence/'loaded-modules.json').read_text());modules=loaded['modules'];require(all('sha256' in m for m in modules),'Complete module hashes required')
  warp=[m for m in modules if Path(m['path']).name.lower()=='d3d10warp.dll'];require(len(warp)==1 and warp[0]['sha256']==PIN['warpSha256'],'Actual original WARP required')
  for file in ['progpu_native.dll','progpu_native_dawn.dll','progpu_dawn_system_warp.dll']:
   found=[m for m in modules if Path(m['path']).name.lower()==file];require(len(found)==1 and found[0]['sha256']==after[file]['sha256'],'Actual coherent native module required')
  identity={m['path'].lower():m['sha256'] for m in modules if Path(m['path']).name.lower() not in NAMES}
  item['unchangedModuleIdentity']=identity
  if module_reference is None:module_reference=identity
  else:require(identity==module_reference,'Same-host private, compiler and driver module inventory changed')
  (out/'result.json').write_text(json.dumps(record,indent=2)+'\n')
 record['finiteSetCompleted']=True
except BaseException as error:record['error']=repr(error);raise
finally:
 record['processCount']=len(record['records']);(out/'result.json').write_text(json.dumps(record,indent=2)+'\n')
