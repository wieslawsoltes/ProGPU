# ProGPU.CAD TABLE persisted-cache research

Date: 2026-08-30

## Scope and clean-room boundary

This checkpoint covers non-annotative `TABLE` entities whose persisted owning
block contains a bounded static display cache. It preserves the cache's cell
borders, fills, formatted text, field display results, nested static block-cell
graphics, ByBlock/ByLayer appearance, arbitrary table placement, exact
selection, managed/native replay, printing, DWG persistence, and existing DXF
read behavior.

The implementation is clean-room. No third-party renderer source was copied,
ported, translated, or used as an implementation template. Autodesk's public
entity and ObjectARX contracts define the observable relationship between a
table and its display block. ProGPU expands that ACadSharp-owned persisted
model through its original bounded INSERT compiler and existing retained
primitive streams. The ACadSharp `TableEntity.ApplyTransform` correction is a
direct change to ProGPU's pinned dependency source and has matched dependency
tests.

## Primary contracts consulted

- Autodesk's [`TABLE` DXF contract](https://help.autodesk.com/cloudhelp/2020/ENU/AutoCAD-DXF/files/GUID-D8CCD2F0-18A3-42BB-A64D-539114A07DA0.htm)
  defines `TABLE` as an `AcDbBlockReference`, gives group 2 an anonymous `*T`
  block name, gives group 343 the hard pointer to the owning block record, and
  persists insertion point plus horizontal direction.
