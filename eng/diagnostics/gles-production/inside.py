"""Original native probes only; shared process and driver-observation policy."""
from pathlib import Path
import json,os,platform,re,subprocess,time
from shared_inside import run,sha,require
INPUT=Path('/inputs');PAYLOAD=Path('/payload');OUT=Path('/evidence')
def parse(text,code,kind):
    require(code==0,'Original '+kind+' control failed')
    if kind=='surface':
        require('PASS: three owned GL contexts, five presentations, instance retirement and surviving peer; no input; pixels unqualified' in text,'Original lifetime completion required')
    else:
        cases=[list(map(int,p)) for p in re.findall(r'case(\d) samples=4 pixels=192 mismatches=(\d+) draw=',text)]
        require(cases==[[0,0],[1,0],[2,0]],'All original 576 pixels must pass')
        require('PASS: three authored MSAA4 passes, complete576pixel comparison' in text,'Original resolve completion required')
        require('loaded_runtime=/payload/native/libwgpu_native.so' in text,'Original owned library path required')
def main():
    pin=json.loads((INPUT/'inputs.json').read_text());rid=os.environ['RID'];spec=pin['architectures'][rid];record={'qualified':False,'inputSent':False,'probes':[]}
    try:
        require(platform.machine()==spec['architecture'] and platform.system()=='Linux','Matching native architecture only')
        require('VERSION_ID="26.04"' in Path('/etc/os-release').read_text(),'Ubuntu26 container required')
        receipt=json.loads((PAYLOAD/'build-receipt.json').read_text());require(receipt['producerCommit']==pin['producerCommit'] and receipt['qualified'] is False,'Exact producer required')
        require(sha(PAYLOAD/'build-receipt.json')==os.environ['BUILD_RECEIPT_SHA256'],'Exact same-run build receipt required')
        for n,v in receipt['files'].items():require(sha(PAYLOAD/n)==v['sha256'],'Original payload changed')
        record['osRelease']=Path('/etc/os-release').read_text();record['packages']=subprocess.check_output(['dpkg-query','-W'],text=True,timeout=15)
        for p in spec['mesaPackages']:
            require(subprocess.check_output(['dpkg-query','-W','-f=${Version}',p['Package']],text=True,timeout=15)==p['Version'],'Installed Mesa version mismatch')
        names=subprocess.check_output(['dpkg-query','-L',*[p['Package'] for p in spec['mesaPackages']]],text=True,timeout=15).splitlines()
        installed={str(Path(n).resolve()):sha(Path(n)) for n in set(names) if Path(n).is_file() and '.so' in Path(n).name};record['installedDriverHashes']=installed
        require(time.time()-float(os.environ['PROGPU_SETUP_STARTED'])<600,'Original setup budget exceeded')
        env=dict(os.environ,LD_LIBRARY_PATH='/payload/native',LIBGL_ALWAYS_SOFTWARE='1')
        for kind,exe in [('surface','native-surface-probe'),('resolve','native-resolve-scissor-probe')]:
            args=['xvfb-run','-a','env','LD_DEBUG=libs',PAYLOAD/exe]
            if kind=='resolve':args.append(PAYLOAD/'native/libwgpu_native.so')
            item={'kind':kind};record['probes'].append(item)
            item['run']=run(args,OUT/(kind+'.log'),60,env=env)
            text=(OUT/(kind+'.log')).read_text()
            loaded=[Path(n.strip()).resolve() for n in re.findall(r'calling init:\s*([^\n]+)',text)]
            require((PAYLOAD/'native/libwgpu_native.so') in loaded,'Actual owned runtime must load')
            drivers=[p for p in loaded if 'libEGL_mesa' in p.name or 'libgallium' in p.name]
            require(any('libEGL_mesa' in p.name for p in drivers) and any('libgallium' in p.name for p in drivers),'Loaded driver witnesses required')
            item['loadedDriverHashes']={str(p):sha(p) for p in drivers}
            require(all(installed.get(n)==h for n,h in item['loadedDriverHashes'].items()),'Loaded driver changed')
            parse(text,item['run']['exitCode'],kind);item['passed']=True
        for n,v in receipt['files'].items():require(sha(PAYLOAD/n)==v['sha256'],'Readonly payload changed after run')
        record['originalPixelControlPassed']=True
    except BaseException as error:record['error']=repr(error);raise
    finally:(OUT/'inside-result.json').write_text(json.dumps(record,indent=2)+'\n')
if __name__=='__main__':main()
