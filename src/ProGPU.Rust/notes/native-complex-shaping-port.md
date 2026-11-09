# Original native complex-script shaping increment

The native port now has a connected `NativeComplexShaper` for original Indic,
USE, Myanmar and Khmer design-unit runs. It retains native 32-byte glyph records,
packed script metadata, source scalar coordinates, font/normalization ownership,
ordered GSUB stages, nominal metrics, GPOS and final output order. It does not
convert to `ManagedShapingGlyph`, `ManagedFont` or managed lookup execution.

| Family | Implementation in this increment |
| --- | --- |
| Native script/language route | Complete original font ScriptList probing, second/third generation preference, script aliases, direction defaults and language aliases |
| Indic | Category/position/syllable preparation, native 64-slot GSUB probes, consonant positioning, reph/base selection, old/new specification behavior, stable sorting and original permutation-based cluster merging, final prebase/reph movement and init dependencies |
| USE | Original CGJ/ZWNJ filtered index inventory and end sentinel, syllable serials, repha/prebase observations, broken-cluster circles and post-basic reordering |
| Myanmar | Original preparation, broken-cluster circles, kinzi/base positioning, in-place stable insertion sorting and left-matra subgroup reversal |
| Khmer | Original category/syllable preparation, full insertion-capacity preflight, broken-cluster circles, coeng-ro/pre-vowel movement, cluster merge and feature masks |
| Feature scheduler | Required lookup pass; directional group; source-ordered preprocessing lookup pass; exact script basic/topographic stages; remaining presentation features; per-syllable restrictions, feature ranges and original context-match advancement |
| Input | Original normalization admission, per-source-grapheme composition and preserved Indic composites, missing-glyph decomposition/hyphen mapping, Khmer split-matra and cluster policies, variation selectors, directional fallback, modified combining classes, vowel constraints and USE diacritic expansion |
| Positioning | Native HVAR/gvar nominal advances, horizontal/vertical metrics, special-space fallback, original early zero-mark policy, all native GPOS, original kern suppression and attachment resolution, backward output and monotone-character cluster finishing |
| Ownership | Frozen source font, normalized coordinates, features, normalization and pre-context; reusable caller scratch; native partial error writes; atomic publication of owned positioned output |

## Exact public API

The types are exported from `text::native_pipeline` and implemented under its
`complex` module:

* `NativeComplexShaper::new(font, NativeComplexRequest)` freezes one source route;
  `requirements(&[UnicodeScalar])`, `shape(...)`, and `shape_into(..., &mut
  NativeComplexScratch)` perform complete run preparation/execution.
* `NativeComplexScratch::{new, with_glyph_capacity}` own original capacities;
  `glyphs()` exposes the live native prefix, including partial writes on failure.
* `NativeComplexRun` retains the selected `SfntFace`, exact source scalar inventory,
  normalized coordinates, route and positioned native glyph records.
* `NativeComplexKind`, `NativeClusterLevel` and `NativeBufferFlags` preserve the
  original native values. `NativeShapingRoute`, `resolve_native_shaping_route`
  and `resolve_native_language_tag` expose the original route boundary separately.

The normalization argument is the actual existing `NormalizationPlan` owner.
Indic/USE preserve the original requirement that this plan be supplied; no host
normalizer or managed runtime profile is silently selected.

## Original source provenance

All paths below are relative to `src/ProGPU.Native/src/Text/Shaping/`. These
original ProGPU files, with their exact SHA-256 values, are the algorithm sources.

| Source | SHA-256 |
| --- | --- |
| `progpu_native_open_type_complex_internal.hpp` | `5df90d47731587694fe1f655ccf9b2a57fa5a1489be57295c6c2e7b159c9fb0d` |
| `progpu_native_open_type_indic.cpp` | `98af03aebb64837e5e766b52a0dfc3217a89627fd0139de10b995a81c163943f` |
| `progpu_native_open_type_use.cpp` | `04160762a50d131ce63bea0c4e4ed1d941370dcd43534e3158b18f5bebd84346` |
| `progpu_native_open_type_myanmar.cpp` | `141590689938fe591267b179c3cbdedf2e4967844bb397401f0310d66d1bf7ae` |
| `progpu_native_open_type_khmer.cpp` | `e6e991b6a39c345b777b512a2b46ad626842786f7980ccd84d87846c58c7d774` |
| `progpu_native_unicode_syllables.cpp` | `b62e0be20d08ba90c89ce80198e836e9d0155ceb450c03e79c1188d177d1f806` |
| `progpu_native_unicode_shaping.cpp` | `a064a7b8f613d147f55d05728926aee00862e8088c6ed8b14fc66ffcc03efd6c` |
| `progpu_native_open_type_preprocess.cpp` | `eac1e7379742865c85fc160511b5ab0b0bcca5bff8a655efe16ab73d8d3d3794` |
| `progpu_native_initial_mapping.cpp` | `239d0d3ef652ac49931ef865794c725aaa07afe9ed6a3d5c4746566ae1919ea3` |
| `progpu_native_use_diacritics.cpp` | `f56293b4623d7c19acf1021e648b156fe0ba8085688aa2be60c37e93dc9cbb18` |
| `progpu_native_vowel_constraints.cpp` | `2d01605ed428a270e778a6096817e1e3bbc9fc40d3ecbbf56e7b2eac3d18f524` |
| `progpu_native_shaping_route.cpp` | `c26eed40cf38f0f3b21adc3594d8380146b35af0eabb1fb1942433fd6b9e9a2b` |
| `progpu_native_open_type_feature_values.cpp` | `8553eb85882daadedefaa08ad9dfc058a987d83547848072d38a4f5a05d2b132` |
| `progpu_native_space_fallback.cpp` | `6d3dd9d3bc113f9292becdd12f128ec0c8ae67f5d5689a1431f0542ae2e49e07` |
| `progpu_native_open_type_shaper.cpp` | `1590eacb54721eb1c9ae5bd71b824c91ccc0893c8e5a2969625d5bc40131ddce` |

