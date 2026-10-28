"""Runs only inside the one owned disposable container. No host package changes."""
from pathlib import Path
import hashlib,json,os,platform,re,signal,subprocess,time
INPUT=Path('/inputs');PAYLOAD=Path('/payload');OUT=Path('/evidence')
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def require(ok,why):
    if not ok:raise ValueError(why)
def run(args,log,cap,env=None,cwd=None):
    start=time.monotonic()
    with log.open('xb') as stream:
        child=subprocess.Popen(list(map(str,args)),cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=child.wait(timeout=cap)
        except BaseException:
            if child.poll() is None:os.killpg(child.pid,signal.SIGKILL)
            child.wait();raise
    return dict(command=list(map(str,args)),seconds=time.monotonic()-start,exitCode=code,logSha256=sha(log))
def parse_observation(text,code):
    require(code==0,'Original pixel control failed')
    cases=[list(map(int,p)) for p in re.findall(r'case(\d) samples=4 pixels=192 mismatches=(\d+) draw=',text)]
    require(cases==[[0,0],[1,0],[2,0]],'All original576 pixels must pass')
    pixels=re.findall(r'PIXEL case=(\d) x=(\d+) y=(\d+) rgba=(\d+),(\d+),(\d+),(\d+)',text)
    require(len(pixels)==576 and {tuple(map(int,p[:3])) for p in pixels}=={(i,x,y) for i in range(3) for y in range(12) for x in range(16)},'Complete unique originalpixelinventory required')
    states=[line for line in text.splitlines() if line.startswith('GL_STATE ')];require(len(states)==14,'Original fourteenGLstate records required')
    identity=[line for line in text.splitlines() if line.startswith('GL_IDENTITY ')];require(len(identity)==1 and 'Mesa 26.0.8' in identity[0] and 'llvmpipe' in identity[0],'Actual correctedsoftwaredriver identity required')
    require('loaded_runtime=/payload/libwgpu_native.so' in text,'Actual loaded original library path required')
    require('PASS: three authored MSAA4 passes' in text,'Normal originalcontrol completion required')
    return dict(cases=cases,pixels=[list(map(int,p)) for p in pixels],states=states,identity=identity)
def main():
    pin=json.loads((INPUT/'inputs.json').read_text());record=dict(inputSent=False,frameworkApplicationExecuted=False,probeExecuted=False,qualified=False)
    try:
        require(platform.system()=='Linux' and platform.machine()=='x86_64','Linuxx64 only')
        require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU','Ownedhost identity required')
        for n,h in pin['binaryHashes'].items():require(sha(PAYLOAD/n)==h,'Originalbinary changed:'+n)
        record['osRelease']=Path('/etc/os-release').read_text();require('VERSION_ID="26.04"' in record['osRelease'],'ActualUbuntu26.04 required')
        record['packages']=subprocess.check_output(['dpkg-query','-W'],text=True,timeout=15)
        for p in pin['mesaPackages']:
            actual=subprocess.check_output(['dpkg-query','-W','-f=${Version}',p['Package']],text=True,timeout=15);require(actual==p['Version'],'InstalledMesa version mismatch')
        files=subprocess.check_output(['dpkg-query','-L',*[p['Package'] for p in pin['mesaPackages']]],text=True,timeout=15).splitlines()
        record['installedDriverHashes']={n:sha(Path(n)) for n in sorted(set(files)) if Path(n).is_file() and '.so' in Path(n).name}
        record['binaryHashes']={n:sha(PAYLOAD/n) for n in pin['binaryHashes']};require(record['binaryHashes']==pin['binaryHashes'],'Readonly originalpayload changed')
        env=dict(os.environ,LD_LIBRARY_PATH=str(PAYLOAD),LIBGL_ALWAYS_SOFTWARE='1')
        require(time.time()-float(os.environ['PROGPU_SETUP_STARTED'])<600,'Original600s setup budget exceeded before app launch')
        record['probeExecuted']=True;record['run']=run(['xvfb-run','-a','env','LD_DEBUG=libs',PAYLOAD/'probe',PAYLOAD/'libwgpu_native.so'],OUT/'probe.log',60,env=env)
        text=(OUT/'probe.log').read_text()
        loaded=[Path(p.strip()) for p in re.findall(r'calling init:\s*([^\n]+)',text) if any(n in Path(p.strip()).resolve().name for n in ('libEGL_mesa','libgallium'))]
        require(any('libEGL_mesa' in p.resolve().name for p in loaded) and any('libgallium' in p.resolve().name for p in loaded),'Actual loaded EGL/gallium image witnesses required')
        installed={str(Path(n).resolve()):h for n,h in record['installedDriverHashes'].items()}
        record['loadedDriverHashes']={str(p.resolve()):sha(p.resolve()) for p in loaded}
        require(all(installed.get(p)==h for p,h in record['loadedDriverHashes'].items()),'Actual loaded driver differs from verified installed package')
        record['observation']=parse_observation(text,record['run']['exitCode']);record['originalPixelControlPassed']=True
        require({n:sha(PAYLOAD/n) for n in pin['binaryHashes']}==pin['binaryHashes'],'Originalpayload changedafterrun')
    except BaseException as error:record['error']=repr(error);raise
    finally:(OUT/'inside-result.json').write_text(json.dumps(record,indent=2)+'\n')
if __name__=='__main__':main()
