# Native nominal GlyphRun source replay

Implementation only; no compiler, tests, lint, fixtures, browser, benchmark or
pixel validation ran. Only touched Rust sources were formatted.

| Original ProGPU source (under `src/ProGPU.Native/src/Text/Interop/`) | SHA-256 |
| --- | --- |
| `progpu_native_hinted_nominal_metrics.cpp` | `03800888763d1a37f17369f822ccefbdb59bdff283b3f7cacbbfe7adf3ffa0a1` |
| `progpu_native_hinted_source_frame.cpp` | `2c25901a68fd3f637dc8ab8966640bcb4539bab85d76f1a348398ba7862a4c37` |
| `progpu_native_hinted_source_run.cpp` | `31449ded9e302533e1fba0f7019eabb1d0bbc3e7aa238e3efceaef8d9b2114ff` |
| `progpu_native_hinted_source_run_internal.hpp` | `706bdd79cf5f0fc15e4c0639768b1ec19d9cb4380785424635375f7b8d7a4f42` |
| `progpu_native_text_shaping_interop.cpp` | `501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd` |
| `progpu_native_hinted_source_transport.inl` | `26578ad8830f7c879fe045ac16988dce9d5a4331987716cb0ff8d42805c883c6` |

`NativeNominalParagraph::new` retains an actual `NativeParagraphGeometry` producer
and captures the selected faces' original hmtx advances in complete occurrence
order. It rejects normalized-coordinate and selected device-variation requests;
there is no legacy half-em fallback, HVAR inference, glyph execution or hinting.
Existing immutable `SfntFace` views retain exactly the original font allocations.
Imported geometry has no producer and is rejected by these original owned APIs.

The raw positioned lane exposes `nominal_metrics`, `copy_source_offsets` and
`admit_source_frame`. The double source lane exposes `copy_source_metrics` and
`admit_source_run`. Each preserves its original admission: selected line/font/
full bidi level, per-run source scale, one actual run for the double lane, original
em/DPI, zero vertical advance and exact representable raster translation. Offset
arithmetic retains design advance / units-per-em * original em and the ordered
double prefix; no shaping offset or hinted advance replaces nominal geometry.
Raw advances require exact bits while raw offsets keep original numeric equality;
the source-double route compares every supplied advance/offset bit. Private
staging leaves complete caller outputs unchanged on any later failure.

`admit_positioned_replay` and `admit_source_replay` publish `NativeNominalReplay`
only after frame admission. It retains the actual nominal owner, original selected
indices, unchanged captured outlines and all descriptor/no-ink slots through the
existing selected `FontReplayGeometry`. `prepare_draws` uses the existing complete
paragraph style preparation; `geometry()` and `draw_position()` feed the existing
`Scene::draw_native_paragraph` API. No new Scene or renderer policy is introduced.

The original source resource preparation requires its f32 DPI to widen exactly
to the source double DPI. `NativeParagraphGeometry::new_source` now applies this
original producer gate; paired imported source-v2 admission retains its separate
original cast-to-float rule. Errors retain source/geometry/replay causes and no
candidate is published early. Safe disjoint output borrows replace raw overlap
checks; retained Arc ownership uses normal Rust allocation semantics.

These APIs implement the original owned adapter contracts; they do not qualify
Microsoft source GlyphRuns, GDI/GDI+, ordinary Display, pixel equivalence, safe
FreeType execution, native/browser/RID packages or cold/warm performance. Generic
unhinted/fragment/vertical paragraph layers and source OS catalogs remain separate
implementation work.
