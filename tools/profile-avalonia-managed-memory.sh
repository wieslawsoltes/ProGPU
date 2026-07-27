#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  echo "Usage: tools/profile-avalonia-managed-memory.sh [output-directory]"
  echo
  echo "Captures matched .NET, native heap, VM residency, physical footprint,"
  echo "GPU telemetry, and forced-GC heaps for source-progpu and Skia"
  echo "ControlCatalog processes. Configuration:"
  echo "  PROGPU_AVALONIA_MANAGED_PAGE=Composition"
  echo "  PROGPU_AVALONIA_MANAGED_BACKENDS=source-progpu,skia"
  echo "  PROGPU_AVALONIA_WARMUP_FRAMES=180"
  echo "  PROGPU_AVALONIA_MEASURE_FRAMES=600"
  echo "  PROGPU_AVALONIA_DIAGNOSTIC_HOLD_SECONDS=120"
  echo "  PROGPU_AVALONIA_DIAGNOSTIC_READY_TIMEOUT_SECONDS=120"
  echo "  PROGPU_AVALONIA_MEMORY_CAPTURE_SECONDS=20"
  echo "  PROGPU_AVALONIA_MEMORY_CAPTURE_INTERVAL_SECONDS=2"
  echo "  PROGPU_AVALONIA_CAPTURE_LIVE_HEAP=1"
  echo "  PROGPU_AVALONIA_KEEP_GCDUMPS=0"
  echo "  PROGPU_AVALONIA_SKIP_BUILD=1"
  exit 0
fi
output_root="${1:-$repo_root/artifacts/avalonia-managed-memory}"
page="${PROGPU_AVALONIA_MANAGED_PAGE:-Composition}"
backend_list="${PROGPU_AVALONIA_MANAGED_BACKENDS:-source-progpu,skia}"
warmup_frames="${PROGPU_AVALONIA_WARMUP_FRAMES:-180}"
measure_frames="${PROGPU_AVALONIA_MEASURE_FRAMES:-600}"
hold_seconds="${PROGPU_AVALONIA_DIAGNOSTIC_HOLD_SECONDS:-120}"
ready_timeout_seconds="${PROGPU_AVALONIA_DIAGNOSTIC_READY_TIMEOUT_SECONDS:-120}"
capture_seconds="${PROGPU_AVALONIA_MEMORY_CAPTURE_SECONDS:-20}"
capture_interval_seconds="${PROGPU_AVALONIA_MEMORY_CAPTURE_INTERVAL_SECONDS:-2}"
capture_live_heap="${PROGPU_AVALONIA_CAPTURE_LIVE_HEAP:-1}"
keep_gcdumps="${PROGPU_AVALONIA_KEEP_GCDUMPS:-0}"
source_app="$repo_root/integration/AvaloniaSourceControlCatalog/bin/Release/net10.0/AvaloniaSourceControlCatalog.dll"
skia_app="$repo_root/integration/AvaloniaSkiaControlCatalogReference/bin/Release/net10.0/AvaloniaSkiaControlCatalogReference.dll"
profiler_app="$repo_root/tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"

if ! command -v dotnet-gcdump >/dev/null 2>&1; then
  echo "dotnet-gcdump is required. Install it with 'dotnet tool install --global dotnet-gcdump'." >&2
  exit 2
fi
if [[ "$capture_live_heap" == "1" ]] &&
   ! command -v dotnet-dump >/dev/null 2>&1; then
  echo "dotnet-dump is required for root-filtered live-heap reports. Install it with 'dotnet tool install --global dotnet-dump' or set PROGPU_AVALONIA_CAPTURE_LIVE_HEAP=0." >&2
  exit 2
fi
if [[ "$capture_live_heap" != "0" && "$capture_live_heap" != "1" ]]; then
  echo "PROGPU_AVALONIA_CAPTURE_LIVE_HEAP must be 0 or 1." >&2
  exit 2
fi
if [[ "$keep_gcdumps" != "0" && "$keep_gcdumps" != "1" ]]; then
  echo "PROGPU_AVALONIA_KEEP_GCDUMPS must be 0 or 1." >&2
  exit 2
fi

