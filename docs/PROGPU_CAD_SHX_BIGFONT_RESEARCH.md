# ProGPU.CAD compiled Big Font SHX research and design record

Date: 2026-08-30

Status: implemented for regular and extended `AutoCAD-86 bigfont 1.0`
containers, drawing-code-page character selection, and retained horizontal
TEXT/MTEXT/complex-linetype output.

## Scope and clean-room method

This record covers Big Font parsing, character identity, regular and extended
program interpretation, immutable primary/Big Font resolution, discovery,
layout, retained rendering, exact selection, printing, and persistence. The
implementation was designed from Autodesk's public format contracts and
independent inspection of compiled binary artifacts. No third-party SHX parser,
renderer, or text-mapping implementation was read, copied, ported, translated,
adapted, or used as an implementation template.

The tests construct original synthetic ProGPU fonts from the documented
contract. No external font bytes are vendored. Existing ProGPU-owned standard
and Unicode SHX code in `CadShxFont`, `CadShxInterpreter`, `CadShxGlyphCache`,
`CadShxTextLayout`, and the retained CAD snapshot/scene pipeline is the direct
in-repository implementation provenance.

## Authoritative contracts

- Autodesk's [Big Font definition](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-DE0CCC57-AC55-4CDC-887E-730FC90364E4.htm)
  defines `*BIGFONT`, ordered lead-byte ranges, two-byte character codes, and
  ordinary text-font programs.
- The [extended Big Font definition](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-00ED0CC6-A4BE-4591-93FA-598CC40AA43D.htm)
  defines the five-byte shape-zero metrics and command
  `7,0,primitive#,basepoint-x,basepoint-y,width,height`. The primitive number is
  two bytes; each basepoint and dimension is one byte. A primitive is normalized
  by the font character dimensions, independently scaled by width and height,
  and placed at the authored basepoint.
- Autodesk's [Big Font extension contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-88F0ED5F-A4CA-41C8-AD63-0E1B0CA5E703.htm)
  documents ASCII escape/lead combinations such as `|A`; these are encoded-byte
  identities, not Unicode scalar values.
- The [Big Font overview](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-CDAF6EAF-85D1-48FC-9A78-43514E0132D5.htm),
  [shape/font descriptions](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm),
  and [special codes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-06832147-16BE-4A66-A6D0-3ADF98DC8228.htm)
  establish the distinct container and shared bounded stroke-program contract.
- ACadSharp's pinned public document model exposes the persisted
  `CadDocument.Header.CodePage` and `TextStyle.BigFontFilename`. ProGPU reads
  those typed values; it does not add a parallel document representation.

## Independently observed compiled envelope

Three unrelated public compiled fonts were inspected strictly as byte arrays:

