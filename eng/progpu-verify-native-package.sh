#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_version="${PROGPU_PACKAGE_VERSION:?PROGPU_PACKAGE_VERSION is required}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages-native/Release}"
package="${package_output}/ProGPU.Backend.Native.${package_version}.nupkg"

if [[ ! -f "${package}" ]]; then
  echo "Native backend package is missing: ${package}" >&2
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
dotnet restore "${consumer}" \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}"
dotnet run --project "${consumer}" --configuration Release --no-restore \
  -p:ProGpuNativePackageSource="${package_output}" \
  -p:ProGpuNativePackageVersion="${package_version}"

echo "Verified ProGPU.Backend.Native ${package_version} package contents and runtime consumer."
