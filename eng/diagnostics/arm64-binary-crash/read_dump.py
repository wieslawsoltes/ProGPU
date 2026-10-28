"""Bounds-checked read-only exception/module inventory; never repairs a dump."""
import json, pathlib, struct, sys

def read(path):
    b=pathlib.Path(path).read_bytes()
    def unpack(fmt, offset):
        size=struct.calcsize(fmt)
        if offset<0 or offset+size>len(b):raise ValueError('Truncated dump field')
        return struct.unpack_from(fmt,b,offset)
    signature,version,count,directory=unpack('<4I',0)
    if signature!=0x504d444d or count>4096:raise ValueError('Invalid dump header')
    streams={};directory_entries=[]
    for i in range(count):
        kind,size,rva=unpack('<3I',directory+i*12)
        directory_entries.append({'type':kind,'size':size,'rva':rva})
        if rva+size>len(b):raise ValueError('Truncated stream')
        # DbgHelp reserves all-zero UnusedStream directory slots. They do not
        # identify a stream and may repeat; required streams remain unique.
        if kind==0:
            if size or rva:raise ValueError('Nonempty unused directory entry')
            continue
        if kind in streams:raise ValueError('Duplicate stream')
        streams[kind]=(rva,size)
    if 6 not in streams or 4 not in streams or 7 not in streams:raise ValueError('Missing exception/module/system stream')
    system,system_size=streams[7]
    if system_size<2:raise ValueError('Short system-info stream')
    architecture=unpack('<H',system)[0]
    if architecture!=12:raise ValueError('Expected ARM64 dump')
    rva,size=streams[6]
    if size<168:raise ValueError('Short exception stream')
    tid=unpack('<I',rva)[0];code,flags,nested,address,nparams,_=unpack('<IIQQII',rva+8)
    if nparams>15:raise ValueError('Invalid exception parameters')
    parameters=list(unpack('<15Q',rva+40)[:nparams])
    mrva,msize=streams[4];n=unpack('<I',mrva)[0]
    if 4+n*108>msize:raise ValueError('Short module list')
    modules=[]
    for i in range(n):
        offset=mrva+4+i*108;base,length,checksum,timestamp,name=unpack('<QIIII',offset)
        nbytes=unpack('<I',name)[0]
        if nbytes%2 or name+4+nbytes>len(b):raise ValueError('Invalid module name')
        text=b[name+4:name+4+nbytes].decode('utf-16-le')
        modules.append({'name':text,'base':hex(base),'size':length,'timestamp':timestamp,'containsExceptionAddress':base<=address<base+length})
    return {'architecture':architecture,'threadId':tid,'exceptionCode':hex(code),'flags':flags,'address':hex(address),'parameters':[hex(x) for x in parameters],'modules':modules,'directoryEntries':directory_entries}

if __name__=='__main__':
    result=read(sys.argv[1]);pathlib.Path(sys.argv[2]).write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps({k:v for k,v in result.items() if k!='modules'}))
