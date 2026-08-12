#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
expected="${repo_root}/eng/progpu-native-exports.txt"
temporary="$(mktemp -d)"
trap 'rm -r "${temporary}"' EXIT
actual="${temporary}/actual.txt"

case "$(uname -s)" in
  Darwin)
    library="${build_dir}/libprogpu_native.dylib"
    nm -gU "${library}" |
      awk '$2 ~ /^[TDBS]$/ { sub(/^_/, "", $3); print $3 }' |
      LC_ALL=C sort -u > "${actual}"
    ;;
  Linux)
    library="${build_dir}/libprogpu_native.so"
    nm -D --defined-only "${library}" |
      awk '$2 ~ /^[TDBS]$/ { print $3 }' |
      LC_ALL=C sort -u > "${actual}"
    ;;
  *)
    echo "Unsupported native export-verification host $(uname -s)." >&2
    exit 1
    ;;
esac

if [[ ! -f "${library}" ]]; then
  echo "Native renderer library is missing: ${library}" >&2
  exit 1
fi

if ! diff -u "${expected}" "${actual}"; then
  echo "The ProGPU native exported-symbol surface changed." >&2
  exit 1
fi

echo "Verified the ProGPU native exported-symbol allowlist."
