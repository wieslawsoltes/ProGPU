#!/usr/bin/env bash
set -euo pipefail

# Builds the exact Dawn C ABI consumed by WebGPUSharp 0.5.5 as one Android
# Vulkan shared object. Source remains an external build input under artifacts/
# and is never vendored into ProGPU.

usage() {
  cat <<'EOF'
Usage: ./eng/build-webgpu-dawn-android.sh [arm64|x64|all] [--api LEVEL]

Builds arm64-v8a by default. Pass x64 for an Android emulator library or all
for both ABIs. ANDROID_NDK_ROOT (or ANDROID_NDK_HOME) must identify Android
NDK r27 or newer. If neither is set, the script searches the ndk/ directory
below ANDROID_SDK_ROOT or ANDROID_HOME.

Environment overrides:
  ANDROID_API_LEVEL      Minimum Android API level (default: 30)
  DAWN_SOURCE            External pinned Dawn checkout
  DAWN_ANDROID_BUILD_ROOT
                         Per-ABI CMake/Ninja build directories
  DAWN_ANDROID_OUTPUT    Packaged headers, library, licenses, and manifest
EOF
}

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${DAWN_SOURCE:-${repo_root}/artifacts/dawn-webgpusharp-0.5.5-src}"
build_root="${DAWN_ANDROID_BUILD_ROOT:-${repo_root}/artifacts/dawn-webgpusharp-0.5.5-android-build}"
output_dir="${DAWN_ANDROID_OUTPUT:-${repo_root}/artifacts/dawn-android}"
wrapper_dir="${repo_root}/eng/dawn-android"
android_api_level="${ANDROID_API_LEVEL:-30}"
expected_commit="01249a97332468dbdd6cf5edb8dd7bae77875de5"
expected_ref="refs/heads/chromium/7871_124"
upstream_url="https://dawn.googlesource.com/dawn"