The four complete original script files contain 2,028 C++ lines; the scheduler,
input preparation and positioning additionally port the applicable original
shared run-writer branches. The original fallback-mark, Arabic and Hangul branches
are not conflated with these families.

## Narrow reuse and native distinctions

The category facts reuse existing independent range observations in
`text/unicode/script_properties.rs`. The raw Indic property query is used: its
invalid-input default category/position differs from the managed public Unicode
query. Existing category-only independent grammars in
`text/script_actions/{indic,use,myanmar,khmer}_syllables.rs` are shared through
`complex/syllables.rs`; no foreign generated state tables or lexer topology is
copied. The adapter implements the original native whole-capacity checks, strict
filtered index ordering/end sentinel, prefix zeros, selected source ranges and
1-through-15 serial wrap. No managed glyph mutation action is reused.

The existing independent vowel-constraint facts are reused, retaining original
pair-before-triple shadowing and native full-capacity/cmap preflight. Native
Indic/Myanmar segmentation uses the existing explicit
`GraphemePolicy::ManagedCompatible` contract (native Unicode inventory with GB9c
omitted), not a managed runtime profile. Other routes use the original native
Unicode grapheme policy. Existing original native normalization, GSUB, GPOS,
GDEF/blocklist, HVAR/gvar and legacy-kern primitives remain authoritative.

Native Myanmar/Indic retain their original in-place stable insertion sort, not
the managed scratch counting-sort implementation. Native USE tests original
categories for halants and positional reordering without adding managed ligature
filters. Original zero-metric and default-ignorable sequencing remain intact.
The source's intentional tolerance of a failed dotted-circle/virama cmap probe in
specific script stages remains distinct from strict initial mapping errors.

All run scratch is explicit and reused. Owned publication copies only the final
source/output/coordinate inventories. Existing native non-HVAR phantom metrics
may allocate according to their separate contract. No GPU initialization, host
font execution or frame-time font loading is introduced.

## Independent remaining boundaries

The subsequent [native-unified-shaping-port.md](native-unified-shaping-port.md)
connects the ordinary-script, Arabic (including USE overlap), Hangul, Hebrew,
Thai/Lao, fallback-mark and stretch families through `NativeShaper`.
`NativeComplex*` names remain aliases to that shared run owner.

* The source `Verify` flag requires the independent fragment verification/replay
  family. This entry point explicitly rejects it rather than omitting verification.
* Actual hinted-device metric/outline preparation, device descriptor mapping,
  positioning/boundary capture and provider callbacks remain independent work.
  These are design-unit runs; existing device GPOS contracts are not a hinted
  font producer or source-policy qualification.
* Paragraph fallback, bidi itemization and source application/package/browser
  qualification remain independent. The exact device mark/stretch policies and
  actual hinted metric producer remain outside the connected design-unit writer.

## Architecture and deferred validation

The architecture research already recorded in `DESIGN.md` remains the basis:
[Skia text stages](https://skia.org/docs/dev/design/text_overview/),
[HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
and [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
inform reusable CPU stages and positioned output;
[Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
and [Win2D hosting](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)
inform independent device policy;
[WebRender](https://github.com/servo/webrender/wiki),
[Vello](https://github.com/linebender/vello) and
[Parley](https://github.com/linebender/parley) inform immutable retained generations
and demand-driven rendering. No external engine algorithm was imported.

Per the user's mandatory ordering, no compiler, tests, lint, browser run,
benchmark or new validation fixture was run or authored. Only touched-source
formatting was performed. Source-family differential execution, complete malformed
inventory/error-order evidence, browser execution and native/managed cold/warm
performance remain deferred. Implementation presence does not establish parity.
