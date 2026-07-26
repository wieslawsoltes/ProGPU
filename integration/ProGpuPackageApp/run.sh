#!/usr/bin/env bash
# shellcheck disable=SC1091,SC2154
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"
project="${repo_root}/integration/ProGpuPackageApp/ProGpuPackageApp.csproj"
mode="${1:-nuget}"
configuration="${PROGPU_CONFIGURATION:-Release}"
integration_version="${PROGPU_INTEGRATION_PACKAGE_VERSION:-12.0.5-preview.27}"
avalonia_version="${PROGPU_AVALONIA_PACKAGE_VERSION:-12.0.5}"
runtime_version="${PROGPU_RUNTIME_PACKAGE_VERSION:-0.1.0-preview.27}"
package_source="${PROGPU_PACKAGE_SOURCE:-${repo_root}/artifacts/packages/${configuration}}"
case "${package_source}" in
  /*|[A-Za-z]:[\\/]*)
    ;;
  *)
    package_source="${PWD}/${package_source}"
    ;;
esac
working_directory="$(mktemp -d "${TMPDIR:-/tmp}/progpu-package-app.XXXXXX")"
consumer_artifacts="${working_directory}/artifacts"
verify_replacement=0
native_aot="${PROGPU_INTEGRATION_NATIVE_AOT:-0}"
runtime_identifier="${PROGPU_INTEGRATION_RUNTIME_IDENTIFIER:-}"

if [[ "$#" -gt 0 ]]; then
  shift
fi

cleanup() {
  local exit_code=$?
  rm -rf "${working_directory}"
  return "${exit_code}"
}
trap cleanup EXIT

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

resolve_runtime_identifier() {
  local kernel
  local architecture
  kernel="$(uname -s)"
  architecture="$(uname -m)"

  case "${kernel}:${architecture}" in
    Darwin:arm64) echo "osx-arm64" ;;
    Darwin:x86_64) echo "osx-x64" ;;
    Linux:aarch64|Linux:arm64) echo "linux-arm64" ;;
    Linux:x86_64) echo "linux-x64" ;;
    MINGW*:ARM64|MSYS*:ARM64) echo "win-arm64" ;;
    MINGW*:x86_64|MSYS*:x86_64) echo "win-x64" ;;
    *)
      echo "Unsupported NativeAOT host: ${kernel} ${architecture}" >&2
      return 1
      ;;
  esac
}

if [[ "${native_aot}" != 0 && "${native_aot}" != 1 ]]; then
  echo "PROGPU_INTEGRATION_NATIVE_AOT must be 0 or 1." >&2
  exit 2
fi
if [[ "${native_aot}" == 1 && -z "${runtime_identifier}" ]]; then
  runtime_identifier="$(resolve_runtime_identifier)"
fi

"${dotnet}" new nugetconfig --output "${working_directory}" --force >/dev/null

configure_local_sources() {
  # SDK templates have used both `nuget` and `nuget.org` for the default
  # source name. Remove either spelling so source ordering is deterministic.
  "${dotnet}" nuget remove source nuget \
    --configfile "${working_directory}/nuget.config" >/dev/null 2>&1 || true
  "${dotnet}" nuget remove source nuget.org \
    --configfile "${working_directory}/nuget.config" >/dev/null 2>&1 || true
  "${dotnet}" nuget add source "${package_source}" \
    --name progpu-local \
    --configfile "${working_directory}/nuget.config" >/dev/null
  "${dotnet}" nuget add source https://api.nuget.org/v3/index.json \
    --name nuget \
    --configfile "${working_directory}/nuget.config" >/dev/null
}

case "${mode}" in
  local)
    mkdir -p "${package_source}"

    if [[ ! -f "${package_source}/ProGPU.Backend.${runtime_version}.nupkg" ]]; then
      PROGPU_CONFIGURATION="${configuration}" \
      PROGPU_PACKAGE_VERSION="${runtime_version}" \
      PROGPU_PACKAGE_OUTPUT="${package_source}" \
      PROGPU_PACKAGE_GROUP=portable \
        "${repo_root}/eng/progpu-pack.sh"
    fi

    PROGPU_CONFIGURATION="${configuration}" \
    PROGPU_PACKAGE_OUTPUT="${package_source}" \
      "${repo_root}/scripts/progpu-pack.sh"
    configure_local_sources
    ;;
  replacement)
    if [[ "${PROGPU_REUSE_REPLACEMENT_STACK:-0}" != 1 ]]; then
      PROGPU_AVALONIA_REPLACEMENT_OUTPUT="${package_source}" \
        "${repo_root}/tools/pack-avalonia-progpu-stack.sh"
    else
      required_packages=(
        "Avalonia.${avalonia_version}.nupkg"
        "ProGPU.Avalonia.Rendering.${integration_version}.nupkg"
        "ProGPU.Avalonia.SilkNet.${integration_version}.nupkg"
      )
      for package_id in "${progpu_avalonia_runtime_package_ids[@]}"; do
        required_packages+=("${package_id}.${runtime_version}.nupkg")
      done
      for required_package in "${required_packages[@]}"; do
        if [[ ! -f "${package_source}/${required_package}" ]]; then
          echo "The requested prebuilt replacement stack is missing ${required_package} in ${package_source}." >&2
          exit 3
        fi
      done
    fi
    configure_local_sources
    verify_replacement=1
    ;;
  nuget)
    ;;
  *)
    echo "Usage: $0 [local|replacement|nuget] [application arguments...]" >&2
    exit 2
    ;;
esac

export NUGET_HTTP_CACHE_PATH="${working_directory}/http-cache"
packages_path="${working_directory}/packages"
require_replacement=false
if [[ "${verify_replacement}" == 1 ]]; then
  require_replacement=true
fi

restore_arguments=(
  "${project}"
  --packages "${packages_path}"
  --artifacts-path "${consumer_artifacts}"
  --configfile "${working_directory}/nuget.config"
  --force
  --no-cache
  --verbosity minimal
  "-p:ProGpuIntegrationPackageVersion=${integration_version}"
  "-p:ProGpuAvaloniaPackageVersion=${avalonia_version}"
  "-p:ProGpuRequireReplacement=${require_replacement}"
)
if [[ "${native_aot}" == 1 ]]; then
  restore_arguments+=(
    --runtime "${runtime_identifier}"
    "-p:PublishAot=true"
    "-p:TrimMode=full"
    "-p:InvariantGlobalization=true"
  )
fi

if [[ "${verify_replacement}" == 1 ]]; then
  # Warm dependencies in the isolated package folder, then remove only the
  # package identities supplied by the replacement stack and resolve all of
  # them from the local feed. Keeping an old same-version runtime cache entry
  # would otherwise allow NuGet to create a source-inconsistent binary mix.
  "${dotnet}" restore "${restore_arguments[@]}"
  replacement_package_ids=(
    avalonia
    progpu.avalonia.rendering
    progpu.avalonia.silknet
  )
  replacement_package_versions=(
    "${avalonia_version}"
    "${integration_version}"
    "${integration_version}"
  )
  for package_id in "${progpu_avalonia_runtime_package_ids[@]}"; do
    replacement_package_ids+=("$(printf '%s' "${package_id}" | tr '[:upper:]' '[:lower:]')")
    replacement_package_versions+=("${runtime_version}")
  done
  for index in "${!replacement_package_ids[@]}"; do
    replacement_cache_entry="${packages_path}/${replacement_package_ids[$index]}/${replacement_package_versions[$index]}"
    rm -rf "${replacement_cache_entry}"
  done
  "${dotnet}" restore "${restore_arguments[@]}" --source "${package_source}"
else
  "${dotnet}" restore "${restore_arguments[@]}"
fi

if [[ "${verify_replacement}" == 1 ]]; then
  expected_package_root="${working_directory}/expected-packages"
  mkdir -p \
    "${expected_package_root}/avalonia" \
    "${expected_package_root}/renderer" \
    "${expected_package_root}/silk"
  unzip -q \
    "${package_source}/Avalonia.${avalonia_version}.nupkg" \
    -d "${expected_package_root}/avalonia"
  unzip -q \
    "${package_source}/ProGPU.Avalonia.Rendering.${integration_version}.nupkg" \
    -d "${expected_package_root}/renderer"
  unzip -q \
    "${package_source}/ProGPU.Avalonia.SilkNet.${integration_version}.nupkg" \
    -d "${expected_package_root}/silk"

  restored_base="${packages_path}/avalonia/${avalonia_version}/lib/net10.0/Avalonia.Base.dll"
  expected_base="${expected_package_root}/avalonia/lib/net10.0/Avalonia.Base.dll"
  if [[ ! -f "${restored_base}" ]] || ! cmp -s "${restored_base}" "${expected_base}"; then
    echo "Restore did not select the validated local ProGPU Avalonia replacement." >&2
    exit 3
  fi

  restored_renderer="${packages_path}/progpu.avalonia.rendering/${integration_version}/lib/net10.0/Avalonia.ProGpu.dll"
  expected_renderer="${expected_package_root}/renderer/lib/net10.0/Avalonia.ProGpu.dll"
  restored_silk="${packages_path}/progpu.avalonia.silknet/${integration_version}/lib/net10.0/Avalonia.SilkNet.dll"
  expected_silk="${expected_package_root}/silk/lib/net10.0/Avalonia.SilkNet.dll"
  if [[ ! -f "${restored_renderer}" ]] ||
     ! cmp -s "${restored_renderer}" "${expected_renderer}" ||
     [[ ! -f "${restored_silk}" ]] ||
     ! cmp -s "${restored_silk}" "${expected_silk}"; then
    echo "Restore did not select the exact-source ProGPU renderer/Silk.NET packages." >&2
    exit 4
  fi

  verify_restored_package_hash() {
    local package_id="$1"
    local package_version="$2"
    local package_file="$3"
    local normalized_package_id
    local restored_hash_file
    local expected_hash

    normalized_package_id="$(printf '%s' "${package_id}" | tr '[:upper:]' '[:lower:]')"
    restored_hash_file="${packages_path}/${normalized_package_id}/${package_version}/${normalized_package_id}.${package_version}.nupkg.sha512"
    if [[ ! -f "${restored_hash_file}" ]]; then
      echo "Restored package hash is missing for ${package_id} ${package_version}." >&2
      return 1
    fi

    expected_hash="$(openssl dgst -sha512 -binary "${package_file}" | openssl base64 -A)"
    if [[ "$(tr -d '\r\n' < "${restored_hash_file}")" != "${expected_hash}" ]]; then
      echo "Restore selected different bytes for ${package_id} ${package_version}." >&2
      return 1
    fi
  }

  verify_restored_package_hash \
    Avalonia "${avalonia_version}" \
    "${package_source}/Avalonia.${avalonia_version}.nupkg"
  verify_restored_package_hash \
    ProGPU.Avalonia.Rendering "${integration_version}" \
    "${package_source}/ProGPU.Avalonia.Rendering.${integration_version}.nupkg"
  verify_restored_package_hash \
    ProGPU.Avalonia.SilkNet "${integration_version}" \
    "${package_source}/ProGPU.Avalonia.SilkNet.${integration_version}.nupkg"
  for package_id in "${progpu_avalonia_runtime_package_ids[@]}"; do
    verify_restored_package_hash \
      "${package_id}" "${runtime_version}" \
      "${package_source}/${package_id}.${runtime_version}.nupkg"
  done
fi

if [[ "${native_aot}" == 1 ]]; then
  publish_directory="${working_directory}/publish"
  "${dotnet}" publish "${project}" \
    --configuration "${configuration}" \
    --runtime "${runtime_identifier}" \
    --self-contained true \
    --artifacts-path "${consumer_artifacts}" \
    --output "${publish_directory}" \
    --no-restore \
    --verbosity minimal \
    "-p:RestorePackagesPath=${packages_path}" \
    "-p:ProGpuIntegrationPackageVersion=${integration_version}" \
    "-p:ProGpuAvaloniaPackageVersion=${avalonia_version}" \
    "-p:ProGpuRequireReplacement=${require_replacement}" \
    "-p:PublishAot=true" \
    "-p:TrimMode=full" \
    "-p:InvariantGlobalization=true"

  published_executable="${publish_directory}/ProGpuPackageApp"
  if [[ "${runtime_identifier}" == win-* ]]; then
    published_executable="${published_executable}.exe"
  fi
  if [[ ! -x "${published_executable}" && ! -f "${published_executable}" ]]; then
    echo "NativeAOT publish did not produce ${published_executable}." >&2
    exit 5
  fi

  published_executable_bytes="$(wc -c < "${published_executable}" | tr -d '[:space:]')"
  echo "[ProGpuPackageAot] rid=${runtime_identifier} executableBytes=${published_executable_bytes}"
  if [[ "${PROGPU_INTEGRATION_BUILD_ONLY:-0}" != 1 ]]; then
    "${published_executable}" "$@"
  fi
elif [[ "${PROGPU_INTEGRATION_BUILD_ONLY:-0}" == 1 ]]; then
  "${dotnet}" build "${project}" \
    --configuration "${configuration}" \
    --artifacts-path "${consumer_artifacts}" \
    --no-restore \
    --verbosity minimal \
    "-p:RestorePackagesPath=${packages_path}" \
    "-p:ProGpuIntegrationPackageVersion=${integration_version}" \
    "-p:ProGpuAvaloniaPackageVersion=${avalonia_version}" \
    "-p:ProGpuRequireReplacement=${require_replacement}"
else
  "${dotnet}" run \
    --project "${project}" \
    --configuration "${configuration}" \
    --artifacts-path "${consumer_artifacts}" \
    --no-restore \
    "-p:RestorePackagesPath=${packages_path}" \
    "-p:ProGpuIntegrationPackageVersion=${integration_version}" \
    "-p:ProGpuAvaloniaPackageVersion=${avalonia_version}" \
    "-p:ProGpuRequireReplacement=${require_replacement}" \
    -- "$@"
fi
