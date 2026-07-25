#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output_root="${1:-$repo_root/artifacts/voxel-instruments/$(date +%Y%m%d-%H%M%S)}"
desktop_project="$repo_root/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj"
desktop_app="$repo_root/src/ProGPU.Samples.Desktop/bin/Release/net10.0/ProGPU.Samples.Desktop"
warmup_frames="${PROGPU_VOXEL_INSTRUMENTS_WARMUP_FRAMES:-180}"
measure_frames="${PROGPU_VOXEL_INSTRUMENTS_MEASURE_FRAMES:-600}"
game_memory_limit="${PROGPU_VOXEL_INSTRUMENTS_GAME_MEMORY_LIMIT:-8s}"

if ! command -v xcrun >/dev/null 2>&1; then
  echo "Xcode command-line tools are required." >&2
  exit 2
fi
if [[ -e "$output_root" ]]; then
  echo "Output path already exists: $output_root" >&2
  exit 3
fi

if [[ "${PROGPU_VOXEL_INSTRUMENTS_SKIP_BUILD:-0}" != "1" ]]; then
  dotnet restore "$desktop_project"
  dotnet build "$desktop_project" -c Release --no-restore
fi

mkdir -p "$output_root"

record() {
  local template="$1"
  local slug="$2"
  shift 2
  xcrun xctrace record \
    --template "$template" \
    --output "$output_root/$slug.trace" \
    --no-prompt \
    "$@" \
    --env PROGPU_SAMPLE_BENCHMARK_PAGE="Voxel Game" \
    --env PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES="$warmup_frames" \
    --env PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES="$measure_frames" \
    --env PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
    --target-stdout "$output_root/$slug.log" \
    --launch -- "$desktop_app"
}

record "Time Profiler" "time-profiler"
record "Game Memory" "game-memory" --time-limit "$game_memory_limit"

echo "[VoxelInstruments] traces=$output_root"
