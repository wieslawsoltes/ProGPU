"""Single-attempt diagnostics; never changes product gates or retries workloads."""
from pathlib import Path
import hashlib, json, os, shutil, subprocess, sys, time
import xml.etree.ElementTree as ET

PRODUCT='45981bf7619d2e809f08ceda491fad2be535d3b0'
ROOT=Path.cwd()
SOURCE=ROOT/'eng/diagnostics/finalizer-371'
OUT=ROOT/'artifacts/finalizer-371'
TEMP=Path(os.environ['RUNNER_TEMP'])/'finalizer-371-owned-diagnostic'
CONFIG={'DOTNET_EnableEventPipe':'1','DOTNET_EventPipeConfig':'Microsoft-Windows-DotNETRuntime:40004018:5','DOTNET_EventPipeCircularMB':'40','DOTNET_EventPipeOutputStreaming':'1'}

def save(path, value):
    with path.open('x') as stream: json.dump(value,stream,indent=2)

def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as stream:
        for chunk in iter(lambda: stream.read(1024*1024),b''): h.update(chunk)
    return h.hexdigest()

def run(command, name, seconds, env=None):
    started=time.monotonic()
    receipt={'command':command,'attempts':1,'diagnosticOnly':True,'qualification':False,'deadlineSeconds':seconds}
    with (OUT/(name+'.log')).open('xb') as log:
        try:
            result=subprocess.run(command,stdout=log,stderr=subprocess.STDOUT,env=env,timeout=seconds,check=False)
            receipt.update(exitCode=result.returncode,timedOut=False)
        except subprocess.TimeoutExpired:
            receipt.update(exitCode=None,timedOut=True)
        finally:
            receipt['elapsedSeconds']=time.monotonic()-started
            save(OUT/(name+'-execution.json'),receipt)
    print((OUT/(name+'.log')).read_text(errors='replace'))
    if receipt['timedOut'] or receipt['exitCode']!=0: raise RuntimeError(name+' failed; no retry')

mode=sys.argv[1]
if mode=='identity':
    OUT.mkdir(parents=True,exist_ok=False)
    event=json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    actual=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip()
    parent=subprocess.check_output(['git','rev-parse','HEAD^'],text=True).strip()
    delta=subprocess.check_output(['git','diff','--name-only',PRODUCT,'HEAD'],text=True).splitlines()
    receipt={'actualHead':actual,'eventHead':event['pull_request']['head']['sha'],'eventMergeSha':os.environ['GITHUB_SHA'],'productHead':PRODUCT,'parent':parent,'changedFiles':delta,'runId':os.environ['GITHUB_RUN_ID'],'runAttempt':os.environ['GITHUB_RUN_ATTEMPT'],'diagnosticOnly':True,'qualification':False,'xlibArtifact':{'run':40652693827,'id':12799723111,'sha256':'187b11bd7219624f828db9d13b7052ae0c445e89c1c603b9fad11319a7074c7a'}}
    save(OUT/'identity.json',receipt)
    assert actual==receipt['eventHead'] and parent==PRODUCT
    assert os.environ['GITHUB_RUN_ATTEMPT']=='1', 'Reruns are not authorized'
    assert delta and all(p=='.github/workflows/diagnostic-finalizer-371.yml' or p.startswith('eng/diagnostics/finalizer-371/') for p in delta)
    assert not subprocess.check_output(['git','status','--porcelain'],text=True).strip()
elif mode=='control':
    TEMP.mkdir(exist_ok=False)
    shutil.copy2(ROOT/'global.json',TEMP/'global.json')
    for project in ('control','reader','suite-reader'):
        shutil.copytree(SOURCE/project,TEMP/project)
        run(['dotnet','build',str(TEMP/project/'Probe.csproj'),'-c','Release'],project+'-build',120)
    run(['dotnet','--info'],'runtime',30)
    control_env=os.environ.copy()
    control_env.update(CONFIG,DOTNET_EventPipeOutputPath=str(OUT/'control.nettrace'))
    run(['dotnet',str(TEMP/'control/bin/Release/net10.0/Probe.dll'),str(OUT/'control.json')],'control',15,control_env)
    run(['dotnet',str(TEMP/'reader/bin/Release/net10.0/Probe.dll'),str(OUT/'control.nettrace'),str(OUT/'control.json'),str(OUT/'control-decoded.json')],'control-decode',30)
    decoded=json.loads((OUT/'control-decoded.json').read_text())
    assert decoded['EventsLost']==0 and decoded['startsBeforeUnlock']>0 and decoded['stops']>0
    settings=ET.Element('RunSettings')
    variables=ET.SubElement(ET.SubElement(settings,'RunConfiguration'),'EnvironmentVariables')
    values=dict(CONFIG,DOTNET_EventPipeOutputPath=str(OUT/'core-{pid}.nettrace'))
    for key,value in values.items(): ET.SubElement(variables,key).text=value
    ET.ElementTree(settings).write(OUT/'core.runsettings',encoding='utf-8',xml_declaration=True)
    save(OUT/'control-admission.json',{'passed':True,'traceConfiguration':values,'bufferMiB':64,'bufferIsNotFileLimit':True,'originalGateSeconds':5,'noSamplingOrAllocationEvents':True,'traceSha256':digest(OUT/'control.nettrace'),'diagnosticOnly':True,'qualification':False})
    save(OUT/'product-binaries.json',{str(p.relative_to(ROOT)):digest(p) for p in (ROOT/'src/ProGPU.Tests/bin/Release/net10.0/linux-x64').glob('ProGPU*.dll')})
elif mode=='decode':
    if not OUT.exists(): raise RuntimeError('No original identity receipt')
    traces=sorted(OUT.glob('core-*.nettrace'))
    results=[]
    for i,trace in enumerate(traces):
        try:
            run(['dotnet',str(TEMP/'suite-reader/bin/Release/net10.0/Probe.dll'),str(trace),str(OUT/(trace.stem+'-decoded.json'))],'suite-decode-'+str(i),120)
            results.append({'trace':trace.name,'decoded':True})
        except Exception as error:
            results.append({'trace':trace.name,'decoded':False,'error':str(error)})
    save(OUT/'decoding.json',{'diagnosticOnly':True,'qualification':False,'traceCount':len(traces),'results':results,'scope':'Managed Monitor contention only. A pass does not attribute historical failures; no run is repeated.'})
    # ETLX files are reproducible conversion scratch. Original traces, logs and
    # decoder failures remain intact; only successful conversion scratch is removed.
    for item in results:
        if item['decoded']:
            for scratch in OUT.glob(Path(item['trace']).stem+'-decoded.json.etlx*'): scratch.unlink()
    if (OUT/'control-decoded.json').exists():
        for scratch in OUT.glob('control-decoded.json.etlx*'): scratch.unlink()
    save(OUT/'hashes.json',{p.name:{'bytes':p.stat().st_size,'sha256':digest(p)} for p in OUT.iterdir() if p.is_file()})
    if not traces or any(not item['decoded'] for item in results): raise RuntimeError('Missing or undecodable original traces; preserved without retry')
else: raise ValueError(mode)
