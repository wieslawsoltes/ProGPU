# ProGPU.CAD vertical SHX MTEXT research record

Date: 2026-08-30

Status: implemented for top-to-bottom standard and paired Big Font SHX MTEXT.
Right-to-left, bottom-to-top, and vertical TrueType MTEXT remain explicit
capability gates.

## Scope and clean-room method

This checkpoint adds top-to-bottom SHX MTEXT without rotating horizontal glyph
outlines. It covers explicit top-to-bottom direction, direction inherited from
a vertical STYLE, standard and paired Big Font glyphs, wrapping, paragraph
alignment, all nine attachment points, static/dynamic/reversed columns,
formatting, three stack forms, masks/frames, decorations, nested transforms,
exact retained bounds, managed/native picture replay, selection, and printing.

The design uses Autodesk's public DXF/ObjectARX contracts, primary text-engine
architecture material, and only the existing original ProGPU SHX interpreter,
MTEXT formatter, retained streams, and render consumers as implementation
provenance. No third-party parser, layout, renderer source, naming, file
organization, control flow, lookup table, or implementation text was used.

## Authoritative CAD contract

Autodesk's
[MTEXT DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-5E5DB93B-F8D3-4433-ADF7-E92E250D2BAB.htm)
defines group 72 values 1 (left-to-right), 3 (top-to-bottom), and 5 (by STYLE),
the nine group-71 attachment points, the reference rectangle, and persisted
column records. The
[ObjectARX `AcDbMText` contract](https://help.autodesk.com/cloudhelp/2027/ENU/OARX-RefGuide/files/OARX-RefGuide-__MEMBERTYPE_Methods_AcDbMText.html)
adds the decisive column rule: ordinary columns are added below for
top-to-bottom flow and reversed columns above. The
[vertical Asian text guide](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Core/files/GUID-32786109-F454-47DD-AA4C-FB8C37F4430D.htm)
specifies a standard SHX plus SHX Big Font pair and the vertical STYLE option.
The
[STYLE contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Core/files/GUID-0496BC60-0D07-4982-A395-B83E39EA5CF4.htm)
limits vertical mode to fonts with dual-orientation support. Autodesk's
[Asian Big Font table](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-Core/files/GUID-CBC3F683-06F3-4B9C-8747-D01F3C262956.htm)
identifies purpose-built vertical Big Fonts whose selected character programs
differ from horizontal output.

Adopted: top-to-bottom as a distinct inline axis; STYLE-inherited flow;
dual-orientation font eligibility; authored command-14 vertical programs;
standard/Big Font pairing; geometric attachment; columns below or above based
on the persisted reverse bit.

Adapted: ProGPU's existing horizontal formatter works in logical inline/block
coordinates. After wrapping, line/column placement, stacks, decorations, and
background construction, the bounded affine map `(inline, block) ->
(-block, inline)` produces physical top-to-bottom layout. Glyph paths are not
mapped by that rotation: each cache lookup selects the font-authored vertical
program, and only its placement origin enters the logical-to-physical map.

Rejected: rotating cached horizontal glyphs, synthesizing vertical advances,
guessing Big Font orientation, accepting a horizontal-only font, flattening
formatted content, or treating right-to-left/bottom-to-top as reversals of the
implemented contract.

## Cross-engine rendering and text gate

The required engines were rechecked through primary sources:

- [DirectWrite vertical text](https://learn.microsoft.com/en-us/windows/win32/directwrite/vertical-text)
  separates top-to-bottom reading direction, block flow, per-run orientation,
  vertical variants, and baseline selection. Its
  [glyph-orientation enum](https://learn.microsoft.com/en-us/windows/win32/api/dwrite_1/ne-dwrite_1-dwrite_vertical_glyph_orientation)
  reinforces that upright CJK and rotated Latin policy is a glyph/run decision,
  not a whole-layout bitmap rotation.
- [HarfBuzz direction types](https://harfbuzz.github.io/harfbuzz-hb-common.html#hb-direction-t)
  distinguish horizontal from vertical buffers and explicitly define TTB and
  BTT. [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  keep glyph selection and two-dimensional positioning in reusable CPU work.
- [SkParagraph](https://skia.googlesource.com/skia.git/+/4134f8091147e2e13df687829d137f97355fb0dd/modules/skparagraph/src/ParagraphImpl.cpp)
  currently centers its public paragraph path on horizontal LTR/RTL/bidi
  layout; it was not treated as a vertical CAD behavioral oracle.
- [WebRender's retained text run](https://searchfox.org/mozilla-central/source/gfx/thebes/gfxTextRun.h)
  retains orientation and reusable glyph data, while Firefox painting keeps
  vertical-upright decoration behavior explicit rather than implicit.
- [Vello's scene glyph runs](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  retain global and per-glyph transforms, and its
  [retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  separates text layout from glyph painting.
- [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  keeps itemization, shaping, line breaking, and positioning as CPU stages;
  its current line implementation uses logical inline/block terminology but is
  not used as a CAD vertical-output oracle.
- [Direct2D geometry](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview)
  and [Win2D cached geometry](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm)
  support the existing device-independent retained-path boundary.

Adopted: logical axes, authored orientation-specific glyphs, immutable CPU
layout, and retained transformed paths. Rejected: delegating SHX programs to an
OpenType shaper, GPU layout, backend-specific orientation caches, per-frame
font interpretation, and translating a horizontal paragraph implementation.

## Architecture, complexity, and parity

`CadShxTextLayout` already caches glyphs by `(shape, orientation)` and executes
the SHX font's command-14 conditionals. The MTEXT compiler now validates
nonnegative X-only horizontal advances or nonpositive Y-only vertical advances,
measures vertical cross-axis glyph bounds after width/oblique formatting, and
lays all inline objects in one logical coordinate system. A final linear pass
maps glyph origins, rectangles, separators, and backgrounds into physical MTEXT
coordinates before resolving attachment and immutable snapshot publication.

Work remains `O(C + G + L)` time and temporary storage for decoded source units
`C`, glyphs `G`, and retained logical lines `L`; automatic column height retains
its bounded `O(32L)` search. The added orientation map is `O(G + D + S + M)`
for glyphs, decorations, stack strokes, and mask/frame rectangles. Stable replay
does not parse, interpret, lay out, transform, allocate, or upload anything
introduced by this checkpoint.

The managed/native applicability audit found no native CAD text compiler to
duplicate. Both backends consume the same existing retained `DrawPath`,
rectangle, and line commands. Focused regressions compile vertical formatted
SHX MTEXT into a native picture and reuse the same immutable commands in the
print plan. No public C ABI, shader, cache-generation, upload, atlas, DPI, or
device-loss contract changed.

## Validation and remaining work

Original synthetic fixtures cover dual-orientation standard text, explicit and
STYLE-inherited top-to-bottom flow, physical attachment, vertical decorations
and stacks, ordinary/reversed column direction, paired CP932 Big Font selection,
managed/native replay, printing, and transactional rejection of horizontal-only
fonts. The final macOS arm64 Release validation passed 96/96 SHX-focused tests and
744/744 complete ProGPU.CAD tests. Release packaging produced
`ProGPU.CAD.0.1.0-preview.62.nupkg`. These are correctness and packaging gates,
not latency, throughput, memory, or image-quality measurements.

Remaining acceptance work:

- capture licensed AutoCAD geometry or images for representative standard and
  vertical Big Fonts and add pixel/placement differentials;
- add a redistributable compiled vertical Big Font corpus beyond original
  synthetic programs;
- design independent RTL and bottom-to-top contracts if Autodesk exposes them
  as supported MTEXT behavior;
- implement TrueType vertical shaping only with script orientation, vertical
  glyph variants, and baseline behavior equivalent across managed/native paths.

No performance or image-quality improvement is claimed by this checkpoint.
