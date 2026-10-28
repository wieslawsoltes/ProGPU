from pathlib import Path
import unittest,re
ROOT=Path(__file__).resolve().parent
class Source(unittest.TestCase):
    def test_original_drawing_and_oracle_preserved(self):
        original=(ROOT/'original-renderbuffer.c').read_text();new=(ROOT/'probe.c').read_text()
        for line in original.splitlines():
            if any(k in line for k in ['const char *vs=','const char *fs=','const int left=','glClearColor(index==','if(draw) { glScissor(','int green=draw','unsigned char expected[4]']):self.assertIn(line,new)
    def test_sample_fetch_is_unfiltered_and_complete(self):
        new=(ROOT/'probe.c').read_text()
        self.assertIn('texelFetch(originalSamples,ivec2(p.x,p.y%12),p.y/12)',new)
        mapping={(x,y%12,y//12) for y in range(48) for x in range(16)}
        self.assertEqual(mapping,{(x,y,s) for s in range(4) for y in range(12) for x in range(16)})
        self.assertNotIn('texture(originalSamples',new)
    def test_readback_precedes_original_resolve(self):
        s=(ROOT/'probe.c').read_text();self.assertLess(s.index('read-original-samples'),s.index('glBlitFramebuffer('))
        self.assertLess(s.index('state(index,"after-sample-observation")'),s.index('glBlitFramebuffer('))
    def test_cap_and_driver_guard(self):
        s=(ROOT/'run.py').read_text();self.assertIn("exe,'disable',out],out/'probe.log',60)",s)
        c=(ROOT/'probe.c').read_text();self.assertIn('assert(actual_samples==4 && sample_buffers==1)',c);self.assertIn('Mesa 25.2.8-0ubuntu0.24.04.4',c)
if __name__=='__main__':unittest.main()
