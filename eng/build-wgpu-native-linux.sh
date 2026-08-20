#!/usr/bin/env bash
set -euo pipefail

# Builds the exact wgpu-native ABI consumed by Silk.NET.WebGPU 2.23.0 with
# ProGPU's reviewed Linux EGL compatibility patch. Upstream sources and Cargo
# caches remain external build inputs under artifacts/.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${WGPU_NATIVE_SOURCE:-${repo_root}/artifacts/wgpu-native-linux-src}"
output_dir="${WGPU_NATIVE_LINUX_OUTPUT:-${repo_root}/artifacts/wgpu-native-linux}"
target_dir="${WGPU_NATIVE_LINUX_TARGET:-${repo_root}/artifacts/wgpu-native-linux-build}"
cargo_home="${WGPU_NATIVE_LINUX_CARGO_HOME:-${repo_root}/artifacts/wgpu-native-linux-cargo}"
patch_file="${repo_root}/eng/patches/wgpu-0.19-linux-egl-fallback.patch"

native_commit="33133da4ec5a0174cb21539ef2d3346f75200411"
wgpu_commit="87576b72b37c6b78b41104eb25fc31893af94092"
upstream_url="https://github.com/gfx-rs/wgpu-native.git"

for required_tool in cargo gcc git nm readelf rustc strip; do
  if ! command -v "${required_tool}" >/dev/null 2>&1; then
    echo "Required tool not found: ${required_tool}" >&2
    exit 1
  fi
done

case "${output_dir}" in
  ""|/|"${repo_root}"|"${source_dir}"|"${target_dir}"|"${cargo_home}")
    echo "Unsafe WGPU_NATIVE_LINUX_OUTPUT: ${output_dir}" >&2
    exit 2
    ;;
esac

case "$(uname -m)" in
  x86_64) runtime_id="linux-x64" ;;
  aarch64|arm64) runtime_id="linux-arm64" ;;
  *)
    echo "Unsupported Linux architecture: $(uname -m)" >&2
    exit 2
    ;;
esac

if [[ ! -d "${source_dir}/.git" ]]; then
  git clone --filter=blob:none "${upstream_url}" "${source_dir}"
fi

if [[ -n "$(git -C "${source_dir}" status --porcelain --untracked-files=no)" ]]; then
  echo "Refusing to change a modified external wgpu-native checkout: ${source_dir}" >&2
  exit 1
fi

git -C "${source_dir}" fetch --depth 1 origin "${native_commit}"
if [[ "$(git -C "${source_dir}" rev-parse HEAD)" != "${native_commit}" ]]; then
  git -C "${source_dir}" checkout --detach "${native_commit}"
fi
git -C "${source_dir}" submodule update --init --depth 1 ffi/webgpu-headers

mkdir -p "${cargo_home}" "${target_dir}"
export CARGO_HOME="${cargo_home}"
export CARGO_NET_GIT_FETCH_WITH_CLI=true
export CARGO_TARGET_DIR="${target_dir}"
export CARGO_INCREMENTAL=0
export CARGO_PROFILE_RELEASE_CODEGEN_UNITS=1
export CARGO_PROFILE_RELEASE_LTO=thin

cargo fetch --manifest-path "${source_dir}/Cargo.toml" --locked

wgpu_checkout=""
while IFS= read -r candidate; do
  if [[ "$(git -C "${candidate}" rev-parse HEAD 2>/dev/null || true)" == "${wgpu_commit}" ]]; then
    wgpu_checkout="${candidate}"
    break
  fi
done < <(find "${cargo_home}/git/checkouts" -mindepth 2 -maxdepth 2 -type d -name "${wgpu_commit:0:7}" -print)

if [[ -z "${wgpu_checkout}" ]]; then
  echo "Cargo did not fetch the pinned wgpu checkout ${wgpu_commit}." >&2
  exit 1
fi

if git -C "${wgpu_checkout}" apply --check "${patch_file}"; then
  git -C "${wgpu_checkout}" apply "${patch_file}"
elif ! git -C "${wgpu_checkout}" apply --reverse --check "${patch_file}"; then
  echo "The pinned wgpu checkout contains changes other than the reviewed EGL patch." >&2
  exit 1
fi

