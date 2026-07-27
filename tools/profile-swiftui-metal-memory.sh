#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/swiftui-metal-memory}"
probe_source="$repo_root/tools/ProGPU.SampleMemoryProfiler/Probes/SwiftUiMetalProbe.swift"
probe_info="$repo_root/tools/ProGPU.SampleMemoryProfiler/Probes/SwiftUiMetalProbe-Info.plist"
probe_entitlements="$repo_root/tools/ProGPU.SampleMemoryProfiler/Probes/SwiftUiMetalProbe.entitlements"
profiler_app="$repo_root/tools/ProGPU.SampleMemoryProfiler/bin/Release/net10.0/ProGPU.SampleMemoryProfiler.dll"
duration_seconds="${PROGPU_SWIFTUI_METAL_DURATION_SECONDS:-15}"
window_seconds="${PROGPU_SWIFTUI_METAL_WINDOW_SECONDS:-5}"
live_duration_seconds="${PROGPU_SWIFTUI_METAL_LIVE_DURATION_SECONDS:-6}"
active_probe_pid=""

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "The SwiftUI Metal memory probe requires macOS." >&2
  exit 2
fi
if ! command -v xcrun >/dev/null 2>&1; then
  echo "xcrun and Xcode command-line tools are required." >&2
  exit 2
fi
if [[ -e "$output_root" &&
      -n "$(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  echo "Refusing to overwrite non-empty output directory: $output_root" >&2
  exit 2
fi
for numeric_value in \
  "$duration_seconds" \
  "$window_seconds" \
  "$live_duration_seconds"; do
  if [[ ! "$numeric_value" =~ ^[1-9][0-9]*$ ]]; then
    echo "Capture duration and rolling window must be positive integers." >&2
    exit 2
  fi
done

probe_bundle="$output_root/build/SwiftUiMetalProbe.app"
probe_binary="$probe_bundle/Contents/MacOS/SwiftUiMetalProbe"

cleanup_probe()
{
  if [[ -n "$active_probe_pid" ]] &&
     kill -0 "$active_probe_pid" 2>/dev/null; then
    kill -TERM "$active_probe_pid" 2>/dev/null || true
    wait "$active_probe_pid" 2>/dev/null || true
  fi
  active_probe_pid=""

  if [[ "${PROGPU_SWIFTUI_METAL_KEEP_BINARY:-0}" == "1" ]]; then
    return
  fi

  [[ -f "$probe_binary" ]] && unlink "$probe_binary"
  [[ -f "$probe_bundle/Contents/Info.plist" ]] &&
    unlink "$probe_bundle/Contents/Info.plist"
  [[ -f "$probe_bundle/Contents/_CodeSignature/CodeResources" ]] &&
    unlink "$probe_bundle/Contents/_CodeSignature/CodeResources"
  [[ -d "$probe_bundle/Contents/_CodeSignature" ]] &&
    rmdir "$probe_bundle/Contents/_CodeSignature"
  [[ -d "$probe_bundle/Contents/MacOS" ]] &&
    rmdir "$probe_bundle/Contents/MacOS"
  [[ -d "$probe_bundle/Contents" ]] &&
    rmdir "$probe_bundle/Contents"
  [[ -d "$probe_bundle" ]] && rmdir "$probe_bundle"
  [[ -d "$output_root/build" ]] && rmdir "$output_root/build"
  return 0
}

mkdir -p "$probe_bundle/Contents/MacOS"
trap cleanup_probe EXIT
cp "$probe_info" "$probe_bundle/Contents/Info.plist"
xcrun swiftc \
  -O \
  -whole-module-optimization \
  -framework AppKit \
  -framework Metal \
  -framework MetalKit \
  -framework SwiftUI \
  "$probe_source" \
  -o "$probe_binary"
codesign \
  --force \
  --sign - \
  --timestamp=none \
  --entitlements "$probe_entitlements" \
  "$probe_bundle"

dotnet build \
  "$repo_root/tools/ProGPU.SampleMemoryProfiler/ProGPU.SampleMemoryProfiler.csproj" \
  -c Release

capture_probe()
{
  local lane="$1"
  shift
  local lane_root="$output_root/$lane"
  local process_log="$lane_root/process.log"
  local capture_status=0
  mkdir -p "$lane_root"

  env "$@" "$probe_binary" >"$process_log" 2>&1 &
  local probe_pid="$!"
  active_probe_pid="$probe_pid"
  for _ in {1..100}; do
    if ! kill -0 "$probe_pid" 2>/dev/null; then
      echo "SwiftUI Metal probe exited before its first frame." >&2
      wait "$probe_pid" 2>/dev/null || true
      active_probe_pid=""
      return 4
    fi
    if grep -q "\\[SwiftUiMetalProbe\\] device=" "$process_log"; then
      break
    fi
    sleep 0.1
  done
  if ! grep -q "\\[SwiftUiMetalProbe\\] device=" "$process_log"; then
    echo "SwiftUI Metal probe did not produce a frame within 10 seconds." >&2
    kill -TERM "$probe_pid" 2>/dev/null || true
    wait "$probe_pid" 2>/dev/null || true
    active_probe_pid=""
    return 4
  fi

  dotnet "$profiler_app" capture \
    --pid "$probe_pid" \
    --duration "$live_duration_seconds" \
    --interval 2 \
    --native-heap \
    --no-runtime-counters \
    --output "$lane_root/live-memory.json" ||
    capture_status="$?"

  if (( capture_status == 0 )); then
    dotnet "$profiler_app" instruments \
      --output "$lane_root" \
      --duration "$duration_seconds" \
      --window "$window_seconds" \
      --templates allocations,time,metal \
      --allocation-details \
      --cleanup-traces \
      --cleanup-exports \
      --attach "$probe_pid" ||
      capture_status="$?"
  fi

  kill -TERM "$probe_pid" 2>/dev/null || true
  wait "$probe_pid" 2>/dev/null || true
  active_probe_pid=""
  return "$capture_status"
}

capture_probe default
capture_probe malloc-fallback \
  LIBDISPATCH_CONTINUATION_ALLOCATOR=0

cleanup_probe
trap - EXIT
echo "[SwiftUiMetalProbe] completed output=$output_root"
