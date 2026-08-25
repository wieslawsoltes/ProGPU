#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.1.1}"
expected_revision="e33eaed9c106846b200680751022385d9cc5dc6f"
actual_revision="$(git -C "${avalonia_root}" rev-parse HEAD)"
avalonia_version="${PROGPU_AVALONIA_VERSION:-12.1.1}"
target_framework="${PROGPU_AVALONIA_TARGET_FRAMEWORK:-net10.0}"
package_root="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"
contract_assembly="${package_root}/avalonia/${avalonia_version}/lib/${target_framework}/Avalonia.Base.dll"
implementation_assembly="${avalonia_root}/src/Avalonia.Base/bin/Release/${target_framework}/Avalonia.Base.dll"

if [[ "${actual_revision}" != "${expected_revision}" ]]; then
  echo "Pinned Avalonia revision mismatch: expected ${expected_revision}, found ${actual_revision}." >&2
  exit 2
fi

if [[ ! -f "${contract_assembly}" ]]; then
  dotnet restore "${repo_root}/integration/AvaloniaSourceControlCatalog/AvaloniaSourceControlCatalog.csproj" \
    -p:ProGpuDependencyMode=Source \
    -p:ProGpuSourceRoot="${repo_root}"
fi

dotnet build "${avalonia_root}/src/Avalonia.Base/Avalonia.Base.csproj" \
  -c Release \
  -f "${target_framework}" \
  --no-restore

dotnet msbuild "${repo_root}/tools/validate-avalonia-source-abi.proj" \
  -t:Validate \
  -p:ContractAssembly="${contract_assembly}" \
  -p:ImplementationAssembly="${implementation_assembly}"
