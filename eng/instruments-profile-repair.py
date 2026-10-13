"""Bounded repair of the original profiler finalization failure; no product qualification."""
import hashlib,json,os,re,signal,subprocess,time,zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
assert all(os.environ.get(k)==v for k,v in {'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','RUNNER_OS':'macOS','GITHUB_REPOSITORY':'wieslawsoltes/ProGPU'}.items())
root=Path(os.environ['RUNNER_TEMP'])/'crossing-profile-repair';root.mkdir(exist_ok=False)
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
def artifact(number,name,digest,size):
 a=json.loads(subprocess.check_output(['gh','api',f'repos/wieslawsoltes/ProGPU/actions/artifacts/{number}'],timeout=60))
 assert a['id']==number and a['name']==name and not a['expired']
 assert a['workflow_run']['id']==38352863608 and a['workflow_run']['head_sha']=='045cc2cac3d6eb2639233ce15f001038fc95fb59'
 assert a['digest']=='sha256:'+digest and a['size_in_bytes']==size
 archive=root/(name+'.zip')
 with archive.open('xb') as f:subprocess.run(['gh','api',f'repos/wieslawsoltes/ProGPU/actions/artifacts/{number}/zip'],stdout=f,check=True,timeout=120)
 assert archive.stat().st_size==size and sha(archive)==digest
 return a,archive
def extract(archive,out,predicate=lambda n:True):
 with zipfile.ZipFile(archive) as z:
  assert all(not Path(i.filename).is_absolute() and '..' not in Path(i.filename).parts and (i.external_attr>>16)&0o170000!=0o120000 for i in z.infolist())
  for n in z.namelist():
   if predicate(n):z.extract(n,out)
def bounded(command,log,env=None,limit=150):
 start=time.monotonic();timed=False
 with log.open('wb') as f:
  p=subprocess.Popen(command,stdout=f,stderr=subprocess.STDOUT,env=env,cwd=log.parent,start_new_session=True)
  try:code=p.wait(timeout=limit)
  except subprocess.TimeoutExpired:timed=True;os.killpg(p.pid,signal.SIGKILL);code=p.wait(timeout=5)
 return {'exit':code,'timedOut':timed,'wallSeconds':time.monotonic()-start}
inputs_meta,archive=artifact(11793437099,'matched-instruments-inputs-38352863608','b2ac83644d015b5602c9f1a7588e0294b393b40d5792263b9ca2e8fc8171322e',37180457)
original=root/'original-inputs';extract(archive,original)
prior=json.loads((original/'receipt.json').read_text())
assert prior['candidate']=='4bbd1dc0cca4dfc8b6d3463346e305074a22a4be' and prior['baseline']=='8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb'
assert prior['inputsUnchanged'] and len(prior['results'])==10
assert all(r['complete'] for i,r in enumerate(prior['results']) if i!=4) and prior['results'][4]['timedOut'] and not prior['results'][4]['complete']
assert all(sha(original/p)==s for p,s in prior['inputSha256'].items())
app=original/'inputs/progpu_native_webscene_provider_tests';app.chmod(0o755)
provider=original/'inputs/libwebscene_native_gpu.dylib';reference=prior['results'][0]['pictures'];assert len(reference)==14
receipt={'diagnosticOnly':True,'qualified':False,'candidate':prior['candidate'],'baseline':prior['baseline'],'inputArtifact':inputs_meta,
 'originalFailure':{'run':38352863608,'artifact':11794006443,'reason':'Target completed original pixels/submission; Time Profiler exited only on150-second collector bound; incomplete trace retained.'},
 'applicationLimitSeconds':60,'outerProfilerLimitSeconds':150,'results':[],'existingMetalExports':[],
 'eventPipe':'Native C++ workload has no CLR. Whole managed/native producer and application gates remain independent.'}
