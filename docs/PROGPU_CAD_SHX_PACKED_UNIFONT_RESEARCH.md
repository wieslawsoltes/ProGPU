# ProGPU.CAD packed-multibyte and shape-file UNIFONT research record

Date: 2026-08-30

Status: implemented with strict drawing-code-page mapping for encoding 1 and
typed non-text eligibility for encoding 2. Independent AutoCAD output
conformance with a licensed encoding-1 artifact remains a named acceptance
gate.

## Scope and clean-room method

This checkpoint implements the two non-Unicode metadata modes carried by the
compiled `AutoCAD-86 unifont 1.0` container. It covers parsing eligibility,
character identity, TEXT, horizontal MTEXT, complex linetypes, standalone
SHAPE resolution, exact selection, printing, retained managed/native replay,
and DXF/DWG code-page persistence.

The implementation was designed only from Autodesk's public contracts, the
existing original ProGPU SHX implementation, and independent inspection of
compiled font bytes as data. No third-party SHX parser, renderer, helper type,
naming, file organization, control flow, table encoding, or source text was
read or used as an implementation template. No external font bytes are
vendored.

The exact approved in-repository provenance is `CadShxFont`,
`CadDrawingCodePage`, `CadShxTextLayout`, `CadShxFontCatalog`, the TEXT and
MTEXT snapshot compilers, the complex-linetype lowerer, and the shared retained
path/selection/print consumers. This checkpoint extends those ProGPU-owned
contracts directly.

## Authoritative contracts and evidence boundary

Autodesk's
[Unicode font description](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-MAC-Customization/files/GUID-D38A5A7B-1877-46B3-8120-32DA5F7430D1.htm)
defines the six-byte `*UNIFONT` header, 16-bit shape identities, two-byte
command-7 references, encoding 0 as Unicode, encoding 1 as Packed multibyte 1,
and encoding 2 as Shape file. Autodesk's
[shape-description contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm)
distinguishes character-numbered text fonts from named shape definitions and
sets the 2,000-byte program limit. The
[DXF header contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-A85E8E67-27CD-4C59-BE61-4DC9FADBE74A.htm)
defines persisted `$DWGCODEPAGE`, while Autodesk's
[text-style contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-LT-MAC/files/GUID-0496BC60-0D07-4982-A395-B83E39EA5CF4.htm)
keeps SHX font selection and shape-file loading as distinct style roles.

