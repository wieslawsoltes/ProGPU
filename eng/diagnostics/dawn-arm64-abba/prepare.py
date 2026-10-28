"""Admit two exact retained source pairs without rebuilding native code."""
from pathlib import Path
import hashlib,json,os,re,struct,subprocess,sys,zipfile,xml.etree.ElementTree as ET
ROOT=Path(__file__).resolve().parent
PIN=json.loads((ROOT/'pins.json').read_text())
NAMES={'progpu_native.dll','progpu_native_dawn.dll'}
def require(ok,message):
 if not ok:raise ValueError(message)
def sha(p):return hashlib.sha256(Path(p).read_bytes()).hexdigest()
def inventory(root):
 files=list(root.rglob('*'));require(not any(p.is_symlink() for p in files),'No linked payload files')
 return {p.relative_to(root).as_posix():{'sha256':sha(p),'size':p.stat().st_size} for p in files if p.is_file()}
def exports(path):
 text=subprocess.check_output(['dumpbin.exe','/nologo','/exports',str(path)],text=True,timeout=30)
 rows=re.findall(r'^\s+(\d+)\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)\s*$',text,re.M);require(len(rows)>100,'Complete native exports required');return [list(row) for row in rows]
def head(path):return subprocess.check_output(['git','-C',str(path),'rev-parse','HEAD'],text=True,timeout=30).strip()
def expected_variant(label):
 expected={k:dict(v) for k,v in PIN['originalConsumerInventory'].items()}
 for name in NAMES:expected[name]={'sha256':PIN['sources'][label]['files'][name]['sha256'],'size':PIN['sources'][label]['files'][name]['bytes']}
 return expected
def validate_variant(before,after,label):
 require(before==PIN['originalConsumerInventory'],'Original 63-file consumer changed')
 require(after==expected_variant(label),'Exact coherent source pair and all other original bytes required')
 require({n for n in before if before[n]!=after[n]}==NAMES,'Exactly two DLL substitutions required')
def payload(label):
 spec=PIN['sources'][label];root=Path(os.environ['PAYLOAD_'+label]).resolve();metadata=root/'candidate-artifact.json'
 require(sha(metadata)==spec['receiptSha256'],'Original candidate receipt changed');record=json.loads(metadata.read_text())
 require(record['head']==spec['head'] and record['runId']==spec['run'] and record['qualification'] is False,'Original source/run identity required')
 canonical=subprocess.check_output(['git','-C',str(ROOT),'show',spec['head']+':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'],timeout=30)
 require(hashlib.sha256(canonical).hexdigest()==spec['shaderSha256'] and canonical==(ROOT/(label+'.wgsl')).read_bytes(),'Exact canonical source required')
 header=root/'build-evidence/BinaryGlyphRasterizerWgsl.generated.hpp';require(sha(header)==record['generatedHeader']['sha256'],'Actual generated header changed')
 embedded=bytes(int(v,16) for v in re.findall(r'0x([0-9a-fA-F]{2})',header.read_text()));require(embedded in (canonical+b'\0',canonical.replace(b'\n',b'\r\n')+b'\0'),'Original generated header must equal canonical shader plus NUL')
 for name in NAMES:
  path=root/'runtimes/win-arm64/native'/name;data=path.read_bytes();off=struct.unpack_from('<I',data,0x3c)[0]
  require(data[off:off+4]==b'PE\0\0' and struct.unpack_from('<H',data,off+4)[0]==0xaa64,'Original ARM64 native DLL required')
  require(sha(path)==spec['files'][name]['sha256'] and len(data)==spec['files'][name]['bytes'],'Exact native bytes required')
  require(data.count(embedded)==1 and exports(path)==spec['files'][name]['exports'],'Original embedded shader and exports required')
 return root

def main():
 require(head(ROOT)==os.environ['EXPECTED_HEAD'],'Exact diagnostic checkout required')
 manifest=json.loads((ROOT/'source-manifest.json').read_text());require(all(sha(ROOT/n)==h for n,h in manifest.items()),'Diagnostic source bytes changed')
 mode=sys.argv[1]
 if mode=='packages':
  require(head(Path(os.environ['ORIGINAL']))==PIN['originalCommit'],'Immutable original consumer source required')
  feed=Path(os.environ['FEED']);rows=[]
  for path in sorted(feed.glob('*.nupkg')):rows.append({'name':path.name,'sha256':sha(path)})
  require(rows==PIN['originalPackages'],'Exact retained package files required')
 elif mode=='payloads':
  before=inventory(Path(os.environ['ORIGINAL'])/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64');require(before==PIN['originalConsumerInventory'],'Original compiled 63-file consumer required')
  for label in ('A','B'):
   root=payload(label)
   for name in NAMES:require(exports(Path(os.environ['ORIGINAL'])/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64'/name)==PIN['sources'][label]['files'][name]['exports'],'Same native ABI required')
  warp=Path(os.environ['WINDIR'])/'System32/d3d10warp.dll';require(sha(warp)==PIN['warpSha256'],'Original WARP bytes required before any workload')
 else:raise ValueError(mode)
if __name__=='__main__':
 evidence=Path(os.environ['EVIDENCE']);evidence.mkdir(exist_ok=True);mode=sys.argv[1];receipt={'mode':mode,'qualified':False,'admitted':False}
 try:
  main();receipt['admitted']=True
 except BaseException as error:receipt['error']=repr(error);raise
 finally:(evidence/('preparation-'+mode+'.json')).write_text(json.dumps(receipt,indent=2)+'\n')
