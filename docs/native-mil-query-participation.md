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

## Remaining application connection

This checkpoint does **not** publish TextBlock input rectangles yet. Continue the
same MVP action by exposing an authoritative source-owned point-hit descriptor,
retaining it through batched MIL metadata and managed command snapshots, and
emitting point-only rectangle coverage plus region-only drawing coverage for that
visual. Preserve visual transforms, source clips, descendants, invalidation and
the source IContentHost promotion used to locate inline content. Empty text and
glyph overhang require paired fixtures: adding a rectangle without excluding the
visual's drawing from point queries is not sufficient. Do not infer this policy
from a family/type name or assign arranged rectangles to arbitrary visuals.
