#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BROWSER_GALLERY_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build-browser-gallery}"
publish_dir="${PROGPU_NATIVE_BROWSER_GALLERY_PUBLISH_DIR:-${repo_root}/artifacts/progpu-native/browser-gallery-aot}"

command -v emcmake >/dev/null 2>&1 || {
  echo "emcmake is required to publish the native C++ browser gallery." >&2
  exit 1
}

emcmake cmake \
  -S "${repo_root}/src/ProGPU.Native" \
  -B "${build_dir}" \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_TESTING=OFF \
  -DPROGPU_NATIVE_BUILD_SAMPLE=OFF
cmake --build "${build_dir}" \
  --target progpu_native_browser_gallery \
  --parallel

cmake -E make_directory "${publish_dir}"
for asset in \
  progpu_native_browser_gallery.html \
  progpu_native_browser_gallery.js \
  progpu_native_browser_gallery.wasm \
  progpu-browser-host.js; do
  cmake -E copy_if_different \
    "${build_dir}/${asset}" \
    "${publish_dir}/${asset}"
done

echo "Published pure C++20/WebAssembly AOT gallery to ${publish_dir}"
du -h "${publish_dir}"/*
