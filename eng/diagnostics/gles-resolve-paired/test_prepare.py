from pathlib import Path
import unittest
import prepare
ROOT=Path(__file__).resolve().parent
class Preparation(unittest.TestCase):
    def setUp(self):
        self.queue=(ROOT/'queue-product.rs').read_bytes();self.probe=(ROOT/'probe-product.c').read_bytes()
    def test_source_and_seams(self):
        a=prepare.derive_queue(self.queue,'disabled');b=prepare.derive_queue(self.queue,'full-scissor')
        self.assertEqual(a.count(b'progpu_diagnostic_state(gl, "'),5)
        self.assertEqual(b.count(b'progpu_diagnostic_state(gl, "'),5)
        old=b'unsafe { gl.disable(glow::SCISSOR_TEST) };\n                unsafe { progpu_diagnostic_state(gl, "after_resolve_policy"); }'
        new=b'unsafe { gl.enable(glow::SCISSOR_TEST); gl.scissor(0, 0, size.width as i32, size.height as i32); };\n                unsafe { progpu_diagnostic_state(gl, "after_resolve_policy"); }'
        self.assertEqual(a.replace(old,new),b)
    def test_no_source_hash_relaxation(self):
        with self.assertRaises(ValueError):prepare.derive_queue(self.queue+b'\n','disabled')
    def test_unknown_variant_rejected(self):
        with self.assertRaises(ValueError):prepare.derive_queue(self.queue,'guess')
    def test_pixel_logging_only(self):
        after=prepare.derive_probe(self.probe)
        line=b'\n        printf("PIXEL case=%u x=%u y=%u rgba=%u,%u,%u,%u\\n",index,x,y,actual[0],actual[1],actual[2],actual[3]);'
        self.assertEqual(after.replace(line,b''),self.probe)
    def test_probe_hash_rejected(self):
        with self.assertRaises(ValueError):prepare.derive_probe(self.probe+b'\n')
if __name__=='__main__':unittest.main()
