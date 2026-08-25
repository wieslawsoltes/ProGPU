#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-${repo_root}/.worktrees/avalonia-12.1.1}"
project="${repo_root}/integration/AvaloniaSourceSandbox/AvaloniaSourceSandbox.csproj"

PROGPU_AVALONIA_ROOT="${avalonia_root}" \
  "${repo_root}/tools/prepare-avalonia-12.1.1-source.sh"

dotnet run \
  --project "${project}" \
  --configuration Release \
  -p:PackAvaloniaNative=false \
  -p:UseSkiaSharpShim=true \
  -p:ProGpuSourceRoot="${repo_root}" \
  -p:ProGpuAvaloniaSourceRoot="${avalonia_root}" \
  -- "$@"