def save():
 receipt['inputsUnchanged']=all(sha(original/p)==s for p,s in prior['inputSha256'].items())
 (root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
def append(item):
 receipt['results'].append(item);save();print(json.dumps({k:v for k,v in item.items() if k!='pictures'}),flush=True)
def pictures(out):return {p.name:sha(p) for p in sorted(out.glob('*.ppm'))}
for name,args in [('system',['sw_vers']),('xcode',['xcodebuild','-version']),('libraries',['otool','-L',str(app),str(provider),str(original/'inputs/baseline/libprogpu_native_dawn.dylib'),str(original/'inputs/candidate/libprogpu_native_dawn.dylib')])]:
 p=subprocess.run(args,capture_output=True,text=True,timeout=30);(root/(name+'.txt')).write_text(p.stdout+p.stderr);assert p.returncode==0
# Export missing correlation tables from the original immutable completed Metal traces.
metal_meta,metal_zip=artifact(11794325003,'matched-instruments-metal-38352863608','32dc17d93d88a71e85281f66e4dd1f30d0822e8a6678d873480798b79108f9d9',96914984)
receipt['metalInputArtifact']=metal_meta;metal=root/'original-metal'
extract(metal_zip,metal,lambda n:'.trace/' in n or n.endswith('/toc.xml'))
for index,label in enumerate(['baseline','candidate']):
 trace=metal/f'metal-{index}-{label}'/'capture.trace';out=root/'metal-exports'/label;out.mkdir(parents=True)
 for schema in ['metal-current-allocated-size','metal-command-buffer-completed','metal-command-buffer-error']:
  result=bounded(['xcrun','xctrace','export','--input',str(trace),'--xpath',"/trace-toc/run[@number='1']/data/table[@schema='"+schema+"']",'--output',str(out/(schema+'.xml'))],out/(schema+'.log'),limit=90)
  receipt['existingMetalExports'].append({'variant':label,'schema':schema,**result});save()
# One new original sixty-second control per variant, then one predetermined CPU pair.
controls_ok=True
for label in ['baseline','candidate']:
 out=root/('control-'+label);out.mkdir();env=os.environ.copy();env['DYLD_LIBRARY_PATH']=str(original/'inputs'/label)
 result=bounded(['/usr/bin/time','-l',str(app),str(provider),str(out/'provider.ppm')],out/'target.log',env,60)
 log=(out/'target.log').read_text(errors='replace');frames=pictures(out)
 item={'kind':'control','variant':label,**result,'pictures':frames,'complete':result['exit']==0 and not result['timedOut'] and frames==reference and 'submission=11852' in log}
 for key,pattern in [('maximumResidentBytes',r'(\d+)\s+maximum resident set size'),('peakFootprintBytes',r'(\d+)\s+peak memory footprint')]:
  match=re.search(pattern,log);item[key]=int(match[1]) if match else None
 append(item);controls_ok=controls_ok and item['complete']
 if not item['complete']:break
if controls_ok:
 for label in ['baseline','candidate']:
  out=root/('time-'+label);out.mkdir()
  result=bounded(['xcrun','xctrace','record','--template','Time Profiler','--time-limit','60s','--output',str(out/'capture.trace'),'--target-stdout',str(out/'target.log'),'--env','DYLD_LIBRARY_PATH='+str(original/'inputs'/label),'--launch','--',str(app),str(provider),str(out/'provider.ppm')],out/'profiler.log')
  item={'kind':'time','variant':label,**result,'pictures':pictures(out),'complete':False}
  if result['exit']==0 and not result['timedOut']:
   item['tocExport']=bounded(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--toc','--output',str(out/'toc.xml')],out/'toc-export.log',limit=60)
   if item['tocExport']['exit']==0 and not item['tocExport']['timedOut']:
    toc=ET.parse(out/'toc.xml');target=toc.find('./run/info/target/process');summary=toc.find('./run/info/summary')
    item['target']=target.attrib if target is not None else {};item['endReason']=summary.findtext('end-reason') if summary is not None else None
    item['traceDuration']=summary.findtext('duration') if summary is not None else None
    log=(out/'target.log').read_text(errors='replace')
    item['complete']=item['target'].get('return-exit-status')=='0' and item['endReason']=='Target app exited' and item['pictures']==reference and 'submission=11852' in log
    item['tableExport']=bounded(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--xpath',"/trace-toc/run[@number='1']/data/table[@schema='time-profile']",'--output',str(out/'time-profile.xml')],out/'time-profile-export.log',limit=90)
    item['complete']=item['complete'] and item['tableExport']['exit']==0 and not item['tableExport']['timedOut']
  append(item)
receipt['complete']=len(receipt['results'])==4 and all(r['complete'] for r in receipt['results']) and len(receipt['existingMetalExports'])==6 and all(r['exit']==0 and not r['timedOut'] for r in receipt['existingMetalExports']) and receipt['inputsUnchanged']
save();raise SystemExit(0 if receipt['complete'] else 1)
