#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
    echo "usage: $0 APP_PATH OUTPUT_PATH EXPECTED [TIMEOUT_SECONDS]" >&2
    exit 2
fi

app_path=$1
output_path=$2
expected=$3
timeout_seconds=${4:-60}
display=${DISPLAY:-:0}
xauthority=${XAUTHORITY:-}
if [[ -z $xauthority ]]; then
    for candidate in /run/user/$(id -u)/.mutter-Xwaylandauth.*; do
        if [[ -f $candidate ]]; then
            xauthority=$candidate
            break
        fi
    done
fi
if [[ -z $xauthority ]]; then
    echo "XAUTHORITY is required for the XWayland session." >&2
    exit 3
fi

export DISPLAY=$display
export XAUTHORITY=$xauthority
export XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-/run/user/$(id -u)}
export PROGPU_AVALONIA_INPUT_OUTPUT=$output_path
export PROGPU_AVALONIA_INPUT_EXPECT=$expected
export PROGPU_AVALONIA_INPUT_TIMEOUT_SECONDS=$timeout_seconds
unset WAYLAND_DISPLAY

set +e
"$app_path" >"$output_path.stdout.log" 2>&1
status=$?
set -e
printf '%s\n' "$status" >"$output_path.exit-code"
exit "$status"