The public
[`p5-single-line-font-resources` archive](https://github.com/golanlevin/p5-single-line-font-resources)
was inspected as binary data only. Eighteen compiled SHX artifacts were
checked; thirteen used the UNIFONT
container and every one declared encoding 0, while the remaining artifacts used
standard shape containers. This broadens the compiled-envelope evidence but
does not supply an encoding-1 or encoding-2 behavioral oracle. The new tests
therefore use original synthetic ProGPU byte fixtures, and this record does not
claim an independently measured AutoCAD image differential for those modes.

The packed mapping is a documented-contract inference: ACadSharp has already
decoded persisted text into Unicode, so ProGPU strictly re-encodes each BMP
scalar through the drawing's persisted code page and packs the resulting byte
sequence into the 16-bit authored shape identity. One byte maps to `0x00xx`; two
bytes map in stream order to `0xHHLL`. Any unavailable, lossy,
non-round-tripping, surrogate, or more-than-two-byte result is rejected. The
chosen interpretation preserves both Autodesk's 16-bit shape domain and
`$DWGCODEPAGE`; independent AutoCAD confirmation remains required before the
checkpoint is called externally conformance-verified.

Encoding 2 is not guessed as text. Its explicit Shape file role makes the
container ineligible for TEXT resolution and alternate-font policy, while its
named/numbered programs remain eligible for `ICadShxShapeResolver` and the
existing retained standalone-SHAPE pipeline.

## Adopted architecture

`CadShxFont` retains the original immutable sequential-record parser and now
publishes typed predicates for Unicode, packed-multibyte, and UNIFONT shape-file
roles. Encoding 2 clears `IsTextFont`; the catalog enforces that role during
exact, mapped, style-name, and alternate resolution instead of allowing a later
layout failure.

`CadShxTextLayout` resolves a strict cached drawing encoding once per layout
when either an encoding-1 primary or Big Font pair requires it. Encoding-1
scalars, DXF `\U+hhhh` escapes, and percent-symbol scalars enter the same
reversible mapping. U+00A0 deliberately shares shape 32 but keeps its
nonbreaking layout bit, matching the existing standard and Big Font spacing
contract. Decimal `%%nnn` remains an explicit authored shape identity and does
not undergo character transcoding.

The resulting glyph cache, placements, analytic paths, bounds, scene commands,
selection data, and print commands are identical in type and ownership to
encoding-0 Unicode SHX. No replay-time decoding, filesystem access, GPU text
layout, shader variant, texture, atlas, upload, or native ABI was added.

## Cross-engine rendering and text gate

The required systems were rechecked through primary architecture material:

- [Skia retained paths](https://skia.org/docs/user/api/skpath_overview/) and
  [SkParagraph shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/)
  separate reusable geometry from Unicode/OpenType shaping.
- [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite),
  [Direct2D geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview),
  and [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  keep layout/preparation separate from retained draw resources.
- [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates retained display data, scene work, and renderer/device work.
- [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  retains transformable paths before GPU execution.
- [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  and [HarfBuzz's shaping scope](https://harfbuzz.github.io/what-is-harfbuzz.html)
  keep Unicode analysis, clusters, fallback, and OpenType shaping in reusable
  CPU results.

Adopted: decode once during bounded CPU preparation, cache one immutable
analytic glyph per font/shape/orientation, retain positioned results, cull with
the existing spatial index, and reuse across managed/native rendering,
selection, and output.

Adapted: packed SHX identity resolution is reversible character-to-authored-code
mapping, not OpenType shaping. The drawing code page participates in immutable
snapshot preparation and cache selection policy, while the interpreted path
continues through the existing retained renderer.

Rejected: GPU transcoding, HarfBuzz/SkParagraph/DirectWrite shaping of SHX
programs, platform-default encodings, replacement fallbacks, byte-order
guessing, accepting three-or-more-byte identities, treating encoding 2 as text,
per-frame parsing, and backend-specific SHX caches.

## Complexity, ownership, and parity

- Parsing remains `O(B + S)` time and storage for source bytes `B` and shape
  records `S`.
- Layout remains `O(C + G)` time and `O(G)` retained placements for source
  units `C` and glyphs `G`; each scalar performs one bounded encoding and
  round-trip check.
- First glyph interpretation remains `O(K + P)` for executed commands `K` and
  retained path segments `P`; cached lookup is expected `O(1)`.
- Stable replay performs no encoding, parsing, interpretation, layout, upload,
  or allocation introduced by this checkpoint.

Managed/native applicability is shared at the retained-picture boundary. The
managed snapshot compiler creates ordinary `DrawPath` commands and both picture
compilers consume them; the focused regression compiles the packed result into
a native picture and reuses it in the print plan. The native C++ tree has no CAD
font parser to duplicate. No shader or public C record changed.

## Validation and remaining work

Original synthetic fixtures cover encoding metadata, strict CP932 one- and
two-byte identities, DXF escapes, nonbreaking space, missing/incorrect/UTF-8
code pages, encoding-2 text exclusion, name-based SHAPE resolution, retained
TEXT/MTEXT/complex-linetype output, managed/native picture compilation,
printing, and DXF/DWG code-page/style/content round trips.

Matched Release validation for the final implementation passed 82/82 focused
SHX/font-mapping tests and 730/730 full ProGPU.CAD tests. Release packaging also
produced `ProGPU.CAD.0.1.0-preview.62.nupkg`. These are correctness and package
gates, not latency, throughput, memory, or image-quality measurements.

Remaining acceptance work:

- obtain a redistributable encoding-1 compiled font plus AutoCAD reference
  images or geometry and run matched differential tests;
- independently observe an encoding-2 compiled artifact and confirm name/number
  lookup across DXF and DWG;
- extend property/fuzz coverage over code-page aliases, record boundaries, and
  recursive command graphs.

No performance or image-quality improvement is claimed by this checkpoint.
