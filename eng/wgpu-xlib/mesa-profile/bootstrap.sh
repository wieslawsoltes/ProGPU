#!/bin/sh
set -eu
export PROGPU_SETUP_STARTED="$(date +%s)"
timeout --signal=KILL 600s sh /inputs/setup.sh > /evidence/setup.log 2>&1
exec python3 /inputs/inside.py
