# Native fragment, exclusion and floating flow

Original ProGPU source inventory:

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/src/Text/progpu_native_text_layout.cpp` | `49a7106575011056c3ca3c4362138d210cefddf53492f3e5a61558ddbc664ef2` |
| `src/ProGPU.Native/src/Text/progpu_native_text_metrics.cpp` | `fad0dbca5d0c8f76ef66e64a7ad924a366f5b24f7ecc23e2036d950b5923c326` |
| `src/ProGPU.Native/src/Text/Interop/progpu_native_text_shaping_interop.cpp` | `501d9e190bd966ce2885ff71960ef1b1b24dbd9ce7f786d86817e6261e5b05dd` |
| `src/ProGPU.Native/include/progpu_native_text.hpp` | `3fe6431abed09f1f15e0ebfc5ee18687ba8fc0ed9a5d65e13ff582c1c3d5c0a7` |

Ports the complete original interval resolver, anchor width policy, fixed/float packing, exclusion-band fitter and measured writer, bounded complete exclusion/floating flow, fragment measurement and connected styled paragraph orchestration. Existing DESIGN.md architecture references apply only to CPU ownership; all implementation comes from original ProGPU sources.

`native_resolve_text_intervals_into` keeps whole rectangle inventory admission, clipping/coalescing, ordered obstacle bottoms and E/E+1 scratch contracts. `native_resolve_anchor_width` retains fixed/fill/fit-content policy, real inset overflow and remeasurement. `native_place_text_anchor` and `native_place_text_floater` retain distinct fixed versus free-interval X policy, original double-to-float edge arithmetic and ordered bounded downward retries.

`native_fit_exclusion_band_into` and `native_layout_exclusion_band_into` preserve original shaping-safe scans, RTL interval ordering, paragraph-relative tab grids, mandatory boundaries and full-width-only indivisible overflow. Height refits use the actual fitted item envelope before publishing glyphs. Local original measured writing retains continuation justification, actual font/source glyph indices, per-fragment translation, row baseline and explicit frames.

`native_layout_excluded_into` and `native_layout_floating_into` port the complete original row loop, first-pass inventory admission, bounded retries/refits, double top accumulation, maximum-row clipping, source-ordered float events, cumulative collision inventory, empty floating rows and independent text/float extents. Typed outputs preserve each original count, next glyph, attempt count and frame. Low-level caller-buffer APIs retain partial scratch/used-prefix writes on failure; only a successful return publishes counts. Safe exclusive borrows replace writable pointer-alias checks. No CPU image or fabricated glyph substitutes for an exclusion or float.

`NativeFragmentParagraph::{shape,from_source,reflow,copy_into}` connects this engine to complete actual native styled shaping through `NativeDesignSource`. Caller-resolved inline/style metrics are required as in original excluded/floating paragraph APIs. Original scalar floating events map only to actual prepared cluster boundaries, with equal events preserving source order. The immutable source retains full fonts, native runs, original/admitted scalars, bidi/script/grapheme/fallback metadata and inline/tab/glyph owners, including clipped content. Reflow shares all actual owners and retains the exact source policy. All buffers/placements are private until complete publication; complete copies preflight every capacity before writing used prefixes. Empty-source publication preserves the original special metrics and event policy.

This is ordinary native design-unit fragment flow. Source-double/SourceIdealUnits layout remains in its separate producer, and no device/source-width policy is silently changed. Fragment-specific horizontal interaction/query builders, multi-style vertical orchestration, generic unhinted Scene geometry transport and document-grid/anchored-subtree orchestration remain independent next layers. The existing actual hinted-provider and package/browser/source-pixel gates remain unchanged. Per the user's mandatory implementation-first order, no compilation, tests, new fixtures, browser runs or benchmarks were performed; touched source was formatted only.
