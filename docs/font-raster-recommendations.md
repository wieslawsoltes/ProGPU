# Original font raster recommendations

The original Forms grid still requires source GDI and GDI+ raster behavior.
`TextRenderingHint` state alone does not select the required font execution,
coverage, contrast or system policy. The current generic compositor's
`Auto`/`Fixed`/`Animated` hinting values are placement policies, not those six
source rendering hints.

The independent Microsoft [reference run 39096839921](https://github.com/wieslawsoltes/LibreWinForms/actions/runs/39096839921)
retains original selected-face `gasp` bytes alongside font hashes. Its 72 complete
images and case receipts exactly match the earlier reference. Microsoft Sans
Serif at 13 ppem selects version-zero maximum18/flags1; Segoe UI at 12 ppem
selects version-one maximum19/flags7. Their different grayscale recommendations
correlate with their different `AntiAliasGridFit` pixels; that correlation does
not prove the complete raster algorithm.

`SfntRasterizationRecommendations.TryRead` validates the complete original table
before returning its first inclusive matching range. It retains version, queried
size, original maximum and all four independent flags. Unknown versions, reserved
flags, duplicate/descending ranges, truncated inventories and trailing bytes
reject atomically. A valid empty inventory or a size beyond a finite final range
has no recommendation; an explicit zero flag record is still available. The
[OpenType gasp specification](https://learn.microsoft.com/en-us/typography/opentype/spec/gasp)
recommends a final 65535 sentinel but does not require inventing one for a finite
inventory. No nearest-size selection or threshold rounding occurs.

`TtfFont.TryGetStaticRasterizationRecommendations` queries the actual selected
face's owned bytes and validates raw directory records, including malformed
entries omitted by legacy metadata. Duplicate/out-of-range/malformed gasp tables
throw before publishing output. Missing coverage returns false. Non-TrueType
faces and any raw `fvar` presence remain outside this static query; MVAR-adjusted
instance thresholds require a separate implementation. Ordinary font metrics,
glyph placement and rendering are unchanged. Work is O(T+R), for T table entries
and R ranges, with O(1) auxiliary storage and no allocations or GPU initialization.

The controls use the two original 16-byte tables, inclusive boundaries, every
truncated prefix, corrupt unselected later records, version-specific flags,
zero recommendations, missing coverage, raw duplicate/invalid records, existing
source storage identity and two independent TTC faces. They establish metadata semantics;
they do not qualify glyph rasterization or an application.

The paired native implementation is
`Text/Font/progpu_native_font_raster_recommendations.cpp`, sharing the same
value semantics through the C++ header and `progpu.native.text` module. Its
`sfnt_font_view` borrows the caller's existing immutable source lifetime and
returns values without retaining another owner. Both providers use the common
text library. This adds no C ABI, per-font P/Invoke, rendering flag or font-load
policy. Native failure uses the existing status/error convention; the managed
selected-face wrapper throws for malformed data. Both clear unavailable outputs.
Native and managed controls cover every integral size from 1 through 65535 for
each original table, including unselected corrupt records, raw `fvar` rejection,
missing TrueType outlines and independently selected collection faces.

The range walk keeps a dependent previous maximum and first matching record,
with no parallel raster workload or per-glyph work. Scalar validation is bounded
by the original 16-bit range inventory; no SIMD/GPU dispatch, font cache,
device ownership or frame replay changes are involved. The implementation uses
the original ProGPU reader and specification field contract, with no imported
third-party implementation text. Include-based and module-based native controls
exercise the same test source, and malformed-input controls also run with address
and undefined-behavior sanitizers. Whole hosted producer/package and original
application gates remain independent of these local metadata checks.

## Rendering integration still required

The source renderer must distinguish GDI quality from ordinary GDI+ hints and
retain the actual font/size, system settings, target and contrast. The canonical
Forms source already maps GDI hints to LOGFONT quality; portable drawing currently
does not carry that policy into glyph replay. [GDI font quality](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfontw)
and [GDI+ antialiasing](https://learn.microsoft.com/en-us/windows/win32/gdiplus/-gdiplus-antialiasing-with-text-use)
are separate contracts. Never infer that Draft/Proof means non-antialiased output.

The native font adapter currently selects `FT_LOAD_TARGET_NORMAL`. The public
[FreeType glyph retrieval contract](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html)
distinguishes load targets from raster formats and requires cache identity to
reflect policy changes. Interpreter35/40 availability is not Microsoft parity.
Keep font execution lazy and out of frame replay, retain bounded exact-generation
caches, and preserve original layout/interaction and GPU resource ownership.

The architecture follows the existing [font-hinting research](native-font-hinting.md):
[Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
[DirectWrite rendering parameters](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriterenderingparams),
[Win2D layouts](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
[WebRender font instances](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html),
[Vello](https://github.com/linebender/vello),
[Parley](https://github.com/linebender/parley/blob/main/doc/concept.md) and
[HarfBuzz](https://harfbuzz.github.io/what-does-harfbuzz-do.html).
Adopt the separation of reusable layout, font-instance policy and target-dependent
GPU coverage; reject reshaping for paint, captured-image substitution and global
gamma changes. Existing culling, worker preparation, bounded eviction, demand
uploads, batching, device loss and atlas-generation ownership remain unchanged.
Final source integration needs complete original pixels, cold startup/first
interaction, scroll percentiles, allocation/residency evidence and native/package
gates. This reader does not admit source raster policy or close Forms issue174.
