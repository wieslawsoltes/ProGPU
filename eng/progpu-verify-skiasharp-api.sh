#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_root="${1:-$repo_root/artifacts/skiasharp-api}"
tool_project="$repo_root/tools/ProGPU.SkiaSharp.ApiParity/ProGPU.SkiaSharp.ApiParity.csproj"
baseline="$repo_root/eng/skiasharp-api-baseline.json"
candidate="$repo_root/src/SkiaSharp/bin/Release/net10.0/SkiaSharp.dll"
reference="$artifact_root/official/SkiaSharp.Official.dll"

dotnet build "$tool_project" --configuration Release
dotnet run --project "$tool_project" --configuration Release --no-build -- \
  self-test
dotnet run --project "$tool_project" --configuration Release --no-build -- \
  acquire \
  --lock "$baseline" \
  --output "$artifact_root/official"
dotnet build "$repo_root/src/SkiaSharp/SkiaSharp.csproj" --configuration Release
dotnet run --project "$tool_project" --configuration Release --no-build -- \
  compare \
  --lock "$baseline" \
  --reference "$reference" \
  --candidate "$candidate" \
  --json "$artifact_root/report.json" \
  --markdown "$artifact_root/report.md"
