#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
package_root="${PROGPU_NATIVE_PACKAGE_ROOT:-${repo_root}/artifacts/progpu-native/package}"

case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)
    rid="osx-arm64"
    source_library="${build_dir}/libprogpu_native.dylib"
    dawn_library="${build_dir}/libprogpu_native_dawn.dylib"
    ;;
  Darwin-x86_64)
    rid="osx-x64"
    source_library="${build_dir}/libprogpu_native.dylib"
    dawn_library="${build_dir}/libprogpu_native_dawn.dylib"
    ;;
  Linux-x86_64)
    rid="linux-x64"
    source_library="${build_dir}/libprogpu_native.so"
    dawn_library="${build_dir}/libprogpu_native_dawn.so"
    ;;
  Linux-aarch64|Linux-arm64)
    rid="linux-arm64"
    source_library="${build_dir}/libprogpu_native.so"
    dawn_library="${build_dir}/libprogpu_native_dawn.so"
    ;;
  *)
    echo "Unsupported native package host $(uname -s)-$(uname -m)." >&2
    exit 1
    ;;
esac

if [[ ! -f "${source_library}" || ! -f "${dawn_library}" ]]; then
  echo "Native renderer build outputs are missing." >&2
  echo "Expected ${source_library} and ${dawn_library}." >&2
  exit 1
fi

sdk_libraries=(
  libprogpu_native_compression.a
  libprogpu_native_hit_testing.a
  libprogpu_native_image.a
  libprogpu_native_text.a
  libprogpu_native_scene_builder.a
)
for sdk_library in "${sdk_libraries[@]}"; do
  if [[ ! -f "${build_dir}/${sdk_library}" ]]; then
    echo "Native C++ SDK build output is missing: ${build_dir}/${sdk_library}" >&2
    exit 1
  fi
done

destination="${package_root}/runtimes/${rid}/native"
sdk_destination="${destination}/sdk"
mkdir -p "${destination}"
mkdir -p "${sdk_destination}"
cp "${source_library}" "${destination}/$(basename "${source_library}")"
cp "${dawn_library}" "${destination}/$(basename "${dawn_library}")"
for sdk_library in "${sdk_libraries[@]}"; do
  cp "${build_dir}/${sdk_library}" "${sdk_destination}/${sdk_library}"
done

echo "Staged ProGPU native renderer and C++ SDK for ${rid}: ${destination}"
