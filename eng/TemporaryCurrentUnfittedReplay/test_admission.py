"""CPU-only admission controls; synthetic private bytes are never font executions."""
from copy import deepcopy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT))
spec = importlib.util.spec_from_file_location('standalone64_admission_runner', ROOT / 'run.py')
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class FontAdmissionTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.private = self.root / 'private'
        self.private.mkdir()
        self.original = json.loads((ROOT / 'original-contract.json').read_text())
        # Preserve the real metadata shape and values; use tiny labelled fixture
        # bytes only for the independent filesystem/hash admission controls.
        for index, font in enumerate(self.original['fonts']):
            data = ('synthetic-admission-fixture-' + str(index)).encode()
            font['sha256'] = hashlib.sha256(data).hexdigest()
            font['bytes'] = len(data)
            (self.private / (font['sha256'] + '.ttf')).write_bytes(data)
        (self.root / 'original-contract.json').write_text(json.dumps(self.original))
        self.metadata = {
            'schema': 'original-font-only-v1', 'qualified': False,
            'inputEvents': 0, 'desktopWindows': 0,
            'text': 'Name Age 123 Hello Windows Forms',
            'loadedIdentity': {'architecture': 'X64'}, 'elapsedSeconds': 1.0,
            'smoothing': deepcopy(self.original['smoothing']),
            'fonts': deepcopy(self.original['fonts']),
        }
        self.path = self.root / 'fonts.json'

    def validate(self):
        self.path.write_text(json.dumps(self.metadata))
        with patch.object(runner, 'ROOT', self.root):
            return runner.validate_fonts(self.path, self.private)

    def rejected(self):
        with self.assertRaises((AssertionError, ValueError, KeyError, OSError)):
            self.validate()

    def test_exact_manifest_and_private_bytes_admitted_without_72_frames(self):
        result = self.validate()
        self.assertFalse(result['qualified'])
        self.assertNotIn('cases', result)
        self.assertNotIn('original72RGBA', result)

    def test_fabricated_success_or_reference_schema_rejected(self):
        for field, value in [('qualified', True), ('schema', 'original72-reference')]:
            with self.subTest(field=field):
                old = self.metadata[field]
                self.metadata[field] = value
                self.rejected()
                self.metadata[field] = old

    def test_font_identity_drift_rejected_independently(self):
        replacements = {'requested': 'Other', 'managed': 'Other', 'native': 'Other',
                        'points': 10.0, 'charset': 1, 'bytes': 99, 'sha256': 'f' * 64,
                        'gasp': {'available': False}}
        for index in range(2):
            for field, value in replacements.items():
                with self.subTest(font=index, field=field):
                    old = self.metadata['fonts'][index][field]
                    self.metadata['fonts'][index][field] = value
                    self.rejected()
                    self.metadata['fonts'][index][field] = old

    def test_each_raw_system_observation_and_availability_rejected(self):
        for index in range(4):
            for field in ('value', 'action', 'error', 'success'):
                with self.subTest(query=index, field=field):
                    row = self.metadata['smoothing'][index]
                    old = row[field]
                    row[field] = not old if isinstance(old, bool) else old + 1
                    self.rejected()
                    row[field] = old

    def test_missing_duplicate_reordered_or_extra_fonts_rejected(self):
        fonts = self.metadata['fonts']
        for variant in (fonts[:1], [fonts[0], fonts[0]], list(reversed(fonts)), fonts + [fonts[0]]):
            with self.subTest(fonts=variant):
                self.metadata['fonts'] = variant
                self.rejected()
        self.metadata['fonts'] = fonts

    def test_missing_or_extra_system_query_rejected(self):
        queries = self.metadata['smoothing']
        for variant in (queries[:-1], queries + [queries[0]], list(reversed(queries))):
            with self.subTest(queries=variant):
                self.metadata['smoothing'] = variant
                self.rejected()
        self.metadata['smoothing'] = queries

    def test_same_size_private_byte_substitution_rejected(self):
        path = self.private / (self.metadata['fonts'][0]['sha256'] + '.ttf')
        data = path.read_bytes()
        path.write_bytes(bytes([data[0] ^ 1]) + data[1:])
        self.rejected()

    def test_missing_private_font_rejected(self):
        (self.private / (self.metadata['fonts'][1]['sha256'] + '.ttf')).unlink()
        self.rejected()

    def test_symlink_private_font_rejected_even_with_correct_bytes(self):
        path = self.private / (self.metadata['fonts'][0]['sha256'] + '.ttf')
        target = self.root / 'borrowed.ttf'
        path.rename(target)
        path.symlink_to(target)
        self.rejected()

    def test_unexpected_private_file_or_directory_rejected(self):
        extra = self.private / 'unexpected'
        extra.write_bytes(b'extra')
        self.rejected()
        extra.unlink()
        extra.mkdir()
        self.rejected()

    def test_native_input_window_wrong_architecture_and_text_rejected(self):
        for field, value in [('inputEvents', 1), ('desktopWindows', 1), ('text', 'substituted')]:
            with self.subTest(field=field):
                old = self.metadata[field]
                self.metadata[field] = value
                self.rejected()
                self.metadata[field] = old
        self.metadata['loadedIdentity']['architecture'] = 'Arm64'
        self.rejected()

    def test_nonfinite_negative_and_over_deadline_rejected(self):
        for value in (float('nan'), float('inf'), -1, 45.0001):
            with self.subTest(seconds=value):
                self.metadata['elapsedSeconds'] = value
                self.rejected()
        self.metadata['elapsedSeconds'] = 45
        self.validate()


if __name__ == '__main__':
    unittest.main()
