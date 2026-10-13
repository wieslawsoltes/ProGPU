"""Check hosted Instruments availability; never product qualification."""
import hashlib, json, os, signal, subprocess, time
from pathlib import Path

assert all(os.environ.get(k) == v for k, v in {
    'GITHUB_ACTIONS': 'true', 'RUNNER_ENVIRONMENT': 'github-hosted',
    'RUNNER_OS': 'macOS', 'GITHUB_REPOSITORY': 'wieslawsoltes/ProGPU'}.items())
root = Path(os.environ['RUNNER_TEMP']) / 'instruments-preflight'
root.mkdir(exist_ok=False)
source = root / 'probe.m'
source.write_text('''#import <Foundation/Foundation.h>
#import <Metal/Metal.h>
#include <stdio.h>
#include <unistd.h>
int main(void) { @autoreleasepool {
  id<MTLDevice> d = MTLCreateSystemDefaultDevice();
  if (!d) return 1;
  id<MTLCommandQueue> q = [d newCommandQueue];
  id<MTLBuffer> b = [d newBufferWithLength:1048576 options:MTLResourceStorageModeShared];
  if (!q || !b) return 2;
  for (int i=0; i<30; ++i) {
    id<MTLCommandBuffer> c = [q commandBuffer];
    id<MTLBlitCommandEncoder> e = [c blitCommandEncoder];
    [e fillBuffer:b range:NSMakeRange(0,1048576) value:7]; [e endEncoding];
    [c commit]; [c waitUntilCompleted];
    if (c.status != MTLCommandBufferStatusCompleted) return 3;
    printf("sample=%d currentAllocatedSize=%lu\\n", i, (unsigned long)d.currentAllocatedSize);
    fflush(stdout); usleep(100000);
  }
  puts("preflight-completed");
  return 0;
} }
''')
subprocess.run(['xcrun', 'clang', '-O2', '-fobjc-arc', '-framework', 'Foundation', '-framework', 'Metal', str(source), '-o', str(root/'probe')], check=True)
for name, command in [('security', ['/usr/sbin/DevToolsSecurity', '-status']), ('templates', ['xcrun','xctrace','list','templates']), ('system', ['sw_vers'])]:
    result = subprocess.run(command, capture_output=True, text=True, timeout=30)
    (root/(name+'.txt')).write_text(result.stdout+result.stderr)
results=[]
for label, template, extra in [('time','Time Profiler',[]), ('memory','Allocations',['--instrument','VM Tracker']), ('metal','Metal System Trace',[])]:
    out=root/label;out.mkdir()
    cmd=['xcrun','xctrace','record','--template',template,*extra,'--time-limit','12s','--output',str(out/'capture.trace'),'--target-stdout',str(out/'target.log'),'--launch','--',str(root/'probe')]
    started=time.monotonic();timed=False
    with (out/'profiler.log').open('wb') as log:
        proc=subprocess.Popen(cmd,stdout=log,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=proc.wait(timeout=40)
        except subprocess.TimeoutExpired:
            timed=True;os.killpg(proc.pid,signal.SIGKILL);code=proc.wait(timeout=5)
    item={'kind':label,'exit':code,'timedOut':timed,'seconds':time.monotonic()-started,'targetCompleted':'preflight-completed' in (out/'target.log').read_text() if (out/'target.log').exists() else False}
    if code==0 and not timed:
        exported=subprocess.run(['xcrun','xctrace','export','--input',str(out/'capture.trace'),'--toc','--output',str(out/'toc.xml')],capture_output=True,text=True,timeout=30)
        item['exportExit']=exported.returncode
        (out/'export.log').write_text(exported.stdout+exported.stderr)
    results.append(item);print(json.dumps(item),flush=True)
receipt={'diagnosticOnly':True,'qualified':False,'productLaunched':False,'securitySettingsChanged':False,'workflowCommit':os.environ['GITHUB_SHA'],'results':results}
(root/'receipt.json').write_text(json.dumps(receipt,indent=2)+'\n')
raise SystemExit(0 if all(x['exit']==0 and not x['timedOut'] and x['targetCompleted'] and x.get('exportExit')==0 for x in results) else 1)
