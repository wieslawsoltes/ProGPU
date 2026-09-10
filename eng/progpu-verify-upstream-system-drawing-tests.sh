#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
winforms_commit="b8acee9d29af0ed4c9049cea5f05f80570ecf3b0"
runtime_assets_commit="13b701d371826c168b26d581351422c031089faa"
checkout_root="${PROGPU_SYSTEM_DRAWING_UPSTREAM_ROOT:-$repo_root/artifacts/system-drawing-upstream}"
artifact_root="${PROGPU_SYSTEM_DRAWING_UPSTREAM_ARTIFACTS:-$repo_root/artifacts/system-drawing-upstream/results}"
update_baseline=0

if [[ "${1:-}" == "--update-baseline" ]]; then
  update_baseline=1
elif [[ $# -ne 0 ]]; then
  echo "Usage: $0 [--update-baseline]" >&2
  exit 2
fi

case "$(uname -s)" in
  Linux) os=linux ;;
  Darwin) os=osx ;;
  MINGW*|MSYS*|CYGWIN*) os=windows ;;
  *) echo "Unsupported operating system: $(uname -s)" >&2; exit 2 ;;
esac

case "$(uname -m)" in
  x86_64|amd64) arch=x64 ;;
  arm64|aarch64) arch=arm64 ;;
  *) echo "Unsupported architecture: $(uname -m)" >&2; exit 2 ;;
esac

platform="$os-$arch"
winforms_root="$checkout_root/winforms"
runtime_assets_root="$checkout_root/runtime-assets"
mkdir -p "$checkout_root" "$artifact_root"

checkout_sparse() {
  local repository="$1"
  local commit="$2"
  local target="$3"
  shift 3

  if [[ ! -d "$target/.git" ]]; then
    git clone --filter=blob:none --no-checkout "$repository" "$target"
    git -C "$target" sparse-checkout init --cone
  fi

  git -C "$target" sparse-checkout set "$@"
  git -C "$target" fetch --depth 1 origin "$commit"
  git -C "$target" checkout --detach "$commit"

  local actual
  actual="$(git -C "$target" rev-parse HEAD)"
  if [[ "$actual" != "$commit" ]]; then
    echo "Pinned checkout mismatch for $target: expected $commit, got $actual" >&2
    exit 1
  fi
}

checkout_sparse \
  https://github.com/dotnet/winforms.git \
  "$winforms_commit" \
  "$winforms_root" \
  src/System.Drawing.Common/tests \
  src/Common/tests/TestUtilities

checkout_sparse \
  https://github.com/dotnet/runtime-assets.git \
  "$runtime_assets_commit" \
  "$runtime_assets_root" \
  src/System.Drawing.Common.TestData \
  src/System.ComponentModel.TypeConverter.TestData \
  src/System.Windows.Extensions.TestData

project="$repo_root/eng/SystemDrawing.UpstreamTests/SystemDrawing.UpstreamTests.csproj"
common_properties=(
  -p:UpstreamWinFormsRoot="$winforms_root"
  -p:UpstreamRuntimeAssetsRoot="$runtime_assets_root"
)

dotnet build "$project" --configuration Release --no-incremental "${common_properties[@]}"

list_json="$artifact_root/discovered-tests.json"
results_xml="$artifact_root/results.xml"
runner_log="$artifact_root/runner.log"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-build \
  "${common_properties[@]}" \
  -- \
  -list tests/json \
  -noColor \
  -noLogo \
  >"$list_json"

method_exclusions=()
while IFS='|' read -r method reason; do
  [[ -z "$method" || "$method" == \#* ]] && continue
  [[ -n "$reason" ]] || { echo "Missing reason for excluded method: $method" >&2; exit 1; }
  method_exclusions+=( -method- "$method" )
done < "$repo_root/eng/system-drawing-upstream-excluded-methods.txt"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-build \
  "${common_properties[@]}" \
  -- \
  -parallel none \
  -longRunning 30 \
  -ignoreFailures \
  -noColor \
  -noLogo \
  -reporter quiet \
  -xml "$results_xml" \
  "${method_exclusions[@]}" \
  >"$runner_log" 2>&1

verifier_arguments=(
  --results "$results_xml"
  --list "$list_json"
  --tests-root "$winforms_root/src/System.Drawing.Common/tests"
  --excluded-files "$repo_root/eng/system-drawing-upstream-excluded-files.txt"
  --excluded-methods "$repo_root/eng/system-drawing-upstream-excluded-methods.txt"
  --known-failures "$repo_root/eng/system-drawing-upstream-known-failures-$platform.txt"
  --known-skips "$repo_root/eng/system-drawing-upstream-known-skips-$platform.txt"
  --known-summary "$repo_root/eng/system-drawing-upstream-known-summary-$platform.txt"
  --artifacts "$artifact_root"
  --platform "$platform"
  --winforms-commit "$winforms_commit"
  --runtime-assets-commit "$runtime_assets_commit"
)

if [[ $update_baseline -eq 1 ]]; then
  verifier_arguments+=( --update-baseline )
fi

dotnet run \
  --project "$repo_root/eng/SystemDrawing.UpstreamTests.Verifier/SystemDrawing.UpstreamTests.Verifier.csproj" \
  --configuration Release \
  -- \
  "${verifier_arguments[@]}"
