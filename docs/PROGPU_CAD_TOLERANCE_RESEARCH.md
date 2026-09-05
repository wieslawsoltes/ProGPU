# ProGPU.CAD TOLERANCE feature-control-frame research

Date: 2026-08-30

## Scope and clean-room boundary

This checkpoint covers non-annotative standalone `TOLERANCE` entities and
`MULTILEADER` tolerance content. It retains multiline geometric
feature-control frames, cell boundaries, documented geometric-characteristic
and material-condition symbols, DIMSTYLE and typed `DSTYLE` appearance,
arbitrary entity planes and affine block placement, TrueType or SHX ordinary
text, exact frame selection, managed/native picture replay, printing, and
DXF/DWG persistence.

The implementation is clean-room. No third-party renderer source was copied,
ported, translated, or used as an implementation template. Autodesk's public
contracts defined the observable text grammar, insertion point, orientation,
and style inputs. The retained parser, layout, immutable streams, selection,
and scene recording are original ProGPU code in
`CadSnapshotCompiler.Tolerance.cs`, `CadDocumentSnapshot.cs`,
`CadPlanSceneCompiler.cs`, and `CadSelection.cs`. The ACadSharp-owned
`Tolerance.ApplyTransform` change is a direct correction of ProGPU's pinned
dependency source and is covered by matched dependency tests.

## Primary contracts consulted

