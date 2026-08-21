#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="${PROGPU_NATIVE_BROWSER_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build-browser}"
evidence_dir="${PROGPU_NATIVE_BROWSER_EVIDENCE:-${repo_root}/artifacts/progpu-native/browser-evidence}"
port="${PROGPU_NATIVE_BROWSER_PORT:-4173}"

command -v emcmake >/dev/null 2>&1 || {
  echo "emcmake is required for the ProGPU native browser lane." >&2
  exit 1
}
command -v npm >/dev/null 2>&1 || {
  echo "npm is required for the ProGPU native browser integration test." >&2
  exit 1
}

emcmake cmake \
  -S "${repo_root}/src/ProGPU.Native" \
  -B "${build_dir}" \
  -G Ninja \
  -DCMAKE_BUILD_TYPE=Release \
  -DBUILD_TESTING=OFF \
  -DPROGPU_NATIVE_BUILD_SAMPLE=OFF
cmake --build "${build_dir}" --parallel

npm ci --prefix "${repo_root}/src/ProGPU.Native/browser"
if [[ "${PROGPU_NATIVE_BROWSER_INSTALL_CHROMIUM:-1}" == "1" ]]; then
  npx --prefix "${repo_root}/src/ProGPU.Native/browser" \
    playwright install chromium
fi

python3 -m http.server "${port}" \
  --bind 127.0.0.1 \
  --directory "${build_dir}" \
  >"${build_dir}/http-server.log" 2>&1 &
server_pid=$!
trap 'kill "${server_pid}" 2>/dev/null || true' EXIT

server_ready=0
for _ in {1..600}; do
  if curl --fail --silent --connect-timeout 1 --max-time 1 \
      "http://127.0.0.1:${port}/progpu_native_browser_smoke.html" \
      >/dev/null; then
    server_ready=1
    break
  fi
  if ! kill -0 "${server_pid}" 2>/dev/null; then
    cat "${build_dir}/http-server.log" >&2
    echo "The ProGPU native browser HTTP server exited early." >&2
    exit 1
  fi
  sleep 0.1
done
if [[ "${server_ready}" != "1" ]]; then
  cat "${build_dir}/http-server.log" >&2
  echo "The ProGPU native browser HTTP server did not become ready." >&2
  exit 1
fi

PROGPU_NATIVE_BROWSER_URL="http://127.0.0.1:${port}/progpu_native_browser_smoke.html" \
PROGPU_NATIVE_BROWSER_EVIDENCE="${evidence_dir}" \
  npm test --prefix "${repo_root}/src/ProGPU.Native/browser"
