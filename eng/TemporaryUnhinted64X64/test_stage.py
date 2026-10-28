import hashlib
import json
from pathlib import Path
import tempfile
import struct
import unittest
from unittest import mock
import zipfile
import stage

class StagingTests(unittest.TestCase):
    def test_live_placeholder_rejects_before_artifact_access(self):
        with self.assertRaisesRegex(ValueError,'not reviewed and qualified'):stage.qualification()

    def receipt(self,root):
        evidence=b'original dedicated diagnostics';(root/'producer-checks.json').write_bytes(evidence)
        value={'qualificationReviewed':True,'producer':{'head':stage.HEAD,'wholeBuildRunId':stage.RUN,
            'wholeBuildConclusion':'success','reviewEvidenceSha256':hashlib.sha256(evidence).hexdigest()},
            'artifact':{'name':'progpu-native-package','id':1,'bytes':100,'sha256':'a'*64},
            'packageVersion':'1.0.0','packages':{n:{'file':n+'.1.0.0.nupkg','bytes':1,'sha256':'b'*64} for n in stage.PACKAGES}}
        return value

    def check(self,root,value):
        (root/'producer-qualification.json').write_text(json.dumps(value));return stage.qualification(root)

    def test_changed_qualification_evidence_rejects(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);value=self.receipt(root);(root/'producer-checks.json').write_bytes(b'changed')
            with self.assertRaisesRegex(ValueError,'evidence changed'):self.check(root,value)

    def test_oversize_artifact_rejects_before_download(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);value=self.receipt(root);value['artifact']['bytes']=512*1024*1024+1
            with self.assertRaisesRegex(ValueError,'artifact identity'):self.check(root,value)

    def test_missing_transitive_sdk_is_not_a_closed_feed(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);value=self.receipt(root);del value['packages']['ProGPU.Native.Sdk']
            with self.assertRaisesRegex(ValueError,'four-package closure'):self.check(root,value)

    def package(self,root,head,dependency='ProGPU.Backend',version='1.0.0'):
        path=root/'fixture.nupkg'
        with zipfile.ZipFile(path,'w') as archive:
            archive.writestr('fixture.nuspec','<package><metadata><id>ProGPU.Backend.Dawn</id><version>1.0.0</version>'
                '<repository commit="'+head+'"/><dependencies><group><dependency id="'+dependency+'" version="'+version+'"/>'
                '</group></dependencies></metadata></package>')
        return path

    def test_old_producer_package_rejects(self):
        with tempfile.TemporaryDirectory() as folder:
            path=self.package(Path(folder),'dcaac6c708371ffee2f6dfa7f304307e57623437')
            with self.assertRaisesRegex(ValueError,'source/version'):stage.check_package(path,'ProGPU.Backend.Dawn','1.0.0')

    def test_unknown_transitive_dependency_rejects(self):
        with tempfile.TemporaryDirectory() as folder:
            path=self.package(Path(folder),stage.HEAD,'ProGPU.Scene')
            with self.assertRaisesRegex(ValueError,'Unexpected dependency'):stage.check_package(path,'ProGPU.Backend.Dawn','1.0.0')

    def test_exact_owned_dependency_is_preserved(self):
        with tempfile.TemporaryDirectory() as folder:
            path=self.package(Path(folder),stage.HEAD)
            result=stage.check_package(path,'ProGPU.Backend.Dawn','1.0.0')
            self.assertEqual(result['dependencies'],{'ProGPU.Backend':'1.0.0'})

    def payload(self,root,machine=0x8664):
        feed=root/'feed';feed.mkdir();published=root/'published';published.mkdir();evidence=root/'evidence';evidence.mkdir()
        pe=bytearray(128);pe[:2]=b'MZ';struct.pack_into('<I',pe,60,64);pe[64:68]=b'PE\0\0';struct.pack_into('<H',pe,68,machine)
        inventories={
            'ProGPU.Backend':{'lib/net10.0/ProGPU.Backend.dll':b'managed'}|
                {'runtimes/win-x64/native/'+n:(bytes(pe) if n.endswith('.dll') else n.encode()) for n in stage.WINDOWS_FILES},
            'ProGPU.Backend.Dawn':{'lib/net10.0/ProGPU.Backend.Dawn.dll':b'managed'}|
                {'buildTransitive/runtimes/win-x64/native/'+n:(bytes(pe) if n.endswith('.dll') else n.encode())
                 for n in ('progpu_dawn_system_warp.dll','progpu-dawn-system-warp.json','Dawn-LICENSE.txt')},
            'ProGPU.Backend.Native':{'lib/net10.0/ProGPU.Backend.Native.dll':b'managed'}|
                {'runtimes/win-x64/native/'+n:bytes(pe) for n in ('progpu_native.dll','progpu_native_dawn.dll')}}
        for package,members in inventories.items():
            with zipfile.ZipFile(feed/(package+'.1.0.0.nupkg'),'w') as z:
                for name,data in members.items():z.writestr(name,data);(published/Path(name).name).write_bytes(data)
        return feed,published,evidence

    def test_all_eight_backend_owned_runtime_members_retained(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder));stage.check_payload(feed,'1.0.0',published,evidence)
            rows=json.loads((evidence/'original-payload-members.json').read_text())
            actual={Path(r['member']).name for r in rows if r['package']=='ProGPU.Backend' and '/native/' in r['member']}
            self.assertEqual(actual,stage.WINDOWS_FILES)

    def test_original_arm64_payload_rejected_in_x64_route(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder),machine=0xaa64)
            with self.assertRaisesRegex(ValueError,'not AMD64'):
                stage.check_payload(feed,'1.0.0',published,evidence)
            self.assertFalse((evidence/'original-payload-members.json').exists())

    def test_stock_or_modified_published_runtime_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder));(published/'wgpu_native.dll').write_bytes(b'stock runtime')
            with self.assertRaisesRegex(ValueError,'differs from original package'):stage.check_payload(feed,'1.0.0',published,evidence)
            self.assertFalse((evidence/'original-payload-members.json').exists())

    def test_modified_published_receipt_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder));(published/'wgpu-native-build.json').write_text('{}')
            with self.assertRaisesRegex(ValueError,'differs from original package'):stage.check_payload(feed,'1.0.0',published,evidence)

    def test_missing_published_notice_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder));(published/'webgpu-headers-LICENSE').unlink()
            with self.assertRaises(FileNotFoundError):stage.check_payload(feed,'1.0.0',published,evidence)

    def test_duplicate_original_runtime_member_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            feed,published,evidence=self.payload(Path(folder))
            import warnings
            with warnings.catch_warnings():
                warnings.simplefilter('ignore',UserWarning)
                with zipfile.ZipFile(feed/'ProGPU.Backend.1.0.0.nupkg','a') as z:z.writestr('runtimes/win-x64/native/wgpu_native.dll',b'substitute')
            with self.assertRaisesRegex(ValueError,'duplicate original package member'):stage.check_payload(feed,'1.0.0',published,evidence)

    def test_duplicate_package_owner_rejected_after_both_rid_verifications(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);feed,published,evidence=self.payload(root)
            with zipfile.ZipFile(feed/'ProGPU.Native.Sdk.1.0.0.nupkg','w') as z:z.writestr('runtimes/win-x64/native/wgpu_native.dll',b'wrong owner')
            with mock.patch.object(stage,'command') as invoke:
                with self.assertRaisesRegex(ValueError,'Duplicate Windows runtime ownership'):stage.verify_owned_windows(feed,'1.0.0',evidence,{})
            self.assertEqual(invoke.call_count,2)
            for call,rid in zip(invoke.call_args_list,('win-x64','win-arm64')):
                self.assertEqual(call.args[0][1],'-I');self.assertEqual(call.args[0][-2:],['--rid',rid])

    def test_missing_source_verifier_pin_rejected_before_execution(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder);helper=root/'helpers/owned-windows';helper.mkdir(parents=True)
            (helper/'source-receipt.json').write_text(json.dumps({'head':stage.HEAD,'files':[{'copiedPath':'missing-pin.json','sha256':'a'*64}]}))
            with mock.patch.object(stage,'ROOT',root),mock.patch.object(stage,'command') as invoke:
                with self.assertRaises(FileNotFoundError):stage.verify_owned_windows(root,'1.0.0',root,{})
            invoke.assert_not_called()

if __name__=='__main__':unittest.main()
