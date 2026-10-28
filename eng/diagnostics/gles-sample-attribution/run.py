from pathlib import Path
import os,platform,subprocess,signal,time,json,hashlib,re
ROOT=Path(__file__).resolve().parent
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def require(ok,why):
    if not ok:raise ValueError(why)
def run(args,log,cap):
    start=time.monotonic()
    with log.open('xb') as stream:
        child=subprocess.Popen(list(map(str,args)),stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=child.wait(timeout=cap)
        except BaseException:
            if child.poll() is None:os.killpg(child.pid,signal.SIGKILL)
            child.wait();raise
    return dict(command=list(map(str,args)),seconds=time.monotonic()-start,exitCode=code,logSha256=sha(log))
def main():
    require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','Owned first-attempt host required')
    require(platform.system()=='Linux' and platform.machine()=='x86_64','Linux x64 only')
    head=subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip();require(head==os.environ['EXPECTED_HEAD'],'Exact head required')
    pin=json.loads((ROOT/'inputs.json').read_text());require(all(sha(ROOT/n)==h for n,h in pin['files'].items()),'Source changed')
    out=ROOT/'evidence';require(not out.exists(),'Preserve old evidence');out.mkdir()
    record=dict(head=head,inputs=pin,inputSent=False,frameworkApplicationExecuted=False,probeExecuted=False,qualified=False)
    try:
        exe=out/'probe';record['compile']=run(['clang-18','-std=c11','-Wall','-Wextra','-Werror',ROOT/'probe.c','-lEGL','-lGLESv2','-lX11','-o',exe],out/'compile.log',60)
        require(record['compile']['exitCode']==0,'Compile failed');record['executableSha256']=sha(exe)
        record['mesa']=subprocess.check_output(['dpkg-query','-W','libgl1-mesa-dri','libegl1','libgles2'],text=True,timeout=15)
        record['probeExecuted']=True;record['run']=run(['xvfb-run','-a',exe,'disable',out],out/'probe.log',60)
        require(record['run']['exitCode'] in (0,1),'Abnormal diagnostic exit')
        text=(out/'probe.log').read_text();record['resolvedCounts']=[list(map(int,p)) for p in re.findall(r'case(\d) samples=4 pixels=192 mismatches=(\d+)',text)]
        record['sampleCounts']=[list(map(int,p)) for p in re.findall(r'samples case=(\d) pixels=768 mismatches=(\d+),(\d+),(\d+),(\d+)',text)]
        require([p[0] for p in record['resolvedCounts']]==[0,1,2] and [p[0] for p in record['sampleCounts']]==[0,1,2],'Complete original cases required')
        require('standalone GLES texture samples=4 pixels=576 mismatches=' in text,'Normal cleanup completion required')
        record['files']={}
        for i in range(3):
            for name,size in [(f'case-{i}-bottom-left.rgba',768),(f'case-{i}-samples-bottom-left.rgba',3072)]:
                f=out/name;require(f.is_file() and f.stat().st_size==size,'Complete source samples and resolved grid required');record['files'][name]={'sha256':sha(f),'bytes':size}
        record['observationCompleted']=True
    except BaseException as error:record['error']=repr(error);raise
    finally:(out/'result.json').write_text(json.dumps(record,indent=2)+'\n')
if __name__=='__main__':main()