- ObjectARX [`AcDbTable::recomputeTableBlock`](https://help.autodesk.com/cloudhelp/2019/ENU/OARX-RefGuide/files/OREF-AcDbTable__recomputeTableBlock_bool.html)
  defines regeneration as updating the table's referenced block-table record
  to match the table object.
- Autodesk's ActiveX [`RecomputeTableBlock`](https://help.autodesk.com/cloudhelp/2025/ENU/AutoCAD-ActiveX-Reference/files/GUID-5ED2334B-96E1-4CE9-9FD9-C5B02561717E.htm)
  exposes the same explicit cache-update operation.
- Autodesk's [Table object contract](https://help.autodesk.com/cloudhelp/2026/ENU/AutoCAD-ActiveX-Reference/files/GUID-7B82400C-53D0-4D1A-94FA-66BB3040F0AA.htm)
  notes that recomputing the table from scratch is expensive and recommends
  suppressing regeneration across multiple mutations.

These contracts support using the persisted block as the authoritative visual
cache during capture. They do not support silently inventing a second layout
from partial cell metadata.

## Cross-engine architecture audit

The required production stacks were checked before design:

- Skia's [API overview](https://skia.org/docs/user/api/) separates reusable
  pictures, paths, text blobs, and images from replay. A table cache maps to a
  retained picture, not a special rasterizer.
- Direct2D and DirectWrite's [interoperation model](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  retains vector drawing resources while DirectWrite supplies reusable shaped
  glyph runs. [Win2D](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/)
  exposes the same resource/drawing split.
- WebRender's [typed display-list API](https://github.com/servo/webrender/tree/main/webrender_api)
  keeps scene items backend-neutral and replayable rather than relaying out
  document structures in the renderer.
- Vello's [scene contract](https://github.com/linebender/vello/blob/main/vello/src/scene.rs)
  retains paths, fills, images, and glyphs, while its
  [architecture vision](https://github.com/linebender/vello/blob/main/doc/vision.md)
  keeps scene construction separate from GPU execution.
- [Parley](https://github.com/linebender/parley) retains CPU text layout, and
  HarfBuzz's [shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  retain Unicode/OpenType shaping results independently of drawing replay.

Adopted: bounded expansion of an immutable persisted visual cache into the
existing typed primitives, reusable text shaping, semantic root handles, and
one backend-neutral picture. Adapted: the anonymous `*T` block stays an
ACadSharp document resource while each lowered child carries the TABLE handle
for selection and editing. Rejected: a bespoke table shader, per-frame cell
layout, raster flattening, native-only lowering, parsing proxy graphics, and a
reduced grid/text approximation that would discard fields, fills, or block
cells.

## Retained cache and complexity contract

`CompileTable` first requires a non-empty persisted cache. It then uses the
ordinary INSERT path, which already validates nesting depth, XRef/unloaded and
dynamic-block state, recursive cycles, finite transforms, array bounds, layer
state, draw order, and ByBlock style inheritance. TABLE precedes INSERT in type
dispatch so an empty cache fails with a TABLE-specific `CADSNAP003` diagnostic
instead of succeeding with no pixels. Missing cache references remain invalid
input (`CADSNAP002`).

For `T` tables, `E` total cached child entities, and `G` positioned glyphs,
snapshot capture is `O(T + E + G)` time and storage, plus the established
bounded costs of the child primitive types. Scene recording and stable replay
are `O(E + G)` output work; table-cell metadata is not reparsed or laid out per
frame. The existing `MaxBlockNestingDepth`, `MaxExpandedEntities`, block-array,
primitive, text, and glyph limits bound expansion.

Every lowered primitive retains the TABLE's semantic root handle. Exact point
and Window/Crossing selection therefore test the retained child geometry but
deduplicate to one table handle. Parent INSERT transforms and the table's own
insertion/normal/rotation/scale compose once through the shared affine path.
ACadSharp now defaults `HorizontalDirection` to world X and normalizes it after
affine editing while retaining the base INSERT transform state.

## Managed/native, shader, persistence, and performance applicability

Cached primitives record ordinary retained path, fill, glyph-run, image, and
nested-picture commands. The same picture feeds managed rendering,
`GpuPictureNativeSceneCompiler`, and printing. No managed/native crossing, C
record, shader, resource lease, reflection, or per-frame P/Invoke was added.
The shader-source and C-ABI generation rules are therefore not applicable;
shared-picture parity is covered by focused native-picture and print tests.

ACadSharp tests cover default orientation, nonuniform affine editing, and DWG
cache/orientation round trips. ProGPU tests cover cache contents, nested affine
placement, semantic exact selection, fail-closed missing/empty/XRef caches,
DWG round trips without regeneration, managed/native replay, and print reuse.
Existing ACadSharp fixture tests cover DXF and DWG TABLE reads. ACadSharp's DXF
writer currently classifies `TableEntity` as not implemented, so TABLE DXF
write/round-trip is an explicit dependency gate and is not claimed here.

The Release benchmark harness has a bounded `--table-entities` lane. On macOS
26.6 arm64 with .NET 10.0.5, 1,000 seven-primitive cached tables (8,000 expanded
entities), three warmups, and 24 measured iterations produced these millisecond
`p50/p95/p99` values from the final binaries: snapshot capture
`34.336/114.352/135.434`, plan-scene compilation
`8.086/25.108/54.591`, and print-plan compilation
`16.045/48.630/63.496`. The 1,000-query spatial lane measured
`7.0/14.0/41.3` microseconds with zero managed allocation per query. There is no
before/after speedup claim because the prior compiler reached TABLE only through
an undocumented generic dispatch path and silently accepted empty caches. The
exact command was:

```text
dotnet run --project src/ProGPU.CAD.Benchmarks/ProGPU.CAD.Benchmarks.csproj -c Release --no-build -- --entities 0 --table-entities 1000 --warmup 3 --iterations 24 --queries 1000 --output-json artifacts/progpu-cad-table-benchmark.json
```

## Explicit remaining fidelity gates

- TABLE DXF writing and generated DXF round trips remain blocked on a complete
  ACadSharp writer contract.
- Missing or stale persisted caches fail closed; associative table evaluation,
  field/data-link refresh, formula evaluation, style-driven cell relayout, and
  cache regeneration require a separate bounded document-service design.
- Annotative tables, table breaks, live data links, proxy-only graphics, and
  unresolved XRefs/dynamic blocks remain unsupported.
- Licensed AutoCAD visual differentials across table styles, merged cells,
  field states, block cells, versions, paper space, and malformed/fuzz corpora
  remain before declaring the entity family fully verified.
