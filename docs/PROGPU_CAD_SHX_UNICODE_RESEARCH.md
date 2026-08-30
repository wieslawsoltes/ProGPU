# ProGPU.CAD compiled Unicode SHX research and design record

Date: 2026-08-30

Status: implemented for `AutoCAD-86 unifont 1.0` encoding 0; Big Font and
non-Unicode `*UNIFONT` encodings remain explicit capability gates.

## Scope and clean-room method

This record covers the compiled Unicode SHX ingestion, interpretation, layout,
retained rendering, selection, printing, and persistence checkpoint. The design
was derived from Autodesk's public format contracts and independently observed
compiled-file behavior. No third-party SHX parser or renderer source was read,
copied, translated, adapted, or used as an implementation template. The test
font is an original synthetic ProGPU fixture assembled from the documented
contract and the independently observed record envelope; no external font bytes
are vendored.

The in-repository implementation provenance is:

- `CadShxFont`, `CadShxInterpreter`, `CadShxGlyphCache`, and
  `CadShxTextLayout` for the existing ProGPU-owned standard SHX pipeline;
- `CadSnapshotCompiler` and `CadSnapshotCompiler.ShxMText` for retained TEXT,
  MTEXT, complex-linetype, selection, and print lowering;
- `CadPlanSceneCompiler`, `CadTextSelection`, and
  `GpuPictureNativeSceneCompiler` for the existing shared retained-path
  consumers.

## Authoritative format contracts

Autodesk documents three distinct source contracts, which ProGPU keeps typed
and separate:

- [Shape and font descriptions](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-DE941DB5-7044-433C-AA68-2A9AE98A5713.htm)
  define shape programs, the 2,000-byte definition limit, and command families.
- [Special codes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-06832147-16BE-4A66-A6D0-3ADF98DC8228.htm)
  and [vector directions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-0A8E12A1-F4AB-44AD-8A9B-2140E0D5FD23.htm)
  define the stroke interpreter rather than a text-shaping algorithm.
- [Text-font descriptions](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-Customization/files/GUID-9BBE5B28-DF02-4EC5-863A-BA04AB6F5EF1.htm)
  define above/below metrics and horizontal/dual-orientation modes.
