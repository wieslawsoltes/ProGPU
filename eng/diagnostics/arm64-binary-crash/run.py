import hashlib, json, os, pathlib, re, struct, subprocess, sys, zipfile
import xml.etree.ElementTree as ET
from owned_process import run
from read_dump import read

HEAD='686a34bb092ada9eec73b3ce1381dcb592e276b6'
PROCDUMP='3448555503526053bd819c760ad7b40368e130db8348f535f5212e12e9ab2ea5'
WGPU='4c78555a7fa5fbb97d19fa0c7a0aaa97d3d8eebe63de89514e5fd08053c8a6e0'
base=pathlib.Path(__file__).resolve().parent
out=pathlib.Path(os.environ['CAPTURE_OUTPUT']).resolve()
out.mkdir(parents=True,exist_ok=True)

def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
def machine(p):
    b=pathlib.Path(p).read_bytes(); off=struct.unpack_from('<I',b,0x3c)[0]
    if b[off:off+4]!=b'PE\0\0':raise ValueError('Invalid PE')
    return struct.unpack_from('<H',b,off+4)[0]
def save(name,value):(out/name).write_text(json.dumps(value,indent=2)+'\n',encoding='utf-8')

def capture(name,target,args,control=False):
    tools=json.loads((out/'tools.json').read_text(encoding='utf-8-sig'))
    pd=pathlib.Path(tools['procDump']);cdb=pathlib.Path(tools['cdb'])
    assert sha(pd)==PROCDUMP and machine(pd)==0xaa64
    assert sha(cdb)==tools['cdbSha256'] and machine(cdb)==0xaa64
    assert machine(target)==0xaa64
    dest=out/name;dest.mkdir()
    result=run([str(pd),'-accepteula','-g','-e','-mm','-n','1','-x',str(dest),str(target),*args],dest/'capture.log',dest/'capture-process.json',60)
    dumps=list(dest.glob('*.dmp'))
    save(name+'-dump-inventory.json',[{'name':p.name,'size':p.stat().st_size,'sha256':sha(p)} for p in dumps])
    if result['timedOut'] or len(dumps)!=1:raise RuntimeError('One original exception dump required; no retry.')
    exception=read(dumps[0]);save(name+'-exception.json',exception)
    if control and (exception['exceptionCode']!='0xc0000409' or exception['parameters'][:1]!=['0x7']):
        raise RuntimeError('Positive control did not preserve the exact fail-fast subtype.')
    commands='.echo PROGPU_CAPTURE_STACK_BEGIN; .exr -1; .ecxr; .echo PROGPU_SELECTED_STACK_BEGIN; kv 40; .echo PROGPU_SELECTED_STACK_END; ~* k 12; lm; .echo PROGPU_CAPTURE_STACK_END; q'
    analysis=run([str(cdb),'-z',str(dumps[0]),'-y',str(target.parent),'-c',commands],dest/'native-stack.log',dest/'analysis-process.json',60)
    stack=(dest/'native-stack.log').read_text(errors='replace')
    if analysis['timedOut'] or analysis['exitUnsigned']!=0 or 'PROGPU_CAPTURE_STACK_END' not in stack:
        raise RuntimeError('Read-only native stack analysis incomplete; original dump retained.')
    selected=re.search(r'^PROGPU_SELECTED_STACK_BEGIN\r?\n(.*?)^PROGPU_SELECTED_STACK_END\r?$',stack,re.MULTILINE|re.DOTALL)
    if control and (not selected or not re.search(r'CrashControl(?:!main|\+0x)',selected.group(1),re.IGNORECASE)):
        raise RuntimeError('Control frame not observed in the native stack.')
    if control:save('control-admitted.json',{'dumpSha256':sha(dumps[0]),'stackSha256':sha(dest/'native-stack.log'),'code':exception['exceptionCode'],'parameter0':exception['parameters'][0],'analysisComplete':True})
    return exception

mode=sys.argv[1]
if mode=='control':
    capture('control',out/'CrashControl.exe',[],True)
elif mode=='prepare':
    product=pathlib.Path(os.environ['PRODUCT_ROOT']).resolve()
    actual=subprocess.check_output(['git','-C',str(product),'rev-parse','HEAD'],text=True).strip()
    assert actual==HEAD
    feed=pathlib.Path(os.environ['PACKAGE_SOURCE']).resolve();packages=[];versions=set()
    for p in sorted(feed.glob('*.nupkg')):
        with zipfile.ZipFile(p) as z:
            specs=[n for n in z.namelist() if n.endswith('.nuspec')];assert len(specs)==1
            root=ET.fromstring(z.read(specs[0]));nodes={n.tag.split('}')[-1]:n for n in root.iter()}
            identity=nodes['id'].text;version=nodes['version'].text
            if identity.startswith('ProGPU.'):
                assert nodes['repository'].get('commit')==HEAD
                versions.add(version)
            packages.append({'id':identity,'version':version,'name':p.name,'sha256':sha(p)})
    assert len(versions)==1 and any(p['id']=='ProGPU.Backend.Dx12' for p in packages)
    save('package-inputs.json',{'head':actual,'packages':packages,'qualification':False})
    (out/'package-version.txt').write_text(next(iter(versions)),encoding='utf-8')
elif mode=='workload':
    product=pathlib.Path(os.environ['PRODUCT_ROOT']).resolve()
    target=product/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64/ProGPU.Native.PackageConsumer.exe'
    assert sha(target.parent/'wgpu_native.dll')==WGPU
    inventory=[{'path':str(p.relative_to(target.parent)),'size':p.stat().st_size,'sha256':sha(p)} for p in sorted(target.parent.rglob('*')) if p.is_file()]
    save('actual-product-files.json',inventory)
    assert os.environ['PROGPU_DX12_SHADER_COMPILER']=='dxc'
    assert os.environ['PROGPU_HIT_TEST_EXECUTION']=='ordered-stages'
    # Admission requires an actual same-host captured CPU failure, before product execution.
    control=json.loads((out/'control-exception.json').read_text())
    assert control['exceptionCode']=='0xc0000409' and control['parameters'][0]=='0x7'
    admitted=json.loads((out/'control-admitted.json').read_text())
    assert admitted['analysisComplete'] and admitted['parameter0']=='0x7'
    assert admitted['stackSha256']==sha(out/'control/native-stack.log')
    assert admitted['dumpSha256']==sha(next((out/'control').glob('*.dmp')))
    capture('original',target,['--binary-glyph-originals-only','--software-adapter'])
else:raise ValueError(mode)
