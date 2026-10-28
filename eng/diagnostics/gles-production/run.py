"""Same-run production dependency in an owned corrected-driver container."""
from pathlib import Path
import json,os,platform,subprocess,uuid
from shared_host import sha,require,run,capture,owned
from build_receipt import inventory,machine
ROOT=Path(__file__).resolve().parent
def validate_payload(payload,pin,rid,run_id):
    require(not payload.is_symlink(),'No payload symlink')
    receipt=json.loads((payload/'build-receipt.json').read_text())
    actual=inventory(payload);actual.pop('build-receipt.json')
    require(receipt['files']==actual,'Exact immutable artifact inventory required')
    require(receipt['producerCommit']==pin['producerCommit'] and receipt['rid']==rid and receipt['run']==run_id and receipt['attempt']=='1' and receipt['qualified'] is False,'Exact same-run producer required')
    require(receipt['sourceHashes']==pin['producerSourceHashes'],'Original build and probe sources required')
    native=json.loads((payload/'native/wgpu-native-linux.json').read_text())
    require(native==receipt['producerReceipt'] and native['abiTestsPassed'] is True and native['producerCommit']==pin['producerCommit'] and native['rid']==rid,'Original producer ABI receipt required')
    for n in ['native-surface-probe','native-resolve-scissor-probe','native/libwgpu_native.so']:machine(payload/n,rid)
    for n in ['native-surface-probe','native-resolve-scissor-probe']:(payload/n).chmod(0o755)
    return receipt
def main():
    pin=json.loads((ROOT/'inputs.json').read_text());rid=os.environ['RID'];spec=pin['architectures'][rid]
    require(platform.system()=='Linux' and platform.machine()==spec['architecture'],'Native host architecture required')
    require('VERSION_ID="24.04"' in Path('/etc/os-release').read_text(),'Original Ubuntu24 host required')
    require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','First owned hosted attempt required')
    require(capture(['git','rev-parse','HEAD'])==os.environ['EXPECTED_HEAD'],'Exact diagnostic head required')
    require(all(sha(ROOT/n)==h for n,h in pin['files'].items()),'Diagnostic source changed')
    out=ROOT/'evidence';require(not out.exists(),'Preserve original evidence');out.mkdir()
    record={'rid':rid,'qualified':False,'inputSent':False};cid=None;image_id=None;label=uuid.uuid4().hex;cleanup=[]
    try:
        payload=Path(os.environ['BUILD_PAYLOAD']).resolve();record['producer']=validate_payload(payload,pin,rid,os.environ['GITHUB_RUN_ID'])
        receipt_hash=sha(payload/'build-receipt.json');record['buildReceiptSha256']=receipt_hash
        previous=subprocess.run(['docker','image','inspect',spec['image']],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,timeout=30);record['imagePreexisting']=previous.returncode==0
        record['pull']=run(['docker','pull','--platform',spec['platform'],spec['image']],out/'image-pull.log',180);require(record['pull']['exitCode']==0,'Image pull failed')
        image=json.loads(capture(['docker','image','inspect',spec['image']]))[0];record['imageInspect']=image;image_id=image['Id']
        require(image['Architecture']==spec['dockerArchitecture'] and any(spec['imageDigest'] in d for d in image['RepoDigests']),'Pinned image mismatch')
        logs=out/'container';logs.mkdir()
        args=['docker','create','--platform',spec['platform'],'--label','progpu.diagnostic='+label,'--mount','type=bind,src='+str(ROOT)+',dst=/inputs,readonly','--mount','type=bind,src='+str(payload)+',dst=/payload,readonly','--mount','type=bind,src='+str(logs)+',dst=/evidence','--env','RUNNER_ENVIRONMENT=github-hosted','--env','GITHUB_REPOSITORY=wieslawsoltes/ProGPU','--env','RID='+rid,'--env','BUILD_RECEIPT_SHA256='+receipt_hash,spec['image'],'sh','/inputs/bootstrap.sh']
        cid=capture(args);record['containerID']=cid;record['containerInspect']=owned(cid,label,image_id)
        record['containerRun']=run(['docker','start','-a',cid],out/'container.log',760)
        require((logs/'inside-result.json').is_file(),'Probe result absent');record['inside']=json.loads((logs/'inside-result.json').read_text())
        require(record['containerRun']['exitCode']==0 and record['inside'].get('originalPixelControlPassed') is True,'Original controls failed')
        validate_payload(payload,pin,rid,os.environ['GITHUB_RUN_ID']);record['comparisonPassed']=True
    except BaseException as error:record['error']=repr(error);raise
    finally:
        if cid:
            try:owned(cid,label,image_id);record['containerRemoval']=capture(['docker','rm','-f',cid])
            except BaseException as error:cleanup.append(repr(error))
        if image_id and record.get('imagePreexisting') is False:
            try:
                if not capture(['docker','ps','-aq','--filter','ancestor='+image_id]):record['imageRemoval']=capture(['docker','image','rm',image_id])
                else:record['imageRetainedBecauseInUse']=True
            except BaseException as error:cleanup.append(repr(error))
        record['cleanupErrors']=cleanup
        if cleanup:record['comparisonPassed']=False
        (out/'result.json').write_text(json.dumps(record,indent=2)+'\n')
        if cleanup:raise RuntimeError('Owned cleanup failed: '+repr(cleanup))
if __name__=='__main__':main()
