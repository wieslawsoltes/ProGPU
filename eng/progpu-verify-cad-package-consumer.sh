#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.62}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"
fixture="${repo_root}/eng/fixtures/cad-package-consumer"

for package_id in ACadSharp.ProGPU ProGPU.CAD; do
  package="${package_output}/${package_id}.${package_version}.nupkg"
  if [[ ! -f "${package}" ]]; then
    echo "Required CAD consumer package was not produced: ${package}" >&2
    exit 1
  fi
done

consumer_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-cad-package-consumer.XXXXXX")"
trap 'rm -rf "${consumer_root}"' EXIT
cp -R "${fixture}/." "${consumer_root}/"
export NUGET_PACKAGES="${consumer_root}/packages"

project="${consumer_root}/ProGPU.CAD.PackageConsumer.csproj"
common_properties=(
  "-p:ProGpuPackageVersion=${package_version}"
  "-p:ContinuousIntegrationBuild=true"
)

"${dotnet}" restore "${project}" \
  --source "${package_output}" \
  --source "https://api.nuget.org/v3/index.json" \
  "${common_properties[@]}" \
  --verbosity minimal
"${dotnet}" build "${project}" \
  --configuration "${configuration}" \
  --no-restore \
  "${common_properties[@]}" \
  --verbosity minimal
"${dotnet}" run --project "${project}" \
  --configuration "${configuration}" \
  --no-build \
  --no-restore \
  "${common_properties[@]}"

assets="${consumer_root}/obj/project.assets.json"
if ! grep -Fq "\"ACadSharp.ProGPU/${package_version}\"" "${assets}"; then
  echo "The isolated CAD consumer did not resolve ACadSharp.ProGPU ${package_version}." >&2
  exit 1
fi
if grep -Fq '"ACadSharp/' "${assets}"; then
  echo "The isolated CAD consumer unexpectedly resolved upstream ACadSharp." >&2
  exit 1
fi

echo "Verified isolated ProGPU.CAD package consumer for ${package_version}."
