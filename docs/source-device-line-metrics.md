# Explicit source device line metrics

The original Forms grid label uses Microsoft Sans Serif 9.75pt, charset0,
at96DPI. Its original GDI line height/ascent are16/13 pixels; the ordinary
scaled hhea values are14.7138671875/11.984375. Rounding the final paragraph
height cannot fix the baseline or spacing of each retained row.

Independent Windows diagnostic39080050996 retains90 measurements across five
families, six point sizes and three DPIs. The45 earlier observations are
unchanged. `TestData/SourceDeviceLines/metrics.json` is the complete unmodified
receipt, with its archive and file hashes in `provenance.json`. The actual VDMX
parser matches all72 original records with tables; the18 Consolas observations
have no VDMX and remain a separate source contract. This does not qualify font
outlines, rendering, a package or the original application.

Microsoft Sans Serif's original version-zero table contains two separate charset
inventories, each ending in a default ratio. The previous reader incorrectly
required the ANSI default to be the last physical entry, even though the following
non-ANSI entries are excluded from ANSI matching. The reader now requires a default
to terminate its own charset inventory in version0. Version1 keeps a single shared
inventory and one final default. Every group, ratio and offset is still validated;
duplicate/default-followed-by-matching entries reject atomically. This bounded
legacy compatibility behavior is backed by the original font and Windows receipt;
the current [VDMX specification](https://learn.microsoft.com/en-us/typography/opentype/spec/vdmx)
describes the default as last and separately specifies version-zero charset
exclusion. It does not explicitly describe this historical two-default layout.

`SourceDeviceLineMetrics.TryCreate` explicitly captures one font's exact existing
VDMX record. It retains the immutable `TtfFont` owner, pixel em size and signed
extrema. Missing records and nonpositive heights return false. Invalid metadata
still fails through the existing whole-table/raw-directory checks. No nearest
size, scaled replacement, caller-authored ascent or rounded Drawing height is
accepted. Static square-pixel nonsymbol TrueType admission remains unchanged.

`TextLayoutFormattingOptions.SourceDeviceLineMetrics` selects that record only
for the same font, exact pixel em and horizontal direction. Reset validates before
changing a reusable generation. Both ASCII and general shaping use the selected
height/baseline; line fitting, blank rows, glyph Y positions and interaction rows
share that frame. Empty text retains zero measured content and a real caret band.
Shaping, original advances, glyph X positions, font fallback and ordinary layouts
keep their existing policies. ClearForReuse retires the selected descriptor;
interaction snapshots retain their completed independent geometry.

The extra work is one constant-time descriptor check per layout and one scalar
selection per horizontal layout path. Descriptor construction uses the existing
bounded VDMX table query once, outside glyph traversal. There is no new per-glyph
allocation, raster loop, upload, draw or native call. This is a correctness change;
no startup, scrolling, allocation or GPU performance improvement is claimed.

## Architecture and clean-room research

The design uses public contracts and independent measurements, not external
implementation text:

* [DirectWrite uniform line spacing](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/ne-dwrite-dwrite_line_spacing_method)
  separates baseline/line distance from glyph shaping. Adopt explicit selection;
  reject an automatic change to ordinary typography.
* [Win2D LineSpacingBaseline](https://microsoft.github.io/Win2D/WinUI3/html/P_Microsoft_Graphics_Canvas_Text_CanvasTextFormat_LineSpacingBaseline.htm)
  exposes the same separation. Here the source record owns both values, so arbitrary
  independent overrides cannot manufacture source parity.
* [SkParagraph paragraph/strut contracts](https://github.com/google/skia/blob/main/modules/skparagraph/include/ParagraphStyle.h)
  distinguish paragraph line policy from text style. Adopt a retained layout policy;
  do not import a generic strut multiplier or fallback-size approximation.
* [Parley line metrics](https://github.com/linebender/parley/blob/main/parley/src/layout/line.rs)
  keep baseline and row geometry with reusable layout. Adopt one row frame for
  paint and interaction, rather than repairing measurement after shaping.
* [HarfBuzz font contracts](https://harfbuzz.github.io/fonts-and-faces-native-opentype.html)
  keep font/glyph metrics and shaping distinct. Preserve original glyph advances,
  contours, fallback identities and shaping inputs.
* [WebRender retained rendering API](https://doc.servo.org/webrender_api/index.html)
  consumes positioned drawing data. Keep source line policy before display-list
  publication and preserve downstream glyph/raster ownership.

| Concern | Decision for this change |
| --- | --- |
| Startup and worker preparation | Explicit immutable descriptor; ordinary layouts perform no table query. Existing CPU shaping remains reusable. |
| Layout and retained-scene reuse | Exact font/em ownership belongs to the whole layout generation; pool reset cannot leak it. Retained glyphs keep their original source order. |
| Visibility and GPU batching | Existing positioned-glyph culling, batches and submission ownership are unchanged. |
| Cache keys, eviction and demand upload | No glyph identity, raster key, texture or path allocation changes. Only authored positions differ for an explicit source policy. |
| DPI, subpixel and hinting | Exact integral pixel em only; no rounding of fractional em, glyph positions or downstream raster phase. Source adapters must preserve actual device policy. |
| Fallback and variable fonts | Existing fallback shaping remains; primary line frame is explicit. Raw fvar stays outside VDMX admission. Missing records remain missing. |
| Device loss and atlas generations | Descriptor has no device/texture handle. Existing realization and generation checks remain authoritative. |

## Managed/native applicability and remaining gates

The change is in paragraph construction before positioned glyph publication.
`GpuPictureNativeSceneCompiler.Glyphs.cs` consumes supplied glyph positions; it
does not calculate these paragraph rows from hhea. The native scene renderer
likewise consumes retained positioned glyph data. Recomputing line spacing there
would apply the source policy twice. There is no new native wire, shader flag or
provider-specific raster contract. Native source text APIs keep their independent
line policy; this API does not silently opt them in.

Authored controls cover signed records, missing/malformed/variable tables, exact
font/size ownership, rejected reset atomicity, ASCII/general/wrapped/blank/empty
rows, both paragraph directions and retained interaction. Original Windows
metric records are an independent table oracle, not a substituted pixel fixture.
Full renderer/headless and required Svg.Skia CI gates still apply. LibreWinForms
must rebuild against the qualified producer, carry the policy through draw,
measurement, trimming and editor layout, and rerun original package/application
captures before claiming the grid issue fixed or releasing.
