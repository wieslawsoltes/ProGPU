#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
header_source="${PROGPU_NATIVE_DAWN_HEADER_SOURCE:-${repo_root}/artifacts/webgpu-headers-dawn}"
build_root="${PROGPU_NATIVE_MOBILE_BUILD_ROOT:-${repo_root}/artifacts/progpu-native/mobile-build}"
output_root="${PROGPU_NATIVE_MOBILE_OUTPUT:-${repo_root}/artifacts/progpu-native/mobile}"
expected_headers_commit="01addc4ba8a2915a061b7095a6768b512071ab96"
header_repository="https://github.com/webgpu-native/webgpu-headers.git"
requested_platform="${1:-all}"

case "${requested_platform}" in
  android|ios|all) ;;
  *)
    echo "Usage: $0 [android|ios|all]" >&2
    exit 2
    ;;
esac

for path in "${header_source}" "${build_root}" "${output_root}"; do
  case "${path}" in
    ""|/|"${repo_root}")
      echo "Unsafe mobile native path: ${path}" >&2
      exit 2
      ;;
  esac
done

if [[ -e "${header_source}" && ! -d "${header_source}/.git" ]]; then
  echo "Dawn header source is not a Git checkout: ${header_source}" >&2
  exit 1
fi
if [[ ! -d "${header_source}/.git" ]]; then
  git clone --filter=blob:none "${header_repository}" "${header_source}"
fi
if [[ "$(git -C "${header_source}" remote get-url origin)" != "${header_repository}" ]]; then
  echo "Dawn header checkout has an unexpected origin." >&2
  exit 1
fi
if [[ -n "$(git -C "${header_source}" status --porcelain --untracked-files=no)" ]]; then
  echo "Refusing to change a modified Dawn header checkout." >&2
  exit 1
fi
if ! git -C "${header_source}" cat-file -e \
    "${expected_headers_commit}^{commit}" 2>/dev/null; then
  git -C "${header_source}" fetch --depth 1 origin \
    "${expected_headers_commit}"
fi
git -C "${header_source}" checkout --detach --force \
  "${expected_headers_commit}"
if [[ "$(git -C "${header_source}" rev-parse HEAD)" != "${expected_headers_commit}" ]]; then
  echo "Dawn header checkout did not resolve to the pinned revision." >&2
  exit 1
fi

configure_common=(
  -S "${repo_root}/src/ProGPU.Native"
  -DCMAKE_BUILD_TYPE=Release
  -DPROGPU_NATIVE_BUILD_WGPU_TARGET=OFF
  -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR="${header_source}"
  -DPROGPU_NATIVE_BUILD_SAMPLE=OFF
  -DBUILD_TESTING=OFF)

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
  done
  [[ -n "${best}" ]] || return 1
  printf '%s\n' "${best}"
}

