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
path. EMF/EMF+ structural and comment records are
nonvisual. The byte layouts and playback state follow the
official [EMR_RECTANGLE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/3c471238-0a02-4992-90a2-bfd2afd98f2a),
[EMR_CREATEBRUSHINDIRECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/b9a8ef5d-0089-4e42-b317-e6ebc0ff098f),
[EMR_CREATEPEN](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/2374647f-df67-48e3-86aa-384715c28e71),
[EMR_SELECTOBJECT](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/145b063d-5f96-41fe-b7ae-1e615b2bc2bf),
[EMR_SETMAPMODE](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/aa4ad35d-fa42-4a4f-959a-8b41304e1b05),
[EMR_SETWORLDTRANSFORM](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/985724c0-4db1-48f0-b346-67288b3288cb),
[EMR_POLYGON](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-emf/eb916781-58b6-4e92-b606-68071aa65733),
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
blits. Intersect-, exclude-, and logical-offset clip
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
contracts. Paths, other clipping records, `EXTTEXTOUT` glyph-index, numeric-
substitution, two-dimensional, DBCS-advance, and bidi-advance modes, independent
escapement/orientation, vertical fonts, SYMBOL glyph-index mapping, DIB images, richer GDI objects,
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
a supported draw but before an unsupported record. WMF gates cover 16-bit
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
`BLACKNESS`, and `WHITENESS` pixels remain the rendering authority, while a
destination-dependent `PATINVERT` record fails explicitly and rolls back an
earlier supported fill rather than silently approximating XOR composition.

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
