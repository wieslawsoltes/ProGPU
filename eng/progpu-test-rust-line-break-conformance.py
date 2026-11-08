#!/usr/bin/env python3
"""Run the Rust line-break segmenter against hash-pinned Unicode 17 conformance data.

Downloads are temporary and removed on success/failure. The official files retain
Unicode's terms of use; no reference engine or test data is copied into the crate.
This checks default line opportunities, not line fitting, shaping or text pixels.
"""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
FILES = {
    'LineBreakTest.txt': 'e69884e0dde6a8724873f885d68c52dc14518abf9ae4ca9e2283b8773db3b752',
}

def main():
    with tempfile.TemporaryDirectory(prefix='progpu-line-break-conformance-') as directory:
        paths = []
        for name, expected in FILES.items():
            url = f'https://www.unicode.org/Public/17.0.0/ucd/auxiliary/{name}'
            with urllib.request.urlopen(url, timeout=30) as response:
                data = response.read(32 * 1024 * 1024 + 1)
            actual = hashlib.sha256(data).hexdigest()
            if len(data) > 32 * 1024 * 1024 or actual != expected:
                raise ValueError(f'Unqualified Unicode fixture {url}: {actual}')
            path = Path(directory) / name
            path.write_bytes(data)
            paths.append(str(path))
            print(f'{url} sha256={actual}', flush=True)
        subprocess.run(['cargo', 'run', '--locked', '--release', '--no-default-features',
                        '--example', 'line_break_conformance', '--', *paths],
                       cwd=ROOT, check=True)

if __name__ == '__main__':
    main()
