"""Diagnostic-only first-chance observation; never supplies an exception response."""
import hashlib, json, os, pathlib, re, struct, sys
from owned_process import run
from read_dump import read

out=pathlib.Path(os.environ['CAPTURE_OUTPUT']).resolve()
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
def save(p,obj):pathlib.Path(p).write_text(json.dumps(obj,indent=2)+'\n',encoding='utf-8')
def arm64(p):
    b=pathlib.Path(p).read_bytes();pe=struct.unpack_from('<I',b,0x3c)[0]
    return b[pe:pe+4]==b'PE\0\0' and struct.unpack_from('<H',b,pe+4)[0]==0xaa64

mode=sys.argv[1];control=mode=='control'
if mode not in ('control','workload'):raise ValueError(mode)
tools=json.loads((out/'tools.json').read_text(encoding='utf-8-sig'))
pd=pathlib.Path(tools['procDump']);cdb=pathlib.Path(tools['cdb'])
assert sha(pd)=='3448555503526053bd819c760ad7b40368e130db8348f535f5212e12e9ab2ea5' and arm64(pd)
assert sha(cdb)==tools['cdbSha256'] and arm64(cdb)
name='first-chance-control' if control else 'first-chance-original'
dest=out/name;dest.mkdir()
if control:
    target=out/'FirstChanceControl.exe';args=[]
else:
    admission=json.loads((out/'first-chance-admitted.json').read_text())
    assert admission['handledContinuationObserved'] and admission['allAnalysisComplete']
    for f in admission['evidence']:
        assert sha(out/f['path'])==f['sha256']
    assert os.environ['PROGPU_DX12_SHADER_COMPILER']=='dxc' and os.environ['PROGPU_HIT_TEST_EXECUTION']=='ordered-stages'
    product=pathlib.Path(os.environ['PRODUCT_ROOT']).resolve()
    target=product/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64/ProGPU.Native.PackageConsumer.exe'
    assert sha(target.parent/'wgpu_native.dll')=='4c78555a7fa5fbb97d19fa0c7a0aaa97d3d8eebe63de89514e5fd08053c8a6e0'
    save(out/'first-chance-product-files.json',[{'path':str(p.relative_to(target.parent)),'size':p.stat().st_size,'sha256':sha(p)} for p in sorted(target.parent.rglob('*')) if p.is_file()])
    args=['--binary-glyph-originals-only','--software-adapter']
assert arm64(target)
# No -b: ProcDump's original initial-breakpoint handling is unchanged. Exclude
# only CLR's managed exception code; ExecuteJIT's SEH filter is not code-specific.
argv=[str(pd),'-accepteula','-g','-e','1','-fx','E0434352','-mm','-n','8','-x',str(dest),str(target),*args]
process=run(argv,dest/'capture.log',dest/'capture-process.json',60)
dumps=sorted(dest.glob('*.dmp'),key=lambda p:(p.stat().st_mtime_ns,p.name))
inventory=[{'name':p.name,'size':p.stat().st_size,'mtimeNs':p.stat().st_mtime_ns,'sha256':sha(p)} for p in dumps]
save(dest/'dump-inventory.json',inventory)
if not 1<=len(dumps)<=8:raise RuntimeError('No bounded native exception evidence; no retry.')
records=[];evidence=[];analyses_complete=True
for i,dump in enumerate(dumps):
    exception=read(dump);record=dest/f'exception-{i:02}.json';save(record,exception)
    commands='.exr -1; .ecxr; .echo PROGPU_SELECTED_STACK_BEGIN; kv 40; .echo PROGPU_SELECTED_STACK_END; ~* k 12; lm; .echo PROGPU_ANALYSIS_COMPLETE; q'
    log=dest/f'native-stack-{i:02}.log'
    analysis=run([str(cdb),'-z',str(dump),'-y',str(target.parent),'-c',commands],log,dest/f'analysis-{i:02}.json',60)
    text=log.read_text(errors='replace')
    complete=not analysis['timedOut'] and analysis['exitUnsigned']==0 and re.search(r'^PROGPU_ANALYSIS_COMPLETE\r?$',text,re.MULTILINE) is not None
    analyses_complete=analyses_complete and complete
    selected=re.search(r'^PROGPU_SELECTED_STACK_BEGIN\r?\n(.*?)^PROGPU_SELECTED_STACK_END\r?$',text,re.MULTILINE|re.DOTALL)
    record={'index':i,'dump':dump.name,'code':exception['exceptionCode'],'flags':exception['flags'],'parameters':exception['parameters'],'address':exception['address'],'analysisComplete':bool(complete),'controlFrame':bool(selected and re.search(r'FirstChanceControl(?:!main|\+0x)',selected.group(1),re.IGNORECASE))}
    records.append(record)
    for p in (dump,log):evidence.append({'path':str(p.relative_to(out)),'sha256':sha(p)})
raw=(dest/'capture.log').read_bytes();marker='PROGPU_FIRST_CHANCE_HANDLED_NATIVE'
continued=marker.encode() in raw or marker.encode('utf-16-le') in raw
summary={'process':process,'records':records,'countLimit':8,'countLimitReached':len(dumps)==8,'handledContinuationObserved':continued,'allAnalysisComplete':bool(analyses_complete),'qualification':False}
save(dest/'summary.json',summary)
if process['timedOut'] or len(dumps)==8:raise RuntimeError('Diagnostic bound reached; evidence retained, no retry.')
if not analyses_complete:raise RuntimeError('Native exception analysis incomplete; original dumps retained.')
if control:
    codes=[r['code'] for r in records]
    assert codes[0]=='0xe0421234' and records[0]['flags']==0 and continued
    assert any(r['code']=='0xc0000409' and r['parameters'][:1]==['0x7'] and r['controlFrame'] for r in records)
    for p in (dest/'capture.log',dest/'capture-process.json',dest/'summary.json'):
        evidence.append({'path':str(p.relative_to(out)),'sha256':sha(p)})
    save(out/'first-chance-admitted.json',{'handledContinuationObserved':True,'allAnalysisComplete':True,'evidence':evidence})
else:
    assert any(r['code']=='0xc0000409' and r['parameters'][:1]==['0x49'] for r in records),'Original PAC outcome not observed; no cause or qualification inferred.'
