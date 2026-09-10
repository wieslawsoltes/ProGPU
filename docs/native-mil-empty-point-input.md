# Source visuals with no point input

## Core application dependency

LibreWPF's Showcase selects text, clicks the highlight to move the caret, and replaces
a keyboard-selected range. Source `CaretElement.HitTestCore(PointHitTestParameters)`
and its `CaretSubElement` return null, while their selection/caret drawing remains
available to geometry-region queries. The previous portable descriptor could
replace point coverage with a rectangle but could not express no own point hits.
Indexing the painted selection or caret therefore disagreed with the source.

## Shared contract and implementation

`IPortablePointHitRegionSource` now accepts a successful `PortableRect.Empty` as
authoritative empty own point coverage. False remains missing metadata. Ordinary
zero-width/height rectangles retain their existing boundary-point semantics.
This is not `IsHitTestVisible=false`, opacity culling or subtree suppression.
Both caret visuals publish the descriptor; ordinary descendants keep their own
policy. The source adapters close each own-content scope before child traversal.

The native sorted MIL snapshot retains its 40-byte layout. The formerly reserved
second uint is now `is_empty` (generated C# `IsEmpty`): zero retains the old
rectangle contract, one suppresses own point emission, all other values reject.
An empty entry requires four zero coordinates; invalid snapshots do not replace
the installed map. Old zero-initialized wire records remain compatible, while
callers referring to the former `Reserved` field must rebuild. Older native
payloads reject the new nonzero field; do not use stale packages to qualify it.

The shared C++ builder's optional `empty_point_region` save argument requires
the existing point-only scope. Sparse scope metadata suppresses only its point
rectangle and retains `RegionOnly` drawing until restore. It leaves rendering,
source owner identity, transforms, clips and descendant scopes unchanged.
Reset and serialization retain the existing builder ownership rules.

Managed retained commands use `SourceHitTestGeometryKind.PointEmptyBegin` with
zero coordinates and the existing point-scope terminator. Both direct indexing
and typed source-only traversal consume it. Picture clones preserve the existing
metadata; ordinary raster scopes remain identity opacity operations. Unbalanced
or malformed metadata faults publication rather than silently becoming a miss.

## Provenance, cost and research

This extends original ProGPU `SourceHitTestGeometry`,
`GpuRenderCommandHitTestCache` point scopes and native scene-builder point scopes;
it is not a port of another engine's implementation. See the primary
[cross-engine research record](native-mil-hit-test-ownership.md#design-references-and-decisions)
for Skia/SkParagraph, WebRender, Vello/Parley, Direct2D/DirectWrite/Win2D and
HarfBuzz. The retained geometry/layout separation is reused; inferring point
eligibility from raster alpha or bounds is rejected. WPF supplies only its
original source policy, not a second hit-testing algorithm.

Each boundary adds O(1) metadata work/storage, with bounded existing scope stacks
and the same batched scene update. No per-glyph/item crossing, shader fork, CPU
pixel readback, GPU submission or numeric fallback is introduced. Intrinsic
transform/bounds and shared GPU query paths remain unchanged. Shaping, font/cache
keys, culling, worker preparation, lazy startup, upload, batching, DPI/hinting and
device/atlas invalidation are unaffected. No performance gain is claimed;
final exact-binary image, lifetime and performance gates remain required.

## Authored qualification coverage

- Managed direct picture clone: empty own points with retained region drawing,
  independent child admission, scope validation and empty-versus-zero distinction.
- Native MIL scene 9830: parent/child suppression, retained region primitives,
  invalid discriminator/coordinates, zero-sized restoration and sideband removal.
- Include-based MIL and import-based builder consumers exercise the same library.
- LibreWPF typed direct/retained replay, sideband updates, real source caret and
  the Showcase live host selection/click/caret/replacement path cover integration.

These fixtures are authored for final execution, not runtime qualification.
Other source HitTestCore overrides and required application clip/cache/effect
combinations remain subject to concrete acceptance-path tracing. This does not
admit all custom visual point behavior or enable native input defaults.
