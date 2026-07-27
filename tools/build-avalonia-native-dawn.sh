#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
avalonia_root="${PROGPU_AVALONIA_ROOT:-$repo_root/.worktrees/avalonia-12.0.5}"
source_file="$avalonia_root/native/Avalonia.Native/src/OSX/metal.mm"
project="$avalonia_root/native/Avalonia.Native/src/OSX/Avalonia.Native.OSX.xcodeproj"
destination="${1:-$repo_root/integration/AvaloniaSourceControlCatalog/bin/Release/net10.0/runtimes/osx/native/libAvaloniaNative.dylib}"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "The Avalonia Native Dawn presentation lane requires macOS." >&2
  exit 2
fi
if ! command -v xcodebuild >/dev/null 2>&1; then
  echo "xcodebuild was not found. Install and select Xcode first." >&2
  exit 2
fi
if [[ ! -f "$source_file" || ! -d "$project" ]]; then
  echo "The exact Avalonia source tree is missing. Run tools/prepare-avalonia-12.0.5-source.sh first." >&2
  exit 3
fi
if ! rg -q '_layer\.framebufferOnly[[:space:]]*=[[:space:]]*false;' "$source_file"; then
  echo "The Avalonia CAMetalLayer is not configured for Dawn-importable IOSurfaces." >&2
  exit 4
fi
if [[ ! -d "$(dirname "$destination")" ]]; then
  echo "The destination runtime directory does not exist: $(dirname "$destination")" >&2
  echo "Build AvaloniaSourceControlCatalog before installing the native library." >&2
  exit 5
fi

derived_data="$(mktemp -d "${TMPDIR:-/tmp}/progpu-avalonia-native.XXXXXX")"
cleanup() {
  rm -rf "$derived_data"
}
trap cleanup EXIT

build_log="$derived_data/xcodebuild.log"
if ! xcodebuild \
    -quiet \
    -project "$project" \
    -scheme Avalonia.Native.OSX \
    -configuration Release \
    -derivedDataPath "$derived_data" \
    ONLY_ACTIVE_ARCH=YES \
    CODE_SIGNING_ALLOWED=NO \
    build >"$build_log" 2>&1; then
  tail -200 "$build_log" >&2
  exit 6
fi

product="$derived_data/Build/Products/Release/libAvalonia.Native.OSX.dylib"
if [[ ! -f "$product" ]]; then
  echo "xcodebuild did not produce $product" >&2
  exit 7
fi

cp "$product" "$destination"
codesign --force --sign - "$destination"

echo "[AvaloniaNativeDawn] installed=$destination architecture=$(uname -m)"
