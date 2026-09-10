# System.Drawing.Region retained-geometry architecture

## Scope and clean-room boundary

This design is based on the documented `System.Drawing.Region` public contract and on
published renderer architecture. No third-party implementation source is copied or ported.
The compatibility source of truth is the .NET 10 reference-assembly metadata plus the
[official Region API documentation](https://learn.microsoft.com/en-us/dotnet/api/system.drawing.region?view=windowsdesktop-10.0).

## Primary-source review

- [Skia `SkRegion`](https://api.skia.org/classSkRegion.html) uses compact rectangle/run
  storage, exposes exact boolean operations and separates cheap bounds/reject queries from
  full iteration. ProGPU adopts the query discipline, but retains float path expressions
  because `System.Drawing.Region` is defined in world coordinates rather than integer pixels.
- [Direct2D `ID2D1Geometry::CombineWithGeometry`](https://learn.microsoft.com/en-us/windows/win32/direct2d/id2d1geometry-combinewithgeometry)
  defines union, intersection, XOR, and exclusion over device-independent geometry and emits
  the result through a geometry sink. ProGPU preserves those operations as typed retained
  nodes and lowers them only when a draw/query needs a finite geometry.
- [Mozilla WebRender clip management](https://searchfox.org/firefox-main/source/gfx/layers/wr/ClipManager.h)
  represents nested clips as parented chains and caches them within reference-frame scope.
  ProGPU similarly keeps region operations immutable and shareable instead of eagerly
  rasterizing after every mutation.
- [Vello `Scene::push_clip_layer`](https://docs.rs/vello/latest/vello/struct.Scene.html#method.push_clip_layer)
  retains a clip shape and transform until scene encoding. This supports keeping Region
  construction GPU-independent and applying the drawing viewport only at lowering time.
- HarfBuzz and DirectWrite were reviewed for applicability. Region mutation and clip boolean
  lowering do not change glyph shaping or font metrics, so no text-engine behavior is affected
  by this seam.

## Adopted design

`Region` owns an immutable expression with four node kinds: empty, infinite, finite retained
path, and typed boolean operation. Inputs are snapshotted, clones share immutable expressions,
and mutation replaces the root. Infinite operands remain symbolic until a caller supplies a
finite drawing universe, which avoids arbitrary giant GPU paths for complements.

Point hit testing evaluates the same boolean tree on the CPU. Axis-aligned rectangle trees
produce exact, deterministic scan rectangles by plane subdivision and vertical merging.
Curved paths remain retained for exact renderer clipping; exact curved scan extraction is a
tracked follow-up and currently reports `NotSupportedException` instead of returning a false
precision approximation. Rectangle visibility is conservative for such curved expressions so
invalidation cannot omit pixels.

The portable `RegionData` format is versioned, length-limited, depth-limited, deterministic,
and does not use object serialization. Native HRGN import/export remains an explicit platform
adapter seam; the portable backend throws `PlatformNotSupportedException` until that adapter
is supplied.

## Quality and performance gates

- no GPU/device creation during construction, mutation, clone, point hit testing, or rectangle
  scan extraction;
- exact boolean truth tables for point hit testing, including infinite and complement nodes;
- exact rectangle scan decomposition and round-trip serialization tests;
- bounded deserialization depth/item counts and rejection of non-finite geometry;
- retained operation creation is O(1), while explicit scan extraction is proportional to the
  unique rectangle edge grid and guarded by a complexity limit;
- the existing ProGPU path-atlas renderer and headless suites remain the managed/native parity
  gate for deferred boolean lowering.

## Managed/native applicability audit

The implementation adds `PathBooleanOperation` and retained boolean nodes to the shared managed
geometry model. Existing scene compilation remains the only lowering path consumed by both
managed and native backends. No command-wire record, native C++ structure, shader layout,
texture format, or text-shaping contract changed in this slice. Consequently there is no
second native Region implementation to keep synchronized; the full renderer suite and the
standalone headless suite are the required parity gates for the shared lowering behavior.

The exactness boundary is deliberate. Point queries and axis-aligned rectangle scans are exact;
curved rendering remains exact through retained paths; curved scan extraction throws instead of
inventing rectangle precision; and rectangle visibility is conservative when exact proof is not
available. Native HRGN conversion likewise throws until an explicit Windows adapter exists.
