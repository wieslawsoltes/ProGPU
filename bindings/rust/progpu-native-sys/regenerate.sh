#!/bin/sh
# Regenerates src/progpu_native.rs and src/webgpu.rs with bindgen (cargo install bindgen-cli).
# INCLUDE is the include directory of the ProGPU native SDK of this crate's version.
set -eu
cd "$(dirname "$0")"
INCLUDE="${INCLUDE:?set INCLUDE to the SDK include directory}"
bindgen "$INCLUDE/progpu_native.h" --allowlist-function 'progpu_native_.*' --allowlist-type 'progpu_native_.*' \
  --allowlist-var 'PROGPU_NATIVE_.*' --dynamic-loading ProGPUNativeLibrary --default-enum-style consts --no-prepend-enum-name \
  --no-doc-comments --use-core --merge-extern-blocks -o src/progpu_native.rs -- -I"$INCLUDE" -std=c11
bindgen headers/wgpu.h --allowlist-function 'wgpu.*' --allowlist-type 'WGPU.*' --allowlist-var 'WGPU.*' \
  --dynamic-loading WgpuNativeLibrary --default-enum-style consts --no-prepend-enum-name --no-doc-comments --use-core \
  -o src/webgpu.rs -- -Iheaders
