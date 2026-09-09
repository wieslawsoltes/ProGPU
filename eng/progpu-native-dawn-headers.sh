#!/usr/bin/env bash

# Shared dependency preparation only. Neither caller may replace the pinned
# header with a similarly named system header. No compiler or verifier runs here.
progpu_prepare_native_dawn_headers() {
  local source_dir="$1"
  local version_manifest="$2"
  local expected_commit upstream_url actual_commit
  expected_commit="$(awk -F'"' '/"webGpuHeadersRevision"/ { print $4; exit }' "${version_manifest}")"
  upstream_url="$(awk -F'"' '/"webGpuHeadersRepository"/ { print $4; exit }' "${version_manifest}")"
  if [[ ! "${expected_commit}" =~ ^[0-9a-f]{40}$ || -z "${upstream_url}" ]]; then
    echo "Invalid Dawn WebGPU header version manifest: ${version_manifest}" >&2
    return 1
  fi
  if [[ -e "${source_dir}" && ! -d "${source_dir}/.git" ]]; then
    echo "Dawn WebGPU header source is not a Git checkout: ${source_dir}" >&2
    return 1
  fi
  if [[ ! -d "${source_dir}/.git" ]]; then
    git clone --filter=blob:none --no-checkout "${upstream_url}" "${source_dir}"
  elif [[ -n "$(git -C "${source_dir}" status --porcelain --untracked-files=no)" ]]; then
    echo "Refusing to change a modified Dawn WebGPU header checkout." >&2
    return 1
  fi
  if ! git -C "${source_dir}" cat-file -e "${expected_commit}^{commit}" 2>/dev/null; then
    git -C "${source_dir}" fetch --depth 1 origin "${expected_commit}"
  fi
  git -C "${source_dir}" checkout --detach "${expected_commit}"
  actual_commit="$(git -C "${source_dir}" rev-parse HEAD)"
  if [[ "${actual_commit}" != "${expected_commit}" ]]; then
    echo "Expected WebGPU headers ${expected_commit}, found ${actual_commit}." >&2
    return 1
  fi
}
