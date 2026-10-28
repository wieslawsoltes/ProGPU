"""Diagnostic-only CDB observation; native fault callbacks resume not handled."""
import hashlib, json, os, pathlib, re, struct, sys
from owned_process import run
from read_dump import read
from cdb_event_scripts import prepare, validate_filter_inventory

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
name='cdb-first-chance-control' if control else 'cdb-first-chance-candidate'
dest=out/name;dest.mkdir()
if control:
    target=out/'FirstChanceControl.exe';args=[]
else:
    admission=json.loads((out/'cdb-first-chance-admitted.json').read_text())
    assert admission['handledContinuationObserved'] and admission['allAnalysisComplete']
    for f in admission['evidence']:
        assert sha(out/f['path'])==f['sha256']
    assert os.environ['PROGPU_DX12_SHADER_COMPILER']=='dxc' and os.environ['PROGPU_HIT_TEST_EXECUTION']=='ordered-stages'
    execution=pathlib.Path(os.environ['EXECUTION']).resolve()
    expected=json.loads((pathlib.Path(__file__).parent/'failed-private-closure.json').read_text())
    actual={str(p.relative_to(execution)).replace('\\','/'):{'sha256':sha(p),'size':p.stat().st_size} for p in execution.rglob('*') if p.is_file()}
    assert not any(p.is_symlink() for p in execution.rglob('*'))
    save(out/'captured-private-closure.json',actual)
    assert actual==expected, 'Changed failed private closure; do not execute.'
    warp=pathlib.Path(os.environ['WINDIR'])/'System32/d3d10warp.dll'
    warp_hash=sha(warp);save(out/'actual-warp-before.json',{'path':str(warp),'sha256':warp_hash})
    assert warp_hash=='a2a4ca2622020a8fc2d9bc3a6e854619dc9ad4332d1d09b8cf6bfc0f84090077', 'WARP differs from the actual failed candidate.'
    target=execution/'ProGPU.Native.PackageConsumer.exe'
    assert sha(target.parent/'wgpu_native.dll')=='4c78555a7fa5fbb97d19fa0c7a0aaa97d3d8eebe63de89514e5fd08053c8a6e0'
    args=['--binary-glyph-originals-only','--software-adapter']
assert arm64(target)
# Setup occurs at the initial loader breakpoint; its normal continuation is g.
# Subsequent native faults use first/second chance scripts ending in gN.
setup=prepare(dest)
argv=[str(cdb),'-hd','-noshell','-cf',str(setup),'-y',str(target.parent),str(target),*args]
process=run(argv,dest/'capture.log',dest/'capture-process.json',60)
dumps=sorted(dest.glob('event-*.dmp'))
inventory=[{'name':p.name,'size':p.stat().st_size,'mtimeNs':p.stat().st_mtime_ns,'sha256':sha(p)} for p in dumps]
save(dest/'dump-inventory.json',inventory)
if not 1<=len(dumps)<=8:raise RuntimeError('No bounded native exception evidence; no retry.')
capture_text=(dest/'capture.log').read_text(errors='replace')
events=[{'index':int(i),'chance':int(c),'code':'0x'+code.lower()} for i,c,code in re.findall(r'^PROGPU_NATIVE_EVENT (\d+) ([12]) ([0-9a-fA-F]{8})\r?$',capture_text,re.MULTILINE)]
exit_observed=re.search(r'^PROGPU_TARGET_EXIT\r?$',capture_text,re.MULTILINE) is not None
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
    record={'index':i,'dump':dump.name,'chance':events[i]['chance'] if i<len(events) else None,'code':exception['exceptionCode'],'flags':exception['flags'],'parameters':exception['parameters'],'address':exception['address'],'analysisComplete':bool(complete),'controlFrame':bool(selected and re.search(r'FirstChanceControl(?:!main|\+0x)',selected.group(1),re.IGNORECASE))}
    records.append(record)
    for p in (dump,log):evidence.append({'path':str(p.relative_to(out)),'sha256':sha(p)})
raw=(dest/'capture.log').read_bytes();marker='PROGPU_FIRST_CHANCE_HANDLED_NATIVE'
continued=marker.encode() in raw or marker.encode('utf-16-le') in raw
summary={'process':process,'records':records,'countLimit':8,'countLimitReached':len(dumps)==8,'handledContinuationObserved':continued,'allAnalysisComplete':bool(analyses_complete),'qualification':False,'events':events,'targetExitObserved':exit_observed}
save(dest/'summary.json',summary)
if process['timedOut'] or len(dumps)==8:raise RuntimeError('Diagnostic bound reached; evidence retained, no retry.')
validate_filter_inventory(capture_text)
if not exit_observed or len(events)!=len(dumps) or [e['index'] for e in events]!=list(range(len(dumps))):raise RuntimeError('Native event/exit sequence incomplete; no product admission.')
if any(int(e['code'],16)!=int(r['code'],16) for e,r in zip(events,records)):raise RuntimeError('Native event/dump identity mismatch.')
if not analyses_complete:raise RuntimeError('Native exception analysis incomplete; original dumps retained.')
if control:
    codes=[r['code'] for r in records]
    assert codes[0]=='0xe0421234' and not (records[0]['flags'] & 1) and records[0]['chance']==1 and continued
    assert any(r['code']=='0xc0000409' and r['parameters'][:1]==['0x7'] and r['controlFrame'] and r['chance']==2 for r in records)
    assert records[-1]['chance']==2 and re.search(r'PROGPU_TARGET_EXIT\r?\n[\s\S]*?code:?\s*(?:0x)?c0000409',capture_text,re.IGNORECASE), 'Final exception did not terminate the control with its original exit code.'
    assert len(records)==2, 'Control must have exactly one handled first chance and one final unhandled event.'
    for p in (dest/'capture.log',dest/'capture-process.json',dest/'summary.json',setup,dest/'event-chance-1.cmd',dest/'event-chance-2.cmd'):
        evidence.append({'path':str(p.relative_to(out)),'sha256':sha(p)})
    save(out/'cdb-first-chance-admitted.json',{'handledContinuationObserved':True,'allAnalysisComplete':True,'evidence':evidence})
else:
    assert records[-1]['code']=='0xc0000409' and records[-1]['chance']==2, 'Observed candidate fail-fast outcome is not final; diagnostic incomplete.'
    assert re.search(r'PROGPU_TARGET_EXIT\r?\n[\s\S]*?code:?\s*(?:0x)?c0000409',capture_text,re.IGNORECASE), 'Observed candidate native exit code not retained; diagnostic incomplete.'
