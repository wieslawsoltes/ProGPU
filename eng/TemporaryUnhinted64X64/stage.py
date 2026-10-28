"""Build-only staging; reviewed whole-producer evidence is mandatory before download."""
from pathlib import Path
import hashlib
import json
import os
import platform
import shutil
import struct
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
import zipfile

ROOT=Path(__file__).resolve().parent
HEAD='6208f44ca74f2b5cf5d03ed1117d938421f27f7d'
RUN=40740184789
PACKAGES={'ProGPU.Backend','ProGPU.Backend.Dawn','ProGPU.Backend.Native','ProGPU.Native.Sdk'}
WINDOWS_FILES={'wgpu_native.dll','wgpu-native-build.json','wgpu-native-windows.json',
               'LICENSE.MIT','LICENSE.APACHE','wgpu-LICENSE.MIT','wgpu-LICENSE.APACHE','webgpu-headers-LICENSE'}
EXTERNAL={'Microsoft.DotNet.PlatformAbstractions':'3.1.6','Microsoft.Extensions.DependencyModel':'9.0.9',
          'Silk.NET.Core':'2.23.0','Silk.NET.GLFW':'2.23.0','Silk.NET.Input.Common':'2.23.0',
          'Silk.NET.Input.Glfw':'2.23.0','Silk.NET.Maths':'2.23.0','Silk.NET.WebGPU':'2.23.0',
          'Silk.NET.WebGPU.Native.WGPU':'2.23.0','Silk.NET.Windowing.Common':'2.23.0',
          'Silk.NET.Windowing.Glfw':'2.23.0','Ultz.Native.GLFW':'3.4.0','WebGPUSharp':'0.5.5'}

def digest(path):
    with path.open('rb') as stream:return hashlib.file_digest(stream,'sha256').hexdigest()
def save(path,value):
    with path.open('x') as stream:json.dump(value,stream,indent=2)
def require(condition,message):
    if not condition:raise ValueError(message)
def qualification(root=ROOT):
    value=json.loads((root/'producer-qualification.json').read_text())
    producer=value['producer'];artifact=value['artifact']
    require(value['qualificationReviewed'] is True and producer['head']==HEAD and
            producer['wholeBuildRunId']==RUN and producer['wholeBuildConclusion']=='success',
            'Whole exact producer is not reviewed and qualified; no download/build/execution admitted')
    require(artifact['name']=='progpu-native-package' and isinstance(artifact['id'],int) and
            0<artifact['bytes']<=512*1024*1024 and len(artifact['sha256'])==64,'Invalid bounded original artifact identity')
    require(digest(root/'producer-checks.json')==producer['reviewEvidenceSha256'],'Qualification evidence changed')
    require(set(value['packages'])==PACKAGES and value['packageVersion'],'Exact four-package closure required')
    for name,item in value['packages'].items():
        require(item['file']==name+'.'+value['packageVersion']+'.nupkg' and len(item['sha256'])==64 and item['bytes']>0,
                'Invalid selected package identity')
    return value

def check_package(path,name,version):
    with zipfile.ZipFile(path) as archive:
        specs=[n for n in archive.namelist() if n.endswith('.nuspec')]
        require(len(specs)==1,'Ambiguous package metadata')
        metadata=ET.fromstring(archive.read(specs[0])).find('{*}metadata')
        require(metadata.find('{*}id').text==name and metadata.find('{*}version').text==version and
                metadata.find('{*}repository').get('commit')==HEAD,'Package source/version mismatch')
        dependencies={d.get('id'):d.get('version') for d in metadata.findall('.//{*}dependency')}
        for package,wanted in dependencies.items():
            require(package in PACKAGES|EXTERNAL.keys(),'Unexpected dependency '+package)
            require(wanted==(version if package in PACKAGES else EXTERNAL[package]),'Dependency version mismatch '+package)
        return {'id':name,'version':version,'repositoryCommit':HEAD,'dependencies':dependencies,
                'sha256':digest(path),'bytes':path.stat().st_size}

def check_payload(feed,version,published,evidence):
    members={
        'ProGPU.Backend':['lib/net10.0/ProGPU.Backend.dll']+
            ['runtimes/win-x64/native/'+name for name in sorted(WINDOWS_FILES)],
        'ProGPU.Backend.Dawn':['lib/net10.0/ProGPU.Backend.Dawn.dll',
            'buildTransitive/runtimes/win-x64/native/progpu_dawn_system_warp.dll',
            'buildTransitive/runtimes/win-x64/native/progpu-dawn-system-warp.json',
            'buildTransitive/runtimes/win-x64/native/Dawn-LICENSE.txt'],
        'ProGPU.Backend.Native':['lib/net10.0/ProGPU.Backend.Native.dll',
            'runtimes/win-x64/native/progpu_native.dll','runtimes/win-x64/native/progpu_native_dawn.dll']}
    rows=[]
    for package,entries in members.items():
        with zipfile.ZipFile(feed/(package+'.'+version+'.nupkg')) as archive:
            for entry in entries:
                require(archive.namelist().count(entry)==1,'Missing or duplicate original package member: '+entry)
                original=archive.read(entry);path=published/Path(entry).name
                require(not path.is_symlink(),'Published native member must not be a link: '+entry)
                require(path.read_bytes()==original,'NuGet payload differs from original package member: '+entry)
                if '/native/' in entry and entry.endswith('.dll'):
                    require(len(original)>=64 and original[:2]==b'MZ','Invalid native PE')
                    offset=struct.unpack_from('<I',original,60)[0]
                    require(offset<=len(original)-24 and original[offset:offset+4]==b'PE\0\0' and
                            struct.unpack_from('<H',original,offset+4)[0]==0x8664,'Native provider is not AMD64')
                rows.append({'package':package,'member':entry,'absolutePath':str(path),
                             'sha256':hashlib.sha256(original).hexdigest(),'bytes':len(original)})
    save(evidence/'original-payload-members.json',rows)

