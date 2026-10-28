from pathlib import Path
import json,tempfile,unittest,struct,os,re
from unittest import mock
import build_receipt,run,inside
class Controls(unittest.TestCase):
    def setUp(self):
        self.environment=mock.patch.dict(os.environ,{"GITHUB_RUN_ATTEMPT":"1"});self.environment.start();self.addCleanup(self.environment.stop)
    def fixture(self,root,rid='linux-arm64'):
        root.mkdir();(root/'native').mkdir();elf=bytearray(64);elf[:6]=b'\x7fELF\x02\x01';struct.pack_into('<H',elf,18,183 if rid=='linux-arm64' else 62)
        for n in ['native-resolve-scissor-probe','native/libwgpu_native.so']:(root/n).write_bytes(elf)
        native={'abiTestsPassed':True,'producerCommit':'exact','rid':rid};(root/'native/wgpu-native-linux.json').write_text(json.dumps(native))
        record={'files':build_receipt.inventory(root),'producerCommit':'exact','rid':rid,'run':'123','attempt':'1','qualified':False,'sourceHashes':{'source':'exact'},'profileSha256':run.sha(run.ROOT/'inputs.json'),'producerReceipt':native}
        (root/'build-receipt.json').write_text(json.dumps(record));return {'producerCommit':'exact','producerSourceHashes':{'source':'exact'}}
    def test_both_native_machine_inventories(self):
        for rid in ['linux-x64','linux-arm64']:
            with tempfile.TemporaryDirectory() as d:
                root=Path(d)/'payload';pin=self.fixture(root,rid);self.assertEqual(run.validate_payload(root,pin,rid,'123')['rid'],rid)
    def test_wrong_run_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root)
            with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-arm64','other')
    def test_original_bytes_must_match(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root);(root/'native/libwgpu_native.so').write_bytes(b'changed')
            with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-arm64','123')
    def test_source_identity_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root);pin['producerSourceHashes']={'source':'other'}
            with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-arm64','123')
    def test_other_rid_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root)
            with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-x64','123')
    def test_symlink_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root);(root/'alias').symlink_to(root/'native/libwgpu_native.so')
            with self.assertRaises(AssertionError):run.validate_payload(root,pin,'linux-arm64','123')
    def test_actual_production_summary_requires_all_original_cases(self):
        text='\n'.join(f'case{i} samples=4 pixels=192 mismatches=0 draw=1' for i in range(3))+'\nloaded_runtime=/payload/native/libwgpu_native.so\nPASS: three authored MSAA4 passes, complete576pixel comparison'
        inside.parse(text,0,'resolve')
        with self.assertRaises(ValueError):inside.parse(text.replace('case1 samples=4 pixels=192 mismatches=0','case1 samples=4 pixels=192 mismatches=13'),0,'resolve')
        with self.assertRaises(ValueError):inside.parse(text,1,'resolve')
    def test_original_lifetime_completion(self):
        inside.parse('PASS: three owned GL contexts, five presentations, instance retirement and surviving peer; no input; pixels unqualified',0,'surface')
        with self.assertRaises(ValueError):inside.parse('partial',0,'surface')
    def test_wrong_attempt_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root)
            with mock.patch.dict(os.environ,{'GITHUB_RUN_ATTEMPT':'2'}):
                with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-arm64','123')
    def test_wrong_profile_rejects(self):
        with tempfile.TemporaryDirectory() as d:
            root=Path(d)/'payload';pin=self.fixture(root)
            p=root/'build-receipt.json';receipt=json.loads(p.read_text());receipt['profileSha256']='wrong';p.write_text(json.dumps(receipt))
            with self.assertRaises(ValueError):run.validate_payload(root,pin,'linux-arm64','123')
    def test_owned_container_identity_is_required(self):
        import common
        identity={'Id':'a'*64,'Config':{'Labels':{'progpu.diagnostic':'mine'}},'Image':'exact'}
        with mock.patch.object(common,'capture',return_value=json.dumps([identity])):
            self.assertEqual(common.owned('a'*64,'mine','exact'),identity)
            with self.assertRaises(ValueError):common.owned('a'*64,'other','exact')
            with self.assertRaises(ValueError):common.owned('a'*64,'mine','other')
    def test_stage_budget_cannot_extend_operation_cap(self):
        import common,time
        with mock.patch.object(common,"DEADLINE",time.monotonic()+0.03):
            self.assertLessEqual(common.bounded(60),0.03)
        with mock.patch.object(common,"DEADLINE",time.monotonic()-1):
            with self.assertRaises(TimeoutError):common.bounded(60)
    def test_process_deadline_kills_owned_group(self):
        import common,subprocess,sys,time
        with tempfile.TemporaryDirectory() as d:
            start=time.monotonic()
            with self.assertRaises(subprocess.TimeoutExpired):common.run([sys.executable,'-c','import time; time.sleep(30)'],Path(d)/'log',0.05)
            self.assertLess(time.monotonic()-start,2)
    def check_job_env_contexts(self,text):
        # GitHub's jobs.<job_id>.env context table; inspect only this new job.
        job=text.split('  xlib-runtime:\n',1)[1].split('\n  dx12-runtime:',1)[0]
        env=job.split('    env:\n',1)[1].split('    steps:',1)[0]
        allowed={'github','needs','strategy','matrix','vars','secrets','inputs'}
        for expr in re.findall(r'\$\{\{(.*?)\}\}',env):
            roots=set(re.findall(r'(?<![.\w])([A-Za-z_]\w*)\.',expr))
            self.assertFalse(roots-allowed,roots-allowed)
    def test_new_workflow_job_env_uses_available_contexts(self):
        for name in ['build','release']:
            self.check_job_env_contexts((build_receipt.SOURCE/'.github/workflows'/f'{name}.yml').read_text())
    def test_job_env_rejects_step_only_env_context(self):
        original=(build_receipt.SOURCE/'.github/workflows/build.yml').read_text()
        changed=original.replace('EXPECTED_HEAD: ${{ github.event.pull_request.head.sha || github.sha }}','EXPECTED_HEAD: ${{ env.PROGPU_QUALIFIED_COMMIT }}')
        with self.assertRaises(AssertionError):self.check_job_env_contexts(changed)
if __name__=='__main__':unittest.main()
