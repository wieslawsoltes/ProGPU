# Native measured writer and shaped hinted source

Connected one complete original production route: actual `NativeDeviceRun` →
`NativeHintedTextLayout` → `NativeHintedSourceGeometry` → shaped
`FontReplayGeometry` → existing hinted Scene/atlas/direct-paint renderer. No
managed paragraph execution, fabricated capture, glyph-ID owner lookup or
reconstructed paragraph metadata is used.

The native float measured writer retains complete shaping-safe fitting, legal
trailing whitespace, emergency-break policy, maximum lines, character/word
ellipsis, collapsed final lines, tab grids, original cluster-preserving L1/L2,
justification with final residual distribution, alignment and original scaled
four-lane metrics. Caller-resolved ascent/descent and double accumulated row tops
remain separate from the native driver's metrics. Original retained bidi/pen
origins/line-frame outputs come from the actual emission loop. Intrinsic widths
retain their independent legal/mandatory boundaries, whole source clusters and
candidate tab grids. No floating sum is rounded to manufacture a source width.

The owned hinted adapter copies only used original metadata prefixes; restores
RTL cluster groups while retaining their internal order; converts only original
Y-up signs and applies exactly units-per-physical-pixel / 64; then restores exact
native run and capture-descriptor indices. It keeps full run/source/font/axes
ownership after maximum-lines truncation. Geometry requires exact Arc run
identity and literal reciprocal-DPI equality/product, repeats complete original
layout admission, converts the whole post-GSUB descriptor prefix and retains all
no-ink source/run/fitted slots. Monochrome remains unadmitted for shaped runs.

Shaped occurrences retain actual fitted index, original run index, descriptor,
outline, glyph ID, cluster/end, full source level, writer position and advance.
They do not invent multi-font paragraph/run indices. Selected replay views keep
these identities unchanged and share the same raster-generation owner.
Metadata-free batch access is distinct: `source_geometry()` is now optional,
`font_placements()` is absent for shaped sources, and `shaped_occurrences()` is
absent for metadata-free batches. Existing physical renderer placement is a
projection of these original records, never synthesized shaping.

| Original ProGPU source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/progpu_native_text_layout.cpp` (float measured writer, fitting, justification and intrinsic widths) | `49a7106575011056c3ca3c4362138d210cefddf53492f3e5a61558ddbc664ef2` |
| `src/ProGPU.Native/src/Text/Bidi/progpu_native_text_visual_order.cpp` | `2db6a1ac9e9e2a1003f19f443b62b42c9323a0128e07eb435ccc99cdbbe347f4` |
| `src/ProGPU.Native/src/Text/progpu_native_text_metrics.cpp` (horizontal measured/raw lines) | `fad0dbca5d0c8f76ef66e64a7ad924a366f5b24f7ecc23e2036d950b5923c326` |
| `src/ProGPU.Native/src/Text/progpu_native_text_layout_origin_internal.hpp` | `3f394c6816dc1904df68b0796c4d8b0225233c25ea75b765f56a4702e43f7c0f` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_text_layout.cpp` | `9ea075d01fa9c710e7d72f6e769b6539cd2b87c4d86f70534163c799c2268511` |
| `src/ProGPU.Native/src/Text/Font/progpu_native_hinted_glyph_frame.cpp` (complete layout validation and source/run geometry packing) | `0b23949be0c85e0ae6ddbd02cf3f7e978b8a58036713d81b44dc65808dc17d32` |
| `src/ProGPU.Text/HintedGlyphGeometry.cs` (shaped occurrence and selected-view ownership) | `ac9fa8aa8aff77fcd081e31e5ae65b8e73d5ae04f690761f276888c43f54dbec` |

Original native header contracts and already ported run-outline conversion are
reused. DESIGN.md architecture references remain applicable; no foreign code was
consulted. Safe exclusive borrows replace native full-capacity alias guards.
Low-level writer errors preserve original used-prefix/success-count semantics;
owned adapters publish only complete candidates. Vec allocations are fallible;
Arc allocation follows the crate's existing policy. Signed source cluster-end
overflow rejects rather than invoking C++ signed-overflow undefined behavior.

Independent remaining implementation: native multi-style/fallback paragraph
orchestration and its full interaction/reflow resource transport; original
source-double/ideal-unit width and soft-wrap-space writer; exclusions, floating
items, anchors, columns/vertical text; static DXF/late-camera and direct advanced
blend/R8-target hinted replay. These remain separate original paths, not aliases
for this complete single-run float source. Actual safe FreeType binding, loaded
dependency proof, browser producer, package/application/source pixels and
performance qualification are also unchanged independent gaps.

Mandatory port-first order was preserved: no compilation, lint, tests, fixtures,
browser runs or benchmarks. Only source formatting was performed; no build,
benchmark, trace or result artifacts were generated.