- [Unicode font descriptions](https://help.autodesk.com/cloudhelp/2023/ENU/AutoCAD-MAC-Customization/files/GUID-D38A5A7B-1877-46B3-8120-32DA5F7430D1.htm)
  define `*UNIFONT,6`, 16-bit shape numbers, encoding and embedding fields, and
  two-byte command-7 references.
- [Big Font overview](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-CDAF6EAF-85D1-48FC-9A78-43514E0132D5.htm),
  [Big Font definitions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-DE0CCC57-AC55-4CDC-887E-730FC90364E4.htm),
  [extended Big Font definitions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-00ED0CC6-A4BE-4591-93FA-598CC40AA43D.htm),
  and [Big Font extensions](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Customization/files/GUID-88F0ED5F-A4CA-41C8-AD63-0E1B0CA5E703.htm)
  define lead-byte ranges, multibyte character identities, and extended
  primitive placement. These are not Unicode scalar identities.
- [Shape/font compilation](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-Customization/files/GUID-BC8EFEAC-D640-410A-8EC8-2EBB38DE6563.htm)
  establishes compiled SHX as the runtime artifact.

For Unicode fonts, header shape zero contains exactly six program bytes:
above, below, mode, encoding, type, and terminator. ProGPU accepts documented
modes 0 and 2, encoding values 0 through 2, and the two embedding/type bits.
Only encoding 0 is currently eligible for text layout. Encodings 1 and 2 remain
parsed metadata but fail before character decoding because their byte mapping is
a different contract.

## Independently observed compiled envelope

Two unrelated public compiled Unicode artifacts were inspected as binary data,
without consulting their projects' parser source:

- iTwin [`Cdm.shx`](https://github.com/iTwin/imodel-native/blob/main/iModelJsNodeAddon/api_package/ts/assets/test/Fonts/Cdm.shx),
  Git blob `f0701449bbbca2e20016c0a3df6f7249e8f857f6`,
  3,930 bytes and 107 records;
- mlightcad [`SIMPLEX8.shx`](https://github.com/mlightcad/shx-parser/blob/main/data/SIMPLEX8.shx),
  Git blob `da9d51e3536808ac018388e8dcfdc8ec770ca266`,
  13,813 bytes and 284 records.

Both independently exhibit this envelope:

1. the 25-byte `AutoCAD-86 unifont 1.0` signature including CR/LF and `0x1A`;
2. a little-endian 16-bit record count;
3. that many sequential records, each containing a little-endian 16-bit shape
   number, little-endian 16-bit record length, NUL-terminated ASCII name, and
   NUL-terminated program;
4. exact end of input after the last declared record, with no standard-container
   `EOF` trailer.

The independent files differ substantially in size and directory population,
which guards against treating one artifact's incidental offsets as a contract.
The parser validates every length, terminator, duplicate identity, limit, shape
zero metadata field, and exact end boundary before publication.

Compiled Big Font artifacts were also inspected only to confirm that their
indexed directory is structurally distinct. That observation is not sufficient
to implement faithful text mapping: ACadSharp exposes decoded .NET strings,
whereas Big Font selection depends on original lead-byte/code-page sequences.
ProGPU therefore rejects `BigFontFilename` resolution rather than guessing that
a Unicode scalar is a Big Font code.

## Adopted design

`CadShxFont.Parse` selects the standard or Unicode container from its complete
signature. A successful parse performs one owned byte copy and retains each
program as a packed `ReadOnlyMemory<byte>` slice. Public typed metadata exposes
container kind, Unicode encoding, and embedding permissions. The existing
limits remain 16 MiB input, 65,535 shapes, and 2,000 program bytes per shape.

`CadShxInterpreter` selects command-7 operand width once from the immutable font:
one byte for standard programs and two little-endian bytes for Unicode programs.
All remaining commands, recursion/cycle checks, coordinate and scale limits,
four-position stack, and retained analytic line/arc output are shared. This is
not an approximation and introduces no per-command format probe.

For encoding-zero Unicode fonts, `CadShxTextLayout` maps one BMP scalar to the
same 16-bit shape number. DXF `\U+hhhh` uses that identical mapping. Autodesk
percent controls select U+00B0, U+00B1, and U+2205; standard fonts retain their
existing reserved-shape mapping. U+00A0 retains nonbreaking semantics and maps
directly to U+00A0 in a Unicode font. Surrogate code units and supplementary
scalars fail explicitly because the format identity is 16-bit.

The resulting immutable glyph/path and placement representation is unchanged.
Consequently Unicode SHX works through the existing TEXT, horizontal MTEXT,
complex-linetype, block, spatial-culling, exact-selection, print-plan, managed
picture, and native-picture paths. Desktop discovery and browser bundled-byte
registration use the same catalog API and immutable parser.

## Cross-engine architecture gate

The required rendering and text systems were rechecked through their primary
design/API material:

- [Skia retained paths](https://skia.org/docs/user/api/skpath_overview/) and
  [SkParagraph shaping stages](https://docs.skia.org/docs/dev/design/text_shaper/)
  separate reusable geometry from Unicode/OpenType shaping.
- [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/getting-started-with-directwrite)
  owns Unicode/OpenType analysis, while
  [Direct2D geometries](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-geometries-overview)
  retain path data for drawing and geometric queries.
- [Win2D `CanvasPathBuilder`](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasPathBuilder.htm)
  supplies an explicit geometry-building boundary rather than coupling path
  construction to text shaping.
- [WebRender's rendering overview](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html)
  separates retained display data and renderer/device work.
- [Vello's retained-scene vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  preserves scene/path reuse before GPU rendering.
- [Parley's layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  and [HarfBuzz's shaping scope](https://harfbuzz.github.io/what-is-harfbuzz.html)
  keep Unicode analysis, clustering, fallback, and OpenType shaping in a
  reusable CPU text pipeline.

Adopted: immutable parsed font data, lazy cached stroke interpretation, one
retained analytic path per font/shape/orientation, positioned snapshot content,
visibility culling, and reuse across rendering, selection, and output.

Adapted: Unicode SHX's authored codepoint-to-shape identity enters the retained
geometry pipeline directly. It does not enter HarfBuzz, SkParagraph, or
DirectWrite because there are no OpenType glyph substitutions, clusters,
fallback faces, variation axes, or font tables to shape. MTEXT line breaking and
positioning remain reusable CPU results; no layout work moves onto the GPU.

Rejected: per-frame parsing, eager expansion of every font program, GPU text
layout, texture-atlas conversion of analytic strokes, backend-specific glyph
caches, native-only parsing, fallback to TrueType, synthesized supplementary
identities, and treating Big Font byte identities as Unicode scalars.

## Complexity, ownership, and hot paths

- Parsing is `O(B + S)` time and `O(B + S)` owned storage for input bytes `B`
  and shape records `S`; lookup is expected `O(1)`.
- First-use interpretation is `O(C + P)` time and `O(P + D)` storage for
  executed commands `C`, retained path segments `P`, and recursion depth `D`.
- TEXT/MTEXT decoding and placement are `O(U + G)` for UTF-16/control units `U`
  and retained glyphs `G`, plus the existing bounded MTEXT line/column work.
- Stable retained replay performs no SHX parsing, command interpretation, text
  decoding, layout, filesystem access, or upload.
- No C#/C++ crossing, native ABI, shader, atlas, texture, upload, or device-loss
  contract changes. Unicode paths use the same immutable picture commands in
  both renderers.

## Validation and parity

The original synthetic Unicode fixture covers metadata and owned storage,
16-bit glyph identities, command-7 references above 255, direct BMP text, DXF
escapes, percent controls, nonbreaking space, rejected surrogate/non-Unicode
encodings, malformed/truncated/trailing/duplicate/over-limit inputs, retained
TEXT and formatted MTEXT, complex linetype resources, exact selection, print
reuse, managed/native picture compilation, and DXF/DWG round trips.

The managed/native applicability audit found one shared implementation boundary:
snapshot compilation creates retained `DrawPath` commands and both picture
compilers consume them. A native-picture regression covers the Unicode result;
there is no native CAD parser to duplicate. No shader changed, so the canonical
shader-source and complexity contract is not applicable to this checkpoint.

No performance improvement is claimed. The implementation preserves bounded
work and stable retained replay; matched GPU/image-quality and platform profiling
remain required before any latency, throughput, or quality claim.

## Remaining work

- Add a typed Big Font text-source/code-page seam that preserves original
  encoded character identity through ACadSharp import and ProGPU editing.
- Implement the indexed Big Font parser, lead-byte ranges, extended primitive
  calls, and matched standard/extended fixtures only after that identity seam is
  authoritative.
- Decide whether `*UNIFONT` encodings 1 and 2 need separate source-byte APIs.
- Expand the independently licensed compiled Unicode conformance corpus without
  vendoring third-party fonts into ordinary implementation files.
- Add property/fuzz campaigns around all record boundaries and recursive
  command graphs.
