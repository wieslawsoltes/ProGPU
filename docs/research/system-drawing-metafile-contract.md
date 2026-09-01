# System.Drawing metafile contract

## Objective and compatibility boundary

This subsystem restores the .NET 10 `System.Drawing.Imaging` metafile public
model without routing the portable product path through GDI+, HDCs, opaque
native handles, reflection, or WinForms-shaped compatibility objects. The
pinned `System.Drawing.Common` 10.0.11 reference assembly defines the public
type and member shape. Microsoft Open Specifications define the byte-level
[WMF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/ba5458c6-e885-41e6-b5d7-d54ef9e1065f),
[EMF](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e0137630-f3ad-492c-bde9-e68866e255ba), and
[EMF+](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emfplus/8b9363ba-cf5b-4d35-bced-0620e3b5b5ef)
contracts. Parser, validation, record storage, lowering, and rendering code is
original ProGPU code.

File and stream construction is the portable path. It snapshots the source,
parses it transactionally into immutable typed records, and exposes metadata
without retaining caller-owned streams. Handle import/export and HDC-backed
recording remain explicit Windows adapter operations. Those members preserve
their official public shape but must throw a descriptive
`PlatformNotSupportedException` until a typed Windows adapter is supplied; a
raw pointer is never treated as a portable image or drawing context.

## Bounded parser model

All formats are little-endian. A reader first identifies one of three roots:

- placeable WMF starts with key `0x9AC6CDD7`, followed by the standard
  18-byte `META_HEADER`;
- standard WMF starts directly with `META_HEADER`; and
- EMF starts with an `EMR_HEADER` whose type is one, size is aligned to four
  bytes, and signature is `0x464D4520`.

Placeable WMF parsing validates the XOR checksum, nonzero units-per-inch,
ordered bounds, the declared word count, maximum-record declaration, record
word lengths, and terminal `META_EOF`. Standard WMF has no device-independent
frame; its initial public bounds remain empty until playback derives a window
and viewport. EMF parsing validates the declared byte count, four-byte record
alignment, every record's in-buffer extent, and terminal `EMR_EOF`. The record
count accepts both specification-shaped producers that include `EMR_HEADER` and
legacy Windows-compatible producers that count only the records following the
header; no other count mismatch is accepted. The header's device-pixel and
physical-millimeter sizes derive DPI only when both denominators are positive.

An `EMR_GDICOMMENT` immediately following the EMF header may contain an EMF+
stream. Its identifier, 12-byte record headers, aligned sizes, data sizes,
header-first ordering, and end marker are validated independently. The EMF+
header flag distinguishes `EmfPlusDual` from `EmfPlusOnly`; its logical DPI and
graphics version populate `MetafileHeader`. The outer EMF transport and inner
EMF+ stream are validated independently. The enumeration table replaces the
`EMR_GDICOMMENT` transport envelope with its decoded EMF+ records at that
envelope's source position, while preserving surrounding outer EMF order and
official `EmfPlusRecordType` identities.

The parser fails closed before publishing a `Metafile`. Initial limits are
256 MiB per source, 1,000,000 outer records, 1,000,000 nested EMF+ records,
16 MiB per record, 65,535 WMF object slots, and checked arithmetic for every
offset or unit conversion. Limits are implementation safety ceilings rather
than permission to allocate their maxima eagerly. Records retain offsets and
lengths into one owned source buffer; enumeration creates no per-record byte
array unless a callback requests the public unmanaged view.

## Public records and playback

`EmfPlusRecordType`, `EmfType`, `MetafileFrameUnit`, `MetafileType`,
`MetaHeader`, `MetafileHeader`, `PlayRecordCallback`, and
`Graphics.EnumerateMetafileProc` preserve their official identities. Header
objects are defensive managed snapshots. `GetMetafileHeader` for file, stream,
and instance inputs uses the same parser as construction, so validation cannot
diverge between inspection and playback.

Enumeration exposes the owned typed records in source order for all 36 official
destination overloads. It pins the one owned source buffer for the complete
walk; each nonempty callback pointer addresses the record payload inside that
buffer and is valid only for the callback duration. No payload is copied per
record, and a callback returning `false` stops cleanly. The public
`callbackData` pointer remains ABI-compatible; matching the official managed
adapter, the managed callback's `PlayRecordCallback` argument is null.

Direct `Graphics.DrawImage` playback now has bounded EMF and WMF vector tranches.
Rectangle, point, source-rectangle, and three-point affine overloads compose the
metafile source bounds, caller destination mapping, and host graphics transform.
Four-point projective playback and `ImageAttributes` fail explicitly until the
typed vector player can preserve those semantics. Enumeration remains a
separate callback walk; its destination arguments do not trigger rendering, and
`Metafile.PlayRecord` remains unavailable until it can bind to a typed active
enumeration session without accepting arbitrary native addresses.

