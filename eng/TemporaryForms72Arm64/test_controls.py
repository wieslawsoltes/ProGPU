import importlib.util,json,os,pathlib,sys,tempfile,types,unittest
from unittest.mock import patch
ROOT=pathlib.Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT))
import run as runner
class Controls(unittest.TestCase):
 def env(self,temp):
  return {'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','RUNNER_ARCH':'ARM64','GITHUB_REPOSITORY':'wieslawsoltes/ProGPU','GITHUB_RUN_ATTEMPT':'1','RUNNER_TEMP':str(temp),'PREFIX_EVIDENCE':str(temp/'evidence')}
 def test_exact_original_source(self):
  for p in (ROOT/'with-prefix').iterdir():self.assertEqual(p.read_bytes(),(ROOT.parent/'with-prefix'/p.name).read_bytes())
 def test_original_limits_and_rid(self):
  s=(ROOT/'run.py').read_text();self.assertIn("'-r','win-arm64'",s);self.assertIn('process_receipt,60)',s)
  self.assertIn("sys.getwindowsversion().build==26200",s)
  s=(ROOT/'with-prefix/Program.cs').read_text();self.assertIn('clock.Elapsed.TotalSeconds > 45',s);self.assertIn('cases.Count != 72 || driverGlyphs.Count != Text.Length * 12',s)
 def test_actual_entrypoint_one_prefix_and_cleanup(self):
  with tempfile.TemporaryDirectory() as d:
   temp=pathlib.Path(d);capture=json.loads((ROOT.parent/'original/with-prefix/reference.json').read_text())
   with patch.dict(os.environ,self.env(temp)),patch.object(sys,'platform','win32'),patch.object(sys,'getwindowsversion',return_value=types.SimpleNamespace(build=26200),create=True),patch.object(runner.subprocess,'check_output',return_value='authored offline fixture'),patch.object(runner,'execute_variant',return_value=capture) as execute:
    runner.main()
   execute.assert_called_once();self.assertEqual(execute.call_args.args[2],'with-prefix')
   result=json.loads((temp/'evidence/result.json').read_text());self.assertTrue(result['completed']);self.assertFalse(result['applicationExecuted']);self.assertIsNone(result['retainedWorkspace'])
   self.assertEqual(result['comparisons']['matchedCurrentX64']['differentFrameIndices'],[])
 def test_wrong_arch_or_os_rejects_before_application(self):
  for arch,build in [('X64',26200),('ARM64',26100)]:
   with tempfile.TemporaryDirectory() as d:
    temp=pathlib.Path(d);env=self.env(temp);env['RUNNER_ARCH']=arch
    with patch.dict(os.environ,env),patch.object(sys,'platform','win32'),patch.object(sys,'getwindowsversion',return_value=types.SimpleNamespace(build=build),create=True),patch.object(runner,'execute_variant') as execute:
     with self.assertRaises(AssertionError):runner.main()
    execute.assert_not_called();self.assertFalse((temp/'evidence').exists())
if __name__=='__main__':unittest.main()
