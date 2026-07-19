#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
manifest="$repo_root/eng/performance/sample-pages.txt"
project="$repo_root/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj"
configuration="Release"
warmup_frames=180
measure_frames=600
scroll_step=40
vector_engine="atlas"
output_dir="$repo_root/artifacts/performance/$(date -u +%Y%m%dT%H%M%SZ)"
build=true
pages=()

usage() {
  sed -n '/^# Run deterministic/,/^#   logs/p' "$0" | sed 's/^# \{0,1\}//'
}

# Run deterministic, process-isolated sample-page benchmarks.
#
# Usage: eng/progpu-benchmark-pages.sh [options]
#   --page NAME       Benchmark one page. Repeat to select several pages.
#   --all             Benchmark every page in the canonical manifest (default).
#   --warmup N        Warmup frames per process (default: 180).
#   --frames N        Measured frames per process (default: 600).
#   --scroll-step N   Per-frame scroll delta for scroll-enabled pages (default: 40).
#   --vector-engine E Select atlas or wavefront rendering (default: atlas).
#   --output DIR      Result directory (default: artifacts/performance/<UTC stamp>).
#   --no-build        Reuse an existing Release build.
#   --help            Show this help.
#
# Outputs:
#   results.jsonl     One schema-versioned SampleBenchmark JSON object per page.
#   results.csv       Flattened columns suitable for comparisons and CI artifacts.
#   logs/*.log        Complete stdout/stderr for each isolated page process.

while (($#)); do
  case "$1" in
    --page)
      pages+=("${2:?--page requires a page name}")
      shift 2
      ;;
    --all)
      pages=()
      shift
      ;;
    --warmup)
      warmup_frames="${2:?--warmup requires a count}"
      shift 2
      ;;
    --frames)
      measure_frames="${2:?--frames requires a count}"
      shift 2
      ;;
    --scroll-step)
      scroll_step="${2:?--scroll-step requires a value}"
      shift 2
      ;;
    --vector-engine)
      vector_engine="${2:?--vector-engine requires atlas or wavefront}"
      shift 2
      ;;
    --output)
      output_dir="${2:?--output requires a directory}"
      shift 2
      ;;
    --no-build)
      build=false
      shift
      ;;
    --help|-h)
      usage
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      usage >&2
      exit 2
      ;;
  esac
done

case "$(printf '%s' "$vector_engine" | tr '[:upper:]' '[:lower:]')" in
  atlas) vector_engine="Atlas" ;;
  wavefront) vector_engine="Wavefront" ;;
  *)
    echo "Vector engine must be atlas or wavefront: $vector_engine" >&2
    exit 2
    ;;
esac

for value in "$warmup_frames" "$measure_frames"; do
  if [[ ! "$value" =~ ^[1-9][0-9]*$ ]]; then
    echo "Frame counts must be positive integers: $value" >&2
    exit 2
  fi
done

if ((${#pages[@]} == 0)); then
  while IFS= read -r page; do
    [[ -z "$page" || "$page" == \#* ]] && continue
    pages+=("$page")
  done < "$manifest"
fi

mkdir -p "$output_dir/logs"
jsonl="$output_dir/results.jsonl"
csv="$output_dir/results.csv"
: > "$jsonl"

if [[ "$build" == true ]]; then
  dotnet build "$project" --configuration "$configuration" --no-restore
fi

failed=0
for page in "${pages[@]}"; do
  slug="$(printf '%s' "$page" | tr '[:upper:]' '[:lower:]' | tr -cs '[:alnum:]' '-')"
  slug="${slug%-}"
  log="$output_dir/logs/$slug.log"
  echo "[benchmark] $page"

  if ! PROGPU_SAMPLE_BENCHMARK_PAGE="$page" \
      PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
      PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
      PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
      PROGPU_SAMPLE_BENCHMARK_SCROLL=true \
      PROGPU_SAMPLE_BENCHMARK_SCROLL_STEP="$scroll_step" \
      PROGPU_SAMPLE_BENCHMARK_VECTOR_ENGINE="$vector_engine" \
      dotnet run --project "$project" --configuration "$configuration" --no-build \
        >"$log" 2>&1; then
    failed=$((failed + 1))
    echo "[benchmark] FAILED: $page (see $log)" >&2
    continue
  fi

  json="$(sed -n 's/^\[SampleBenchmark\] JSON //p' "$log" | tail -n 1)"
  if [[ -z "$json" ]]; then
    failed=$((failed + 1))
    echo "[benchmark] MISSING RESULT: $page (see $log)" >&2
    continue
  fi

  printf '%s\n' "$json" >> "$jsonl"
  python3 - "$json" <<'PY'
import json
import sys
r = json.loads(sys.argv[1])
print(
    f"[benchmark] completedFPS={r['gpuCompletedFps']:.2f} "
    f"p95={r['frameIntervalP95Ms']:.4f}ms "
    f"compileP95={r['compileP95Ms']:.4f}ms "
    f"acquireP95={r['surfaceAcquireP95Ms']:.4f}ms "
    f"queueMax={r['gpuMaxInFlightFrames']}"
)
PY
done

python3 - "$jsonl" "$csv" <<'PY'
import csv
import json
import sys

source, destination = sys.argv[1:]
with open(source, encoding="utf-8") as stream:
    rows = [json.loads(line) for line in stream if line.strip()]

keys = []
for row in rows:
    for key in row:
        if key not in keys:
            keys.append(key)

with open(destination, "w", encoding="utf-8", newline="") as stream:
    writer = csv.DictWriter(stream, fieldnames=keys)
    writer.writeheader()
    writer.writerows(rows)
PY

echo "[benchmark] JSONL: $jsonl"
echo "[benchmark] CSV:   $csv"
if ((failed != 0)); then
  echo "[benchmark] $failed page(s) failed" >&2
  exit 1
fi
