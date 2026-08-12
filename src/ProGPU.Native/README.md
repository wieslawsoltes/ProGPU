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
fixed-device, or ordinary transformed lines. Use `--geometry-kind 3|4` for an
isolated quadratic/cubic Bezier, or `--geometry-curves` for a deterministic
mixed curve scene covering hairline, fixed-device, and ordinary affine
strokes. Use `--geometry-start-cap 0|1|2|3` and
`--geometry-end-cap 0|1|2|3` to select flat, square, round, or triangle caps.
The differential compares a second submission after both pipelines are fully
warmed and reports the optional native compiled-payload hash alongside the
readback hashes. `--sync` includes an individual
device-completion wait inside each renderer's measured interval. Generated
native, managed, and absolute-difference images are written under
`artifacts/progpu-native/differential/`.

Use `--geometry-polylines`, `--geometry-splines`, or `--geometry-dashes` for
the connected-stroke lanes. Geometry benchmarks publish a stable native
content revision, so timed replay reuses compiled CPU vectors and the prior GPU
vertex/index/brush upload exactly as the managed retained scene does.

Use `--paths` for the first Tranche B lane. It transfers compact analytic path
segments, dispatches the shared path-coverage compute shader on a cache miss,
and composites retained atlas quads. Add `--write-images` for native, managed,
and amplified-difference captures. DPI-1 and Retina DPI-2 outputs are
byte-exact against the managed compositor.

Current native parity:

- versioned C ABI and exact backend-ABI rejection;
- borrowed external render targets with retained device/queue ownership;
- one batched draw and one submission for all solid rectangles;
- physical framebuffer sizing and logical-to-physical DPI projection;
- exact `VectorVertex` layout and the shared solid-rectangle shader path;
- indexed mixed analytic rectangle/ellipse/circular-rounded-rectangle fill and
  stroke batches with per-primitive affine transforms;
- indexed line, triangle, and quadrilateral batches, including
  one-device-pixel hairlines, positive fixed-device strokes, conformal scalar
  expansion, and transformed local outlines under anisotropic scale/shear;
- indexed quadratic/cubic Bezier batches: conformal and device-space strokes
  are evaluated by the production 24-section GPU curve shader, while ordinary
  anisotropic/sheared strokes use bounded 24–1,024-section exact local-outline
  compilation before the same indexed GPU pass;
- flat, square, round, and triangle start/end caps for lines and Bezier curves;
  hairline/fixed caps expand after the full affine transform, while ordinary
  non-conformal caps transform their complete local outlines;
- connected open/closed polyline and adaptive rational-spline strokes with all
  transform modes, caps, joins, and reusable odd/even dash styles;
- one affine analytic WebGPU quad for every positive-width round cap, including
  anisotropic/sheared ordinary strokes;
- explicit retained geometry revisions that reuse compiled CPU payloads and
  skip unchanged GPU vertex/index/brush uploads while still encoding and
  submitting the current target pass;
- retained filled line/quadratic/cubic/resolved-arc paths with a native-owned
  R8 coverage atlas, 64-phase tile reuse, shared compute/vector WGSL, and no
  stable-frame raster or payload upload;
- compact reusable per-frame solid-brush tables only for geometry whose shader
  payload occupies the vertex color fields;
- four vertices and six indices per analytic primitive, one draw/submission,
  lazily initialized reusable resources, and no per-primitive WebGPU resource
  allocation;
- reusable uniform/vertex resources with geometric buffer growth;
- headless hardware-WebGPU image verification;
- a typed zero-copy .NET host sharing device, queue, and render target;
- an interactive desktop page cycling reusable 1–4,096 rectangle, analytic,
  geometry, GPU Bezier, connected polyline, dashed, rational-spline, and
  retained compute-path batches;
- exact managed/native pixel differential and matched submission benchmark.

The complete migration sequence and .NET substitution gates are in
[`NATIVE_CPP_ENGINE_SPECIFICATION.md`](../../docs/NATIVE_CPP_ENGINE_SPECIFICATION.md).
The bounded macOS baseline, exact commands, retained trace inventory, and
scope limitations are recorded in
[`NATIVE_CPP_PERFORMANCE_BASELINE.md`](../../docs/NATIVE_CPP_PERFORMANCE_BASELINE.md).
