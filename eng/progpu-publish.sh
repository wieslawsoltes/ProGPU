#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "NUGET_API_KEY must be set before publishing." >&2
  exit 1
fi

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.3}"
configuration="${PROGPU_CONFIGURATION:-Release}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"
nuget_source="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

PROGPU_PACKAGE_VERSION="${package_version}" \
PROGPU_CONFIGURATION="${configuration}" \
PROGPU_PACKAGE_OUTPUT="${package_output}" \
  "${repo_root}/eng/progpu-pack.sh"

for package_id in "${progpu_package_ids[@]}"; do
  package="${package_output}/${package_id}.${package_version}.nupkg"

  "${dotnet}" nuget push "${package}" \
    --api-key "${NUGET_API_KEY}" \
    --source "${nuget_source}" \
    --skip-duplicate
done

echo "Published ProGPU ${package_version} packages to ${nuget_source}."
