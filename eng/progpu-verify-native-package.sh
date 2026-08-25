#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_version="${PROGPU_PACKAGE_VERSION:?PROGPU_PACKAGE_VERSION is required}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages-native/Release}"
package="${package_output}/ProGPU.Backend.Native.${package_version}.nupkg"
dawn_package="${package_output}/ProGPU.Backend.Dawn.${package_version}.nupkg"

if [[ ! -f "${package}" ]]; then
  echo "Native backend package is missing: ${package}" >&2
  exit 1
fi
if [[ ! -f "${dawn_package}" ]]; then
  echo "Dawn backend package is missing: ${dawn_package}" >&2
  exit 1
fi
if ! unzip -p "${package}" '*.nuspec' |
    grep -Fq 'id="ProGPU.Backend.Dawn"'; then
  echo "The native backend package does not depend on ProGPU.Backend.Dawn." >&2
  exit 1
fi

required_entries=(
  runtimes/linux-x64/native/libprogpu_native.so
  runtimes/linux-x64/native/libprogpu_native_dawn.so
  runtimes/linux-arm64/native/libprogpu_native.so
  runtimes/linux-arm64/native/libprogpu_native_dawn.so
  runtimes/osx-x64/native/libprogpu_native.dylib
  runtimes/osx-x64/native/libprogpu_native_dawn.dylib
  runtimes/osx-arm64/native/libprogpu_native.dylib
  runtimes/osx-arm64/native/libprogpu_native_dawn.dylib
  runtimes/win-x64/native/progpu_native.dll
  runtimes/win-x64/native/progpu_native_dawn.dll
  runtimes/win-arm64/native/progpu_native.dll
  runtimes/win-arm64/native/progpu_native_dawn.dll
  build/native/include/progpu_native.h
  build/native/include/progpu_native_dawn.h
  build/native/include/progpu_native_compression.hpp
  build/native/include/progpu_native_hit_testing.hpp
  build/native/include/progpu_native_image.hpp
  build/native/include/progpu_native_mil.h
  build/native/include/progpu_native_mil.hpp
  build/native/include/progpu_native_scene_builder.hpp
  build/native/include/progpu_native_text.hpp
  build/native/modules/progpu_native_compression.cppm
  build/native/modules/progpu_native_hit_testing.cppm
  build/native/modules/progpu_native_image.cppm
  build/native/modules/progpu_native_scene_builder.cppm
  build/native/modules/progpu_native_text.cppm
  build/native/cmake/ProGPUNativeConfig.cmake
)
for rid in linux-x64 linux-arm64 osx-x64 osx-arm64; do
  for library in compression hit_testing image mil text scene_builder; do
    required_entries+=(
      "runtimes/${rid}/native/sdk/libprogpu_native_${library}.a")
  done
done
for rid in win-x64 win-arm64; do
  required_entries+=(
    "runtimes/${rid}/native/sdk/progpu_native_dawn.lib")
  for library in compression hit_testing image mil text scene_builder; do
    required_entries+=(
      "runtimes/${rid}/native/sdk/progpu_native_${library}.lib")
  done
done
for entry in "${required_entries[@]}"; do
  if ! unzip -Z1 "${package}" | grep -Fx "${entry}" >/dev/null; then
    echo "Native backend package is missing ${entry}." >&2
    exit 1
  fi
done

consumer="${repo_root}/tests/ProGPU.Native.PackageConsumer/ProGPU.Native.PackageConsumer.csproj"
consumer_packages="$(mktemp -d /tmp/progpu-native-consumer-packages.XXXXXX)"
cleanup_consumer_packages() {
  rm -rf "${consumer_packages}"
}
trap cleanup_consumer_packages EXIT
NUGET_PACKAGES="${consumer_packages}" dotnet restore "${consumer}" \
  --no-cache \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}"
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}"
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-drawing-group-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-glyph-run-drawing-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-text-render-options-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-visual-clip-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-visual-opacity-mask-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-visual-effect-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-visual-guideline-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-drawing-image-only
NUGET_PACKAGES="${consumer_packages}" dotnet run \
  --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}" -- \
  --mil-guideline-only

native_consumer_root="$(mktemp -d /tmp/progpu-native-cpp-consumer.XXXXXX)"
cleanup_native_consumer() {
  rm -rf "${native_consumer_root}"
}
trap 'cleanup_consumer_packages; cleanup_native_consumer' EXIT
unzip -q "${package}" -d "${native_consumer_root}/package"
cmake -S "${repo_root}/tests/ProGPU.Native.CppPackageConsumer" \
  -B "${native_consumer_root}/build" \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DProGPUNative_DIR="${native_consumer_root}/package/build/native/cmake"
cmake --build "${native_consumer_root}/build" --parallel
"${native_consumer_root}/build/progpu_native_cpp_package_consumer"

echo "Verified ProGPU.Backend.Native ${package_version} package contents and .NET/C++ consumers."
