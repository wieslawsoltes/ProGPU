#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"

dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.27}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"
package_group="${PROGPU_PACKAGE_GROUP:-all}"

case "${package_group}" in
  all)
    selected_package_ids=("${progpu_package_ids[@]}")
    selected_package_projects=("${progpu_package_projects[@]}")
    ;;
  portable)
    selected_package_ids=("${progpu_portable_package_ids[@]}")
    selected_package_projects=("${progpu_portable_package_projects[@]}")
    ;;
  avalonia-runtime)
    selected_package_ids=("${progpu_avalonia_runtime_package_ids[@]}")
    selected_package_projects=("${progpu_avalonia_runtime_package_projects[@]}")
    ;;
  mobile)
    selected_package_ids=("${progpu_mobile_package_ids[@]}")
    selected_package_projects=("${progpu_mobile_package_projects[@]}")
    ;;
  *)
    echo "Unknown PROGPU_PACKAGE_GROUP '${package_group}'. Expected all, portable, avalonia-runtime, or mobile." >&2
    exit 1
    ;;
esac

"${repo_root}/eng/progpu-verify-package-list.sh"

mkdir -p "${package_output}"

echo "Packing ProGPU ${package_version} ${package_group} packages to ${package_output}..."
for index in "${!selected_package_ids[@]}"; do
  package_id="${selected_package_ids[$index]}"
  project="${selected_package_projects[$index]}"

  rm -f \
    "${package_output}/${package_id}.${package_version}.nupkg" \
    "${package_output}/${package_id}.${package_version}.snupkg"

  pack_arguments=(
    --configuration "${configuration}" \
    --output "${package_output}" \
    --verbosity minimal \
    -p:ContinuousIntegrationBuild=true \
    -p:Version="${package_version}" \
    -p:PackageVersion="${package_version}"
  )
  if [[ "${package_id}" == "ProGPU.Xaml.SourceGenerator" ]]; then
    pack_arguments+=(-p:IncludeSymbols=false)
  else
    pack_arguments+=(-p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg)
  fi

  "${dotnet}" pack "${repo_root}/${project}" "${pack_arguments[@]}"

done

PROGPU_PACKAGE_VERSION="${package_version}" \
PROGPU_PACKAGE_OUTPUT="${package_output}" \
PROGPU_PACKAGE_GROUP="${package_group}" \
  "${repo_root}/eng/progpu-verify-packages.sh"

if [[ "${package_group}" == "portable" || "${package_group}" == "all" ]]; then
  PROGPU_CONFIGURATION="${configuration}" \
  PROGPU_PACKAGE_VERSION="${package_version}" \
  PROGPU_PACKAGE_OUTPUT="${package_output}" \
    "${repo_root}/eng/progpu-verify-xaml-package-consumer.sh"
fi

echo "ProGPU ${package_group} NuGet package build succeeded for ${package_version}."
