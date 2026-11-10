# Native combined paragraph geometry and raw resource import

Implementation only. No compilation, tests, lint, browser, benchmark, captures or
other qualification was performed. Formatting is limited to changed source files.

## Original sources

| ProGPU-owned original | SHA-256 | Implemented scope |
| --- | --- | --- |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.cpp` | `4c48facd747ba7af4d2dcfdd902c630ee8754ac0468060c2c30e841004beda79` | Complete raw paragraph admission, per-run requirements/conversion/rebasing, source/run/outline/positioned ownership, exact per-positioned-run DPI, independent style paint/source order |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_paragraph_glyph_frame.hpp` | `e4dcea1649e7a97a8c94f148b94578f5b74ad2194b401f35d7d4048a4c5616e2` | Original resource versus actual paragraph ownership and flat-import distinction |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_glyph_resource_import.cpp` | `6546429ef0465c3ddd31f22c93020f6f4c76576441151f7458fa4a79c8ec6d9a` | Complete original `valid_records(..., false)` raw-resource lane and owned import publication; additive source-double extension is separate |
| `src/ProGPU.Native/include/progpu_native_text_hinting.h` | `dc33c4c3d9a8002f663813e32bf65c5d78497ac48f767c4b48ede0cbf822a2a5` | Fixed font/device/style/run/slice/outline/positioned/interaction records and complete flat inventories |
| `src/ProGPU.Native/include/progpu_native_text_styles.h` | `656af3b851bda5f386b66288b9e31ed1617999ab17014ca7af5f0fb7478c36d1` | Original style and number-symbol payloads |
| `src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp` | `65266bdae4fe8939bc485a83abb2c6a5a7034b108fcdfbc8659cfde13355eeb0` | Original measured interaction emission and bit-exact retained record admission |

## Connected API and ownership

`NativeParagraphGeometry::new(Arc<NativeHintedParagraph>, dpi, projection, coverage)`
validates the complete original raw paragraph before converting every actual run.
It retains the complete actual source owner; runs omitted from maximum-lines or
suffix placement are not discarded. Each original source descriptor/no-ink slot,
run occurrence, global outline owner and positioned owner remains distinct. Local
outline segment offsets and source/run indices receive their original cumulative
rebases. One reusable point scratch inventory serves all runs. Font bytes and face/
UPM bindings retain the original full immutable sources, not reconstructed cmap
or glyph-ID witnesses. Original descriptor identities, phase-containing points,
control bounds and no-ink slots are unchanged.

The exact reciprocal and product DPI gate applies to every positioned occurrence,
including no-ink. Independent resource bounds remain 2^20 outlines and 2^24 source,
run, segment and occurrence slots. Policies remain Strict/NonzeroVector/
AntialiasedVector and Automatic/IntrinsicSimd/ScalarReference; the original
paragraph route does not acquire the batch-only Monochrome contract.

`NativeParagraphResource` retains all original raw source, admitted/context,
feature/style/device/variation, font byte, logical, positioned, line, interaction,
geometry and mapping inventories. Safe typed slice lengths replace raw pointer
counts and invalid ABI-size/discriminator combinations; this is a portable owned
Rust transport, not an unsafe C ABI loader or a claim of binary compatibility for
Rust outer structs. New fixed payload records retain original C field order and
reserved fields. `copy_into(NativeParagraphResourceBuffers)` checks every one of
34 output capacities before writing any prefix; unused tails survive. Safe borrows
exclude all complete-capacity aliases. `try_copy` owns independent arrays.

`NativeParagraphGeometry::import(&resource)` validates the entire raw record set,
then copies it atomically. It preserves exact font bytes/face/UPM, axis counts,
raw-to-normalized coordinate agreement, style scalar/feature ranges, source-digit
bidi, source/run/slice partitions, original RTL group restoration, occurrence
uniqueness, descriptor-ID consistency, no-ink maps, closed bounded contours,
original final `cluster + 1` line endpoints and every retained interaction bit.
Every unused/later original record receives its original admission. Font bytes
are capped at the original 256 MiB import bound. Import never shapes, hints,
queries a platform font provider or creates an actual `NativeHintedParagraph` or
`NativeDeviceRun`. `paragraph()` is `None` and `is_imported()` is true for imports.
Optional original native diagnostic counters survive verbatim; Rust-produced
resources leave them absent instead of inventing a C++ plan cache.

The safe paragraph API owns per-style feature slices rather than a raw shared
pointer/start pair. Export first retains the original request feature catalog,
reuses an exact existing contiguous match and otherwise appends the style's exact
owned values. Thus transport style starts bind actual exported arrays without
claiming a nonexistent original wire pointer identity. All feature values/ranges
and unused original request features survive.

`FontReplayGeometry::from_paragraph_source` and `Arc<NativeParagraphGeometry>::
into_replay` add a distinct paragraph source variant. `paragraph_occurrences()`
retains positioned/logical/run/run-glyph/descriptor/font/style/outline IDs, actual
cluster ends/full levels, positions and advances. Selected views retain order,
duplicates/no-ink and the same raster/source generation. Batch-only placement and
one-run shaping accessors remain distinct. A flat import's metadata is not a
claim of a live provider or original pixel qualification.

`prepare_native_paragraph_draws` and `Scene::draw_native_paragraph` preserve visual
order by grouping only adjacent occurrences of the same original style. They
validate all style paints and every selected occurrence before any recorder
mutation, construct a private complete command list, reserve the destination and
append atomically. `NativeParagraphPaint` retains the source brush/image/domain,
rendering mode, integer-origin policy and explicit grayscale contrast;
`NativeParagraphPaint::solid` retains the original finite color. Exact target DPI,
identity basis, source ink/brush domain and composed target translation follow the
existing common hinted replay. No scene enum or renderer/shader change is needed.
The original interaction's floating min/max ties now explicitly retain the first
operand, preserving signed zero through flat-record transport.

## Remaining independent scope

- Source-double/SourceIdealUnits fitting, exact source-soft-wrap spaces,
  whole-run/source placement, raw/effective/prepared fitting witnesses and the
  additive version-two source-double import extension remain separate. The raw
  resource cannot accept or float-promote those records.
- Native MIL channel/cache handles and ABI pointer entry points are not fabricated
  by this safe Rust resource layer. GPU Scene recording uses the existing owned
  wgpu path rather than a borrowed native target-view integer.
- Actual pinned safe FreeType binding/execution, browser SIMD admission, original
  source/package/provider/RID controls, pixels and cold/warm performance remain
  unqualified. Import validates transport; it does not prove capture provenance.
- Temporary owned font views during flat import copy their font slices because
  the existing safe `SfntFace` owns bytes. Explicit large inventories use fallible
  reservations; Arc, style and standard nested allocation behavior is unchanged.
