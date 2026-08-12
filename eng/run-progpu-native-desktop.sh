#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
runtime_dir="${PROGPU_NATIVE_RUNTIME_DIR:-${repo_root}/artifacts/progpu-native/runtime}"

"${repo_root}/eng/build-progpu-native.sh"

case "$(uname -s)" in
  Darwin)
    # The C++ renderer itself is platform-neutral and does not require the
    # AVFoundation-specific macOS TFM. The portable target also avoids making
    # the native renderer smoke depend on an installed Apple runtime pack.
    target_framework="net10.0"
    DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
      dotnet run \
        --project "${repo_root}/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj" \
        --framework "${target_framework}" \
        --configuration Release \
        -- --native-renderer "$@"
    ;;
  Linux)
    target_framework="net10.0"
    LD_LIBRARY_PATH="${build_dir}:${runtime_dir}${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
      dotnet run \
        --project "${repo_root}/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj" \
        --framework "${target_framework}" \
        --configuration Release \
        -- --native-renderer "$@"
    ;;
  *)
    echo "The native desktop launcher currently supports macOS and Linux." >&2
    exit 1
    ;;
esac
