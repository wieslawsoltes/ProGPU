#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.1.1}"
expected_revision="e33eaed9c106846b200680751022385d9cc5dc6f"
test_project="${avalonia_root}/tests/Avalonia.Base.UnitTests/Avalonia.Base.UnitTests.csproj"

"${repo_root}/tools/prepare-avalonia-12.1.1-source.sh"

actual_revision="$(git -C "${avalonia_root}" rev-parse HEAD)"
if [[ "${actual_revision}" != "${expected_revision}" ]]; then
  echo "Pinned Avalonia revision mismatch: expected ${expected_revision}, found ${actual_revision}." >&2
  exit 2
fi

dotnet build "${test_project}" -c Release

target_framework="$(
  cd "${avalonia_root}"
  dotnet msbuild "${test_project}" -getProperty:TargetFramework
)"
runner="${avalonia_root}/tests/Avalonia.Base.UnitTests/bin/Release/${target_framework}/Avalonia.Base.UnitTests"

if [[ ! -x "${runner}" ]]; then
  echo "Microsoft.Testing.Platform runner was not produced at ${runner}." >&2
  exit 3
fi

"${runner}" \
  --filter-class "Avalonia.Base.UnitTests.Composition.RetainedCompositionRevisionTests" \
  --output Normal \
  --no-progress
