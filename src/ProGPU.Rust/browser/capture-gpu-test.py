"""Synthetic process-tree controls; never starts a debugger or reads real /proc."""

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('capture_gpu', Path(__file__).with_name('capture-gpu.py'))
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class ProcessTreeTests(unittest.TestCase):
    def test_only_current_descendant_gpu_is_selected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)

            def process(pid, parent, args):
                directory = root / str(pid)
                directory.mkdir()
                fields = ['S', str(parent)] + ['0'] * 20
                fields[11], fields[12], fields[19] = '23', '17', str(pid * 99)
                (directory / 'stat').write_text(f'{pid} (gpu (worker)) ' + ' '.join(fields))
                (directory / 'cmdline').write_bytes(args)

            process(101, 1, b'node\0')
            process(102, 101, b'chrome\0')
            process(103, 102, b'chrome\0--type=gpu-process\0')
            process(104, 1, b'chrome\0--type=gpu-process\0')  # Unrelated browser.
            process(105, 106, b'chrome\0--type=gpu-process\0')
            process(106, 105, b'chrome\0')  # Malformed ancestry cycle.
            process(107, 102, b'chrome\0--type=renderer\0')
            process(108, 102, b'chrome\0--type=gpu-process-extra\0')
            process(109, 102, b'chrome\0--type=gpu-process\0')
            (root / '109' / 'stat').unlink()  # Retired during enumeration.
            found = capture.gpu_processes(101, root)
            self.assertEqual([item['pid'] for item in found], [103])
            self.assertEqual(found[0], dict(pid=103, name='gpu (worker)', state='S',
                                           parent=102, user_ticks=23, system_ticks=17,
                                           start_ticks=10197))
            self.assertFalse(capture.descendant(105, 101, root))
            self.assertFalse(capture.descendant(999, 101, root))
            # Reparenting an existing PID cannot keep its previous admission.
            text = (root / '103' / 'stat').read_text().replace(' S 102 ', ' S 1 ')
            (root / '103' / 'stat').write_text(text)
            self.assertEqual(capture.gpu_processes(101, root), [])


if __name__ == '__main__':
    unittest.main()
