# ProGPU.CAD typed MTEXT paragraph research

Date: 2026-08-30

## Scope and clean-room boundary

This record covers first-line, left, and right paragraph indents; paragraph
space before and after; exact/multiple line spacing; and left, center, right,
and decimal tab stops for retained TrueType and SHX MTEXT. The implementation
was designed from public contracts and independently observed persisted MTEXT
strings. No third-party parser or layout implementation source was copied,
translated, or used as an implementation template. The exact in-repository
implementation provenance is `CadMTextContent.cs`, `StyledTextLayout.cs`,
`CadSnapshotCompiler.MText.cs`, `CadShxText.cs`, and
`CadSnapshotCompiler.ShxMText.cs`.

Autodesk documents the editor behavior but does not publish a complete formal
grammar for every serialized `\p` option. ProGPU therefore treats the grammar
implemented here as an independently derived, bounded compatibility contract:
`i/l/r/b/a`, `se/sm`, `q`, and `t/c/r/d` records, numeric continuation tab
positions, and `*` resets. This is not a claim of pixel-identical AutoCAD
behavior. A licensed AutoCAD differential corpus remains required before such
a claim.

## Primary sources examined

- Autodesk's [multiline formatting, columns, and stacking guide](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-E4DC3A14-3F0A-46AE-9503-6BBEE8DAF916.htm),
  [alternate-editor control codes](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-Core/files/GUID-7D8BB40F-5C4E-4AE5-BD75-9ED7112E5967.htm),
  and [MTEXT DXF contract](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-DXF/files/GUID-5E5DB93B-F8D3-4433-ADF7-E92E250D2BAB.htm)
  establish paragraph formatting, tabs, line spacing, retained content, column
  geometry, and drawing-direction values. The ActiveX drawing-direction
  contract describes right-to-left and bottom-to-top as reserved values, so
  both remain explicit capability gates.
- Skia's [text overview](https://docs.skia.org/docs/dev/design/text_overview/)
  and [SkParagraph module](https://github.com/google/skia/tree/main/modules/skparagraph), plus
  HarfBuzz's [cluster](https://harfbuzz.github.io/clusters.html) and
  [shape-plan](https://harfbuzz.github.io/harfbuzz-hb-shape.html) contracts,
  reinforce reusable paragraph shaping and cluster-safe line breaking.
- DirectWrite's [`SetIncrementalTabStop`](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextformat-setincrementaltabstop),
  [text formatting and layout guidance](https://learn.microsoft.com/en-us/windows/win32/directwrite/text-formatting-and-layout),
  and Win2D's [retained `CanvasTextLayout`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  support resolving paragraph geometry during reusable CPU layout rather than
  during drawing.
- WebRender/Mozilla's [retained `gfxTextRun`](https://searchfox.org/mozilla-central/source/gfx/thebes/gfxTextRun.h),
  Vello's [retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md),
  and Parley's [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  informed the immutable positioned-result boundary. Parley's open
  [tab-character issue](https://github.com/linebender/parley/issues/302) was
  treated as evidence that its current tab behavior is not a conformance oracle.

## Adopted, adapted, and rejected

Adopted:

- paragraph metrics are resolved before retained scene recording;
- tab positions and indent values scale from the MTEXT initial character
  height, while glyph shaping remains font/run specific;
- centered, right, and decimal tabs inspect only the bounded field through the
  next tab or line end; a missing decimal separator uses right alignment;
- paragraph space is retained in line boxes so static/dynamic column flow,
  masks, attachment, and total height see the same geometry;
- tabs create no drawable glyph and do not become justification gaps;
- SHX placements retain their decoded source scalar separately from shape
  identity so decimal alignment does not reverse-map font-specific shapes.

Adapted:

- `StyledTextLayout` accepts immutable per-paragraph records and shares the
  result with TrueType MTEXT, while SHX uses an equivalent original path
  specialization because SHX has font-authored advances and no OpenType
  shaping;
- default tabs continue at a bounded four-character-height interval after
  custom stops;
- exact spacing may be smaller than natural glyph bounds, matching the existing
  entity-level Exact contract, while multiple spacing remains at-least spacing.

Rejected:

- parsing paragraph strings during draw, replay, selection, or printing;
- one draw command per tab, whitespace expansion across tabs, or unbounded
  reverse mapping from SHX shape numbers;
- synthesizing right-to-left, bottom-to-top, or vertical TrueType MTEXT from
  horizontal glyph runs;
- using Parley's incomplete tab path or any foreign source organization as an
  implementation shortcut.

## Complexity, ownership, and parity

Parsing is `O(C + T log T)` time and `O(D + R + T)` storage for source units
`C`, decoded units `D`, inlines `R`, and at most 256 tab stops `T` per paragraph.
TrueType and SHX paragraph layout remain linear in positioned candidates; tab
field lookahead partitions at subsequent tabs and is bounded by the paragraph.
Temporary per-line advance arrays exist only during immutable snapshot
preparation. Stable replay, printing, and exact selection perform no paragraph
parsing, shaping, or tab resolution.

This change adds no shader, C ABI, native CAD compiler, upload, device-loss, or
resource-lifetime contract. Managed and native picture replay consume the same
retained glyph/path/rectangle/stroke commands. Matched TrueType and SHX tests
cover source parsing, positioning, scene replay, and print-plan replay.

## Validation status

Focused parser, shared-layout, TrueType MTEXT, SHX MTEXT, vertical SHX tabs,
SHX scalar-retention, managed scene, native-picture, and print-plan regressions
are checked in with the implementation. The final macOS arm64 Release runs
passed 751/751 ProGPU.CAD tests and 3,841/3,841 broader ProGPU tests. The isolated
warm MTEXT selection check passed with zero managed allocation after one
full-suite run observed 5,456 bytes of nondeterministic first-use/JIT noise; an
immediate complete rerun passed 751/751. Release packaging produced
`ProGPU.CAD.0.1.0-preview.62.nupkg`. No performance improvement is claimed; the
algorithm preserves the existing snapshot-only allocation and zero-work stable
replay contracts.
