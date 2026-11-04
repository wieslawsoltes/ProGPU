"""One fixed Microsoft-only matched pair. No input, GPU or provider initialization."""
import hashlib,json,os,pathlib,shutil,subprocess,sys,tempfile
from owned_process import run
from contract import validate,compare
ROOT=pathlib.Path(__file__).resolve().parent

def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def inventory(root):return {p.relative_to(root).as_posix():{'bytes':p.stat().st_size,'sha256':sha(p)} for p in root.rglob('*') if p.is_file()}
def execute_variant(workspace,evidence,variant):
    source=workspace/variant;shutil.copytree(ROOT/variant,source)
    with (evidence/(variant+'-build.log')).open('w') as log:
        built=subprocess.run(['dotnet','build',str(source/'TextRasterReference.csproj'),'-c','Release','-r','win-x64','-p:ImportDirectoryBuildProps=false','-p:ImportDirectoryBuildTargets=false','--nologo'],cwd=workspace,stdout=log,stderr=subprocess.STDOUT,timeout=600)
    assert built.returncode==0
    binary=source/'bin/Release/net10.0-windows/win-x64'
    frozen=inventory(binary);(evidence/(variant+'-closure.json')).write_text(json.dumps(frozen,indent=2)+'\n')
    temporary=workspace/(variant+'-temp');temporary.mkdir();observation=temporary/'observation'
    prior=os.environ.get('RUNNER_TEMP');process_receipt=evidence/(variant+'-process.json')
    try:
        os.environ['RUNNER_TEMP']=str(temporary)
        result=run([str(binary/'TextRasterReference.exe'),str(observation)],evidence/(variant+'-application.log'),process_receipt,60)
        assert result['exitUnsigned']==0 and not result['timedOut'] and not result['cleanupErrors']
        captured=validate(observation,variant=='with-prefix')
        assert frozen==inventory(binary)
        return captured
    finally:
        if prior is None:os.environ.pop('RUNNER_TEMP',None)
        else:os.environ['RUNNER_TEMP']=prior
        # Only observations are retained: selected font bytes never leave this
        # owned disposable workspace or become an artifact member.
        if observation.exists():shutil.copytree(observation,evidence/variant)
        fonts=temporary/'text-raster-font-inputs'
        if fonts.exists():shutil.rmtree(fonts)

def main():
    assert sys.platform=='win32' and os.environ.get('GITHUB_ACTIONS')=='true'
    assert os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted' and os.environ.get('RUNNER_ARCH')=='X64'
    assert os.environ.get('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.environ.get('GITHUB_RUN_ATTEMPT')=='1'
    manifest=json.loads((ROOT/'source-manifest.json').read_text())
    for name,value in manifest['files'].items():assert sha(ROOT/name)==value
    evidence=pathlib.Path(os.environ['PREFIX_EVIDENCE']).resolve();assert not evidence.exists();evidence.mkdir(parents=True)
    workspace=pathlib.Path(tempfile.mkdtemp(prefix='forms72-prefix-',dir=os.environ['RUNNER_TEMP']))
    result={'qualified':False,'applicationExecuted':False,'completed':False,'cleanupErrors':[]}
    try:
        shutil.copy2(ROOT/'global.json',workspace/'global.json')
        (workspace/'NuGet.config').write_text('<configuration><packageSources><clear/><add key="nuget" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>')
        shutil.copy2(ROOT/'source-manifest.json',evidence/'source-manifest.json')
        (evidence/'environment.json').write_text(json.dumps({k:os.environ.get(k) for k in ['GITHUB_REPOSITORY','GITHUB_RUN_ID','GITHUB_RUN_ATTEMPT','GITHUB_SHA','RUNNER_ARCH','RUNNER_OS','ImageOS','ImageVersion']},indent=2)+'\n')
        (evidence/'dotnet-info.log').write_text(subprocess.check_output(['dotnet','--info'],cwd=workspace,text=True,timeout=30))
        first=execute_variant(workspace,evidence,'without-prefix')
        second=execute_variant(workspace,evidence,'with-prefix')
        result['comparison']=compare(first,second);result['completed']=True
    except BaseException as error:
        result['error']=repr(error);raise
    finally:
        for p in evidence.glob('*-process.json'):
            if 'pid' in json.loads(p.read_text()):result['applicationExecuted']=True
        try:shutil.rmtree(workspace)
        except BaseException as error:result['cleanupErrors'].append(repr(error));result['completed']=False
        result['retainedWorkspace']=str(workspace) if workspace.exists() else None
        (evidence/'result.json').write_text(json.dumps(result,indent=2)+'\n')
    assert result['completed'] and not result['cleanupErrors']
if __name__=='__main__':main()