The implemented EMF record set is deliberately narrow: `EMR_SAVEDC`, relative
negative `EMR_RESTOREDC`, `MM_TEXT` and `MM_ANISOTROPIC` window/viewport state,
world-transform set/modify, polygon fill mode, move/line, rectangle, ellipse,
polygon/polyline/poly-polygon/poly-polyline, intersect-clip rectangles,
transparent/opaque background state, `R2_COPYPEN`, solid or null cosmetic pen
creation, solid or null brush creation, stock/dynamic selection, and safe
deletion. The typed text slice adds text/background colors, alignment,
justification, Unicode `EMR_EXTCREATEFONTINDIRECTW` object-table fonts, and
`EMR_EXTTEXTOUTA/W` with opaque/clipped rectangles, RTL layout without explicit
advances, and 32-bit character-cell advances. Saved DC state includes the
retained clip and text state and restores both through the typed `GraphicsState`
path. Typed path playback adds bracket creation, closure, selection, abort,
fill/stroke, flatten, widen, clip selection, and miter-limit state over every
implemented EMF vector family. Region clipping adds `EMR_EXTSELECTCLIPRGN` and
`EMR_SETMETARGN`: application clipping and metaclip are separate managed
`Region` values, all five `RGN_AND`/`OR`/`XOR`/`DIFF`/`COPY` modes are typed,
and SaveDC/RestoreDC snapshots both layers. `RegionDataHeader`, rectangle count,
byte size, ordered rectangles, containing bounds, and the data-size envelope
are validated before state changes. Rectangles form a balanced retained-region
tree; the XOR/difference combinations that require it materialize exact
axis-aligned scans before recording while rotated or curved regions retain the
deferred-vector path. An omitted `RGN_COPY` restores the default application
clip without escaping the metaclip. EMF/EMF+ structural and comment records are
nonvisual. Typed DIB playback adds `EMR_STRETCHDIBITS`,
`EMR_SETDIBITSTODEVICE`, `EMR_BITBLT`, `EMR_STRETCHBLT`, source-bearing
`META_DIBBITBLT`,
`META_DIBSTRETCHBLT`, `META_STRETCHDIB`, and `META_SETDIBTODEV`, plus EMF/WMF
stretch-mode state. The shared bounded
decoder accepts `DIB_RGB_COLORS`, `DIB_PAL_COLORS`, and `DIB_PAL_INDICES`
with uncompressed `BI_RGB` 1-, 4-, 8-, 16-,
24-, and 32-bit pixels plus `BI_BITFIELDS` 16- and 32-bit pixels in
`BITMAPINFOHEADER`, `BITMAPV4HEADER`, or `BITMAPV5HEADER` envelopes. It handles
RGBQUAD palettes, RGB555, RGB565 and arbitrary valid contiguous bit masks,
optional V4/V5 alpha masks, and bottom-up `BI_RLE8`/`BI_RLE4` indexed streams.
The same path accepts uncompressed 32-bit `BI_CMYK` in C/M/Y/K byte order and
the indexed `BI_CMYKRLE8`/`BI_CMYKRLE4` variants. CMYK channels use the existing
typed ProGPU conversion, including multiplicative black ink, while the RLE
variants retain the bounded RGBQUAD/logical-palette index path.
Logical palettes are typed EMF/WMF objects with create, select, set, resize,
realize, WMF animate, deletion, and SaveDC/RestoreDC selection behavior.
Sixteen-bit DIB color-table indexes and direct pixel indexes resolve against
the selected palette after complete table and object bounds validation. Palette
realization is a validated no-op because retained playback consumes logical
colors directly instead of mutating an OS hardware palette.
The RLE state machine bounds encoded and absolute runs, end-of-line/end-of-bitmap
escapes, deltas, word padding, palette indexes, and the declared compressed byte
count before publishing pixels. It preserves unspecified pixels as palette index
zero and supports supplied scan bands without assuming DWORD-aligned compressed
rows. `BI_JPEG` and `BI_PNG` accept bit-count-zero positive-height headers with
no color table, exact declared buffer sizes and matching file signatures. Codec
dimensions are checked against the DIB header before pixel allocation; decode
failure rolls back transactionally. For `SetDIBitsToDevice`, the complete
JPEG/PNG file is decoded and `StartScan`/`cScans` selects only the requested
rows from that validated image. Uncompressed and bit-field paths handle DWORD row
stride, bottom-up and top-down storage, source cropping, sign-directed
mirroring, partial scan bands, destination transforms, and saved nearest or
halftone sampling state. BI_RGB's unused 32-bit high byte is made opaque rather
than misread as alpha. Record-relative header/bit offsets, sizes, disjointness,
dimensions, planes, palette bounds, scan ranges, source geometry, and arithmetic
are validated before a retained texture can publish. The byte layouts and
playback state follow the
official [EMR_RECTANGLE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/3c471238-0a02-4992-90a2-bfd2afd98f2a),
[EMR_CREATEBRUSHINDIRECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/b9a8ef5d-0089-4e42-b317-e6ebc0ff098f),
[EMR_CREATEPEN](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/2374647f-df67-48e3-86aa-384715c28e71),
[EMR_SELECTOBJECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/145b063d-5f96-41fe-b7ae-1e615b2bc2bf),
[EMR_SETMAPMODE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/aa4ad35d-fa42-4a4f-959a-8b41304e1b05),
[EMR_SETWORLDTRANSFORM](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/985724c0-4db1-48f0-b346-67288b3288cb),
[EMR_POLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/eb916781-58b6-4e92-b606-68071aa65733),
[EMR_EXTSELECTCLIPRGN](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/c6b9f4e6-27f6-4a4d-a383-c2daf5da11d9),
[EMR_STRETCHDIBITS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/89c0d808-0dea-413f-be40-2e9e51fa36ac),
[EMR_SETDIBITSTODEVICE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e8816cc6-35d2-43e6-8d88-d69cd342372e),
[EMR_BITBLT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/347d1c44-1847-47ec-8762-7059e9e9b185),
[EMR_STRETCHBLT](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emrstretchblt),
[META_DIBBITBLT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/524aa748-f274-4bd3-a4c1-f280bd6cac09),
[META_DIBSTRETCHBLT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/e666a66f-b29d-4adb-82da-e00eaf032ea6),
[META_STRETCHDIB](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/7ebae08d-61ee-4d82-9aa5-9217ba2aa8c1),
[META_SETDIBTODEV](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/d0e77d4d-653f-4535-a4db-1496af84acdc),
[DIBColors](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/a5e722e3-891a-4a67-be1a-ed5a48a7fda1),
[EMR_CREATEPALETTE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/07e1492b-e4bb-4394-934f-4eaee67ab8ff),
[EMR_SELECTPALETTE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e6a4ce2a-209d-43df-b763-5d8e54c21a10),
[EMR_SETPALETTEENTRIES](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/88348296-3c9a-488f-bbf7-19c897535372),
[META_ANIMATEPALETTE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/abac3df4-c19a-4102-9344-b5bf68fcfa99),
[DeviceIndependentBitmap](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/7376542a-cce9-4625-8ead-585e9538f9f1),
[BitmapInfoHeader](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/567172fa-b8a2-4d79-86a2-5e21d6659ef3),
[BitCount](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/792153f4-1e99-4ec8-93cf-d171a5f33903),
[Compression](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/4e588f70-bd92-4a6f-b77f-35d0feaf7a57),
[Bitmap Compression](https://learn.microsoft.com/en-us/windows/win32/gdi/bitmap-compression),
[RLE4 bitmap example](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/73b57f24-6d78-4eeb-9c06-8f892d88f1ab),
[JPEG and PNG bitmap extensions](https://learn.microsoft.com/en-us/windows/win32/gdi/jpeg-and-png-extensions-for-specific-bitmap-functions-and-structures),
[BitmapV4Header](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/071b0c0d-c2df-4f1c-9828-d03c26002c61),
[RegionData](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e66601f2-9b5c-4619-8476-ddb7b087551b),
[RegionMode](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/b7f99f50-dd2f-4528-9624-f74140368019),
[clipping record](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/0ca0d18e-324e-452f-9a41-26e1a82e3e03),
[EMR_EXTCREATEFONTINDIRECTW](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/7e266b6d-32e5-4201-b687-8ec40c24cd73),
[EMR_EXTTEXTOUTA](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/6b582a71-3c29-4fc6-a0f4-1f8a313739a1),
[EMR_EXTTEXTOUTW](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/a59a79ac-328e-492d-a34d-e02727af6edf), and
[EmrText](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/dd585d0a-5d7c-4034-963a-1141af836972)
contracts. Each supported record lowers to existing typed `Graphics`, brush,
and pen operations. Playback records into a temporary `DrawingContext`; an
unsupported or malformed record reports its type and byte offset and prevents
the entire temporary command stream from being appended.

The text parser treats offsets as record-relative, requires format-appropriate
in-bounds non-overlapping string/advance ranges, rejects invalid UTF-16 or
selected-font charset sequences, and bounds `LOGFONTEXDV` to its protocol
maximum. ANSI records decode through the selected font's GDI charset; explicit
advances require a one-byte encoding with a one-to-one UTF-16 mapping until a
typed DBCS byte-to-cell seam lands. `ETO_IGNORELANGUAGE` is accepted
only for ASCII text carrying explicit cell advances, matching the common print-
spool form without pretending to implement an unshaped complex-script path.
Glyph-index, numeric substitution, small-character encoding, two-dimensional
advances, the `ETO_NO_RECT` optional layout, bidi plus explicit advances,
vertical fonts, and independent
escapement/orientation remain explicit typed boundaries.

The initial WMF family follows the official 16-bit record layouts and keeps its
state/object path separate from EMF. It implements background mode/color,
`R2_COPYPEN`, `META_SETRELABS` no-op semantics, polygon fill mode, text-alignment
state, text color, `CREATEFONTINDIRECT` object-table fonts, charset-decoded
`TEXTOUT`, and `EXTTEXTOUT` opaque/clipped rectangles and signed character
advances. `META_SETTEXTCHAREXTRA` is retained as unsigned 16-bit DC state and
adds logical-unit spacing to every character cell for `TEXTOUT` and
`EXTTEXTOUT` without a `Dx` array. Outside `MM_TEXT`, playback transforms and
rounds the spacing to the nearest device pixel before returning it to the
logical baseline. Alignment, measured opaque background, compatible-mode
escapement, and `TA_UPDATECP` use the same effective advance. An explicit `Dx`
array supplies the complete character-cell origins and therefore overrides
default character extra. `META_SETTEXTJUSTIFICATION` retains unsigned break
count and total-extra state, distributes integer quotient/remainder spacing at
space break characters, and carries the remainder across consecutive text runs.
Resetting the state clears that error term. It uses the same mapped total,
alignment, background, escapement, current-position, and `Dx`-override rules.
Selected WMF underline and strikeout font bits lower through the same
OpenType-metric retained decoration path as ordinary `Graphics.DrawString`.
Compatible-mode fonts whose escapement and orientation match rotate the text
baseline, glyphs, measured background, and decorations together in device
space. `TA_UPDATECP` maps the transformed advance back into logical state, so a
following text record continues along that same baseline.
Text/anisotropic map modes, set/offset/scale window and viewport state,
move, lowest-free object-table allocation and slot
reuse, selection/deletion, solid/null pens and brushes, polygons, polylines,
poly-polygons, current-position lines, explicit-color device pixels, counterclockwise
elliptical arcs, filled/stroked pies and chords, rectangles, ellipses, and
rounded rectangles, plus exact pattern-copy/blackness/whiteness rectangle
blits. Source-bearing packed-DIB bit blits, stretch blits, explicit-usage
stretch blits, and partial scan-band transfers share the bounded decoder and
retained texture path described above. Intersect-, exclude-, and logical-offset clip
rectangles lower through the retained Region clip path, and SaveDC/relative
RestoreDC uses the complete managed state snapshot described below. The record
inventory used by the canonical LibreWinForms `telescope_01.wmf` asset remains
fully covered; rectangle and ellipse playback are additional typed families. The
implementation is based on the official
[WMF object record rules](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/aeab62b8-03ab-48c0-8176-09c392f3c9da),
[META_POLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/0982bbfc-feb7-4f06-a8fb-ad03b465ffea),
[META_POLYPOLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/941630ee-85e6-4a0f-b02f-1c534b3fa9f8),
[PolyPolygon Object](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/cf1ac7c0-749d-4678-b32c-6f68d2a5f268),
[META_SETMAPMODE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/c70612fb-5beb-4adf-b919-8f55deba943a),
[META_SETVIEWPORTEXT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/d558b1a4-26ba-4ad6-a9c8-c3caecaf0b1a),
[META_SETVIEWPORTORG](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/a508f8e1-8c3d-499b-a800-9dc8e1199d0b),
[META_OFFSETVIEWPORTORG](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/9d9ea10a-16ea-4967-b951-4aa4efd0354f),
[META_OFFSETWINDOWORG](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/6a411d75-d922-4f6a-8fc2-4360766499de),
[META_SCALEVIEWPORTEXT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/3742ef91-28b9-4a54-97d8-35959662b8c1),
[META_SCALEWINDOWEXT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/8e5dfa2b-2107-4726-86c3-81ec3380016a),
[META_OFFSETCLIPRGN](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/63e5f3cc-7b05-48b8-b602-fdd983eb3bd0),
[META_ARC](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/742097b4-5879-4c36-b57e-77e7cc152253),
[META_LINETO](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/bf92fda0-2d68-4ea2-8b31-6a0a22574d7f),
[META_SETPIXEL](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/de9a67c4-2ddb-4e5b-b5df-eca1772af366),
[META_PATBLT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/00e25092-a0d3-4b39-a0cf-ab49be6dddcd),
[TernaryRasterOperation](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/1605dd68-a635-4639-ab81-99ff3e3fc5a3),
[META_CREATEFONTINDIRECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/6040492f-7b58-49bd-bfef-ef1126bdffe3),
[Font Object](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/dabb1ed6-e5e8-4243-80ed-e63443e5484f),
[LOGFONT escapement and orientation](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logfonta),
[META_SETTEXTCOLOR](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/2bdfee2b-3016-4a6a-b4cd-c725ce9cb2a0),
[META_SETTEXTCHAREXTRA](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/e9ac157a-cb53-406d-be53-f249cd5b2dff),
[SetTextCharacterExtra](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-settextcharacterextra),
[GetTextExtentPoint32W spacing rules](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-gettextextentpoint32w),
[SetTextJustification](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-settextjustification),
[WMF state records](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/54e4a2e0-5ca9-4c69-b6a8-dc8f938c68ae),
[META_TEXTOUT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/96531e1a-1875-49e5-b797-b4c4c50fa789),
[META_EXTTEXTOUT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/7d07c44a-a828-4b82-9af0-e0a81cced5a8),
[ExtTextOutOptions Flags](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/830cec14-2f3c-46f3-8f20-82b3da370573),
[Rect Object](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/3ccb757b-5eaa-460c-9269-6b638484640f),
[TextAlignmentMode Flags](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/2cf0d802-5db7-42f6-bb75-50ff195a6c7c),
[META_PIE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/b3f3e55f-6f69-4678-87ea-e6feb6af6eeb),
[META_CHORD](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/44aa3feb-ab01-47ca-9386-62acf7df5263),
[META_ROUNDRECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/9c262e3b-e631-4343-8b90-0441872f1e9a), and
[state record](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/54e4a2e0-5ca9-4c69-b6a8-dc8f938c68ae)
contracts. WMF paths and select-clip-region records, `EXTTEXTOUT` glyph-index, numeric-
substitution, two-dimensional, DBCS-advance, and bidi-advance modes, independent
escapement/orientation, vertical fonts, SYMBOL glyph-index mapping,
source-required playback-device-context
`META_DIBBITBLT`/`META_DIBSTRETCHBLT` variants,
richer GDI objects,
other WMF drawing families, and nonstructural EMF+ drawing records remain
explicit later tranches. The Win32 contract identifies character extra as
incompatible with complex shaping; playback consequently rejects explicit RTL
plus character-extra combinations until a typed bidi cell-positioning path can
match Windows behavior rather than silently perturbing visual clusters.

Portable comment recording is a typed encoder over the same immutable record
model. `ProGPU.SystemDrawing.PortableMetafile.Create` accepts a caller-owned,
writable stream and integer pixel bounds without an HDC. The resulting
`Metafile` supplies one exclusive `Graphics.FromImage` session.
`Graphics.AddMetafileComment` copies each caller buffer synchronously and emits
an aligned EMF+ `Comment` record. Disposing the graphics instance builds an
EMF+ header, the ordered comments, and an end record inside one bounded
`EMR_GDICOMMENT`, adds the outer EMF header/end records, validates the complete
owned bytes with the normal parser, writes and flushes the target, and publishes
the immutable document. The stream remains caller-owned.

The comment envelope is capped at the parser's 16 MiB per-record ceiling and
all size, alignment, coordinate, and frame-unit arithmetic is checked. Header
queries and cloning reject incomplete recording; a second recorder, a read-only
target, invalid bounds, a disposed owner, or comments outside the active
session fail explicitly. The initial portable encoder accepts comment records
only. If ordinary retained drawing commands were recorded, finalization aborts
before writing anything rather than publishing a metafile that silently lost
content. Typed drawing-record encoding and official HDC constructors remain
later work; HDC constructors stay Windows-adapter entry points.

## Quality and performance gates

The initial parser gate uses hand-built minimal placeable-WMF, standard-WMF,
EMF, EMF+ only, and EMF+ dual fixtures. Tests cover exact public enum values,
header properties, source ownership, cloning, file/stream equivalence,
non-seekable streams, checksum and signature failures, truncation, integer
overflow, record alignment, count mismatches, missing EOF, nested EMF+ bounds,
and explicit handle seams. Mutation fuzzing must reject malformed input without
access violations, hangs, partial objects, or unbounded allocation.

Rendering gates compare representative primitives and state transitions in the
managed bitmap compositor. The first gate covers retained pixels, destination
scaling and translation, object selection, map/world/save/restore composition,
saved clip restoration, multi-polygon count/point validation,
explicit image-attribute/projective rejection, and transactional rollback after
a supported draw but before an unsupported record. EMF region gates cover all
five combine modes, metaclip containment, omitted-copy reset, selection-time
transforms, saved/restored two-layer state, malformed headers/bounds/modes, and
whole-stream rollback. The fixed 64-selection warmed allocation gate permits
4-7 MiB per complete retained playback. WMF gates cover 16-bit
parameter ordering, lowest-free slot reuse, state, pen/brush selection,
polygon/polyline/poly-polygon/current-position-line/set-pixel/pattern-blit/arc/pie/chord/rectangle/ellipse/rounded-rectangle pixels,
intersect/exclude clip pixels, zero-corner rectangle fallback, invalid-bound rejection,
SaveDC/relative RestoreDC scope, and transactional rollback. Saved WMF state
includes window and viewport origins/extents, current point, world transform,
fill/map/background/raster/text/background-color settings, selected pen and
brush, selected font, text color, and the typed `GraphicsState` clip. Text gates
cover selected font/color output, transparent and measured opaque backgrounds,
saved text-state restoration, and invalid-alignment rollback. The real
LibreWinForms `telescope_01.wmf` fixture renders end to end into a 200-by-267
bitmap with 6,048 opaque pixels. The focused metafile suite is 30/30 and both
complete Debug and Release drawing suites are 391/391. Windows differential and
headless GPU fixtures remain follow-up evidence. Supported records lower to
ordinary typed ProGPU scene commands; no metafile-specific opaque payload
crosses the renderer boundary.

`MetafileBenchmarks.ParseAndEnumerate4096RecordFixture` measures owned parsing
and record-table creation. The 2026-08-27 ARM64/.NET 10.0.11
ShortRun measured a 47.510 microsecond median (48.029 microsecond mean, 2.068
microsecond standard deviation) with 224.56 KB allocated for the owned 32 KB
source and 4,098 typed records. `Enumerate4098RecordsWithoutPayloadCopies`
isolates the warmed callback walk. On the same host its ShortRun measured a
1.593 microsecond median (1.495 microsecond mean, 0.177 microsecond standard
deviation) with zero managed allocation. One launch, three warmups, and three
measured iterations make these coarse subsystem evidence.
`Playback256RectanglesToRetainedCommands` measures the bounded parser-table
walk, transform/object state, transactional temporary recording, append, and
cleanup for 256 filled rectangles. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun
measured a 154.013 microsecond median (163.161 microsecond mean, 32.602
microsecond standard deviation) and 305.26 KB managed allocation. This is a
first coarse retained-command baseline, not an allocation target; the next
optimization tranche should reduce temporary command/resource copying without
weakening rollback. CI stores BenchmarkDotNet JSON and
focused tests enforce a maximum 4,096 bytes across sixteen warmed 4,098-record
walks. Parser complexity must remain linear in source bytes plus record count;
no record can trigger an unbounded scan of the complete source.

`Playback256WmfPolygonsToRetainedCommands` measures WMF record decoding,
lowest-free object-table setup, 256 four-point polygon lowers, transactional
append, and cleanup. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a
373.387 microsecond median (345.537 microsecond mean, 87.265 microsecond
standard deviation) and 477.6 KB managed allocation. The three-iteration result
is a deliberately coarse first baseline; like the EMF result, it identifies
temporary point arrays and retained-command ownership as later optimization
work rather than claiming allocation-free playback.

`Playback256WmfRectanglesToRetainedCommands` exercises the shared ordered-box decoder and selected brush/pen lowering through the simpler rectangle path. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 757.639 microsecond median (753.507 microsecond mean, 139.549 microsecond standard deviation) and 622.08 KB managed allocation for 256 filled/stroked rectangles. One launch and three measured iterations make this coarse retained-command evidence; exact selected-fill pixels and the shared malformed-bound/transactional gates remain the correctness proof.

`Playback256WmfRectanglesWithClipState` adds an outer intersect clip, saves the
complete typed WMF state, applies an exclude clip for the first 128 rectangles,
restores relative level -1, and draws the remaining 128 rectangles under only
the outer clip. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a
561.572 microsecond median (599.013 microsecond mean, 103.320 microsecond
standard deviation) and 628.33 KB managed allocation. The three-iteration
result is a coarse clip/save/restore checkpoint. Independent pixels prove that
earlier commands remain visible, the exclude hole is transparent only inside
the saved scope, the outer intersection survives restoration, and a following
unsupported record still rolls back the complete temporary stream. Restoring
an unavailable relative level also fails before publishing commands.

`Playback256WmfEllipsesToRetainedCommands` guards the WMF 16-bit bottom/right/top/left parameter order, selected brush and pen lowering, transactional append, and retained curve commands. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 1.060 millisecond median (1.109 millisecond mean, 0.115 millisecond standard deviation) and 622.14 KB managed allocation for 256 filled and stroked ellipses. One launch and three measured iterations make this a coarse first baseline. Focused gates verify selected fill/outline pixels, reject unordered bounds without publication, and prove that a following unsupported `STRETCHBLT` record does not publish a partially lowered ellipse stream. The complete drawing suite passes 419/419, and ApiCompat remains at 0 missing types, 0 missing members, and 13 reviewed platform-annotation differences.

`Playback256WmfRoundRectanglesToRetainedCommands` guards the official height,
width, bottom, right, top, left `META_ROUNDRECT` payload and typed selected
brush/pen lowering through ProGPU rounded geometry. The 2026-08-31 ARM64/.NET
10.0.11 in-process ShortRun measured a 1.347 millisecond median (1.379
millisecond mean, 0.234 millisecond standard deviation) and 1.05 MB managed
allocation for 256 filled and stroked rounded rectangles. Three iterations make
this a coarse curve-lowering checkpoint and expose allocation as an explicit
optimization target. Exact center, antialiased outline, and transparent-corner
pixels, zero-corner rectangle fallback, and invalid-bound rollback remain the
correctness evidence.

`Playback256WmfPiesToRetainedCommands` and
`Playback256WmfChordsToRetainedCommands` guard the shared official radial2,
radial1, bottom/right/top/left decoder and the distinct center-radial versus
straight-chord closures. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured pies at a 1.382 millisecond median (1.621 millisecond mean, 0.785
millisecond standard deviation) with 816.23 KB allocated, and chords at a
792.480 microsecond median (946.270 microsecond mean, 284.554 microsecond
standard deviation) with 800.03 KB allocated. Three high-variance iterations
make these coarse curve-lowering checkpoints. Independent inside/outside pixels
distinguish both closures, and an invalid chord after a valid pie proves that
the earlier shape is not published transactionally.

`Playback256WmfLinesToRetainedCommands` guards `META_MOVETO`/`META_LINETO`
current-position progression and selected-pen lowering. Its 2026-08-31
ARM64/.NET 10.0.11 in-process ShortRun measured a 503.124 microsecond median
(477.934 microsecond mean, 206.828 microsecond standard deviation) with 323.97
KB allocated for 256 lines. `Playback256WmfSetPixelsToRetainedCommands` guards
explicit `COLORREF` decoding and the transform-to-device, rounded-coordinate,
one-pixel retained rectangle path. It measured a 199.155 microsecond median
(199.350 microsecond mean, 14.387 microsecond standard deviation) with 305.70 KB
allocated for 256 pixels. Three iterations make the line result high-variance
coarse evidence and the pixel result a local subsystem checkpoint. Exact scaled
pixels prove that one logical point becomes one device pixel rather than a
scaled logical rectangle; a later unsupported record rolls back both prior
families, and saved-state coverage proves current-point restoration.

`Playback256WmfPolyPolygonsToRetainedCommands` guards the official unsigned
polygon-count/per-polygon-count layout and two selected-brush/selected-pen
closed figures per record. Its 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured a 2.405 millisecond median (2.542 millisecond mean, 0.463 millisecond
standard deviation) with 1.85 MB allocated for 256 records and 512 polygons.
Three iterations make this coarse retained-command evidence and expose polygon
array/path allocation as an optimization target. Disjoint fill/outline pixels,
unchanged current-position output, invalid per-polygon counts, and rollback
after a later unsupported record remain the authoritative correctness gates.

`Playback256WmfMappedPixelsWithViewportState` guards 256 cycles containing
balanced signed window/viewport origin offsets, y-denominator/y-numerator/
x-denominator/x-numerator window and viewport extent scales, and one transformed
device pixel. Its 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a
155.282 microsecond median (156.556 microsecond mean, 3.099 microsecond standard
deviation) with 305.71 KB allocated. Three iterations make this a local state-
lowering checkpoint. Exact pixels independently cover `MM_ANISOTROPIC`, set,
offset, scale, and SaveDC/RestoreDC composition; a zero scale denominator fails
before any earlier temporary pixel commands publish.

`Playback256WmfPatternCopiesToRetainedCommands` guards exact `PATCOPY`
selected-brush rectangle lowering. Its 2026-08-31 ARM64/.NET 10.0.11 in-process
ShortRun measured a 133.616 microsecond median (135.580 microsecond mean, 16.236
microsecond standard deviation) with 305.88 KB allocated for 256 records. Three
iterations make this a coarse local fill checkpoint. Exact pattern-copy,
`BLACKNESS`, and `WHITENESS` pixels remain the rendering authority. A
destination-dependent `PATINVERT` record was an explicit transactional
boundary at that checkpoint; the later typed ROP3 destination-sampling path
documented below supersedes it for source-bearing bitmap records.

`Playback256WmfPatternCopiesWithOffsetClipState` guards 256 pattern fills, each
surrounded by balanced signed logical clip offsets over one finite Region. Its
2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 4.148 millisecond
median (4.425 millisecond mean, 1.005 millisecond standard deviation) with 2.12
MB allocated. Three high-variance iterations make this coarse state-lowering
evidence and expose Region clone/path repush allocation as an optimization
target. Exact old/moved/restored clip pixels and rollback after a following
unsupported record remain the correctness authority.

`Playback256WmfTextOutToRetainedCommands` guards one selected WMF font plus 256
charset-decoded `TEXTOUT` records lowered through typed measurement, brushes,
and retained glyph commands. The 2026-08-31 ARM64/.NET 10.0.11 in-process
ShortRun measured an 884.902 microsecond median (912.665 microsecond mean,
279.158 microsecond standard deviation) with 562.05 KB allocated. Five measured
iterations make this high-variance coarse evidence. Exact colored glyphs,
measured opaque background pixels, SaveDC/RestoreDC text state, and invalid-
alignment rollback remain the correctness authority. Per-record measurement
and transient brush allocation are explicit optimization targets.

A follow-up playback-state cache reuses the foreground and opaque-background
`SolidBrush` while its canonical color is unchanged; color changes and restored
DC state invalidate by value, and disposal remains scoped to one playback. The
same five-iteration local ShortRun reduced managed allocation from 562.05 KB to
550.25 KB per operation (11.80 KB, 2.1%). Its 1.140 millisecond median and 1.494
millisecond mean were substantially noisier than the initial run, so this is an
allocation result only; it does not establish a throughput improvement.

`Playback256WmfExtTextOutWithClipAndAdvances` guards the official signed Y/X,
length, flags, optional Rect, padded charset bytes, and optional signed `Dx`
layout. Its 256 records each apply an opaque/clipped rectangle and three explicit
character advances. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured a 5.874 millisecond median (5.929 millisecond mean, 0.970 millisecond
standard deviation) with 3.28 MB allocated across five iterations. Exact clip,
background, spaced glyph, current-position, malformed-array, unsupported-option,
and rollback gates are authoritative. The coarse result exposes per-character
shaping, glyph-command fragmentation, and clip-state ownership as explicit
optimization targets rather than concealing them behind an API-only record.

A typed follow-up shapes each extended string once, maps its UTF-16 cluster
origins onto the requested character-cell origins, preserves fallback-font and
mark offsets within each cluster, and records one glyph run per resolved font
rather than one string command per character. A command-level gate proves that
two 20-unit-spaced characters remain one shaped glyph run. The comparable
five-iteration ShortRun improved to a 5.227 millisecond median (5.474
millisecond mean, 0.527 millisecond standard deviation) and 2.66 MB allocated:
0.647 milliseconds (11.0%) lower median and 0.62 MB (18.9%) less allocation
than the initial checkpoint. Repeated layout/caret construction and Region clip
state remain visible optimization targets.

WMF `CREATEFONTINDIRECT` now preserves its underline and strikeout bits in the
selected `System.Drawing.Font`. A retained-command gate proves that one font
carrying both bits records exactly two decoration rectangles with its text;
the ordinary string-format gate independently covers point, clipped rectangle,
and formatted rectangle overloads. The complete drawing suite passes 421/421.

Compatible-mode WMF font escapement now uses a typed font object that owns the
managed `Font` plus its signed tenths-of-a-degree baseline angle. Playback
rotates after the normal WMF logical-to-device transform about the text
reference point, preserving horizontal/vertical alignment, explicit advances,
retained decorations, rectangular `EXTTEXTOUT` clipping, and unrotated explicit
opaque rectangles. A deterministic 90-degree gate proves the retained baseline
points upward, decorated glyph and rectangle transforms match, and
`TA_UPDATECP` moves a following record 24 units along that baseline. A mismatched
orientation fails transactionally because independent glyph orientation needs a
separate typed character-transform path. The complete drawing suite passes
423/423.

`Playback256WmfRotatedExtTextOutWithAdvances` applies 90-degree escapement to
the existing 256-record/three-advance workload. The first implementation cloned
full `GraphicsState` per record and measured 4.05 MB allocation. Restoring only
the exact base transform reduced the comparable five-iteration checkpoint to
2.66 MB, matching the unrotated workload. The optimized 2026-08-31 ARM64/.NET
10.0.11 ShortRun measured a 5.599 millisecond median (5.831 millisecond mean,
0.821 millisecond standard deviation). Timing remains high variance; the
allocation result and command-transform gates are the authoritative evidence.

`META_SETTEXTCHAREXTRA` now participates in the same owned playback-state
snapshot as map mode, alignment, colors, selected objects, and clip state. A
fixed-size record gate proves malformed payload rollback; command gates prove
SaveDC/RestoreDC spacing, one shaped run, right-aligned opaque-background
extent, explicit-`Dx` override, and nearest-device-pixel rounding under
anisotropic mapping. A rotated `TA_UPDATECP` gate
adds character extra to the measured default advance and verifies that the
following text origin moves along the selected font baseline.

`Playback256WmfSpacedRotatedTextOutToRetainedCommands` guards one selected
90-degree WMF font, one character-extra state record, and 256 three-character
`TEXTOUT` records. The paired 2026-08-31 ARM64/.NET 10.0.11 five-iteration
in-process ShortRun measured a 799.768 microsecond median (795.294 microsecond
mean, 24.929 microsecond standard deviation after one outlier) with 800.19 KB
allocated. The unspaced/unrotated retained-text reference measured a 492.332
microsecond median (499.250 microsecond mean, 26.098 microsecond standard
deviation) with 550.16 KB. The difference captures the shaped glyph-position
arrays and rotation work of the new workload; it is not presented as a
like-for-like regression. The complete drawing suite passes 429/429 and
ApiCompat remains 0 missing types, 0 missing members, and 13 reviewed
platform-annotation differences.

`META_SETTEXTJUSTIFICATION` shares the generalized shaped character-cell
spacing seam rather than splitting text into per-character commands. Its fixed
unsigned break-count/total-extra state and running remainder are included in
SaveDC/RestoreDC. Focused gates prove a 5-unit total distributes as 2 then 3
across two space breaks and across two separate text records, while a 4-unit
temporary saved state distributes as 2 then 2. Additional gates cover explicit
`Dx` override, anisotropic rounding of the total before distribution, combined
character-extra/justification under 90-degree `TA_UPDATECP`, and malformed
record rollback.

`Playback256WmfJustifiedRotatedTextOutToRetainedCommands` guards 256 retained
three-character records with both character extra and one justified break. Its
managed allocation is 800.26 KB, compared with 799.95 KB for the paired
character-extra-only workload. Severe host contention made the five-iteration
timing samples span 5.197 to 111.522 milliseconds, invalidating any throughput
comparison; no timing baseline is claimed from that run. The complete drawing
suite passes 433/433 and ApiCompat remains 0 missing types, 0 missing members,
and 13 reviewed platform-annotation differences.

`Playback256EmfExtTextOutWWithAdvances` guards one selected Unicode EMF font and
256 three-character `EMR_EXTTEXTOUTW` records with record-relative UTF-16 and
32-bit advance arrays plus the common ASCII `ETO_IGNORELANGUAGE` flag. The
2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun allocated 1.1 MB per complete
playback (1,152,936 bytes from the diagnostic count). Its three timing samples
spanned 22.188 to 49.840 milliseconds with a 14.505 millisecond standard
deviation, so no throughput baseline is claimed. Exact Unicode glyph identity,
advance origins, colors, current-position updates, justification remainder,
saved state, opaque clipping, malformed offsets, and transactional rollback are
covered by the authoritative 438/438 drawing suite.

`Playback256EmfExtTextOutAWithAdvances` applies the same 256-record retained
workload to one-byte ANSI records, exercising selected-font charset conversion,
arbitrary byte-aligned strings, and 32-bit cell advances. The ARM64/.NET 10.0.11
in-process ShortRun allocated 1.07 MB per playback. Its three timing samples
ranged from 3.510 to 10.228 milliseconds with a 3.833 millisecond standard
deviation, so no latency baseline is claimed. CP1252 non-ASCII conversion,
odd-byte offsets, explicit advances, Shift-JIS decoding without explicit
advances, invalid Shift-JIS input, DBCS-advance rejection, and rollback raise
the authoritative drawing suite to 442/442.

`EMR_POLYTEXTOUTA` and `EMR_POLYTEXTOUTW` now follow the official counted-array
layout: one common graphics-mode header, a bounded contiguous array of 40-byte
[`EmrText`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/dd585d0a-5d7c-4034-963a-1141af836972)
objects, and record-relative string/advance buffers that cannot overlap the
descriptor array. Each entry executes through the same typed font, charset,
alignment, background, clipping, shaping, and explicit-cell path as
`EMR_EXTTEXTOUT`; this matches the specification's recommended series-of-text
operations model for
[`EMR_POLYTEXTOUTA`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/d8b7ac21-76b8-4f9a-a7cc-05f9d2d5627e)
and
[`EMR_POLYTEXTOUTW`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/381015eb-29a7-4774-93ad-a210697f5972).
Focused Unicode and CP1252 gates prove independent anchors, glyph identity,
odd-byte ANSI offsets, and exact advances; a malformed later descriptor proves
whole-stream rollback after an earlier entry would otherwise draw. The complete
drawing suite passes 445/445.

`Playback256EmfPolyTextOutWTwoStringsWithAdvances` guards 256 records, two
three-character descriptors per record, and two independent advance buffers.
The 2026-08-31 ARM64/.NET 10.0.11 in-process run allocated 2.17 MB per complete
512-command playback. Its three timing samples ranged from 117.397 to 519.049
milliseconds with a 215.618 millisecond standard deviation under severe host
contention, so no latency baseline is claimed. The default isolated harness
could not restore its generated project without network access; the recorded
in-process allocation and the deterministic correctness gates are the evidence.

[`EMR_SMALLTEXTOUT`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/20eee81d-0bd4-42d1-a624-860adfe62358)
now uses its official compact typed layout. `ETO_NO_RECT` removes the 16-byte
bounds field, while `ETO_SMALL_CHARS` expands each stored byte directly to a
Unicode code point with a zero high byte; it is intentionally independent of
the selected font's ANSI charset. Records without `ETO_SMALL_CHARS` use strict
UTF-16. Present bounds support opaque/clipped output, and contradictory compact
bounds flags, malformed sizes, glyph-index, numeric/language substitution, and
two-dimensional modes fail transactionally. Unicode identity, low-byte Latin
identity under a Shift-JIS selected font, present-bounds pixels, and rollback
raise the complete drawing suite to 449/449.

`Playback256EmfSmallTextOutSmallChars` guards 256 compact three-character
records. The 2026-08-31 ARM64/.NET 10.0.11 in-process run measured a 754.892
microsecond median (750.068 microsecond mean, 34.800 microsecond standard
deviation) with 516.24 KB allocated. Denied process-priority elevation and
three measured iterations make this a coarse local allocation/command-shape
checkpoint rather than a universal throughput claim.

`ETO_PDY` now consumes the official interleaved horizontal/vertical 32-bit cell
array through a typed two-dimensional drawing seam. Cumulative glyph origins,
transparent background extents, text escapement, and `TA_UPDATECP` all preserve
both components. Out-of-range cells and right-to-left explicit positioning fail
transactionally. Underlined or strikeout PDY text remains a named boundary
until decorations can follow per-cell vertical origins instead of drawing an
incorrect continuous rule. Focused retained-command gates prove a `(20,5)`
second-glyph delta, a `(44,12)` final current-position delta, and malformed-cell
rollback. The complete drawing suite passes 451/451.

`Playback256EmfExtTextOutWPdyAdvances` guards 256 three-character records with
interleaved two-dimensional cells. The 2026-08-31 ARM64/.NET 10.0.11 in-process
run measured a 4.032 millisecond median (4.449 millisecond mean, 0.850
millisecond standard deviation) and 1.08 MB allocated. Denied priority
elevation and three iterations make this coarse local regression evidence.

Unicode `ETO_GLYPH_INDEX` records bypass Unicode decoding, fallback, and
OpenType shaping and write the stored 16-bit glyph IDs directly to the selected
ProGPU font's retained glyph-run command. Scalar and `ETO_PDY` cells share the
typed floating-point position path. When `offDx` is zero, the same path derives
each cell from the selected font's exact glyph advance instead of rounding it
to an integer character width. Alignment, opaque/clipped rectangles,
background mode, escapement, and `TA_UPDATECP` remain owned EMF state. ANSI
glyph-index storage is rejected transactionally because its separately
specified 16-bit storage contract is not implemented. The official
[`ExtTextOutOptions`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/e7ffcc53-40d1-4873-8eda-c5c5ee104aa5)
contract defines glyph input as already positioned, while
[`ExtTextOutW`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-exttextoutw)
specifies that `ETO_RTLREADING` is ignored with `ETO_GLYPH_INDEX`. Playback
therefore retains stored glyph order when either record-level RTL or
`TA_RTLREADING` is present, and accepts `ETO_IGNORELANGUAGE` as the natural
no-language-processing form of the direct path. Decorated two-dimensional
cells remain a named boundary. Horizontal explicit or natural cells now lower
underline and strikeout through selected-font OpenType metrics without Unicode
clusters; PDY decorations reject until per-cell vertical geometry is defined.
Exact selected-font glyph IDs, explicit 20-unit cell placement, a 44-unit
current-position update, natural positive advance, both horizontal decoration
forms, stored-order language suppression, ANSI/PDY-decoration rejection, and
rollback raise the complete drawing suite to 458/458.

`Playback256EmfExtTextOutWGlyphIndices` guards 256 direct three-glyph records.
The 2026-08-31 ARM64/.NET 10.0.11 in-process run allocated 528.25 KB and
measured a 3.818 millisecond median (4.194 millisecond mean, 1.187 millisecond
standard deviation). Timing samples ranged from 3.241 to 5.524 milliseconds,
so this is coarse allocation/command-shape evidence rather than a latency claim.
After horizontal decoration support, the unchanged undecorated workload still
allocates 528.24 KB. Its three-sample rerun measured a 1.069 millisecond median
(1.187 millisecond mean, 0.334 millisecond standard deviation); the short run
confirms allocation shape but does not establish a throughput improvement.

`Playback256EmfExtTextOutWNaturalGlyphIndices` guards the matching direct-glyph
path without an explicit cell array. The 2026-08-31 ARM64/.NET 10.0.11
in-process run measured a 1.650 millisecond mean (0.324 millisecond standard
deviation) and 528.24 KB allocated across three measured iterations. The
short, contended run makes this coarse allocation/command-shape evidence; exact
glyph identity and selected-font natural positioning remain the correctness
authority.

The fixed-layout EMF geometry follow-up decodes `EMR_SETARCDIRECTION`,
`EMR_ARC`, `EMR_PIE`, `EMR_CHORD`, `EMR_ROUNDRECT`, and `EMR_SETPIXELV`
directly into the existing typed `Graphics` path. Arc direction starts at the
documented counterclockwise default, accepts only the official counterclockwise
and clockwise values, and participates in SaveDC/RestoreDC. Arc-family records
share one ellipse-angle decoder while retaining their distinct open, center-
radial, and straight-chord closures. Rounded rectangles reuse the managed
rounded-path primitive and pixels reuse the complete transformed one-device-
pixel path. This follows the Win32
[`SetArcDirection`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-setarcdirection)
contract and the fixed
[`EMRARC`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emrarc)
and
[`EMRROUNDRECT`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emrroundrect)
layouts without native handles or compatibility-shaped objects. Focused raster
and retained-command gates prove both directions across saved state, all three
closures, a transparent rounded corner and filled center, exact `COLORREF`
pixel output, invalid-direction rejection, and whole-stream rollback. The
complete drawing suite passes 462/462.

`Playback256EmfArcFamilyToRetainedCommands` guards 256 alternating open arc,
pie, and chord records after explicit clockwise state. The 2026-08-31
ARM64/.NET 10.0.11 ShortRun measured a 129.7 microsecond mean (8.87 microsecond
standard deviation) and 258.04 KB allocated. Three measured iterations and
denied priority elevation make this coarse local command-shape/allocation
evidence; the focused direction, closure, pixel, and rollback gates remain the
correctness authority.

The current-position and compact-vector follow-up adds the 32- and 16-bit
`EMR_POLYBEZIER`, `EMR_POLYBEZIERTO`, and `EMR_POLYLINETO` forms, both
`EMR_POLYDRAW` forms, all compact polygon/polyline/poly-poly variants,
`EMR_ARCTO`, and `EMR_ANGLEARC`. Point counts, exact payload sizes, cubic
triplets, type arrays, signed compact coordinates, positive radii, and finite
angles are bounded before execution. `PolyBezier` remains independent of the
current position; the `To` forms update it. PolyDraw retains the most recent
MoveTo origin across record boundaries and SaveDC/RestoreDC so
`PT_CLOSEFIGURE` closes to the GDI figure origin rather than the current
record's first point. ArcTo connects to the ellipse intersection and follows
the saved arc direction; AngleArc converts GDI's counterclockwise angle system
to the managed downward-positive coordinate system and updates the logical end
point. These rules follow the official
[`EMRPOLYLINE`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emrpolyline),
[`PolyDraw`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-polydraw),
[`ArcTo`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-arcto),
and
[`AngleArc`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-anglearc)
contracts. Eight focused retained-geometry and rollback gates prove exact
curve/line endpoints, signed 16-bit storage, saved closure origin, arc
orientation, current-position progression, invalid cubic groups, invalid type
arrays, and zero-radius rejection. The complete drawing suite passes 470/470.

`Playback256EmfPolyDraw16ToRetainedCommands` guards 256 compact records, each
containing a MoveTo and cubic Bézier triplet. The 2026-08-31 ARM64/.NET 10.0.11
ShortRun measured a 230.0 microsecond median (239.1 microsecond mean, 31.53
microsecond standard deviation) and 483.46 KB allocated. The three measured
iterations and denied priority elevation make this coarse local
command-shape/allocation evidence; the focused state, geometry, and malformed-
input gates remain authoritative.

The EMF clip-state follow-up adds `EMR_OFFSETCLIPRGN` and
`EMR_EXCLUDECLIPRECT` through the same typed `Graphics` clip state already used
by the WMF player. The records require exact `POINTL` and `RECTL` payloads;
unordered rectangles and truncated offsets fail before publication. Clip
offsets and exclusions participate in the active map/world transform and the
existing `GraphicsState` SaveDC/RestoreDC snapshot. These rules follow the
official
[`EMROFFSETCLIPRGN`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emroffsetcliprgn)
and
[`EMREXCLUDECLIPRECT`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-emrexcludecliprect)
structures. Focused raster evidence covers the initial intersection, moved
clip, excluded hole, and restored outer clip; a malformed offset after earlier
geometry proves whole-stream rollback. The complete drawing suite passes
472/472.

`Playback256EmfOffsetExcludeClipSequences` guards 256 saved clip scopes, each
with an offset, exclusion, retained rectangle, and relative restore. The
2026-08-31 ARM64/.NET 10.0.11 ShortRun measured a 5.499 millisecond median
(5.566 millisecond mean, 1.306 millisecond standard deviation) and 2.41 MB
allocated. Three measured iterations and denied priority elevation make this
coarse state-heavy command-shape evidence; the focused raster and rollback
gates remain the correctness authority.

The EMF and WMF DIB follow-ups decode ordinary embedded device-independent images
without a native HDC, runtime reflection, or a compatibility bitmap wrapper.
`EMR_STRETCHDIBITS` supports typed source crop, source/destination sign
mirroring, transformed destination parallelograms, the destination-independent
`SRCCOPY`, `NOTSRCCOPY`, `BLACKNESS`, `WHITENESS`, and selected-brush `PATCOPY`
operations, and saved
BLACKONWHITE/WHITEONBLACK/COLORONCOLOR/HALFTONE sampling state.
`EMR_SETDIBITSTODEVICE` materializes only the supplied scan band and places its
intersection at the corresponding full-image destination. Twelve focused cases
cover bottom-up padding, top-down rows, clipped source adjustment, crop,
mirroring, transforms, all six BI_RGB bit depths, both scan orientations,
saved stretch state, malformed offsets/sizes/scan ranges, unsupported raster
destination-dependent operations, transactional rollback, and warmed allocation.
The WMF follow-up
adds all four source-bearing packed-DIB layouts, exact packed header/color-table
splitting, direct-color optimization tables, both scan orientations, retained
command shape, and explicit rollback for the two source-required playback-DC
forms. Source-required playback-device-context pixels remain a named typed
boundary. ApiCompat remains at zero missing types, zero missing members, and 13
reviewed shape differences.

The `BI_BITFIELDS` follow-up validates three external masks after a 40-byte
header or the embedded V4/V5 masks before decoding any pixels. Red, green, and
blue masks must be nonzero, contiguous, within the declared 16/32-bit pixel,
and mutually disjoint; an optional embedded alpha mask obeys the same rules.
Arbitrary channel widths scale to eight bits with rounded integer math. Exact
packed-buffer splitting includes external masks and any direct-color
optimization table, preventing either from being consumed as pixel rows. Seven
focused cases cover RGB565 through all three accepted header sizes, custom
32-bit channel order and alpha, packed WMF optimization tables, malformed masks
with complete EMF/WMF rollback, and warmed allocation.

The `BI_RLE8`/`BI_RLE4` follow-up uses one bounded decoder for the EMF and WMF
DIB families. RLE is restricted to the official bottom-up 8-bit and 4-bit
indexed combinations. Encoded runs, alternating RLE4 nibbles, absolute runs,
word padding, end-of-line, end-of-bitmap, and right/up deltas are consumed
without reading beyond `biSizeImage`; an end marker is mandatory and trailing
bytes are rejected. Cursor motion and every run remain inside the supplied row
band, palette indexes are checked before RGBA materialization, and skipped
pixels retain palette index zero. Six focused cases cover RLE8 and RLE4 encoded
and absolute modes, delta/default pixels, EMF and WMF partial scan bands, a
malformed-input matrix with transactional rollback in both formats, and warmed
allocation.

The `BI_JPEG`/`BI_PNG` follow-up decodes complete embedded file buffers through
the existing managed bitmap codec path after the metafile layer validates the
official compression/header combination. The decoder requires bit count zero,
positive dimensions, no color table, exact nonzero `biSizeImage`, a matching
JPEG or PNG signature, and codec dimensions equal to the declared DIB before
allocating the pixel bitmap. WMF's optional final word-alignment byte is kept
outside the declared encoded buffer. `SetDIBitsToDevice` decodes that complete
file once per record, then applies `StartScan`/`cScans` to the decoded bitmap so
only the selected destination rows publish. Focused cases cover exact PNG
pixels, crop/mirroring, lossy JPEG color bounds, odd-sized WMF buffers, complete
and partial EMF/WMF set-DIB records for both encoded formats, malformed
header/buffer/codec and out-of-range scan rollback in both formats, and warmed
allocation.

The logical-palette follow-up implements the complete core palette record
families used by indexed DIB playback: EMF create/select/set/resize/realize and
WMF create/select/set/resize/realize/animate. Object kinds and indexes, palette
version and entry counts, mutation ranges, WMF flags, selected-object lifetime,
and saved selections are validated before retained commands publish.
`DIB_PAL_COLORS` consumes 16-bit color-table indexes into the selected logical
palette; `DIB_PAL_INDICES` omits the color table and resolves packed/RLE pixel
indexes directly. Six focused gates cover exact EMF and WMF pixels, palette
selection restoration, set/resize/animation semantics, malformed transactional
rollback, and warmed allocation.

The CMYK follow-up completes the remaining official DIB compression values.
`BI_CMYK` requires 32 bits per pixel and consumes C/M/Y/K bytes through the
same multiplicative device-independent conversion already used by ProGPU's
typed `Cmyk32` pixel path. `BI_CMYKRLE8` and `BI_CMYKRLE4` require bottom-up
8-bit and 4-bit indexed images, respectively, and reuse the exact-size bounded
RLE state machine and RGBQUAD/logical-palette resolution. Six focused metafile
gates cover top-down and bottom-up direct pixels, mixed black/colorant math,
both CMYK RLE forms, invalid depth/orientation/size rollback, and warmed
allocation.

The destination-independent raster-operation follow-up accepts the official
common `BLACKNESS`, `WHITENESS`, `NOTSRCCOPY`, and `PATCOPY` values in every
source-bearing EMF/WMF DIB family. Solid black/white and selected-brush pattern
copies fill the same clipped, mirrored, transformed destination parallelogram
without pretending to sample destination pixels. `NOTSRCCOPY` bitwise-inverts
straight RGB channels while preserving alpha ownership; premultiplied inputs
are unpremultiplied and repremultiplied around the inversion. Destination-
dependent AND/OR/XOR/merge operations still reject transactionally until a
typed destination-read composition seam exists. Three focused gates cover
exact channel inversion, black/white/pattern pixels, and warmed allocation;
the existing malformed-stream matrix guards rollback for `SRCINVERT`.

The source-omitted WMF bitmap-record follow-up covers the official no-source
layouts of `META_BITBLT`, `META_STRETCHBLT`, `META_DIBBITBLT`, and
`META_DIBSTRETCHBLT`. `BLACKNESS`, `WHITENESS`, and selected-brush `PATCOPY`
render without inventing source pixels, including the reserved-word field shift
in the DIB forms. Although the specification describes the omitted source as
the current playback-device-context region, its
[`META_BITBLT` without-bitmap contract](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/e6dd5312-c622-4fb9-9945-22297cee3ad4)
requires processing to fail when the ROP actually depends on source pixels;
the portable renderer therefore does not invent a destination snapshot or a
platform DC source. Source-bearing `META_BITBLT` and `META_STRETCHBLT` records
validate the complete `Bitmap16` envelope—dimensions, word-aligned stride,
planes, bit depth, and exact payload length—even when the raster operation does
not consume its pixels. Five focused gates cover exact output from all four
record families, valid and malformed `Bitmap16` envelopes, transactional
rejection of source-required operations, rollback of earlier commands, and a
warmed allocation ceiling.

The typed `Bitmap16` adapter follow-up closes that device-dependent source
boundary without assuming a pixel layout that the WMF contract does not
define. `WmfBitmap16DecodeServices` gives one registered
`IWmfBitmap16DecodeService` the validated signed type, dimensions,
word-aligned stride, planes, bit depth, and exact raw bit span. The adapter must
synchronously publish exactly one top-down, straight-alpha RGBA8 snapshot to a
single-use destination; missing, short, duplicate, late, or retained output
fails explicitly. `META_BITBLT` and `META_STRETCHBLT` then reuse the same typed
crop, mirror, stretch-mode, transform, and ROP3 path as DIB playback. Four
focused gates cover exact pixels and metadata for both record families, exact
`SRCINVERT` composition, registration/output-contract failures with whole-
stream rollback, and warmed allocation. The platform-specific device-format
interpretation is owned by the registered adapter, not inferred by the
portable renderer.

`Playback256EmfDibImagesToRetainedCommands` measures bounded header/row decode,
256 owned two-by-two RGBA snapshots, typed retained-texture recording,
transactional append, and cleanup. The 2026-08-31 ARM64/.NET 10.0.11 ShortRun
measured a 69.391 millisecond median (64.169 millisecond mean, 18.101
millisecond standard deviation) with 501.73 KB allocated. Three measured
iterations, denied priority elevation, and visible timing variance make this a
coarse allocation/command-ownership baseline. The focused 64-image gate remains
the deterministic bound; per-record bitmap/texture construction is the next
explicit optimization target.

`Playback256WmfDibImagesToRetainedCommands` measures the same bounded decode
and retained ownership path from 256 packed `META_STRETCHDIB` records. The
2026-08-31 ARM64/.NET 10.0.11 ShortRun measured an 18.268 millisecond median
(18.582 millisecond mean, 9.289 millisecond standard deviation) with 501.73 KB
allocated. Three measured iterations, denied priority elevation, and high
timing variance make allocation and command ownership authoritative rather than
throughput. Ten focused WMF cases independently cover all four record families,
bottom-up padding, top-down and bottom-up partial bands, packed color-table
splitting, retained sampling, playback-DC boundaries, malformed input,
transactional rollback, and the warmed 64-image allocation ceiling.
Both complete Debug and Release drawing suites pass 569/569; ApiCompat remains
at zero missing types, zero missing members, and 13 reviewed shape differences.

`Playback256BitFieldDibImagesToRetainedCommands` measures 256 packed RGB565
`META_STRETCHDIB` records including external-mask parsing. The 2026-08-31
ARM64/.NET 10.0.11 ShortRun measured a 17.411 millisecond median (16.561
millisecond mean, 9.096 millisecond standard deviation) with 501.79 KB
allocated. Three iterations, denied priority elevation, and high timing
variance make allocation and retained ownership authoritative rather than a
throughput comparison with the BI_RGB fixture.

`Playback256RleDibImagesToRetainedCommands` measures 256 packed two-by-two
`BI_RLE8` `META_STRETCHDIB` records through the bounded state machine and the
same retained ownership path. The 2026-08-31 ARM64/.NET 10.0.11 ShortRun
measured a 30.944 millisecond median (25.515 millisecond mean, 16.116
millisecond standard deviation) with 509.73 KB allocated. Three iterations and
high timing variance make allocation and command ownership authoritative rather
than throughput.

`Playback256EncodedDibImagesToRetainedCommands` measures 256 packed two-by-two
`BI_PNG` `META_STRETCHDIB` records, including signature/size/dimension checks,
managed codec decode, retained ownership, and cleanup. The 2026-08-31
ARM64/.NET 10.0.11 ShortRun measured a 19.527 millisecond median (18.959
millisecond mean, 3.838 millisecond standard deviation) with 743.78 KB
allocated. Three iterations and timing variance make allocation and command
ownership authoritative rather than throughput.

`Playback256EncodedDibScanBandsToRetainedCommands` measures 256 packed
two-by-two `BI_PNG` `META_SETDIBTODEV` records that alternate the selected scan
row. The 2026-09-01 ARM64/.NET 10.0.11 ShortRun measured a 21.815 millisecond
median (28.512 millisecond mean, 15.321 millisecond standard deviation) with
743.85 KB allocated. Three measured iterations, denied priority elevation, and
high timing variance make the exact-pixel, transactional, and bounded warmed
allocation tests authoritative rather than this local throughput sample.

`Playback256LogicalPaletteDibImagesToRetainedCommands` measures 256 packed
two-by-two `DIB_PAL_INDICES` images after one typed WMF palette creation and
selection. It guards palette lookup, indexed RGBA materialization, retained
ownership, command cleanup, and the same transactional publication boundary.
The 2026-08-31 ARM64/.NET 10.0.11 ShortRun measured a 15.186 millisecond
median (19.553 millisecond mean, 8.962 millisecond standard deviation) with
502.08 KB allocated. Three iterations, denied priority elevation, and high
timing variance make allocation and command ownership authoritative rather than
throughput.

`Playback256CmykDibImagesToRetainedCommands` measures 256 packed two-by-two
32-bit `BI_CMYK` images through channel conversion, row orientation, retained
ownership, and cleanup. The 2026-08-31 ARM64/.NET 10.0.11 ShortRun measured a
41.660 millisecond median (40.847 millisecond mean, 19.178 millisecond standard
deviation) with 501.71 KB allocated. Three iterations, denied priority
elevation, and high timing variance make allocation and command ownership
authoritative rather than throughput.

`Playback256NotSourceCopyDibImagesToRetainedCommands` measures 256 packed
two-by-two DIBs through bitwise RGB inversion, alpha-mode preservation,
retained ownership, and cleanup. The 2026-09-01 ARM64/.NET 10.0.11 in-process
ShortRun measured a 51.551 millisecond median (61.174 millisecond mean, 23.446
millisecond standard deviation) with 605.85 KB allocated. Three iterations,
denied priority elevation, and high timing variance make allocation and exact
focused pixels authoritative rather than throughput.

`Playback256WmfSourceIndependentBitmapRecordsToRetainedCommands` measures 256
source-omitted `META_BITBLT` records using selected-brush `PATCOPY`. The
2026-09-01 ARM64/.NET 10.0.11 in-process ShortRun measured an 810.465
microsecond median (859.788 microsecond mean, 104.814 microsecond standard
deviation) with 464.05 KB allocated. Three iterations and denied priority
elevation make the focused exact-pixel and warmed-allocation gates
authoritative; this benchmark is a coarse retained-command baseline.

`Playback256WmfBitmap16AdapterRecordsToRetainedCommands` measures 256 embedded
8-by-8 `META_BITBLT` sources through the registered typed adapter, synchronous
owned-pixel transfer, crop, and retained image recording. The 2026-09-01
ARM64/.NET 10.0.11 in-process ShortRun measured a 23.843 millisecond median
(27.789 millisecond mean, 9.288 millisecond standard deviation) with 569.88 KB
allocated. Three iterations, denied priority elevation, and visible timing
variance make the exact-pixel, provider-contract, rollback, and deterministic
allocation gates authoritative.

The destination-dependent ROP3 follow-up adds one typed
`GpuRasterOperation` value to retained texture draws without growing the hot
`RenderCommand` union. The operation carries the official eight-bit ternary
truth table and one normalized solid-pattern color through retained pictures
and draw-call batching. Offscreen composition renders only the clipped physical
source bounds, samples the current destination through the existing full-size
ping-pong texture, quantizes straight source/pattern/destination RGB to exact
eight-bit device values, evaluates all 256 Boolean truth tables, and writes an
opaque GDI device pixel. Source alpha is excluded from the truth table while
geometry and mask coverage remain explicit. The ordinary advanced-blend shader
path remains unchanged.

Direct presentation detects a compiled ROP draw before scene uploads, renders
the ordered frame into one reusable bindable texture sized to the physical host
viewport, and uses the typed GPU blitter to place it into the raw swapchain view.
Offset and HiDPI viewports remain explicit through `GpuTextureBlitViewport`; the
rest of the attachment retains the compositor clear color. There is no CPU
readback or approximate fixed blend. The presentation texture is reported in
intermediate-memory metrics, resized exactly, and retired after 240 idle frames.

WMF/EMF DIB and typed `Bitmap16` source-bearing records now accept every valid
ROP3 truth-table byte. Existing `SRCCOPY`, `NOTSRCCOPY`, and source-independent
black/white/pattern fast paths remain intact; other operations use the typed GPU
path. Pattern-dependent operations require the player’s selected solid brush;
non-solid pattern materialization remains an explicit future boundary. Exact
focused output covers `SRCINVERT` (`S XOR D`) and `PATINVERT` (`P XOR D`),
unchanged outside pixels, alpha-independent source RGB, retained-command round
trips, clipped source-memory sizing, malformed-envelope rollback, and a warmed
64-record allocation ceiling. Both complete Debug and Release drawing suites
pass 574/574. ApiCompat remains zero missing types, zero missing members, and
13 reviewed shape differences.

`Playback256DestinationDependentDibImagesToRetainedCommands` measures 256
packed two-by-two `SRCINVERT` records through decode, exact ROP payload
publication, retained image ownership, and cleanup. The 2026-09-01
ARM64/.NET 10.0.11 ShortRun measured a 21.579 millisecond median (20.781
millisecond mean, 1.680 millisecond standard deviation) with 501.8 KB
allocated. Three iterations and denied priority elevation make exact pixel,
rollback, retained-payload, and allocation gates authoritative rather than this
coarse throughput sample. The truth-table definition is pinned to the official
[`TernaryRasterOperation enumeration`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/1605dd68-a635-4639-ab81-99ff3e3fc5a3).

The source-dependency follow-up classifies each ROP3 byte from the official
truth table before source clipping. Every operation whose result is invariant
when `S` changes can therefore execute across source-bearing and source-omitted
`META_BITBLT`, `META_STRETCHBLT`, `META_DIBBITBLT`, and
`META_DIBSTRETCHBLT` records. Existing `BLACKNESS`, `WHITENESS`, and `PATCOPY`
fills remain fast paths. Other functions of pattern and destination, including
`DSTINVERT` and solid-brush `PATINVERT`, use one owned one-pixel coverage bitmap
per playback and the typed destination-sampling command; it is a real bitmap
with explicit lifetime rather than a fabricated WinForms-shaped object.
Source-dependent records without a bitmap remain an explicit transactional
failure. Exact tests cover all four no-source layouts, source-bearing DIBs with
irrelevant out-of-range source coordinates, `Bitmap16` `SRCINVERT`, outside
pixels, retained payloads, rollback, and warmed allocation.

`Playback256WmfDestinationOnlyBitmapRecordsToRetainedCommands` measures 256
source-omitted `META_BITBLT` `DSTINVERT` records through source-dependency
classification, shared coverage-texture retention, exact ROP publication, and
cleanup. The 2026-09-01 ARM64/.NET 10.0.11 ShortRun measured a 454.471
microsecond median (450.983 microsecond mean, 216.582 microsecond standard
deviation) with 296.73 KB allocated. Three iterations, denied priority
elevation, and high variance make exact focused pixels, rollback, and the
allocation gate authoritative rather than this coarse throughput sample.

The typed hatch-brush foundation adds `BS_HATCHED` handling to
`EMR_CREATEBRUSHINDIRECT` and `META_CREATEBRUSHINDIRECT`. The immutable object
stores one of the six official hatch orientations plus the foreground
`COLORREF`; draw-time resolution supplies transparent or current-background-
color pixels from playback state rather than incorrectly freezing DC state at
object creation. EMF `EMR_SETBRUSHORGEX` maps directly to the managed rendering
origin, and Graphics Save/Restore retains that origin together with the
selected object and background state. The existing ProGPU `TilePatternBrush`
then handles ordinary vector fills and source-independent `PATCOPY` without a
native brush handle. Invalid hatch values reject before object publication and
roll back prior retained commands. The foundation did not approximate
destination-reading ROP3 operations with the foreground color; the following
checkpoint supplies the required typed shader payload.

`Playback256WmfHatchPatternCopiesToRetainedCommands` measures 256 horizontal
hatch `PATCOPY` records through object selection, current background
materialization, tile-pattern retained commands, and cleanup. The 2026-09-01
ARM64/.NET 10.0.11 ShortRun measured a 161.322 microsecond median (173.049
microsecond mean, 32.199 microsecond standard deviation) with 313.9 KB
allocated. Three iterations, denied priority elevation, and timing variance
make the exact foreground/background/origin pixels, saved-state restoration,
rollback, and command-shape gates authoritative. Both complete Debug and
Release drawing suites pass 577/577; ApiCompat remains zero missing types and
zero missing members. The record and state semantics are pinned to the
official [`META_CREATEBRUSHINDIRECT`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/8331e35d-0f97-4ec3-b3b0-cfb3281c0642)
and [`LOGBRUSH`](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-logbrush)
contracts.

The destination-reading hatch ROP3 checkpoint reuses the same immutable
`TilePatternBrush` as the vector path. `GpuRasterOperation` transports that
typed payload through the existing `RenderCommand.DataParam` union slot, so the
hot retained command stays within its 576-byte size gate. Retained pictures
preserve the complete 64-bit mask, foreground/background colors, and rendering
origin; semantic equality keeps equivalent texture draws batchable. The
advanced-composition uniform is a bounded 96-byte value carrying two mask
words, colors, origin, and transparent-background state. Its shader evaluates
the repeating 8-by-8 tile in physical device coordinates, selects foreground
or current DC background before the existing exact bytewise ternary truth
table, and returns the destination unchanged for a transparent hatch hole.
`META_PATBLT` uses the same truth-table source-dependency classifier as bitmap
records, accepting `PATINVERT`, `DSTINVERT`, and every other source-independent
function while rejecting a function that requires an unavailable source.

Exact compositor tests cover a true source/pattern/destination XOR, opaque and
transparent tile backgrounds, nonzero origin phase, uniform ABI offsets, and
retained-picture round trips. Metafile tests cover exact opaque/transparent
horizontal-hatch `PATINVERT`, unchanged exterior pixels, and one cached typed
pattern object shared by 64 commands in a playback. Both complete Debug and
Release drawing suites pass 579/579; ApiCompat remains zero missing types, zero
missing members, and 13 reviewed shape diagnostics.

`Playback256WmfHatchPatternInvertsToRetainedCommands` measures 256 horizontal
hatch `PATINVERT` records through selected-object resolution, one cached typed
pattern payload, coverage-texture retention, exact destination sampling, and
cleanup. The 2026-09-01 ARM64/.NET 10.0.11 ShortRun measured a 358.096
microsecond median (359.472 microsecond mean, 19.634 microsecond standard
deviation) with 296.98 KB allocated. Three iterations and denied priority
elevation make the exact device/metafile pixels, retained payload, command-size,
and allocation gates authoritative.

The EMF path-bracket follow-up adds `EMR_BEGINPATH`, `EMR_ENDPATH`,
`EMR_CLOSEFIGURE`, `EMR_ABORTPATH`, `EMR_FILLPATH`, `EMR_STROKEPATH`,
`EMR_STROKEANDFILLPATH`, `EMR_FLATTENPATH`, `EMR_WIDENPATH`,
`EMR_SELECTCLIPPATH`, and `EMR_SETMITERLIMIT`. Vector calls inside a bracket
append typed `GraphicsPath` geometry instead of publishing drawing commands.
Each call's active map/world transform is applied at capture time, so the
selected path owns device-coordinate points; a `MoveTo` remains at its
original device position even if the transform changes before the connecting
record. `BeginPath` discards a previously selected path, `EndPath` selects the
completed path, consuming fill/stroke/clip operations remove that selected
path, and `AbortPath` clears either lifecycle state. Fill and clip close open
figures and use the active alternate/winding mode. Clip selection maps the five
official RGN combine values to the existing typed Region clip path.
`FlattenPath` and `WidenPath` reuse managed path geometry; widening requires a
supported selected pen wider than one device unit and applies the saved DC
miter limit. The exact 16-byte bounds metadata on fill/stroke records and exact
scalar/mode payloads are validated before execution. These rules follow the
official [path creation](https://learn.microsoft.com/en-us/windows/win32/gdi/path-creation),
[device-coordinate path storage](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getpath),
[outlined and filled path](https://learn.microsoft.com/en-us/windows/win32/gdi/outlined-and-filled-paths),
[WidenPath](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-widenpath),
and [clip path](https://learn.microsoft.com/en-us/windows/win32/gdi/clip-paths)
contracts. Text/glyph outline capture inside a bracket remains an explicit typed
boundary; it fails transactionally instead of emitting ordinary text outside
the path. Twenty-five focused cases cover lifecycle, raster output, every
supported vector-record family, device-coordinate transform changes, widening,
clip selection, abort, text-boundary enforcement, retained-command suppression,
and rollback. The complete drawing suite passes 497/497.

`Playback256EmfPathBracketsToRetainedCommands` guards 256 independent
Begin/rectangle/End/StrokeAndFill sequences. The 2026-08-31 ARM64/.NET 10.0.11
ShortRun measured a 1.520 millisecond median (1.477 millisecond mean, 0.381
millisecond standard deviation) and 713.5 KB allocated. Three measured
iterations, denied priority elevation, and timing variance make this coarse
transactional path-lowering evidence; the focused lifecycle, raster,
device-coordinate, retained-command, and rollback gates remain authoritative.

`RecordAndFinalize256PortableComments` measures the complete portable writer:
256 owned 64-byte comment copies, EMF+/EMF assembly, validation, and publication
to a pre-sized memory stream. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun
measured an 11.346 microsecond median (11.348 microsecond mean, 0.406
microsecond standard deviation) with 150.72 KB allocated for the complete 19 KB
document, output stream, and immutable parser tables. The encoder is linear in
comment bytes plus record count. Focused tests additionally reparse emitted
bytes, compare exact copied payloads after caller mutation, exercise a
non-seekable target and zero-length comment, and prove that unsupported drawing
aborts without partial output.

### Typed DIB pattern-brush materialization

The official
[`EMR_CREATEDIBPATTERNBRUSHPT`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/332116b4-c6f9-4b18-a7cc-22c531b52afc)
and
[`META_DIBCREATEPATTERNBRUSH`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/aeab62b8-03ab-48c0-8176-09c392f3c9da)
records enter the same
bounded DIB header, palette, compression, and row decoder used by image records.
The EMF path validates the four official offset/size fields and rejects
overlapping buffers. The WMF path validates its style/color-usage prefix and
packed DIB envelope. A successfully decoded bitmap is owned by a typed playback
pattern object and is not published into the object table until decoding has
completed, preserving whole-stream rollback.

Selection resolves that object to the managed `TextureBrush` path. Resolution
is cached by selected object and `RenderingOrigin`; a brush-origin change creates
one new typed texture transform and disposes the preceding cached brush. Ordinary
vector fills and `PATCOPY` therefore tile exact decoded pixels without an HDC,
native brush handle, runtime reflection, private-field scan, or compatibility
object. The selected object and every SaveDC snapshot retain the owning pattern
object, so delete and disposal rules remain the same as other GDI objects.

The deprecated
[`META_CREATEPATTERNBRUSH`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/6f1c6ba3-7710-42c1-b6e2-1b776c2f769b)
form validates its partial
[`Bitmap16`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wmf/dc487315-3bb9-40c8-9f49-55ffc6152d8c),
four-byte ignored `Bits` field, 18-byte reserved area, and exact trailing pattern
length before invoking the registered typed device-format decoder. The decoder
is not called for malformed envelopes and object publication remains
transactional. The
[`EMR_CREATEMONOBRUSH`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/49b42277-31b0-4eb9-a6af-86d9be9b568f)
path reuses the offset-checked EMF packed-DIB envelope but requires an actual
one-bit DIB before materialization.

Destination-reading ROP3 now carries an arbitrary bitmap pattern as
`GpuRasterTexturePattern`: one immutable, texture-bindable ProGPU resource plus
its finite device-space origin. `RenderCommand` reuses its existing object-union
slot, so the hot command layout does not grow. The destination-composition bind
group adds a typed pattern texture and retains the existing 96-byte uniform by
using its final eight bytes for the pattern extent. The WGSL path uses signed
repeating modulo and exact `textureLoad`, then feeds the resulting RGB bytes into
the existing ternary truth-table evaluator. Playback caches one typed pattern
payload per selected `TextureBrush`; 64 `PATINVERT` commands therefore share the
same object and texture lease rather than cloning pixels or commands.
The same exact path now reaches raw presentation views through the bounded
bindable presentation texture, so metafile ROP output is not limited to tests or
offscreen consumers.

`Playback256WmfDibPatternInvertsToRetainedCommands` measures 256 packed
two-by-two DIB-pattern `PATINVERT` records through bounded decode, one cached
typed texture payload, retained ROP publication, and cleanup. The 2026-09-01
ARM64/.NET 10.0.11 ShortRun measured a 579.442 microsecond median (482.158
microsecond mean, 172.568 microsecond standard deviation) and 305,349 bytes
allocated. All 102 System.Drawing benchmarks completed on the same Release
run. Three measured iterations, denied priority elevation, and high variance
make the exact device/metafile pixels, resource-sharing assertions, command-size
gate, and allocation bounds authoritative rather than this local timing sample.
The complete System.Drawing suite passes 600/600 in Debug and Release;
ApiCompat remains at zero missing types, zero missing members, and 13 reviewed
shape diagnostics.

### EMF source-DC bitmap transfers

`EMR_BITBLT` and `EMR_STRETCHBLT` now reuse the same bounded DIB decoder,
logical-palette resolution, source clipping, mirroring, sampling, and typed
ROP3 compositor as the established DIB record families. Both official
record-relative bitmap-info and bitmap-bits ranges must be present,
nonoverlapping, in bounds, and structurally valid before source-dependent
output can publish. A source-independent ROP3 is resolved before those ranges
are read, matching the official omitted-source record shape without inventing
a device-context bitmap.

The source `XFORM` supports finite invertible axis scale, translation, and
mirroring. Rotation and shear fail at an explicit named boundary because native
BitBlt/StretchBlt do not accept those source transformations. BitBlt derives its
source extent from the destination extent; StretchBlt consumes its explicit
source extent. Fractional transformed source rectangles remain fractional
through clipping and retained texture sampling instead of being rounded early.
The embedded DIB palette remains the color authority; source-DC background-color
conversion beyond that DIB description is still a documented fidelity boundary.

Six focused cases cover both record types, exact crop/stretch pixels,
scale/translation/mirroring, source-independent pattern copy with omitted
buffers, overlapping/missing buffers, transform rejection, whole-playback
rollback, and a warmed 64-record allocation window. The 2026-09-01 ARM64/.NET
10.0.11 in-process ShortRun for
`Playback256EmfBitmapBltsToRetainedCommands` measured a 6.037 ms median
(6.721 ms mean, 3.214 ms standard deviation) and 501.77 KB allocated. The
three measured iterations, denied priority elevation, minimum-iteration warning,
and 47.8% relative standard deviation make this allocation and command-shape
evidence only, not a throughput claim. The isolated BenchmarkDotNet toolchain
also hit its 120-second generated-build timeout before measurement on this host;
the successful in-process raw result is retained under
`artifacts/performance/system-drawing-emf-bitmap-blt-inprocess-20260901`.

### EMF alpha and transparent bitmap transfers

[`EMR_ALPHABLEND`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/34e07d4f-aee6-4b63-a4bb-96996ad47669)
and
[`EMR_TRANSPARENTBLT`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/aa216051-2dc0-4317-b343-525431cfa103)
now share a typed, bounded image-transfer envelope. Both records validate their
official fixed fields, disjoint record-relative bitmap ranges, complete source
rectangle, and finite invertible axis-only source transform before publishing
retained commands. Zero-sized or mirrored rectangles and source rotation/shear
fail explicitly, matching the native AlphaBlend and TransparentBlt restrictions.

AlphaBlend implements `AC_SRC_OVER`, global `SourceConstantAlpha`, and optional
`AC_SRC_ALPHA`. The per-pixel path accepts 32-bit `BI_RGB` premultiplied BGRA,
rejects invalid RGB-greater-than-alpha pixels, converts once to the renderer's
straight-alpha bitmap contract, and combines that alpha with the global value
through typed `ImageAttributes`. The reserved blend-flags byte is deliberately
ignored as required by the record contract. Opaque indexed and direct-color DIBs
reuse the existing decoder. Adjusted-alpha JPEG/PNG transport remains an explicit
directly-addressable-DIB boundary.

TransparentBlt uses an exact typed color key for non-32-bit sources. The 32-bit
record means destination alpha-channel composition rather than ordinary color-key
transparency, so it fails at a named typed destination-alpha seam instead of
silently substituting the wrong operation. The record's source-DC background
color remains palette/conversion metadata outside the embedded DIB authority.

Seven focused cases cover zero/full/global/combined alpha, premultiplied-pixel
validation, color-key preservation, source translation, full-source bounds,
overlapping buffers, 32-bit transparency rejection, transform rejection,
whole-stream rollback, and a warmed 64-record allocation window. The 2026-09-01
ARM64/.NET 10.0.11 in-process ShortRun for
`Playback256EmfImageBlendsToRetainedCommands` measured an 8.178 ms median
(8.612 ms mean, 1.250 ms standard deviation) and 1.1 MB allocated for 256
alternating records. Three iterations, denied priority elevation, and the wide
confidence interval make this local command-playback/allocation evidence rather
than a throughput claim; the focused allocation and exact-pixel tests are the
regression authority. Raw ignored artifacts are under
`artifacts/performance/system-drawing-emf-image-blend-inprocess-20260901`.

### EMF vertex-color gradient fills

[`EMR_GRADIENTFILL`](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/1a3849c8-be6c-4d30-b5d3-f43b4c70ca0d)
now parses its exact Bounds/count/mode envelope, 16-byte `TRIVERTEX` array, and
12-byte rectangle or triangle mesh entries before publishing one typed ProGPU
`VertexMesh2D`. Rectangle-horizontal and rectangle-vertical modes expand to two
triangles with the required constant-color edges; triangle mode preserves all
three vertex colors for barycentric GPU interpolation. DWORD source indices are
validated before output allocation, so the retained mesh is not limited to a
16-bit index domain. Rectangle padding is ignored, lower-right coordinates are
treated as exclusive geometry, the documented high byte of each 16-bit color
channel is used, and `TRIVERTEX.Alpha` is ignored so GradientFill output remains
opaque as required by GDI.

The mesh carries the active mapping/world transform and uses the existing typed
vertex-color blend contract without an HDC, runtime reflection, or a CPU gradient
raster. Malformed sizes/counts/modes/indices and unordered rectangle vertices
fail before publication; drawing inside a path bracket remains an explicit
transactional boundary. Five focused gates cover retained geometry and colors,
world transforms, nonzero padding and ignored alpha, horizontal/vertical/triangle
pixel interpolation, empty meshes, path capture, malformed-record rollback, and
a warmed 64-record allocation ceiling of 512 KB.

The 2026-09-01 ARM64/.NET 10.0.11 in-process ShortRun for
`Playback256EmfGradientFillsToRetainedCommands` measured a 379.401 microsecond
median (411.111 microsecond mean, 93.315 microsecond standard deviation) and
348.97 KB allocated for 256 alternating rectangle/triangle records. Three
iterations, denied priority elevation, and the wide confidence interval make
this allocation/command-shape evidence rather than a throughput claim. The
isolated toolchain exceeded its 120-second generated-build timeout before
measurement on this host; raw ignored artifacts are under
`artifacts/performance/system-drawing-emf-gradient-fill-inprocess-20260901`.
The complete Release System.Drawing suite passes 612/612; ApiCompat remains at
zero missing types, zero missing members, and 13 reviewed shape diagnostics.

The enum/switch coverage inventory now finds 150 explicitly handled records out
of 192 enum-backed EMF/WMF record identities, leaving 42 explicit unsupported
boundaries. A handled switch case is not a claim of complete semantics. The
largest remaining groups are mask/plg transfers,
region paint and flood fill, extended pens, color-management and pixel-format
state, escape/OpenGL records, and WMF region/layout/mapper records. This count
keeps the remaining playback work visible while the public API contract stays
at zero missing types and members.

## Delivery checkpoints

1. Restore the eight missing public identities and a bounded header/record
   parser with functional file/stream construction, cloning, and header queries.
2. Add allocation-free record enumeration and callback lifetime rules for all
   destination overloads, then bind `PlayRecord` to the typed playback session.
3. Implement state/object tables and the initial WMF/EMF/EMF+ vector and image
   playback families over typed ProGPU drawing commands.
4. Add portable stream recording and comments, then typed Windows handle/HDC
   adapters where the host explicitly provides them.
5. Remove each ApiCompat suppression only with matching behavior, malformed
   input, pixel, native-boundary, and performance evidence.

API presence alone is not subsystem completion. Until every required record
family in checkpoint three lands, the quality report must describe the current
vector player as partial metafile support and must not claim complete portable
rendering parity.

Checkpoints one, the enumeration half of checkpoint two, the bounded direct EMF
and WMF vector slices of checkpoint three, and the portable comment portion of checkpoint
four are implemented at this revision. Checkpoint
one removes all eight
missing-type diagnostics and 44 `Metafile` missing-member diagnostics, reducing
measured ApiCompat debt from 8 missing types, 98 missing members, 15 other
diagnostics, and 121 total to 0 missing types, 54 missing members, 15 other
diagnostics, and 69 total. Typed enumeration removes the remaining 36 overload
suppressions, leaving 0 missing types, 18 missing members, 15 other diagnostics,
and 33 total at that checkpoint. Later compatibility slices reduce other debt;
portable comment recording removes the final `Graphics.AddMetafileComment`
suppression and leaves 0 missing types, 0 missing members, 13 reviewed shape
diagnostics, and 13 total. Direct playback changes behavior rather than public
API shape, so those counts remain unchanged. No claim is made yet for complete
WMF/EMF/EMF+ playback or drawing-record encoding.
