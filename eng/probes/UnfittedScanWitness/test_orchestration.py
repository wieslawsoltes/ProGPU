import json,os,pathlib,sys,tempfile,unittest
from unittest.mock import patch
import run
class Orchestration(unittest.TestCase):
 def invoke(self,exit_codes):
  with tempfile.TemporaryDirectory() as d:
   root=pathlib.Path(d);closure={'dotnet':'owned-dotnet','fontCollectorAssembly':'FontCapture.dll','nativeAssembly':'Unhinted64.dll'};calls=[]
   def launch(argv,log,receipt,seconds):
    calls.append((argv,seconds));code=exit_codes[len(calls)-1]
    target=pathlib.Path(argv[-1]) if len(calls)==1 else pathlib.Path(argv[-2])
    target.mkdir()
    if len(calls)>1 and code in(0,2):(target/'result.json').write_text(json.dumps({'qualified':False,'scanWitness':True,'scanMask':run.SCAN_MASK,'rows':[{}]*128,'elapsedSeconds':1}))
    return {'exitUnsigned':code,'timedOut':False,'cleanupErrors':[]}
   env={'GITHUB_ACTIONS':'true','RUNNER_ENVIRONMENT':'github-hosted','GITHUB_REPOSITORY':'wieslawsoltes/ProGPU','UNHINTED64_RUNNER_LABEL':'windows-2025','GITHUB_RUN_ATTEMPT':'1','RUNNER_TEMP':str(root)}
   with patch.dict(os.environ,env),patch.object(sys,'argv',['run.py','--closure',str(root/'closure.json')]),patch.object(run.platform,'system',return_value='Windows'),patch.object(run.platform,'machine',return_value='AMD64'),patch.object(run,'verified_closure',return_value=closure),patch.object(run,'validate_fonts'),patch.object(run,'owned_run',side_effect=launch),patch.object(run,'compare',return_value={'qualified':False,'exactOrderedCount':64}):
    with patch.object(run,'compare_scan_witness',return_value={'qualified':False,'fonts':[]}):
     code=run.main()
   result=json.loads((root/'original64-standalone-results/execution.json').read_text())
   retained=json.loads((root/'original64-standalone-results/original72-failed-status.json').read_text())
   self.assertEqual(retained['status'],'failed');self.assertFalse(result['qualified']);self.assertFalse(result['baselineSubstitution']);self.assertTrue(result['privateFontsRetired'])
   self.assertTrue(all(seconds==60 for _,seconds in calls))
   return code,result,calls
 def test_complete_diagnostic_cannot_qualify72(self):
  code,result,calls=self.invoke([0,0,0]);self.assertEqual(code,0);self.assertTrue(result['completed']);self.assertEqual(len(calls),3)
 def test_pixel_failure_keeps_both_provider_evidence(self):
  code,result,calls=self.invoke([0,2,2]);self.assertEqual(code,1);self.assertTrue(result['completed']);self.assertTrue(result['pixelFailure']);self.assertEqual([x[0][2]for x in calls[1:]],['wgpu','dawn'])
 def test_source_failure_does_not_start_provider(self):
  code,result,calls=self.invoke([1]);self.assertEqual(code,1);self.assertFalse(result['completed']);self.assertEqual(len(calls),1)
 def test_native_fault_preserved_without_second_provider(self):
  code,result,calls=self.invoke([0,1]);self.assertEqual(code,1);self.assertFalse(result['completed']);self.assertEqual(len(calls),2)
if __name__=='__main__':unittest.main()