resolve_ndk_readelf() {
  local ndk_root="$1"
  local candidate=""
  local host_tag=""

  case "$(uname -s)" in
    Darwin)
      for host_tag in darwin-arm64 darwin-x86_64; do
        candidate="${ndk_root}/toolchains/llvm/prebuilt/${host_tag}/bin/llvm-readelf"
        if [[ -x "${candidate}" ]]; then
          printf '%s\n' "${candidate}"
          return 0
        fi
      done
      ;;
    Linux)
      for host_tag in linux-x86_64 linux-aarch64; do
        candidate="${ndk_root}/toolchains/llvm/prebuilt/${host_tag}/bin/llvm-readelf"
        if [[ -x "${candidate}" ]]; then
          printf '%s\n' "${candidate}"
          return 0
        fi
      done
      ;;
  esac

  # Keep custom NDK host distributions usable without relying on find's
  # regular-file predicate: llvm-readelf is commonly an executable symlink.
  for candidate in \
      "${ndk_root}"/toolchains/llvm/prebuilt/*/bin/llvm-readelf; do
    if [[ -x "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done
  return 1
}

build_android() {
  local ndk_root
  ndk_root="$(resolve_ndk_root)" || {
    echo "Android NDK was not found." >&2
    exit 1
  }
  local readelf
  readelf="$(resolve_ndk_readelf "${ndk_root}")" || {
    echo "Android NDK llvm-readelf was not found." >&2
    exit 1
  }
  local architecture
  local android_abi
  for architecture in arm64 x64; do
    if [[ "${architecture}" == arm64 ]]; then
      android_abi="arm64-v8a"
    else
      android_abi="x86_64"
    fi
    local build_dir="${build_root}/android-${architecture}"
    cmake "${configure_common[@]}" -B "${build_dir}" \
      -DCMAKE_TOOLCHAIN_FILE="${ndk_root}/build/cmake/android.toolchain.cmake" \
      -DANDROID_ABI="${android_abi}" \
      -DANDROID_PLATFORM=android-30 \
      -DANDROID_STL=c++_static \
      -DPROGPU_NATIVE_DAWN_LIBRARY_TYPE=SHARED
    cmake --build "${build_dir}" --target progpu_native_dawn --parallel
    local library="${build_dir}/libprogpu_native_dawn.so"
    [[ -f "${library}" ]] || {
      echo "Android C++ renderer was not produced: ${library}" >&2
      exit 1
    }
    local dynamic_symbols="${build_dir}/progpu-native-dynamic-symbols.txt"
    "${readelf}" --dyn-syms "${library}" > "${dynamic_symbols}"
    for symbol in \
      progpu_native_get_abi_version \
      progpu_native_dawn_get_adapter_abi_version \
      progpu_native_dawn_engine_create \
      progpu_native_engine_render_scene; do
      grep -Fq "${symbol}" "${dynamic_symbols}" || {
          echo "Android C++ renderer is missing ${symbol}." >&2
          exit 1
        }
    done
    if "${readelf}" -d "${library}" |
        grep -Eq 'NEEDED.*(dawn|webgpu|wgpu|c\+\+_shared)'; then
      echo "Android provider-resolved renderer has a forbidden private DSO dependency." >&2
      exit 1
    fi
    local destination="${output_root}/android/lib/${android_abi}"
    mkdir -p "${destination}"
    cp "${library}" "${destination}/libprogpu_native_dawn.so"
  done
  {
    printf 'headers-commit=%s\n' "${expected_headers_commit}"
    printf 'android-api-level=30\n'
    printf 'android-abis=arm64-v8a,x86_64\n'
    printf 'adapter=provider-resolved-dawn\n'
  } > "${output_root}/android/BUILD-MANIFEST.txt"
}

build_ios_slice() {
  local name="$1"
  local sdk="$2"
  local architecture="$3"
  local build_dir="${build_root}/ios-${name}"
  cmake "${configure_common[@]}" -B "${build_dir}" -G Xcode \
    -DCMAKE_SYSTEM_NAME=iOS \
    -DCMAKE_OSX_SYSROOT="$(xcrun --sdk "${sdk}" --show-sdk-path)" \
    -DCMAKE_OSX_ARCHITECTURES="${architecture}" \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
    -DPROGPU_NATIVE_DAWN_LIBRARY_TYPE=STATIC >&2
  cmake --build "${build_dir}" --target progpu_native_dawn \
    --config Release --parallel >&2
  local library="${build_dir}/Release-${sdk}/libprogpu_native_dawn.a"
  if [[ ! -f "${library}" ]]; then
    library="$(find "${build_dir}" -type f -name libprogpu_native_dawn.a -print -quit)"
  fi
  [[ -f "${library}" ]] || {
    echo "iOS C++ renderer slice was not produced: ${name}" >&2
    exit 1
  }
  printf '%s\n' "${library}"
}

build_ios() {
  command -v xcodebuild >/dev/null 2>&1 || {
    echo "xcodebuild is required for the iOS C++ renderer." >&2
    exit 1
  }
  local device_library
  local simulator_arm64_library
  local simulator_x64_library
  device_library="$(build_ios_slice device iphoneos arm64)"
  simulator_arm64_library="$(build_ios_slice simulator-arm64 iphonesimulator arm64)"
  simulator_x64_library="$(build_ios_slice simulator-x64 iphonesimulator x86_64)"
  local simulator_library="${build_root}/ios-simulator/libprogpu_native_dawn.a"
  mkdir -p "$(dirname "${simulator_library}")"
  xcrun lipo -create \
    "${simulator_arm64_library}" \
    "${simulator_x64_library}" \
    -output "${simulator_library}"
  local device_symbols="${build_root}/ios-device/progpu-native-symbols.txt"
  xcrun nm -gU "${device_library}" > "${device_symbols}"
  for symbol in \
    _progpu_native_get_abi_version \
    _progpu_native_dawn_get_adapter_abi_version \
    _progpu_native_dawn_engine_create \
    _progpu_native_engine_render_scene; do
    grep -Fq "${symbol}" "${device_symbols}" || {
      echo "iOS C++ renderer is missing ${symbol}." >&2
      exit 1
    }
  done
  local headers="${output_root}/ios/include"
  local xcframework="${output_root}/ios/progpu_native_dawn.xcframework"
  mkdir -p "${headers}"
  cp "${repo_root}/src/ProGPU.Native/include/progpu_native.h" "${headers}/"
  cp "${repo_root}/src/ProGPU.Native/include/progpu_native_dawn.h" "${headers}/"
  if [[ -e "${xcframework}" ]]; then
    rm -rf "${xcframework}"
  fi
  xcodebuild -create-xcframework \
    -library "${device_library}" -headers "${headers}" \
    -library "${simulator_library}" -headers "${headers}" \
    -output "${xcframework}"
  {
    printf 'headers-commit=%s\n' "${expected_headers_commit}"
    printf 'ios-deployment-target=15.0\n'
    printf 'ios-slices=ios-arm64,iossimulator-arm64,iossimulator-x64\n'
    printf 'adapter=provider-resolved-dawn\n'
  } > "${output_root}/ios/BUILD-MANIFEST.txt"
}

if [[ "${requested_platform}" == android ||
      "${requested_platform}" == all ]]; then
  build_android
fi
if [[ "${requested_platform}" == ios ||
      "${requested_platform}" == all ]]; then
  build_ios
fi

echo "Created provider-resolved mobile C++ renderer artifacts under ${output_root}."
