#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/sample-memory-profile}"
page_filter="${PROGPU_MEMORY_PAGE_FILTER:-.*}"
warmup_frames="${PROGPU_MEMORY_WARMUP_FRAMES:-180}"
measure_frames="${PROGPU_MEMORY_MEASURE_FRAMES:-600}"
keep_traces="${PROGPU_MEMORY_KEEP_TRACES:-1}"
desktop_project="$repo_root/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj"
desktop_app="$repo_root/src/ProGPU.Samples.Desktop/bin/Release/net10.0/ProGPU.Samples.Desktop.dll"
analyzer_project="$repo_root/tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj"
analyzer_app="$repo_root/tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"
page_source="$repo_root/src/ProGPU.Samples/Windows/MainWindowController.cs"

mkdir -p "$output_root"

if [[ "${PROGPU_MEMORY_SKIP_BUILD:-0}" != "1" ]]; then
  dotnet restore "$desktop_project"
  dotnet build "$desktop_project" -c Release --no-restore
  dotnet restore "$analyzer_project"
  dotnet build "$analyzer_project" -c Release --no-restore
fi

pages=()
while IFS= read -r page; do
  pages+=("$page")
done < <(
  sed -n '/var basicInputItem = PageItem/,/var pathOpsItem = PageItem/p' "$page_source" |
    sed -n 's/.*PageItem("\([^"]*\)".*/\1/p'
)

if [[ ${#pages[@]} -eq 0 ]]; then
  echo "No sample pages were discovered in $page_source" >&2
  exit 3
fi

providers='Microsoft-DotNETCore-SampleProfiler:0:5,Microsoft-Windows-DotNETRuntime:0x8000300201b:5,ProGPU-SampleBenchmark:0xffffffffffffffff:4'
completed=0

for page in "${pages[@]}"; do
  if [[ ! "$page" =~ $page_filter ]]; then
    continue
  fi

  slug="$(printf '%s' "$page" | tr '[:upper:]' '[:lower:]' | sed -E 's/[^a-z0-9]+/-/g; s/^-|-$//g')"
  trace_path="$output_root/$slug.nettrace"
  log_path="$output_root/$slug.log"
  json_path="$output_root/$slug.json"

  echo "[MemoryProfile] page=$page"
  PROGPU_SAMPLE_BENCHMARK_PAGE="$page" \
  PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
  PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
  PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
  PROGPU_SAMPLE_BENCHMARK_SCROLL=true \
  PROGPU_SAMPLE_BENCHMARK_MEMORY=true \
    dotnet-trace collect \
      --show-child-io \
      --buffersize 256 \
      --output "$trace_path" \
      --providers "$providers" \
      -- dotnet "$desktop_app" 2>&1 | tee "$log_path"

  dotnet "$analyzer_app" analyze "$trace_path" "$log_path" "$json_path"
  if [[ "$keep_traces" != "1" ]]; then
    rm -f "$trace_path"
  fi
  completed=$((completed + 1))
done

if [[ $completed -eq 0 ]]; then
  echo "No pages matched PROGPU_MEMORY_PAGE_FILTER=$page_filter" >&2
  exit 4
fi

dotnet "$analyzer_app" summarize \
  "$output_root" \
  "$output_root/summary.json" \
  "$output_root/summary.md"

echo "[MemoryProfile] completed=$completed report=$output_root/summary.md"
