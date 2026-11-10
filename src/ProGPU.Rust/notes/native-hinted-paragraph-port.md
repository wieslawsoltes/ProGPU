# Native hinted paragraph core

Implementation-only increment. No compiler, tests, lint, browser, benchmark or
pixel qualification was run. Only the changed Rust files were formatted.

## Original source

All algorithms are ports of this repository's ProGPU native implementation.
Existing native Unicode, shaping, device capture and measured-writer modules are
called directly. No managed paragraph/shaping executor or foreign implementation
is substituted.

| Original source | SHA-256 | Ported scope |
| --- | --- | --- |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_text_shaping_interop.cpp` | `501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd` | Native style admission; original number substitution/context; `paragraph_layout_core` horizontal hinted float partition/append/retain route; `try_layout_context_hinted_paragraph`; `retain_paragraph_source_coverage`; float `reflow_hinted_paragraph_core` |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_paragraph_internal.hpp` | `6d9422c4c08c39a103cdc74f13e284d92ec3bd652653584e64f1835d2975f9c2` | Immutable source/run/style/logical/positioned ownership |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_font_fallback.cpp` | `0c0b9116b64d8475555c30e35af42301a5e5abb82a1b7ea9cb84536a1f6a390e` | Complete preferred/inventory grapheme fallback selection, null faces, missing state and adjacent merge |
| `src/ProGPU.Native/src/Text/progpu_native_text_cluster_breaks_internal.hpp` | `4c8325da398a22f6055b9e40921341c1a387631ebfc9dcd2ccde283c9171e061` | Exact scalar-to-logical-cluster break projection |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_hinted_paragraph_interaction.cpp` | `dab97e377a7893111c720063088708669b1f6efe63d71378d566f9c97c448232` | Raw measured paragraph source-map admission and owned interaction construction |
| `src/ProGPU.Native/src/Text/progpu_native_text_interaction_impl.hpp` | `65266bdae4fe8939bc485a83abb2c6a5a7034b108fcdfbc8659cfde13355eeb0` | Complete nonfragment float measured records, dedup, hit/caret/move/selection and exact retained record admission |

## Connected public API

`text::native_paragraph::NativeHintedParagraph::shape` accepts the actual immutable
`SfntFace` inventory, Unicode scalar ranges, explicit `NativeParagraphStyle`
inventory, original `NativeShapeRequest`, raw `NativeTextLayoutOptions`, projection
and mandatory actual `NativeHintedProvider`. `NativeParagraphStyle` owns its exact
feature values/ranges, language, source scale, number substitution, device request,
physical-to-logical scale and caller-resolved ascent/descent. Source fonts and every
actual `NativeDeviceRun` survive through the immutable `NativeParagraphSource`.

Original execution order remains number substitution, original/source-digit bidi,
script itemization, graphemes, explicit style-font selection, scalar line breaks,
style/script/full-level run intersection, exact source pre/post contexts, native
shaping/device capture, logical RTL cluster-group restoration, scalar break maps,
original measured writer and retained source coverage. Only original Y-up device
advance/offset signs are inverted. Source scale is identity metadata; the sole
metric scale is original units-per-pixel / 64. Actual L1-adjusted output levels,
content origins and double accumulated line-top frames come from the writer.
All actual descriptor/no-ink/duplicate slots and unpositioned maximum-lines source
runs remain owned. Empty input creates no invented line or caret.

The complete standalone `native_itemize_font_fallback` preserves preferred-first
then inventory ordering, variation-selector/join-control admission and first
available missing-font ownership. `NativeHintedParagraph::shape_with_fallback` is
an explicit Rust composition of that original selector with the original styled
hinted producer. The caller supplies a per-font device/metric/style template;
selected complete ranges replace template ranges. Number substitution is rejected
on this composition because original explicit styled substitution and original
unstyled fallback are distinct policies. `shape` never silently replaces an
explicit style font. Platform discovery and preferred-family catalog policies are
not inferred.

`reflow` accepts only an existing original logical cluster at or after the current
positioned first input, retains the complete original source generation through
`Arc`, and runs only the original float measured writer. Original glyph indices
remain full-paragraph indices; no shaper, bidi or hinting callback runs. The native
C++ code copies immutable logical arrays and shares fonts/runs; Rust shares the
complete immutable source owner, preserving the same records and source lifetime
without redundant copies.

`NativeParagraphInteraction::new` validates complete actual descriptor/source
maps and measured line admission before emitting original advance boxes/carets.
It uses the writer's actual pen origins and full output levels, independent of ink
and glyph offsets. APIs include `hit_test`, `caret`, `move_caret`, `selection`,
`selection_into`, `copy_into` and `validate_retained_records`. Original distance
order, affinity ties, midpoint policy, caret dedup and half-pixel rectangle merge
are retained; transport comparison preserves every float bit including signed
zero. `NativeHintedParagraph::copy_into` preflights all seven complete capacities
before any write. Safe exclusive output borrows replace raw full-capacity alias
checks. Read-only getters retain original source/logical/run/positioned inventories.

All constructors return unpublished candidates on failure with explicit shaping,
bidi, line-break, cluster-map, layout or interaction stage and original underlying
font/Unicode/provider cause. Layout/reflow publication is atomic. The actual
provider may retain its legitimate caches after a failed capture, as before.
Checked arithmetic rejects nonrepresentable coordinates and source ranges instead
of invoking signed C++ overflow. Large explicit buffers use `try_reserve`; nested
owned style/normalization clones and Arc allocations keep the crate's standard
Rust allocation behavior, so this is not a recoverable-all-allocation guarantee.

## Independent remaining layers

- Source-double layout, exact `SourceIdealUnits` integer line measure, source
  fitting/recomposition, source-soft-wrap-space and whole-run placement policy
  remain separate unimplemented native paragraph entry points. This API does not
  accept or narrow those policies into the float route.
- The generic unhinted paragraph producer's inline objects, incremental tabs,
  synthetic trimming, fragment/exclusion/floating layout and vertical paragraphs
  are not added here. The hinted original opt-in rejects these same shape/item
  contracts. Source interaction/empty-hard-row/fragment navigation APIs remain
  distinct; only the original nonfragment raw float interaction is claimed.
- The new paragraph retains complete source/resource ownership and complete
  positioned/interaction copies, but native paragraph-to-combined captured glyph
  geometry and C ABI source-resource import/cache wire records are still separate
  implementation work. Existing one-run `NativeHintedSourceGeometry` and GPU replay
  remain unchanged; no fabricated one-run or metadata-free paragraph is emitted.
- Original context plan cache/diagnostic counters, owned OS font discovery and
  fallback preference catalogs are not synthesized by the safe paragraph API.
- Actual safe pinned FreeType execution/binding and all original source/provider,
  browser, RID/package, pixel, cold/warm and performance qualification remain open.
