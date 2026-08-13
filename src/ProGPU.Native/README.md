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

Verify that the same renderer source remains compatible with WebScene PR #10's
exact modern WebGPU header contract:

```sh
./eng/progpu-verify-native-dawn-header.sh
```

This builds the separately linked `progpu_native_dawn` shared library with
warnings as errors, runs its fail-closed provider contract test, and verifies
its exported-symbol allowlist. The library has no Dawn or wgpu-native link
dependency: its typed constructor loads every required WebGPU procedure through
a neutral callback backed by WebScene's provider resolver. The ordinary
wgpu-native constructor is disabled in this binary, so the two object domains
cannot be cross-cast accidentally.

Run the macOS-arm64 hardware integration against the exact WebScene provider
and Dawn revisions recorded in `eng/progpu-native-dawn.version.json`:

```sh
./eng/progpu-verify-native-webscene-provider.sh
```

The gate builds WebScene's provider through its own published build entry
point, creates one Metal provider/device/canvas resource domain, renders the
ProGPU C++ frame into the acquired canvas texture, waits for its native queue
submission, presents it, and verifies the external IOSurface retain/release
lifecycle. Production rendering and presentation remain GPU-only and
zero-copy. The gate maps the IOSurface only after presentation for deterministic
pixel verification and a CI evidence image; that readback is test-only.

Both `progpu_native` and `progpu_native_dawn` are staged in the
`ProGPU.Backend.Native` RID package for Linux, macOS, and Windows x64/arm64.
The source-independent package consumer loads both binaries and validates their
distinct backend identities; it executes the existing wgpu-native hardware
render smoke. The exact WebScene provider hardware gate runs separately on
macOS arm64 because that provider revision currently exposes Metal/IOSurface.

ABI v3 also publishes an opaque submission token for each native frame.
External-image owners can poll or wait for that token before recycling a
borrowed texture; stable rendering does not wait and creates no managed
per-frame synchronization object. Platform decoder imports and cross-API
producer fences remain separate adapters.

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

Use `--semantic-scene` for the first whole-scene substitution benchmark rather
than a single-family call:

```sh
DYLD_LIBRARY_PATH="$PWD/artifacts/progpu-native/build:$PWD/artifacts/progpu-native/runtime" \
  dotnet run --project src/ProGPU.Native.Benchmarks/ProGPU.Native.Benchmarks.csproj -c Release -- \
  --semantic-scene --rectangles 384 --warmup 120 --iterations 600 --sync --write-images
```

Both sides retain the same four-quadrant analytic/path/glyph/image workload.
The native side installs one versioned pointer-free snapshot and renders it
through one C ABI call, one command buffer, and one queue submission. The
managed side uses the production retained `Visual`/`Compositor` path. The
report separates CPU submission from GPU-completion wait, publishes snapshot
and frame metrics, requires zero stable vertex/index/texture/coverage upload,
checks zero managed allocation after warm-up, and writes native, managed, and
amplified-difference images. Use multiple alternating Release runs and the
required platform profilers before making a performance claim from this mode.

Add `--group-vector-clip-chain --write-images` to apply the retained
intersection/difference path-mask gate to the selected family. The chain uses
independent affine transforms and cubic coverage, validates mutation rebuilds,
and requires unchanged replay to report a clip-cache hit with zero clip passes,
uploads, family-content rebuilds, or native managed allocation. The native
build scripts run this mode for solid, analytic, geometry, path, glyph, and
image families; `--group-texture-mask` and `--group-rounded-mask` select the
other common-mask representations.

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

Use `--glyphs` for the retained positioned-glyph lane. The managed side shapes
and positions glyph IDs once, while C++ owns outline validation, production
`GlyphRasterizer.wgsl` compute dispatch, the bounded R8 glyph atlas, and one
instanced `Text.wgsl` composite. Add `--dpi 2` for the Retina gate and
`--write-images` for exact native, managed, and amplified-difference captures.
Use `--drain-each-pair` to bound queue depth while measuring CPU submission
without charging the shared GPU completion wait to either renderer; use
`--sync` when deliberately measuring complete GPU work.
Use `--atlas-growth` with `--paths` or `--glyphs` and a sufficiently large
`--rectangles` count to exercise transactional 1024-to-4096 R8 atlas growth,
generation stability, and zero-upload retained replay.

