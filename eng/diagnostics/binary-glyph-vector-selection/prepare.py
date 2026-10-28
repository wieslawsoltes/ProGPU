import hashlib,json,os,pathlib,subprocess,urllib.request,zipfile
root=pathlib.Path(__file__).resolve().parent;out=root/'evidence';out.mkdir(exist_ok=True)
pin=json.loads((root/'compiler-package.json').read_text());package=out/'compiler.nupkg'
with urllib.request.urlopen(pin['url'],timeout=60) as r:
 b=r.read(128*1024*1024)
assert hashlib.sha256(b).hexdigest()==pin['sha256'];package.write_bytes(b)
subprocess.run(['dotnet','nuget','verify',str(package),'--all','--certificate-fingerprint',pin['authorCertificateSha256']],check=True,timeout=120)
compiler=out/'compiler';compiler.mkdir();runtime=pin['runtimes']['win-arm64']
with zipfile.ZipFile(package) as z:
 for name in ('dxcompiler.dll','dxil.dll'):
  data=z.read(runtime['archiveDirectory']+'/'+name);assert hashlib.sha256(data).hexdigest()==runtime[name];(compiler/name).write_bytes(data)
 header=z.read('build/native/include/dxcapi.h');(compiler/'dxcapi.h').write_bytes(header)
meta={'packageSha256':pin['sha256'],'files':[]}
for f in sorted(compiler.iterdir()):meta['files'].append({'path':f.name,'sha256':hashlib.sha256(f.read_bytes()).hexdigest(),'bytes':f.stat().st_size})
(out/'compiler-inputs.json').write_text(json.dumps(meta,indent=2)+'\n')
