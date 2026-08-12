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
  --target progpu_native_dawn_header_contract \
  --config Release \
  --parallel

echo "Verified the shared ProGPU C++ renderer against WebScene's exact Dawn WebGPU header ${expected_commit}."
