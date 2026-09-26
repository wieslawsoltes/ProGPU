#!/usr/bin/env bash
set -euo pipefail

# Builds only the reported wgpu-native UI contract. The caller builds the exact
# Android provider first; this script never installs tools or starts an emulator.
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
evidence="${PROGPU_ANDROID_EVIDENCE_ROOT:-${repo_root}/artifacts/progpu-native/android-x64-evidence}"
mkdir -p "${evidence}"
evidence="$(cd "${evidence}" && pwd)"
apk="${PROGPU_ANDROID_APK:-${evidence}/ProGPU.Samples-Signed.apk}"
wgpu_root="${PROGPU_ANDROID_WGPU_ROOT:-${repo_root}/artifacts/wgpu-native-android}"
dotnet_command="${PROGPU_ANDROID_DOTNET:-dotnet}"
verifier="${repo_root}/eng/progpu-verify-android-evidence.py"
project="${repo_root}/src/ProGPU.Samples.Android/ProGPU.Samples.Android.csproj"

bounded() { python3 "${verifier}" run-command --timeout "$1" -- "${@:2}"; }
finish() {
  local status=$?
  if ((status != 0)); then
    printf 'FAIL: APK build/verification incomplete (exit %s); no runtime qualification.\n' "${status}" > "${evidence}/build-status.txt"
  fi
}
trap finish EXIT
printf 'STARTED: Android x64 Debug UI-only package build.\n' > "${evidence}/build-status.txt"

[[ -f "${wgpu_root}/lib/x86_64/libwgpu_native.so" ]] || { echo "Build the pinned x64 Android wgpu-native provider first." >&2; exit 1; }
wgpu_root="$(cd "${wgpu_root}" && pwd)"
[[ ! -e "${evidence}/absent-dawn" && ! -e "${evidence}/absent-engine" ]] || { echo "UI-only provider exclusion paths must not exist." >&2; exit 1; }
mkdir -p "$(dirname "${apk}")"
cd "${repo_root}"

{
  git rev-parse HEAD
  git status --short
  bounded 30 "${dotnet_command}" --info
  bounded 30 "${dotnet_command}" workload list
  if command -v rustc >/dev/null 2>&1; then rustc --version; fi
  if [[ -n "${ANDROID_NDK_ROOT:-}" ]]; then
    test -f "${ANDROID_NDK_ROOT}/source.properties"
    sed -n '/^Pkg.Revision/p' "${ANDROID_NDK_ROOT}/source.properties"
  fi
} > "${evidence}/build-versions.txt" 2>&1
cp "${wgpu_root}/BUILD-MANIFEST.txt" "${evidence}/wgpu-BUILD-MANIFEST.txt"
cp "${wgpu_root}/SHA256SUMS" "${evidence}/wgpu-SHA256SUMS"

# SignAndroidPackage includes Build. EmbedAssembliesIntoApk prevents an IDE-only
# fast-deployment APK; keeping the staged provider bytes permits exact hash proof.
# The sample already declares net10.0-android. Do not pass TargetFramework as a
# global restore property: its ordinary net10.0/netstandard references must keep
# their own restore targets before Android negotiates the build graph.
# The fixed 15-minute command bound leaves the workflow's other deadlines intact.
bounded 900 "${dotnet_command}" msbuild "${project}" \
  -restore -target:SignAndroidPackage -nologo -verbosity:minimal \
  -p:Configuration=Debug \
  -p:RuntimeIdentifier=android-x64 -p:RuntimeIdentifiers=android-x64 \
  -p:EmbedAssembliesIntoApk=true -p:AndroidPackageFormat=apk -p:AndroidPackageFormats=apk \
  -p:AndroidStripNativeLibraries=false -p:AndroidCreatePackagePerAbi=false \
  -p:ProGpuRequireZeroCopyMedia=false \
  -p:ProGpuWgpuNativeAndroidRoot="${wgpu_root}" \
  -p:ProGpuDawnAndroidRoot="${evidence}/absent-dawn" \
  -p:ProGpuNativeDawnAndroidRoot="${evidence}/absent-engine" \
  -getProperty:ApkFileSigned -getItem:ApkAbiFilesSigned \
  -bl:"${evidence}/android-sample.binlog" 2>&1 | tee "${evidence}/build.log"

python3 "${verifier}" stage-apk --build-log "${evidence}/build.log" \
  --destination "${apk}" --project-directory "$(dirname "${project}")"
python3 "${verifier}" apk --apk "${apk}" --wgpu-root "${wgpu_root}" \
  --output "${evidence}/apk-validation.json"
unzip -Z1 "${apk}" > "${evidence}/apk-entries.txt"
printf 'PASS: verified Android x64 Debug UI-only APK; runtime not yet qualified.\n' > "${evidence}/build-status.txt"
echo "Verified UI-only APK: ${apk}"