for numeric_value in \
  "$warmup_frames" \
  "$measure_frames" \
  "$hold_seconds" \
  "$ready_timeout_seconds" \
  "$capture_seconds" \
  "$capture_interval_seconds"; do
  if [[ ! "$numeric_value" =~ ^[1-9][0-9]*$ ]]; then
    echo "Frame counts, hold duration, and ready timeout must be positive integers." >&2
    exit 2
  fi
done

if [[ -e "$output_root" && -n "$(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "Refusing to overwrite non-empty output directory: $output_root" >&2
  exit 2
fi
mkdir -p "$output_root"

IFS=',' read -r -a requested_backends <<< "$backend_list"
backends=()
for requested_backend in "${requested_backends[@]}"; do
  backend="$(printf '%s' "$requested_backend" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$backend" in
    source-progpu|skia)
      backends+=("$backend")
      ;;
    "")
      ;;
    *)
      echo "Unsupported managed-memory backend '$requested_backend'. Use source-progpu or skia." >&2
      exit 2
      ;;
  esac
done

if [[ ${#backends[@]} -eq 0 ]]; then
  echo "No managed-memory backends were selected." >&2
  exit 2
fi

if [[ "${PROGPU_AVALONIA_SKIP_BUILD:-0}" != "1" ]]; then
  PROGPU_AVALONIA_BACKENDS="$backend_list" \
  PROGPU_AVALONIA_BUILD_ONLY=1 \
    "$repo_root/tools/profile-avalonia-controlcatalog.sh" \
    "$output_root/build"
  dotnet build \
    "$repo_root/tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj" \
    -c Release
elif [[ ! -f "$profiler_app" ]]; then
  echo "Missing prepared memory profiler: $profiler_app" >&2
  exit 3
fi

native_arch="$(uname -m)"
case "$native_arch" in
  x86_64|amd64)
    native_arch="x64"
    ;;
  arm64|aarch64)
    native_arch="arm64"
    ;;
esac

terminate_profiled_process()
{
  local process_id="$1"
  if kill -0 "$process_id" 2>/dev/null; then
    kill -TERM "$process_id" 2>/dev/null || true
  fi
  wait "$process_id" 2>/dev/null || true
}

managed_dump_path=""
cleanup_profile_run()
{
  terminate_profiled_process "$launched_pid"
  if [[ -n "$managed_dump_path" && -e "$managed_dump_path" ]]; then
    rm "$managed_dump_path"
  fi
}

for backend in "${backends[@]}"; do
  backend_output="$output_root/$backend"
  mkdir -p "$backend_output"
  ready_path="$backend_output/ready.pid"
  result_path="$backend_output/result.json"
  log_path="$backend_output/process.log"
  gcdump_path="$backend_output/heap.gcdump"
  heap_report_path="$backend_output/heap-report.txt"
  live_heap_report_path="$backend_output/live-heap-report.txt"
  live_capture_path="$backend_output/live-memory.json"
  metadata_path="$backend_output/capture.txt"

  if [[ "$backend" == "source-progpu" ]]; then
    app="$source_app"
    custom_visual_fixture=0
    if [[ "$page" == "Composition" ]]; then
      custom_visual_fixture=1
    fi
  else
    app="$skia_app"
    custom_visual_fixture=0
  fi

  if [[ ! -f "$app" ]]; then
    echo "Missing prepared application: $app" >&2
    exit 3
  fi

  app_root="$(dirname "$app")"
  native_root="$app_root/runtimes/osx-$native_arch/native"
  native_launch=(env)
  if [[ "$(uname -s)" == "Darwin" && -d "$native_root" ]]; then
    native_launch+=(
      "DYLD_LIBRARY_PATH=$native_root${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}")
  fi

  {
    echo "backend=$backend"
    echo "page=$page"
    echo "warmup_frames=$warmup_frames"
    echo "measure_frames=$measure_frames"
    echo "diagnostic_hold_seconds=$hold_seconds"
    echo "capture_seconds=$capture_seconds"
    echo "capture_interval_seconds=$capture_interval_seconds"
    echo "capture_live_heap=$capture_live_heap"
    echo "keep_gcdumps=$keep_gcdumps"
    echo "application=$app"
    echo "collector=$(command -v dotnet-gcdump)"
    if [[ "$capture_live_heap" == "1" ]]; then
      echo "live_heap_collector=$(command -v dotnet-dump)"
    fi
    echo "memory_profiler=$profiler_app"
    dotnet --info
  } > "$metadata_path"

  echo "[AvaloniaManagedMemory] launching backend=$backend page=$page"
  PROGPU_AVALONIA_BENCHMARK_OUTPUT="$result_path" \
  PROGPU_AVALONIA_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
  PROGPU_AVALONIA_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
  PROGPU_AVALONIA_BENCHMARK_RUN=1 \
  PROGPU_AVALONIA_BENCHMARK_CUSTOM_VISUAL="$custom_visual_fixture" \
  PROGPU_AVALONIA_BENCHMARK_DIAGNOSTIC_HOLD_SECONDS="$hold_seconds" \
  PROGPU_AVALONIA_BENCHMARK_DIAGNOSTIC_READY="$ready_path" \
    "${native_launch[@]}" \
    dotnet "$app" --page "$page" > "$log_path" 2>&1 &
  launched_pid=$!

  cleanup_needed=1
  trap 'if [[ "${cleanup_needed:-0}" == "1" ]]; then cleanup_profile_run; fi' EXIT

  ready_deadline=$((SECONDS + ready_timeout_seconds))
  while [[ ! -s "$ready_path" ]]; do
    if ! kill -0 "$launched_pid" 2>/dev/null; then
      echo "Profiled process exited before the diagnostic hold. See $log_path." >&2
      exit 4
    fi
    if ((SECONDS >= ready_deadline)); then
      echo "Timed out waiting for the diagnostic hold. See $log_path." >&2
      exit 4
    fi
    sleep 0.25
  done

  ready_pid="$(tr -d '[:space:]' < "$ready_path")"
  if [[ "$ready_pid" != "$launched_pid" ]]; then
    echo "Diagnostic marker PID $ready_pid does not match launched PID $launched_pid." >&2
    exit 4
  fi

  echo "[AvaloniaManagedMemory] collecting live memory backend=$backend pid=$launched_pid"
  dotnet "$profiler_app" capture \
    --pid "$launched_pid" \
    --duration "$capture_seconds" \
    --interval "$capture_interval_seconds" \
    --native-heap \
    --benchmark-json "$result_path" \
    --output "$live_capture_path"

  echo "[AvaloniaManagedMemory] collecting forced-GC heap backend=$backend pid=$launched_pid"
  dotnet-gcdump collect \
    --process-id "$launched_pid" \
    --output "$gcdump_path"
  dotnet-gcdump report "$gcdump_path" > "$heap_report_path"

  if [[ "$capture_live_heap" == "1" ]]; then
    managed_dump_path="$backend_output/heap.dmp"
    echo "[AvaloniaManagedMemory] collecting root-filtered live heap backend=$backend pid=$launched_pid"
    dotnet-dump collect \
      --process-id "$launched_pid" \
      --type Heap \
      --output "$managed_dump_path"
    dotnet-dump analyze "$managed_dump_path" \
      -c "dumpheap -live -stat" \
      -c "exit" > "$live_heap_report_path"
    managed_dump_bytes="$(wc -c < "$managed_dump_path" | tr -d '[:space:]')"
    rm "$managed_dump_path"
    managed_dump_path=""
    {
      echo "live_heap_report=$live_heap_report_path"
      echo "temporary_live_heap_dump_bytes=$managed_dump_bytes"
      echo "temporary_live_heap_dump_removed=true"
    } >> "$metadata_path"
  fi

  if [[ "$keep_gcdumps" != "1" ]]; then
    gcdump_bytes="$(wc -c < "$gcdump_path" | tr -d '[:space:]')"
    rm "$gcdump_path"
    {
      echo "raw_gcdump_bytes=$gcdump_bytes"
      echo "raw_gcdump_removed=true"
    } >> "$metadata_path"
  fi

  terminate_profiled_process "$launched_pid"
  cleanup_needed=0
  trap - EXIT

  if [[ ! -s "$result_path" ||
        ! -s "$live_capture_path" ||
        ! -s "$heap_report_path" ||
        ("$capture_live_heap" == "1" &&
         ! -s "$live_heap_report_path") ]]; then
    echo "Managed-memory capture was incomplete for $backend." >&2
    exit 5
  fi

  echo "[AvaloniaManagedMemory] captured backend=$backend live=$live_capture_path heap=$heap_report_path"
done

echo "[AvaloniaManagedMemory] completed=${#backends[@]} output=$output_root"
