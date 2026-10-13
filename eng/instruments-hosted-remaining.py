"""Missing matched Instruments captures on fresh hosted runners; original limits retained."""
import hashlib,json,os,re,signal,subprocess,time,zipfile
import xml.etree.ElementTree as ET
from pathlib import Path
assert all(os.environ.get(k)==v for k,v in {'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','RUNNER_OS':'macOS','GITHUB_REPOSITORY':'wieslawsoltes/ProGPU'}.items())
kind=os.environ['CAPTURE_KIND'];order=os.environ['CAPTURE_ORDER'];assert kind in ('memory','metal') and order in ('AB','BA')
root=Path(os.environ['RUNNER_TEMP'])/'remaining-instruments';root.mkdir(exist_ok=False)
def gh(*args):return json.loads(subprocess.check_output(['gh',*args],timeout=120))
def sha(p):
 with p.open('rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()
producer=gh('run','view','38323191922','--repo','wieslawsoltes/ProGPU','--json','headSha,status,conclusion,jobs')
assert producer['headSha']=='8bfe5b885ae9a8dcceccdccb19bb9ebeacd487fb' and producer['status']=='completed' and producer['conclusion']=='success'
assert len(producer['jobs'])==59 and all(j['status']=='completed' and j['conclusion']=='success' for j in producer['jobs'])
(root/'whole-producer.json').write_text(json.dumps(producer,indent=2)+'\n')
artifact=gh('api','repos/wieslawsoltes/ProGPU/actions/artifacts/11785614402')
assert artifact['workflow_run']['id']==38336442559 and artifact['workflow_run']['head_sha']=='5cdbf72e9c995f0d9c91305e861562a9a6ab989b'
assert artifact['name']=='matched-instruments-inputs-38336442559' and not artifact['expired']
assert artifact['digest']=='sha256:a10f128d33881c160b3d7c131210f2f0b3b978e045f6f7ca4feb91bbaf1a6f82'
archive=root/'original-inputs.zip'
with archive.open('xb') as f:subprocess.run(['gh','api','repos/wieslawsoltes/ProGPU/actions/artifacts/11785614402/zip'],stdout=f,check=True,timeout=120)
assert archive.stat().st_size==artifact['size_in_bytes'] and sha(archive)==artifact['digest'][7:]
with zipfile.ZipFile(archive) as z:
 assert all(not Path(n).is_absolute() and '..' not in Path(n).parts for n in z.namelist());z.extractall(root/'original')
prior=json.loads((root/'original/receipt.json').read_text())
assert prior['candidate']==producer['headSha'] and prior['baseline']=='9873da2149939b1de80c003b41c06f6c53e477b4' and prior['inputsUnchanged']
assert len(prior['results'])==10 and all(r['complete'] for r in prior['results'][:9]) and not prior['results'][9]['complete']
original=root/'original';assert all(sha(original/p)==s for p,s in prior['inputSha256'].items())
app=original/'inputs/progpu_native_webscene_provider_tests';app.chmod(0o755)
provider=original/'inputs/libwebscene_native_gpu.dylib'
reference=prior['results'][0]['pictures'];assert len(reference)==14
labels=['baseline','candidate'] if order=='AB' else ['candidate','baseline']
receipt={'diagnosticOnly':True,'qualified':False,'kind':kind,'order':order,'applicationLimitSeconds':60,'outerProfilerLimitSeconds':150,
 'originalFailedCapturePreserved':{'run':38336442559,'artifact':11786328203,'reason':'Workload completed all original pixels/counters; Instruments did not finalize its recording within150s.'},
 'inputArtifact':artifact,'results':[],'eventPipe':'Native C++ workload has no CLR; managed producer gates remain separate.'}
def save(item):
 receipt['results'].append(item);receipt['inputsUnchanged']=all(sha(original/p)==s for p,s in prior['inputSha256'].items())
 (root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n');print(json.dumps({k:v for k,v in item.items() if k!='pictures'}),flush=True)
def run(command,out,env=None,limit=150):
 start=time.monotonic();timed=False
 with out.open('wb') as f:
  p=subprocess.Popen(command,stdout=f,stderr=subprocess.STDOUT,env=env,start_new_session=True)
  try:code=p.wait(timeout=limit)
  except subprocess.TimeoutExpired:timed=True;os.killpg(p.pid,signal.SIGKILL);code=p.wait(timeout=5)
 return {'exit':code,'timedOut':timed,'wallSeconds':time.monotonic()-start}
def pictures(out):return {p.name:sha(p) for p in sorted(out.glob('*.ppm'))}
for label in labels:
 out=root/('control-'+label);out.mkdir();env=os.environ.copy();env['DYLD_LIBRARY_PATH']=str(original/'inputs'/label)
 result=run(['/usr/bin/time','-l',str(app),str(provider),str(out/'provider.ppm')],out/'target.log',env,60)
 log=(out/'target.log').read_text(errors='replace');frames=pictures(out)
 rss=re.search(r'(\d+)\s+maximum resident set size',log);foot=re.search(r'(\d+)\s+peak memory footprint',log)
 item={'phase':'control','variant':label,**result,'pictures':frames,'complete':result['exit']==0 and not result['timedOut'] and frames==reference and 'submission=11852' in log,
 'maximumResidentBytes':int(rss[1]) if rss else None,'peakFootprintBytes':int(foot[1]) if foot else None}
 save(item)
 if not item['complete']:raise SystemExit('Original60-second control failed; no retry')
for label in labels:
 out=root/(kind+'-'+label);out.mkdir();template='Allocations' if kind=='memory' else 'Metal System Trace'
 extra=['--instrument','VM Tracker'] if kind=='memory' else []
 result=run(['xcrun','xctrace','record','--template',template,*extra,'--time-limit','60s','--output',str(out/'capture.trace'),
  '--target-stdout',str(out/'target.log'),'--env','DYLD_LIBRARY_PATH='+str(original/'inputs'/label),'--launch','--',str(app),str(provider),str(out/'provider.ppm')],out/'profiler.log')
 item={'phase':kind,'variant':label,**result,'pictures':pictures(out),'complete':False}
 if result['exit']==0 and not result['timedOut']:
  item['tocExport']=run(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--toc','--output',str(out/'toc.xml')],out/'toc-export.log',limit=60)
  if item['tocExport']['exit']==0:
   toc=ET.parse(out/'toc.xml');target=toc.find('./run/info/target/process');summary=toc.find('./run/info/summary')
   item['target']=target.attrib if target is not None else {};item['endReason']=summary.findtext('end-reason') if summary is not None else None
   log=(out/'target.log').read_text(errors='replace') if (out/'target.log').exists() else ''
   item['complete']=item['target'].get('return-exit-status')=='0' and item['endReason']=='Target app exited' and item['pictures']==reference and 'submission=11852' in log
   selectors={'allocations':"/trace-toc/run[@number='1']/tracks/track[@name='Allocations']/details/detail[@name='Statistics']",'vm-map':"/trace-toc/run[@number='1']/tracks/track[@name='VM Tracker']/details/detail[@name='Regions Map']"} if kind=='memory' else {s:"/trace-toc/run[@number='1']/data/table[@schema='"+s+"']" for s in ['metal-current-allocated-size','metal-resource-allocations','metal-kernel-resource-allocations','metal-application-command-buffer-submissions','metal-command-buffer-completed','metal-command-buffer-error','graphics-compiler-activity-intervals']}
   item['tables']={}
   for name,xpath in selectors.items():item['tables'][name]=run(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--xpath',xpath,'--output',str(out/(name+'.xml'))],out/(name+'-export.log'),limit=90)
 save(item)
 if not item['complete']:raise SystemExit('Instrumented capture incomplete; no retry')
receipt['allPlannedCapturesComplete']=len(receipt['results'])==4 and all(x['complete'] for x in receipt['results']) and receipt['inputsUnchanged']
receipt['analysisRequired']=True;(root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
