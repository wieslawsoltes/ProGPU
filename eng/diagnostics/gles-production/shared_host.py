"""One private corrected-driver comparison, reusing immutable Ubuntu24 binaries."""
from pathlib import Path
import hashlib,json,os,platform,re,signal,subprocess,time,uuid,zipfile
ROOT=Path(__file__).resolve().parent
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def require(ok,why):
    if not ok:raise ValueError(why)
def capture(args,cap=30):return subprocess.check_output(list(map(str,args)),text=True,timeout=cap).strip()
def run(args,log,cap):
    start=time.monotonic()
    with log.open('xb') as stream:
        child=subprocess.Popen(list(map(str,args)),stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
        try:code=child.wait(timeout=cap)
        except BaseException:
            if child.poll() is None:os.killpg(child.pid,signal.SIGKILL)
            child.wait();raise
    return dict(command=list(map(str,args)),exitCode=code,seconds=time.monotonic()-start,logSha256=sha(log))
def stage(archive,payload,pin):
    require(archive.stat().st_size==pin['artifact']['bytes'] and sha(archive)==pin['artifact']['sha256'],'Original artifact changed')
    require(not payload.exists(),'Fresh originalpayload required');payload.mkdir()
    with zipfile.ZipFile(archive) as z:
        result=json.loads(z.read('result.json'));require(result['head']==pin['artifact']['head'],'Original result head mismatch')
        for name,digest in pin['binaryHashes'].items():
            member='disabled/'+name;require(z.namelist().count(member)==1,'Duplicate originalmember')
            raw=z.read(member);require(hashlib.sha256(raw).hexdigest()==digest,'Originalbinaryhash changed')
            target=payload/name;target.write_bytes(raw);target.chmod(0o755 if name=='probe' else 0o644)
def owned(container,label,image_id):
    require(re.fullmatch('[0-9a-f]{64}',container) is not None,'Invalid ownedcontainerID')
    current=json.loads(capture(['docker','inspect',container]))[0]
    require(current['Id']==container and current['Config']['Labels'].get('progpu.diagnostic')==label and current['Image']==image_id,'Ownedcontaineridentity changed')
    return current
def main():
    require(platform.system()=='Linux' and platform.machine()=='x86_64','ActualUbuntu24Linuxx64host required')
    require('VERSION_ID="24.04"' in Path('/etc/os-release').read_text(),'PreserveUbuntu24host ABI')
    require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','Oneowneddisposablehostattempt required')
    head=capture(['git','rev-parse','HEAD']);require(head==os.environ['EXPECTED_HEAD'],'Exact diagnostichead required')
    pin=json.loads((ROOT/'inputs.json').read_text());require(all(sha(ROOT/n)==h for n,h in pin['files'].items()),'Diagnostic inputchanged')
    out=ROOT/'evidence';require(not out.exists(),'Preserveold evidence');out.mkdir();record=dict(head=head,inputs=pin,inputSent=False,qualified=False,containerExecuted=False);container=None;image_id=None;label=uuid.uuid4().hex;image=pin['image'];cleanup=[]
    try:
        archive=out/'original-artifact.zip'
        with archive.open('xb') as stream:result=subprocess.run(['gh','api','repos/wieslawsoltes/ProGPU/actions/artifacts/'+str(pin['artifact']['id'])+'/zip'],stdout=stream,timeout=180)
        require(result.returncode==0,'Originalartifact download failed');payload=out/'payload';stage(archive,payload,pin);record['originalHashes']={n:sha(payload/n) for n in pin['binaryHashes']}
        record['dockerVersion']=capture(['docker','version','--format','{{json .}}'])
        previous=subprocess.run(['docker','image','inspect',image],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=30);record['imagePreexisting']=previous.returncode==0
        record['pull']=run(['docker','pull','--platform','linux/amd64',image],out/'image-pull.log',180);require(record['pull']['exitCode']==0,'Exactimage pull failed')
        image_info=json.loads(capture(['docker','image','inspect',image]))[0];record['imageInspect']=image_info;image_id=image_info['Id']
        require(any(pin['imageDigest'] in x for x in image_info['RepoDigests']) and image_info['Architecture']=='amd64','Actualimage identity mismatch')
        logs=out/'container';logs.mkdir()
        args=['docker','create','--platform','linux/amd64','--label','progpu.diagnostic='+label,'--mount','type=bind,src='+str(ROOT)+',dst=/inputs,readonly','--mount','type=bind,src='+str(payload)+',dst=/payload,readonly','--mount','type=bind,src='+str(logs)+',dst=/evidence','--env','RUNNER_ENVIRONMENT=github-hosted','--env','GITHUB_REPOSITORY=wieslawsoltes/ProGPU',image,'sh','/inputs/bootstrap.sh']
        container=capture(args);record['containerID']=container;record['containerInspect']=owned(container,label,image_id)
        record['containerExecuted']=True;record['containerRun']=run(['docker','start','-a',container],out/'container.log',700)
        require((logs/'inside-result.json').is_file(),'Container did not retain fixture result')
        record['inside']=json.loads((logs/'inside-result.json').read_text());require(record['containerRun']['exitCode']==0 and record['inside'].get('originalPixelControlPassed') is True,'Corrected-driver originalpixelcontrol failed')
        require({n:sha(payload/n) for n in pin['binaryHashes']}==pin['binaryHashes'],'Originalreadonlypayloadchanged')
        record['comparisonPassed']=True
    except BaseException as error:record['error']=repr(error);raise
    finally:
        if container:
            try:owned(container,label,image_id);record['containerRemoval']=capture(['docker','rm','-f',container])
            except BaseException as error:cleanup.append(repr(error))
        if image_id and record.get('imagePreexisting') is False:
            try:
                users=capture(['docker','ps','-aq','--filter','ancestor='+image_id])
                if not users:record['imageRemoval']=capture(['docker','image','rm',image_id])
                else:record['imageRetainedBecauseInUse']=True
            except BaseException as error:cleanup.append(repr(error))
        record['cleanupErrors']=cleanup
        if cleanup:record['comparisonPassed']=False
        (out/'result.json').write_text(json.dumps(record,indent=2)+'\n')
        if cleanup:raise RuntimeError('Owned container cleanup incomplete: '+repr(cleanup))
if __name__=='__main__':main()