Use `--images` for the retained straight-alpha RGBA8 lane. The first frame
uploads one typed pixel payload and compiles one transformed quad; later frames
reuse the texture, sampler bind group, vertices, indices, and uniforms. Add
`--dpi 2` and `--write-images` for the Retina exact-pixel gate, or `--sync` to
separate CPU submission from the shared WebGPU/Metal completion wait.

Use `--external-images` for the same-device zero-copy lane. It binds an
existing RGBA/BGRA WebGPU texture view directly and performs no native texture
upload. The native renderer retains the view until replacement or disposal;
the caller must keep the underlying texture alive for that interval.

Use `--group-gaussian-blur`, `--group-drop-shadow`, or
`--group-effect-chain` to apply retained GPU effects after any of the six frame
families. The chain benchmark evaluates Gaussian blur followed by source-alpha
drop shadow, compares it with independently nested managed visuals, requires a
five-pass changed graph and zero-dispatch stable replay, and retains three
full-target RGBA8 intermediates. Add `--recompute-group-effect --sync` for the
matched changed-graph GPU-complete distribution or `--write-images` for native,
managed, and amplified-difference screenshots.

Use `--group-blend-mode <GpuBlendMode>` to composite a retained root group
through any of ProGPU's 29 blend modes. Exact Porter-Duff/coefficient modes use
one fixed-function WebGPU composite pass. Multiply, Screen, Overlay, and the
other destination-aware modes retain a bounded source texture and execute one
static WGSL fullscreen pass over the target backdrop. Stable advanced replay
skips the source-family pass, reuses its pipeline and texture, and allocates
zero managed bytes after warm-up. The current ABI applies the mode to the root
group against the frame clear color; semantic nested/backdrop layers remain a
later tranche.

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
  geometrically growing bounded R8 coverage atlas, published generation,
  64-phase tile reuse, shared compute/vector WGSL, and no stable-frame raster
  or payload upload;
- retained positioned glyphs with deduplicated analytic outlines, a
  native-owned geometrically growing bounded R8 glyph atlas, published
  generation/growth counters, production glyph-compute/text-composite WGSL,
  one instanced draw, exact DPI-1/DPI-2 parity, and no stable-frame glyph
  raster or payload upload;
- retained straight-alpha RGBA8 images with checked row stride/source bounds,
  affine destination transform, opacity, persistent nearest/linear samplers,
  production unmasked `Texture.wgsl`, exact DPI-1/DPI-2 parity, and no stable
  texture/vertex/index/uniform upload;
- retained same-device straight-alpha RGBA/BGRA texture views with typed
  device/usage/format/sample validation, zero CPU transfer, and explicit
  borrowed-view lifetime ownership;
- retained anisotropic Gaussian blur, source-alpha drop shadow, and immutable
  one-to-eight-node linear effect chains with bounded texture pooling,
  independent content/effect revisions, and zero-dispatch stable replay;
- all 29 root-group blend/compositing modes, with fixed-function fast paths for
  exact coefficient equations and one destination-aware static WGSL pipeline
  for advanced modes, retained across all six frame families;
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
  retained compute-path batches plus upload-backed and same-device zero-copy
  images;
- exact managed/native pixel differential and matched submission benchmark.

The complete migration sequence and .NET substitution gates are in
[`NATIVE_CPP_ENGINE_SPECIFICATION.md`](../../docs/NATIVE_CPP_ENGINE_SPECIFICATION.md).
The bounded macOS baseline, exact commands, retained trace inventory, and
scope limitations are recorded in
[`NATIVE_CPP_PERFORMANCE_BASELINE.md`](../../docs/NATIVE_CPP_PERFORMANCE_BASELINE.md).
