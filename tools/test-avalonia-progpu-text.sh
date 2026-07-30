#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.0.5}"
expected_revision="fee9c561ce036e8a3e8cee2397c75ca599b4790d"
test_project="${avalonia_root}/tests/Avalonia.Skia.UnitTests/Avalonia.Skia.UnitTests.csproj"

"${repo_root}/tools/prepare-avalonia-12.0.5-source.sh"

if [[ ! -f "${test_project}" ]]; then
  echo "Pinned Avalonia text test project was not found at ${test_project}." >&2
  exit 2
fi

actual_revision="$(git -C "${avalonia_root}" rev-parse HEAD)"
if [[ "${actual_revision}" != "${expected_revision}" ]]; then
  echo "Pinned Avalonia revision mismatch: expected ${expected_revision}, found ${actual_revision}." >&2
  exit 3
fi

dotnet build "${test_project}" \
  -c Release \
  -m:1 \
  -p:ProGpuTextShaperTests=true \
  -p:ProGpuSourceRoot="${repo_root}" \
  -p:ProGpuAvaloniaSourceRoot="${avalonia_root}"

target_framework="$(
  cd "${avalonia_root}"
  dotnet msbuild "${test_project}" \
    -getProperty:TargetFramework \
    -p:ProGpuTextShaperTests=true \
    -p:ProGpuSourceRoot="${repo_root}" \
    -p:ProGpuAvaloniaSourceRoot="${avalonia_root}"
)"
runner="${avalonia_root}/tests/Avalonia.Skia.UnitTests/bin/Release/${target_framework}/Avalonia.Skia.UnitTests"

if [[ ! -x "${runner}" ]]; then
  echo "Microsoft.Testing.Platform runner was not produced at ${runner}." >&2
  exit 4
fi

"${runner}" \
  --filter-namespace "Avalonia.Skia.UnitTests.Media.TextFormatting" \
  --output Normal \
  --no-progress
"${runner}" \
  --filter-class "Avalonia.Skia.UnitTests.Media.GlyphRunTests" \
  --output Normal \
  --no-progress