def verify_owned_windows(feed,version,evidence,env):
    # Invoke the unchanged exact producer validator in an isolated Python process.
    # It verifies both complete eight-file RID inventories and all pinned inputs.
    source=json.loads((ROOT/'helpers/owned-windows/source-receipt.json').read_text())
    require(source['head']==HEAD,'Windows verifier is not from the selected producer')
    for item in source['files']:
        require(digest(ROOT/item['copiedPath'])==item['sha256'],'Copied Windows verifier input changed')
    package=feed/('ProGPU.Backend.'+version+'.nupkg')
    for rid in ('win-x64','win-arm64'):
        command([sys.executable,'-I',str(ROOT/'helpers/owned-windows/eng/progpu-wgpu-windows-runtime.py'),
                 '--verify-package',str(package),'--rid',rid],evidence,'owned-windows-'+rid,env)
    for name in ('ProGPU.Backend.Native','ProGPU.Native.Sdk'):
        with zipfile.ZipFile(feed/(name+'.'+version+'.nupkg')) as archive:
            for entry in archive.namelist():
                for rid in ('win-x64','win-arm64'):
                    require(entry not in {'runtimes/'+rid+'/native/'+f for f in WINDOWS_FILES},
                            'Duplicate Windows runtime ownership in '+name+': '+entry)
    save(evidence/'owned-windows-verifier-source.json',source)

def command(argv,evidence,name,env):
    start=time.monotonic();receipt={'command':argv,'deadlineSeconds':600}
    with (evidence/(name+'.log')).open('xb') as log:
        process=subprocess.Popen(argv,stdout=log,stderr=subprocess.STDOUT,env=env)
        try:receipt['exitCode']=process.wait(timeout=max(.001,start+600-time.monotonic()))
        except subprocess.TimeoutExpired:
            receipt['timedOut']=True
            subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],stdout=log,stderr=subprocess.STDOUT,timeout=10)
            process.wait(timeout=10)
        finally:
            receipt['elapsedSeconds']=time.monotonic()-start
            save(evidence/(name+'.json'),receipt)
    require(receipt.get('exitCode')==0 and not receipt.get('timedOut'),'Build failed: '+name)

