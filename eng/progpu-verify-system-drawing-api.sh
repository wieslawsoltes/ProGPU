#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
contract_version="${PROGPU_SYSTEM_DRAWING_CONTRACT_VERSION:-10.0.11}"
target_framework="net10.0"
contract_project="${repo_root}/eng/SystemDrawing.ApiCompat/SystemDrawing.ApiCompat.csproj"
implementation_project="${repo_root}/src/System.Drawing.Common/System.Drawing.Common.csproj"
implementation="${repo_root}/src/System.Drawing.Common/bin/${configuration}/${target_framework}/System.Drawing.Common.dll"
suppression_file="${repo_root}/eng/SystemDrawing.ApiCompat/CompatibilitySuppressions.xml"
artifact_root="${repo_root}/artifacts/system-drawing-api-compat"
report="${artifact_root}/current.txt"
update_baseline=0
skip_build=0

for argument in "$@"; do
  case "${argument}" in
    --update-baseline)
      update_baseline=1
      ;;
    --no-build)
      skip_build=1
      ;;
    *)
      echo "Unknown argument: ${argument}" >&2
      exit 2
      ;;
  esac
done

mkdir -p "${artifact_root}"
dotnet tool restore
dotnet restore "${contract_project}" --locked-mode

if [[ "${skip_build}" -eq 0 ]]; then
  dotnet build "${implementation_project}" --configuration "${configuration}" --nologo
fi

if [[ ! -f "${implementation}" ]]; then
  echo "System.Drawing.Common implementation was not found at ${implementation}." >&2
  exit 1
fi

global_packages_line="$(dotnet nuget locals global-packages --list)"
global_packages="${global_packages_line#*: }"
contract="${global_packages}/microsoft.windowsdesktop.app.ref/${contract_version}/ref/${target_framework}/System.Drawing.Common.dll"

if [[ ! -f "${contract}" ]]; then
  echo "System.Drawing.Common contract was not restored at ${contract}." >&2
  exit 1
fi

api_compat=(
  dotnet tool run apicompat --
  --left "${contract}"
  --right "${implementation}"
  --verbosity normal
)

if [[ "${update_baseline}" -eq 1 ]]; then
  "${api_compat[@]}" \
    --generate-suppression-file \
    --suppression-output-file "${suppression_file}" || true
  echo "Updated ${suppression_file}. Review every added suppression before committing."
  exit 0
fi

if [[ ! -f "${suppression_file}" ]]; then
  echo "Missing ${suppression_file}; run $0 --update-baseline and review the generated debt." >&2
  exit 1
fi

set +e
"${api_compat[@]}" >"${report}" 2>&1
report_exit=$?
set -e

missing_types="$(grep -c '^CP0001:' "${report}" || true)"
missing_members="$(grep -c '^CP0002:' "${report}" || true)"
total_diagnostics="$(grep -c '^CP[0-9][0-9][0-9][0-9]:' "${report}" || true)"
other_diagnostics=$((total_diagnostics - missing_types - missing_members))

printf 'System.Drawing.Common API debt: missingTypes=%s missingMembers=%s other=%s total=%s\n' \
  "${missing_types}" "${missing_members}" "${other_diagnostics}" "${total_diagnostics}"

if [[ "${report_exit}" -eq 0 ]]; then
  echo "System.Drawing.Common matches the pinned ${contract_version} contract with no suppressions required."
fi

# Exact suppressions reject new incompatibilities. ApiCompat's default policy also
# rejects suppressions that became unnecessary, forcing the debt file to shrink when
# an API is implemented instead of silently retaining stale baseline entries.
"${api_compat[@]}" --suppression-file "${suppression_file}"
