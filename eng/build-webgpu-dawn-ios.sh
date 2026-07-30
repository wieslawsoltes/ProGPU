#!/usr/bin/env bash
set -euo pipefail

# Builds the exact Dawn C ABI consumed by WebGPUSharp 0.5.5 for iOS device
# and Apple-silicon simulator. Source remains an external build input under
# artifacts/ and is never vendored into ProGPU.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${DAWN_SOURCE:-${repo_root}/artifacts/dawn-webgpusharp-0.5.5-src}"
build_root="${DAWN_IOS_BUILD_ROOT:-${repo_root}/artifacts/dawn-webgpusharp-0.5.5-ios-build}"
output_dir="${DAWN_IOS_OUTPUT:-${repo_root}/artifacts/dawn-ios}"
deployment_target="${IPHONEOS_DEPLOYMENT_TARGET:-15.0}"
expected_commit="01249a97332468dbdd6cf5edb8dd7bae77875de5"
expected_ref="refs/heads/chromium/7871_124"
upstream_url="https://dawn.googlesource.com/dawn"
framework_info_plist="${repo_root}/eng/dawn-ios/Info.plist"

case "${source_dir}" in
  ""|/|"${repo_root}"|"${build_root}"|"${output_dir}")
    echo "Unsafe DAWN_SOURCE: ${source_dir}" >&2
    exit 2
    ;;
esac
case "${build_root}" in
  ""|/|"${repo_root}"|"${source_dir}"|"${output_dir}")
    echo "Unsafe DAWN_IOS_BUILD_ROOT: ${build_root}" >&2
    exit 2
    ;;
esac
case "${output_dir}" in
  ""|/|"${repo_root}"|"${source_dir}"|"${build_root}")
    echo "Unsafe DAWN_IOS_OUTPUT: ${output_dir}" >&2
    exit 2
    ;;
esac

for required_tool in cmake git ninja python3 xcodebuild xcrun; do
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

common_args=(
  -G Ninja
  -DCMAKE_BUILD_TYPE=Release
  -DCMAKE_SYSTEM_NAME=iOS
  -DCMAKE_OSX_ARCHITECTURES=arm64
  -DCMAKE_OSX_DEPLOYMENT_TARGET="${deployment_target}"
  -DBUILD_SHARED_LIBS=OFF
  -DDAWN_ENABLE_D3D11=OFF
  -DDAWN_ENABLE_D3D12=OFF
  -DDAWN_ENABLE_DESKTOP_GL=OFF
  -DDAWN_ENABLE_INSTALL=OFF
  -DDAWN_ENABLE_METAL=ON
  -DDAWN_ENABLE_NULL=OFF
  -DDAWN_ENABLE_OPENGLES=OFF
  -DDAWN_ENABLE_PIC=ON
  -DDAWN_ENABLE_VULKAN=OFF
  -DDAWN_FETCH_DEPENDENCIES=OFF
  -DDAWN_BUILD_MONOLITHIC_LIBRARY=STATIC
  -DDAWN_BUILD_BENCHMARKS=OFF
  -DDAWN_BUILD_NODE_BINDINGS=OFF
  -DDAWN_BUILD_PROTOBUF=OFF
  -DDAWN_BUILD_SAMPLES=OFF
  -DDAWN_USE_GLFW=OFF
  -DTINT_BUILD_BENCHMARKS=OFF
  -DTINT_BUILD_CMD_TOOLS=OFF
  -DTINT_BUILD_FUZZERS=OFF
  -DTINT_BUILD_IR_BINARY=OFF
  -DTINT_BUILD_TESTS=OFF
)

build_slice() {
  local sdk="$1"
  local build_dir="$2"
  local sysroot
  sysroot="$(xcrun --sdk "${sdk}" --show-sdk-path)"
  cmake -S "${source_dir}" -B "${build_dir}" \
    "${common_args[@]}" \
    -DCMAKE_OSX_SYSROOT="${sysroot}"
  cmake --build "${build_dir}" \
    --target webgpu_dawn \
    --parallel
}

device_build="${build_root}/iphoneos-arm64"
simulator_build="${build_root}/iphonesimulator-arm64"
build_slice iphoneos "${device_build}"
build_slice iphonesimulator "${simulator_build}"
device_header="${device_build}/gen/include/dawn/webgpu.h"
simulator_header="${simulator_build}/gen/include/dawn/webgpu.h"
verify_webgpusharp_abi_header "${device_header}"
verify_webgpusharp_abi_header "${simulator_header}"

