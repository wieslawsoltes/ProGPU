import base64
import struct
import unittest
from verify_scan_witness import compare_records, SCAN_MASK


class CapturedScanBits(unittest.TestCase):
    def setUp(self):
        # Raw unhinted high-precision flag and negative-zero coordinate.
        a = bytearray(48)
        struct.pack_into('<I',a,0,0x80000000)
        struct.pack_into('<III',a,36,0x1000101,0,3)
        b = bytearray(a);struct.pack_into('<I',b,36,0x300304)
        c = bytearray(a);struct.pack_into('<I',c,36,0x1300304)
        self.records = [{'segmentRecordBytes':base64.b64encode(x).decode()} for x in (a,b,c)]
        self.records[2].update(qualified=False,changedMetadataOnly=True,scanMask=SCAN_MASK)

    def corrupt(self, record, offset, value):
        raw=bytearray(base64.b64decode(self.records[record]['segmentRecordBytes']))
        raw[offset]=value;self.records[record]['segmentRecordBytes']=base64.b64encode(raw).decode()

    def test_exact_metadata_keeps_all_geometry_and_precision(self):
        r=compare_records(*self.records);self.assertEqual(r['changedScanRecords'],1)
        self.assertTrue(r['allOriginalNonScanBitsPreserved']);self.assertFalse(r['qualified'])

    def test_negative_zero_cannot_be_replaced(self):
        self.corrupt(2,3,0)
        with self.assertRaisesRegex(ValueError,'geometry'):compare_records(*self.records)

    def test_precision_flag_cannot_be_replaced(self):
        self.corrupt(2,39,0)
        with self.assertRaisesRegex(ValueError,'metadata'):compare_records(*self.records)

    def test_contour_range_cannot_change(self):
        self.corrupt(2,44,2)
        with self.assertRaisesRegex(ValueError,'contour'):compare_records(*self.records)

    def test_different_source_mode_rejected(self):
        self.corrupt(1,36,5)
        with self.assertRaisesRegex(ValueError,'metadata'):compare_records(*self.records)

    def test_late_truncated_record_rejected(self):
        self.records[1]['segmentRecordBytes']=base64.b64encode(b'\0'*47).decode()
        with self.assertRaisesRegex(ValueError,'inventory'):compare_records(*self.records)

    def test_larger_mask_rejected(self):
        self.records[2]['scanMask'] |= 0x1000000
        with self.assertRaisesRegex(ValueError,'mask'):compare_records(*self.records)

    def test_result_cannot_claim_qualification(self):
        self.records[2]['qualified']=True
        with self.assertRaisesRegex(ValueError,'classification'):compare_records(*self.records)


if __name__=='__main__':unittest.main()
