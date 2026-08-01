#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"

package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.35}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/Release}"
package_group="${PROGPU_PACKAGE_GROUP:-all}"

case "${package_group}" in
  all)
    selected_package_ids=("${progpu_package_ids[@]}")
    ;;
  portable)
    selected_package_ids=("${progpu_portable_package_ids[@]}")
    ;;
  avalonia-runtime)
    selected_package_ids=("${progpu_avalonia_runtime_package_ids[@]}")
    ;;
  mobile)
    selected_package_ids=("${progpu_mobile_package_ids[@]}")
    ;;
  *)
    echo "Unknown PROGPU_PACKAGE_GROUP '${package_group}'. Expected all, portable, avalonia-runtime, or mobile." >&2
    exit 1
    ;;
esac

is_selected_artifact() {
  local file_name="$1"
  local package_id
  for package_id in "${selected_package_ids[@]}"; do
    if [[ "${file_name}" == "${package_id}.${package_version}.nupkg" ||
          "${file_name}" == "${package_id}.${package_version}.snupkg" ]]; then
      return 0
    fi
  done
  return 1
}

is_selected_package_id() {
  local candidate="$1"
  local package_id
  for package_id in "${selected_package_ids[@]}"; do
    if [[ "${candidate}" == "${package_id}" ]]; then
      return 0
    fi
  done
  return 1
}

is_shipping_package_id() {
  local candidate="$1"
  local package_id
  for package_id in "${progpu_package_ids[@]}"; do
    if [[ "${candidate}" == "${package_id}" ]]; then
      return 0
    fi
  done
  return 1
}

is_owned_nonshipping_project_id() {
  local candidate="$1"
  local project
  local project_id
  for project in "${progpu_nonshipping_projects[@]}"; do
    project_id="$(sed -nE 's/.*<PackageId>([^<]+)<\/PackageId>.*/\1/p' "${repo_root}/${project}" | head -n 1)"
    if [[ -z "${project_id}" ]]; then
      project_id="$(basename "${project}" .csproj)"
    fi
    if [[ "${candidate}" == "${project_id}" ]]; then
      return 0
    fi
  done
  return 1
}

for package_id in "${selected_package_ids[@]}"; do
  package="${package_output}/${package_id}.${package_version}.nupkg"
  symbols="${package_output}/${package_id}.${package_version}.snupkg"
  if [[ ! -f "${package}" ]]; then
    echo "Expected package was not produced: ${package}" >&2
    exit 1
  fi
  if [[ "${package_id}" == "ProGPU.Xaml.SourceGenerator" ]]; then
    if [[ -f "${symbols}" ]]; then
      echo "Analyzer-only package must not produce an empty symbol package: ${symbols}" >&2
      exit 1
    fi
    for analyzer_pdb in \
      ProGPU.Xaml.SourceGenerator.pdb \
      ProGPU.Xaml.pdb \
      ProGPU.Xaml.Roslyn.pdb; do
      if ! unzip -Z1 "${package}" | grep -Fx "analyzers/dotnet/cs/${analyzer_pdb}" >/dev/null; then
        echo "${package_id} is missing analyzer symbols ${analyzer_pdb}." >&2
        exit 1
      fi
    done
  elif [[ ! -f "${symbols}" ]]; then
    echo "Expected symbol package was not produced: ${symbols}" >&2
    exit 1
  fi

  while IFS=$'\t' read -r dependency_id dependency_version; do
    [[ -z "${dependency_id}" ]] && continue
    if is_shipping_package_id "${dependency_id}"; then
      if [[ "${dependency_version}" != "${package_version}" ]]; then
        echo "${package_id} depends on ${dependency_id} ${dependency_version}, expected ${package_version}." >&2
        exit 1
      fi
      if [[ "${package_group}" == "avalonia-runtime" ]] && ! is_selected_package_id "${dependency_id}"; then
        echo "${package_id} depends on ${dependency_id}, which is missing from the isolated avalonia-runtime package closure." >&2
        exit 1
      fi
    elif [[ "${dependency_id}" == ProGPU.* || "${dependency_id}" == LibreWPF.* ]] || is_owned_nonshipping_project_id "${dependency_id}"; then
      echo "${package_id} depends on unpublished internal package ${dependency_id}." >&2
      exit 1
    fi
  done < <(unzip -p "${package}" '*.nuspec' | sed -nE 's/.*<dependency id="([^"]+)" version="([^"]+)".*/\1\t\2/p')
done

while IFS= read -r -d '' artifact; do
  file_name="$(basename "${artifact}")"
  if ! is_selected_artifact "${file_name}"; then
    echo "Unexpected ${package_version} package artifact in ${package_group} output: ${artifact}" >&2
    exit 1
  fi
done < <(find "${package_output}" -maxdepth 1 -type f \( -name "*.${package_version}.nupkg" -o -name "*.${package_version}.snupkg" \) -print0)

echo "Verified ${#selected_package_ids[@]} ProGPU ${package_group} packages and symbol packages for ${package_version}."
