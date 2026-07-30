#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_root="${1:-${PROGPU_AVALONIA_REPLACEMENT_OUTPUT:-${repo_root}/artifacts/avalonia-replacement}}"
integration_version="${PROGPU_INTEGRATION_PACKAGE_VERSION:-12.0.5-preview.31}"
renderer_package="${package_root}/ProGPU.Avalonia.Rendering.${integration_version}.nupkg"
windowing_package="${package_root}/ProGPU.Avalonia.SilkNet.${integration_version}.nupkg"

for package in "${renderer_package}" "${windowing_package}"; do
  if [[ ! -f "${package}" ]]; then
    echo "Exact-source ProGPU package was not found: ${package}" >&2
    exit 2
  fi
done

temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-avalonia-reflection.XXXXXX")"
trap 'rm -rf "${temporary_root}"' EXIT

renderer_assembly="${temporary_root}/Avalonia.ProGpu.dll"
windowing_assembly="${temporary_root}/Avalonia.SilkNet.dll"
unzip -p \
  "${renderer_package}" \
  "lib/net10.0/Avalonia.ProGpu.dll" \
  > "${renderer_assembly}"
unzip -p \
  "${windowing_package}" \
  "lib/net10.0/Avalonia.SilkNet.dll" \
  > "${windowing_assembly}"

dotnet run \
  --project "${repo_root}/tools/ProGPU.AssemblyContractInspector/ProGPU.AssemblyContractInspector.csproj" \
  --configuration Release \
  -- \
  "${renderer_assembly}" \
  "${windowing_assembly}"
