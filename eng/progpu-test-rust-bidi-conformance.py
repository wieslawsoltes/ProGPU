#!/usr/bin/env python3
"""Run the Rust level resolver against hash-pinned Unicode 17 conformance data.

Downloads are temporary and removed on success/failure. The official files retain
Unicode's terms of use; no reference engine or test data is copied into the crate.
This checks levels, not the separate L2 visual ordering or rendered text gates.
"""
import hashlib
from pathlib import Path
import subprocess
import tempfile
import urllib.request

ROOT = Path(__file__).resolve().parents[1]
FILES = {
    'BidiCharacterTest.txt': 'a3e6e905ab5afbe318a96df5401d0372a04cd73ef139ab5e3cf0ae241c255488',
    'BidiTest.txt': '888bdfc8090652272d1f859cdb00ae659e2dc6c26740be61ef1d03998a687620',
}

def main():
    with tempfile.TemporaryDirectory(prefix='progpu-bidi-conformance-') as directory:
        paths = []
        for name, expected in FILES.items():
            url = f'https://www.unicode.org/Public/17.0.0/ucd/{name}'
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
                        '--example', 'bidi_levels_conformance', '--', *paths],
                       cwd=ROOT, check=True)

if __name__ == '__main__':
    main()
