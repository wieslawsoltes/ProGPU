# Original image-filter port (2026-11-09; unvalidated)

This increment implements immutable original image-filter descriptions, bounded
source-rebound lowering, exact parameter records and the original five compute
engines. The user's mandatory implementation-first order remains in force:
no compilation, lint, tests, shader/browser execution, benchmark, native comparison
or package verification ran. Only formatting ran. No new build or trace artifacts
were generated. Descriptors and canonical kernel inclusion do not establish
complete retained execution or parity qualification.

## Original provenance and boundary

Only original ProGPU-owned code was read or translated. The existing architecture
research in `DESIGN.md` remains applicable: immutable source generations, explicit
device domains, retained ownership and GPU image evaluation. No Skia, SkSL,
System.Numerics runtime or third-party engine implementation was copied.

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKPaint.cs` | `684b87af3f62633200e96265997d40250ddc98d0cf6ddcbb008d8746de6d41ff` |
| `src/SkiaSharp/SKImageFilter.Overloads.cs` | `ca8cb11c6e95cbb11b8dcd808130497972490a63981bb25719357fd59e642b94` |
| `src/SkiaSharp/SKCanvas.cs` | `08b8d871bccfefdda7021e533c41641eaf22299932d1c32f8e7d9b49a2741e64` |
| `src/SkiaSharp/SKBlender.cs` | `ee11d400a60e2e583cb0fb59c606018f0fc9c7452f5baf0c8050d9a74e35d55c` |
| `src/SkiaSharp/SKColorSpaceTransferFn.cs` | `23222c56765b316bbe3137a04f3d0b39b3f11bd9509a202d0f67111d96c63269` |
| `src/SkiaSharp/SKColorSpace.cs` | `39009e68e44861f743eb2e842cae93473e45e3ef5e1e52c3acf6aa2ddf8e0020` |
| `src/SkiaSharp/SkiaSharp.cs` | `3b43aa657ad5584157b612fd16d2b56d96af352e310d69ff5a004d49e85d216f` |
| `src/ProGPU.Compute/ComputeAccelerator.cs` | `7e3cd2be4a1f02cfe44f9b784759cf5ae5677f089c5f7c5c2db7e2a4b28c8ea1` |
| `src/ProGPU.Backend/GpuTexture.cs` | `0b5e89412acd16e86ae2f86c7e85e8bfa880b85dea196a79c2014cc1928bdb25` |
| `src/ProGPU.Compute/Shaders/Morphology.wgsl` | `08739b716dc7aed05a5be079882b0812ca84122266bd46700c4d2cb51967787e` |
| `src/ProGPU.Compute/Shaders/MatrixConvolution.wgsl` | `9efcbdcfbbd74e48859d8d0b015bc23c5cfd211fd1fcd83586f306fcff25726b` |
| `src/ProGPU.Compute/Shaders/DisplacementMap.wgsl` | `b771c27cf1ab9d9e07b51b86108f96cae53b9b8414e9e46a58b555aa3bcd1018` |
| `src/ProGPU.Compute/Shaders/Magnifier.wgsl` | `3f7b37719e19940b70bb535cde3fd6da72f849d7d4f33fd20b3a1005fdf52080` |
| `src/ProGPU.Compute/Shaders/ImageLighting.wgsl` | `1632a4803ee654cc4943720d4e08716c7e856409861f5a74543585dc1badefe2` |

`SKPaint.cs` supplies all 24 `FilterKind` variants, factory descriptors and
convolution inventory copying. `SKImageFilter.Overloads.cs` supplies magnifier
admission, image Mitchell defaults and convenience policy. `SKCanvas.cs` supplies
memoization, source color-space selection, every kind's execution order, captures,
transforms, crop exceptions and parameter lowering. `SKBlender.cs` supplies mode,
arithmetic and explicitly unsupported runtime ownership. Transfer evaluation,
inversion and fast approximations come from `SKColorSpaceTransferFn.cs`;
`SKColorSpace.cs` is consulted only for selected-transfer identity. Sampling
metadata comes from `SkiaSharp.cs`. The five compute kernels are included unchanged
and all exact original provider/kernel provenance is also recorded in `gpu.rs`.

## Implementation inventory

| Slice | Implemented production code | Remaining connection |
| --- | --- | --- |
| All 24 filter kinds | Immutable `ImageFilter`, typed factory data, independent input/crop, checked convolution inventory, all six lighting identities | Scene command and full plan executor |
| Source rebinding | Child-before-parent `ImageFilterPlan`, `(filter owner, bound source result, preserve color space)` memo keys, repeated source ownership, bounded visits/steps/draws/storage | GPU result-generation table and replay ownership |
| Gaussian/shadow | Original two-axis blur parameters; shadow tint, blur, transformed offset and optional source overlay remain separate operations | Existing Gaussian kernels and exact managed ImageEffect capture execution |
| Morphology/convolution/displacement/magnifier/lighting | Exact 16/48/32/80/80-byte records and independently owned canonical compute engine | Retained plan-to-engine preparation |
| Color filters and blends | Existing full color-filter graph expands to exact matrix/table/nonlinear/overdraw/constant-capture/blend/arithmetic steps | Reuse exact managed ImageEffect and existing color compute engines |
| Transform/crop/tile/merge | Full 4x4 frame composition, original crop ordering, tile loop range and painter order, ordered nullable merge children | Original retained draw/clip/capture realization |
| Image/picture/shader leaves | Actual immutable image/scene/brush owners, source/destination rectangles, sampling, target clip, shader wrappers, explicit runtime owners | Exact source capture and wrapper realization |
| Color-space source policy | Original transfer curves/inversion, strict identity, original first-applicable linear-source search, homogeneous picture inventory and retained 256-entry sRGB conversion | Producer-backed picture inventory and table execution |

The 24 original kinds are Blur, Compose, DropShadow, Arithmetic, BlendMode,
ColorFilter, Dilate, DisplacementMap, DistantLitDiffuse, DistantLitSpecular, Erode,
Image, Magnifier, MatrixTransform, MatrixConvolution, Merge, Offset, Shader,
Picture, PointLitDiffuse, PointLitSpecular, SpotLitDiffuse, SpotLitSpecular and Tile.
Every kind has retained typed parameters and concrete lowering; no kind is replaced
by a generic effect or placeholder completed bitmap.

## Public API and integration contracts

`image_filter` is exported from `lib.rs`. CPU production modules are `graph.rs`,
`plan.rs`, `parameters.rs`, `transform.rs`, and `color_space.rs`; `gpu.rs` is gated
by the existing `gpu` feature.

`ImageFilter::new` retains a complete descriptor atomically, checking typed light
identity, exact signed convolution coefficient count, required tile child,
magnifier admission and bounded owned graph/shader depth. Named factories retain
all original policies. `magnifier` returns `Result<Option<ImageFilter>, Error>`
for the original zoom <= 1 / absent-crop null-or-input fast path. `whole_image`
selects Mitchell; `image_with_sampling` retains explicit reconstruction. `crop`
retains the original ignored tile-mode behavior and an offset-zero child.

`ImageFilterPlan::new(&root, FilterPlanRequest)` performs no GPU work. Result zero
is the caller's actual source, step n produces result n+1, and `output()` may be
zero after an original identity path. `steps()`, `output_size()`, `request()`,
`resources()` and `source_captures()` expose the complete immutable plan. The
request retains distinct source mip inventory, canvas physical and logical
extents, full filter transform and explicit resource/work bounds. New Rust bounds
fail atomically rather than truncating original visits or silently changing paint.

`ImageFilterOperation::is_explicitly_unsupported()` identifies runtime blenders
and runtime shaders even under composed/local-matrix/color-filter wrappers.
`FilterShaderSource` owns its actual source text/uniforms/children for unsupported
runtime sources; it is never equated with an existing WPF bytecode effect. The
`runtime_support()` query checks supported source categories and bounded depth,
not successful source-capture realization. A renderer must reject every unmet
source category before GPU mutation. Actual picture ownership is `Arc<Scene>`;
`PictureColorEntry` is an explicit matching original command-metadata inventory,
not inferred proof from supplied pixels. Producer integration must establish that
inventory before claiming selected-source color-space provenance.

The GPU exports are `ImageFilterComputeEngine`, `ImageFilterComputeRequest`,
`ImageFilterComputeMetrics`, and `PreparedImageFilterCompute`. Engine construction
uses the actual device and `ExecutionPreference`. Static `preflight(device,
&request)` admits complete source/device/size/copy/resource requirements before
mutation. Asynchronous `prepare(request)` dispatches five typed variants:
Morphology, Convolution, Displacement, Magnifier and Lighting. Individual prepare
methods are also available. Prepared results retain input, intermediate, output,
parameter, bind-group and pipeline owners; `encode` checks the creating engine
and records original GPU work without queue submission or readback. The crate
internal `encode_owned` permits a complete owning renderer generation to replay
after the mutable engine has moved on. Metrics expose original pass count, bytes,
allocations, workgroups and complete texture-copy payload, with checked merging.

## Preserved policies

- Compose evaluates inner first and rebinds outer's implicit source to that result.
  Memo entries include source identity and source-color-space preservation.
- Magnifier crops its input first and has no second post-crop. Other kinds retain
  original final crop; invalid/full logical-canvas crops bypass independently of
  the actual filter transform. Crop clips retain the full transform, not an envelope.
- Both nonzero morphology routes execute horizontal then vertical even if one
  radius is zero. Two zero radii perform the original real `CopyFrom`, preserving
  source format and every mip with explicit CopySrc admission; they are not aliases.
- Blend/arithmetic outputs use the maximum of the actual two input extents.
  Displacement uses max(canvas, color input), ignoring displacement-map dimensions.
  Merge starts with the currently bound source extent and visits children in order.
  Image/Picture/Shader captures use that bound source's actual extent.
- Convolution preserves raw kernel rows, gain/bias/alpha policy and signed anchors.
  Non-Decal domains use original transformed floor/ceil crop bounds, including
  original out-parameter contents on failure. Missing/invalid domains select
  Decal; ignored Decal domain bytes are retained. Unsupported dimensions preserve
  the original input before the final independent crop.
- Shadow uses the original managed ImageEffect alpha tint before blur, then the
  complete transformed offset and optional unshifted source overlay. It is not
  represented by a superficially similar group shadow.
- Matrix transforms compose inverse(filter frame) * local * filter frame, with
  transparent output on singular/nonfinite final frames and exact identity bypass.
  A raw all-zero filter frame retains the original default/identity interpretation.
- Tile retains original Y-outer/X-inner integer ranges and original source-domain
  coordinates plus independent destination clip. Invalid nonpositive rectangles
  preserve input. Nonfinite/out-of-range loop coordinates reject with capacity.
- Lighting retains all six kinds, full distant normal versus point/spot mapping,
  independent geometric-mean Z, original color/height/constants/exponents/cutoff
  normalization and exact 80-byte kernel record.
- Displacement source search preserves original stack order and first applicable
  image/picture return; it is not an any-linear reduction. Picture uniform-color
  selection rejects missing transfers, mixed paint, empty scopes and conflicts.
- Selected transfer conversion retains the seven original coefficients, named HDR
  cases, strict linear/sRGB identities and once-per-owner 256-entry table. Final
  sRGB encoding keeps the original standard power function, separate from the
  original transfer evaluator's fast approximation.

## SIMD, ownership and costs

Independent transfer samples use four SIMD lanes with the original owned
fast-log/exp/power arithmetic. Bounds and matrix rows/cofactors use independent
four-lane work; determinant, graph order and crop decisions remain dependent
scalar work. The matrix inverse is an independently written complete adjugate
implementation of the public mathematical contract; the source calls the platform
System.Numerics API, whose implementation was not copied. Its exact original
runtime floating transcript remains a separate qualification gate. Final table
encoding retains scalar standard power where substituting an approximation would
change bytes. No per-pixel CPU effect fallback is introduced.

Graph expansion is bounded by distinct source-bound visits and emitted operations;
retained sharing avoids repeated work within each source/policy binding. Tile draw
counts use checked products before capture. Resource inventories include ordered
passes and RGBA8-equivalent intermediate/mip storage. They are planning bounds,
not measured allocator peaks or exact device-format copy bytes; GPU preflight
supplies actual device metrics. No source callbacks, polling or readback occur in
factories/planning. Immutable Arc owners preserve sources beyond caller retirement.

## Known unfinished production and qualification work

1. Connect the plan to Scene and Renderer: complete preflight, private target
   creation, Gaussian/matrix/capture/compute ordering, retained result and nested
   source ownership, typed failure rollback, masks/layers/AA and final consumer.
2. Realize Image/Picture/Shader owners through the actual retained captures.
   Procedural Perlin uses the existing owned brush tables and canonical material
   shader, including original inverse filter-frame adjustment; it is not another
   generic CPU noise implementation. Composed shader wrappers and original runtime
   source languages still need their actual production implementations or explicit
   unsupported-source gates. Runtime callbacks must never be fabricated.
3. Full SKColorSpace/ICC/XYZ primary ownership and CreateRgb canonicalization are
   outside this selected-transfer filter API. `ImageFilterColorSpace::new` takes
   the actual already-selected original transfer; it does not claim ICC decoding
   or infer primaries. Source producers must retain that policy independently.
4. Original runtime floating arithmetic, NaN/infinite-to-integer casts and managed
   matrix-inverse transcripts remain unqualified. Rust rejects bounded-work/domain
   overflow where required; source metadata itself is retained without pretending
   cross-runtime bit equivalence. Shader/device-format semantics, full source pixels,
   ownership failure paths, browser, platform/package and performance evidence all
   remain deferred until the mandatory full-port phase is complete.

No current-head build or execution success, performance improvement, browser
support qualification or professional release readiness is claimed by this note.


## Retained execution continuation (2026-11-10; unvalidated)

`ImageFilteredImageDraw` now connects the complete plan to Scene/Renderer through
owned result generations. The request freezes filter source/canvas metadata
independently of final consumer placement. All 24 kinds now have execution:
original Gaussian axes, exact managed ImageEffect matrices/tints, all color and
five image compute families, source-over merge/offset, true transformed crop,
ordered tile draws and independent Image/Picture/Shader source capture.

Every intermediate, child renderer, source recording, pipeline, binding and result
remains owned through replay. Each captured scene uses RGBA8Unorm, original pixel
projection/DPI one and an independently retained Renderer. Child scenes encode on
the consumer encoder; preparation performs no effect/capture submission and replay
runs no source callbacks. Existing path/text/image preparation keeps its own
ordinary upload/raster submissions. Whole-plan/device/owner/resource admission
precedes candidate creation; failures publish no prepared graph and restore actual
image/filter engine state. Nested source policies use their independent admission.

Shader sources now retain original raw-image policy, picture tile modes/filter
and composed arithmetic. Implementation includes original bounded tile ranges,
mirrored placement, picture tile area/dimension limits, local-matrix wrappers,
selected image transfer before tiling, shader color-filter captures and the exact
solid-background/two-point-conical SrcOver brush fast path. Direct Perlin sources
retain the original inverse filter-frame adjustment; wrapped/general shader
sources keep their different original canvas policy. Explicit shader-local zero
matrices remain singular, independently of the filter-frame default sentinel.

New production files: `image_filter/draw.rs`, `renderer/image_filter.rs` and
`renderer/image_filter/{capture,gaussian,preflight,source}.rs`. Original shared
color-filter capture helpers are exposed internally; separate target-frame and
same-format mip helpers preserve existing callers. Final mips retain actual
source format, including the zero-radius morphology/identity routes. Metrics
separate canonical passes/copies, capture resources and nested source replay.

Remaining production gates are original runtime shader/blender language sources,
non-2D source-picture placement, and non-affine nested shader local-matrix concat.
Those reject explicitly. Full source-provider ownership, default-runtime numeric
casts/inverse transcripts, native/managed pixels and all browser/performance/package
qualification remain open. No compiler, tests, lint, shaders, browser or benchmarks
were run; formatting only. No build/trace artifacts were created.

Additional directly consulted original source SHA-256:

| Original source | SHA-256 |
| --- | --- |
| `src/SkiaSharp/SKMatrix.cs` | `a1a0fb70cb138510b7407bf41de5b3d88fb6e3f34bde01dd948d1128268b54e3` |
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl` | `7ffc9636c103bddbbf13e7ba5fc253d4e9a02c26478edc5e5a38ef9a81de8834` |
| `src/ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl` | `fc700b1b057b369588960ce485d8bf07a0cb9a6f06e8611f794c1d990f32080c` |
