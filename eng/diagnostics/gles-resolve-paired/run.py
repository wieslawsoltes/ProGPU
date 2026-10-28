"""One paired, diagnostic-only dependency build; no framework app or native input."""
from pathlib import Path
import hashlib,importlib.util,json,os,platform,re,shutil,signal,subprocess,time,difflib
import prepare
ROOT=Path(__file__).resolve().parent
REPO=ROOT.parents[2]
BASE='1f81f86e6f004bac53e939e18d1a239fac464c49'
def require(ok,message):
    if not ok:raise ValueError(message)
def digest(path):return hashlib.sha256(Path(path).read_bytes()).hexdigest()
def run(command,log,seconds,cwd=None,env=None):
    began=time.monotonic()
    with log.open('xb') as stream:
        child=subprocess.Popen(list(map(str,command)),cwd=cwd,env=env,stdout=stream,stderr=subprocess.STDOUT,start_new_session=True)
        try: code=child.wait(timeout=seconds)
        except BaseException:
            os.killpg(child.pid,signal.SIGKILL);child.wait();raise
    return dict(command=list(map(str,command)),exitCode=code,seconds=time.monotonic()-began,logSha256=digest(log))
def module(path):
    s=importlib.util.spec_from_file_location('producer',path);m=importlib.util.module_from_spec(s);s.loader.exec_module(m);return m
