#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_version="${PROGPU_PACKAGE_VERSION:?PROGPU_PACKAGE_VERSION is required}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages-mobile/Release}"
android_package="${package_output}/ProGPU.Android.${package_version}.nupkg"
ios_package="${package_output}/ProGPU.iOS.${package_version}.nupkg"

for package in "${android_package}" "${ios_package}"; do
  [[ -f "${package}" ]] || {
    echo "Mobile package is missing: ${package}" >&2
    exit 1
  }
done

android_entries=(
  buildTransitive/ProGPU.Android.targets
  build/native/android/BUILD-MANIFEST.txt
  runtimes/android-arm64/native/libprogpu_native_dawn.so
  runtimes/android-x64/native/libprogpu_native_dawn.so)
ios_entries=(
  buildTransitive/ProGPU.iOS.targets
  build/native/ios/BUILD-MANIFEST.txt
  runtimes/ios/native/progpu_native_dawn.xcframework/Info.plist
  runtimes/ios/native/progpu_native_dawn.xcframework/ios-arm64/libprogpu_native_dawn.a
  runtimes/ios/native/progpu_native_dawn.xcframework/ios-arm64_x86_64-simulator/libprogpu_native_dawn.a)

for entry in "${android_entries[@]}"; do
  unzip -Z1 "${android_package}" | grep -Fx "${entry}" >/dev/null || {
    echo "ProGPU.Android is missing ${entry}." >&2
    exit 1
  }
done
for entry in "${ios_entries[@]}"; do
  unzip -Z1 "${ios_package}" | grep -Fx "${entry}" >/dev/null || {
    echo "ProGPU.iOS is missing ${entry}." >&2
    exit 1
  }
done
for package in "${android_package}" "${ios_package}"; do
  if ! unzip -p "${package}" '*.nuspec' |
      grep -Fq 'id="ProGPU.Backend.Native"'; then
    echo "$(basename "${package}") does not depend on ProGPU.Backend.Native." >&2
    exit 1
  fi
done

echo "Verified packaged Android and iOS provider-resolved C++ renderer assets."
