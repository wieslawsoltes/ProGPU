# ProGPU.CAD MULTILEADER retained-content research

## Scope and provenance

This checkpoint covers non-annotative `MULTILEADER` entities with multiple
leader roots and branches, straight and spline-fit paths, per-line appearance,
doglegs, default and bounded static custom arrow blocks, embedded MTEXT, and
embedded static block content. It also closes ACadSharp editing and empty-content
DWG persistence defects needed by the ProGPU contract.

The subsequent TOLERANCE checkpoint also routes embedded tolerance content
through the complete retained feature-control-frame pipeline documented in
[`PROGPU_CAD_TOLERANCE_RESEARCH.md`](PROGPU_CAD_TOLERANCE_RESEARCH.md).

The implementation is clean-room. Autodesk's public file/API contracts and the
observable ACadSharp public object model were used to determine behavior. No
third-party renderer implementation text or structure was copied. The retained
cubic interpolation, spline streams, linetype lowering, MTEXT/SHX lowering,
scene recording, and selection algorithms are original ProGPU-owned code. The
new implementation provenance is:

- `src/ProGPU.CAD/CadSnapshotCompiler.MultiLeader.cs`
- `src/ProGPU.CAD/CadSnapshotCompiler.cs`
- `src/ProGPU.CAD/CadLineTypeLowerer.cs`
- `src/ProGPU.CAD/CadPlanSceneCompiler.cs`
- `src/ProGPU.CAD/CadSelection.cs`
- `external/ACadSharp/src/ACadSharp/Entities/MultiLeader.cs`
- `external/ACadSharp/src/ACadSharp/IO/DWG/DwgStreamWriters/DwgObjectWriter.Entities.cs`

## Primary contracts consulted

