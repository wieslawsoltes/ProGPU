#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "${repo_root}/eng/progpu-package-list.sh"

classified_projects=()
package_ids=()

array_contains() {
  local candidate="$1"
  shift
  local value
  for value in "$@"; do
    if [[ "${value}" == "${candidate}" ]]; then
      return 0
    fi
  done
  return 1
}

project_package_id() {
  local project="$1"
  local package_id
  package_id="$(sed -nE 's/.*<PackageId>([^<]+)<\/PackageId>.*/\1/p' "${repo_root}/${project}" | head -n 1)"
  if [[ -z "${package_id}" ]]; then
    package_id="$(basename "${project}" .csproj)"
  fi
  printf '%s' "${package_id}"
}

project_is_packable() {
  local project="$1"
  local project_name
  project_name="$(basename "${project}" .csproj)"
  grep -Fq '<IsPackable>true</IsPackable>' "${repo_root}/${project}" ||
    grep -Fq "<IsPackable Condition=\"'\$(MSBuildProjectName)' == '${project_name}'\">true</IsPackable>" "${repo_root}/Directory.Build.props"
}

for index in "${!progpu_package_ids[@]}"; do
  package_id="${progpu_package_ids[$index]}"
  project="${progpu_package_projects[$index]}"

  if [[ ! -f "${repo_root}/${project}" ]]; then
    echo "Shipping package project does not exist: ${project}" >&2
    exit 1
  fi
  if array_contains "${package_id}" "${package_ids[@]-}"; then
    echo "Duplicate shipping package ID: ${package_id}" >&2
    exit 1
  fi
  if array_contains "${project}" "${classified_projects[@]-}"; then
    echo "Project is classified more than once: ${project}" >&2
    exit 1
  fi

  actual_package_id="$(project_package_id "${project}")"
  if [[ "${actual_package_id}" != "${package_id}" ]]; then
    echo "Package ID mismatch for ${project}: expected ${package_id}, found ${actual_package_id}" >&2
    exit 1
  fi
  if ! project_is_packable "${project}"; then
    echo "Shipping project is not packable: ${project}" >&2
    exit 1
  fi

  package_ids+=("${package_id}")
  classified_projects+=("${project}")
done

for project in "${progpu_nonshipping_projects[@]}"; do
  if [[ ! -f "${repo_root}/${project}" ]]; then
    echo "Non-shipping project does not exist: ${project}" >&2
    exit 1
  fi
  if array_contains "${project}" "${classified_projects[@]-}"; then
    echo "Project is classified more than once: ${project}" >&2
    exit 1
  fi
  if project_is_packable "${project}"; then
    echo "Non-shipping project is packable: ${project}" >&2
    exit 1
  fi
  classified_projects+=("${project}")
done

for project in "${progpu_integration_lane_projects[@]}"; do
  if [[ ! -f "${repo_root}/${project}" ]]; then
    echo "Integration-lane project does not exist: ${project}" >&2
    exit 1
  fi
  if array_contains "${project}" "${classified_projects[@]-}"; then
    echo "Project is classified more than once: ${project}" >&2
    exit 1
  fi
  if ! project_is_packable "${project}"; then
    echo "Integration-lane project is not packable: ${project}" >&2
    exit 1
  fi
  classified_projects+=("${project}")
done

while IFS= read -r project; do
  relative_project="${project#"${repo_root}"/}"
  if ! array_contains "${relative_project}" "${classified_projects[@]-}"; then
    echo "Owned project is missing from the shipping/non-shipping manifest: ${relative_project}" >&2
    exit 1
  fi
done < <(find "${repo_root}/src" -type f -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort)

if [[ "${#classified_projects[@]}" -ne 68 ]]; then
  echo "Expected 68 classified projects, found ${#classified_projects[@]}. Update the manifest and this audit count together." >&2
  exit 1
fi

echo "ProGPU package manifest verification succeeded: ${#progpu_package_ids[@]} runtime, ${#progpu_integration_lane_projects[@]} integration-lane, and ${#progpu_nonshipping_projects[@]} non-shipping projects."
