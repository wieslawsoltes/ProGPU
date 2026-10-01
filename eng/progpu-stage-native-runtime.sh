#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
package_root="${PROGPU_NATIVE_PACKAGE_ROOT:-${repo_root}/artifacts/progpu-native/package}"
font_manifest=
edit_word_icu_archive=
while [[ "$#" -ge 2 ]]; do
  case "$1" in
    --font-manifest) font_manifest="$2"; shift 2 ;;
    --edit-word-icu-archive) edit_word_icu_archive="$2"; shift 2 ;;
    *) break ;;
  esac
done
if [[ "$#" != 0 ]]; then
  echo "Usage: $0 [--font-manifest absolute-producer-receipt] [--edit-word-icu-archive absolute-pinned-archive]" >&2
  exit 2
fi

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
  libprogpu_native_mil.a
  libprogpu_native_direct2d_core.a
  libprogpu_native_text.a
  libprogpu_native_scene_builder.a
)
for sdk_library in "${sdk_libraries[@]}"; do
  if [[ ! -f "${build_dir}/${sdk_library}" ]]; then
    echo "Native C++ SDK build output is missing: ${build_dir}/${sdk_library}" >&2
    exit 1
  fi
done

if [[ -n "${font_manifest}" ]]; then
  python3 "${repo_root}/eng/progpu-verify-freetype.py" --manifest "${font_manifest}" --rid "${rid}" \
    --build-directory "${build_dir}" > /dev/null
fi
if [[ -n "${edit_word_icu_archive}" || -f "${build_dir}/progpu-native-edit-word-dependency.json" ]]; then
  python3 "${repo_root}/eng/progpu-edit-word-icu-dependency.py" stage \
    --source-archive "${edit_word_icu_archive}" --rid "${rid}" --build-directory "${build_dir}"
fi

destination="${package_root}/runtimes/${rid}/native"
sdk_destination="${destination}/sdk"
mkdir -p "${destination}"
mkdir -p "${sdk_destination}"
cp "${source_library}" "${destination}/$(basename "${source_library}")"
cp "${dawn_library}" "${destination}/$(basename "${dawn_library}")"
for sdk_library in "${sdk_libraries[@]}"; do
  cp "${build_dir}/${sdk_library}" "${sdk_destination}/${sdk_library}"
done
if [[ -n "${font_manifest}" ]]; then
  python3 "${repo_root}/eng/progpu-verify-freetype.py" --manifest "${font_manifest}" --rid "${rid}" \
    --native-destination "${destination}" --build-directory "${build_dir}"
fi
if [[ -n "${edit_word_icu_archive}" || -f "${build_dir}/progpu-native-edit-word-dependency.json" ]]; then
  python3 "${repo_root}/eng/progpu-edit-word-icu-dependency.py" stage \
    --source-archive "${edit_word_icu_archive}" --rid "${rid}" \
    --native-destination "${destination}" --build-directory "${build_dir}"
fi

echo "Staged ProGPU native renderer and C++ SDK for ${rid}: ${destination}"
