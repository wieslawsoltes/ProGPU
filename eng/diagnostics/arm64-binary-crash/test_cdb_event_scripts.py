import pathlib, tempfile, unittest
from cdb_event_scripts import prepare, FAULTS, quoted_command_path, validate_filter_inventory
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
 def test_windows_command_path_escaping(self):
  original=r'C:\a\ProGPU\evidence\event-chance-1.cmd'
  escaped=quoted_command_path(original)
  self.assertEqual(escaped.replace(chr(92)*2,chr(92)),original)
  self.assertEqual(escaped.count(chr(92)),original.count(chr(92))*2)
 def test_actual_missing_named_filters_reject(self):
  import pathlib
  original=pathlib.Path(__file__).with_name('original-cdb-filter-inventory.txt')
  if original.exists():
   with self.assertRaises(ValueError):validate_filter_inventory(original.read_text())
 def test_corrected_actual_inventory_admits(self):
  import re
  original=pathlib.Path(__file__).with_name('original-cdb-filter-inventory.txt').read_text()
  corrected=original
  for key in ('svh','vs','rto','rtt'):
   corrected=re.sub(r'(?m)^(\s{0,3}'+key+r' - .*not handled)$',
                    lambda m:m.group(0)+'\n       Command: "event-chance-1.cmd"\n       Second command: "event-chance-2.cmd"',corrected)
  observed=validate_filter_inventory(corrected)
  self.assertTrue({'*','av','sbo','eh','svh','vs','rto','rtt'}<=set(observed))
 def test_named_faults_and_wildcard(self):
  self.assertIn('*',FAULTS)
  for x in ('av','eh','sbo','sov','ch'):self.assertIn(x,FAULTS)
  for x in ('ibp','clrn','bpe','vcpp'):self.assertNotIn(x,FAULTS)
if __name__=='__main__':unittest.main()
