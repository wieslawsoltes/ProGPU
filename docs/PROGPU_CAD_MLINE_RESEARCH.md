# ProGPU.CAD MLINE retained-topology research

Date: 2026-08-30

## Scope and clean-room boundary

This checkpoint covers immutable capture of classic `MLINE` vertices, authored
group-41 element cuts, independent MLINESTYLE element colors, group-42 outer
fill cuts, affine block placement, retained plan/print recording, and exact
point/Window/Crossing selection, and compound A-aligned simple/complex element
linetypes. Style joint lines and square/round/inner-arc caps remain follow-up
work. Unsupported alignment or exhausted lowering budgets use the existing
explicit diagnostic fallback rather than silently changing the pattern.

No third-party renderer implementation was copied, ported, translated, or used
as a source template. ACadSharp is used only through its in-repository public
dependency model (`MLine`, `MLine.Vertex`, and `MLineStyle`). The original
ProGPU implementation provenance is `CadSnapshotCompiler.MLine.cs`,
`CadDocumentSnapshot.cs`, `CadPlanSceneCompiler.cs`, and `CadSelection.cs`.

## Primary sources examined

- Autodesk's [MLINE DXF contract](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-DXF/files/GUID-590E8AE3-C6D9-4641-8485-D7B3693E432C.htm)
  defines WCS vertices, direction and miter vectors, group-41 element paths and
  cuts, group-42 fill boundaries, justification, closed/cap-suppression flags,
  and the authoritative style handle. Autodesk's
  [AcDbMlineStyle API](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-__MEMBERTYPE_Methods_AcDbMlineStyle.html)
  defines fill, joint, start/end cap, and cap-angle behavior.
- Skia's [SkPath overview](https://docs.skia.org/docs/user/api/skpath_overview/)
  and [SkPicture-oriented API overview](https://skia.org/docs/user/api/)
  support retaining multiple contours with explicit fill/stroke paint and
  replaying recorded commands instead of regenerating geometry per frame.
- Direct2D's [API contract](https://learn.microsoft.com/en-us/windows/win32/api/_direct2d/)
  and Win2D's [drawing overview](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  separate reusable path geometry from stroke styles. ProGPU adapts that split
  to CAD by retaining one immutable style index per visible MLINE interval.
- WebRender's [display-list architecture](https://github.com/servo/servo/wiki/Webrender-Overview)
  keeps compact geometry-bearing display items independent from content-owned
  objects and supports display-list hit testing. Vello's
  [compute renderer](https://github.com/linebender/vello) similarly accepts a
  retained scene. These informed the snapshot/recording boundary, not the
  MLINE geometry algorithm.
- DirectWrite's [programming guide](https://learn.microsoft.com/en-us/windows/win32/directwrite/programming-guide),
  HarfBuzz's [shaping contract](https://harfbuzz.github.io/harfbuzz-hb-shape.html),
  Skia's [SkParagraph module](https://github.com/google/skia/tree/main/modules/skparagraph),
  and Parley's [layout model](https://github.com/linebender/parley/blob/main/doc/concept.md)
  were checked as required. MLINE has no character, glyph, fallback, or line
  layout state, so text shaping/layout and glyph-cache changes are not
  applicable to this checkpoint.

## Adopted, adapted, and rejected

Adopted:

- preserve the persisted miter intersection and cut parameterization rather
  than reconstructing parallel offsets from a center polyline;
- keep each element's resolved color and linetype identity independent;
- lower bounded visible intervals and fill triangles once during immutable
  snapshot capture;
- share those retained primitives across managed recording, native picture
  replay, printing, BVH bounds, and exact selection.

Adapted:

- group-41 and group-42 cut distances become fixed-width double-precision WCS
  endpoints. Each element also retains its full logical path length and visible
  interval offsets, so dash phase continues across authored cuts. Recording
  batches strokes by element style and fills by color;
- affine INSERT composition transforms endpoints at capture, preserving the
  existing large-WCS rebase and one-picture replay path;
- malformed topology is rejected transactionally, while configurable document
  stroke/fill budgets fail closed before a partial snapshot can escape.

Rejected:

- drawing a center polyline, assuming two symmetric elements, or ignoring
  group-41 breaks;
- resolving all element colors/linetypes from the owning entity style;
- restarting a pattern at each visible cut, synthesizing caps/joints without
  their exact contract, or regenerating paths during replay/selection/print;
- a new shader or native ABI. Existing ProGPU `DrawPath` commands already feed
  both managed and native replay from the same retained picture, so a separate
  native MLINE implementation would create an unnecessary semantic fork.

## Complexity and validation contract

For `V` source vertices, `E` style elements, `P` persisted cut parameters, and
`T` retained fill triangles, snapshot work/storage is `O(V*E + P + T)` and is
bounded by `MaxMLineStrokes` and `MaxMLineFillTriangles`. Plan recording is
`O(S + T)` for `S` retained strokes, produces one fill path plus at most one
stroke path per referenced element style, and camera-only picture replay does
not revisit ACadSharp or allocate MLINE geometry. Exact point and box selection
are `O(S + T)` with no warm-query allocation. Patterned elements add
`O(Q + S + F)` work and `O(F + P)` output for `Q` pattern descriptors, `F`
visible figures, and `P` complex placements; counting completes before output
allocation and group-41 gaps do not reset phase.

The focused suite covers cut coordinates, independent colors, fills, retained
command batching, exact selection, nested affine blocks, dash-phase continuity,
native/print replay, DXF/DWG round trips, and budget failure. Licensed visual
differentials, joints, and all cap forms remain required before declaring
classic MLINE verified.
