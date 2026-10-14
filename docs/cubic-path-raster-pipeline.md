# Line and cubic path pipeline specialization

## Cause and scope

The original Gallery acceptance run at LibreWPF
`632f4c9bf44db5f17954366be7ebef07505a6e66` failed its unchanged 60-second
controller bound without either capture. The subsequent single bounded CPU and
completion diagnostic, [run 38519631899](https://github.com/wieslawsoltes/LibreWPF/actions/runs/38519631899),
retained the original application, source actions, scenes and deadline. It is
diagnostic evidence, not a passing application run.

The trace contains 69,638 CPU samples with zero lost events. Of these, 28,979
have FXC as the leaf module. Mapping native caller instruction addresses and
pipeline-label literals in the exact loaded ARM64 DLL attributes 10,086 FXC
samples to the single-path raster pipeline and 5,538 to the analytic primitive
pipeline. These are sampled compiler stacks, not kernel execution times. The
DLL SHA-256 is
`617597478521fb2944aa52618cbca4c6ab66ce61fbe2418b83099a3c77839dd8`;
the original trace SHA-256 is
`75cfb558d55c973dfdd55f672faf10ce29e33dacebcbbd8c506ad2b086715a35`.
The actual adapter is Microsoft Basic Render Driver with D3D12/automatic FXC.
The one observed current-frame submission token was valid and still pending.
This does not demonstrate a completion-checkpoint rejection bug.

The retained Gallery paths use lines and polynomial cubics. The new entry can
exclude other curve families from shader compilation when the complete source
inventory proves this narrower input contract. It does not select a different
compiler, adapter, raster quality, execution stage or fallback.

## Original algorithm and ownership

Implementation provenance is the original ProGPU `PathRasterizer.wgsl`,
`PathRasterizerCommon.wgsl`, `PathAtlas.cs` and native path/clip execution at
`22bbd4a138735a1098d8a700bb2b81980a06f7d4`. No third-party implementation text
or structure is included. The new `cs_main_cubic_path` entry is admitted only
for operation zero and kinds 0/2. Its raw GPU gate validates the full selected
record, offset/count arithmetic and every segment before publishing output.
Quadratic, arc, rational and unknown kinds retain the general path.

The constant classification parameter excludes unrelated curve solvers. Original
line arithmetic, homogeneous unit-weight cubic coefficients, root order,
derivatives, endpoint predicates, sample grids, scaling, winding, rounding and
four-byte packing remain unchanged. In particular, there is no algebraic
replacement of the cubic equation or conversion of rational curves.

Managed `PathAtlas` classifies every admitted request in its existing preparation
loop. Native `progpu_native_path_execution.cpp` and
`progpu_native_clip_execution.cpp` classify every original segment in their
existing validation traversal, including unused source segments. Any nonzero
operation keeps the original ordinary/signed/split choice. Line-only work keeps
its existing line pipeline. Empty or rejected work creates no new pipeline.

`progpu_native_path_text_resources.cpp` creates the new pipeline on demand;
`progpu_native_engine.hpp` retains and releases it and exposes its selection
through the existing opt-in encoding trace. The same CMake backend source list
and canonical WGSL are compiled into both wgpu-native and Dawn providers.
Managed creation uses the existing device pipeline cache and leases. No public
ABI, scene/cache identity, upload, batching, source geometry or completion API
changes. No MIL implementation changed, so its generated coverage ledger stays
unchanged and is checked for freshness.

Classification uses O(S) existing host segment visits, O(1) extra state and at
most one additional lazy pipeline per engine/device. The raw guard adds O(S)
kind reads per four-pixel word to unchanged O(A*S) raster work for A samples and
S segment visits. Workgroups remain 16 by 16. Retained warm frames do not gain
segment scans, uploads or pipeline compilation.

## Research and applicability

Primary sources informed the architecture rather than supplying implementation:

| Source | Decision |
| --- | --- |
| [WGSL specification](https://www.w3.org/TR/WGSL/#const-expressions) | Keep typed entry specialization and shared original arithmetic; constant propagation does not waive pixel comparison. |
| [Direct2D performance](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance) | Retain device resources and deferred batching; add no flush or extra submit. |
| [Win2D resource loading](https://microsoft.github.io/Win2D/WinUI3/html/LoadingResourcesOutsideCreateResources.htm) | Keep lazy resources under the actual device owner and its retirement. |
| [DirectWrite layout](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout), [SkParagraph interface](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h) | Preserve reusable layout separately from painting; no reshaping or paragraph invalidation. |
| [WebRender architecture](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html) | Preserve retained scene and spatial ownership while changing one raster pipeline choice. |
| [Vello architecture](https://github.com/linebender/vello), [Parley architecture](https://github.com/linebender/parley) | Keep preparation, layout and raster responsibilities distinct; do not import another engine's CPU/GPU routing. |
| [HarfBuzz responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html) | Keep shaping, fallback and font state outside this optimization. |

Startup/lazy compilation is the changed dimension. Layout reuse, retained scene
and visibility decisions, glyph/texture/path cache keys and eviction, demand
uploads, worker preparation, device-loss/atlas generations, DPI/subpixel/hinting,
fallback and variable-font state retain their existing contracts. This is the
same rendering contract in managed code and both native providers.

## Validation and remaining gates

`CubicPathRasterPipelineTests` compares all raw output words with the independent
frozen original `RasterCrossingReference/Path.wgsl`, not a reference changed with
the candidate. Its 896 cases cover seven line/cubic shapes, both fill rules,
four grids, two placements/scales, eight widths, reversed/inflected/degenerate
and doubled contours, partial words, untouched padding and overdispatch.
Later segments of every unsupported/unknown kind, invalid slices and nonzero
operations must leave the complete destination untouched.

The actual-atlas control checks cubic, line, later mixed-curve and Boolean
demands, unchanged retained pixels, warm reuse and disposal. Windows conformance
adds these two bodies to all ten original bodies, retaining each original
120-second process limit. Local Metal passes all twelve with zero skips. Raw
pipeline acquisition measurements are observations, not startup claims.
The local Release build also passes 95 focused managed shader/atlas regressions
and all 48 configured native CTests. Both native libraries build from the same
changed sources; this local CTest set does not include the external WebScene
provider aggregate, which remains a separate qualification gate.

Before merge, the final exact producer still requires whole Build/Docs,
Svg.Skia parity, Windows ARM64/x64 raw coverage, both native provider corpora,
original captured-scene pixels/counters, matched final CPU/memory/Metal profiles,
and the original Gallery application gate. The 60-second Gallery limit and
57-second host reserve remain unchanged. This source change alone qualifies
neither packages nor a release and does not close the separate WebScene
performance objective.