- Autodesk ObjectARX [`AcDbFcf::setText`](https://help.autodesk.com/view/OARX/2018/ENU/?guid=OREF-AcDbFcf__setText_ACHAR__)
  defines newline-separated frame rows, `%%v` cell boundaries, and the complete
  documented `GDT` symbol-code vocabulary.
- Autodesk ObjectARX [`AcDbFcf::setLocation`](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-AcDbFcf__setLocation_AcGePoint3d_.html)
  defines the location as the midpoint of the first row's left frame edge.
- Autodesk ObjectARX [feature-control-frame methods](https://help.autodesk.com/cloudhelp/2018/ENU/OARXMAC-RefGuide/files/OREFMAC-__MEMBERTYPE_Methods_AcDbFcf.html)
  and the [`AcDbFcf` class contract](https://help.autodesk.com/view/OARX/2025/ENU/?guid=OARX-RefGuide-AcDbFcf)
  define the normal/direction plane, direction away from the location, bounds,
  DIMSTYLE association, and dimension-style overrides.
- Autodesk's [Geometric Tolerance dialog contract](https://help.autodesk.com/cloudhelp/2022/ENU/AutoCAD-Core/files/GUID-2821B734-18B4-4A0E-A020-318D45DDDAE1.htm)
  and [geometric-tolerance concepts](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-LT-MAC/files/GUID-E5691618-A71F-4BF4-81EC-859B22AE6BF4.htm)
  establish the characteristic, tolerance, material-condition, projected-zone,
  and datum cell roles.
- Autodesk's [dimension override DXF contract](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-6A4C31C0-4988-499C-B5A4-15582E433B0F.htm)
  defines the `ACAD`/`DSTYLE` typed code/value records used by tolerances.

The supported inline codes are mapped to semantic Unicode characters before
the existing ProGPU text stack runs. This preserves font fallback and shaping
instead of retaining a dependency-specific symbol font. Unknown font tokens or
symbol codes fail closed; they are not displayed as misleading literal text.

## Cross-engine architecture audit

The required production stacks were checked before design:

- Skia's [API overview](https://skia.org/docs/user/api/) separates retained
  pictures, paths, and reusable shaped text blobs; SkParagraph retains layout
  separately from drawing.
- Direct2D and DirectWrite's [interoperation model](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  records vector geometry while DirectWrite supplies reusable glyph runs.
  [Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  exposes the same retained-resource split to Windows applications.
- WebRender's [typed display-list API](https://github.com/servo/webrender/tree/main/webrender_api)
  keeps scene descriptions backend-neutral and replayable.
- Vello's [scene contract](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  retains paths and glyphs for GPU-oriented replay, while its
  [architecture vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  keeps the scene/resource boundary explicit.
- [Parley](https://github.com/linebender/parley) retains CPU text layout, and
  HarfBuzz's [shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  keep Unicode/OpenType shaping reusable rather than rebuilding it in a frame
  renderer.

Adopted: immutable frame strokes plus independently reusable shaped text
fragments, one retained frame path command, CPU layout during snapshot capture,
and backend-neutral picture replay. Adapted: every frame and text header carries
the source CAD handle so selection/editing sees one semantic entity while each
fragment retains its existing text primitive and paint. Rejected: a bespoke GPU
FCF shader, per-frame grammar parsing, per-symbol paths, a second text shaper,
flattened raster frames, and a native-only CAD ABI.

## Retained layout and complexity contract

The parser produces rows, cells, and ordinary/symbol text runs. `%%v` starts a
new cell and a physical newline starts a new row. Each text run compiles once at
the entity origin through the established TrueType/SHX path; its retained
advance determines cell width. Cell width is at least one text-height square
including `DIMGAP`; each row's last cell expands to the maximum row width. The
frame location remains the midpoint of the first row's left edge. Direction,
normal, entity-plane basis, and any parent INSERT transform are composed once
before publication.

For `U` UTF-16 input units, `C` cells, `R` rows, `K` frame strokes, and `G`
positioned glyphs, capture is `O(U + C + K + G)` time and storage. Scene
recording emits one path for all `K` frame strokes plus one command per retained
text fragment. Steady replay does not parse ACadSharp or allocate frame
geometry. `MaxToleranceCellsPerEntity`, `MaxToleranceCells`,
`MaxToleranceStrokes`, and the existing text/glyph limits bound expansion.
Capture is transactional: a rejected entity rolls back frame, stroke, text,
glyph, font, decoration, diagnostic, header, and bounds state.

Effective appearance resolves `DIMSTYLE`, then typed `DSTYLE` codes for
`DIMSCALE`, `DIMTXT`, `DIMGAP`, `DIMCLRD`, `DIMCLRT`, and `DIMTXSTY`.
Fixed text-style height takes precedence over `DIMTXT`. Ordinary content honors
the resolved TrueType or SHX style; documented GDT tokens use the normal
TrueType fallback chain. Colors retain the established ByLayer/ByBlock and
background-adaptive policy.

## Managed/native, shader, and persistence applicability

The frame records an ordinary retained `DrawPath`; text records the existing
`DrawGlyphRun` or SHX path commands. The same immutable picture feeds managed
rendering, `GpuPictureNativeSceneCompiler`, and printing. No managed/native
crossing, C record, shader, resource lease, reflection, or per-frame P/Invoke
was added. The shader-source and C-ABI generation rules are therefore not
applicable to this checkpoint, while the shared-picture parity rule is covered
by focused native-picture and print tests.

ACadSharp now gives a newly constructed tolerance the documented world-X
direction and normalizes its rotated direction during `ApplyTransform`.
Dependency tests cover default orientation, nonuniform affine editing, and
DXF/DWG round trips. ProGPU tests cover frame topology, symbols, insertion
anchoring, typed overrides, parent INSERT transforms, exact point/Window/
Crossing selection, transactional rejection, standalone DXF/DWG round trips,
MULTILEADER embedding, managed/native replay, and print reuse.

The Release benchmark harness now has a bounded `--tolerance-entities` lane.
On macOS 26.6 arm64 with .NET 10.0.5, 1,000 three-cell frames, three warmups,
and 24 measured iterations produced the following millisecond
`p50/p95/p99` values from the final binaries: snapshot capture
`34.004/172.675/181.183`, plan-scene compilation
`6.480/14.958/15.202`, and print-plan compilation
`8.047/14.666/20.925`. The 1,000-query spatial lane measured
`7.9/20.2/31.7` microseconds with zero managed allocation per query. There is no
before/after speedup claim because the before binary rejected this entity
family. The exact command was:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --entities 0 --tolerance-entities 1000 --warmup 3 --iterations 24 --queries 1000 --output-json artifacts/progpu-cad-tolerance-benchmark.json
```

## Explicit remaining fidelity gates

- Annotative tolerances fail closed until ACadSharp exposes a synchronized,
  typed active annotation-context contract.
- Undocumented third-party GDT/font tokens and fields fail closed until a public
  contract and conformance fixtures establish their meaning.
- Licensed visual differentials across AutoCAD versions, fallback-font sets,
  paper-space/UCS cases, and malformed/fuzz corpora remain before declaring the
  entity family fully verified.
