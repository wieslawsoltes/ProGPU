"""Retain actual build products; this does not execute a renderer or qualify packages."""
import ast,hashlib,json,os,pathlib,re,shutil,struct,subprocess
base=pathlib.Path(__file__).parent
source=pathlib.Path(os.environ['CANDIDATE_ROOT']).resolve()
dest=pathlib.Path(os.environ['PAYLOAD']).resolve();assert not dest.exists();dest.mkdir()
head='98594ac0817865c5ebe3097d204fdb6b609b86b2'
assert subprocess.check_output(['git','-C',str(source),'rev-parse','HEAD'],text=True).strip()==head
def sha(p):return hashlib.sha256(pathlib.Path(p).read_bytes()).hexdigest()
scope={'hashlib':hashlib,'struct':struct,'subprocess':subprocess,'re':re,'SHADER':'e48c9e0428888a560902600c0e625684c13b600f534215b309dc2f24b6159707'}
for node in ast.parse((base/'stage.py').read_text()).body:
 if isinstance(node,ast.FunctionDef) and node.name in ('machine','exports','validate_embedded'):exec(compile(ast.Module([node],[]),'admission','exec'),scope)
canonical=subprocess.check_output(['git','-C',str(source),'show',head+':src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl'])
assert hashlib.sha256(canonical).hexdigest()==scope['SHADER']
checkout=(source/'src/ProGPU.Backend/Shaders/BinaryGlyphRasterizer.wgsl').read_bytes()
assert checkout in (canonical,canonical.replace(b'\n',b'\r\n'))
build=source/'artifacts/progpu-native/build-win-arm64'
header=build/'generated/BinaryGlyphRasterizerWgsl.generated.hpp'
header_bytes=bytes(int(v,16) for v in re.findall(r'0x([0-9a-fA-F]{2})',header.read_text()))
assert header_bytes==checkout+b'\0'
dependency=source/'artifacts/progpu-wgpu-windows/package/win-arm64/native'
subprocess.run(['python',str(source/'eng/progpu-wgpu-windows-runtime.py'),'--rid','win-arm64','--verify',str(dependency)],check=True)
original_wgpu='4c78555a7fa5fbb97d19fa0c7a0aaa97d3d8eebe63de89514e5fd08053c8a6e0'
assert sha(dependency/'wgpu_native.dll')==original_wgpu
native=source/'artifacts/progpu-native/package/runtimes/win-arm64/native'
target=dest/'runtimes/win-arm64/native';target.mkdir(parents=True)
files={}
for name in ('progpu_native.dll','progpu_native_dawn.dll'):
 p=native/name;assert scope['machine'](p)==0xaa64
 counts,embedded=scope['validate_embedded'](canonical,b'not-the-current-shader',p.read_bytes())
 files[name]={'sha256':sha(p),'bytes':p.stat().st_size,'exports':scope['exports'](p),'embeddedShaderSha256':embedded,'embeddedShaderByteCounts':counts}
 shutil.copy2(p,target/name);assert sha(target/name)==sha(p)
receipts=dest/'build-evidence';receipts.mkdir()
for p in (header,build/'CMakeCache.txt',build/'build.ninja',dependency/'wgpu-native-windows.json',dependency/'wgpu-native-build.json'):
 assert p.is_file();shutil.copy2(p,receipts/p.name)
source_hashes={name:sha(source/name) for name in ('eng/build-progpu-native-windows.ps1','src/ProGPU.Native/CMakeLists.txt','src/ProGPU.Native/cmake/EmbedShader.cmake','eng/native-edit-word-icu.json','eng/native-freetype.json')}
receipt={'head':head,'runId':int(os.environ['GITHUB_RUN_ID']),'dependencyRun':40720344709,'dependencyArtifact':12831320057,'dependencyArtifactSha256':'05401db5edb2f6a8475cc23e774d26a24150df0369cf8a01ac80dd2fbb5ab649','dependencyDllSha256':original_wgpu,'dllSha256':{k:v['sha256'] for k,v in files.items()},'files':files,'generatedHeader':{'sha256':sha(header),'actualBytesEqualShaderPlusNul':True},'sourceFiles':source_hashes,'qualification':False,'buildMode':'Unchanged native builder -BuildOnly; no Rust dependency build; no renderer executed in producer.'}
metadata=dest/'candidate-artifact.json';metadata.write_text(json.dumps(receipt,indent=2)+'\n')
with open(os.environ['GITHUB_OUTPUT'],'a') as output:output.write('receipt-sha256='+sha(metadata)+'\n')
print(json.dumps({'head':head,'files':receipt['dllSha256'],'qualification':False}))