requested_architectures=()
while (($# > 0)); do
  case "$1" in
    arm64|--arm64)
      requested_architectures+=("arm64")
      ;;
    x64|--x64)
      requested_architectures+=("x64")
      ;;
    all|--all)
      requested_architectures+=("arm64" "x64")
      ;;
    --api)
      if (($# < 2)); then
        echo "--api requires a numeric Android API level." >&2
        exit 2
      fi
      android_api_level="$2"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
  shift
done

if ((${#requested_architectures[@]} == 0)); then
  requested_architectures=("arm64")
fi

if [[ ! "${android_api_level}" =~ ^[0-9]+$ ]] ||
   ((android_api_level < 30)); then
  echo "Android API level must be an integer greater than or equal to 30." >&2
  exit 2
fi

case "${source_dir}" in
  ""|/|"${repo_root}"|"${build_root}"|"${output_dir}")
    echo "Unsafe DAWN_SOURCE: ${source_dir}" >&2
    exit 2
    ;;
esac
case "${build_root}" in
  ""|/|"${repo_root}"|"${source_dir}"|"${output_dir}")
    echo "Unsafe DAWN_ANDROID_BUILD_ROOT: ${build_root}" >&2
    exit 2
    ;;
esac
case "${output_dir}" in
  ""|/|"${repo_root}"|"${source_dir}"|"${build_root}")
    echo "Unsafe DAWN_ANDROID_OUTPUT: ${output_dir}" >&2
    exit 2
    ;;
esac

resolve_ndk_root() {
  local candidate=""
  local sdk_root=""
  local best=""

  for candidate in "${ANDROID_NDK_ROOT:-}" "${ANDROID_NDK_HOME:-}"; do
    if [[ -n "${candidate}" &&
          -f "${candidate}/build/cmake/android.toolchain.cmake" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done

  for sdk_root in "${ANDROID_SDK_ROOT:-}" "${ANDROID_HOME:-}"; do
    [[ -n "${sdk_root}" ]] || continue
    for candidate in "${sdk_root}"/ndk/*; do
      [[ -f "${candidate}/build/cmake/android.toolchain.cmake" ]] || continue
      if [[ -z "${best}" || "${candidate##*/}" > "${best##*/}" ]]; then
        best="${candidate}"
      fi
    done
    if [[ -n "${best}" ]]; then
      printf '%s\n' "${best}"
      return 0
    fi
  done

  return 1
}

if ! ndk_root="$(resolve_ndk_root)"; then
  echo "Android NDK not found. Set ANDROID_NDK_ROOT to NDK r27 or newer." >&2
  exit 1
fi

for required_tool in cmake git ninja python3; do
  if ! command -v "${required_tool}" >/dev/null 2>&1; then
    echo "Required tool not found: ${required_tool}" >&2
    exit 1
  fi
done

if [[ -e "${source_dir}" && ! -d "${source_dir}/.git" ]]; then
  echo "DAWN_SOURCE exists but is not a Git checkout: ${source_dir}" >&2
  exit 1
fi
if [[ ! -d "${source_dir}/.git" ]]; then
  mkdir -p "${source_dir}"
  git -C "${source_dir}" init
  git -C "${source_dir}" remote add origin "${upstream_url}"
fi
if [[ -n "$(git -C "${source_dir}" status --porcelain --untracked-files=no)" ]]; then
  echo "Refusing to change a modified external Dawn checkout: ${source_dir}" >&2
  exit 1
fi

if ! git -C "${source_dir}" cat-file -e \
    "${expected_commit}^{commit}" 2>/dev/null; then
  git -C "${source_dir}" fetch --depth 1 "${upstream_url}" "${expected_ref}"
fi
git -C "${source_dir}" checkout --detach "${expected_commit}"
python3 "${source_dir}/tools/fetch_dawn_dependencies.py" \
  --directory "${source_dir}"

actual_commit="$(git -C "${source_dir}" rev-parse HEAD)"
if [[ "${actual_commit}" != "${expected_commit}" ]]; then
  echo "Expected Dawn ${expected_commit}, found ${actual_commit}." >&2
  exit 1
fi

verify_webgpusharp_abi_header() {
  local header="$1"
  local declaration

  if [[ ! -f "${header}" ]]; then
    echo "Dawn did not generate the WebGPU C header: ${header}" >&2
    exit 1
  fi
  for declaration in \
    'WGPUSType_SurfaceSourceMetalLayer[[:space:]]*=[[:space:]]*0x0*4' \
    'WGPUSType_SurfaceSourceAndroidNativeWindow[[:space:]]*=[[:space:]]*0x0*8' \
    'WGPUSType_SharedTextureMemoryIOSurfaceDescriptor[[:space:]]*=[[:space:]]*0x0*50023' \
    'WGPUSType_SharedFenceMTLSharedEventDescriptor[[:space:]]*=[[:space:]]*0x0*50032'; do
    if ! grep -Eq "${declaration}" "${header}"; then
      echo "Generated Dawn header does not match WebGPUSharp 0.5.5: ${declaration}" >&2
      exit 1
    fi
  done
}

ndk_revision="unknown"
if [[ -f "${ndk_root}/source.properties" ]]; then
  ndk_revision="$(sed -n \
    's/^Pkg\.Revision[[:space:]]*=[[:space:]]*//p' \
    "${ndk_root}/source.properties" | head -n 1)"
fi
ndk_major="${ndk_revision%%.*}"
if [[ "${ndk_major}" =~ ^[0-9]+$ ]] && ((ndk_major < 27)); then
  echo "Android NDK r27 or newer is required; found ${ndk_revision}." >&2
  exit 1
fi

host_prebuilt=""
case "$(uname -s)" in
  Darwin)
    for candidate in darwin-arm64 darwin-x86_64; do
      if [[ -d "${ndk_root}/toolchains/llvm/prebuilt/${candidate}" ]]; then
        host_prebuilt="${candidate}"
        break
      fi
    done
    ;;
  Linux)
    for candidate in linux-x86_64 linux-aarch64; do
      if [[ -d "${ndk_root}/toolchains/llvm/prebuilt/${candidate}" ]]; then
        host_prebuilt="${candidate}"
        break
      fi
    done
    ;;
esac
if [[ -z "${host_prebuilt}" ]]; then
  echo "No compatible LLVM toolchain was found in ${ndk_root}." >&2
  exit 1
fi

llvm_bin="${ndk_root}/toolchains/llvm/prebuilt/${host_prebuilt}/bin"
llvm_nm="${llvm_bin}/llvm-nm"
llvm_readelf="${llvm_bin}/llvm-readelf"
llvm_strip="${llvm_bin}/llvm-strip"
for tool in "${llvm_nm}" "${llvm_readelf}" "${llvm_strip}"; do
  if [[ ! -x "${tool}" ]]; then
    echo "The selected NDK is missing ${tool}." >&2
    exit 1
  fi
done

# Each invocation describes exactly the ABIs it was asked to package.
rm -rf \
  "${output_dir}/lib/arm64-v8a" \
  "${output_dir}/lib/x86_64"
mkdir -p \
  "${output_dir}/include" \
  "${output_dir}/licenses" \
  "${output_dir}/lib"
install -m 0644 \
  "${source_dir}/LICENSE" \
  "${output_dir}/licenses/LICENSE"

built_abis=()
seen_architectures=" "
for architecture in "${requested_architectures[@]}"; do
  if [[ "${seen_architectures}" == *" ${architecture} "* ]]; then
    continue
  fi
  seen_architectures+="${architecture} "

  case "${architecture}" in
    arm64)
      android_abi="arm64-v8a"
      expected_machine="AArch64"
      ;;
    x64)
      android_abi="x86_64"
      expected_machine="Advanced Micro Devices X86-64"
      ;;
  esac

  build_dir="${build_root}/${android_abi}-api${android_api_level}"
  cmake \
    -S "${wrapper_dir}" \
    -B "${build_dir}" \
    -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_TOOLCHAIN_FILE="${ndk_root}/build/cmake/android.toolchain.cmake" \
    -DCMAKE_ANDROID_NDK="${ndk_root}" \
    -DANDROID_ABI="${android_abi}" \
    -DANDROID_PLATFORM="android-${android_api_level}" \
    -DANDROID_STL=c++_static \
    -DDAWN_SOURCE_DIR="${source_dir}"
  cmake --build "${build_dir}" \
    --target progpu_webgpu_dawn \
    --parallel

  generated_header="${build_dir}/dawn/gen/include/dawn/webgpu.h"
  verify_webgpusharp_abi_header "${generated_header}"
  install -m 0644 \
    "${generated_header}" \
    "${output_dir}/include/webgpu.h"

  source_library="${build_dir}/package/libwebgpu_dawn.so"
  destination_dir="${output_dir}/lib/${android_abi}"
  destination_library="${destination_dir}/libwebgpu_dawn.so"
  if [[ ! -f "${source_library}" ]]; then
    echo "Dawn completed without producing ${source_library}." >&2
    exit 1
  fi

  mkdir -p "${destination_dir}"
  install -m 0755 "${source_library}" "${destination_library}"
  "${llvm_strip}" --strip-unneeded "${destination_library}"

  if ! "${llvm_readelf}" -h "${destination_library}" |
      awk -v expected="${expected_machine}" '
        /^[[:space:]]*Machine:/ {
          sub(/^[[:space:]]*Machine:[[:space:]]*/, "", $0)
          found = ($0 == expected)
        }
        END { exit !found }
      '; then
    echo "Packaged ${android_abi} library has the wrong ELF machine type." >&2
    exit 1
  fi
  if ! "${llvm_readelf}" -d "${destination_library}" |
      awk '/\(SONAME\)/ && /\[libwebgpu_dawn\.so\]/ {
        found = 1
      } END { exit !found }'; then
    echo "Packaged ${android_abi} library has the wrong SONAME." >&2
    exit 1
  fi
  for symbol in \
    wgpuCreateInstance \
    wgpuDeviceImportSharedTextureMemory \
    wgpuSharedTextureMemoryBeginAccess \
    wgpuSharedTextureMemoryEndAccess; do
    if ! "${llvm_nm}" -D --defined-only "${destination_library}" |
        awk -v symbol="${symbol}" \
          '$NF == symbol { found = 1 } END { exit !found }'; then
      echo "Packaged ${android_abi} library does not export ${symbol}." >&2
      exit 1
    fi
  done
  if "${llvm_readelf}" -d "${destination_library}" |
      awk '/\(NEEDED\)/ &&
           ($0 ~ /libdawn/ || $0 ~ /libtint/ ||
            $0 ~ /libwebgpu/ || $0 ~ /libc\+\+_shared/) {
             bad = 1
           }
           END { exit bad }'; then
    :
  else
    echo "Packaged ${android_abi} library has a private DSO dependency." >&2
    exit 1
  fi
  if ! "${llvm_readelf}" -l "${destination_library}" |
      awk '$1 == "LOAD" {
        found = 1
        if ($NF != "0x4000") bad = 1
      } END { exit !found || bad }'; then
    echo "Packaged ${android_abi} library is not aligned for 16 KiB pages." >&2
    exit 1
  fi

  built_abis+=("${android_abi}")
