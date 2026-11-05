"""Remove only this disposable diagnostic's generated work and tool downloads."""
import os,pathlib,json,shutil,subprocess
work=pathlib.Path(os.environ['RUNNER_TEMP']).resolve()/'managed-arm64-first-fault'
assert os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('RUNNER_ENVIRONMENT')=='github-hosted'
evidence=work/'evidence'
if not evidence.is_dir():raise SystemExit(0)
removed=[]
source=work/'source'
if source.exists():
 result=subprocess.run(['git','worktree','remove','--force',str(source)],cwd=os.environ['GITHUB_WORKSPACE'],capture_output=True,text=True,timeout=30)
 (evidence/'worktree-cleanup.json').write_text(json.dumps({'returncode':result.returncode,'stdout':result.stdout,'stderr':result.stderr},indent=2))
 if result.returncode:raise RuntimeError('Owned worktree cleanup failed')
for p in (work/'app',work/'feed',work/'nuget',work/'execution',evidence/'Procdump.zip',evidence/'procdump'):
 if not p.exists():continue
 size=p.stat().st_size if p.is_file() else sum(x.stat().st_size for x in p.rglob('*') if x.is_file())
 if p.is_dir():shutil.rmtree(p)
 else:p.unlink()
 removed.append({'path':str(p),'bytes':size})
(evidence/'cleanup.json').write_text(json.dumps(removed,indent=2)+'\n')
