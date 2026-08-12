# ProGPU.Native

`ProGPU.Native` is the first clean-room C++ rendering slice parallel to the
managed ProGPU compositor. It owns native command encoding, pipeline and buffer
lifetime, batching, submission, and validation while consuming the exact same
[`Vector.wgsl`](../ProGPU.Backend/Shaders/Vector.wgsl) source as the managed
renderer.

The current ABI accepts an existing wgpu-native device, queue, and target view.
It intentionally supports only the May-2024 WebGPU C ABI used by
Silk.NET.WebGPU 2.23.0 and rejects all other ABI identifiers. This prevents a
Dawn handle from being interpreted through wgpu-native descriptor layouts.

Build, test, and run the live offscreen sample from the repository root:

```sh
./eng/build-progpu-native.sh
```

The command writes the verified sample image to
`artifacts/progpu-native/sample/progpu-native-sample.ppm` and then runs the
typed .NET host to produce `progpu-native-managed-sample.ppm` through the same
C++ engine. Third-party headers remain under ignored `artifacts/`; no upstream
implementation is vendored into ProGPU.

Run the interactive desktop gallery directly on the exact wgpu-native backend:

```sh
./eng/run-progpu-native-desktop.sh
```

The launcher builds the independently reproducible CMake target, selects the
portable desktop TFM, and opens the **Native C++ Renderer** page. The ordinary
desktop launch remains on Dawn for native media interop. The two handle domains
are deliberately separate.

Run the matched managed/native rectangle differential and CPU-submission
benchmark after the native build:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --rectangles 384 --warmup 60 --iterations 600
```

Use `LD_LIBRARY_PATH` with the same directories on Linux. The benchmark renders
the same retained scene into two textures on one device, rejects pixel drift,
alternates measurement order, and reports p50/p95/worst CPU submission plus
managed allocation. It does not by itself establish whole-engine parity.

Exercise the first indexed analytic batch with deterministic rectangles,
ellipses, circular rounded rectangles, strokes, and affine transforms:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --analytic --rectangles 512 --warmup 60 --iterations 600
```

Add `--analytic-kind 1` for the tight ellipse-only differential or
`--analytic-kind 2` for rounded rectangles. The mixed gate records the bounded
AA-edge difference from the managed compositor's separate solid-rectangle
stroke specialization; the general analytic paths remain within 3/255 per
channel, and the original rectangle fast path remains byte-exact.
Add `--dpi 2` to render a 480 by 270 logical scene into the 960 by 540 physical
target and exercise Retina projection and analytic derivative coverage.

Exercise the indexed geometry batch with flat-cap lines, transformed fills,
hairlines, fixed-device strokes, and exact non-conformal stroke outlines:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --geometry --rectangles 512 --warmup 60 --iterations 5000 --write-images
```

Use `--geometry-kind 0 --geometry-line-mode 0|1|2` to isolate hairline,
fixed-device, or ordinary transformed lines. `--sync` includes an individual
device-completion wait inside each renderer's measured interval. Generated
native, managed, and absolute-difference images are written under
`artifacts/progpu-native/differential/`.

Current native parity:

- versioned C ABI and exact backend-ABI rejection;
- borrowed external render targets with retained device/queue ownership;
- one batched draw and one submission for all solid rectangles;
- physical framebuffer sizing and logical-to-physical DPI projection;
- exact `VectorVertex` layout and the shared solid-rectangle shader path;
- indexed mixed analytic rectangle/ellipse/circular-rounded-rectangle fill and
  stroke batches with per-primitive affine transforms;
- indexed flat-cap line, triangle, and quadrilateral batches, including
  one-device-pixel hairlines, positive fixed-device strokes, conformal scalar
  expansion, and transformed local outlines under anisotropic scale/shear;
- compact reusable per-frame solid-brush tables only for geometry whose shader
  payload occupies the vertex color fields;
- four vertices and six indices per analytic primitive, one draw/submission,
  lazily initialized reusable resources, and no per-primitive WebGPU resource
  allocation;
- reusable uniform/vertex resources with geometric buffer growth;
- headless hardware-WebGPU image verification.
- a typed zero-copy .NET host sharing device, queue, and render target;
- an interactive desktop page cycling reusable 1–4,096 rectangle, mixed
  analytic, and mixed geometry batches;
- exact managed/native pixel differential and matched submission benchmark.

The complete migration sequence and .NET substitution gates are in
[`NATIVE_CPP_ENGINE_SPECIFICATION.md`](../../docs/NATIVE_CPP_ENGINE_SPECIFICATION.md).
The bounded macOS baseline, exact commands, retained trace inventory, and
scope limitations are recorded in
[`NATIVE_CPP_PERFORMANCE_BASELINE.md`](../../docs/NATIVE_CPP_PERFORMANCE_BASELINE.md).
