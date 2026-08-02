#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
project="${repo_root}/integration/ProGpuAvaloniaPackageSmoke/ProGpuAvaloniaPackageSmoke.csproj"
mode="${1:-local}"
configuration="Release"
runtime_version="${PROGPU_RUNTIME_PACKAGE_VERSION:-0.1.0-preview.38}"
integration_version="${PROGPU_INTEGRATION_PACKAGE_VERSION:-12.0.5-preview.38}"
isolated_root="$(mktemp -d "${TMPDIR:-/tmp}/progpu-package-smoke.XXXXXX")"
isolated_packages="${isolated_root}/packages"
result_path="${isolated_root}/result.json"
native_aot="${PROGPU_INTEGRATION_NATIVE_AOT:-0}"
runtime_identifier="${PROGPU_INTEGRATION_RUNTIME_IDENTIFIER:-}"
require_retained=0
kernel="$(uname -s)"
architecture="$(uname -m)"
case "${kernel}/${architecture}" in
  Darwin/arm64)
    host_runtime_identifier="osx-arm64"
    ;;
  Darwin/x86_64)
    host_runtime_identifier="osx-x64"
    ;;
  Linux/x86_64)
    host_runtime_identifier="linux-x64"
    ;;
  Linux/aarch64|Linux/arm64)
    host_runtime_identifier="linux-arm64"
    ;;
  *)
    host_runtime_identifier=""
    ;;
esac

cleanup() {
  if [[ -x /usr/bin/trash ]]; then
    /usr/bin/trash "${isolated_root}" 2>/dev/null || true
  elif command -v trash >/dev/null 2>&1; then
    trash "${isolated_root}" 2>/dev/null || true
  else
    rm -rf -- "${isolated_root}"
  fi
}
trap cleanup EXIT

case "${mode}" in
  local)
    package_source="${PROGPU_PACKAGE_SOURCE:-${repo_root}/artifacts/packages/Release}"
    if [[ "${PROGPU_REUSE_PACKAGES:-0}" != "1" ]]; then
      PROGPU_CONFIGURATION="${configuration}" \
      PROGPU_PACKAGE_VERSION="${runtime_version}" \
      PROGPU_PACKAGE_OUTPUT="${package_source}" \
      PROGPU_PACKAGE_GROUP=avalonia-runtime \
        "${repo_root}/eng/progpu-pack.sh"
      PROGPU_CONFIGURATION="${configuration}" \
      PROGPU_PACKAGE_OUTPUT="${package_source}" \
        "${repo_root}/scripts/progpu-pack.sh"
    fi
    ;;
  replacement)
    require_retained=1
    package_source="${PROGPU_PACKAGE_SOURCE:-${repo_root}/artifacts/avalonia-replacement}"
    if [[ "${PROGPU_REUSE_REPLACEMENT_STACK:-0}" != "1" ]]; then
      PROGPU_AVALONIA_REPLACEMENT_OUTPUT="${package_source}" \
        "${repo_root}/tools/pack-avalonia-progpu-stack.sh"
    fi
    ;;
  nuget)
    package_source="https://api.nuget.org/v3/index.json"
    ;;
  *)
    echo "Usage: $0 [local|replacement|nuget]" >&2
    exit 2
    ;;
esac

restore_arguments=(
  restore "${project}"
  --packages "${isolated_packages}"
  --source "${package_source}"
)
if [[ "${native_aot}" == "1" ]]; then
  if [[ -z "${runtime_identifier}" ]]; then
    runtime_identifier="${host_runtime_identifier}"
  fi
  if [[ -z "${runtime_identifier}" ]]; then
    echo "Set PROGPU_INTEGRATION_RUNTIME_IDENTIFIER for ${kernel}/${architecture}." >&2
    exit 2
  fi
  restore_arguments+=(
    --runtime "${runtime_identifier}"
    -p:PublishAot=true
    -p:SelfContained=true
  )
fi
if [[ "${mode}" != "nuget" ]]; then
  restore_arguments+=(
    --source "https://api.nuget.org/v3/index.json"
  )
fi
restore_arguments+=(
  -p:ProGpuIntegrationPackageVersion="${integration_version}"
)

dotnet "${restore_arguments[@]}"
if [[ "${native_aot}" == "1" ]]; then
  publish_root="${isolated_root}/publish"
  NUGET_PACKAGES="${isolated_packages}" dotnet publish "${project}" \
    --configuration "${configuration}" \
    --runtime "${runtime_identifier}" \
    --no-restore \
    --output "${publish_root}" \
    -p:PublishAot=true \
    -p:SelfContained=true
  app="${publish_root}/ProGpuAvaloniaPackageSmoke"
  if [[ ! -x "${app}" ]] ||
     [[ -e "${publish_root}/ProGpuAvaloniaPackageSmoke.dll" ]]; then
    echo "NativeAOT did not produce a standalone native executable." >&2
    exit 4
  fi
  native_aot_size="$(wc -c < "${app}" | tr -d '[:space:]')"
  echo "NativeAOT executable: ${native_aot_size} bytes."
