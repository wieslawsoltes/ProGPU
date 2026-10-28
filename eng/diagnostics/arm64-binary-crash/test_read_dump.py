import pathlib, struct, tempfile, unittest
from read_dump import read

def fixture():
    # Format offsets independently authored from MINIDUMP_EXCEPTION_STREAM and
    # MINIDUMP_MODULE: three streams, one ARM64 exception and one bounded image.
    b=bytearray(512)
    struct.pack_into('<4I',b,0,0x504d444d,0xa793,3,32)
    struct.pack_into('<3I',b,32,7,2,68)
    struct.pack_into('<3I',b,44,6,168,72)
    struct.pack_into('<3I',b,56,4,112,240)
    struct.pack_into('<H',b,68,12)
    struct.pack_into('<I',b,72,42)
    struct.pack_into('<IIQQII',b,80,0xc0000409,1,0,0x1234,1,0)
    struct.pack_into('<Q',b,112,7)
    struct.pack_into('<I',b,240,1)
    struct.pack_into('<QIIII',b,244,0x1000,0x1000,0,1,352)
    name='CrashControl.exe'.encode('utf-16-le')
    struct.pack_into('<I',b,352,len(name));b[356:356+len(name)]=name
    return b

class DumpControls(unittest.TestCase):
    def parse(self,b):
        with tempfile.TemporaryDirectory() as tmp:
            p=pathlib.Path(tmp)/'control.dmp';p.write_bytes(b);return read(p)
    def test_exact_exception_parameter_and_owner(self):
        r=self.parse(fixture())
        self.assertEqual((r['threadId'],r['exceptionCode'],r['parameters']),(42,'0xc0000409',['0x7']))
        self.assertTrue(r['modules'][0]['containsExceptionAddress'])
    def test_truncated_original_is_rejected(self):
        with self.assertRaises(ValueError):self.parse(fixture()[:170])
    def test_other_machine_rejected(self):
        b=fixture();struct.pack_into('<H',b,68,9)
        with self.assertRaises(ValueError):self.parse(b)
    def test_short_declared_system_info_rejected(self):
        b=fixture();struct.pack_into('<I',b,36,1)
        with self.assertRaises(ValueError):self.parse(b)
    def test_parameter_overflow_rejected(self):
        b=fixture();struct.pack_into('<I',b,104,16)
        with self.assertRaises(ValueError):self.parse(b)
    def test_duplicate_stream_rejected(self):
        b=fixture();struct.pack_into('<I',b,56,6)
        with self.assertRaises(ValueError):self.parse(b)
    def test_repeated_empty_unused_entries_retained(self):
        b=fixture();struct.pack_into('<I',b,8,5)
        # Move the enlarged directory to spare bytes, preserving all streams.
        b[400:436]=b[32:68];struct.pack_into('<I',b,12,400)
        r=self.parse(b)
        self.assertEqual(r['directoryEntries'][-2:],[{'type':0,'size':0,'rva':0}]*2)
        self.assertEqual(r['parameters'],['0x7'])
    def test_nonempty_unused_entry_rejected(self):
        b=fixture();struct.pack_into('<I',b,32,0)
        with self.assertRaises(ValueError):self.parse(b)
    def test_module_count_overflow_rejected(self):
        b=fixture();struct.pack_into('<I',b,240,2)
        with self.assertRaises(ValueError):self.parse(b)
    def test_out_of_range_name_rejected(self):
        b=fixture();struct.pack_into('<I',b,264,9000)
        with self.assertRaises(ValueError):self.parse(b)
    def test_other_exception_preserved_without_relabeling(self):
        b=fixture();struct.pack_into('<I',b,80,0xc0000005)
        self.assertEqual(self.parse(b)['exceptionCode'],'0xc0000005')

if __name__=='__main__':unittest.main()
