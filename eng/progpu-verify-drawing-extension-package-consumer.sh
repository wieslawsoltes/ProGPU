#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.62}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/Release}"
configuration="${PROGPU_CONFIGURATION:-Release}"
consumer_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-drawing-consumer.XXXXXX")"
trap 'rm -rf "${consumer_root}"' EXIT
cp -R "${repo_root}/eng/fixtures/drawing-extension-package-consumer/." "${consumer_root}/"
project="${consumer_root}/DrawingExtension.PackageConsumer.csproj"
export NUGET_PACKAGES="${consumer_root}/packages"

dotnet restore "${project}" --source "${package_output}" --source https://api.nuget.org/v3/index.json \
  "-p:ProGpuPackageVersion=${package_version}" --verbosity minimal
dotnet build "${project}" -c "${configuration}" --no-restore "-p:ProGpuPackageVersion=${package_version}" --verbosity minimal
arguments=()
if [[ "${PROGPU_EXTENSION_GPU_TEST:-0}" == 1 ]]; then arguments+=(--gpu); fi
dotnet run --project "${project}" -c "${configuration}" --no-build --no-restore \
  "-p:ProGpuPackageVersion=${package_version}" -- "${arguments[@]}"
