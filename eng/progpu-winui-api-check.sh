#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${PROGPU_CONFIGURATION:-Release}"
reference_root="${PROGPU_WINUI_API_REFERENCE_DIR:-${repo_root}/artifacts/winui-api/reference}"
report_root="${PROGPU_WINUI_API_REPORT_DIR:-${repo_root}/artifacts/winui-api/report}"
tool_project="${repo_root}/tools/ProGPU.WinUI.ApiParity/ProGPU.WinUI.ApiParity.csproj"
candidate_project="${repo_root}/src/ProGPU.WinUI/ProGPU.WinUI.csproj"
candidate_assembly="${repo_root}/src/ProGPU.WinUI/bin/${configuration}/net10.0/ProGPU.WinUI.dll"
baseline_lock="${repo_root}/eng/winui-api-baseline.json"

dotnet restore "${tool_project}"
dotnet build \
  "${tool_project}" \
  --configuration "${configuration}" \
  --no-restore

dotnet run \
  --project "${tool_project}" \
  --configuration "${configuration}" \
  --no-build \
  -- \
  acquire \
  --lock "${baseline_lock}" \
  --output "${reference_root}"

if [[ "${PROGPU_WINUI_API_SKIP_CANDIDATE_BUILD:-0}" != "1" ]]; then
  dotnet restore "${candidate_project}"
  dotnet build \
    "${candidate_project}" \
    --configuration "${configuration}" \
    --no-restore \
    -p:ContinuousIntegrationBuild=true
fi

dotnet run \
  --project "${tool_project}" \
  --configuration "${configuration}" \
  --no-build \
  -- \
  compare \
  --lock "${baseline_lock}" \
  --reference "${reference_root}/Microsoft.WinUI.dll;${reference_root}/Microsoft.InteractiveExperiences.Projection.dll" \
  --candidate "${candidate_assembly}" \
  --json "${report_root}/winui-api-parity.json" \
  --markdown "${report_root}/winui-api-parity.md"
