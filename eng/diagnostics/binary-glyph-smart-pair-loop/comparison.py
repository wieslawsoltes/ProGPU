"""Admit exact retained compiler identities and report compiler IR, not runtime safety."""
import hashlib,re

def require(condition,message):
    if not condition:raise ValueError(message)

def metrics(assembly):
    return dict(textBytes=len(assembly.encode()),lines=len(assembly.splitlines()),
                fxcTemps=re.findall(r'dcl_temps\s+(\d+)',assembly),
                dxilSqrtCalls=len(re.findall(r'call float @dx\.op\.unary\.f32\(i32 24,',assembly)),
                dxilLoopMetadataAttachments=len(re.findall(r'!llvm\.loop ![0-9]+',assembly)),
                fxcSqrtInstructions=len(re.findall(r'^\s*sqrt\b',assembly,re.M)),
                fxcLoopInstructions=len(re.findall(r'^loop\b',assembly,re.M)),
                dxilAllocaInstructions=len(re.findall(r'\balloca\b',assembly)),
                dxilTwoElementAllocas=len(re.findall(r'\balloca\s+\[2 x (?:float|i32)\]',assembly)))

def admit(root,entry,system,compiler,baseline):
    require(entry in ('cs_binary_horizontal','cs_binary_vertical'),'Original entry required')
    selected=baseline['entries'][entry];identity=selected['identity']
    require(system['architecture']=='ARM64' and system['sha256'].lower()==identity['fxc']['sha256'].lower()
            and system['version']==identity['fxc']['version'],'System compiler differs; retained baseline comparison forbidden')
    require(compiler==identity['dxc'],'Pinned DXC package/header/runtime identity differs')
    for kind,record in selected['records'].items():
        source=root/('baseline-'+kind+'.hlsl');assembly=root/record['retainedDisassembly']
        require(hashlib.sha256(source.read_bytes()).hexdigest()==record['sourceSha256'],'Retained baseline generated input differs')
        expected=next(f for f in record['files'] if f['path']=='disassembly.txt')
        require(assembly.stat().st_size==expected['bytes'] and hashlib.sha256(assembly.read_bytes()).hexdigest()==expected['sha256'],'Retained original compiler disassembly differs')
        require(not record['process']['timedOut'] and record['process']['exitUnsigned']==0,'Retained baseline compilation failed')
    return selected