- Autodesk [Common MLeader Group Codes](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-69B9139A-48B4-48A5-B3CF-A3233ABFBE49.htm)
- Autodesk [MLeader Context Data Group Codes](https://help.autodesk.com/cloudhelp/2021/ENU/AutoCAD-DXF/files/GUID-EC56D0DE-026D-46AB-87B1-9692393B0C22.htm)
- Autodesk [MLeader Leader Line Group Codes](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-B2E6436A-F17D-4F59-9DE8-DBDB61AD36C6.htm)
- Autodesk [MLeader Leader Node Group Codes](https://help.autodesk.com/cloudhelp/2024/ENU/AutoCAD-DXF/files/GUID-8648B8F7-5BD3-445B-A1B2-6F65EC4ECB3E.htm)
- Autodesk [MLEADERSTYLE Group Codes](https://help.autodesk.com/cloudhelp/2016/ENU/AutoCAD-DXF/files/GUID-0E489B69-17A4-4439-8505-9DCE032100B4.htm)
- Autodesk ObjectARX [AcDbMLeader methods](https://help.autodesk.com/cloudhelp/2017/ENU/OARXMAC-RefGuide/files/OREFMAC-__MEMBERTYPE_Methods_AcDbMLeader.html)
- Autodesk ActiveX [MLeader overview](https://help.autodesk.com/cloudhelp/2023/CHS/AutoCAD-ActiveX-Reference/files/GUID-95FD33C2-DF78-4014-933C-9AC124E6A35D.htm)
- Autodesk managed API [MLeaderStyle properties](https://help.autodesk.com/cloudhelp/2018/ENU/OARX-ManagedRefGuide/files/OREFNET-__MEMBERTYPE_Properties_Autodesk_AutoCAD_DatabaseServices_MLeaderStyle.html)

The contracts establish that a multileader contains leader nodes (roots), each
root owns a dogleg vector and one or more leader lines, and each line can
override the entity/style path type, color, linetype, lineweight, arrow symbol,
and arrow size. The first line vertex is the arrow tip. Context values contain
the already-scaled arrow size, landing gap, text height, and block scale.

## Cross-engine architecture audit

The required retained-rendering and text stacks were checked before design:

- Skia's [SkPicture](https://api.skia.org/classSkPicture.html) and
  [SkParagraph](https://skia.org/docs/dev/design/text_shaper/) separate retained
  drawing from reusable shaping/layout.
- Direct2D [command lists](https://learn.microsoft.com/windows/win32/direct2d/command-lists)
  and DirectWrite [text layouts](https://learn.microsoft.com/windows/win32/directwrite/text-formatting-and-layout)
  retain replay and text layout independently.
- WebRender's [display-list API](https://github.com/servo/webrender/tree/master/webrender_api)
  keeps scene descriptions typed and backend-neutral.
- Vello's [scene model](https://github.com/linebender/vello) retains paths and
  text resources for GPU-oriented replay; Parley's
  [layout model](https://github.com/linebender/parley) keeps shaping/layout on
  the CPU.
- HarfBuzz's [shaping model](https://harfbuzz.github.io/what-harfbuzz-does.html)
  confirms that Unicode/OpenType shaping should remain a reusable CPU result.

Adopted: one immutable retained primitive per independently styled branch,
shared analytic spline streams, content routed through the existing text/block
pipelines, and backend-neutral recorded commands. Adapted: all branch headers
retain the source multileader handle, so selection and editing preserve one CAD
identity while paint remains branch-local. Rejected: flattening splines,
expanding MTEXT into per-character paths, creating a second text renderer, and
adding a native-only MULTILEADER ABI. Those choices would reduce fidelity or
fork managed/native behavior.

## Retained algorithm and contracts

For `R` roots, `L` visible leader lines, `V` total authored vertices, and `B`
expanded block/arrow children:

- validation and snapshot capture are `O(R + L + V + B)` time;
- straight paths retain `O(V)` degree-one controls;
- spline-fit paths retain `3(V - L) + L` cubic controls and are `O(V)` time and
  storage;
- each root dogleg is one degree-one path;
- steady scene replay is `O(L + R + C)`, where `C` is the retained content
  command count, with no ACadSharp traversal or geometry allocation;
- point and bounds selection reuse exact retained spline and arrow geometry.

`MaxMultiLeaderPaths`, `MaxMultiLeaderVerticesPerPath`, and
`MaxMultiLeaderControlPoints` bound proportional storage before publication.
Compilation is transactional: a rejected source does not leave partial paths,
splines, controls, knots, headers, or bounds.

Style resolution follows `MLEADERSTYLE -> entity override flags -> per-line
override flags`. `ByLayer` and `ByBlock` color, linetype, and lineweight values
are resolved with the same insertion context as ordinary entities. Patterned
branches use the existing exact NURBS linetype splitter, and default arrows are
recorded after patterned path lowering so they are never dropped. Custom arrow
blocks and block content require bounded static definitions and expand through
the normal nested-block compiler.

Embedded MTEXT is adapted to a detached ACadSharp `MText` value only during
snapshot capture, then lowered through ProGPU's complete TrueType/SHX parser,
shaper, column, background, decoration, native replay, and print paths. No
mutable adapter is retained. The persisted complete 4x4 content-block matrix is
used directly; it is not reconstructed from lossy derived fields.

## Managed/native and shader applicability

The immutable picture is the shared contract for managed rendering, native
picture compilation, and printing. No new managed/native crossing, wire record,
shader, duplicated backend algorithm, reflection, or per-frame P/Invoke was
introduced. Existing spline/path/text commands therefore preserve managed and
native parity automatically. Shader-source and C-ABI generation audits are not
applicable to this checkpoint.

## Editing and persistence findings

ACadSharp's public `MultiLeader.ApplyTransform` was empty. The dependency branch
now transforms retained points, root/line break endpoints, dogleg vectors and
lengths, content bases, text/block locations and directions, scalar display
sizes, and composes the persisted block-content matrix. Focused net48 tests
cover geometry and matrix composition.

The ACadSharp DWG writer also omitted the required false block-content
discriminator when both text and block content were absent. That shifted the
remaining context bitstream and made a written entity unreadable. The writer
now emits the discriminator, and matched ProGPU DXF/DWG round trips cover the
empty-content branch/dogleg case.

## Explicit remaining fidelity gates

- Annotative multileaders fail closed. ACadSharp documents its embedded and
  XDictionary annotation contexts as unsynchronized and does not expose a typed
  active-context selection contract.
- Authored leader-line and dogleg break pairs fail closed until retained
  segmentation can preserve continuous linetype phase across every gap.
- Tolerance content now reuses the complete retained feature-control-frame
  layout; its annotative and undocumented-token gates are recorded in the
  TOLERANCE research note.
- Block attributes fail closed until attribute references, values, and visibility
  can be synchronized with the embedded block expansion.
- Vertical multileader MTEXT fails closed through the existing vertical-shaping
  gate.

These cases are diagnosed instead of being drawn through, omitted, or replaced
with lower-quality approximations.
