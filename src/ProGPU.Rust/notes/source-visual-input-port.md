# Original live source input and glyph ownership

| Original owned source | SHA-256 |
| --- | --- |
| `src/ProGPU.Scene/GpuRenderCommandHitTestCache.SourceVisual.cs` | `8ea05126353ff7fb83ca752989115152a6778e95739ec7fe7636d5f456704cf3` |
| `src/ProGPU.Scene/Visual.cs` | `546cd62ba3f2c0d4a85f1479d8c4122c0032b46b090341d088a2749fa1c264fe` |
| `src/ProGPU.Scene/GpuRenderCommandHitTestCache.cs` | `011ac58ddc898022d61fb0b983feba244a1863cd5c00de00586d08a64d5f0366` |
| `src/ProGPU.Text/HintedGlyphGeometry.cs` | `ac9fa8aa8aff77fcd081e31e5ae65b8e73d5ae04f690761f276888c43f54dbec` |
| `src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp` | `65266bdae4fe8939bc485a83abb2c6a5a7034b108fcdfbc8659cfde13355eeb0` |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_paragraph_interaction.cpp` | `dab97e377a7893111c720063088708669b1f6efe63d71378d566f9c97c448232` |

`SourceVisualProvider` is a synchronous actual-provider callback returning an
immutable `SourceVisualSnapshot`. The provider owns capture of already-recorded
source facts and stable resource leases; it must not call OnRender or fabricate
facts from raster storage. The callback itself need not be Send/Sync and is never
retained by the query queue. Snapshot and source-owner leases are Send+Sync.
`SourceVisualCommands` preserves the original opt-in interface, whose opacity-mask
input-neutral contract defaults false. Missing interfaces on visible nodes,
nonidentity input effects, required cache sources and unproven visual masks reject.
Invisible nodes skip. Visual opacity and optional raster caching do not enter input.

The full original walker preserves raw local-transform inputs, ordered matrix
composition, original offset override, local versus outer clip frames, template
children-before-commands order, ordinary children-after-commands order, and the
synchronous observer only for explicitly embedded DrawVisual commands. Nested
source pictures/visuals retain their actual immutable owners. Camera matrices are
resolved for source input before clearing the GPU-transform flag; DrawPath keeps
its distinct recorded-transform composition. Clip, opacity, image-input and
point-region scopes cannot escape their source owner. Source drawing masks retain
the original bounded input-neutral depth without inspecting mask contents.
Source capture faults all publication on errors or unwinding callbacks until
`clear`; a callback panic is not swallowed. The original depth limit remains 256.

Strict source clips use actual transformed paths. Only axis-preserving rectangles
use envelope clipping. Empty admission requires the complete original geometry
and complete transformed definition; unavailable coverage is an error, never the
ordinary builder's inherited-rectangle fallback. Existing Boolean compiler
availability remains explicit: a required unavailable reconstruction rejects.
Raw nonfinite Rect.IsEmpty behavior remains distinct from later primitive bounds
admission. No source coordinates are repaired or transformed into raster estimates.

`SourceGlyphProvider`/`SourceGlyphInput` explicitly bind the original nonempty
DrawGlyphRun ink rectangle and complete command to an actual retained source
lease. Archive fields alone do not assert that contract. The source command tree
requires the glyph wrapper; ordinary archive traversal retains its independent
compatibility estimates. `ArchiveHitIndex` exposes source visual, command-list and
glyph owner inventories, which survive builder clear and GPU queue retirement.

`HintedTextInput::from_replay` binds actual paragraph-backed replay to the exact
originating complete interaction records, retaining selected occurrence/no-ink
identity and the whole source generation. Raw float hit/caret/move/selection reuse
the existing original native record queries (identical nonfragment algorithms);
source-double/SourceIdealUnits use their existing separate double query owner.
Validated imported paragraph records work without claiming a live native producer.
A raster selection does not silently prune paragraph interaction. Metadata-free
batches, nominal glyph placements and single-run shaping lacking a retained
interaction generation reject this binding. DrawHintedGlyphs still never produces
a source hit primitive. Caller source/world transforms remain explicit; raster
origins, atlas bounds and DPI are not inverse input-mapping witnesses.

Platform source adapters must implement the explicit producer contracts; no WPF,
GDI/GDI+, native provider/package or browser qualification is inferred. Original
runtime trig/SIMD arithmetic and input/pixel execution qualification remain open.
Implementation and touched-source formatting only: no builds, tests, new fixtures,
benchmarks, browser runs or artifacts.
