#!/usr/bin/env python3
"""Retain a Windows package process's full exit code before Git Bash narrows it."""
import subprocess
import sys

if len(sys.argv) < 2:
    raise SystemExit('Expected a native executable and its arguments.')
result = subprocess.run(sys.argv[1:])
unsigned_code = result.returncode & 0xFFFFFFFF
signed_code = unsigned_code if unsigned_code < 0x80000000 else unsigned_code - 0x100000000
if unsigned_code:
    print(f'Native package process failed: executable={sys.argv[1]}, '
          f'exit={signed_code}, hex=0x{unsigned_code:08x}', file=sys.stderr)
raise SystemExit(signed_code)
