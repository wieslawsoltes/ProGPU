#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.0.5}"
package_output="${PROGPU_AVALONIA_REPLACEMENT_OUTPUT:-${repo_root}/artifacts/avalonia-replacement}"
runtime_version="${PROGPU_RUNTIME_PACKAGE_VERSION:-0.1.0-preview.33}"

PROGPU_AVALONIA_REPLACEMENT_OUTPUT="${package_output}" \
PROGPU_AVALONIA_ROOT="${avalonia_root}" \
  "${repo_root}/tools/pack-avalonia-progpu-replacement.sh"

PROGPU_CONFIGURATION=Release \
PROGPU_PACKAGE_VERSION="${runtime_version}" \
PROGPU_PACKAGE_OUTPUT="${package_output}" \
PROGPU_PACKAGE_GROUP=avalonia-runtime \
  "${repo_root}/eng/progpu-pack.sh"

PROGPU_CONFIGURATION=Release \
PROGPU_PACKAGE_OUTPUT="${package_output}" \
PROGPU_AVALONIA_SOURCE_ROOT="${avalonia_root}" \
  "${repo_root}/scripts/progpu-pack.sh"

"${repo_root}/tools/validate-avalonia-progpu-no-reflection.sh" \
  "${package_output}"

source "${repo_root}/eng/progpu-package-list.sh"
for package_id in "${progpu_avalonia_runtime_package_ids[@]}"; do
  package="${package_output}/${package_id}.${runtime_version}.nupkg"
  if [[ ! -f "${package}" ]]; then
    echo "Replacement stack is missing exact runtime package ${package}." >&2
    exit 2
  fi
done

echo "Built the exact-source ProGPU Avalonia replacement stack at ${package_output}."
