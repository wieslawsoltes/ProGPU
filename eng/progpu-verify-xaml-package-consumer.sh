#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${repo_root}/.dotnet/dotnet"
if [[ ! -x "${dotnet}" ]]; then
  dotnet="dotnet"
fi

configuration="${PROGPU_CONFIGURATION:-Release}"
package_version="${PROGPU_PACKAGE_VERSION:-0.1.0-preview.25}"
package_output="${PROGPU_PACKAGE_OUTPUT:-${repo_root}/artifacts/packages/${configuration}}"
fixture="${repo_root}/eng/fixtures/xaml-package-consumer"

for package_id in ProGPU.WinUI ProGPU.Xaml.SourceGenerator ProGPU.Xaml.Cli; do
  package="${package_output}/${package_id}.${package_version}.nupkg"
  if [[ ! -f "${package}" ]]; then
    echo "Required XAML consumer package was not produced: ${package}" >&2
    exit 1
  fi
done

consumer_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-xaml-package-consumer.XXXXXX")"
trap 'rm -rf "${consumer_root}"' EXIT
cp -R "${fixture}/." "${consumer_root}/"
export NUGET_PACKAGES="${consumer_root}/packages"

project="${consumer_root}/ProGPU.Xaml.PackageConsumer.csproj"
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

tool_root="${consumer_root}/tools"
"${dotnet}" tool install ProGPU.Xaml.Cli \
  --tool-path "${tool_root}" \
  --version "${package_version}" \
  --add-source "${package_output}" \
  --verbosity minimal
"${tool_root}/progpu-xaml" parse "${consumer_root}/MainPage.xaml"

generated_source="$(find "${consumer_root}/obj/generated" -type f -name '*.g.cs' -print -quit)"
if [[ -z "${generated_source}" ]]; then
  echo "The packaged source generator produced no inspectable C# output." >&2
  exit 1
fi
if ! grep -Fq 'BindingMemberAccessorRegistry.Register<global::PackageConsumer.ConsumerModel, string>' "${generated_source}"; then
  echo "Generated output did not publish the typed resource-source accessor." >&2
  exit 1
fi
if ! grep -Fq 'XamlResourceResolver.Resolve<object>' "${generated_source}"; then
  echo "Generated output did not retain the runtime resource lookup." >&2
  exit 1
fi

cli_output="${consumer_root}/cli-generated"
"${tool_root}/progpu-xaml" compile "${consumer_root}/MainPage.xaml" \
  --project "${project}" \
  --output "${cli_output}" \
  --framework WinUI \
  --json
cli_source="$(find "${cli_output}" -maxdepth 1 -type f -name '*.g.cs' -print -quit)"
if [[ -z "${cli_source}" ]]; then
  echo "The packaged standalone compiler produced no C# output." >&2
  exit 1
fi
if [[ "$(basename "${generated_source}")" != "$(basename "${cli_source}")" ]]; then
  echo "Generator and CLI hint names differ." >&2
  exit 1
fi
if ! cmp -s "${generated_source}" "${cli_source}"; then
  echo "Generator and CLI C# output is not byte-identical." >&2
  exit 1
fi
generated_prefix="$(LC_ALL=C head -c 3 "${generated_source}" | od -An -tx1 | tr -d ' \n')"
if [[ "${generated_prefix}" == "efbbbf" ]]; then
  echo "Generated C# unexpectedly contains a UTF-8 byte-order mark." >&2
  exit 1
fi

missing_facade_log="${consumer_root}/missing-facade.log"
if "${dotnet}" build "${project}" \
  --configuration "${configuration}" \
  --no-restore \
  "${common_properties[@]}" \
  -p:ProGpuXamlFramework=Missing \
  --verbosity minimal >"${missing_facade_log}" 2>&1; then
  echo "The packaged build unexpectedly accepted a missing XAML generator facade." >&2
  exit 1
fi
if ! grep -Fq "expected exactly one generator facade for framework 'Missing' but found 0" "${missing_facade_log}"; then
  echo "The packaged build did not report deterministic missing-facade selection." >&2
  exit 1
fi

duplicate_facade_log="${consumer_root}/duplicate-facade.log"
if "${dotnet}" build "${project}" \
  --configuration "${configuration}" \
  --no-restore \
  "${common_properties[@]}" \
  -p:ProGpuTestDuplicateXamlGeneratorFacade=true \
  --verbosity minimal >"${duplicate_facade_log}" 2>&1; then
  echo "The packaged build unexpectedly accepted duplicate XAML generator facades." >&2
  exit 1
fi
if ! grep -Fq "expected exactly one generator facade for framework 'WinUI' but found 2" "${duplicate_facade_log}"; then
  echo "The packaged build did not report deterministic duplicate-facade selection." >&2
  exit 1
fi

echo "Verified isolated ProGPU XAML package consumer for ${package_version}."