done

{
  printf 'dawn-commit=%s\n' "${expected_commit}"
  printf 'webgpusharp-abi=0.5.5\n'
  printf 'android-api-level=%s\n' "${android_api_level}"
  printf 'android-abis=%s\n' "$(IFS=,; printf '%s' "${built_abis[*]}")"
  printf 'runtime-backend=vulkan\n'
  printf 'shared-texture-memory=AHardwareBuffer\n'
  printf 'shared-fence=SyncFD\n'
  printf 'cxx-runtime=static\n'
  printf 'ndk-revision=%s\n' "${ndk_revision}"
} > "${output_dir}/BUILD-MANIFEST.txt"

checksum_file="${output_dir}/SHA256SUMS"
checksum_temp="${checksum_file}.tmp"
: > "${checksum_temp}"
while IFS= read -r relative_path; do
  if command -v sha256sum >/dev/null 2>&1; then
    digest="$(sha256sum "${output_dir}/${relative_path}" | awk '{print $1}')"
  else
    digest="$(shasum -a 256 "${output_dir}/${relative_path}" | awk '{print $1}')"
  fi
  printf '%s  %s\n' "${digest}" "${relative_path}" >> "${checksum_temp}"
done < <(
  cd "${output_dir}"
  find BUILD-MANIFEST.txt include licenses lib -type f -print |
    LC_ALL=C sort
)
mv "${checksum_temp}" "${checksum_file}"

echo "Created Android Dawn package at ${output_dir} from ${expected_commit}."
echo "ABIs: $(IFS=,; printf '%s' "${built_abis[*]}") (API ${android_api_level})"
