#!/usr/bin/env bash

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_root="${1:-$repo_root/artifacts/performance/skiasharp}"
progpu_project="$repo_root/tools/ProGPU.SkiaSharp.Benchmarks/ProGPU.SkiaSharp.Benchmarks.csproj"
native_project="$repo_root/tools/ProGPU.SkiaSharp.Benchmarks.Native/ProGPU.SkiaSharp.Benchmarks.Native.csproj"
commit="$(git -C "$repo_root" rev-parse HEAD)"
dirty="clean"
if test -n "$(git -C "$repo_root" status --porcelain)"; then
  dirty="dirty"
fi

mkdir -p "$artifact_root"
dotnet --info > "$artifact_root/dotnet-info.txt"
uname -a > "$artifact_root/uname.txt"
if command -v sw_vers >/dev/null 2>&1; then
  sw_vers > "$artifact_root/macos-version.txt"
fi
if command -v system_profiler >/dev/null 2>&1; then
  system_profiler SPHardwareDataType SPDisplaysDataType > "$artifact_root/macos-hardware.txt"
fi

dotnet build "$native_project" --configuration Release
dotnet build "$progpu_project" --configuration Release

native_dll="$repo_root/tools/ProGPU.SkiaSharp.Benchmarks.Native/bin/Release/net10.0/ProGPU.SkiaSharp.Benchmarks.Native.dll"
progpu_dll="$repo_root/tools/ProGPU.SkiaSharp.Benchmarks/bin/Release/net10.0/ProGPU.SkiaSharp.Benchmarks.dll"
progpu_output_dir="$(dirname "$progpu_dll")"
machine_arch="$(uname -m)"
case "$machine_arch" in
  x86_64|amd64) runtime_arch="x64" ;;
  arm64|aarch64) runtime_arch="arm64" ;;
  *)
    echo "Unsupported benchmark architecture: $machine_arch" >&2
    exit 1
    ;;
esac

case "$(uname -s)" in
  Linux)
    packaged_native_dir="$progpu_output_dir/runtimes/linux-$runtime_arch/native"
    export LD_LIBRARY_PATH="$packaged_native_dir:${LD_LIBRARY_PATH:-}"
    ;;
  Darwin)
    packaged_native_dir="$progpu_output_dir/runtimes/osx-$runtime_arch/native"
    export DYLD_LIBRARY_PATH="$packaged_native_dir:${DYLD_LIBRARY_PATH:-}"
    ;;
  MINGW*|MSYS*|CYGWIN*)
    packaged_native_dir="$progpu_output_dir/runtimes/win-$runtime_arch/native"
    export PATH="$packaged_native_dir:$PATH"
    ;;
  *)
    echo "Unsupported benchmark operating system: $(uname -s)" >&2
    exit 1
    ;;
esac

if test ! -d "$packaged_native_dir"; then
  echo "Packaged WebGPU runtime directory is missing: $packaged_native_dir" >&2
  exit 1
fi

native_results=()
progpu_results=()

run_backend() {
  local backend=$1
  local dll=$2
  local output=$3
  dotnet "$dll" run \
    --backend "$backend" \
    --output "$output" \
    --commit "$commit" \
    --dirty "$dirty"
}

for run in 1 2 3; do
  native_output="$artifact_root/native-$run.json"
  progpu_output="$artifact_root/progpu-$run.json"
  native_results+=("$native_output")
  progpu_results+=("$progpu_output")
  if (( run % 2 == 1 )); then
    run_backend Native "$native_dll" "$native_output"
    run_backend ProGPU "$progpu_dll" "$progpu_output"
  else
    run_backend ProGPU "$progpu_dll" "$progpu_output"
    run_backend Native "$native_dll" "$native_output"
  fi
done

native_joined=$(IFS=';'; printf '%s' "${native_results[*]}")
progpu_joined=$(IFS=';'; printf '%s' "${progpu_results[*]}")
if command -v cygpath >/dev/null 2>&1; then
  native_windows=()
  progpu_windows=()
  for path in "${native_results[@]}"; do
    native_windows+=("$(cygpath -m "$path")")
  done
  for path in "${progpu_results[@]}"; do
    progpu_windows+=("$(cygpath -m "$path")")
  done
  native_joined=$(IFS=';'; printf '%s' "${native_windows[*]}")
  progpu_joined=$(IFS=';'; printf '%s' "${progpu_windows[*]}")
  MSYS2_ARG_CONV_EXCL='*' dotnet "$(cygpath -m "$progpu_dll")" compare \
    --native "$native_joined" \
    --progpu "$progpu_joined" \
    --json "$(cygpath -m "$artifact_root/comparison.json")" \
    --markdown "$(cygpath -m "$artifact_root/comparison.md")"
else
  dotnet "$progpu_dll" compare \
    --native "$native_joined" \
    --progpu "$progpu_joined" \
    --json "$artifact_root/comparison.json" \
    --markdown "$artifact_root/comparison.md"
fi
