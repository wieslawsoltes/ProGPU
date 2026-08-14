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
)
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

echo "Verified ProGPU.Backend.Native ${package_version} package contents and runtime consumer."
