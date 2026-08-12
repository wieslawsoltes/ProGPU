#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="${PROGPU_NATIVE_WGPU_SOURCE:-${repo_root}/artifacts/wgpu-native-src}"
build_dir="${PROGPU_NATIVE_BUILD_DIR:-${repo_root}/artifacts/progpu-native/build}"
sample_dir="${PROGPU_NATIVE_SAMPLE_DIR:-${repo_root}/artifacts/progpu-native/sample}"
include_dir="${PROGPU_NATIVE_INCLUDE_DIR:-${repo_root}/artifacts/progpu-native/include}"
runtime_dir="${PROGPU_NATIVE_RUNTIME_DIR:-${repo_root}/artifacts/progpu-native/runtime}"
expected_commit="33133da4ec5a0174cb21539ef2d3346f75200411"
expected_headers_commit="aef5e428a1fdab2ea770581ae7c95d8779984e0a"
package_version="2.23.0"

if [[ ! -d "${source_dir}/.git" ]]; then
  git clone --filter=blob:none https://github.com/gfx-rs/wgpu-native.git \
    "${source_dir}"
fi
git -C "${source_dir}" fetch --depth 1 origin "${expected_commit}"
git -C "${source_dir}" checkout --detach "${expected_commit}"
git -C "${source_dir}" submodule update --init --depth 1 ffi/webgpu-headers

actual_commit="$(git -C "${source_dir}" rev-parse HEAD)"
headers_commit="$(git -C "${source_dir}/ffi/webgpu-headers" rev-parse HEAD)"
if [[ "${actual_commit}" != "${expected_commit}" ]]; then
  echo "Expected wgpu-native ${expected_commit}, found ${actual_commit}." >&2
  exit 1
fi
if [[ "${headers_commit}" != "${expected_headers_commit}" ]]; then
  echo "Expected WebGPU headers ${expected_headers_commit}, found ${headers_commit}." >&2
  exit 1
fi

global_packages="$(dotnet nuget locals global-packages --list | sed -E 's/^[^:]+:[[:space:]]*//')"
package_root="${global_packages%/}/silk.net.webgpu.native.wgpu/${package_version}"
case "$(uname -s)-$(uname -m)" in
  Darwin-arm64)
    package_library="${package_root}/runtimes/osx-arm64/native/libwgpu_native.dylib"
    ;;
  Darwin-x86_64)
    package_library="${package_root}/runtimes/osx-x64/native/libwgpu_native.dylib"
    ;;
  Linux-x86_64)
    package_library="${package_root}/runtimes/linux-x64/native/libwgpu_native.so"
    ;;
  Linux-aarch64|Linux-arm64)
    package_library="${package_root}/runtimes/linux-arm64/native/libwgpu_native.so"
    ;;
  *)
    echo "The initial native build script supports macOS and Linux. " \
         "Use the CMake cache variables directly for another platform." >&2
    exit 1
    ;;
esac
if [[ ! -f "${package_library}" ]]; then
  echo "Missing ${package_library}; restore ProGPU.Backend first." >&2
  exit 1
fi

mkdir -p "${include_dir}"
cp "${source_dir}/ffi/webgpu-headers/webgpu.h" "${include_dir}/webgpu.h"
cp "${source_dir}/ffi/wgpu.h" "${include_dir}/wgpu.h"

mkdir -p "${runtime_dir}"
if [[ "$(uname -s)" == "Darwin" ]]; then
  native_library="${runtime_dir}/libwgpu_native.dylib"
  cp "${package_library}" "${native_library}"
  chmod u+w "${native_library}"
  install_name_tool -id '@rpath/libwgpu_native.dylib' "${native_library}"
else
  native_library="${package_library}"
fi

cmake -S "${repo_root}/src/ProGPU.Native" -B "${build_dir}" \
  -DCMAKE_BUILD_TYPE=Release \
  -DPROGPU_NATIVE_WEBGPU_INCLUDE_DIR="${include_dir}" \
  -DPROGPU_NATIVE_WEBGPU_LIBRARY="${native_library}" \
  -DPROGPU_NATIVE_BUILD_SAMPLE=ON \
  -DBUILD_TESTING=ON
cmake --build "${build_dir}" --config Release --parallel
ctest --test-dir "${build_dir}" -C Release --output-on-failure

mkdir -p "${sample_dir}"
"${build_dir}/progpu_native_sample" \
  "${sample_dir}/progpu-native-sample.ppm"

if [[ "$(uname -s)" == "Darwin" ]]; then
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.ManagedSample/ProGPU.Native.ManagedSample.csproj" \
      -c Release -- \
      "${sample_dir}/progpu-native-managed-sample.ppm"
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --rectangles 384 --warmup 4 --iterations 8
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --rectangles 96 --warmup 4 --iterations 8
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --analytic-kind 1 --rectangles 96 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --rectangles 96 --warmup 4 --iterations 8
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --geometry-kind 0 --geometry-line-mode 2 \
      --rectangles 96 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-curves --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-curves --geometry-start-cap 2 --geometry-end-cap 3 \
      --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-polylines --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-splines --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-dashes --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --atlas-growth --rectangles 1024 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --rectangles 96 --warmup 2 --iterations 4
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --dpi 2 --atlas-growth --rectangles 1024 --warmup 1 --iterations 2
  DYLD_LIBRARY_PATH="${build_dir}:${runtime_dir}${DYLD_LIBRARY_PATH:+:${DYLD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --dpi 2 --rectangles 96 --warmup 1 --iterations 2
else
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.ManagedSample/ProGPU.Native.ManagedSample.csproj" \
      -c Release -- \
      "${sample_dir}/progpu-native-managed-sample.ppm"
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --rectangles 384 --warmup 4 --iterations 8
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --rectangles 96 --warmup 4 --iterations 8
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --analytic-kind 1 --rectangles 96 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --analytic --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --rectangles 96 --warmup 4 --iterations 8
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --geometry-kind 0 --geometry-line-mode 2 \
      --rectangles 96 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-curves --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-curves --geometry-start-cap 2 --geometry-end-cap 3 \
      --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-polylines --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-splines --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry-dashes --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --paths --atlas-growth --rectangles 1024 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --rectangles 96 --warmup 2 --iterations 4
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --dpi 2 --rectangles 96 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --glyphs --dpi 2 --atlas-growth --rectangles 1024 --warmup 1 --iterations 2
  LD_LIBRARY_PATH="${build_dir}:$(dirname "${native_library}")${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}" \
    dotnet run \
      --project "${repo_root}/src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj" \
      -c Release -- \
      --geometry --dpi 2 --rectangles 96 --warmup 1 --iterations 2
fi

echo "ProGPU native renderer built from ${actual_commit}."
echo "Sample: ${sample_dir}/progpu-native-sample.ppm"
echo "Managed sample: ${sample_dir}/progpu-native-managed-sample.ppm"
