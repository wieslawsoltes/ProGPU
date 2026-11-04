"""Independent original64 evidence; the original72 qualification gate stays failed."""
from pathlib import Path
import argparse,hashlib,json,math,os,platform,shutil
from owned_process import run as owned_run
from compare_geometry import compare
from verify_scan_witness import compare as compare_scan_witness, SCAN_MASK
ROOT=Path(__file__).resolve().parent

def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def save(path,value):
 with path.open('x') as stream:json.dump(value,stream,indent=2)
def verified_closure(path):
 value=json.loads(path.read_text());producer=value['producer']
 if producer['head']!='6208f44ca74f2b5cf5d03ed1117d938421f27f7d' or producer['wholeBuildConclusion']!='success' or producer['wholeBuildRunId']!=40740184789:raise ValueError('Exact whole-qualified producer required')
 if set(producer['packageRepositoryCommits'])!={producer['head']}:raise ValueError('Package sources differ')
 for item in value['files']:
  candidate=Path(item['absolutePath'])
  if not candidate.is_absolute() or candidate.is_symlink() or digest(candidate)!=item['sha256']:raise ValueError('Closure identity changed')
 names={str(Path(x['absolutePath']).resolve()).casefold() for x in value['files']}
 for key in ('dotnet','fontCollectorAssembly','nativeAssembly'):
  if str(Path(value[key]).resolve()).casefold() not in names:raise ValueError('Executable missing from closure')
 return value

def validate_fonts(path,private):
 d=json.loads(path.read_text());original=json.loads((ROOT/'original-contract.json').read_text())
 assert d['schema']=='original-font-only-v1' and d['qualified'] is False and d['inputEvents']==d['desktopWindows']==0
 assert d['text']=='Name Age 123 Hello Windows Forms' and d['loadedIdentity']['architecture']=='X64'
 assert math.isfinite(d['elapsedSeconds']) and 0<=d['elapsedSeconds']<=45
 assert d['smoothing']==original['smoothing']
 assert [{k:f[k] for k in original['fonts'][0]} for f in d['fonts']]==original['fonts']
 names=set()
 for font in d['fonts']:
  name=font['sha256']+'.ttf';names.add(name);p=private/name
  assert p.is_file() and not p.is_symlink() and p.stat().st_size==font['bytes'] and digest(p)==font['sha256']
 assert {p.name for p in private.iterdir()}==names
 return d

def main():
 parser=argparse.ArgumentParser();parser.add_argument('--closure',type=Path,required=True);args=parser.parse_args()
 assert platform.system()=='Windows' and platform.machine().lower() in ('amd64','x86_64')
 assert os.getenv('GITHUB_ACTIONS')=='true' and os.getenv('RUNNER_ENVIRONMENT')=='github-hosted'
 assert os.getenv('GITHUB_REPOSITORY')=='wieslawsoltes/ProGPU' and os.getenv('UNHINTED64_RUNNER_LABEL')=='windows-2025' and os.getenv('GITHUB_RUN_ATTEMPT')=='1'
 closure=verified_closure(args.closure)
 for name,expected in json.loads((ROOT/'hosted-source-manifest.json').read_text()).items():assert digest(ROOT/name)==expected
 prior=json.loads((ROOT/'failed72-receipt.json').read_text());assert prior['status']=='failed' and prior['qualified'] is False and prior['gateUnchanged'] is True
 temporary=Path(os.environ['RUNNER_TEMP']).resolve();fonts=temporary/'text-raster-font-inputs';assert not fonts.exists()
 output=temporary/'original64-standalone-results';output.mkdir(exist_ok=False)
 save(output/'closure.json',closure);save(output/'original72-failed-status.json',prior)
 result={'qualified':False,'original72Qualification':'failed-preserved','baselineSubstitution':False,'inputEvents':0,'desktopWindows':0,'providers':{},'completed':False,'cleanupErrors':[]};failed=False
 try:
  metadata=output/'font-source'
  observed=owned_run([closure['dotnet'],closure['fontCollectorAssembly'],str(metadata)],output/'font-source.log',output/'font-source-process.json',60)
  assert observed['exitUnsigned']==0 and not observed['timedOut'] and not observed['cleanupErrors']
  validate_fonts(metadata/'fonts.json',fonts)
  verified_closure(args.closure)
  for provider in ('wgpu','dawn'):
   observed=owned_run([closure['dotnet'],closure['nativeAssembly'],provider,str(ROOT/'inputs'),str(metadata),str(output/provider),str(output/'closure.json')],output/(provider+'.log'),output/(provider+'-process.json'),60)
   result['providers'][provider]=observed
   assert observed['exitUnsigned'] in (0,2) and not observed['timedOut'] and not observed['cleanupErrors']
   actual=json.loads((output/provider/'result.json').read_text());assert actual['qualified'] is False and actual['scanWitness'] is True and actual['scanMask']==SCAN_MASK and len(actual['rows'])==128 and actual['elapsedSeconds']<=45
   save(output/provider/'geometry-comparison.json',compare(ROOT/'inputs',output/provider))
   save(output/provider/'scan-witness-comparison.json',compare_scan_witness(output/provider))
   failed|=observed['exitUnsigned']!=0
  verified_closure(args.closure);result['completed']=True;result['pixelFailure']=failed
 except BaseException as error:failed=True;result['error']=repr(error)
 finally:
  expected={f['sha256'] for f in json.loads((ROOT/'inputs/original64.json').read_text())['fonts']}
  if fonts.exists():
   for path in fonts.iterdir():
    try:
     if path.is_symlink() or path.name not in {h+'.ttf' for h in expected} or digest(path) not in expected:raise RuntimeError('Unexpected private font; refused deletion')
     path.unlink()
    except BaseException as error:result['cleanupErrors'].append(repr(error))
   try:fonts.rmdir()
   except BaseException as error:result['cleanupErrors'].append(repr(error))
  result['privateFontsRetired']=not fonts.exists()
  if result['cleanupErrors']:result['completed']=False
  save(output/'execution.json',result)
  save(output/'hashes.json',{p.relative_to(output).as_posix():{'bytes':p.stat().st_size,'sha256':digest(p)}for p in output.rglob('*')if p.is_file()})
 return 1 if failed or result['cleanupErrors'] else 0
if __name__=='__main__':raise SystemExit(main())