else
  dotnet build "${project}" \
    --configuration "${configuration}" \
    --no-restore
  app="${repo_root}/integration/ProGpuAvaloniaPackageSmoke/bin/${configuration}/net10.0/ProGpuAvaloniaPackageSmoke.dll"
fi

if [[ "${PROGPU_INTEGRATION_BUILD_ONLY:-0}" == "1" ]]; then
  echo "ProGPU Avalonia package-only build passed."
  exit 0
fi

app_root="$(dirname "${app}")"
native_root="${app_root}/runtimes/${runtime_identifier:-${host_runtime_identifier}}/native"
if [[ ! -d "${native_root}" ]]; then
  native_root="${app_root}"
fi
case "${kernel}" in
  Darwin)
    native_environment=(
      "DYLD_LIBRARY_PATH=${native_root}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}"
    )
    ;;
  *)
    native_environment=(
      "LD_LIBRARY_PATH=${native_root}${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"
    )
    ;;
esac

if [[ "${native_aot}" == "1" ]]; then
  if env \
    "${native_environment[@]}" \
    PROGPU_PACKAGE_SMOKE_FRAMES="${PROGPU_PACKAGE_SMOKE_FRAMES:-20}" \
    PROGPU_PACKAGE_SMOKE_OUTPUT="${result_path}" \
    PROGPU_PACKAGE_SMOKE_REQUIRE_RETAINED="${require_retained}" \
    PROGPU_PACKAGE_SMOKE_MULTI_WINDOW="${PROGPU_PACKAGE_SMOKE_MULTI_WINDOW:-0}" \
    PROGPU_PACKAGE_SMOKE_WINDOW_CHROME="${PROGPU_PACKAGE_SMOKE_WINDOW_CHROME:-0}" \
    "${app}"; then
    runtime_exit=0
  else
    runtime_exit=$?
  fi
else
  if env \
    "${native_environment[@]}" \
    PROGPU_PACKAGE_SMOKE_FRAMES="${PROGPU_PACKAGE_SMOKE_FRAMES:-20}" \
    PROGPU_PACKAGE_SMOKE_OUTPUT="${result_path}" \
    PROGPU_PACKAGE_SMOKE_REQUIRE_RETAINED="${require_retained}" \
    PROGPU_PACKAGE_SMOKE_MULTI_WINDOW="${PROGPU_PACKAGE_SMOKE_MULTI_WINDOW:-0}" \
    PROGPU_PACKAGE_SMOKE_WINDOW_CHROME="${PROGPU_PACKAGE_SMOKE_WINDOW_CHROME:-0}" \
    dotnet "${app}"; then
    runtime_exit=0
  else
    runtime_exit=$?
  fi
fi

retained_valid=1
if [[ "${require_retained}" == "1" ]] &&
   ! grep -Eq '"RetainedCompositionFallbackNodes"[[:space:]]*:[[:space:]]*0' "${result_path}" 2>/dev/null; then
  retained_valid=0
fi

multi_window_valid=1
if [[ "${PROGPU_PACKAGE_SMOKE_MULTI_WINDOW:-0}" == "1" ]] &&
   ! grep -Eq '"MultiWindowLifecyclePassed"[[:space:]]*:[[:space:]]*true' "${result_path}" 2>/dev/null; then
  multi_window_valid=0
fi

window_chrome_valid=1
if [[ "${PROGPU_PACKAGE_SMOKE_WINDOW_CHROME:-0}" == "1" ]] &&
   ! grep -Eq '"WindowChromePassed"[[:space:]]*:[[:space:]]*true' "${result_path}" 2>/dev/null; then
  window_chrome_valid=0
fi

if [[ ! -s "${result_path}" ]] ||
   ! grep -Eq '"Passed"[[:space:]]*:[[:space:]]*true' "${result_path}" ||
   [[ "${retained_valid}" != "1" ]] ||
   [[ "${multi_window_valid}" != "1" ]] ||
   [[ "${window_chrome_valid}" != "1" ]]; then
  [[ -s "${result_path}" ]] && cat "${result_path}" >&2
  echo "The package-only runtime smoke did not satisfy the retained-rendering contract." >&2
  exit 3
fi

if [[ "${runtime_exit}" -ne 0 ]]; then
  echo "The package-only process exited with status ${runtime_exit}." >&2
  exit "${runtime_exit}"
fi

echo "ProGPU Avalonia package-only runtime smoke passed (${mode})."
