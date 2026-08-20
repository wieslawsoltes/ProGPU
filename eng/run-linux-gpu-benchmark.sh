#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"
iterations="${PROGPU_LINUX_GPU_BENCHMARK_ITERATIONS:-5}"
warmup_frames="${PROGPU_LINUX_GPU_BENCHMARK_WARMUP_FRAMES:-180}"
measure_frames="${PROGPU_LINUX_GPU_BENCHMARK_MEASURE_FRAMES:-600}"
timeout_seconds="${PROGPU_LINUX_GPU_BENCHMARK_TIMEOUT_SECONDS:-240}"
page="${PROGPU_LINUX_GPU_BENCHMARK_PAGE:-Basic Input}"
webgpu_implementation="${PROGPU_LINUX_GPU_BENCHMARK_WEBGPU_IMPLEMENTATION:-silk}"
output_dir="${PROGPU_LINUX_GPU_BENCHMARK_OUTPUT:-${repo_root}/artifacts/performance/linux-gpu-benchmark}"

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "The Linux GPU benchmark must run on Linux." >&2
  exit 2
fi

for value_name in iterations warmup_frames measure_frames timeout_seconds; do
  value="${!value_name}"
  if [[ ! "${value}" =~ ^[1-9][0-9]*$ ]]; then
    echo "${value_name} must be a positive integer, got '${value}'." >&2
    exit 2
  fi
done

case "${webgpu_implementation}" in
  dawn|silk|wgpu) ;;
  *)
    echo "PROGPU_LINUX_GPU_BENCHMARK_WEBGPU_IMPLEMENTATION must be dawn, silk, or wgpu." >&2
    exit 2
    ;;
esac

case "$(uname -m)" in
  x86_64) runtime_id="linux-x64" ;;
  aarch64|arm64) runtime_id="linux-arm64" ;;
  *)
    echo "Unsupported Linux architecture: $(uname -m)" >&2
    exit 2
    ;;
esac

sample_project="${repo_root}/src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj"
sample_output="${repo_root}/src/ProGPU.Samples.Desktop/bin/Release/net10.0"
sample_assembly="${sample_output}/ProGPU.Samples.Desktop.dll"
sample_native="${sample_output}/runtimes/${runtime_id}/native"
patched_native="${repo_root}/artifacts/wgpu-native-linux/runtimes/${runtime_id}/native"

if [[ "${webgpu_implementation}" != "dawn" &&
      ! -f "${patched_native}/libwgpu_native.so" ]]; then
  echo "Missing reviewed wgpu-native build under ${patched_native}." >&2
  echo "Run eng/build-wgpu-native-linux.sh first." >&2
  exit 1
fi

if [[ "${PROGPU_LINUX_GPU_BENCHMARK_SKIP_BUILD:-0}" != "1" ]]; then
  "${dotnet}" build "${sample_project}" \
    -c Release \
    -p:ProGpuDesktopLinuxOnly=true \
    -v:minimal
fi
if [[ ! -f "${sample_assembly}" ]]; then
  echo "Missing Release benchmark assembly: ${sample_assembly}" >&2
  exit 1
fi

mkdir -p "${output_dir}"
metadata="${output_dir}/environment.txt"
summary="${output_dir}/results.txt"
{
  printf 'commit=%s\n' "$(git -C "${repo_root}" rev-parse HEAD)"
  printf 'dirty=%s\n' "$(git -C "${repo_root}" status --porcelain | wc -l)"
  printf 'os=%s\n' "$(. /etc/os-release && printf '%s' "${PRETTY_NAME}")"
  printf 'kernel=%s\n' "$(uname -srvm)"
  printf 'architecture=%s\n' "$(uname -m)"
  printf 'processor_count=%s\n' "$(nproc)"
  printf 'dotnet=%s\n' "$("${dotnet}" --version)"
  printf 'runtime_id=%s\n' "${runtime_id}"
  printf 'page=%s\n' "${page}"
  printf 'iterations=%s\n' "${iterations}"
  printf 'warmup_frames=%s\n' "${warmup_frames}"
  printf 'measure_frames=%s\n' "${measure_frames}"
  printf 'vsync=false\n'
  printf 'webgpu_implementation=%s\n' "${webgpu_implementation}"
  printf 'wgpu_backend_override=%s\n' "${WGPU_BACKEND:-automatic}"
  printf 'display=%s\n' "${DISPLAY:-unset}"
  printf 'wayland_display=%s\n' "${WAYLAND_DISPLAY:-unset}"
  if [[ "${webgpu_implementation}" == "dawn" ]]; then
    printf 'ld_library_path=%s%s\n' \
      "${sample_native}" \
      "${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"
  else
    printf 'ld_library_path=%s:%s%s\n' \
      "${patched_native}" \
      "${sample_native}" \
      "${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"
  fi
} > "${metadata}"
: > "${summary}"

for ((iteration = 1; iteration <= iterations; iteration++)); do
  run_log="${output_dir}/run-${iteration}.log"
  echo "Running Linux GPU benchmark ${iteration}/${iterations}..."
  benchmark_library_path="${sample_native}"
  if [[ "${webgpu_implementation}" != "dawn" ]]; then
    benchmark_library_path="${patched_native}:${benchmark_library_path}"
  fi
  if ! timeout --signal=INT --kill-after=5s "${timeout_seconds}s" \
      env \
        LD_LIBRARY_PATH="${benchmark_library_path}${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
        PROGPU_SAMPLE_WEBGPU_IMPLEMENTATION="${webgpu_implementation}" \
        PROGPU_SAMPLE_BENCHMARK_PAGE="${page}" \
        PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES="${warmup_frames}" \
        PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES="${measure_frames}" \
        PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
        "${dotnet}" "${sample_assembly}" > "${run_log}" 2>&1; then
    echo "Benchmark iteration ${iteration} failed; see ${run_log}." >&2
    tail -n 80 "${run_log}" >&2
    exit 1
  fi

  result="$(grep -F '[SampleBenchmark] RESULT' "${run_log}" | tail -n 1 || true)"
  if [[ -z "${result}" ]]; then
    echo "Benchmark iteration ${iteration} produced no result; see ${run_log}." >&2
    exit 1
  fi
  printf 'run=%d %s\n' "${iteration}" "${result}" | tee -a "${summary}"
done

echo "Linux GPU benchmark results: ${summary}"
