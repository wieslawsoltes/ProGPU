from pathlib import Path
import unittest,tempfile,json,subprocess
from unittest.mock import patch,MagicMock
import run,inside
class Controls(unittest.TestCase):
    def test_owned_label_rejects_foreign_container(self):
        cid='a'*64; data={'Id':cid,'Image':'sha256:image','Config':{'Labels':{'progpu.diagnostic':'foreign'}}}
        with patch.object(run,'capture',return_value=json.dumps([data])):
            with self.assertRaises(ValueError):run.owned(cid,'ours','sha256:image')
    def test_owned_image_rejects_replacement(self):
        cid='a'*64; data={'Id':cid,'Image':'sha256:other','Config':{'Labels':{'progpu.diagnostic':'ours'}}}
        with patch.object(run,'capture',return_value=json.dumps([data])):
            with self.assertRaises(ValueError):run.owned(cid,'ours','sha256:image')
    def test_owned_exact_identity(self):
        cid='a'*64; data={'Id':cid,'Image':'sha256:image','Config':{'Labels':{'progpu.diagnostic':'ours'}}}
        with patch.object(run,'capture',return_value=json.dumps([data])):self.assertEqual(run.owned(cid,'ours','sha256:image'),data)
    def test_timeout_kills_only_created_process_group(self):
        child=MagicMock(pid=1234);child.wait.side_effect=[subprocess.TimeoutExpired('owned',60),0];child.poll.return_value=None
        with tempfile.TemporaryDirectory() as d,patch.object(inside.subprocess,'Popen',return_value=child),patch.object(inside.os,'killpg') as kill:
            with self.assertRaises(subprocess.TimeoutExpired):inside.run(['owned'],Path(d)/'log',60)
            child.wait.assert_any_call(timeout=60);kill.assert_called_once_with(1234,inside.signal.SIGKILL)
    def test_original_failure_remains_rejected(self):
        with self.assertRaises(ValueError):inside.parse_observation('case1 samples=4 pixels=192 mismatches=13 draw=1',1)
    def test_missing_pixels_rejected(self):
        text='\n'.join(f'case{i} samples=4 pixels=192 mismatches=0 draw=1' for i in range(3))
        with self.assertRaises(ValueError):inside.parse_observation(text,0)
if __name__=='__main__':unittest.main()
