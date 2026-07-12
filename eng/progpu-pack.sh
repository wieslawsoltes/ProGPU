#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.10}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"

mkdir -p "${package_output}"
rm -f \
  "${package_output}"/*."${package_version}".nupkg \
  "${package_output}"/*."${package_version}".snupkg

is_expected_package_artifact() {
  local file_name="$1"
  local package_id
  for package_id in "${progpu_package_ids[@]}"; do
    if [[ "${file_name}" == "${package_id}.${package_version}.nupkg" ||
          "${file_name}" == "${package_id}.${package_version}.snupkg" ]]; then
      return 0
    fi
  done

  return 1
}

echo "Packing ProGPU ${package_version} packages to ${package_output}..."
for index in "${!progpu_package_ids[@]}"; do
  package_id="${progpu_package_ids[$index]}"
  project="${progpu_package_projects[$index]}"

  rm -f \
    "${package_output}/${package_id}.${package_version}.nupkg" \
    "${package_output}/${package_id}.${package_version}.snupkg"

  "${dotnet}" pack "${repo_root}/${project}" \
    --configuration "${configuration}" \
    --output "${package_output}" \
    --verbosity minimal \
    -p:ContinuousIntegrationBuild=true \
    -p:IncludeSymbols=true \
    -p:SymbolPackageFormat=snupkg \
    -p:Version="${package_version}" \
    -p:PackageVersion="${package_version}"

  if [[ ! -f "${package_output}/${package_id}.${package_version}.nupkg" ]]; then
    echo "Expected package was not produced: ${package_output}/${package_id}.${package_version}.nupkg" >&2
    exit 1
  fi

  if [[ ! -f "${package_output}/${package_id}.${package_version}.snupkg" ]]; then
    echo "Expected symbol package was not produced: ${package_output}/${package_id}.${package_version}.snupkg" >&2
    exit 1
  fi
done

unexpected_package_found=0
while IFS= read -r -d '' artifact; do
  file_name="$(basename "${artifact}")"
  if ! is_expected_package_artifact "${file_name}"; then
    echo "Unexpected package artifact in output: ${artifact}" >&2
    unexpected_package_found=1
  fi
done < <(find "${package_output}" -maxdepth 1 -type f \( -name "*.${package_version}.nupkg" -o -name "*.${package_version}.snupkg" \) -print0)

if [[ "${unexpected_package_found}" -ne 0 ]]; then
  exit 1
fi

echo "ProGPU NuGet package build succeeded for ${package_version}."
