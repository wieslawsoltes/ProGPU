# Original text-blob builder and intercepts

Ported all default/horizontal/positioned/rotation-scale builder allocations,
text/cluster buffers, safe raw-buffer adapters, convenience add methods and
explicit path-positioned runs. Caller buffers borrow the builder exclusively;
no pinned pointers, unsafe aliases or mutable access survives Build. Original
prefix copies, typed positioned tail clearing, ignored bounds hints, zero default
frames, empty-builder versus zero-glyph-run behavior, copied multi-run snapshots
and per-visible-glyph path placement remain distinct.

A single typed positioned run of at most 1,024 glyphs leases the actual existing
Arc-slice arrays into the immutable blob without a Build-time glyph copy. Final
blob retirement returns these arrays to one weakly referenced builder cache.
Reuse requires exclusive Arc ownership of both arrays; independently retained
renderer/run generations prevent mutation. Existing TextBlobRun/Data owners are
preserved. Raw/text/multi-run paths retain their copied snapshot policy. Rust
borrowing replaces the managed detached public-buffer shadow because a mutable
buffer cannot outlive its builder borrow or coexist with Build.

TextBlob intercept APIs preserve the actual original policy: paint ignored,
rotation-scale runs skipped, one ordered min/max pair per ordinary glyph, no
sorting/union, inclusive global band admission, invalid-band no-write behavior,
short-output prefix writes and full required count. Font-scaled/skewed/bold source
paths are cached per immutable run; control-hull interior additions, exact float
versus double boundary coefficients, bounded root solvers and original default
arc flattening remain unchanged. Independent root polynomial evaluations use
SIMD; root selection, contour traversal and interval reduction retain source
order. Bounds/path queries share the same actual cached glyph paths. Transformed
runs no longer expose a falsely positioned-only renderer record.

Original SHA-256 provenance:

- `src/SkiaSharp/SKTextBlob.cs`: `884ec41c6931dfa829762cee59adfb1559b23c39e205fc499adb9243bfeb0155`
- `src/SkiaSharp/SKTextBlobBuilder.cs`: `a06bd09477dbbf5de41b70e13ecb612203d8dcc26282c20323c2b816356fec5e`
- `src/SkiaSharp/SKTextBlobBuilderScratch.cs`: `872024de26bc5eb33835bc5f77fa68707db3c417e45c6592d0a8ec116ad8041a`
- `src/SkiaSharp/SKTextBlob.Intercepts.cs`: `abbd9cd4837deaa0b643d7cf753ee244585cb949a38a30413c65d3a66b109a0d`
- `src/ProGPU.Vector/ArcSegmentGeometry.cs`: `847d2cab48f10fe903eafff7ac56da9a36f4be77ad6932258c45b3ac6b31428e`

No foreign implementation; no compilation, tests, fixtures or runtime validation.
Native source/font/pixel qualification stays independent. No build artifacts.
