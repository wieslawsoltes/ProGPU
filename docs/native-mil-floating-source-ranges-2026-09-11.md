# Floating child source-range mapping

The next LibreWPF acceptance dependency is connecting original anchored document
ranges to the floating provider during first-window measurement. The existing
PortableTextSourceMap already distinguishes hidden source symbols from contiguous
shaping text. Its MapFloatingRanges operation now maps an ordered batch of measured
hidden children to PortableTextFloat events for that provider.

Child source ranges are relative to the map's segment, positive, disjoint and
bounded before addition. Both endpoints must map to the same shaping boundary:
a caller cannot hide visible parent text by declaring it a child. Adjacent siblings
retain their distinct order even when all map to one boundary. Anchor-only segments
map to position zero; trailing hidden children map to the terminal text boundary.
The caller keeps its original ordered child ownership list to consume placements.
The returned array is an independent snapshot and retains dimensions/alignment.
Native transport still validates dimensions, alignment and shaped-cluster admission.

This is source metadata, not layout or shaping. It reuses original ProGPU ToText
lookups, O(A log R) work and O(A) owned output for A anchors and R source ranges.
Ordered range/interval lookup is topology-dependent, not a data-parallel compute
fallback. Existing intrinsic UTF decoding is unchanged. Both WPF renderer modes
share this neutral source map; neither gets a separate composer or geometry model.

All five focused PortableTextSourceMapTests pass in Release (zero skipped).
Tests cover leading/trailing hidden edges, sibling order, empty shaping text,
caller mutation, visible-text overlap, duplicate/overlapping/unordered ranges,
negative/zero lengths, overflow and out-of-source positions. Source automatic
event production, hard-segment continuation, child placement/drawing/input and
application/package/platform/CI qualification remain required. This batch does
not remove source AnchoredBlock rejection or change deferred compatibility scope.
