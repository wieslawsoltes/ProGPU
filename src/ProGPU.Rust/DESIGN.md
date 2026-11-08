# Rust port design and evidence

The authoritative algorithms are the original ProGPU files listed in README.md.
External sources below inform boundaries and tests only; no external engine code
is copied or translated into the implementation.

## Architecture research (2026-11-08)

* [Skia canvas](https://skia.org/docs/user/api/skcanvas_overview/) separates
  matrix/clip state from draw paint. [Skia text overview](https://skia.org/docs/dev/design/text_overview/)
  separates text processing stages. Adopt those boundaries already present in
  ProGPU: immutable source geometry/text and explicit replay state.
* [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
  offers reusable layout and lower-level glyph rendering. Preserve ProGPU's
  positioned glyph ownership independently of rendering; no platform shaper
  substitution or implicit equivalence between grayscale and ClearType.
* [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  distinguish device-independent and dependent resources. Keep CPU geometry
  after device loss, invalidate GPU realizations, and reject cross-device replay.
* [Win2D custom hosts](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)
  leave resize/DPI/device-loss policy to lower-level consumers. Adapt this to a
  winit host separate from the renderer, including zero-size/suspended windows.
* [WebRender](https://github.com/servo/webrender/wiki) provides a retained rendering
  reference. Retain ProGPU's compiled generations and ordered batches, with no
  geometry upload on stable replay. Visibility and atlas eviction must preserve
  source identity and painter order when those families are ported.
* [Vello](https://github.com/linebender/vello) is a compute-oriented rendering
  reference; [Parley](https://github.com/linebender/parley) is a separate text layout
  library. Preserve ProGPU's existing GPU rasterization and reusable CPU layout
  boundary; do not substitute either engine for the owned implementation.
* [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  distinguish script-dependent shaping from simple advances. Full ProGPU Unicode,
  fallback and OpenType behavior remains required; a character-to-glyph loop will
  not stand in for shaping.

Startup must allocate only requested pipeline families. Geometry compilation may
run off-thread without a GPU; realization occurs on demand and batches uploads.
Future glyph cache keys must retain selected face, variations, hinting, DPI,
phase, raster policy and generation. Worker preparation must not mutate published
generations. Resource eviction cannot invalidate an encoded frame. Culling must
operate on original bounds/clip semantics, not approximate shader envelopes.
These are port requirements, not claims that pending families are implemented.

## Retained analytic execution

`Renderer` compiles the original general vector entry points with the native
56-byte vertex layout and straight-alpha blend factors. The default brush,
gradient buffer and required one-texel atlas binding are the only support
resources. Public mesh recording admits only analytic primitive families;
the placeholder atlas cannot silently render an unported path or glyph.

Each prepared scene owns its vertex/index buffers and its exact original
224-byte projection/DPI uniform through a bind group. No mutable uniform is
shared between prepared frames. Renderer identity prevents use of a scene with
another renderer's inferred pipeline layout, even on the same device. wgpu's
retained resource handles own dependencies through command submission; ordinary
Rust drops do not force premature buffer destruction. GPU publication rejects
overflowed geometry while the CPU geometry port keeps the original arithmetic.

Path compute uses a typed execution preference. Automatic and forced compute
select the canonical compute kernel after checking required device limits.
Forced alternative GPU, intrinsic-SIMD CPU and scalar-reference routes fail
explicitly until implemented and qualified; no approximation is selected.

## Initial validation

`cargo test --no-default-features`: two geometry regressions and canonical Naga
validation of all six initial shader compositions pass. The explicit native
oracle test executes the unchanged C++ analytic header: 288 cases and 36,864
output bytes match exactly, covering all three primitive kinds, AA/aliased edges,
zero extents, strokes, affine/reflected frames and invalid dimensions.
`cargo clippy --no-default-features --all-targets -- -D warnings` passes.

The new implementation does not alter managed/native source or shaders; their
behavior and existing qualification gates remain unchanged. Rendering changes
in this port still require matched CPU records and GPU image evidence before
being declared parity-qualified. No performance improvement is claimed.

On Apple M3 Pro/Metal, all six shader modules validate on the actual device.
The analytic GPU test checks all three families, original rectangle boundaries,
2,752 source-over reference pixels, immutable frame preparation, cross-renderer
rejection, and full-frame cold/warm equality after owner retirement. The winit
sample presents four frames and exits successfully. Strict all-feature/all-target
Clippy passes.

Initial Release baseline (`cargo run --release --locked --example retained_benchmark`):

```text
primitives=4096 warmup=120 frames=480 pipeline_ms=297.042 preparation_ms=0.394
cpu_submit_us p50=18.209 p95=26.584 p99=56.833 serial_fps=2267.19
cold_geometry_bytes=1015808 cold_uniform_bytes=224 draws_per_frame=1
warm_geometry_upload_bytes=0 warm_uniform_upload_bytes=0
```

Submission timings exclude the explicit wait; serial FPS includes it. Upload
counts describe the actual immutable payload and upload-free replay route, not
GPU timestamps or allocator measurements. This is a single Rust baseline, not
matched managed/native performance qualification or an optimization claim.

`cargo package --locked --allow-dirty` produces and verifies a standalone crate
containing the canonical shader files (58 files, 608.5 KiB uncompressed at this
increment). The initial allow-dirty verification was triggered by a generated
native dependency license. Follow-up archive inspection found that the unanchored
`LICENSE` pattern had included that nested file; the package allowlist is now
anchored at the repository root. Package verification builds from the extracted crate, with no paths
back into the checkout. Nothing has been published to crates.io.

The existing managed renderer gates passed locally using the exact SDK from
`global.json`, installed in an isolated temporary directory: 6,478 passed and 13
platform skips (6,491 total), and 230/230 headless tests. The first parallel build
attempt collided on a shared generated runtimeconfig; the renderer suite was
rerun sequentially after headless completed and passed. No gate was weakened.

## Independent path masks

The path port retains exact 48-byte line/quadratic/cubic/arc/rational records and
the original finite/reserved/positive-weight checks. Source bounds, parameter
bits, two-axis scale and fractional origin are retained explicitly. Whole-batch
preflight bounds CPU payloads and GPU storage before GPU mutation. All paths use
the canonical single-path operation-zero entry; Boolean/signed programs and
linear-only specialization remain independent pending features.

Native extent grouping is ported with original source-order tie breaks, 256-byte
binding alignment, exact bound byte lengths and the 65,535 Z-slice split. One
compute pass writes packed coverage into one buffer; copies to owned R8 textures
share the same command buffer/submission. Row pitch is 256-byte aligned and
source placement is 512-byte aligned. Warm consumers retain these textures;
cross-generation atlas reuse and eviction are still pending. The retained scene
integration below adds within-generation sharing and direct atlas population.

Validation: an original-C++ differential control compares 65,540 dispatch records
and 3,146,752 bytes, including the Z-limit split and original signed-zero bits.
GPU controls compare 9,545 R8 pixels against an independent 8x8 sample oracle
for all six segment kinds, nonzero/even-odd, reversed winding, two-axis scale,
fractional origins, unequal output extents, and a true two-arc circle. Invalid
last requests reject the whole batch and unavailable forced routes reject.
The compute owner is dropped before mask readback to verify retained outputs.
These authored controls do not replace the remaining original source/application
and package pixel inventories.


## Mixed retained scenes and path atlases

`Scene` retains immutable `Path` source arrays and ordered analytic/path commands.
Compilation runs without a GPU and preserves source coordinates, maximum/minimum
singular-scale arithmetic, 1/64 phase quantization, four-texel raster padding,
source bounds, fill rule, and the complete sampling/contrast identity. Integer
translations and paint changes can share one raster; different phase, scale or
sampling metadata cannot. The temporary identity map never outlives its strong
source leases. Analytic padding keeps the native 1.5/minimum-scale formula.

The original shelf allocator starts at 1024, grows to at most 8192 without
repacking old tiles, and preserves two-texel margins and the 4092 tile limit.
The compute batch writes directly into the final atlas using the same encoder
and submission as rasterization. Prepared scenes own their exact atlas binding;
later preparations cannot replace textures or uniforms referenced by old scenes.
Warm frames submit one painter-ordered draw with no path compute or uploads.
The optional compute pipeline stays uninitialized for analytic-only scenes.
CPU generations are reusable only at their exact captured DPI. A raw path mesh
cannot accidentally be prepared against the analytic placeholder texture.

Exact texel loads require the original complete four-corner integer mapping and
power-of-two projection proof. Other admitted frames retain the canonical
filtered route. Explicit grayscale contrast preserves all thirteen raw values
and remains separate from binary sampling and ordinary coverage.

Validation added at this increment:

- Original native helpers: 528 affine/phase/projection cases, 19,584 exact bytes,
  including reflections, shears, fractional translations, half-phase ties,
  singular and nonfinite rejection. Existing 288 analytic cases and 65,540
  dispatch cases remain mandatory.
- Mixed GPU scenes: 12,288 independently sampled/composited pixels across DPI
  1, 1.25 and 2, preserving overlapping alpha and command order, followed by
  complete cold/warm equality. A later prepared atlas is retired before replay
  of the earlier generation. Mismatched DPI and placeholder path replay reject.
- Every texel in a 1024-square atlas matches the independently checked mask
  inventory, including margins and unused space, after compute-owner retirement.
- All 13 explicit contrast values: 53,248 output pixels checked against exact
  rectangular area intersections and a double background-domain transfer oracle.
  Only final shader/unorm rounding tolerances are admitted. This does not qualify
  source font policy, original Microsoft pixels, or native packages.
- The mixed winit sample presents four Metal frames: 120 commands, 29,760 geometry
  bytes, 224 frame-uniform bytes, three unique path rasters, one 1 MiB atlas,
  and one draw. Resize/DPI rebuilds the actual frozen generation.

Strict all-feature/all-target Clippy, CPU-only/default tests and the explicit
Metal GPU controls pass locally. On the preceding independent-mask commit,
Linux and Windows portable/package lanes and Linux Vulkan/Xvfb passed; the macOS
hosted job remained queued. Those receipts do not validate later commits.

Release measurements on Apple M3 Pro/Metal, run sequentially without the GPU test
suite, use 4,096 commands, 120 warmups and 480 measured serial frames:

| Workload | Pipeline ms | CPU preparation ms | Submit p50/p95/p99 µs | Serial FPS |
| --- | ---: | ---: | --- | ---: |
| Analytic baseline | 17.497 | 0.479 | 133.041 / 249.875 / 443.292 | 1531.41 |
| 2,048 analytic + 2,048 path draws | 13.450 | 5.349 | 134.917 / 179.208 / 239.792 | 1419.36 |

Both upload 1,015,808 geometry bytes and 224 frame-uniform bytes once and submit
one draw per frame. The mixed workload rasterizes one shared path into a 1 MiB
atlas during preparation, then performs zero warm path rasterizations. Preparation
includes first-use pipeline creation but excludes a completion wait; submission
quantiles exclude the wait and serial FPS includes it. Driver caches are not
cleared. An earlier run overlapped GPU tests and is excluded from this table.
The later submission timings differ substantially from the initial baseline;
a matched host/profile investigation remains required for performance
qualification. These are reproducible workloads and measured receipts, not
cross-language comparisons, GPU timestamps, or speedup claims.
