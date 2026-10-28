"""Bounded process and exact owned-container helpers shared by both CI workflows."""
from pathlib import Path
import hashlib,json,os,re,signal,subprocess,time
DEADLINE=None
def bounded(cap):
    if DEADLINE is None:return cap
    remaining=DEADLINE-time.monotonic()
    if remaining<=0:raise TimeoutError("Owned CI stage deadline expired")
    return min(cap,remaining)

def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()

def require(ok,why):
    if not ok:raise ValueError(why)

def capture(args,cap=30):return subprocess.check_output(list(map(str,args)),text=True,timeout=bounded(cap)).strip()

def owned(container,label,image_id):
    require(re.fullmatch('[0-9a-f]{64}',container) is not None,'Invalid ownedcontainerID')
    current=json.loads(capture(['docker','inspect',container]))[0]
    require(current['Id']==container and current['Config']['Labels'].get('progpu.diagnostic')==label and current['Image']==image_id,'Ownedcontaineridentity changed')
    return current

def run(args,log,cap,env=None,cwd=None):
    start=time.monotonic()
    with log.open('xb') as stream:
        child=subprocess.Popen(list(map(str,args)),cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=child.wait(timeout=bounded(cap))
        except BaseException:
            if child.poll() is None:os.killpg(child.pid,signal.SIGKILL)
            child.wait();raise
    return dict(command=list(map(str,args)),seconds=time.monotonic()-start,exitCode=code,logSha256=sha(log))
