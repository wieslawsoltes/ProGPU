#!/bin/sh
# Regenerates src/progpu_native.rs, src/webgpu.rs, src/scene_builder.rs and src/font.rs with bindgen (cargo install
# bindgen-cli): plain extern declarations of the statically linked engine and wgpu-native. headers/wgpu.h
# is that of FerroUI/wgpu-native, branch progpu/wgpu-30 (the native-object interop); progpu_native_dawn.h
# (which includes progpu_native.h) is the engine of the standard webgpu.h with a procedure resolver.
# INCLUDE is the include directory of the C ABI: by default that of the ProGPU checkout this crate
# is part of (src/ProGPU.Native/include).
set -eu
cd "$(dirname "$0")"
INCLUDE="${INCLUDE:-../../../src/ProGPU.Native/include}"
bindgen "$INCLUDE/progpu_native_dawn.h" --allowlist-function 'progpu_native_.*' --allowlist-type 'progpu_native_.*' \
  --allowlist-var 'PROGPU_NATIVE_.*' --default-enum-style consts --no-prepend-enum-name \
  --no-doc-comments --use-core --merge-extern-blocks -o src/progpu_native.rs -- -I"$INCLUDE" -std=c11
bindgen headers/wgpu.h --allowlist-function 'wgpu.*' --allowlist-type 'WGPU.*' --allowlist-var 'WGPU.*' \
  --default-enum-style consts --no-prepend-enum-name --no-doc-comments --use-core \
  -o src/webgpu.rs -- -Iheaders
bindgen native/progpu_native_scene_builder_c.h --allowlist-function 'progpu_native_scene_builder_.*' \
  --allowlist-type 'progpu_native_scene_builder' --no-recursive-allowlist --raw-line 'use crate::progpu::*;' \
  --no-doc-comments --use-core -o src/scene_builder.rs -- -I"$INCLUDE" -Inative
bindgen native/progpu_native_font_c.h --allowlist-function 'progpu_native_font_(catalog|face|data|outline)_.*' \
  --allowlist-type 'progpu_native_font_(catalog|face|style|face_info|face_metrics|glyph_bounds|outline_growth)' \
  --allowlist-var 'PROGPU_NATIVE_FONT_(SLANT|OUTLINE)_.*' --no-recursive-allowlist \
  --raw-line 'use crate::progpu::*;' --no-doc-comments --use-core -o src/font.rs -- -I"$INCLUDE" -Inative
