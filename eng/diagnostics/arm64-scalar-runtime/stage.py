"""Private output overlay only; no package/cache mutation or qualification."""
import hashlib,json,os,pathlib,re,shutil,struct,subprocess,sys,xml.etree.ElementTree as ET,zipfile
BASE='686a34bb092ada9eec73b3ce1381dcb592e276b6'
CANDIDATE='1f81f86e6f004bac53e939e18d1a239fac464c49'
SHADER='29dcaa9d94eab309cb7367ee4b5721c011592766b4cbf9dbcd76b62e62d711c7'
NAMES={'progpu_native.dll','progpu_native_dawn.dll'}
out=pathlib.Path(os.environ['EVIDENCE']).resolve();out.mkdir(exist_ok=True,parents=True)
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
def save(n,x):(out/n).write_text(json.dumps(x,indent=2)+'\n',encoding='utf8')
def head(p):return subprocess.check_output(['git','-C',str(p),'rev-parse','HEAD'],text=True).strip()
def inventory(p):
 files=list(p.rglob('*'));assert not any(f.is_symlink() for f in files)
 return {str(f.relative_to(p)).replace('\\','/'):{'sha256':sha(f),'size':f.stat().st_size} for f in files if f.is_file()}
def machine(p):
 b=p.read_bytes();off=struct.unpack_from('<I',b,0x3c)[0];assert b[off:off+4]==b'PE\0\0';return struct.unpack_from('<H',b,off+4)[0]
def exports(p):
 raw=subprocess.check_output(['dumpbin.exe','/nologo','/exports',str(p)],text=True)
 rows=re.findall(r'^\s+(\d+)\s+[0-9A-F]+\s+[0-9A-F]+\s+(\S+)\s*$',raw,re.M)
 assert len(rows)>100
 return rows
def validate_embedded(canonical,original,data):
 assert hashlib.sha256(canonical).hexdigest()==SHADER
 forms=[canonical,canonical.replace(b'\n',b'\r\n')]
 counts=[data.count(form+b'\0') for form in forms];assert sum(counts)==1
 assert not any(form+b'\0' in data for form in (original,original.replace(b'\n',b'\r\n')))
 return counts,hashlib.sha256(forms[counts.index(1)]).hexdigest()
def validate_overlay(before,after):
 assert set(before)==set(after)
 changed={k for k in before if before[k]!=after[k]};assert changed==NAMES
 return changed
mode=sys.argv[1]
if mode=='prepare':
 source=pathlib.Path(os.environ['ORIGINAL']);assert head(source)==BASE
 feed=pathlib.Path(os.environ['FEED']);rows=[]
 assert sum(p.stat().st_size for p in feed.rglob('*') if p.is_file())<512*1024*1024
 for p in sorted(feed.glob('*.nupkg')):
  with zipfile.ZipFile(p) as z:
   specs=[n for n in z.namelist() if n.endswith('.nuspec')];assert len(specs)==1
   nodes={n.tag.split('}')[-1]:n for n in ET.fromstring(z.read(specs[0])).iter()}
   assert nodes['version'].text=='0.1.0-preview.3676.ci' and nodes['repository'].get('commit')==BASE
   rows.append({'name':p.name,'sha256':sha(p)})
 assert len(rows)>3
 save('original-packages.json',rows)
elif mode=='artifact':
 source=pathlib.Path(os.environ['CANDIDATE_ROOT']);assert head(source)==CANDIDATE
 metadata=pathlib.Path(os.environ['PAYLOAD'])/'candidate-artifact.json'
 assert sha(metadata)==os.environ['CANDIDATE_RECEIPT_SHA256']
 meta=json.loads(metadata.read_text())
 assert meta['head']==CANDIDATE and meta['runId']==int(os.environ['GITHUB_RUN_ID']) and meta['dependencyRun']==40720344709 and not meta['qualification']
 assert int(os.environ['CANDIDATE_ARTIFACT_ID'])>0 and len(os.environ['CANDIDATE_ARTIFACT_DIGEST'])==64
 canonical=subprocess.check_output(['git','-C',str(source),'show',CANDIDATE+':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl']);assert hashlib.sha256(canonical).hexdigest()==SHADER
 original=subprocess.check_output(['git','-C',os.environ['ORIGINAL'],'show',BASE+':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'])
 directory=pathlib.Path(os.environ['PAYLOAD'])/'runtimes/win-arm64/native';files={}
 for name in sorted(NAMES):
  p=directory/name;assert machine(p)==0xaa64 and sha(p)==meta['dllSha256'][name];data=p.read_bytes()
  counts,embedded=validate_embedded(canonical,original,data)
  files[name]={'sha256':sha(p),'exports':exports(p),'embeddedShaderSha256':embedded,'embeddedShaderByteCounts':counts}
 embed=subprocess.check_output(['git','-C',str(source),'show',CANDIDATE+':src/ProGPU.Native/cmake/EmbedShader.cmake'])
 receipt={'candidateCommit':CANDIDATE,'originalCommit':BASE,'shaderSha256':SHADER,'files':files,'qualification':False,'artifact':meta,'embedShaderSourceSha256':hashlib.sha256(embed).hexdigest(),'generatedHeader':meta['generatedHeader'],'artifactId':int(os.environ['CANDIDATE_ARTIFACT_ID']),'artifactDigest':os.environ['CANDIDATE_ARTIFACT_DIGEST']}
 save('candidate.json',receipt)
elif mode=='stage':
 source=pathlib.Path(os.environ['ORIGINAL']);assert head(source)==BASE
 original=source/'tests/ProGPU.Native.PackageConsumer/bin/Release/net10.0/win-arm64';dest=pathlib.Path(os.environ['EXECUTION']).resolve();assert not dest.exists()
 payload=pathlib.Path(os.environ['PAYLOAD'])/'runtimes/win-arm64/native';receipt=json.loads((out/'candidate.json').read_text());assert receipt['candidateCommit']==CANDIDATE and receipt['shaderSha256']==SHADER and not receipt['qualification']
 before=inventory(original);assert NAMES<=before.keys();shutil.copytree(original,dest)
 for name in sorted(NAMES):
  p=payload/name;assert machine(p)==0xaa64 and sha(p)==receipt['files'][name]['sha256']
  assert exports(original/name)==[tuple(x) for x in receipt['files'][name]['exports']]==exports(p)
  shutil.copy2(p,dest/name)
 after=inventory(dest);changed=validate_overlay(before,after)
 assert inventory(original)==before
 save('private-overlay.json',{'before':before,'after':after,'changed':sorted(changed),'candidate':receipt,'qualification':False})
else:raise ValueError(mode)