gcc_include="$(gcc -print-file-name=include)"
target_include="/usr/include/$(gcc -dumpmachine)"
bindgen_args="-I${gcc_include} -I/usr/include"
if [[ -d "${target_include}" ]]; then
  bindgen_args+=" -I${target_include}"
fi
export BINDGEN_EXTRA_CLANG_ARGS="${BINDGEN_EXTRA_CLANG_ARGS:-${bindgen_args}}"

if [[ -z "${LIBCLANG_PATH:-}" ]]; then
  for candidate in /usr/lib/llvm-*/lib; do
    if [[ -e "${candidate}/libclang.so" || -e "${candidate}/libclang.so.1" ]]; then
      export LIBCLANG_PATH="${candidate}"
      break
    fi
  done
fi

base_rustflags="--remap-path-prefix=${source_dir}=wgpu-native --remap-path-prefix=${wgpu_checkout}=wgpu -C link-arg=-Wl,--build-id=sha1 -C link-arg=-Wl,-soname,libwgpu_native.so"
export RUSTFLAGS="${RUSTFLAGS:+${RUSTFLAGS} }${base_rustflags}"
export SOURCE_DATE_EPOCH="$(git -C "${source_dir}" show -s --format=%ct "${native_commit}")"
export TZ=UTC

cargo build \
  --manifest-path "${source_dir}/Cargo.toml" \
  --release \
  --locked \
  --no-default-features \
  --features wgsl

source_library="${target_dir}/release/libwgpu_native.so"
destination_dir="${output_dir}/runtimes/${runtime_id}/native"
destination_library="${destination_dir}/libwgpu_native.so"
if [[ ! -f "${source_library}" ]]; then
  echo "Cargo completed without producing ${source_library}." >&2
  exit 1
fi

mkdir -p "${destination_dir}" "${output_dir}/include" "${output_dir}/licenses"
install -m 0755 "${source_library}" "${destination_library}"
strip --strip-unneeded "${destination_library}"
install -m 0644 "${source_dir}/ffi/wgpu.h" "${output_dir}/include/wgpu.h"
install -m 0644 "${source_dir}/ffi/webgpu-headers/webgpu.h" "${output_dir}/include/webgpu.h"
install -m 0644 "${source_dir}/LICENSE.APACHE" "${output_dir}/licenses/LICENSE.APACHE"
install -m 0644 "${source_dir}/LICENSE.MIT" "${output_dir}/licenses/LICENSE.MIT"

if ! nm -D --defined-only "${destination_library}" |
    awk '$NF == "wgpuCreateInstance" { found = 1 } END { exit !found }'; then
  echo "Packaged library does not export the WebGPU C ABI." >&2
  exit 1
fi
if ! readelf -d "${destination_library}" |
    awk '/\(SONAME\)/ && /\[libwgpu_native\.so\]/ { found = 1 } END { exit !found }'; then
  echo "Packaged library has no libwgpu_native.so SONAME." >&2
  exit 1
fi

patch_sha256="$(sha256sum "${patch_file}" | awk '{print $1}')"
rust_version="$(rustc --version)"
{
  printf 'wgpu-native-commit=%s\n' "${native_commit}"
  printf 'wgpu-commit=%s\n' "${wgpu_commit}"
  printf 'egl-patch-sha256=%s\n' "${patch_sha256}"
  printf 'silk-net-webgpu-abi=2.23.0\n'
  printf 'runtime-id=%s\n' "${runtime_id}"
  printf 'cargo-features=wgsl\n'
  printf 'rust-version=%s\n' "${rust_version}"
} > "${output_dir}/BUILD-MANIFEST.txt"

checksum_file="${output_dir}/SHA256SUMS"
checksum_temp="${checksum_file}.tmp"
: > "${checksum_temp}"
while IFS= read -r relative_path; do
  digest="$(sha256sum "${output_dir}/${relative_path}" | awk '{print $1}')"
  printf '%s  %s\n' "${digest}" "${relative_path}" >> "${checksum_temp}"
done < <(
  cd "${output_dir}"
  find BUILD-MANIFEST.txt include licenses runtimes -type f -print | LC_ALL=C sort
)
mv "${checksum_temp}" "${checksum_file}"

echo "Created Linux wgpu-native package at ${output_dir} from ${native_commit}."
echo "Runtime: ${runtime_id}; wgpu patch: ${patch_sha256}"
