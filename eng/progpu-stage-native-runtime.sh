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

destination="${package_root}/runtimes/${rid}/native"
mkdir -p "${destination}"
cp "${source_library}" "${destination}/$(basename "${source_library}")"
cp "${dawn_library}" "${destination}/$(basename "${dawn_library}")"

echo "Staged ProGPU native renderer for ${rid}: ${destination}"
