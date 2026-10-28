import pathlib, tempfile, unittest
from cdb_event_scripts import prepare, FAULTS
class Scripts(unittest.TestCase):
 def test_distinct_chances_bounded_slots_and_unhandled_continuation(self):
  with tempfile.TemporaryDirectory(prefix='cdb-control-') as t:
   setup=prepare(t).read_text()
   self.assertEqual(setup.count('sxe -c "$$><'),len(FAULTS))
   self.assertIn('0xe0434352',setup)
   self.assertIn('PROGPU_TARGET_EXIT',setup)
   for chance in (1,2):
    script=(pathlib.Path(t)/f'event-chance-{chance}.cmd').read_text()
    self.assertIn(f'%d {chance} %08x',script)
    self.assertEqual(script.count('.dump /m '),8)
    self.assertIn('@$t0 >= 8',script)
    self.assertEqual(script.count('gN;'),1)
    self.assertNotIn('gh',script)
    for i in range(8):self.assertIn(f'event-{i:02}.dmp',script)
 def test_command_path_rejection(self):
  for bad in ('has space','x;y','x"y','x{y'):
   with self.assertRaises(ValueError):prepare(bad)
 def test_named_faults_and_wildcard(self):
  self.assertIn('*',FAULTS)
  for x in ('av','eh','sbo','sov','ch'):self.assertIn(x,FAULTS)
  for x in ('ibp','clrn','bpe','vcpp'):self.assertNotIn(x,FAULTS)
if __name__=='__main__':unittest.main()
