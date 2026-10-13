"""Matched full original native corpus on disposable hosted macOS.

Profiles are diagnostic evidence, never a substitute for the successful whole
producer Build or the unchanged original uninstrumented 60-second acceptance.
"""
import hashlib, json, os, re, shutil, signal, subprocess, time
import xml.etree.ElementTree as ET
from pathlib import Path

CANDIDATE='8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb'
BASELINE='9873da2149939b1de80c003b41c06f6c53e477b4'
assert all(os.environ.get(k)==v for k,v in {
    'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted',
    'RUNNER_OS':'macOS','GITHUB_REPOSITORY':'wieslawsoltes/ProGPU'}.items())
repo=Path.cwd();build=repo/'artifacts/progpu-native/build'
root=Path(os.environ['RUNNER_TEMP'])/'matched-instruments';root.mkdir(exist_ok=False)
app=build/'progpu_native_webscene_provider_tests'
provider=repo/'artifacts/webscene-provider-src/artifacts/native-gpu-provider-build/osx-arm64/libwebscene_native_gpu.dylib'
lib=build/'libprogpu_native_dawn.dylib'
def git(*args):return subprocess.check_output(['git',*args],cwd=repo)
def sha(p):
    with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
assert git('rev-parse','HEAD').decode().strip()==CANDIDATE
assert all(p.is_file() for p in [app,provider,lib])
production_changes=git('diff','--name-only',BASELINE,CANDIDATE,'--','src/ProGPU.Native/src').decode().splitlines()
assert production_changes==[
    'src/ProGPU.Native/src/Backend/progpu_native_3d_execution.cpp',
    'src/ProGPU.Native/src/Backend/progpu_native_3d_execution.hpp',
    'src/ProGPU.Native/src/Backend/progpu_native_engine.hpp']
inputs=root/'inputs';inputs.mkdir()
shutil.copy2(app,inputs/app.name);app=inputs/app.name
shutil.copy2(provider,inputs/provider.name);provider=inputs/provider.name
variants={label:inputs/label for label in ['baseline','candidate']}
for p in variants.values():p.mkdir()
shutil.copy2(lib,variants['candidate']/lib.name)
original={p:(repo/p).read_bytes() for p in production_changes}
assert all(data==git('show',CANDIDATE+':'+p) for p,data in original.items())
try:
    for p in production_changes:(repo/p).write_bytes(git('show',BASELINE+':'+p))
    assert sorted(git('diff','--name-only','--','src/ProGPU.Native/src').decode().splitlines())==production_changes
    with (root/'baseline-build.log').open('wb') as output:
        subprocess.run(['cmake','--build',str(build),'--target','progpu_native_dawn','--config','Release','--parallel'],stdout=output,stderr=subprocess.STDOUT,check=True,timeout=1200)
    shutil.copy2(lib,variants['baseline']/lib.name)
finally:
    for p,data in original.items():(repo/p).write_bytes(data)
assert not git('diff','--name-only','--','src/ProGPU.Native/src').strip()
inputs_before={str(p.relative_to(root)):sha(p) for p in inputs.rglob('*') if p.is_file()}
receipt={'diagnosticOnly':True,'qualified':False,'candidate':CANDIDATE,'baseline':BASELINE,
         'sourceDifference':production_changes,'inputSha256':inputs_before,
         'configuration':'same Release CMake build, compiler, original test executable and exact WebScene provider; only the three native production files differ',
         'applicationLimitSeconds':60,'outerProfilerLimitSeconds':150,
         'eventPipe':'Not applicable to this native C++ workload: no CLR process is launched. Managed GPU tests remain a separate required producer gate.',
         'results':[]}
shutil.copy2(build/'CMakeCache.txt',root/'CMakeCache.txt')
for name,args in [('system',['sw_vers']),('xcode',['xcodebuild','-version']),('libraries',['otool','-L',str(app)])]:
    result=subprocess.run(args,capture_output=True,text=True,timeout=30)
    (root/(name+'.txt')).write_text(result.stdout+result.stderr)

def bounded(command,log,cwd,env=None,limit=150):
    start=time.monotonic();timed=False
    with log.open('wb') as output:
        proc=subprocess.Popen(command,cwd=cwd,env=env,stdout=output,stderr=subprocess.STDOUT,start_new_session=True)
        try:code=proc.wait(timeout=limit)
        except subprocess.TimeoutExpired:
            timed=True;os.killpg(proc.pid,signal.SIGKILL);code=proc.wait(timeout=5)
    return {'exit':code,'timedOut':timed,'wallSeconds':time.monotonic()-start}

