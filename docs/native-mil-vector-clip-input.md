# Native source geometry clip input

## Concrete acceptance dependency

LibreWPF's existing `NativeMilGeometryRelationSmoke` source-built host fixture
sets the drawing visual clip to `M8,8 L88,8 8,88 Z`, then selects inside/outside
that triangle. The visual paints a larger rectangle. Native MIL represents
the real clip with a vector mask, but index capture previously rejected every
mask state. A successful source geometry utility query is not native index proof.

This batch connects that actual clip path, including source visual and
DrawingContext PushClip ingress. It does not enable native host queries or claim
all clip/mask combinations are implemented.

## Contract and implementation

The public C++ builder's `add_vector_clip_mask` overloads have an optional
`source_geometry_clip` annotation, false by default. It declares original source
input geometry, not material/raster coverage. Annotated masks require unit opacity.
The flag is builder-owned metadata: no stream layout, C ABI or shader changes.
MIL's `append_geometry_clip` and rotated rectangle clip path supply it. Other
vector masks do not gain source semantics simply because their data looks similar.

The hit producer admits a declared single intersect path with no boolean program
under explicit source-geometry capture. Line, quadratic and cubic segments retain
their kinds and control points, fill rule and closure. Their own clip transform
maps them once into world coordinates, independently of the drawing transform;
existing NEON/SSE2 four-coordinate work is shared with primitive bounds mapping.
The canonical query shader receives the actual segment range, not an envelope
containment substitute. Clip bounds only prune candidates.

Each used mask's transformed segments are cached once per index build and shared
by affected hit primitives. Cache allocation is lazy; scenes without vector masks
do not allocate it. This adds O(R + S) storage and O(R + S + P) work for resource
slots R, distinct used clip segments S and affected primitives P. State/save/restore
and source-owner boundaries remain unchanged. Partial/unsupported capture never
publishes a successful index. There are no per-primitive native calls, GPU
submissions, raster readbacks or new compute fallback paths.

A containing rectangular clip is redundant and keeps the real vector clip.
Nonredundant rectangle intersections, multiple vector paths, boolean clip
programs, arcs, spatial opacity masks and undeclared material masks remain
explicit unsupported native-input contracts. Do not overwrite a path clip with
rectangle segments or combine independent contours as if winding meant
intersection. Built-in effect/cache layers carrying masks remain independently
guarded. Generic rendered-visibility capture continues rejecting mask states.

## Paired applicability and provenance

The managed source visual path already uses `PushSourceGeometryClip`,
`PushGeometryClip`, `PathAtlas.CompilePath` and canonical `WithClip` payloads.
It needs no duplicate algorithm; a paired triangle fixture retains transformed
edges, two draws sharing the clip and restored unmasked sibling input at zero
source opacity. Native scenes 9820/9821 cover the same triangle, actual MIL
ingress, range reuse/restoration and rejected declaration/composition cases.
Scene 9822 checks all polynomial control lanes against an explicit scalar affine
oracle and retains even-odd fill. An import consumer covers the C++ overload.

Original ProGPU implementation sources: native builder resource recording,
`place_primitive` intrinsic mapping, `GpuRenderCommandHitTestCache.cs` and
`GpuHitTesting.wgsl`. No third-party implementation was ported. The
[existing cross-engine input design references](native-mil-hit-test-ownership.md#design-references-and-decisions)
remain applicable: preserve source spatial/clip metadata separately from raster
coverage, with scene-qualified owners. Shaping, font discovery, retained upload,
shader workgroups, worker scheduling and device ownership are unchanged.

The MIL coverage digest is regenerated after source decoder edits. Fixtures are
authored for final execution, not evidence of runtime correctness or performance.
Native provider/module, managed renderer/headless, source/package applications,
Windows comparisons, lifetime/performance and both PR CI gates remain required.
