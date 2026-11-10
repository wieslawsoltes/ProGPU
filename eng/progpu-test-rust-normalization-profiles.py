#!/usr/bin/env python3
"""Independent pinned canonical conformance for every explicit source profile."""
from pathlib import Path
import hashlib
import subprocess
import tempfile
import urllib.request
ROOT=Path(__file__).resolve().parents[1]
FILES={
 '15.1.0':('151','871238e37e3be0696ec2bd0891119a041b052da1a84485eda05a5438724b223e'),
 '16.0.0':('160','d811971453e7075e1ad56fb1b301eece5aa80757b81f6156e74a1bfb3ae5ceb1'),
 '17.0.0':('170','5019ffd530751a741900c849c0e010332f142a3612234639bd200b82138a87db'),
}
def main():
    with tempfile.TemporaryDirectory(prefix='progpu-normalization-profiles-') as temp:
        for version,(profile,digest) in FILES.items():
            url=f'https://www.unicode.org/Public/{version}/ucd/NormalizationTest.txt'
            with urllib.request.urlopen(url,timeout=30) as response: data=response.read(32*1024*1024+1)
            if len(data)>32*1024*1024 or hashlib.sha256(data).hexdigest()!=digest:raise ValueError(f'Unqualified fixture {url}')
            path=Path(temp)/'NormalizationTest.txt';path.write_bytes(data)
            print(f'{version} sha256={digest}',flush=True)
            subprocess.run(['cargo','run','--locked','--release','--no-default-features','--example','normalization_conformance','--',str(path),profile],cwd=ROOT,check=True)
if __name__=='__main__':main()
