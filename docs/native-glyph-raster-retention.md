# Native glyph raster retention

Exact duplicates inside a rebuilt batch now share first-owner coverage through
[native glyph raster sharing](native-glyph-raster-sharing.md). Complete retained
batch identity below still includes every original outline and segment byte.

## Blocking application and diagnosis

Acceptance application: **ProGPU.Wpf.ShowcaseApp**. Action: finish the live
editing, themes, popup and scrolling checks, then present 120 warmed frames.
The native semantic glyph page invalidated its complete positioned batch when
scene presentation changed. `render_glyphs` also discarded raster coverage on
that change, even with byte-identical outlines and segments.

The September 14 diagnostic Metal System Trace records native glyph coverage
passes around 375 ms, with a maximum of 785.335 ms. The host trace reports
roughly 360–390 ms surface acquisition waits. An unprofiled failure observed
presented frames 91→91, skipped frames 0→0 and a 740.183 ms wait, with 2,292
native commands and 118 draws. The existing 300×2 ms presentation polling
bound was not changed.

Trace: `artifacts/showcase-profile.Q976k4/Showcase.trace`; exported GPU intervals
and encoder tables are retained in the task's external-volume profiling
scratch directory. This was a Debug Showcase with Release native libraries
and an explicitly documented source/package overlay. It diagnoses the blocker;
it is not matched final Release performance qualification.

## Implementation and ownership

Keep the native complete-batch atlas, but separate its raster identity from
the compiled instance identity. The engine owns the last validated outline and
segment bytes. Reuse requires exact byte equality, the same DPI, matching
outline/raster counts and the same live atlas generation. Bounds, physical
raster scale, subpixel phase, segment kinds, points and offsets all participate.
No font-name or hash-only substitute is accepted.

Placement, color, basis, instance count and text-style changes still rebuild
and upload current instances and invalidate their semantic render bundles.
They do not rerasterize an unchanged complete outline batch or advance its atlas
generation. Changed raster content still takes the existing complete packing,
validation and compute/raster/SIMD/scalar implementation. The shader, quality
constants, sampling and execution defaults are unchanged.

Identity is published after successful encoding/submission at the existing
ownership boundary. Abandoning a semantic encoder or failing its finish clears
both raster and compiled glyph validity. Engine disposal/device reconstruction
owns all memory. This adds no global cache, texture readback, per-glyph interop,
eviction policy or atlas repacking under a retained identity.

Stable compiled replay remains O(1) cache admission, with no added copies or
uploads. A changed positioned batch compares O(O + S) native bytes for O
outlines and S segments, using ProGPU's existing NEON/SSE2/Wasm SIMD
`scene_bytes_equal` with its bounded tail. Instance rebuild remains O(G).
Retained key storage is bounded by the largest admitted complete raster batch;
the existing native count and atlas limits remain. Nonretained calls do not
copy a key. This is not a new persistent per-glyph LRU: genuinely changed
outline sets still rasterize as a unit.

`--rerasterize-glyphs` now explicitly disables retention with revision zero.
Incrementing a scene revision alone no longer proves raster work, so the old
benchmark technique would incorrectly time cache hits after this change.

## Provenance and paired-renderer audit

Only original ProGPU implementation is reused:

- `src/ProGPU.Text/GlyphAtlas.cs`: `GlyphKey`, `GetOrCreateGlyph`, retained
  coverage independent of placement and paint; this managed behavior already
  exists, so no managed raster algorithm change is needed.
- `src/ProGPU.Native/src/Backend/progpu_native_glyph_execution.cpp`: existing
  validation, shelf packing, coverage dispatch, instance upload and fallbacks.
- `src/ProGPU.Native/src/Scene/progpu_native_semantic_identity.cpp`:
  alignment-safe intrinsic exact byte comparison.

The existing cross-engine research in
[rendering research](progpu-avalonia-rendering-research.md#retained-glyph-atlas-residency-during-incremental-replay-2026-08-26)
was revisited alongside these primary contracts:
[Skia retained text](https://api.skia.org/classSkTextBlob.html),
[SkParagraph cached shaping](https://skia.googlesource.com/skia/+/5d8f55bfa851a55fa7e111b9a4f1fd063509eca5/modules/skparagraph/src/ParagraphCache.cpp),
[Direct2D resource reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance),
[Win2D device loss](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm),
[WebRender texture ownership](https://doc.servo.org/webrender/texture_cache/index.html),
[Vello glyph-cache design discussion](https://github.com/linebender/vello/issues/204),
[Parley layout](https://docs.rs/parley/latest/parley/struct.Layout.html) and
[HarfBuzz shape plans](https://harfbuzz.github.io/shaping-plans-and-caching.html).

Adopted: separate reusable shaped/layout results, raster content and current
draw instances; qualify GPU residency against its owning device/generation.
Rejected: borrowing another engine's implementation, weakening subpixel keys,
reshaping during replay or globally pinning every historical glyph. Startup
stays lazy; visibility, workers, fallback fonts, variable-font resolution and
shaping are unchanged upstream consumers. GPU preparation still batches at
the existing scene boundary. No third-party implementation text was introduced.

## Validation and remaining gates

`GlyphRasterRetentionQualification` runs in the existing native glyph benchmark
gate for fastest, compute, raster, SIMD and scalar execution. It checks stable
replay, revision-only and placement/paint/count mutations, DPI, phase, scale,
bounds, changed segment bytes, empty batches and rejected-input recovery.
Retained output is compared byte-for-byte with a newly rasterized render of
the same input, and nonempty cases must actually paint pixels. The existing
managed/native pixel differential then still runs on its original workload.

Initial candidate: both native providers compile; all 19 CTests pass. All five
execution modes pass 11 exact uncached pixel comparisons each, stable replay
and rejected-input recovery. The existing one-glyph managed/native fixture is
byte-identical in every mode at `6C59592F05595EFE` (518,400 output pixels).
The Release benchmark project builds with zero warnings/errors. The
diagnostic Showcase completes all live input actions and 120 presentations
without extending a timeout. Its current performance reporter reads idle
managed compositor timing/residency counters in native mode; zeros from that
report **do not qualify native performance or memory**. Accurate native
diagnostics, matched final Release Instruments/counter measurements, exact
package runs and Windows/Linux CI/runtime checks remain required before merge.
