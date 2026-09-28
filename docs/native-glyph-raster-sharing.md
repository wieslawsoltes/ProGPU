# Exact glyph raster sharing within native batches

## Application evidence and scope

LibreWPF Build `36451685592` at `807bfec3379d83ab3765cdef3e410590711c4c52`
passed 12 of 13 jobs; the original Windows ARM64 Showcase resize still failed
with five presentations and the old 744x521 frame after requesting 884x601.
Its separate instrumented replay completed all four idle phases. That replay
does not qualify the failed original run or establish the exact stalled shader.

The replay's bounded native compute trace recorded 1,968 glyph dispatches in
generation one and 1,316 in generation two, plus the path dispatches. Generation
three reused glyph coverage. The 4,096-event budget truncated during generation
four, so later dispatch/submission completeness cannot be inferred. No trace
budget, application deadline, completion rule or renderer default is changed.

The native rasterizer previously allocated and rasterized a tile for every
outline, even when distinct validated segment ranges and outline records encoded
exactly the same coverage. Whole-resource semantic sharing cannot eliminate
duplicates inside one resource or across different resource packs. This change
removes that concrete redundant work; resolving the ARM64 application failure
still requires the unchanged exact-package gate.

## Identity, ownership and execution

During a synchronous raster rebuild, an outline index maps to the first tile with
identical min/max bounds, physical raster scale, subpixel phase and every selected
segment byte. Float parameters compare by their actual bits. Segment count and
all segment fields, including unused line control points, participate. The arena
offset locates those bytes but is not itself coverage identity. Hash collisions
still require complete equality; there is no approximate geometry comparison.

Every original segment and outline is validated before it can share. Source
outline indices, per-outline descriptors, positioned instances, source alpha,
brush/style selection, clips and draw order remain intact. A local work-index list
selects only first-owner tiles for compute dispatch, raster drawing, SIMD/scalar
coverage and atlas copies. The raster route retains its zero-staging contract;
reported raster counts represent actual unique work. Uniform/record upload slots
remain source-indexed, not a replacement or reordered source API.

The lookup borrows validated caller bytes only during the rebuild and is destroyed
before GPU resource creation. Storage is bounded by the admitted outline count;
hashing/comparison visits referenced segment bytes. Existing input count, atlas,
buffer and alignment limits remain authoritative. No new persistent cache or GPU
resource container is added. Complete-batch retention still owns and compares all
original outline/segment bytes, including offsets, DPI and atlas generation.
Abandoned encoders invalidate both retained identities as before. Nonretained
benchmark frames still perform raster work for every distinct coverage tile.

## Qualification

- The same updated C++ GPU fixture against the previous native binary fails its
  new packed-outline work-count assertion; the candidate passes. Earlier fixture
  identity/generation setup errors were corrected without changing validation.
- Both native provider libraries compile on macOS ARM64. All 23 configured CTests
  pass, including CPU identity/collision controls and actual Metal Direct2D/MIL
  pixel readback. This is not a Dawn GPU runtime claim.
- The shared wgpu-native/Dawn GPU fixture retains its nine existing variants and
  adds three equal outlines inside one resource. Its independent reference uses
  distinct unused LINE control-point bytes, which prevent sharing but cannot
  change line coverage. Three original jobs become one with exact cold/warm pixels.
- The existing glyph qualification now adds six exact-pixel sharing comparisons
  in each of fastest, compute, raster, SIMD and scalar modes: 30 new comparisons,
  exact unique-job counts, warm retention and late-invalid-input recovery. All
  original 55 retention comparisons remain. The benchmark builds without warnings
  or errors. The initial new raster-mode assertion incorrectly required staging;
  it now explicitly asserts that mode's original zero-staging contract.

Full exact-head Build, both provider GPU gates, every original RID/package case,
and the original Windows Showcase resize/idle gate remain required before merge.
No failed/canceled producer may supply staged native runtimes. Native dispatch
reduction is not by itself measured application latency or final release parity.

Build `36459867439` exposed an obsolete growth-fixture assumption: separate
segment offsets no longer force duplicate glyphs to consume separate tiles.
The unchanged growth assertion failed on Linux ARM64 and reproduced locally.
The fixture now gives each outline a distinct unused LINE/QUADRATIC control-point
value, preserving coverage while requiring a distinct exact-byte raster identity.
An additional cold assertion requires every fixture outline to rasterize. The
original growth, generation, stable-replay and pixel checks remain unchanged.
All five local execution modes rasterize 1,024 jobs, grow the seeded atlas from
1,024 to 2,048 with one growth event, retain it on warm replay and match managed
pixels exactly. Ordinary retention/sharing checks still pass. Hosted CI remains
required; this fixture correction does not qualify the original ARM64 application.
