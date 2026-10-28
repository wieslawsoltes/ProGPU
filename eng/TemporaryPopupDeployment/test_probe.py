import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock
import run

class ProbePreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.canonical=subprocess.check_output(['git','show',run.PRODUCT+':'+run.MANIFEST],
            cwd=os.environ.get('PROBE_SOURCE_REPO'))

    def test_exact_lf_and_git_crlf_are_distinct_original_transports(self):
        lf=run.validate_manifest(self.canonical,self.canonical)
        crlf=run.validate_manifest(self.canonical,self.canonical.replace(b'\n',b'\r\n'))
        self.assertEqual(lf['blobSha256'],crlf['blobSha256'])
        self.assertNotEqual(lf['checkoutSha256'],crlf['checkoutSha256'])
        self.assertEqual('original-LF',lf['checkoutRepresentation'])
        self.assertEqual('Git-CRLF',crlf['checkoutRepresentation'])

    def test_changed_blob_or_mixed_checkout_is_not_normalized_into_admission(self):
        for blob,checkout in ((self.canonical+b' ',self.canonical),
                              (self.canonical,self.canonical.replace(b'\n',b'\r\n',1)),
                              (self.canonical,b'\xef\xbb\xbf'+self.canonical),
                              (self.canonical,self.canonical.replace(b'6.0.0.0',b'5.0.0.0'))):
            with self.subTest(checkout=checkout[:20]),self.assertRaises(ValueError):run.validate_manifest(blob,checkout)

    def test_publish_timeout_preserves_receipt_and_targets_only_owned_tree(self):
        with tempfile.TemporaryDirectory() as directory:
            root=Path(directory);process=mock.Mock(pid=12345)
            process.wait.side_effect=[subprocess.TimeoutExpired(['dotnet','publish'],600),124]
            process.poll.return_value=None
            with mock.patch.object(run.subprocess,'Popen',return_value=process),mock.patch.object(run.subprocess,'run') as kill:
                receipt=run.publish(['dotnet','publish'],root)
            self.assertTrue(receipt['timedOut']);self.assertEqual(124,receipt['exitCode'])
            self.assertTrue(receipt['ownedTreeCleanupAttempted'])
            self.assertEqual(receipt,json.loads((root/'publish.json').read_text()))
            kill.assert_called_once_with(['taskkill','/PID','12345','/T','/F'],capture_output=True,timeout=5)

    def test_publish_failure_and_success_retain_exact_exit_without_cleanup(self):
        for exit_code in (0,1):
            with self.subTest(exit_code=exit_code),tempfile.TemporaryDirectory() as directory:
                process=mock.Mock(pid=1);process.wait.return_value=exit_code;process.poll.return_value=exit_code
                with mock.patch.object(run.subprocess,'Popen',return_value=process),mock.patch.object(run.subprocess,'run') as kill:
                    receipt=run.publish(['dotnet','publish'],Path(directory))
                self.assertEqual(exit_code,receipt['exitCode']);self.assertFalse(receipt['timedOut']);kill.assert_not_called()

if __name__=='__main__':unittest.main()
