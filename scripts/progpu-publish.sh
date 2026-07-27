#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/scripts/progpu-package-list.sh"

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "NUGET_API_KEY must be set before publishing." >&2
  exit 1
fi

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"
nuget_source="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

PROGPU_CONFIGURATION="${configuration}" \
PROGPU_PACKAGE_OUTPUT="${package_output}" \
  "${repo_root}/scripts/progpu-pack.sh"

for index in "${!progpu_avalonia_package_ids[@]}"; do
  package_id="${progpu_avalonia_package_ids[$index]}"
  package_version="${progpu_avalonia_package_versions[$index]}"
  package="${package_output}/${package_id}.${package_version}.nupkg"
  "${dotnet}" nuget push "${package}" \
    --api-key "${NUGET_API_KEY}" \
    --source "${nuget_source}" \
    --skip-duplicate
done

echo "Published ProGPU Avalonia integration packages to ${nuget_source}."
