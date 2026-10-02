# Owned source glyph-resource import

The additive version-2 flat import pairs the unchanged raster resource with its
original source extension. Old imports retain their complete original validation
and float interaction contract; nonempty double-source resources do not masquerade
as that contract. No public vtable or existing wire record changes.

Import validates version, flags, counts, alignment, pointer arithmetic and bounded
storage before source array access. The source extension plus measured-writer
scratch has a 256 MiB limit, in addition to unchanged base resource/font limits.
Immutable readable arrays may share storage; no input is written. Publication
returns by value only after the whole candidate validates and owns copied records.
All owned allocation capacities participate in the existing alias guard.

Validation preserves original source em/DPI and policy, raw post-GPOS records,
prepared descriptor ordinals, effective fitted occurrences, full bidi identity,
unsafe boundaries, source breaks and selected line/run intersections. The existing
native arithmetic verifies effective source doubles without a float round trip.
The existing measured writer replays only the already-fitted original generation,
comparing every double placement, raster shadow, L1/L2 level and line frame. The
same interaction emitter then compares every double box/caret bit. There is no
font shaping, hinting, GPOS execution, source-text rewrite or driver-owner import.

An imported resource owns every original array and has no synthetic paragraph or
font context. Rendering continues through the same validated raster binding view;
the private source borrow exposes only copied immutable metadata while that exact
resource remains alive. The original producer and caller may retire independently.

The authored CTest fixture uses actual C source capture, nominal preparation and
paired borrows on both interpreter choices, real odd/even runs and all three
advance policies. PairPos wrap and nonzero suffix controls retain the original
raw generation. Corrupt versions, pointers, counts, policies, occurrences,
boundaries, precise/raster geometry and carets reject; copied source arrays remain
owned after producer/context destruction. These are source tests, not native/GPU,
Windows, package or WPF Display qualification. Mixed-resource MIL atomic routing
is a separate shared-channel fixture; source activation remains guarded.