def build(value):
    require(platform.system()=='Windows' and platform.machine().lower() in ('amd64','x86_64') and
            os.getenv('GITHUB_ACTIONS')=='true' and os.getenv('RUNNER_ENVIRONMENT')=='github-hosted',
            'Disposable native x64 Windows 2025 only')
    require(os.getenv('UNHINTED64_RUNNER_LABEL')=='windows-2025','Exact reviewed official runner label required')
    require(os.getenv('GITHUB_RUN_ATTEMPT')=='1','No unchanged retry admitted')
    temporary=Path(os.environ['RUNNER_TEMP']).resolve();work=temporary/'unhinted64-x64-stage';work.mkdir(exist_ok=False)
    evidence=work/'evidence';evidence.mkdir()
    save(evidence/'host.json',{key:os.environ.get(key) for key in ('GITHUB_REPOSITORY','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','GITHUB_SHA','RUNNER_ARCH','RUNNER_OS','UNHINTED64_RUNNER_LABEL','ImageOS','ImageVersion')})
    source_manifest=json.loads((ROOT/'hosted-source-manifest.json').read_text())
    for name,wanted in source_manifest.items():require(digest(ROOT/name)==wanted,'Reviewed source changed: '+name)
    save(evidence/'source-hashes.json',source_manifest);save(evidence/'qualification.json',value)
    feed=work/'feed';feed.mkdir();inventory=[]
    artifact=temporary/'unhinted64-x64-package-artifact'
    for name,item in value['packages'].items():
        path=artifact/item['file']
        require(path.stat().st_size==item['bytes'] and digest(path)==item['sha256'],'Original package identity changed')
        inventory.append(check_package(path,name,value['packageVersion']))
        shutil.copy2(path,feed/path.name)
    save(evidence/'packages.json',inventory)
    verify_owned_windows(feed,value['packageVersion'],evidence,os.environ.copy())
    # Reuse the repository's original read-only validator, without native loading.
    companion=work/'companion'
    with zipfile.ZipFile(feed/('ProGPU.Backend.Dawn.'+value['packageVersion']+'.nupkg')) as archive:
        for rid in ('win-x64','win-arm64'):
            directory=companion/rid/'native';directory.mkdir(parents=True)
            for name in ('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt'):
                (directory/name).write_bytes(archive.read('buildTransitive/runtimes/'+rid+'/native/'+name))
    command(['pwsh','-NoProfile','-File',str(ROOT/'helpers/validate-dawn-system-warp-payload.ps1'),
             '-PayloadRoot',str(companion)],evidence,'companion-original-validator',os.environ.copy())
    # Keep the diagnostic outside the repository's Directory.Build/Central Package
    # files. Its package references cannot silently become source project references.
    sources=work/'sources';sources.mkdir()
    for name in ('native','reference'):
        (sources/name).mkdir()
        for path in (ROOT/name).iterdir():
            if path.suffix in ('.cs','.csproj'):shutil.copy2(path,sources/name/path.name)
    for name in ('WindowsNativePin.json','WindowsBuildPin.json','WindowsShaderPin.json'):
        shutil.copy2(ROOT/'native'/name,sources/'native'/name)
    config=work/'NuGet.Config'
    root=ET.Element('configuration');ps=ET.SubElement(root,'packageSources');ET.SubElement(ps,'clear')
    ET.SubElement(ps,'add',key='qualified',value=str(feed));ET.SubElement(ps,'add',key='nuget',value='https://api.nuget.org/v3/index.json')
    mapping=ET.SubElement(root,'packageSourceMapping')
    ET.SubElement(ET.SubElement(mapping,'packageSource',key='qualified'),'package',pattern='ProGPU.*')
    ET.SubElement(ET.SubElement(mapping,'packageSource',key='nuget'),'package',pattern='*')
    ET.ElementTree(root).write(config,encoding='utf-8',xml_declaration=True)
    env=os.environ.copy();env['NUGET_PACKAGES']=str(work/'nuget');env['DOTNET_CLI_HOME']=str(work/'dotnet-home')
    dotnet=Path(shutil.which('dotnet')).resolve()
    command([str(dotnet),'--info'],evidence,'dotnet-info',env)
    for name,project in [('reference','TextRasterReference.csproj'),('native','Unhinted64.csproj')]:
        argv=[str(dotnet),'publish',str(sources/name/project),'-c','Release','-r','win-x64','--self-contained','false',
              '-p:UseAppHost=false','-p:RestoreConfigFile='+str(config),'-o',str(work/name)]
        if name=='native':argv+=['-p:QualifiedProducerVersion='+value['packageVersion'],'-p:ProGpuEnableDawnSystemWarp=true']
        command(argv,evidence,'publish-'+name,env)
    assets=json.loads((sources/'native/obj/project.assets.json').read_text())
    actual={key.rsplit('/',1)[0]:key.rsplit('/',1)[1] for key,item in assets['libraries'].items() if item['type']=='package'}
    expected={**EXTERNAL,**{name:value['packageVersion'] for name in PACKAGES}}
    require(actual==expected,'Restored dependency closure differs from reviewed exact graph')
    require(all(item['type']=='package' for item in assets['libraries'].values()),'Source project or unknown dependency appeared')
    save(evidence/'restored-library-closure.json',{'packages':actual,'libraries':assets['libraries'],
                                               'assetsSha256':digest(sources/'native/obj/project.assets.json')})
    shutil.copy2(sources/'native/obj/project.assets.json',evidence/'project.assets.json')
    for name in ('progpu_native.dll','progpu_native_dawn.dll','progpu_dawn_system_warp.dll','wgpu_native.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt'):
        require((work/'native'/name).is_file(),'NuGet-owned native dependency missing: '+name)
    check_payload(feed,value['packageVersion'],work/'native',evidence)
    files=[{'absolutePath':str(path.resolve()),'bytes':path.stat().st_size,'sha256':digest(path)}
           for folder in ('native','reference') for path in (work/folder).rglob('*') if path.is_file()]
    files.append({'absolutePath':str(dotnet),'bytes':dotnet.stat().st_size,'sha256':digest(dotnet)})
    closure={'producer':{**value['producer'],'packageRepositoryCommits':[HEAD]},'artifact':value['artifact'],
             'dotnet':str(dotnet),'referenceAssembly':str(work/'reference/TextRasterReference.dll'),
             'nativeAssembly':str(work/'native/Unhinted64.dll'),'files':files}
    save(work/'closure.json',closure)
    save(evidence/'build-complete.json',{'built':True,'executed':False,'qualified':False,'inputEvents':0})

if __name__=='__main__':
    value=qualification()
    if sys.argv[1:]==['preflight']:
        with open(os.environ['GITHUB_OUTPUT'],'a') as output:output.write('artifact_id='+str(value['artifact']['id'])+'\n')
    elif sys.argv[1:]==['build']:build(value)
    else:raise ValueError('Expected preflight or build; this helper never executes the workloads')
