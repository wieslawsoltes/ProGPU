#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
contract_project="${repo_root}/integration/ProGpuBinaryCompatibility/Contract/OfficialBinaryCompatibilityConsumer.csproj"
host_project="${repo_root}/integration/ProGpuBinaryCompatibility/Host/ProGpuBinaryCompatibilityHost.csproj"
work_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-binary-compat.XXXXXX")"
package_output="${work_root}/packages"
package_version="0.1.0-binary-compat-test"

cleanup() {
  rm -rf -- "${work_root}"
}
trap cleanup EXIT

contract_kinds=()
contract_lanes=()
contract_paths=()
contract_hashes=()

build_contract() {
  local kind="$1"
  local package_version_value="$2"
  local lane="$3"
  local safe_kind="${kind//./-}"
  local safe_version="${package_version_value//./-}"
  local output="${work_root}/contracts/${safe_kind}-${safe_version}"
  local version_property

  if [[ "${kind}" == "SkiaSharp" ]]; then
    version_property="ProGpuSkiaSharpPackageVersion"
  else
    version_property="ProGpuAvaloniaSkiaPackageVersion"
  fi

  dotnet build "${contract_project}" \
    --configuration Release \
    --output "${output}" \
    -p:ProGpuContractKind="${kind}" \
    -p:"${version_property}"="${package_version_value}"

  local assembly="${output}/OfficialBinaryCompatibilityConsumer.dll"
  contract_kinds+=("${kind}")
  contract_lanes+=("${lane}")
  contract_paths+=("${assembly}")
  contract_hashes+=("$(shasum -a 256 "${assembly}" | awk '{print $1}')")
}

# Boundary releases cover every currently released stable major/minor band.
build_contract SkiaSharp 2.80.0 12
build_contract SkiaSharp 2.80.4 12
build_contract SkiaSharp 2.88.0 12
build_contract SkiaSharp 2.88.9 12
build_contract SkiaSharp 3.116.0 12
build_contract SkiaSharp 3.116.1 12
build_contract SkiaSharp 3.119.0 12
build_contract SkiaSharp 3.119.4 12
build_contract SkiaSharp 4.148.0 12
build_contract SkiaSharp 4.150.0 12
build_contract SkiaSharp 4.150.2 12
build_contract SkiaSharp 4.151.0 12
build_contract SkiaSharp 4.151.1 12
build_contract Avalonia.Skia 11.0.0 11
build_contract Avalonia.Skia 11.0.13 11
build_contract Avalonia.Skia 11.1.0 11
build_contract Avalonia.Skia 11.1.5 11
build_contract Avalonia.Skia 11.2.0 11
build_contract Avalonia.Skia 11.2.8 11
build_contract Avalonia.Skia 11.3.0 11
build_contract Avalonia.Skia 11.3.20 11
build_contract Avalonia.Skia 12.0.0 12
build_contract Avalonia.Skia 12.0.5 12
build_contract Avalonia.Skia 12.1.0 12
build_contract Avalonia.Skia 12.1.1 12

run_contracts() {
  local host_output="$1"
  local lane="$2"
  local index

  for index in "${!contract_paths[@]}"; do
    if [[ "${contract_lanes[$index]}" != "${lane}" ]]; then
      continue
    fi

    dotnet "${host_output}/ProGpuBinaryCompatibilityHost.dll" \
      "${contract_kinds[$index]}" \
      "${contract_paths[$index]}"
  done
}

for lane in 11 12; do
  direct_output="${work_root}/direct-${lane}"
  dotnet build "${host_project}" \
    --configuration Release \
    --output "${direct_output}" \
    -p:ProGpuAvaloniaLane="${lane}"
  run_contracts "${direct_output}" "${lane}"
done

dotnet pack \
  "${repo_root}/src/ProGPU.BinaryCompatibility/ProGPU.BinaryCompatibility.csproj" \
  --configuration Release \
  --output "${package_output}" \
  -p:PackageVersion="${package_version}" \
  -p:Version="${package_version}"

for lane in 11 12; do
  if [[ "${lane}" == "11" ]]; then
    avalonia_package_version="11.3.20"
  else
    avalonia_package_version="12.1.1"
  fi

  common_properties=(
    -p:ProGpuBinaryCompatibility=true
    -p:ProGpuBinaryCompatibilityPackageVersion="${package_version}"
    -p:ProGpuAvaloniaLane="${lane}"
    -p:ProGpuAvaloniaSkiaPackageVersion="${avalonia_package_version}"
  )

  dotnet restore "${host_project}" \
    --source "${package_output}" \
    --source "https://api.nuget.org/v3/index.json" \
    "${common_properties[@]}"

  build_output="${work_root}/package-build-${lane}"
  dotnet build "${host_project}" \
    --configuration Release \
    --no-restore \
    --output "${build_output}" \
    "${common_properties[@]}"
  run_contracts "${build_output}" "${lane}"

  publish_output="${work_root}/publish-${lane}"
  dotnet publish "${host_project}" \
    --configuration Release \
    --no-restore \
    --output "${publish_output}" \
    "${common_properties[@]}"
  run_contracts "${publish_output}" "${lane}"
done

for index in "${!contract_paths[@]}"; do
  after_hash="$(shasum -a 256 "${contract_paths[$index]}" | awk '{print $1}')"
  if [[ "${contract_hashes[$index]}" != "${after_hash}" ]]; then
    echo "The precompiled official-package consumer was modified." >&2
    exit 1
  fi
done

echo "All ${#contract_paths[@]} official-package consumer hashes remained unchanged."
