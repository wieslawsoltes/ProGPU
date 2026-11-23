#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
OUT="$ROOT/artifacts/progpu-rust/browser"
TOOLS="${PROGPU_RUST_BROWSER_TOOLS:-$ROOT/artifacts/progpu-rust/browser-tools}"
BINDGEN="${PROGPU_RUST_WASM_BINDGEN:-wasm-bindgen}"
if [[ "$("$BINDGEN" --version)" != "wasm-bindgen 0.2.130" ]]; then
  echo 'Install wasm-bindgen-cli 0.2.130 or set PROGPU_RUST_WASM_BINDGEN to its executable.' >&2
  exit 1
fi
cargo build --locked --release --target wasm32-unknown-unknown --features browser --example browser
mkdir -p "$OUT/pkg" "$TOOLS"
"$BINDGEN" target/wasm32-unknown-unknown/release/examples/browser.wasm --target web --out-dir "$OUT/pkg" --out-name progpu
cp src/ProGPU.Rust/browser/index.html src/ProGPU.Rust/browser/main.js "$OUT/"
cp src/ProGPU.Rust/browser/package.json src/ProGPU.Rust/browser/package-lock.json "$TOOLS/"
npm ci --prefix "$TOOLS"
PLAYWRIGHT_SKIP_BROWSER_GC=1 "$TOOLS/node_modules/.bin/playwright" install --no-shell chromium
export PROGPU_RUST_BROWSER_TOOLS="$TOOLS"
# Bind an ephemeral loopback port; the child owns and reports its actual address.
node src/ProGPU.Rust/browser/serve-test.mjs "$OUT"
