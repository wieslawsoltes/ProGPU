import copy,unittest
from prepare import PIN,NAMES,expected_variant,validate_variant
class Controls(unittest.TestCase):
 def test_exact_both_coherent_pairs(self):
  for label in ('A','B'):validate_variant(PIN['originalConsumerInventory'],expected_variant(label),label)
 def test_mixed_pair_rejects(self):
  mixed=expected_variant('A');mixed['progpu_native_dawn.dll']=expected_variant('B')['progpu_native_dawn.dll']
  with self.assertRaises(ValueError):validate_variant(PIN['originalConsumerInventory'],mixed,'A')
 def test_changed_original_consumer_rejects(self):
  before=copy.deepcopy(PIN['originalConsumerInventory']);before['ProGPU.Native.PackageConsumer.dll']['sha256']='0'*64
  with self.assertRaises(ValueError):validate_variant(before,expected_variant('A'),'A')
 def test_changed_driver_rejects(self):
  after=expected_variant('B');after['progpu_dawn_system_warp.dll']['sha256']='0'*64
  with self.assertRaises(ValueError):validate_variant(PIN['originalConsumerInventory'],after,'B')
 def test_extra_file_rejects(self):
  after=expected_variant('B');after['extra']={}
  with self.assertRaises(ValueError):validate_variant(PIN['originalConsumerInventory'],after,'B')
 def test_fixed_finite_order_and_original_bound(self):
  self.assertEqual(PIN['order'],['A','B','B','A']);self.assertEqual(PIN['perProcessSeconds'],60);self.assertEqual(len(PIN['originalConsumerInventory']),63)
if __name__=='__main__':unittest.main(verbosity=2)
