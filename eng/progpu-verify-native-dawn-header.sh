#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${1:-${repo_root}/artifacts/progpu-native/build}"
source_dir="${PROGPU_NATIVE_DAWN_HEADER_SOURCE:-${repo_root}/artifacts/webgpu-headers-dawn}"
version_manifest="${repo_root}/eng/progpu-native-dawn.version.json"
expected_commit="$(awk -F'"' '/"webGpuHeadersRevision"/ { print $4; exit }' "${version_manifest}")"
upstream_url="$(awk -F'"' '/"webGpuHeadersRepository"/ { print $4; exit }' "${version_manifest}")"
if [[ ! "${expected_commit}" =~ ^[0-9a-f]{40}$ || -z "${upstream_url}" ]]; then
  echo "Invalid Dawn WebGPU header version manifest: ${version_manifest}" >&2
  exit 1
fi
if [[ ! -f "${build_dir}/CMakeCache.txt" ]]; then
  echo "Configure the native wgpu build first with ./eng/build-progpu-native.sh." >&2
  echo "Missing CMake cache: ${build_dir}/CMakeCache.txt" >&2
  exit 1
fi

if [[ -e "${source_dir}" && ! -d "${source_dir}/.git" ]]; then
  echo "Dawn WebGPU header source is not a Git checkout: ${source_dir}" >&2
  exit 1
fi
new_checkout=0
if [[ ! -d "${source_dir}/.git" ]]; then
  git clone --filter=blob:none --no-checkout "${upstream_url}" "${source_dir}"
  new_checkout=1
fi
if [[ "${new_checkout}" == "0" &&
      -n "$(git -C "${source_dir}" status --porcelain --untracked-files=no)" ]]; then
  echo "Refusing to change a modified Dawn WebGPU header checkout." >&2
  exit 1
fi
if ! git -C "${source_dir}" cat-file -e \
    "${expected_commit}^{commit}" 2>/dev/null; then
  git -C "${source_dir}" fetch --depth 1 origin "${expected_commit}"
fi
git -C "${source_dir}" checkout --detach "${expected_commit}"
actual_commit="$(git -C "${source_dir}" rev-parse HEAD)"
if [[ "${actual_commit}" != "${expected_commit}" ]]; then
  echo "Expected WebGPU headers ${expected_commit}, found ${actual_commit}." >&2
  exit 1
fi

cmake -S "${repo_root}/src/ProGPU.Native" -B "${build_dir}" \
  -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR="${source_dir}"
cmake --build "${build_dir}" \
  --target progpu_native_dawn progpu_native_dawn_contract_tests \
  --config Release \
  --parallel
ctest --test-dir "${build_dir}" -C Release --output-on-failure \
  -R '^progpu_native_dawn_contract_tests$'
PROGPU_NATIVE_BUILD_DIR="${build_dir}" \
PROGPU_NATIVE_LIBRARY_BASENAME="progpu_native_dawn" \
PROGPU_NATIVE_EXPORTS_FILE="${repo_root}/eng/progpu-native-dawn-exports.txt" \
  "${repo_root}/eng/progpu-verify-native-exports.sh"

case "$(uname -s)" in
  Darwin)
    dawn_library="${build_dir}/libprogpu_native_dawn.dylib"
    unresolved="$(nm -u "${dawn_library}")"
    ;;
  Linux)
    dawn_library="${build_dir}/libprogpu_native_dawn.so"
    unresolved="$(nm -D --undefined-only "${dawn_library}")"
    ;;
  *)
    echo "Unsupported Dawn import-verification host $(uname -s)." >&2
    exit 1
    ;;
esac
if grep -Eq '(^|[[:space:]_])wgpu[A-Z]' <<<"${unresolved}"; then
  echo "The Dawn adapter imports WebGPU procedures directly." >&2
  grep -E '(^|[[:space:]_])wgpu[A-Z]' <<<"${unresolved}" >&2
  exit 1
fi

echo "Verified the provider-resolved ProGPU C++ renderer against WebScene's exact Dawn WebGPU header ${expected_commit}."