reference=None
def pictures(out):return {p.name:sha(p) for p in sorted(out.glob('*.ppm'))}
def retain(item):
    receipt['results'].append(item)
    receipt['inputsUnchanged']=all(sha(root/p)==s for p,s in inputs_before.items())
    (root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
    print(json.dumps({k:v for k,v in item.items() if k!='pictures'}),flush=True)

# Four predetermined complete controls precede the separate instrumented runs.
for index,label in enumerate(['baseline','candidate','candidate','baseline']):
    out=root/f'control-{index}-{label}';out.mkdir()
    env=os.environ.copy();env['DYLD_LIBRARY_PATH']=str(variants[label])
    result=bounded(['/usr/bin/time','-l',str(app),str(provider),str(out/'provider.ppm')],out/'target.log',out,env,60)
    log=(out/'target.log').read_text(errors='replace');images=pictures(out)
    if reference is None and result['exit']==0 and len(images)==14:reference=images
    rss=re.search(r'(\d+)\s+maximum resident set size',log);footprint=re.search(r'(\d+)\s+peak memory footprint',log)
    item={'kind':'control','index':index,'variant':label,**result,'pictures':images,
          'maximumResidentBytes':int(rss[1]) if rss else None,'peakFootprintBytes':int(footprint[1]) if footprint else None,
          'complete':not result['timedOut'] and result['exit']==0 and len(images)==14 and images==reference and 'submission=11852' in log}
    retain(item)
    if not item['complete']:raise SystemExit('Original control failed; all evidence retained without retries')

exports={'time':[('time-profile',"/trace-toc/run[@number='1']/data/table[@schema='time-profile']")],
 'memory':[('allocations',"/trace-toc/run[@number='1']/tracks/track[@name='Allocations']/details/detail[@name='Statistics']"),
           ('vm-map',"/trace-toc/run[@number='1']/tracks/track[@name='VM Tracker']/details/detail[@name='Regions Map']")],
 'metal':[(s,"/trace-toc/run[@number='1']/data/table[@schema='"+s+"']") for s in [
     'metal-resource-allocations','metal-kernel-resource-allocations','metal-application-command-buffer-submissions','graphics-compiler-activity-intervals']]}
for kind,template,extra in [('time','Time Profiler',[]),('memory','Allocations',['--instrument','VM Tracker']),('metal','Metal System Trace',[])]:
    for index,label in enumerate(['baseline','candidate','candidate','baseline']):
        out=root/f'{kind}-{index}-{label}';out.mkdir()
        command=['xcrun','xctrace','record','--template',template,*extra,'--time-limit','60s','--output',str(out/'capture.trace'),
                 '--target-stdout',str(out/'target.log'),'--env','DYLD_LIBRARY_PATH='+str(variants[label]),
                 '--launch','--',str(app),str(provider),str(out/'provider.ppm')]
        result=bounded(command,out/'profiler.log',out)
        item={'kind':kind,'index':index,'variant':label,**result,'complete':False,'pictures':pictures(out)}
        if result['exit']==0 and not result['timedOut']:
            ex=bounded(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--toc','--output',str(out/'toc.xml')],out/'toc-export.log',out,limit=60)
            item['tocExport']=ex
            if ex['exit']==0 and not ex['timedOut']:
                toc=ET.parse(out/'toc.xml');target=toc.find('./run/info/target/process');summary=toc.find('./run/info/summary')
                item['target']=target.attrib if target is not None else {}
                item['endReason']=summary.findtext('end-reason') if summary is not None else None
                item['traceDuration']=summary.findtext('duration') if summary is not None else None
                log=(out/'target.log').read_text(errors='replace') if (out/'target.log').exists() else ''
                item['complete']=item['target'].get('return-exit-status')=='0' and item['endReason']=='Target app exited' and len(item['pictures'])==14 and item['pictures']==reference and 'submission=11852' in log
                item['tables']={}
                for name,xpath in exports[kind]:
                    item['tables'][name]=bounded(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--xpath',xpath,'--output',str(out/(name+'.xml'))],out/(name+'-export.log'),out,limit=90)
        retain(item)
        if not item['complete']:raise SystemExit('Instrumented corpus incomplete; no retries or qualification claim')
receipt['allMatchedCapturesComplete']=len(receipt['results'])==16 and all(x['complete'] for x in receipt['results']) and receipt['inputsUnchanged']
receipt['analysisRequired']=True
(root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
