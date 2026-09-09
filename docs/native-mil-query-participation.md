# Query-specific source input coverage

## Core dependency

LibreWPF's existing MVP `SelectorScrollViewer` contains wrapping TextBlocks. Its
wheel gate targets the arranged viewport center and requires a source descendant
hit. The source `TextBlock.HitTestCore(PointHitTestParameters)` contract accepts
its arranged rectangle, not merely glyph coverage. It does not override geometry
hit testing with that rectangle. Indexing only glyphs loses blank-space pointer
coverage; indexing an ordinary fill rectangle would incorrectly change region
selection. This is a source-backed gap, not a reproduced runtime failure.

Provenance: LibreWPF source `PresentationFramework/System/Windows/Controls/TextBlock.cs`
and `samples/ProGPU.Wpf.MvpApp/MainWindow.xaml.cs` were consulted for their input
contract. No WPF implementation was ported into ProGPU. The implementation extends
ProGPU-owned `ProGPU.Vector/GpuHitTesting.cs`, its canonical WGSL, and the existing
native scene hit-index validators.

## Shared contract

The existing 128-byte primitive flags now admit `PointOnly` (bit 2) or `RegionOnly`
(bit 3). Neither means existing all-query behavior. Both together and unknown bits
are rejected by managed primitive construction, managed native-stream encoding,
native builder admission and native scene validation. Visibility and input
visibility remain independently required. No record layout or query layout changes.

Both managed WebGPU and C++ wgpu-native/Dawn consume the same shader. It filters
query participation before precise geometry testing and result collection for both
single/topmost and list queries. Region means rectangle or ellipse, including a
degenerate region; the request kind, not its size, determines admission. Geometry,
transforms, clips, source IDs and z-order remain unchanged. Flags participate in
the existing retained hit-index hash; changing them renews GPU index resources.
Older native payloads reject the newly set bits; ship matching payloads and managed
assemblies, never claim these flags work with the older development package feed.

This adds fixed-cost bit selection to the existing GPU algorithm. It introduces
no CPU geometry fallback, pixel readback, per-item submission, new P/Invoke, or
numeric CPU loop. Existing SIMD preprocessing is unchanged. No speed claim is made.

## Authored qualification

- `GpuHitTestingTests`: unchanged geometry, preserved flags through bounds/clip
  copies, invalid masks, and GPU point single/list, rectangle and ellipse queries.
- `NativeRendererInteropTests`: wire preservation/allocation fixture for each
  mode and admission rejection for conflicting/unknown flags.
- Native scene-builder fixture: raw scene validation, builder admission and
  retained hash changes for the same masks.
- Native package consumer: original owner snapshot, single/list and both region
  modes through the native compositor.
- Dawn provider fixture: matched query-kind behavior through the C API.

Fixtures are authored, not executed. Compilation is not shader/runtime validation.
Final platform, shader, package, module/header, performance and PR gates remain.

## Historical prerequisite boundary

This checkpoint does **not** publish TextBlock input rectangles yet. Continue the
same MVP action by exposing an authoritative source-owned point-hit descriptor,
retaining it through batched MIL metadata and managed command snapshots, and
emitting point-only rectangle coverage plus region-only drawing coverage for that
visual. Preserve visual transforms, source clips, descendants, invalidation and
the source IContentHost promotion used to locate inline content. Empty text and
glyph overhang require paired fixtures: adding a rectangle without excluding the
visual's drawing from point queries is not sufficient. Do not infer this policy
from a family/type name or assign arranged rectangles to arbitrary visuals.

## Source connection — 2026-09-09

`IPortablePointHitRegionSource` now supplies an authoritative own-content rectangle.
LibreWPF's source TextBlock publishes its actual RenderSize through that interface;
ordinary visuals do not acquire this policy. The native compiler collects sorted
`NativeMilPointHitRectangle` records and sends one complete replacement snapshot
per changed batch, not one native call per control. The public C record is the
generated C# layout authority (40 bytes, fixed-width handle/reserved fields and
four doubles). Native code copies the synchronous caller span before returning.

The channel validates live visual handles, strict ordering, reserved zeros, finite
coordinates/endpoints and nonnegative extents before swapping its sparse map.
Failed updates preserve the previous map. Empty snapshots clear it; resource
deletion removes the matching entry. Input-only changes invalidate the compiled
scene cache and the resulting primitive bytes participate in normal index identity.
LibreWPF's incremental session owns comparison bytes, so mutation of producer
arrays cannot erase a change. No point rectangle is encoded as a painted MIL draw.

Native scene-builder save metadata emits a PointOnly rectangle using the actual
visual transform/clip and retains the enclosed drawing as RegionOnly. Restore
ends the policy before descendants. Existing image scopes continue replacing
both query kinds; nested point scopes cannot reenable points inside a region-only
scope. Empty content and zero-sized rectangles retain their explicit input
descriptor without producing ink. Unsupported outer masks/caches remain rejected.

Managed command snapshots retain equivalent `PointRectangleBegin/End` annotations
on identity opacity scopes. The hit-index builder consumes those markers without
changing raster bounds or opacity. Source-command visuals no longer also publish
generic Size rectangles; actual commands inherit their source owner when needed.
Layer descendant traversal uses the existing typed source command traversal rather
than size-based stand-ins. Root cached/effect texture input remains a separate
coverage gap; this change does not assert complete input for those configurations.

Provenance is ProGPU's existing logical image scopes, source opacity policy,
retained source traversal and canonical hit-primitive encoder, extended in place.
The [existing cross-engine decisions](native-mil-hit-test-ownership.md#design-references-and-decisions)
still apply: source interaction metadata is separate from painted display lists;
font shaping and layout are not duplicated by input adaptation. No third-party
implementation was imported. This metadata path adds no pixel readback or CPU
geometry fallback. Dictionary ownership and balanced scope traversal are
dependency-bound; byte comparisons use the runtime's span comparison. Existing
SIMD geometry transforms and GPU query execution remain shared. No speed claim
is made before final measurements.

Additional authored fixtures cover paired point/drawing/child flags, empty source
content, overhang, transforms/clips, snapshot cloning, balanced scopes and invalid
coordinates, managed source visual size exclusion at zero/full opacity, native
transactional replacement/clear, and the import-based builder surface. LibreWPF
adds sideband-only layout updates and a source-built native host assertion that
blank TextBlock space is a point hit but not a geometry-region hit. Tests are not
executed before feature freeze. Build results and artifact provenance belong in
LibreWPF's `reports/native-mil-source-point-region-2026-09-09.md`.