- [`HT.SHX`](https://github.com/zuoyi001/shxFonts/blob/master/shxfont/HT.SHX),
  Git blob `ff2e3be804e5bca9d4592a814bc4b3429083c62b`, 2,502,245 bytes;
- [`GHD.SHX`](https://github.com/MadhukarMoogala/aps-automation-customfonts/blob/main/Bundle/CustomFonts.bundle/Contents/Fonts/GHD.SHX),
  Git blob `b261fefb9f9dde7c2bddf0e9586780b163894942`, 306,365 bytes;
- [`GHS.SHX`](https://github.com/MadhukarMoogala/aps-automation-customfonts/blob/main/Bundle/CustomFonts.bundle/Contents/Fonts/GHS.SHX),
  Git blob `312a6f3b8ff3037d0f0b3d4fe93f1d354332fded`, 942,107 bytes.

All three independently exhibit:

1. the 25-byte `AutoCAD-86 bigfont 1.0` signature;
2. a little-endian 16-bit directory-entry size of eight, slot count, and
   lead-range count;
3. ordered lead ranges encoded as little-endian 16-bit values within the byte
   domain;
4. eight-byte directory slots containing a big-endian two-byte character code,
   little-endian record length, and little-endian absolute record offset;
5. optional all-zero sparse slots;
6. indexed records containing a NUL-terminated ASCII name followed by a
   terminated program; and
7. exact end after the final indexed record, with one observed optional CR/LF
   trailer.

The fonts differ substantially in size, live record count, ranges, and sparse
directory population. Those differences are used only to distinguish stable
envelope behavior from one artifact's incidental layout. The external bytes are
not test fixtures and are not included in the repository.

## Adopted implementation

`CadShxFont.Parse` owns the input once and publishes a typed Big Font container,
lead ranges, regular/extended metrics, and packed program slices only after
validating directory size, ranges, sparse slots, record identity, offsets,
overlap, terminators, shape-zero metadata, configured limits, and exact trailing
data. Parsing remains `O(B + S)` time and storage for bytes `B` and slots `S`.

Regular Big Font command 7 consumes a big-endian two-byte subshape identity.
Extended command 7 consumes its zero marker, two-byte primitive identity,
signed basepoint, and nonzero width/height. The interpreter saves the caller's
position, pen, uniform/anisotropic scale, and current figure; places the
primitive relative to the caller; composes width/character-width and
height/character-height scale factors; executes through the same bounded
recursion/cycle/command/segment limits; and restores caller state. This matches
the documented composite-character model and the documented examples' explicit
outer character advance. Lines remain analytic, and circles/arcs under unequal
scales become exact axis-aligned `ArcSegment` ellipses rather than sampled
polylines.

`CadDrawingCodePage` resolves the persisted drawing code page once during
snapshot preparation with strict encoder and decoder fallbacks. A Unicode
scalar must encode and round-trip exactly. One non-lead byte selects the primary
standard SHX font. Two bytes select the Big Font only when the first byte is in
the font's declared lead ranges. A one-byte declared lead consumes the next
equally decorated one-byte token, preserving documented ASCII extension pairs.
Unavailable code pages, unrepresentable scalars, invalid lead pairs, sequences
longer than two bytes, and surrogate input fail explicitly. ProGPU never treats
the Unicode scalar value as the Big Font shape number and never silently uses
Windows-1252.

The catalog resolves the primary and Big Font as one immutable generation.
Filename mapping applies independently while preserving container kind; the
ordinary alternate font cannot replace a requested Big Font. Discovery captures
both style filenames before releasing the document lock. TEXT, horizontal
MTEXT, and complex linetype text use the same pair and drawing-code-page
snapshot. A requested but unresolved Big Font rejects the affected content
instead of rendering only its ASCII subset.

## Cross-engine rendering and text gate

The required primary architecture material was rechecked:

- [Skia retained paths](https://skia.org/docs/user/api/skpath_overview/) and
  [SkParagraph shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/);
- [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite),
  [Direct2D geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview),
  and [Win2D `CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm);
- [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html);
- [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  and [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md);
- [HarfBuzz's shaping scope](https://harfbuzz.github.io/what-is-harfbuzz.html).

Adopted: immutable source data, lazy cached analytic geometry, reusable CPU text
placement, retained scene content, visibility culling, and renderer-owned device
resources. Adapted: Big Font's code-page byte identity is resolved during CPU
snapshot preparation and then becomes the same immutable glyph/path placement
used by standard and Unicode SHX. Rejected: OpenType shaping for authored SHX
stroke programs, GPU decoding/layout, per-frame parsing, eager expansion,
texture conversion, backend-specific caches, native-only parsing, TrueType
substitution, and scalar-to-shape guessing.

Startup remains demand-driven: discovery and encoding-provider setup occur
outside replay, programs interpret on first glyph use, and unchanged glyphs and
positioned snapshots are reused. Cache identity includes font instance, shape,
orientation, and immutable catalog generation; Big Font changes therefore
invalidate by ordinary snapshot/resource generation. No glyph texture upload,
font fallback enumeration, variable-font state, hinting/subpixel atlas, or
device-loss-specific Big Font cache exists because the output is retained vector
geometry. Worker preparation remains possible through immutable resolver
snapshots.

## Complexity, parity, and validation

- Parsing is `O(B + S)` time and storage; expected lookup is `O(1)`.
- First-use interpretation is `O(C + P)` time and `O(P + D)` storage for
  commands `C`, path segments `P`, and active depth `D`, under existing bounds.
- Code-page conversion and layout are `O(U + G)` time and `O(G)` retained
  placement storage for source units `U` and glyphs `G`.
- Stable replay performs no parsing, encoding conversion, interpretation,
  layout, filesystem work, managed/native crossing, or upload.

Synthetic tests cover the indexed envelope, sparse slots, ranges, owned bytes,
regular and extended metadata, two-byte subshape calls, anisotropic primitive
placement and ellipse output, strict Shift-JIS and ASCII extension mapping,
malformed/bounded inputs, catalog freezing and substitution rules, ordered
discovery, TEXT/MTEXT/complex-linetype lowering, exact selection, print reuse,
managed/native picture compilation, and DXF/DWG code-page/style/content round
trips.

The managed/native applicability audit finds the same shared boundary as other
SHX paths: managed snapshot compilation creates retained `DrawPath` commands,
and both picture compilers consume them. There is no native CAD parser, text
decoder, or interpreter to duplicate. No C ABI, shader, texture, atlas, upload,
or device-loss contract changed. No performance or image-quality improvement is
claimed; matched final-binary measurements remain required before such a claim.

## Remaining work

- Decide whether non-Unicode `*UNIFONT` encodings 1 and 2 need separate
  persisted-byte APIs.
- Independently verify vertical Big Font placement beyond the existing
  default-insertion dual-orientation TEXT contract.
- Expand the licensed compiled-font conformance corpus without vendoring
  external implementation files or font bytes.
- Add property/fuzz campaigns for indexed offsets, lead ranges, code-page
  round trips, and recursive regular/extended command graphs.
