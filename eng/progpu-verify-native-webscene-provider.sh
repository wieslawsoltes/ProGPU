#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version_manifest="${repo_root}/eng/progpu-native-dawn.version.json"
provider_source="${PROGPU_NATIVE_WEBSCENE_SOURCE:-${repo_root}/artifacts/webscene-provider-src}"
provider_output="${PROGPU_NATIVE_WEBSCENE_OUTPUT:-${repo_root}/artifacts/webscene-provider-packages}"
dawn_workspace="${PROGPU_NATIVE_WEBSCENE_DAWN_WORKSPACE:-${repo_root}/artifacts/webscene-dawn/osx-arm64}"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
sample_dir="${PROGPU_NATIVE_SAMPLE_DIR:-${repo_root}/artifacts/progpu-native/sample}"

if [[ "$(uname -s)" != "Darwin" || "$(uname -m)" != "arm64" ]]; then
  echo "The WebScene provider hardware gate requires Darwin/arm64." >&2
  exit 1
fi

provider_repository="$(awk -F'"' '/"providerRepository"/ { print $4; exit }' "${version_manifest}")"
provider_revision="$(awk -F'"' '/"providerRevision"/ { print $4; exit }' "${version_manifest}")"
dawn_revision="$(awk -F'"' '/"dawnRevision"/ { print $4; exit }' "${version_manifest}")"
if [[ -z "${provider_repository}" ||
      ! "${provider_revision}" =~ ^[0-9a-f]{40}$ ||
      ! "${dawn_revision}" =~ ^[0-9a-f]{40}$ ]]; then
  echo "Invalid WebScene provider version manifest: ${version_manifest}" >&2
  exit 1
fi
if [[ ! -f "${build_dir}/CMakeCache.txt" ]]; then
  echo "Configure the native renderer first with ./eng/build-progpu-native.sh." >&2
  echo "Missing CMake cache: ${build_dir}/CMakeCache.txt" >&2
  exit 1
fi

if [[ -e "${provider_source}" && ! -d "${provider_source}/.git" ]]; then
  echo "WebScene provider source is not a Git checkout: ${provider_source}" >&2
  exit 1
fi
if [[ ! -d "${provider_source}/.git" ]]; then
  git clone --filter=blob:none "${provider_repository}" "${provider_source}"
fi
if [[ "$(git -C "${provider_source}" remote get-url origin)" != "${provider_repository}" ]]; then
  echo "WebScene provider checkout has an unexpected origin." >&2
  exit 1
fi
if [[ -n "$(git -C "${provider_source}" status --porcelain --untracked-files=no)" ]]; then
  echo "Refusing to change a modified WebScene provider checkout." >&2
  exit 1
fi
if ! git -C "${provider_source}" cat-file -e \
    "${provider_revision}^{commit}" 2>/dev/null; then
  git -C "${provider_source}" fetch --depth 1 origin "${provider_revision}"
fi
git -C "${provider_source}" checkout --detach --force "${provider_revision}"
if [[ "$(git -C "${provider_source}" rev-parse HEAD)" != "${provider_revision}" ]]; then
  echo "WebScene provider checkout did not resolve to the pinned revision." >&2
  exit 1
fi

"${provider_source}/scripts/build-native-gpu-runtime.sh" \
  --rid osx-arm64 \
  --output "${provider_output}" \
  --package-version 0.0.0-provider-test \
  --workspace "${dawn_workspace}" \
  --dawn-revision "${dawn_revision}"

dawn_source="${dawn_workspace}/src"
dawn_include="${dawn_workspace}/install/include/dawn"
provider_include="${provider_source}/experiments/WebScene.NativeEngine.Probe/native"
provider_library="${provider_source}/artifacts/native-gpu-provider-build/osx-arm64/libwebscene_native_gpu.dylib"
if [[ "$(git -C "${dawn_source}" rev-parse HEAD)" != "${dawn_revision}" ]]; then
  echo "WebScene built an unexpected Dawn revision." >&2
  exit 1
fi
if [[ ! -f "${dawn_include}/webgpu.h" ||
      ! -f "${provider_include}/webscene_gpu_provider.h" ||
      ! -f "${provider_library}" ]]; then
  echo "WebScene did not produce the required provider inputs." >&2
  exit 1
fi

cmake -S "${repo_root}/src/ProGPU.Native" -B "${build_dir}" \
  -DPROGPU_NATIVE_DAWN_WEBGPU_INCLUDE_DIR="${dawn_include}" \
  -DPROGPU_NATIVE_WEBSCENE_PROVIDER_INCLUDE_DIR="${provider_include}" \
  -DPROGPU_NATIVE_WEBSCENE_PROVIDER_LIBRARY="${provider_library}" \
  -DBUILD_TESTING=ON
cmake --build "${build_dir}" \
  --target progpu_native_webscene_provider_tests \
  --config Release \
  --parallel

mkdir -p "${sample_dir}"
evidence="${sample_dir}/progpu-native-webscene-provider.txt"
ctest --test-dir "${build_dir}" -C Release --output-on-failure --verbose \
  -R '^progpu_native_webscene_provider_tests$' | tee "${evidence}"
capture_source="${build_dir}/progpu-native-webscene-provider.ppm"
capture="${sample_dir}/progpu-native-webscene-provider.ppm"
if [[ ! -s "${capture_source}" ]]; then
  echo "The provider hardware gate did not produce its capture." >&2
  exit 1
fi
cmake -E copy_if_different "${capture_source}" "${capture}"
sips -s format png "${capture}" \
  --out "${sample_dir}/progpu-native-webscene-provider.png" >/dev/null

{
  echo "WebScene provider revision: ${provider_revision}"
  echo "Dawn revision: ${dawn_revision}"
  shasum -a 256 "${provider_library}"
  shasum -a 256 "${capture}"
} | tee -a "${evidence}"

echo "Verified the ProGPU C++ renderer through WebScene's exact Metal provider."
