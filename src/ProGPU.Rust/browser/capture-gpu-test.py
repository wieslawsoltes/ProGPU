"""Synthetic process-tree controls; never starts a debugger or reads real /proc."""

import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
spec = importlib.util.spec_from_file_location('capture_gpu', Path(__file__).with_name('capture-gpu.py'))
capture = importlib.util.module_from_spec(spec)
spec.loader.exec_module(capture)


class ProcessTreeTests(unittest.TestCase):
    def test_packed_title_requires_exact_image_prefix_and_role(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / 'browser with spaces'
            executable.write_bytes(b'browser')
            directory = root / '200'
            directory.mkdir()
            (directory / 'exe').symlink_to(executable)
            prefix = bytes(executable) + b' --type=gpu-process'
            for args in [prefix, prefix + b'\0', prefix + b'\0\0',
                         prefix + b' --value=private value\0',
                         bytes(executable) + b'\0--type=gpu-process\0']:
                with self.subTest(args=args):
                    (directory / 'cmdline').write_bytes(args)
                    result = capture.gpu_identity(200, executable, root)
                    self.assertIsNotNone(result)
                    self.assertNotIn('private', str(result))
            rejected = [b'', b'chrome --type=gpu-process\0',
                        prefix + b'-extra\0', prefix + b'\tprivate\0',
                        bytes(executable) + b' --value=--type=gpu-process\0',
                        bytes(executable) + b' --type=renderer --type=gpu-process\0',
                        bytes(executable) + b' --type=gpu-process\0--value=x\0',
                        bytes(executable) + b'\0--value=--type=gpu-process\0',
                        bytes(executable) + b'\0--type=renderer\0--type=gpu-process\0',
                        bytes(executable) + b'\0--type=gpu-process\0--type=gpu-process\0']
            for args in rejected:
                with self.subTest(args=args):
                    (directory / 'cmdline').write_bytes(args)
                    self.assertIsNone(capture.gpu_identity(200, executable, root))
            # A matching title cannot turn a different executable into Chrome.
            other = root / 'unrelated'
            other.write_bytes(b'different image')
            (directory / 'cmdline').write_bytes(prefix)
            (directory / 'exe').unlink()
            (directory / 'exe').symlink_to(other)
            self.assertIsNone(capture.gpu_identity(200, executable, root))

    def test_identity_is_rechecked_after_lookup_and_before_observation(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / 'chrome'
            executable.write_bytes(b'browser')
            for pid, parent in [(101, 1), (102, 101), (103, 102)]:
                directory = root / str(pid)
                directory.mkdir()
                fields = ['S', str(parent)] + ['0'] * 20
                fields[19] = str(pid * 99)
                (directory / 'stat').write_text(f'{pid} (chrome) ' + ' '.join(fields))
                (directory / 'cmdline').write_bytes(bytes(executable) +
                                                   (b' --type=gpu-process\0' if pid == 103 else b'\0'))
                (directory / 'exe').symlink_to(executable)
            source, = capture.gpu_processes(101, executable, root)
            self.assertEqual(source['selection'], 'packed-executable-prefix')
            self.assertTrue(capture.same_generation(source, 101, executable, root))
            stat = root / '103' / 'stat'
            original = stat.read_text()

            def replaced(pid, expected, proc):
                result = identity(pid, expected, proc)
                if pid == 103:
                    stat.write_text(original.replace('10197', '10198'))
                return result

            identity = capture.gpu_identity
            with patch.object(capture, 'gpu_identity', side_effect=replaced):
                self.assertEqual(capture.gpu_processes(101, executable, root), [])
            self.assertFalse(capture.same_generation(source, 101, executable, root))
            stat.write_text(original)
            with patch.object(capture, 'gpu_identity', side_effect=replaced):
                self.assertFalse(capture.same_generation(source, 101, executable, root))
            stat.write_text(original.replace(' S 102 ', ' S 1 '))
            self.assertFalse(capture.same_generation(source, 101, executable, root))
            stat.write_text(original)
            (root / '103' / 'cmdline').write_bytes(bytes(executable) + b' --type=renderer\0')
            self.assertFalse(capture.same_generation(source, 101, executable, root))
            (root / '103' / 'cmdline').write_bytes(bytes(executable) + b' --type=gpu-process\0')
            old_image = root / 'old-chrome'
            executable.rename(old_image)
            executable.write_bytes(b'new image')
            # Even an exec/replacement at the same PID and path must not reuse
            # the captured executable generation for debugger attachment.
            self.assertFalse(capture.same_generation(source, 101, executable, root))
            (root / '103' / 'exe').unlink()
            self.assertFalse(capture.same_generation(source, 101, executable, root))

    def test_only_current_descendant_gpu_is_selected(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            executable = root / "owned chrome"
            executable.write_bytes(b"browser executable")

            def process(pid, parent, args):
                directory = root / str(pid)
                directory.mkdir()
                fields = ['S', str(parent)] + ['0'] * 20
                fields[11], fields[12], fields[19] = '23', '17', str(pid * 99)
                (directory / 'stat').write_text(f'{pid} (gpu (worker)) ' + ' '.join(fields))
                (directory / 'cmdline').write_bytes(args)
                (directory / 'exe').symlink_to(executable)

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
            found = capture.gpu_processes(101, executable, root)
            self.assertEqual([item['pid'] for item in found], [103])
            self.assertEqual(found[0], dict(pid=103, name='gpu (worker)', state='S',
                                           parent=102, user_ticks=23, system_ticks=17,
                                           start_ticks=10197, selection='separate-argument',
                                           executable_device=executable.stat().st_dev,
                                           executable_inode=executable.stat().st_ino))
            self.assertFalse(capture.descendant(105, 101, root))
            self.assertFalse(capture.descendant(999, 101, root))
            # Classification diagnostics retain neither full argv nor unrelated
            # process identities. An unproven title is still not a GPU target.
            process(110, 102, b'chrome --type=gpu-process --user-data-dir=private\0')
            process(111, 102, b'chrome\0--type=utility\0private-value\0')
            process(112, 102, b'chrome\0')
            (root / '112' / 'cmdline').unlink()
            inventory = capture.owned_process_inventory(101, root)
            self.assertFalse(inventory['truncated'])
            by_id = {v['pid']: v for v in inventory['processes']}
            self.assertNotIn(104, by_id)
            self.assertEqual(by_id[103]['process_types'], ['gpu-process'])
            self.assertEqual(by_id[110]['process_types'], [])
            self.assertTrue(by_id[110]['embedded_gpu_marker'])
            self.assertEqual(by_id[112]['command_line_status'], 'FileNotFoundError')
            self.assertNotIn('private', str(inventory))
            self.assertEqual([v['pid'] for v in capture.gpu_processes(101, executable, root)], [103])
            self.assertEqual(len(capture.owned_process_inventory(101, root, 2)['processes']), 2)
            self.assertTrue(capture.owned_process_inventory(101, root, 2)['truncated'])
            # Reparenting an existing PID cannot keep its previous admission.
            text = (root / '103' / 'stat').read_text().replace(' S 102 ', ' S 1 ')
            (root / '103' / 'stat').write_text(text)
            self.assertEqual(capture.gpu_processes(101, executable, root), [])


if __name__ == '__main__':
    unittest.main()
