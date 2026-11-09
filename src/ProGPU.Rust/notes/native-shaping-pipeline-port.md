# Original native shaping orchestration increment

This increment implements the complete original **printable-ASCII, LTR, Latin,
design-unit flow** through a retained `NativeLatinShaper`, plus the original
reusable feature planner and native design/device legacy-kerning engines. The
native GSUB and GPOS executors are used directly. No managed implementation is
substituted, and no external engine implementation was read or translated.

The complete native arbitrary-script shaper is **not finished**. The explicit
Latin API rejects input outside U+0020..U+007E instead of presenting a restricted
implementation as arbitrary Unicode shaping. These are implementation records,
not validation or parity claims.

| Original family | Port in this increment |
| --- | --- |
| Public feature request normalization | Complete defaults, global overrides, original duplicate baseline behavior, explicit first-tag inventory, exact half-open ranges |
| Script/direction feature planning | Complete Khmer, Myanmar, Hangul, Indic, USE, Arabic, common and LTR/RTL/vertical policies; caller supplies the actual resolved native route |
| Native lookup feature ownership | Required feature precedence, original global-feature preference, ordered native feature/variation selection |
| Latin GSUB orchestration | Native substitution, feature-valued alternates, original `rand` state, reverse type-8 visits, expansion/context-match advancement, provenance and fallback metadata |
| Conditional fraction stage | Complete explicit-fraction path applicable to ASCII, including the original second pass after ordinary GSUB; automatic U+2044 actions are outside this API's input domain |
| Native font-to-positioned-run | Original mapping, UTF-16 clusters, nominal metric preflight, cached HVAR/gvar advances, all native GPOS lookups, original kerning suppression, mark advance zeroing and attachment resolution |
| Legacy kern | Complete Microsoft/Apple headers, formats 0/2, permissive malformed-record stopping, GDEF skipping, cross-stream values, native saturation, exact dependencies |
| Device legacy kern | Actual retained `GposDeviceFrame`, original design projection, independent checked SIMD/scalar metric addition, nonzero raw-pair dependency retained at zero projected scale |
| Ownership/publication | Frozen font/features/coordinates; caller-storage partial mutation remains explicit; owned output publishes only after complete success |

## Source provenance

Paths below are relative to the repository root. Files are original ProGPU code.
The recorded hashes are the exact source read for this increment.

| Source | SHA-256 | Ported responsibility |
| --- | --- | --- |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_shaper.cpp` | `1590eacb54721eb1c9ae5bd71b824c91ccc0893c8e5a2969625d5bc40131ddce` | Original `simple_latin` admission and connected design-unit execution branch; source count/capacity, GDEF blocklist, positioning order |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_shaping_features.cpp` | `56e43ea15f621df0ebec674682eed64eb60d01a20e4f72f5b3e3e5b10c3a6aaa` | Complete baseline and route-specific feature planner |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_shaping_request_features.cpp` | `3c90abaeaef9b9f57d60644fca3353790559828f122f5aa1bb00c938784530ae` | Complete original request normalization |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_shaping_configuration.cpp` | `1e8b1a0ee09730c765b162989195753dcb5efc43de7170dfdf9af13371dd1039` | Original global settings followed by active original ranges; Latin route configuration |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_feature_values.cpp` | `8553eb85882daadedefaa08ad9dfc058a987d83547848072d38a4f5a05d2b132` | Native lookup ownership, value/range dispatch, explicit fraction pass |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_legacy_kern.cpp` | `50435413f01ecc686e66c692a8e9086e3778cd1fbd069063278faee1304e5ea3` | Complete design/device legacy kern execution |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_fallback_marks.cpp` | `dba3622518f53e434f8ea5dd69790041b67787a3d9e8d48f284e446e4bacb466` | Exact mark-free ASCII branch still validates original header/units-per-em |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_space_fallback.cpp` | `6d3dd9d3bc113f9292becdd12f128ec0c8ae67f5d5689a1431f0542ae2e49e07` | Original U+0020 mapping/metric no-op with retained cmap admission |
| `src/ProGPU.Native/src/Text/Shaping/progpu_native_open_type_gsub_internal.hpp` | `1059b5285502dc4fb90ad7fce5c0c259b7c695e52cdb08a5d73bd993eece6f0c` | Original fallback ligature count/component packing and clearing |
| `src/ProGPU.Native/include/progpu_native_text.hpp` | `3fe6431abed09f1f15e0ebfc5ee18687ba8fc0ed9a5d65e13ff582c1c3d5c0a7` | Original public run/options/feature and positioned-record contracts |

