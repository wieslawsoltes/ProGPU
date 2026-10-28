"""Freeze only the original native Rust dependency and two original C probes."""
from pathlib import Path
import hashlib,json,os,platform,shutil,struct,subprocess
ROOT=Path(__file__).resolve().parent
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def inventory(root):
    files=list(root.rglob('*')); assert not any(p.is_symlink() for p in files)
    return {p.relative_to(root).as_posix():{'sha256':sha(p),'bytes':p.stat().st_size} for p in files if p.is_file()}
def machine(p,rid):
    b=p.read_bytes(); assert b[:5]==b'\x7fELF\x02' and b[5]==1 and struct.unpack_from('<H',b,18)[0]=={'linux-x64':62,'linux-arm64':183}[rid]
def main():
    pin=json.loads((ROOT/'inputs.json').read_text());source=Path(os.environ['SOURCE']).resolve();rid=os.environ['RID'];spec=pin['architectures'][rid]
    assert platform.system()=='Linux' and platform.machine()==spec['architecture']
    assert 'VERSION_ID="24.04"' in Path('/etc/os-release').read_text()
    assert subprocess.check_output(['git','-C',source,'rev-parse','HEAD'],text=True).strip()==pin['producerCommit']
    assert not subprocess.check_output(['git','-C',source,'diff','--name-only','HEAD'],text=True).strip()
    native=source/'artifacts/progpu-xlib/package'/rid/'native'
    subprocess.run(['python3',source/'eng/build-wgpu-native-linux.py','--rid',rid,'--verify',native],check=True)
    original=json.loads((native/'wgpu-native-linux.json').read_text())
    assert original['producerCommit']==pin['producerCommit'] and original['abiTestsPassed'] is True
    out=Path(os.environ['BUILD_PAYLOAD']).resolve(); assert not out.exists();out.mkdir()
    shutil.copytree(native,out/'native')
    for name in ['native-surface-probe','native-resolve-scissor-probe']:
        src=source/'artifacts/wgpu-native-linux'/name;machine(src,rid);shutil.copy2(src,out/name)
    machine(out/'native/libwgpu_native.so',rid)
    paths=['eng/build-wgpu-native-linux.py','eng/wgpu-xlib/build-inputs.json','eng/wgpu-xlib/wgpu-xlib-owned.patch','eng/wgpu-xlib/wgpu-native-xlib-entry.patch','eng/wgpu-xlib/native-surface-probe.c','eng/wgpu-xlib/native-resolve-scissor-probe.c']
    record={'producerCommit':pin['producerCommit'],'rid':rid,'run':os.environ['GITHUB_RUN_ID'],'attempt':os.environ['GITHUB_RUN_ATTEMPT'],'sourceHashes':{n:sha(source/n) for n in paths},'producerReceipt':original,'files':inventory(out),'qualified':False,'applicationExecuted':False,'osRelease':Path('/etc/os-release').read_text()}
    (out/'build-receipt.json').write_text(json.dumps(record,indent=2)+'\n')
    with open(os.environ['GITHUB_OUTPUT'],'a') as f:f.write('receipt_sha256='+sha(out/'build-receipt.json')+'\n')
if __name__=='__main__':main()