def main():
    require(os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1','Owned first-attempt host only')
    require(platform.system()=='Linux' and platform.machine()=='x86_64','Actual Linux x64 required')
    head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=REPO,text=True).strip()
    require(head==os.environ['EXPECTED_HEAD'],'Exact diagnostic head required')
    manifest=json.loads((ROOT/'inputs.json').read_text())
    require(all(digest(ROOT/name)==value for name,value in manifest['diagnosticFiles'].items()),'Diagnostic bytes changed')
    require(all(digest(REPO/name)==value for name,value in manifest['productFiles'].items()),'Exact product inputs changed')
    evidence=ROOT/'evidence';require(not evidence.exists(),'Preserve prior evidence');evidence.mkdir()
    record=dict(head=head,base=BASE,inputSent=False,frameworkApplicationExecuted=False,qualified=False,variants=[])
    try:
        producer=module(REPO/'eng/build-wgpu-native-linux.py');pin=producer.load_inputs();record['inputs']=pin
        require(producer.capture(['rustc','+'+pin['rustVersion'],'--version']).startswith('rustc '+pin['rustVersion']+' '),'Pinned Rust required')
        require('clang version 18.' in producer.capture(['clang-18','--version']),'Pinned Clang required')
        work=evidence/'owned-source';work.mkdir();native=work/'wgpu-native';wgpu=work/'wgpu'
        producer.checkout('https://github.com/gfx-rs/wgpu-native.git',pin['nativeRevision'],native)
        producer.checkout('https://github.com/gfx-rs/wgpu.git',pin['wgpuRevision'],wgpu)
        producer.run(['git','submodule','update','--init','ffi/webgpu-headers'],cwd=native)
        require(producer.capture(['git','rev-parse','HEAD'],cwd=native/'ffi/webgpu-headers')==pin['headersRevision'],'Header identity changed')
        for directory,name in ((wgpu,'wgpu-xlib-owned.patch'),(native,'wgpu-native-xlib-entry.patch')):
            producer.run(['git','apply','--check',REPO/'eng/wgpu-xlib'/name],cwd=directory)
            producer.run(['git','apply',REPO/'eng/wgpu-xlib'/name],cwd=directory)
        require(digest(native/'Cargo.lock')==pin['lockSha256'],'Exact Cargo lock required')
        config=native/'.cargo/config.toml';config.parent.mkdir(exist_ok=True)
        config.write_text('[patch."https://github.com/gfx-rs/wgpu"]\n'+''.join(f'{crate} = {{ path = {json.dumps(str(wgpu/crate))} }}\n' for crate in ('d3d12','naga','wgpu-core','wgpu-hal','wgpu-types')))
        queue=wgpu/'wgpu-hal/src/gles/queue.rs';original=queue.read_bytes();require(prepare.sha(original)==prepare.QUEUE_SHA,'Reviewed patched queue changed')
        (evidence/'queue-product.rs').write_bytes(original)
        probe=prepare.derive_probe((REPO/'eng/wgpu-xlib/native-resolve-scissor-probe.c').read_bytes());(evidence/'probe.c').write_bytes(probe)
        shutil.copy2(REPO/'eng/wgpu-xlib/native-surface-probe.c',evidence/'native-surface-probe.c')
        env=dict(os.environ,LIBCLANG_PATH='/usr/lib/llvm-18/lib',CARGO_TARGET_DIR=str(work/'target'))
        record['toolchain']={name:producer.capture(command) for name,command in {'rust':['rustc','+'+pin['rustVersion'],'--version'],'clang':['clang-18','--version'],'mesa':['dpkg-query','-W','libgl1-mesa-dri','libegl1','libgles2','libegl-dev','libgles-dev']}.items()}
        for variant in ('disabled','full-scissor'):
            out=evidence/variant;out.mkdir();source=prepare.derive_queue(original,variant);queue.write_bytes(source);(out/'queue.rs').write_bytes(source)
            (out/'queue.diff').write_text(''.join(difflib.unified_diff(original.decode().splitlines(True),source.decode().splitlines(True),fromfile='product',tofile=variant)))
            row={'name':variant,'queueSha256':digest(queue),'applicationExecuted':False};record['variants'].append(row)
            row['build']=run(['cargo','+'+pin['rustVersion'],'build','--release','--locked'],out/'build.log',900,cwd=native,env=env)
            require(row['build']['exitCode']==0,'Diagnostic runtime compile failed')
            require(digest(native/'Cargo.lock')==pin['lockSha256'],'Build changed lock')
            lib=out/'libwgpu_native.so';shutil.copy2(work/'target/release/libwgpu_native.so',lib);producer.elf_machine(lib,'linux-x64');row['runtimeSha256']=digest(lib)
            exe=out/'probe'
            row['compile']=run(['clang-18','-std=c11','-Wall','-Wextra','-Werror',evidence/'probe.c','-I',native/'ffi','-I',native/'ffi/webgpu-headers','-L',out,'-Wl,-rpath,'+str(out),'-lwgpu_native','-lX11','-ldl','-o',exe],out/'compile.log',60)
            require(row['compile']['exitCode']==0,'Exact-header C compile failed');row['executableSha256']=digest(exe)
            row['applicationExecuted']=True
            row['run']=run(['xvfb-run','-a',exe,lib],out/'probe.log',60,env=dict(os.environ,LIBGL_ALWAYS_SOFTWARE='1'))
            text=(out/'probe.log').read_text();require('loaded_runtime='+str(lib.resolve()) in text,'Actual runtime not witnessed')
            pixels=re.findall(r'PIXEL case=(\d) x=(\d+) y=(\d+) rgba=(\d+),(\d+),(\d+),(\d+)',text)
            require(len(pixels)==576 and {tuple(map(int,p[:3])) for p in pixels}=={(i,x,y) for i in range(3) for y in range(12) for x in range(16)},'Complete unique576 original pixels required')
            row['pixels']=[list(map(int,p)) for p in pixels]
            row['cases']=[list(map(int,p)) for p in re.findall(r'case(\d) samples=4 pixels=192 mismatches=(\d+) draw=',text)]
            row['glState']=[s for s in text.splitlines() if s.startswith('GL_STATE ')]
            row['glIdentity']=[s for s in text.splitlines() if s.startswith('GL_IDENTITY ')]
            require(len(row['glIdentity'])==1,'Actual GL identity required')
            require(len(row['glState'])==14 and len(row['cases'])==3,'Complete two draw/twelve resolve observations required')
            require(row['run']['exitCode'] in (0,1),'Unexpected probe failure')
        record['pairCompleted']=True
        standalone=evidence/'standalone';standalone.mkdir();exe=standalone/'probe'
        record['standaloneCompile']=run(['clang-18','-std=c11','-Wall','-Wextra','-Werror',ROOT/'standalone-egl.c','-lEGL','-lGLESv2','-lX11','-o',exe],standalone/'compile.log',60)
        require(record['standaloneCompile']['exitCode']==0,'Standalone exact source compile failed')
        record['standaloneExecutableSha256']=digest(exe);record['standalone']=[]
        for mode in ('disable','full-box'):
            out=standalone/mode;out.mkdir();row={'mode':mode};record['standalone'].append(row)
            row['run']=run(['xvfb-run','-a',exe,mode,out],out/'probe.log',60,env=dict(os.environ,LIBGL_ALWAYS_SOFTWARE='1'))
            require(row['run']['exitCode'] in (0,1),'Standalone abnormal exit')
            text=(out/'probe.log').read_text()
            row['cases']=[list(map(int,p)) for p in re.findall(r'case(\d) samples=4 pixels=192 mismatches=(\d+)',text)]
            require(len(row['cases'])==3 and [p[0] for p in row['cases']]==[0,1,2],'Standalone complete three cases required')
            require('standalone GLES samples=4 pixels=576 mismatches=' in text,'Standalone normal completion required')
            row['rawFiles']={}
            for index in range(3):
                raw=out/f'case-{index}-bottom-left.rgba';require(raw.is_file() and raw.stat().st_size==768,'Complete standalone original raw pixels required')
                row['rawFiles'][raw.name]=digest(raw)
        record['allFourCompleted']=True
    except BaseException as error:
        record['error']=repr(error);raise
    finally:
        (evidence/'result.json').write_text(json.dumps(record,indent=2)+'\n')
if __name__=='__main__':main()
