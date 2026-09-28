# Exact glyph-resource sharing in native semantic replay

## Application dependency

ShowcaseApp's Windows native resize must wait for actual submitted GPU work before
reconfiguring its surface. The original ARM64 application gate still fails that
requirement; its failure must not be replaced by a diagnostic pass.

A separate Windows ARM64 software-adapter surface diagnostic exposed redundant
glyph work: 40 repeated text rows compiled 1,680 outlines for 1,880 positioned
glyphs. After five presentations, resizing from 744x521 to 884x601 deferred for
133 attempts (about 2.8 seconds). The rectangle control resized in about 27 ms.
Both used Microsoft Basic Render Driver, D3D12 and FXC. These observations
motivate sharing but do not establish the cause of ShowcaseApp's failure.

## Shared compiler policy

The native semantic compiler now shares a monochrome glyph resource's compiled
outline/segment slice only when its original flags and complete outline and
segment byte spans match an earlier resource in the same immutable scene.
Hashes only select candidates; byte equality remains mandatory after collisions.
Raster size, bounds, local segment offsets and subpixel phase remain in identity.
Resource handles and revisions do not prevent byte-identical coverage sharing.

The index contains at most one entry per already admitted glyph draw/resource.
It borrows only the current scene bytes during synchronous packed-page compilation
and is destroyed before returning. It is not a persistent pointer-key cache.
Original preflight validation and worst-case compilation budgets remain unchanged.
Exact accounting requires stored counts plus proven aliases to equal all original
outline and segment counts; no input is dropped from budget validation.

Every draw still appends its own positioned glyphs, text-style indices, color
sentinels and draw range. Source scene bytes, command order, clips, source input
indices and owners are not rewritten. Color bitmap resources retain their existing
separate packing. Whole-batch raster retention, DPI/atlas generation ownership,
all GPU coverage implementations and abandoned-encoder invalidation are unchanged.

## Required evidence

The CPU identity tests cover separate equal allocations, differing phase, scale,
bounds, local segment offsets, segment bytes, flags and span lengths, plus forced
hash collisions. Actual scene rendering must additionally compare shared resources
against independently rendered equivalent input, including changed coverage and
warm replay. Complete provider/RID/package CI and the original Windows ShowcaseApp
resize/idle gate remain required before merge and application qualification.

The shared GPU fixture compares three separately identified resources against an
independent single-resource packing containing all three original outlines and
segments. The reference cannot benefit from resource-level sharing. Four draws
retain overlapping translucent styles, a saved clip, and repeated use of an
already aliased resource. Nine variants cover stable replay, placement/paint,
phase, raster scale, bounds, segment geometry, draw basis, DPI and restoration.
Both wgpu-native's Direct2D/MIL GPU test and Dawn's WebScene provider gate invoke
the same fixture with independent engines and actual completed pixel readback.

Initial macOS ARM64 Release evidence (Apple M3 Pro, Metal, pinned Silk wgpu-native):
all 19 CPU CTests pass and the complete Direct2D/MIL GPU test passes. All
nine variants have byte-identical subject/reference and warm images. Cold exact
duplicates stage 5,120 coverage bytes versus the reference's 15,360; placement/
paint-only changes stage zero while uploading changed instances. Changed raster
bytes rebuild coverage. These counters establish eliminated duplicate fixture
work, not application latency or residency. Local C++ module-disabled builds
are diagnostic; complete module/provider/RID/package CI remains mandatory.

## Design research and bounds

The [existing glyph-retention research](native-glyph-raster-retention.md#provenance-and-paired-renderer-audit)
was revisited against primary contracts for
[Skia immutable runs](https://api.skia.org/classSkTextBlob.html),
[SkParagraph shaping reuse](https://skia.googlesource.com/skia/+/5d8f55bfa851a55fa7e111b9a4f1fd063509eca5/modules/skparagraph/src/ParagraphCache.cpp),
[DirectWrite layout versus drawing](https://learn.microsoft.com/en-us/windows/win32/directwrite/rendering-by-using-direct2d),
[Direct2D resource reuse](https://learn.microsoft.com/en-us/windows/win32/direct2d/improving-direct2d-performance),
[Win2D device loss](https://microsoft.github.io/Win2D/WinUI3/html/HandlingDeviceLost.htm),
[WebRender texture ownership](https://doc.servo.org/webrender/texture_cache/index.html),
[Vello's glyph-cache design discussion](https://github.com/linebender/vello/issues/204),
[Parley retained layout](https://docs.rs/parley/latest/parley/struct.Layout.html), and
[HarfBuzz shape plans](https://harfbuzz.github.io/shaping-plans-and-caching.html).

Adopted: keep reusable outline/raster content separate from draw instances and
CPU shaping/layout, with device-owned raster lifetime. Adapted: the current
bounded native packed page shares exact coverage bytes synchronously instead of
introducing a new persistent glyph atlas or copying another engine's cache.
Rejected: hash-only equality, font-name identity, altered hinting/subpixel keys,
global retention, source-text rewrites and CPU raster fallback. Existing lazy
pipelines, visibility, worker preparation, font fallback/variation resolution,
GPU batching, demand-driven uploads, atlas generations and device loss remain
unchanged. Layout/shaping and eviction policy are not part of this change.

For B validated outline/segment bytes and R distinct admitted glyph resources,
indexing is expected O(B + R), worst-case O(B * R) under hash collisions, with
O(R) temporary entries borrowing existing immutable bytes. Entries are bounded
by the original draw/resource limits; the original worst-case packed allocation
and admission limits remain. Stable page hits do not create or scan this index.
Matched final application performance, Instruments traces and complete image
gates remain required; this fixture is not a startup/scrolling performance claim.

The pre-existing Dawn mixed fixture contains two exact duplicate glyph resources
and an already shared path tile. Its exact coverage assertion now counts two
20-row, 256-byte-pitch tiles (10,240 bytes), removing only the duplicate glyph
tile from the previous 15,360 bytes. Its draw, command, family, submission,
style and independent image assertions are unchanged; this is not a tolerance.
