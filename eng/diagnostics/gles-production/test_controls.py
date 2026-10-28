from pathlib import Path
import json,tempfile,unittest,struct
import build_receipt,run,inside
class Controls(unittest.TestCase):
    def fixture(self,root,rid='linux-arm64'):
        root.mkdir();(root/'native').mkdir();elf=bytearray(64);elf[:6]=b'\x7fELF\x02\x01';struct.pack_into('<H',elf,18,183 if rid=='linux-arm64' else 62)
        for n in ['native-surface-probe','native-resolve-scissor-probe','native/libwgpu_native.so']:(root/n).write_bytes(elf)
        native={'abiTestsPassed':True,'producerCommit':'exact','rid':rid};(root/'native/wgpu-native-linux.json').write_text(json.dumps(native))
        record={'files':build_receipt.inventory(root),'producerCommit':'exact','rid':rid,'run':'123','attempt':'1','qualified':False,'sourceHashes':{'source':'exact'},'producerReceipt':native}
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
if __name__=='__main__':unittest.main()