device_library="${device_build}/src/dawn/native/libwebgpu_dawn.a"
simulator_library="${simulator_build}/src/dawn/native/libwebgpu_dawn.a"
for library in "${device_library}" "${simulator_library}"; do
  if [[ ! -f "${library}" ]]; then
    echo "Dawn did not produce its monolithic static library: ${library}" >&2
    exit 1
  fi
done

create_dynamic_framework() {
  local sdk="$1"
  local input_library="$2"
  local generated_header="$3"
  local framework="$4"
  local minimum_version_flag
  local sysroot

  sysroot="$(xcrun --sdk "${sdk}" --show-sdk-path)"
  if [[ "${sdk}" == "iphoneos" ]]; then
    minimum_version_flag="-miphoneos-version-min=${deployment_target}"
  else
    minimum_version_flag="-mios-simulator-version-min=${deployment_target}"
  fi

  if [[ -e "${framework}" ]]; then
    rm -rf "${framework}"
  fi
  mkdir -p "${framework}/Headers"
  install -m 0644 "${framework_info_plist}" "${framework}/Info.plist"
  plutil -replace MinimumOSVersion \
    -string "${deployment_target}" \
    "${framework}/Info.plist"
  install -m 0644 \
    "${generated_header}" \
    "${framework}/Headers/webgpu.h"

  xcrun --sdk "${sdk}" clang++ \
    -dynamiclib \
    -arch arm64 \
    -isysroot "${sysroot}" \
    "${minimum_version_flag}" \
    "-Wl,-force_load,${input_library}" \
    -framework CoreGraphics \
    -framework Foundation \
    -framework IOSurface \
    -framework Metal \
    -framework QuartzCore \
    -install_name "@rpath/webgpu_dawn.framework/webgpu_dawn" \
    -o "${framework}/webgpu_dawn"
}

device_framework="${device_build}/package/webgpu_dawn.framework"
simulator_framework="${simulator_build}/package/webgpu_dawn.framework"
create_dynamic_framework iphoneos \
  "${device_library}" \
  "${device_header}" \
  "${device_framework}"
create_dynamic_framework iphonesimulator \
  "${simulator_library}" \
  "${simulator_header}" \
  "${simulator_framework}"

for framework in "${device_framework}" "${simulator_framework}"; do
  library="${framework}/webgpu_dawn"
  if [[ "$(xcrun lipo -archs "${library}")" != "arm64" ]]; then
    echo "Dawn slice has the wrong architecture: ${library}" >&2
    exit 1
  fi
  for symbol in \
    _wgpuCreateInstance \
    _wgpuDeviceImportSharedTextureMemory \
    _wgpuSharedTextureMemoryBeginAccess \
    _wgpuSharedTextureMemoryEndAccess; do
    if ! xcrun nm -gU "${library}" |
        awk -v symbol="${symbol}" \
          '$NF == symbol { found = 1 } END { exit !found }'; then
      echo "Dawn slice does not define ${symbol}: ${library}" >&2
      exit 1
    fi
  done
done

mkdir -p "${output_dir}/include" "${output_dir}/licenses"
install -m 0644 \
  "${device_header}" \
  "${output_dir}/include/webgpu.h"
install -m 0644 \
  "${source_dir}/LICENSE" \
  "${output_dir}/licenses/LICENSE"

xcframework="${output_dir}/webgpu_dawn.xcframework"
if [[ -e "${xcframework}" ]]; then
  rm -rf "${xcframework}"
fi
xcodebuild -create-xcframework \
  -framework "${device_framework}" \
  -framework "${simulator_framework}" \
  -output "${xcframework}"

{
  printf 'dawn-commit=%s\n' "${expected_commit}"
  printf 'webgpusharp-abi=0.5.5\n'
  printf 'ios-deployment-target=%s\n' "${deployment_target}"
  printf 'ios-slices=ios-arm64,ios-arm64-simulator\n'
  printf 'runtime-backend=metal\n'
  printf 'shared-texture-memory=IOSurface\n'
  printf 'shared-fence=MTLSharedEvent\n'
  printf 'linkage=embedded-dynamic-framework\n'
  printf 'xcode-version=%s\n' \
    "$(xcodebuild -version | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
} > "${output_dir}/BUILD-MANIFEST.txt"

echo "Created ${xcframework} from Dawn ${expected_commit}."
