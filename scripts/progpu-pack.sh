#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/scripts/progpu-package-list.sh"

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"

mkdir -p "${package_output}"

for index in "${!progpu_avalonia_package_ids[@]}"; do
  package_id="${progpu_avalonia_package_ids[$index]}"
  find "${package_output}" -maxdepth 1 -type f \
    \( -name "${package_id}.*.nupkg" -o \
       -name "${package_id}.*.snupkg" \) \
    -delete
done

echo "Packing ProGPU Avalonia 12 and 11 integration packages..."
for index in "${!progpu_avalonia_package_ids[@]}"; do
  package_id="${progpu_avalonia_package_ids[$index]}"
  package_version="${progpu_avalonia_package_versions[$index]}"
  project="${repo_root}/${progpu_avalonia_package_projects[$index]}"
  pack_arguments=(
    --configuration "${configuration}"
    --output "${package_output}"
    --verbosity minimal
    -p:ContinuousIntegrationBuild=true
    -p:IncludeSymbols=true
    -p:SymbolPackageFormat=snupkg
  )
  if [[ "${package_version}" == 12.1.1-* &&
        -n "${PROGPU_AVALONIA_SOURCE_ROOT:-}" ]]; then
    pack_arguments+=(
      -p:ProGpuAvaloniaSourceRoot="${PROGPU_AVALONIA_SOURCE_ROOT}"
    )
  fi

  "${dotnet}" pack "${project}" "${pack_arguments[@]}"

  for extension in nupkg snupkg; do
    artifact="${package_output}/${package_id}.${package_version}.${extension}"
    if [[ ! -f "${artifact}" ]]; then
      echo "Expected package was not produced: ${artifact}" >&2
      exit 1
    fi
  done
done

is_expected_artifact() {
  local file_name="$1"
  local index
  local package_id
  local package_version
  local extension

  for index in "${!progpu_avalonia_package_ids[@]}"; do
    package_id="${progpu_avalonia_package_ids[$index]}"
    package_version="${progpu_avalonia_package_versions[$index]}"
    for extension in nupkg snupkg; do
      if [[ "${file_name}" == "${package_id}.${package_version}.${extension}" ]]; then
        return 0
      fi
    done
  done

  return 1
}

unexpected_artifact_found=0
while IFS= read -r -d '' artifact; do
  if ! is_expected_artifact "$(basename "${artifact}")"; then
    echo "Unexpected integration package artifact: ${artifact}" >&2
    unexpected_artifact_found=1
  fi
done < <(find "${package_output}" -maxdepth 1 -type f \
  \( -name "ProGPU.Avalonia.Rendering.*.nupkg" -o \
     -name "ProGPU.Avalonia.Rendering.*.snupkg" -o \
     -name "ProGPU.Avalonia.SilkNet.*.nupkg" -o \
     -name "ProGPU.Avalonia.SilkNet.*.snupkg" \) -print0)

if [[ "${unexpected_artifact_found}" -ne 0 ]]; then
  exit 1
fi

echo "ProGPU Avalonia package build succeeded: ${package_output}"
