"""Read only ARM64 exception context and its actually retained memory range."""
import hashlib,json,pathlib,struct,sys
from read_dump import read
p=pathlib.Path(sys.argv[1]);prefix=pathlib.Path(sys.argv[2]);b=p.read_bytes();meta=read(p)
def unpack(fmt,off):
 size=struct.calcsize(fmt)
 if off<0 or off+size>len(b):raise ValueError('Truncated minidump field')
 return struct.unpack_from(fmt,b,off)
streams={v['type']:v for v in meta['directoryEntries'] if v['type']}
e=streams[6]['rva'];size,context=unpack('<II',e+160)
assert size>=272 and context+size<=len(b)
x=unpack('<31Q',context+8);sp,pc=unpack('<QQ',context+256);assert pc==int(meta['address'],16)
m=streams[5];count=unpack('<I',m['rva'])[0];assert 4+count*16<=m['size'];found=[]
for i in range(count):
 start,length,offset=unpack('<QII',m['rva']+4+16*i)
 assert offset+length<=len(b)
 if start<=pc<start+length:found.append((start,length,offset))
assert len(found)==1
start,length,offset=found[0];assert 0<length<=1024*1024 and start%4==0 and length%4==0
code=b[offset:offset+length];prefix.with_suffix('.bin').write_bytes(code)
r={'dump':str(p),'dumpSha256':hashlib.sha256(b).hexdigest(),'exception':meta['exceptionCode'],'parameters':meta['parameters'],'contextRva':context,'contextBytes':size,'registers':{**{f'x{i}':hex(v) for i,v in enumerate(x)},'sp':hex(sp),'pc':hex(pc)},'fpMinusSp':hex(x[29]-sp),'codeStart':hex(start),'codeBytes':length,'faultOffset':pc-start,'codeSha256':hashlib.sha256(code).hexdigest(),'nearFault':[{'address':hex(a),'word':hex(unpack('<I',offset+a-start)[0])} for a in range(max(start,pc-24),min(start+length,pc+16),4)]}
prefix.with_suffix('.json').write_text(json.dumps(r,indent=2)+'\n')
prefix.with_suffix('.s').write_text('.text\n.p2align 2\n.globl _captured_jit\n_captured_jit:\n'+''.join('.inst 0x%08x\n'%w[0] for w in struct.iter_unpack('<I',code)))
print(json.dumps({k:r[k] for k in ['exception','parameters','fpMinusSp','codeStart','codeBytes','faultOffset','nearFault']},indent=2))