The existing native-owned ports `font/layout/{selection,gsub,gdef}.rs`,
`font/horizontal.rs` and `font/layout/native_gpos*` provide their original algorithms.
The GPOS source inventory is recorded in [native-gpos-port.md](native-gpos-port.md).

## Implementation boundaries

`NativeLatinRequest` exposes original language tags, public feature ranges,
normalized coordinates, alternate value and zero-mark-advance policy. Its route
is explicitly Latin/LTR, with the original default grapheme clustering and no
verification flag. ASCII code points remain unchanged by native GSUB, so source
default-ignorable rewriting, Unicode mark fallback, directional preprocessing,
canonical composition and automatic U+2044 fractions are provably inactive.
Their observable unconditional checks remain: the space cmap query after GSUB,
the zero-mark policy's `head`/units-per-em admission, and all native GDEF mark
advance/kerning behavior. Actual substituted glyphs may still have mark classes
and arbitrary GPOS attachment relationships, which execute normally.

The plan prepares lookup ownership once and owns exact font/table generations.
The caller-buffer path performs no allocation itself; existing non-HVAR variable
phantom/outline helpers can allocate as already documented by those APIs.
Owned shaping reserves the original three-times-input glyph capacity, or accepts
an explicit larger capacity. Capacity exhaustion rejects rather than retrying
with a fresh random or partially transformed run. Scratch lives only during the
call, and no build/benchmark/browser artifacts are produced.

Native finite width conversion retains `lround` semantics before signed-16
clamping. Nonfinite advances reject explicitly instead of invoking the C++
nonfinite integer-conversion domain. Device kern retains the actual source
font-object identity and does not synthesize hinted metrics or outline points.

## Remaining native scope

The subsequent [native-complex-shaping-port.md](native-complex-shaping-port.md)
increment adds native route/language discovery, the complete four complex-stage
families, their normalization/mapping/preprocessing, spaces/fractions and connected
design-unit positioning, including vertical/reverse finishing. Remaining families:

* Arabic joining/form/preprocessing/context/reversal/fallback/stretch orchestration,
  including USE routes that independently require joining; Hangul and other
  ordinary noncomplex preprocessing/finishing.
* Full native fallback-mark extent positioning.
* Actual hinted-device font producer, metric/outline preparation, descriptor
  remapping, source positioning/boundary capture, fragment-verification callbacks,
  prepared-glyph placement and final provider integration. Existing GPOS/device
  contracts and legacy-device kerning are not claims that this producer exists.
* Paragraph-level font fallback, bidi/run itemization and source application/
  browser/provider/package qualification of this native orchestration route.

Managed pipeline functionality is independent and does not close these native
source-family gaps.

## Architecture and deferred evidence

This follows the architecture research already recorded in `DESIGN.md`:
[Skia text stages](https://skia.org/docs/dev/design/text_overview/) and
[HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
inform separate CPU shaping stages; [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
informs reusable positioned output; [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
and [Win2D hosting](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)
inform separation from GPU/device state; [WebRender](https://github.com/servo/webrender/wiki),
[Vello](https://github.com/linebender/vello) and [Parley](https://github.com/linebender/parley)
inform immutable retained CPU generations and demand-driven rendering. Their
implementations are not used as replacements for ProGPU algorithms.

Per the user's mandatory ordering, **no compiler, tests, lint, browser runs,
benchmarks or new validation fixtures were run or authored**. Only touched-source
formatting was performed. Differential native/managed evidence, malformed font
behavior, browser execution and cold/warm performance remain deferred gates.
