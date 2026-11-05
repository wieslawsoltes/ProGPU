#!/bin/sh
# Regenerates src/progpu_native.rs and src/webgpu.rs with bindgen (cargo install bindgen-cli).
# INCLUDE is the include directory of the C ABI: by default that of the ProGPU checkout this crate
# is part of (src/ProGPU.Native/include).
set -eu
cd "$(dirname "$0")"
INCLUDE="${INCLUDE:-../../../src/ProGPU.Native/include}"
bindgen "$INCLUDE/progpu_native.h" --allowlist-function 'progpu_native_.*' --allowlist-type 'progpu_native_.*' \
  --allowlist-var 'PROGPU_NATIVE_.*' --dynamic-loading ProGPUNativeLibrary --default-enum-style consts --no-prepend-enum-name \
  --no-doc-comments --use-core --merge-extern-blocks -o src/progpu_native.rs -- -I"$INCLUDE" -std=c11
bindgen headers/wgpu.h --allowlist-function 'wgpu.*' --allowlist-type 'WGPU.*' --allowlist-var 'WGPU.*' \
  --dynamic-loading WgpuNativeLibrary --default-enum-style consts --no-prepend-enum-name --no-doc-comments --use-core \
  -o src/webgpu.rs -- -Iheaders
bindgen native/progpu_native_scene_builder_c.h --allowlist-function 'progpu_native_scene_builder_.*' \
  --allowlist-type 'progpu_native_scene_builder' --no-recursive-allowlist --raw-line 'use crate::progpu::*;' \
  --no-doc-comments --use-core -o src/scene_builder.rs -- -I"$INCLUDE" -Inative
