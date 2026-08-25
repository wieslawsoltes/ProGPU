#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.1.1}"
package_output="${PROGPU_AVALONIA_REPLACEMENT_OUTPUT:-${repo_root}/artifacts/avalonia-replacement}"
package_root="${NUGET_PACKAGES:-${HOME}/.nuget/packages}"
package_version="12.1.1"
official_package="${package_root}/avalonia/${package_version}/avalonia.${package_version}.nupkg"
replacement_package="${package_output}/Avalonia.${package_version}.nupkg"
merged_package="${avalonia_root}/artifacts/nuget/Avalonia.${package_version}.nupkg"

"${repo_root}/tools/prepare-avalonia-12.1.1-source.sh"
"${repo_root}/tools/validate-avalonia-source-abi.sh"

if [[ ! -f "${official_package}" ]]; then
  echo "Official Avalonia package was not found at ${official_package}." >&2
  exit 2
fi

mkdir -p "${package_output}"
if [[ -f "${replacement_package}" ]]; then
  if [[ -x /usr/bin/trash ]]; then
    /usr/bin/trash "${replacement_package}"
  elif command -v trash >/dev/null 2>&1; then
    trash "${replacement_package}"
  else
    rm -f -- "${replacement_package}"
  fi
fi

(
  cd "${avalonia_root}"
  ProGpuReplacementPackage=true \
  ProGpuSourceRoot="${repo_root}" \
  ForcePackAvaloniaNative=true \
  SkipObscurePlatforms=true \
  SkipBuildingSamples=true \
  SkipBuildingTests=true \
    ./build.sh CreateNugetPackages \
      --configuration Release \
      --force-nuget-version "${package_version}" \
      --skip-tests true
)

if [[ ! -f "${merged_package}" ]]; then
  echo "Avalonia's package merge pipeline did not produce ${merged_package}." >&2
  exit 3
fi
cp "${merged_package}" "${replacement_package}"

if [[ ! -f "${replacement_package}" ]]; then
  echo "Expected replacement package was not produced: ${replacement_package}" >&2
  exit 4
fi

temp_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-avalonia-package.XXXXXX")"
cleanup() {
  if [[ -x /usr/bin/trash ]]; then
    /usr/bin/trash "${temp_root}" 2>/dev/null || true
  elif command -v trash >/dev/null 2>&1; then
    trash "${temp_root}" 2>/dev/null || true
  else
    rm -rf -- "${temp_root}"
  fi
}
trap cleanup EXIT
official_root="${temp_root}/official"
replacement_root="${temp_root}/replacement"
mkdir -p "${official_root}" "${replacement_root}"
unzip -q "${official_package}" -d "${official_root}"
unzip -q "${replacement_package}" -d "${replacement_root}"

if [[ ! -f "${replacement_root}/PROGPU-REPLACEMENT.md" ]]; then
  echo "Replacement package provenance notice is missing." >&2
  exit 5
fi

for target_framework in net10.0 net8.0; do
  for asset_kind in lib ref; do
    official_asset_root="${official_root}/${asset_kind}/${target_framework}"
    replacement_asset_root="${replacement_root}/${asset_kind}/${target_framework}"
    if [[ ! -d "${official_asset_root}" || ! -d "${replacement_asset_root}" ]]; then
      echo "Package asset directory is missing: ${asset_kind}/${target_framework}" >&2
      exit 6
    fi

    while IFS= read -r official_assembly; do
      assembly_name="$(basename "${official_assembly}")"
      replacement_assembly="${replacement_asset_root}/${assembly_name}"
      if [[ ! -f "${replacement_assembly}" ]]; then
        echo "Replacement package is missing ${asset_kind}/${target_framework}/${assembly_name}." >&2
        exit 7
      fi

      dotnet msbuild "${repo_root}/tools/validate-avalonia-source-abi.proj" \
        -t:Validate \
        -p:ContractAssembly="${official_assembly}" \
        -p:ImplementationAssembly="${replacement_assembly}"
    done < <(find "${official_asset_root}" -maxdepth 1 -type f -name '*.dll' | sort)

    official_count="$(find "${official_asset_root}" -maxdepth 1 -type f -name '*.dll' | wc -l | xargs)"
    replacement_count="$(find "${replacement_asset_root}" -maxdepth 1 -type f -name '*.dll' | wc -l | xargs)"
    if [[ "${official_count}" != "${replacement_count}" ]]; then
      echo "Assembly count mismatch for ${asset_kind}/${target_framework}: official=${official_count}, replacement=${replacement_count}." >&2
      exit 8
    fi
  done

  source_base="${avalonia_root}/src/Avalonia.Base/bin/Release/${target_framework}/Avalonia.Base.dll"
  packaged_base="${replacement_root}/lib/${target_framework}/Avalonia.Base.dll"
  if ! cmp -s "${source_base}" "${packaged_base}"; then
    echo "Packed Avalonia.Base does not match the validated source build for ${target_framework}." >&2
    exit 9
  fi
done

echo "Validated ProGPU Avalonia replacement package: ${replacement_package}"
