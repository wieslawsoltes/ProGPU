# Original CFF/CFF2 prepared-font controls

The original-owned fixture and Windows companion are authored, **not executed**.
Their inclusion does not qualify DirectWrite, native providers, packages or
applications. The final integrated stack must run the original Windows gates and
both native providers without changing existing assertions or deadlines.

## Independent font inventory

`progpu_native_direct2d_cff_font_fixture.hpp` constructs seven tiny OpenType OTTO
fonts from public formats. No foreign font data or implementation is copied.
Existing TrueType fixtures remain byte-for-byte unchanged. Only their original
sfnt metadata/table-assembly utilities are reused; glyf, loca, gvar, avar and hint
programs are removed. The new maxp is version 0.5. Each font has three glyphs:
an empty glyph, one rectangle and one genuine cubic contour.

| Family | UPM | Independent distinction |
| --- | ---: | --- |
| CFF1 default | 1000 | Implicit default top FontMatrix; one PrivateDICT with two local subroutines |
| CFF1 affine | 1024 | Explicit dyadic top shear and translation |
| CFF1 CID | 1024 | Two FD/private/local-subroutine owners; distinct FD translations concatenate before top shear |
| CFF1 CID inherited | 1024 | FD0 omits its matrix; FD1 retains an explicit translation |
| CFF2 static | 2048 | Explicit reciprocal-UPM matrix, two FD/private/subroutine owners, no charstring width |
| CFF2 variable, fixed metrics | 2048 | Actual wght instances 400/650/900; varied contours, no HVAR, fixed hmtx |
| CFF2 variable, HVAR | 2048 | Same original contour instances; independently encoded HVAR advances |

The eleven font instances produce 22 source configurations: supplied signed
advances versus absent advances. The CFF1 charstring widths agree with hmtx.
CFF2 has no charstring widths; its optional HVAR changes glyph 0/1/2 advances by
64/96/128 at the positive endpoint. Absence of HVAR is a positive fixed-metric
family, not an invented rejection of variable CFF2.

The geometry oracle is a separate literal table of line/cubic coordinates and
advances. It does not call the product CFF decoder, matrix composer, variation
evaluator or metric reader. The CID control deliberately makes the FD1 Y
translation and top shear noncommutative. The empty glyph independently exposes
nominal pen movement without ink. Construction uses bounded O(F) time/storage in
the small font bytes; none of this is a general-purpose font-building API.

## Actual original Windows path

`progpu_native_direct2d_cff_glyph_reference.hpp` is registered in the existing
Windows Direct2D test process, following the previous prepared/variable controls:

- Register an original in-memory DirectWrite loader and retain it through every
  source/native font owner. Let `IDWriteFontFile::Analyze` identify the actual CFF
  file and face type before creating the original face.
- Create real variable instances using `IDWriteFontResource::CreateFontFace`.
  Read the complete original Face5 axis order, tags and user-coordinate bits;
  preserve static descriptor axes instead of fabricating an fvar default.
- Read the original CFF/CFF2 table back through `TryGetFontTable`, comparing the
  entire table including Top/FD matrices, FD selection and local-subroutine
  ownership. DirectWrite has no public FontMatrix getter: raw table identity is
  not falsely described as a semantic matrix readback.
- Query actual `INT32` design advances, including the empty glyph. Extract all
  three original outlines at em=UPM into a bounded sink and compare every line
  and cubic control point against the independent design-space table. The sink
  only reflects the documented source Y direction and records a closing edge;
  it does not use native prepared geometry as an oracle.
- For each configuration, compare complete original DrawGlyphRun bytes with
  independently constructed geometry and native-prepared geometry rasterized by
  that same original target. For null advances, add a separate original draw
  using the original design-advance query, not a product metric substitution.

The source scale is exactly 1/32 for every family. Frames include identity,
fractional translation, and shear with a captured binary clip and layer opacity.
Supplied source glyph IDs, offsets, logical order, em, face identity and null
advance identity remain intact. Original table access and callback lifetimes are
separate from the provider's no-per-draw-font-callback controls.

The paired portable/provider helper retains all 22 configurations, full-frame
cold/warm/independent bytes, submission/cache counters, source mutation controls,
and atomic malformed-input rejection. No CFF raster mode, sideways placement,
simulation, shaping, automatic font selection or application qualification is
inferred from these OUTLINE-only source fixtures.

## Primary contract basis

Only public format/API specifications define the authored bytes and observations:
[OpenType CFF](https://learn.microsoft.com/en-us/typography/opentype/spec/cff),
[Adobe CFF format 5176](https://partners.adobe.com/public/developer/en/font/5176.CFF.pdf),
[Adobe Type 2 format 5177](https://partners.adobe.com/public/developer/en/font/5177.Type2.pdf),
[OpenType CFF2](https://learn.microsoft.com/en-us/typography/opentype/spec/cff2),
and [HVAR](https://learn.microsoft.com/en-us/typography/opentype/spec/hvar).
Matrix algebra motivates the discriminating cases; the actual original Windows
result, not that algebra alone, remains the required source-contract gate.
