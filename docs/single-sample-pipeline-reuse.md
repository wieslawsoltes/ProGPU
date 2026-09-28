# Single-sample pipeline reuse

The original Forms grid's Windows startup trace reached a second general vector
pipeline compilation after already creating the corresponding offscreen pipeline.
At one primary sample, both descriptors use the same target format, shader,
entrypoints, blend state and aliased binding layout; only the logical cache name
and compositor selection key distinguished them.

`Compositor` now normalizes that selection before its existing cache lookup, only
when the primary sample count is one and the corresponding actual primary and
offscreen layout handles are equal. This covers lazy general vector, text and
texture selection and the solid rectangle/rounded rectangle specializations.
Four-sample primary rendering retains its distinct pipeline. Format overrides,
opacity-mask targets, blend modes, alpha representation and shader entrypoints
remain in their existing keys. Actual offscreen rendering, its uniforms and
target ownership are unchanged. Explicit eager base-pipeline precompilation
remains a separate policy and is not changed by this lazy-selection fix.

Admission and lookup stay allocation-free O(1) time/space on a cache hit. Pipeline
ownership, device domains, disposal and device-loss handling remain in the
existing `RenderPipelineCache`. This changes neither shaders nor source/DPI/text
state, GPU submission/completion, renderer/compiler defaults or application
deadlines.

## Provenance and native applicability

The implementation derives only from original ProGPU commit
`f9eced31ebc3af7d0d83d3fbf33958470477cf8a`: the aliased layouts, pipeline getters
and descriptors in `src/ProGPU.Scene/Compositor.cs`. No foreign implementation
was copied. The cross-engine primary references and design comparison in
[pipeline startup diagnostics](render-pipeline-startup-diagnostics.md#research-boundaries-retained)
and [path raster pipelines](single-path-raster-pipeline.md) apply: adopt lazy,
device-owned exact-resource reuse; preserve shaping/layout reuse, retained scenes,
culling, atlas keys/eviction, demand-driven uploads, worker preparation, batching,
DPI/subpixel/hinting, fallback/variable fonts and generation invalidation. Do not
replace those contracts with a global pipeline or a changed rendering policy.

Both native C++ providers use `progpu_native_pipeline.cpp` and engine-owned
pipelines keyed by their actual target format. The solid and analytic pipeline
descriptors already select one sample and have no managed
`PipelineSelectionKey.IsOffscreen` or paired primary/offscreen name. The duplicate
managed selection is therefore not present there. Canonical shader/ABI and native
pipeline creation remain unchanged; this is not native application qualification.

## Evidence and remaining gates

`SingleSamplePipelineReuseTests` executes both render orders for rectangles,
rounded rectangles and ellipses at one and four samples. It compares every pixel
byte for matching single-sample passes, verifies no duplicate creation, repeats
both passes, checks the four-sample separation and confirms disposal releases all
cached pipelines. On the unchanged parent, all six one-sample cases fail while
all six four-sample controls pass. With the fix, all twelve pass.

Focused Release validation including layer rendering, pipeline-cache regressions
and existing eager-precompilation ownership coverage: **58 passed, zero skipped**.
This is not a whole-package or original Windows application startup pass.
Windows application evidence, complete Build, Svg.Skia image comparisons and
the existing final platform/performance gates remain required before integration.
