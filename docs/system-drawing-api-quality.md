# ProGPU System.Drawing API, Quality, and Performance Contract

## Objective

`ProGPU.System.Drawing.Common` is the portable `System.Drawing.Common` implementation used by LibreWinForms. Public API presence, managed behavior, rendering quality, and hot-path performance are one compatibility contract.

The implementation remains clean-room. Official reference assemblies and documentation define public contracts and observable behavior; implementation code is original ProGPU code built on typed ProGPU services. Upstream implementation source must not be copied or mechanically ported into this repository.

## Pinned API contract

The current contract is `System.Drawing.Common.dll` from `Microsoft.WindowsDesktop.App.Ref` 10.0.11. The repository pins Microsoft ApiCompat 10.0.400 through the local tool manifest.

Run:

```bash
./eng/progpu-verify-system-drawing-api.sh
```

The verifier:

1. restores the pinned reference pack and local ApiCompat tool;
2. builds the exact Release implementation assembly;
3. writes the complete current diagnostic report under `artifacts/system-drawing-api-compat`;
4. prints missing-type, missing-member, other-shape, and total counts;
5. rejects every incompatibility not present in the reviewed suppression file; and
6. rejects stale suppressions when an incompatibility has been fixed.

Only regenerate the baseline after reviewing the complete diff:

```bash
./eng/progpu-verify-system-drawing-api.sh --update-baseline
```

The suppression file is debt, not acceptance of permanent incompatibility. Pull requests should normally remove suppressions and must never add suppressions merely to make CI green.
Baseline regeneration removes machine-specific left/right assembly paths, so suppressions are keyed by diagnostic and API target and behave identically in local clones and hosted CI. The verifier rejects a committed baseline that still contains absolute assembly paths.

## Current measured debt

After the component-model converter, hosted graphics-flush, graphics-state, point/source-rectangle and destination-point image-overload, coordinate-space, graphics-container, image-convenience, drawing-identity, brush-base, pen-ownership, stock-icon, printer-settings collection, image-attributes, page device-selection, managed printing-shape, effects, cached-bitmap, managed-metadata, managed-identity, pen-transform, typed-LOGFONT, custom-cap/compound-pen, path-gradient, metafile parser, metafile enumeration, type-scoped bitmap-resource, cumulative graphics-context, managed icon-extraction, managed serialization/base-shape, typed desktop-capture, typed native-image-import, typed native font/graphics interop, portable metafile-comment recording, and bounded typed EMF/WMF vector playback compatibility slices:

| Diagnostic group | Count |
| --- | ---: |
| Missing types (`CP0001`) | 0 |
| Missing members (`CP0002`) | 0 |
| Other shape diagnostics | 13 |
| Total | 13 |

The starting measured baseline was 121 missing types, 906 missing members, 25 other diagnostics, and 1,052 total. Completing coherent resource, graphics, imaging, matrix, brush, path, text/font, icon, buffered-graphics, printing, and component-model converter groups, followed by typed graphics-flush, graphics-state, and point/source-rectangle image-overload boundaries, reduced the current debt to 48 missing types, 292 missing members, 47 other diagnostics, and 387 total. The first image-overload slice adds point/integer placement, unscaled/clipped drawing, source-rectangle-at-point drawing, and float-source rectangle callback/attributes overloads over the existing typed retained-texture path. It preserves exact source pixels and dimensions, clips without stretching, applies image remapping, and honors abort callbacks before recording; no screen capture, HDC, or platform bitmap is introduced. The destination-point follow-up adds all ten array overloads with affine parallelogram and homogeneous perspective-quad mapping in the managed GPU renderer, exact affine native lowering, and explicit native rejection for projective commands the current native wire cannot represent. The graphics-state slice adds the official `CompositingMode` identity, source-over/source-copy state, rendering origin, text contrast, allocation-free `TransformElements`, ordered world-transform overloads, and rectangle visibility overloads. `SourceCopy` records a balanced typed `GpuBlendMode.Src` scope, survives intermediate flushes, and replaces destination alpha in production bitmap rendering. Rendering origin shifts the retained 8×8 hatch coordinate space with a two-float payload, preserving the existing bounded hatch-lowering allocation gate. Save/restore retains the new state; vector text keeps `TextContrast` as validated compatibility state because its glyph coverage is not GDI raster contrast. `Drawing2D.FlushIntention` and both official `Graphics.Flush` overloads have functional bitmap and hosted-recorder behavior rather than API-only storage: batches are balanced before handoff, persistent clip and compositing state is restored for subsequent drawing, `Sync` polls the explicit WebGPU device, and a recorder without a submission target fails at an explicit boundary. The formatted-text slice removes sixteen exact member suppressions by completing the official string/span draw and measurement entry points and routing measurable ranges through typed shaped-cluster selection geometry. Wrapped lines, alignment offsets, clipped versus `NoClip` bounds, empty-span validation order, and bounded warmed measurement allocation now have focused gates. The retained-primitive slice removes 56 exact member suppressions by routing every official arc, Bézier, cardinal/closed-curve, pie, rectangle, rounded-rectangle, and fill-rule overload—including the .NET 10 span surface—through typed `GraphicsPath`/`PathGeometry` and analytic rectangle commands. `Font`, `FontFamily`, `FontCollection`, `GenericFontFamilies`, `InstalledFontCollection`, and `PrivateFontCollection` now use exact typed ProGPU catalog resolution, owned private file/memory faces, real OpenType metrics, canonical overload/base/interface shapes, independent snapshots, explicit fallback identity, and allocation-free warmed metric reads. Native GDI pointer interfaces remain reviewed platform-boundary debt. `HatchBrush` and the complete `HatchStyle` enum lower all 53 concrete styles to deterministic two-color 8×8 tiles consumed by both managed and native ProGPU paths. The sealed `TextureBrush` now supplies every official constructor, clone/interface shape, mutable wrap mode, and transform operation over an owned bitmap snapshot. Rectangle, ellipse, path, polygon, curve, rounded-rectangle, and region fills share typed texture commands and rectangular or retained-geometry clips; tile, mirror-X, mirror-Y, mirror-XY, clamp, crop, remap, color-matrix, brush transform, and graphics transform behavior are applied instead of stored or ignored. The imaging slice includes the official `ColorMap`, `ColorPalette`, `PaletteFlags`, `PaletteType`, `DitherType`, complete `PixelFormat` and `ImageFormat` identities, `PropertyItem`, `Encoder`, `EncoderParameter`, `EncoderParameters`, and truthful managed `ImageCodecInfo` discovery shapes; defensively snapshotted/cloned image metadata, codec descriptors, and `ImageAttributes` state; behaviorally applied bitmap and palette remap/matrix operations rather than API-only storage; CPU-only image resolution/tag/frame/bounds contracts; deterministic fixed and optimal palette generation; typed scan0/caller-buffer pixel-memory conversion across packed, indexed, premultiplied, and high-depth formats; functional `ConvertFormat` palette, alpha-threshold, ordered/spiral/error-diffusion, and reduced-direct-color quantization; and managed PNG/BMP/JPEG encoding with typed JPEG quality selection. `Drawing2D.Matrix` now has its official base/sealed shape and functional parallelogram, composition, pivot, shear, inverse, point/vector, array/span, value, cloning, and disposal contracts. `Blend`, `ColorBlend`, and `LinearGradientBrush` now provide the official public surface plus functional scalable-angle geometry, state ownership, transforms, gamma/spread mapping, custom stops, and renderable triangular/bell falloffs. `GraphicsPath`, `PathData`, `PathPointType`, and `GraphicsPathIterator` now expose source-compatible path construction, shaped text outlines, cardinal curves, clone/composition, point/type export and iteration, analytic bounds, transforms, fill and outline hit-testing, widening, perspective/bilinear warping, reversal, and adaptive flattening directly over retained ProGPU geometry. The missing-member and other-shape subtotals are not monotonic: once a formerly absent type is added, ApiCompat can report the still-missing members and shape details on that type. The committed suppression file is the reviewed current debt and the gate rejects both new and stale suppressions.

The first metafile checkpoint restores the complete eight-type public identity
group and every official `Metafile` constructor/header/handle/play-record member.
File and stream construction now snapshots and transactionally parses bounded
placeable-WMF, standard-WMF, EMF, and initial embedded EMF+ record tables;
header queries, cloning, non-seekable streams, checksum/signature/alignment/count
validation, and explicit Windows handle/HDC seams have focused gates. The
second checkpoint restores all 36 `Graphics.EnumerateMetafile` overloads over
the owned record table. It pins the source snapshot for the callback lifetime,
exposes payload spans without per-record copies, preserves EMF+ source order,
stops on `false`, validates all destination/source overload families, and
matches the managed adapter's null playback-callback behavior. Focused tests
execute every overload and enforce a warmed 4,098-record allocation ceiling.
This reduces measured debt further from 54 to 18 missing members and from 69
to 33 total diagnostics. The portable comment-recording checkpoint adds
`ProGPU.SystemDrawing.PortableMetafile.Create`, an HDC-free, caller-owned stream
target, exclusive `Graphics.FromImage` recording lifetime, and functional
`Graphics.AddMetafileComment`. It encodes bounded, aligned EMF+ comment records
inside a valid EMF transport, reparses the owned output before publication,
and leaves the caller's stream open. Input arrays are copied immediately;
read-only targets, invalid bounds, concurrent/repeated recorders, incomplete
headers, and disposed owners fail explicitly. Ordinary drawing records are not
silently discarded: this initial encoder aborts without writing when its
retained command list is nonempty. The checkpoint removes the last missing
member suppression, leaving zero missing types, zero missing members, and 13
    reviewed shape diagnostics. It does not claim drawing-record encoding.
    The following direct-playback slice adds transactional `Graphics.DrawImage`
    lowering for an initial bounded EMF vector family: affine destination/source
    mapping; `MM_TEXT` and anisotropic window/viewport state; world transforms;
    save/relative restore including clip state; fill/background state and
    `R2_COPYPEN`; move/line, rectangle, ellipse,
    polygon/polyline/poly-polygon/poly-polyline; intersect-clip rectangles; and
    solid/null cosmetic pens and brushes with stock/dynamic object selection.
    The parser accepts the bounded legacy EMF record-count convention that
    excludes `EMR_HEADER`, as used by a canonical WinForms test asset, while
    rejecting every other count mismatch. Unsupported or malformed records report their type and
    source offset and publish no partial commands. A follow-up WMF path decodes
    16-bit Y/X state parameters, uses the required lowest-free object-table
    allocation, and supports the state, solid/null pen/brush, polygon, polyline,
    and counterclockwise arc records used by the canonical LibreWinForms
    `telescope_01.wmf` asset, plus filled/stroked rectangles and ellipses and
    rounded rectangles, pies, chords, poly-polygons, current-position lines,
    explicit-color device pixels, and exact pattern-copy/blackness/whiteness
    rectangle blits, plus intersect/exclude/offset rectangle clip
    state. Typed `CREATEFONTINDIRECT`, `SETTEXTCOLOR`, and charset-decoded
    `TEXTOUT` add selected font/color output, alignment/current-position state,
    and transparent or measured opaque backgrounds. `EXTTEXTOUT` adds explicit
    opaque/clipped rectangles, RTL layout without explicit advances, and signed
    one-byte-character advances. WMF SaveDC and relative RestoreDC
    snapshot window/viewport origins and extents, current point, world
    transform, fill/map/background/raster/text/background-color settings,
    selected pen, brush, and font, text color, and the typed `GraphicsState` clip; restoration
    therefore removes inner clip changes without losing the outer clip. Typed
    `MM_TEXT`/`MM_ANISOTROPIC` state now includes set/offset origins and y-first
    ratio scaling for both window and viewport extents. Four-point
    perspective, image attributes, paths,
    `EXTTEXTOUT` glyph-index, numeric-substitution, two-dimensional, DBCS-
    advance, and bidi-advance modes, transformed or decorated fonts, SYMBOL
    glyph-index mapping, DIBs, other WMF drawing families, and nonstructural EMF+ drawing remain
    explicit follow-up work. Contract, security bounds, and benchmark evidence are recorded
in
[`docs/research/system-drawing-metafile-contract.md`](research/system-drawing-metafile-contract.md).

The type-scoped bitmap-resource slice restores `Bitmap(Type, string)` as a
functional managed path for designer and control artwork embedded beside its
owning type. It performs the exact case-sensitive namespace-scoped manifest
lookup, decodes through the existing owned ProGPU bitmap pipeline, and closes
the resource stream before construction returns. The lookup is the API's
explicit typed contract, not an assembly scan or shape probe. This removes one
exact member suppression, reducing measured debt to 17 missing members and 32
total diagnostics. Contract and focused-test evidence is recorded in
[`docs/research/system-drawing-bitmap-resource-contract.md`](research/system-drawing-bitmap-resource-contract.md).

The cumulative graphics-context slice restores all three `GetContextInfo`
members. It tracks the transform active when each clip is applied, composes
saved contexts in stack order, returns an independently owned cumulative clip,
and keeps the offset-only overload allocation-free when warm. The legacy
object-array form retains its official obsolete marker and returns a `Region`
plus full `Matrix`; the newer clip overload returns null for an infinite clip.
This removes three exact member suppressions, reducing measured debt to 14
missing members and 29 total diagnostics. Contract, canonical WinForms usage,
and focused-test evidence is recorded in
[`docs/research/system-drawing-graphics-context-contract.md`](research/system-drawing-graphics-context-contract.md).

The managed icon-extraction slice restores all three `ExtractAssociatedIcon`
and `ExtractIcon` members over bounded ICO and PE-resource parsing. It supports
zero-based group indices, negative numeric group-resource identifiers,
closest-frame size selection and resampling, source-independent owned pixels,
and managed-image fallback for associated icons. The parser validates every PE
header, resource-directory, RVA, and payload extent and never loads or executes
the source file. Shell associations and native HICON transport remain explicit
platform work. This removes three exact suppressions, reducing measured debt to
11 missing members and 26 total diagnostics. Contract and focused-test evidence
is recorded in
[`docs/research/system-drawing-icon-extraction-contract.md`](research/system-drawing-icon-extraction-contract.md).

The managed serialization/base-shape slice restores the canonical
`MarshalByRefObject` base for `Graphics` and `Icon`, plus owned `ISerializable`
contracts for `Icon` and `Image`. Icon snapshots retain the canonical
`IconData`/`IconSize` fields; bitmaps retain the canonical `Data` field and use
the existing managed encoder, while metafiles retain their validated owned
source. The native `IGraphics`, `IImage`, `IPointer`, and
HDC handle shapes remain explicit adapter debt rather than empty lookalikes.
Completing `Icon` serialization exposes its separate internal
`IIcon : IHandle<HICON>` diagnostic, so the type-level native-shape suppression
remains. This removes two reviewed base-type suppressions, reducing other
diagnostics to 13 and total diagnostics to 24. Contract and focused-test
evidence is recorded in
[`docs/research/system-drawing-managed-serialization-shape-contract.md`](research/system-drawing-managed-serialization-shape-contract.md).

The typed desktop-capture slice restores all four `Graphics.CopyFromScreen`
overloads over a process-scoped `ProGPU.SystemDrawing.IDesktopCaptureService`.
The provider fills caller-owned exact-length RGBA8 storage and cannot retain
its span; the retained drawing command owns the captured pixels after return.
SourceCopy plus the documented capture/no-mirror modifiers is functional.
Missing providers and destination/pattern-dependent raster operations fail at
explicit typed boundaries instead of importing an HDC or rendering a fake
application-only desktop. This removes four exact member suppressions,
reducing measured debt to 7 missing members and 20 total diagnostics. Contract,
ownership, platform-boundary, and allocation evidence is recorded in
[`docs/research/system-drawing-desktop-capture-contract.md`](research/system-drawing-desktop-capture-contract.md).

The typed native-image-import slice restores `Bitmap.FromHicon` and
`Bitmap.FromResource` and makes the already-present `Icon.FromHandle` image
path functional. A process-scoped `INativeImageImportService` receives the
original handle/name and writes exactly one positive, exact-length RGBA8 image
into a guarded destination. The destination synchronously copies provider
storage and becomes inactive after return, so the resulting bitmap owns its
pixels without retaining an HICON, module, resource pointer, or provider
buffer. Missing, duplicate, late, and incorrectly sized writes fail explicitly;
missing providers remain a typed local-OS boundary. This removes two exact
member suppressions, reducing measured debt to 5 missing members and 18 total
diagnostics. Contract, ownership, validation, and allocation evidence is
recorded in
[`docs/research/system-drawing-native-image-import-contract.md`](research/system-drawing-native-image-import-contract.md).

The typed native font/graphics interop slice restores `Font.FromHdc`,
`Graphics.FromHdcInternal`, `Graphics.FromHwndInternal`, and
`Graphics.GetHalftonePalette`, and replaces the previous placeholder behavior
of the public HDC/HWND entries. Independent process-scoped
`INativeFontInteropService` and `INativeGraphicsInteropService` contracts carry
exact native handles into explicit local-OS adapters. Adapters return owned,
typed `Font` or `Graphics` products and may preserve ProGPU bounds, transforms,
flush, target-device, and completion contracts; zero HDCs, missing providers,
and null products fail explicitly. Portable tests now create retained recorders
through `FromProGpuDrawingContext` instead of treating a zero HWND as an empty
fake window. This removes four exact member suppressions, reducing measured
debt to 1 missing member and 14 total diagnostics at that checkpoint. The
subsequent portable metafile-comment recorder removes that final missing member.
Contract, lifetime, boundary, and allocation evidence is recorded in
[`docs/research/system-drawing-native-interop-contract.md`](research/system-drawing-native-interop-contract.md).

`MetafileBenchmarks.Enumerate4098RecordsWithoutPayloadCopies` guards the owned,
pinned callback walk independently from parsing. The 2026-08-27 ARM64/.NET
10.0.11 ShortRun measured a 1.593 microsecond median (1.495 microsecond mean,
0.177 microsecond standard deviation) with zero managed allocation. The focused
suite independently executes all 36 overloads and permits at most 4,096 bytes
    across sixteen warmed 4,098-record walks. This remains enumeration evidence;
    direct rendering has its own gate below.

`MetafileBenchmarks.Playback256RectanglesToRetainedCommands` measures typed EMF
record traversal, state lowering, transactional temporary recording, append,
and cleanup. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 154.013
microsecond median (163.161 microsecond mean, 32.602 microsecond standard
deviation) and 305.26 KB allocation for 256 filled rectangles. This first
coarse baseline includes transactional command/resource ownership and is an
optimization target, not a zero-allocation claim. Focused gates independently
verify pixels, destination transforms, saved/map/world state, explicit feature
boundaries, saved clip and multi-polygon behavior, and rollback after partial
temporary lowering. A local unchanged-asset smoke renders the canonical
WinForms `milkmateya01.emf` fixture end to end; the repository-owned synthetic
gates preserve the same required record families for standalone ProGPU CI.

`MetafileBenchmarks.Playback256WmfRectanglesToRetainedCommands` guards the shared ordered-box decoder and typed selected brush/pen lowering. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 757.639 µs median (753.507 µs mean, 139.549 µs standard deviation) with 622.08 KB allocated for 256 rectangles. The three-iteration result is coarse transactional retained-command evidence; exact selected-fill pixels and shared malformed-bound rollback remain the correctness gates.

`MetafileBenchmarks.Playback256WmfRectanglesWithClipState` wraps that fixture in
an outer intersect clip and a saved inner exclude scope restored halfway
through the 256 records. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured a 561.572 µs median (599.013 µs mean, 103.320 µs standard deviation)
with 628.33 KB allocated. Three iterations make this coarse state-lowering
evidence; independent inside, excluded-hole, restored-clip, intersection-edge,
invalid-relative-level, and transactional-rollback gates remain authoritative.
The complete drawing suite passes 419/419, and ApiCompat remains at zero
missing types, zero missing members, and 13 reviewed platform annotations.

`MetafileBenchmarks.Playback256WmfEllipsesToRetainedCommands` guards typed WMF ellipse playback through the selected fill and outline objects. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 1.060 ms median (1.109 ms mean, 0.115 ms standard deviation) with 622.14 KB allocated for 256 ellipses. The three-iteration result is coarse retained-command evidence; exact pixels and rollback after a later unsupported `STRETCHBLT` record remain the independent correctness gates.

`MetafileBenchmarks.Playback256WmfRoundRectanglesToRetainedCommands` guards the
official height/width plus bottom/right/top/left parameter order and typed
rounded-geometry lowering through the selected fill and outline objects. The
2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 1.347 ms median
(1.379 ms mean, 0.234 ms standard deviation) with 1.05 MB allocated for 256
rounded rectangles. The three-iteration result is coarse curve-lowering
evidence; exact center, antialiased outline, transparent-corner, zero-corner
rectangle fallback, and invalid-bound rollback gates remain authoritative.

`MetafileBenchmarks.Playback256WmfPiesToRetainedCommands` and
`Playback256WmfChordsToRetainedCommands` guard the shared official radial2,
radial1, and bottom/right/top/left parameter order while measuring the distinct
center-radial and straight-chord closures. The 2026-08-31 ARM64/.NET 10.0.11
in-process ShortRun measured pies at a 1.382 ms median (1.621 ms mean, 0.785 ms
standard deviation) with 816.23 KB allocated, and chords at a 792.480 µs median
(946.270 µs mean, 284.554 µs standard deviation) with 800.03 KB allocated.
Three high-variance iterations make these coarse curve-lowering checkpoints;
independent closure pixels and invalid-chord rollback after an earlier valid pie
remain the authoritative correctness evidence.

`MetafileBenchmarks.Playback256WmfLinesToRetainedCommands` guards selected-pen
lowering and logical current-position progression. The 2026-08-31 ARM64/.NET
10.0.11 in-process ShortRun measured a 503.124 µs median (477.934 µs mean,
206.828 µs standard deviation) with 323.97 KB allocated.
`Playback256WmfSetPixelsToRetainedCommands` guards explicit `COLORREF` decoding
and one-device-pixel output after the complete graphics transform; it measured
a 199.155 µs median (199.350 µs mean, 14.387 µs standard deviation) with 305.70
KB allocated. Three iterations make the line result high-variance coarse
evidence and the pixel result a local checkpoint. Exact scaled pixels,
SaveDC/RestoreDC current-point behavior, and rollback after both supported
records remain the correctness gates.

`MetafileBenchmarks.Playback256WmfPolyPolygonsToRetainedCommands` guards the
unsigned WMF polygon-count arrays and selected fill/outline lowering for two
closed figures per record. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured a 2.405 ms median (2.542 ms mean, 0.463 ms standard deviation) with
1.85 MB allocated for 256 records and 512 polygons. Three iterations make this
coarse evidence and expose array/path allocation as an optimization target.
Exact disjoint pixels, unchanged current-position behavior, invalid count
rejection, and rollback after a later unsupported record remain the correctness
gates.

`MetafileBenchmarks.Playback256WmfMappedPixelsWithViewportState` guards 256
balanced cycles of signed window/viewport origin offsets, y-first window and
viewport extent ratios, and transformed one-device-pixel output. The 2026-08-31
ARM64/.NET 10.0.11 in-process ShortRun measured a 155.282 µs median (156.556 µs
mean, 3.099 µs standard deviation) with 305.71 KB allocated. Exact pixels cover
`MM_ANISOTROPIC`, set/offset/scale composition, and SaveDC/RestoreDC; a zero
denominator rollback gate remains the correctness authority.

`MetafileBenchmarks.Playback256WmfPatternCopiesToRetainedCommands` guards exact
selected-brush `PATCOPY` lowering. The 2026-08-31 ARM64/.NET 10.0.11 in-process
ShortRun measured a 133.616 µs median (135.580 µs mean, 16.236 µs standard
deviation) with 305.88 KB allocated for 256 records. Exact `PATCOPY`,
`BLACKNESS`, and `WHITENESS` pixels remain the correctness gate;
destination-dependent `PATINVERT` fails explicitly and transactionally until a
typed destination-read/compositing seam exists.

`MetafileBenchmarks.Playback256WmfPatternCopiesWithOffsetClipState` guards 256
pattern fills surrounded by 512 balanced signed logical clip translations. The
2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun measured a 4.148 ms median
(4.425 ms mean, 1.005 ms standard deviation) with 2.12 MB allocated. Three
high-variance iterations expose Region clone/path repush allocation as an
optimization target; exact old/moved/restored pixels and later-record rollback
remain the correctness gates.

`MetafileBenchmarks.Playback256WmfTextOutToRetainedCommands` guards a selected
WMF font and 256 charset-decoded `TEXTOUT` records through typed measurement,
brushes, and retained glyph commands. The 2026-08-31 ARM64/.NET 10.0.11
in-process ShortRun measured an 884.902 µs median (912.665 µs mean,
279.158 µs standard deviation) with 562.05 KB allocated. Five iterations make
this high-variance coarse evidence. Colored glyph/background pixels, restored
font and text-color state, and invalid-alignment rollback are the independent
correctness gates; per-record measurement and transient brushes remain explicit
optimization debt.

Playback now reuses its typed foreground and opaque-background `SolidBrush`
until the corresponding canonical color changes. The same five-iteration local
ShortRun reduced allocation from 562.05 KB to 550.25 KB per operation (11.80
KB, 2.1%). The rerun's 1.140 ms median and 1.494 ms mean were much noisier, so
only the allocation reduction is claimed; per-record measurement remains the
larger explicit text-playback optimization target.

`MetafileBenchmarks.Playback256WmfExtTextOutWithClipAndAdvances` guards 256
official `EXTTEXTOUT` layouts with opaque/clipped Rect objects and three signed
character advances each. The 2026-08-31 ARM64/.NET 10.0.11 in-process ShortRun
measured a 5.874 ms median (5.929 ms mean, 0.970 ms standard deviation) with
3.28 MB allocated across five iterations. Exact clip/background/spaced-glyph
pixels, current-position progression, malformed arrays, unsupported options,
and rollback remain the correctness gates. Per-character shaping, fragmented
glyph commands, and clip-state ownership are explicit optimization debt.

The typed follow-up shapes each string once, remaps its cluster origins to the
requested character cells, preserves fallback/mark offsets, and emits one glyph
run per resolved font. A command-level gate proves two 20-unit-spaced characters
remain one run. The comparable five-iteration ShortRun improved to a 5.227 ms
median (5.474 ms mean, 0.527 ms standard deviation) and 2.66 MB allocated,
reducing median by 0.647 ms (11.0%) and allocation by 0.62 MB (18.9%). Repeated
layout/caret construction and Region clip state remain explicit debt.

`MetafileBenchmarks.RecordAndFinalize256PortableComments` measures construction,
256 owned 64-byte comment copies, bounded EMF+ encoding, validation through the
same parser used by consumers, and final stream publication. The 2026-08-27
ARM64/.NET 10.0.11 ShortRun measured an 11.346 microsecond median (11.348
microsecond mean, 0.406 microsecond standard deviation) and 150.72 KB allocated
for the complete 19 KB owned document and typed record tables. This is coarse
one-launch throughput/allocation evidence, not a rendering or zero-allocation
claim; recording cost is intentionally linear in encoded bytes and record count.

The preceding synthesis paragraph's 387 subtotal records the immediately prior image-overload checkpoint; the table above is authoritative for the current head. The coordinate-space slice adds the official `Drawing2D.CoordinateSpace` identity and all four array/span `Graphics.TransformPoints` entry points. World, page, and device conversion uses the same world, page-unit/page-scale, and host base matrices used by retained drawing. Caller-owned storage is mutated in place without allocation; invalid spaces, empty inputs, non-invertible destinations, and disposed graphics fail explicitly. This slice reduces the current measured debt to 47 missing types, 288 missing members, 47 other diagnostics, and 382 total. Contract evidence is recorded in [`docs/research/system-drawing-graphics-coordinate-space-contract.md`](research/system-drawing-graphics-coordinate-space-contract.md).

The effects slice source-reuses the complete 23-type .NET 10 `System.Drawing.Imaging.Effects` public model and adds `Bitmap.ApplyEffect` plus both effect-aware `Graphics.DrawImage` overloads. The Windows GDI+ effect handle is replaced at the platform seam by typed ProGPU bitmap execution: pointwise matrices, lookup tables, curves, levels, tint, brightness, and balance operate in one allocation-free warmed pixel pass, while blur and sharpen use pooled, separable linear-time box passes. CPU-resident bitmaps remain CPU-only; a materialized GPU texture crosses one explicit readback/writeback boundary. Effect drawing snapshots the source, maps the selected rectangle through the typed affine transform, composes image attributes, and retains the result without mutating caller storage. Rectangle clipping, premultiplied-alpha conversion, construction snapshots, disposal, validation, representative canonical pixels, and allocation behavior have focused gates. This slice reduces measured debt from 40 missing types, 127 missing members, 17 other diagnostics, and 184 total to 17 missing types, 124 missing members, 17 other diagnostics, and 158 total. Contract and architecture evidence is recorded in [`docs/research/system-drawing-effects-contract.md`](research/system-drawing-effects-contract.md).

The cached-bitmap slice restores the official `System.Drawing.Imaging.CachedBitmap` type and `Graphics.DrawCachedBitmap` member over an immutable, device-bound ProGPU texture snapshot. Typed resource leases preserve deferred-command lifetime and reuse one retained texture across repeated draws; caller transforms are limited to translation without treating the host base transform as caller state. This slice reduces measured debt to 16 missing types, 123 missing members, 17 other diagnostics, and 156 total. Contract, ownership, and performance evidence is recorded in [`docs/research/system-drawing-cached-bitmap-contract.md`](research/system-drawing-cached-bitmap-contract.md).

The managed-metadata slice restores the missing `BitmapSuffixInSameAssemblyAttribute`, `System.Drawing.Design.CategoryNameCollection`, and `System.Drawing.Imaging.ColorMode` identities and corrects the existing satellite bitmap-suffix attribute from sealed to inheritable. These contracts are wholly managed and require no GDI+, renderer, reflection-based product path, or local-OS adapter. This slice reduces measured debt to 13 missing types, 123 missing members, 16 other diagnostics, and 152 total. Contract and focused-test evidence is recorded in [`docs/research/system-drawing-managed-metadata-contract.md`](research/system-drawing-managed-metadata-contract.md).

The managed-identity completion slice restores every official `CopyPixelOperation` value and corrects `ToolboxBitmapAttribute` to its inheritable .NET 10 shape. Desktop capture remains a separate typed local-OS service boundary rather than an HDC operation hidden inside ProGPU. This slice reduces measured debt to 12 missing types, 123 missing members, 15 other diagnostics, and 150 total. Contract and focused-test evidence is recorded in [`docs/research/system-drawing-managed-identity-completion-contract.md`](research/system-drawing-managed-identity-completion-contract.md).

The pen-transform slice restores all eleven `Pen.Transform` property and operation members with defensive managed matrix ownership. Anisotropic tip geometry uses `P × stroke(P⁻¹ × path)`, carrying the existing width, joins, caps, and dashes through one typed widen/fill path shared by rendering, `GraphicsPath.Widen`, bounds, and outline hit testing. Translation remains public matrix state but does not move the pen tip; singular transforms produce no fabricated stroke. Focused gates include production bitmap pixels and zero allocation across warmed transform mutations, and `GraphicsPathBenchmarks.WidenAnisotropicPenClone` tracks the geometry cost. This slice reduces measured debt to 12 missing types, 112 missing members, 15 other diagnostics, and 139 total. Contract evidence is recorded in [`docs/research/system-drawing-pen-transform-contract.md`](research/system-drawing-pen-transform-contract.md).

The typed-LOGFONT slice restores the exact 92-byte Unicode `System.Drawing.Interop.LOGFONT` identity and all eight `Font` conversion members. Typed imports and exports carry face, vertical identity, charset, logical height, weight, and style through managed ProGPU font resolution; graphics-aware export uses the typed DPI contract. Boxed canonical values remain source-compatible without reflection, while arbitrary lookalike object layouts are rejected and HDC-aware selection stays at an explicit Windows GDI adapter boundary. Nine focused tests include exact layout, invalid/default selection, boxed mutation, lifetime behavior, and zero allocation across 10,000 warmed typed exports. This slice reduces measured debt to 11 missing types, 104 missing members, 15 other diagnostics, and 130 total. Contract evidence is recorded in [`docs/research/system-drawing-logfont-contract.md`](research/system-drawing-logfont-contract.md).

The custom-cap and compound-pen slice restores `CustomLineCap`, `AdjustableArrowCap`, and all six `Pen.CompoundArray`/custom-cap accessors with defensive ownership and upstream-compatible state validation. Compound fractions lower to typed offset stroke bands with retained run metadata and physical dash scale. Generic fill/stroke caps and adjustable arrows use endpoint-local retained geometry shared by rendering, widening, bounds, and outline hit testing; nonfinite public state is preserved but never converted into fabricated renderer geometry. Twelve focused tests cover production pixels, center gaps, orientation, inset, ownership, disposal/state contracts, and allocation bounds. This slice reduces measured debt to 9 missing types, 98 missing members, 15 other diagnostics, and 122 total. Alternate-fill nested-cap normalization and Windows GDI+ acute-offset differentials remain explicit rendering-quality work. Contract evidence is recorded in [`docs/research/system-drawing-custom-cap-compound-pen-contract.md`](research/system-drawing-custom-cap-compound-pen-contract.md).

The path-gradient slice restores the complete .NET 10 `Drawing2D.PathGradientBrush` surface and lowers it to a typed retained ProGPU material used by fills, pens, text, portable pictures, and native scene compilation. The shared shader intersects the fragment's center ray with the retained polygon boundary, interpolates per-edge surround colors, applies focus scales and blend or preset-color curves, and preserves the common clamp/repeat/reflect/decal policy without substituting a bounding ellipse. Managed and standalone C++ validators enforce an exact, finite, pointer-free record layout capped at 128 boundary vertices. Eight focused managed tests, a discriminating headless GPU pixel test, portable-picture round-trip coverage, native compiler snapshots, and the native C++ internal suite guard behavior and transport. This slice removes the final ordinary managed renderer-type suppression and reduces measured debt to 8 missing types, 98 missing members, 15 other diagnostics, and 121 total. Concave/self-intersecting and multi-figure Windows image differentials remain explicit rendering-quality work. Contract evidence is recorded in [`docs/research/system-drawing-path-gradient-contract.md`](research/system-drawing-path-gradient-contract.md).

The graphics-container slice adds the official sealed `Drawing2D.GraphicsContainer` token and every `BeginContainer`/`EndContainer` member. Parent transforms and clips remain effective through typed hidden container state while the public world transform, clip, page, and rendering-quality properties reset to official defaults. Rectangle containers map source units into destination coordinates; nested containers, `Save` scopes, cross-instance and reused tokens, restore invalidation, and disposal balance are explicit. This slice reduces the current measured debt to 46 missing types, 284 missing members, 47 other diagnostics, and 377 total. Contract and allocation-gate evidence is recorded in [`docs/research/system-drawing-graphics-container-contract.md`](research/system-drawing-graphics-container-contract.md).

The image-convenience slice adds the official `Image.GetThumbnailImageAbort`, `Image.GetThumbnailImage`, and coordinate `Graphics.DrawIcon` surface. Bitmap thumbnails reuse the typed retained-texture resize path, the compatibility callback is not invoked, and unsupported image storage fails explicitly rather than fabricating pixels. Coordinate icon drawing preserves native size and placement through the existing typed unscaled-image command. This slice reduces the current measured debt to 45 missing types, 282 missing members, 47 other diagnostics, and 374 total. Contract and allocation evidence is recorded in [`docs/research/system-drawing-image-convenience-contract.md`](research/system-drawing-image-convenience-contract.md).

The drawing-identity slice adds the exact `Drawing2D.QualityMode`, `StringUnit`, and `Drawing2D.PenType` enums plus brush-derived `Pen.PenType`. Supported brush kinds are classified through direct managed type matches with zero warmed getter allocation. `Pen.Transform` remains explicit debt because its official pen-tip transformation is not equivalent to moving the stroke centerline and requires a typed anisotropic stroke contract. This slice reduces the current measured debt to 42 missing types, 281 missing members, 47 other diagnostics, and 370 total. Contract evidence is recorded in [`docs/research/system-drawing-drawing-identities-contract.md`](research/system-drawing-drawing-identities-contract.md).

The brush-base slice restores the official `MarshalByRefObject`, `ICloneable`, abstract `Clone`, and protected disposal inheritance contract. The ProGPU brush-lowering method moves off the public surface to an internal virtual seam with an explicit unsupported default for third-party subclasses. Built-in brushes preserve typed lowering, and `SolidBrush` now clones independently and rejects use after disposal. Native brush injection fails at the explicit Windows-adapter boundary. This slice reduces the current measured debt to 42 missing types, 278 missing members, 44 other diagnostics, and 364 total. Contract evidence is recorded in [`docs/research/system-drawing-brush-base-contract.md`](research/system-drawing-brush-base-contract.md).

The pen-ownership slice restores the official sealed `MarshalByRefObject`, `ICloneable`, and `IDisposable` shape and removes the ProGPU lowering method from the public API. Pens own cloned brush state; constructors and setters snapshot input, the public getter returns an independent clone, `Clone` deep-copies brush/dash state, and disposed pens reject reuse. Cached known-color brushes and pens are immutable while their clones are ordinary mutable resources. Rendering reads the owned brush through the internal typed seam, so the defensive public getter adds no per-draw allocation. This slice reduces current measured debt to 42 missing types, 278 missing members, 43 other diagnostics, and 363 total. Contract and performance evidence is recorded in [`docs/research/system-drawing-pen-ownership-contract.md`](research/system-drawing-pen-ownership-contract.md).

The stock-icon slice restores all 93 official `StockIconId` identities, the complete `StockIconOptions` flags enum, and the option-based `SystemIcons.GetStockIcon` overload. Caller-requested icons are independent disposable resources, static properties remain cached, and direct owned-bitmap transfer avoids an encode/decode round trip. A deterministic managed semantic catalog provides useful notification, folder, drive, media, document, printer, network, security, device, action, and application glyphs on every platform. It does not claim Windows shell-theme parity: exact local artwork and shell metrics remain typed local-OS adapter work. This slice reduces measured debt to 41 missing types, 189 missing members, 43 other diagnostics, and 273 total. Contract, platform-boundary, and performance evidence is recorded in [`docs/research/system-drawing-stock-icon-contract.md`](research/system-drawing-stock-icon-contract.md).

The printer-settings collection slice restores the direct-object, inheritable, mutable `ICollection` shapes for strings, paper sizes, paper sources, and printer resolutions, including public array constructors, virtual indexers, additions, counts, copying, and enumeration. Collections snapshot the caller's array, retain insertion order, and provide ordinary unsynchronized collection semantics. `InstalledPrinters` returns an isolated portable snapshot; real printer enumeration and capabilities remain a typed local-OS printing-service boundary rather than fabricated data. This slice reduces measured debt to 41 missing types, 173 missing members, 23 other diagnostics, and 237 total. Contract, platform-boundary, and performance evidence is recorded in [`docs/research/system-drawing-printer-settings-collections-contract.md`](research/system-drawing-printer-settings-collections-contract.md).

The page device-selection slice restores the public `PageSettings(PrinterSettings)` constructor, page-level paper-source and resolution state, raw custom-bin mapping, mutable validated resolution kinds, clone/reset semantics, and allocation-free warmed reads. The managed printing-shape continuation then restores canonical `Component`/`PrintEventArgs` inheritance, the inheritable query event, virtual preview antialiasing, and the protected legacy exception constructor without introducing native printing behavior. Together these two slices reduce measured debt from 207 to 194 diagnostics. Contract and boundary evidence is recorded in [`docs/research/system-drawing-page-settings-contract.md`](research/system-drawing-page-settings-contract.md) and [`docs/research/system-drawing-managed-printing-shape-contract.md`](research/system-drawing-managed-printing-shape-contract.md).

The destination-point image slice then removes ten exact `Graphics.DrawImage` suppressions, reducing measured debt from 194 to 184 diagnostics. Three-point arrays record affine parallelograms; four-point arrays record homogeneous projective quads with perspective-correct texture gradients. The typed texture payload survives retained pictures and translated context append. The native compiler lowers affine quads exactly and fails closed for projective quads until its wire gains homogeneous vertices. Contract and gate evidence is recorded in [`docs/research/system-drawing-destination-point-image-contract.md`](research/system-drawing-destination-point-image-contract.md).

The managed compatibility slice also adds typed deferred path boolean operations used by `Region` and `Graphics` clipping. It does not change the native command wire, C++ backend, shader ABI, text shaping, or image codec boundaries. Managed/native rendering parity therefore remains guarded by the repository renderer and headless suites rather than by a new native implementation fork.

## Quality gates

Focused managed tests live in `src/System.Drawing.Common.Tests`:

```bash
dotnet test src/System.Drawing.Common.Tests/System.Drawing.Common.Tests.csproj -c Release
```

Every API slice should cover:

- public signature and assembly-shape changes through ApiCompat;
- state, validation, disposal, cloning, events, and exception semantics where applicable;
- concurrency when resources or registries are shared;
- deterministic pixel or geometry output for rendering behavior;
- lazy GPU initialization and bounded resource ownership; and
- platform-boundary behavior for unsupported local-OS operations.

The known-color slice uses a 256-entry indexed cache per resource kind. Lookup is O(1), first access creates at most one retained resource, concurrent races publish one instance, and warmed access allocates zero bytes.

## Performance gates

Allocation-sensitive performance assertions run with the focused test suite. BenchmarkDotNet measurements provide review evidence for latency and allocation changes:

```bash
dotnet run --project src/System.Drawing.Common.Benchmarks/System.Drawing.Common.Benchmarks.csproj \
  -c Release -- --job short --filter '*'
```

CI uploads the JSON benchmark results with the raw ApiCompat report. For performance-sensitive rendering changes, also run the repository-wide Release renderer/headless suites and the applicable GPU workload from `agents.md`. Compare the same final binaries and hardware; investigate statistically repeatable regressions rather than accepting a single timing sample.

The 2026-08-21 ARM64 ShortRun checkpoint measured warmed cached brushes at 2.763 ns/op with 0 B allocated and warmed cached pens at 2.857 ns/op with 0 B allocated. Fresh `SolidBrush` and `Pen` construction measured 4.487 ns/op with 40 B and 11.195 ns/op with 112 B respectively. These are local microbenchmark observations, not broad renderer performance claims; the allocation-free warmed-resource invariant is also enforced by tests.

`ImageAttributesBenchmarks.RemapCpuBackedIcon64x64` guards the canonical WinForms recoloring path. Remapping is one source snapshot, one destination bitmap/pixel buffer, one O(M) lookup table, and one O(P) pixel pass for M mappings and P pixels. CPU-backed icons do not initialize a GPU device; a GPU-backed source requires one explicit readback because arbitrary exact color maps are not representable by the existing color-matrix shader.

`ImageAttributesBenchmarks.GammaThresholdCpuBackedIcon64x64` guards the managed fallback for adjustment combinations that cannot use the single color-matrix shader. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 120.910 µs median (120.647 µs mean, 0.760 µs standard deviation) with 16.39 KB allocated. The focused suite independently enforces a 16,384–20,000-byte warmed allocation window and covers category fallback, brush remapping, color keys, paired color/gray matrices, gamma, threshold, no-op, CMYK channel separation, and the explicit ICC-profile platform boundary. Contract and follow-up adapter evidence is recorded in [`docs/research/system-drawing-image-attributes-contract.md`](research/system-drawing-image-attributes-contract.md).

`ColorPaletteBenchmarks.CreateOptimalPalette16From64x64` guards the CPU-only quantization path. The implementation takes one straight-pixel snapshot, builds a weighted unique-color histogram in O(P), and performs deterministic weighted median-cut partitioning with a palette size bounded to 256. It does not initialize a GPU device. Fixed-palette cardinalities and palette/property ownership boundaries are enforced by focused tests; the public contract was checked against the official [`ColorPalette` constructors](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colorpalette.-ctor?view=windowsdesktop-10.0), [`PaletteType`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.palettetype?view=windowsdesktop-10.0), [`CreateOptimalPalette`](https://learn.microsoft.com/dotnet/api/system.drawing.imaging.colorpalette.createoptimalpalette?view=windowsdesktop-10.0), and [`Image.Palette`](https://learn.microsoft.com/dotnet/api/system.drawing.image.palette?view=windowsdesktop-10.0) documentation. The quantizer is original ProGPU code and does not copy framework implementation source.

`BitmapPixelMemoryBenchmarks.CopyRgbaToCallerOwnedLockBuffer` guards the CPU-only 256×256 BGRA export path used by caller-owned `LockBits`. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun checkpoint measured a 111.658 µs median (120.210 µs mean) with zero managed allocation. The three measured iterations make this coarse subsystem evidence rather than a universal throughput claim. The focused suite independently requires at most 512 bytes across 32 warmed 64×64 exports and covers packed/indexed/high-depth round trips. Public contract research and the managed/GPU boundary audit are recorded in [`docs/research/system-drawing-bitmap-pixel-memory-contract.md`](research/system-drawing-bitmap-pixel-memory-contract.md).

`BitmapPixelMemoryBenchmarks.ConvertRgbaToErrorDiffusedIndexedClone` guards a CPU-only 256×256 clone converted to 4-bit indexed color with a fixed custom palette and Floyd-Steinberg diffusion. Removing a redundant straight-alpha full-frame copy reduced the isolated ShortRun median from 4.549 ms to 3.844 ms (15.5%) and allocation from 519.62 KB to 263.54 KB (49.3%) on the same host. The three measured iterations are coarse subsystem evidence. The focused suite independently enforces an 18,000–24,000-byte window for the matching 64×64 clone-and-convert workload.

`ImageCodecBenchmarks.EncodeJpegToReusableStream` guards managed 256×256 JPEG encoding with a typed `Encoder.Quality` parameter and a preallocated destination stream. Removing the redundant SKBitmap staging/copy reduced the isolated ARM64/.NET 10.0.11 ShortRun median from 2.751 ms to 1.013 ms (63.2%) and allocation from 525.41 KB to 257.5 KB (51.0%) on the same host. The three measured iterations are coarse subsystem evidence, and the runner could not acquire high process priority in the restricted environment. The focused suite independently enforces a 16,384–30,000-byte warmed allocation window for the matching 64×64 managed JPEG workload. Public contract sources and the managed/native applicability audit are recorded in [`docs/research/system-drawing-image-codec-contract.md`](research/system-drawing-image-codec-contract.md).

The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 16-color quantization of the deterministic 64×64 gradient fixture at 1.491 ms/op with 496.75 KB allocated. The focused test independently enforces deterministic output and a 400,000–600,000-byte post-warmup allocation window. As with the recoloring checkpoint, this is local regression evidence from the restricted development environment rather than a renderer-wide claim.

`MatrixBenchmarks.TransformPointBatch` guards the managed affine hot path. It updates a preallocated 1,024-point span in place through the same `Matrix3x2` value consumed by the renderer. The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 0.9072 ns per point with zero managed allocation. The focused suite independently requires exactly zero bytes across 64 warmed 1,024-point transforms. Contract sources and the managed/native applicability audit are recorded in [`docs/research/system-drawing-matrix-contract.md`](research/system-drawing-matrix-contract.md).

`LinearGradientBrushBenchmarks.LowerEightStopGradient` guards typed lowering of a custom eight-stop gradient, including spread, gamma mode, and coordinate transform state. The 2026-08-21 ARM64 in-process ShortRun checkpoint measured 62.66 ns/op with 304 B allocated. The focused suite independently enforces a 288–352-byte warmed allocation window. Public contract research, scalable-angle math, and the managed/native applicability audit are recorded in [`docs/research/system-drawing-linear-gradient-contract.md`](research/system-drawing-linear-gradient-contract.md).

`HatchBrushBenchmarks.LowerEightByEightHatchTile` guards O(1) typed lowering of immutable hatch state into one retained tile-pattern brush. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun checkpoint measured a 12.172 ns median (13.319 ns mean) with 64 B allocated. The three measured iterations and unavailable high process priority make this coarse local subsystem evidence. The focused suite independently enforces one bounded 32–96-byte allocation per lowering, exact foreground/background color transport, stable negative-coordinate tiling, declared percentage densities, and nonempty bounded output for every concrete style. Public contract sources, original pattern policy, shader/native ABI applicability, and validation evidence are recorded in [`docs/research/system-drawing-hatch-brush-contract.md`](research/system-drawing-hatch-brush-contract.md).

`TextureBrushBenchmarks.RecordAndReleaseFourTileFill` guards typed recording and retained-resource release for a 4×4 mirror-XY fill backed by a 2×2 owned texture. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun checkpoint measured a 556.757 ns median (556.451 ns mean) with 96 B allocated. The three measured iterations and unavailable high process priority make this coarse local subsystem evidence. The focused suite independently requires zero allocation for warmed transform mutation, at most 512 B for the matching four-tile record/release cycle, exact pixels for every wrap mode, independent source/clone ownership, and geometry-clipped non-rectangle fills. Public contract sources, transform/wrap policy, typed renderer applicability, and validation evidence are recorded in [`docs/research/system-drawing-texture-brush-contract.md`](research/system-drawing-texture-brush-contract.md).

`FontBenchmarks.ReadTypefaceMetrics` guards 4,000 warmed `FontFamily` metric reads over a privately owned Inter face. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun checkpoint measured an 8.368 ns median per read (8.383 ns mean, 0.026 ns standard deviation) with zero managed allocation. This used one launch, three warmups, and three measured iterations; process-priority elevation was denied, so it is a coarse local subsystem checkpoint rather than an end-to-end text claim. The focused suite independently requires exactly zero bytes for the same 4,000 reads and preserves the existing shaped-outline allocation gate. Contract, ownership, native-boundary, and validation evidence are recorded in [`docs/research/system-drawing-font-contract.md`](research/system-drawing-font-contract.md).

Typed `Font.ToLogFont(out LOGFONT)` is a scalar compatibility path rather than a renderer workload, so it uses a focused allocation gate instead of a standalone BenchmarkDotNet job. After warmup, 10,000 nonvertical exports must allocate exactly zero managed bytes while writing the fixed face buffer, logical height, weight, style, and charset. Contract and boundary evidence is recorded in [`docs/research/system-drawing-logfont-contract.md`](research/system-drawing-logfont-contract.md).

`GraphicsPrimitiveBenchmarks.RecordCurveSpan` guards typed recording of a four-point `ReadOnlySpan<PointF>` cardinal curve and release of the retained command. The 2026-08-22 ARM64/.NET 10.0.11 ShortRun measured a 209.644 ns median (207.170 ns mean, 17.922 ns standard deviation) with 792 B allocated. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse subsystem evidence. The focused suite independently enforces a 1,024-byte upper allocation bound, exact retained fill rules, validation-before-recording, and production filled-pie pixels. Public surface, architecture, platform-boundary, and gate evidence are recorded in [`docs/research/system-drawing-graphics-primitives-contract.md`](research/system-drawing-graphics-primitives-contract.md).

`GraphicsFlushBenchmarks.RecordAndFlushRectangle` guards one warmed retained rectangle record followed by synchronous host batch consumption. The 2026-08-26 ARM64/.NET 10.0.11 ShortRun measured a 155.881 ns median (155.858 ns mean, 2.573 ns standard deviation) with 40 B allocated. The focused suite independently enforces a 64-byte upper bound and covers enum identity, bitmap pixels, balanced clip batches, continued drawing, disposed behavior, missing-target failure, and callback consumption. Contract and architecture evidence are recorded in [`docs/research/system-drawing-graphics-flush-contract.md`](research/system-drawing-graphics-flush-contract.md).

The graphics-state slice preserves the allocation-free `TransformElements` value path and the existing 32–96-byte hatch-lowering bound. Focused gates cover exact defaults and validation, disposal, save/restore, append/prepend composition, effective-clip rectangle visibility, production `SourceCopy` alpha replacement, and production rendering-origin pixels. Contract and architecture evidence are recorded in [`docs/research/system-drawing-graphics-state-contract.md`](research/system-drawing-graphics-state-contract.md).

The graphics-container slice keeps its hidden transform as one `Matrix3x2` value and uses the existing retained geometry-clip and blend scopes. Twelve focused tests cover state restoration, nested and rectangle mappings, production inherited-clip pixels, scope invalidation, command balance, and a 256-byte-per-round-trip upper allocation bound across 1,024 warmed transitions. Contract evidence is recorded in [`docs/research/system-drawing-graphics-container-contract.md`](research/system-drawing-graphics-container-contract.md).

`ImageConvenienceBenchmarks.CreateAndDisposeThumbnail` guards the typed retained-texture 64x64-to-32x32 thumbnail path. The 2026-08-26 ARM64/.NET 10.0.11 ShortRun measured a 170.455 microsecond median (192.464 microsecond mean, 38.656 microsecond standard deviation) with 7.77 KB allocated. Three measured iterations and denied process-priority elevation make this coarse local subsystem evidence. The focused suite independently enforces a 4,608-byte-per-operation upper bound across 32 warmed 8x8-to-4x4 thumbnail creations and covers callback, validation, unsupported-storage, icon-pixel, placement, and command-ownership behavior. Contract evidence is recorded in [`docs/research/system-drawing-image-convenience-contract.md`](research/system-drawing-image-convenience-contract.md).

The drawing-identity slice adds no renderer hot-path work. Its focused allocation gate requires exactly zero managed bytes across 4,096 warmed `Pen.PenType` reads and verifies each supported brush mapping. The deferred anisotropic pen-tip work is documented in [`docs/research/system-drawing-drawing-identities-contract.md`](research/system-drawing-drawing-identities-contract.md).

The brush-base slice changes ownership and public shape without adding renderer hot-path work. Four focused tests guard clone independence, disposal, derived-class hooks, and explicit unsupported seams; the existing brush-specific allocation and pixel gates remain authoritative. Contract evidence is recorded in [`docs/research/system-drawing-brush-base-contract.md`](research/system-drawing-brush-base-contract.md).

`KnownColorResourceBenchmarks.ReadCachedPenStateBatch` guards the scalar read path used by cached system pens. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured 2.271 ns per `Color`/`PenType`/`Width` group with zero managed allocation. The focused suite independently requires exactly zero bytes across 100,000 warmed groups and verifies that defensive brush snapshots stay off the renderer path. Contract evidence is recorded in [`docs/research/system-drawing-pen-ownership-contract.md`](research/system-drawing-pen-ownership-contract.md).

`SystemIconBenchmarks` guards direct owned-bitmap creation for a plain 32×32 folder and an overlaid selected 32×32 document. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured the plain icon at a 1.490 microsecond median (1.512 microsecond mean) with 13.97 KB allocated and the decorated icon at a 2.884 microsecond median (3.262 microsecond mean) with 14.65 KB allocated. Three measured iterations and denied process-priority elevation make these coarse local subsystem checkpoints. The focused suite independently covers every identifier and enforces a 36 KB-per-operation warmed in-process allocation ceiling. Contract and platform-boundary evidence is recorded in [`docs/research/system-drawing-stock-icon-contract.md`](research/system-drawing-stock-icon-contract.md).

`PrinterSettingsCollectionBenchmarks.ReadPaperSizeWidthBatch` guards allocation-free virtual indexed access through the managed printing model. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 0.965 ns median (0.947 ns mean, 0.041 ns standard deviation) per indexed width read with 0 B allocated. One launch, three measured iterations, and denied process-priority elevation make this coarse local subsystem evidence. The focused suite independently requires exactly zero bytes across 100,000 warmed reads and covers snapshot, mutation, copying, enumeration, and installed-printer isolation. Contract and platform-boundary evidence is recorded in [`docs/research/system-drawing-printer-settings-collections-contract.md`](research/system-drawing-printer-settings-collections-contract.md).

`PrinterSettingsCollectionBenchmarks.ReadPageDeviceSelectionBatch` guards allocation-free page-level paper-source and resolution reads. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 0.615 ns median (0.615 ns mean, 0.004 ns standard deviation) per alternating page read group with 0 B allocated. The focused suite independently requires zero bytes across 100,000 warmed groups and covers constructor association, raw/custom source values, mutable resolution validation, clone ownership, and the native printer-capability boundary. Contract evidence is recorded in [`docs/research/system-drawing-page-settings-contract.md`](research/system-drawing-page-settings-contract.md).

The managed printing-shape continuation changes inheritance, disposal/event ownership, virtual dispatch, and legacy serialization shape only; it adds no renderer or printing hot-path work. Two focused tests cover exact base/sealed/virtual/constructor metadata, inherited `Component.Dispose` notification, query-page settings reset semantics, and derived preview-controller dispatch. Contract evidence is recorded in [`docs/research/system-drawing-managed-printing-shape-contract.md`](research/system-drawing-managed-printing-shape-contract.md).

The point/source-rectangle image-overload slice reuses the existing texture retention, unit conversion, sampling, remap, color-matrix, and callback path. The destination-point follow-up adds a zero-allocation warmed recording path, exact affine corner mapping, and projective-correct sampling without a diagonal seam. Focused tests cover pixels, typed command retention/translation, attributes, callbacks, validation, and native affine/projective boundaries. The 2026-08-27 ARM64/.NET 10.0.11 `ImageConvenienceBenchmarks.RecordPerspectiveDrawImage` ShortRun measured a 116.578 ns median (119.490 ns mean, 7.541 ns standard deviation) with zero managed allocation. One launch, three measured iterations, and denied process-priority elevation make this coarse local evidence; the focused suite independently requires exactly zero bytes across 1,000 warmed recordings. Contracts are recorded in [`docs/research/system-drawing-graphics-image-overloads-contract.md`](research/system-drawing-graphics-image-overloads-contract.md) and [`docs/research/system-drawing-destination-point-image-contract.md`](research/system-drawing-destination-point-image-contract.md).

`EffectsBenchmarks` isolates 256×256 warmed pointwise inversion, radius-eight blur, and a 64×64 retained effect draw. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured inversion at a 756.184 µs median (747.744 µs mean), blur at a 1.744 ms median (1.806 ms mean), and effect draw at a 282.715 µs median (309.593 µs mean) with 17,200 B allocated. Pointwise and blur operations allocated zero managed bytes. One launch, three measured iterations, and denied process-priority elevation make these coarse local checkpoints. The pointwise path mutates a CPU-resident RGBA buffer without allocation. Blur and sharpen rent three bounded scratch buffers and use two moving-window passes, so runtime is O(width × height) rather than O(radius² × pixels). Effect draw owns one 64×64 snapshot plus retained-resource state. The focused suite independently requires exactly zero bytes across 128 warmed pointwise applications and covers clipped areas, alpha, LUT and matrix snapshots, convolution pixels, identity sharpening, disposal, constructor ranges, draw ownership, transforms, attributes, and validation-before-recording. Contract evidence is recorded in [`docs/research/system-drawing-effects-contract.md`](research/system-drawing-effects-contract.md).

`CachedBitmapBenchmarks` compares warmed ordinary and device-cached 64×64 retained recording. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 225.040 ns median for ordinary bitmap recording and a 169.996 ns median for cached recording, with 96 B allocated by each record/release cycle. One launch, three measured iterations, and denied process-priority elevation make this coarse local checkpoint. The cached path owns one immutable device-domain snapshot at construction, records a direct texture command, and shares one typed texture lease across repeated draws in a retained context. Focused tests cover source independence and disposal, translation-only semantics, validation before recording, exact pixels, deferred command lifetime, retained-resource reuse, and a 128-byte-per-record warmed allocation ceiling. Contract and platform-boundary evidence is recorded in [`docs/research/system-drawing-cached-bitmap-contract.md`](research/system-drawing-cached-bitmap-contract.md).

`GraphicsStringFormatBenchmarks.MeasureSpan` guards one warmed `ReadOnlySpan<char>` measurement through the same typed shaping, wrapping, bidi, fallback, and cluster layout used for retained drawing and character-range geometry. The original 2026-08-22 ARM64/.NET 10.0.11 in-process ShortRun measured a 10.709 µs median (11.316 µs mean, 1.490 µs standard deviation) with 6,712 B/op. The paired advanced-format checkpoint measured the baseline at an 11.909 µs median and 6.64 KB/op, while `MeasureAdvancedFormatSpan`—tab stops, Arabic digit substitution, and trailing-space measurement—measured a 7.235 µs median and 5.67 KB/op. The mnemonic checkpoint's `RecordMnemonicString` measured a 3.021 µs median and 2.02 KB/op. The slash-aware `MeasureEllipsisPathSpan` checkpoint measured an 88.79 µs mean and 70.02 KB/op. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse managed-layout/recording evidence. The focused suite independently enforces 16,384-byte baseline, 24,576-byte advanced, 24,576-byte mnemonic-recording, and 98,304-byte path-trimming upper allocation bounds and covers span/string equality, typed glyph recording, wrapped selection regions, clipped versus `NoClip` bounds, explicit tab origins, vertical flow, digit substitution, fallback suppression, trailing-space width, visible default-ignorable representatives, mnemonic underline geometry, whole-line versus partial-final-line limits, path-prefix/final-segment retention with a retained-tail mnemonic, and official empty-input validation order. Contract, architecture, remaining semantics, and gate evidence are recorded in [`docs/research/system-drawing-string-format-contract.md`](research/system-drawing-string-format-contract.md).

`GraphicsPathBenchmarks` guards caller-owned point/type export, allocation-free iterator enumeration, analytic retained-geometry bounds, outline queries, curve widening, retained path deformation, and shaped text-outline materialization. The 2026-08-21 ARM64 isolated ShortRun checkpoint measured export of sixteen retained ellipses at 7.620 µs/op and bounds at 2.220 µs/op, both with zero managed allocation. The 2026-08-22 iterator checkpoint measured enumeration of the same 208-point snapshot into caller storage at 37.59 ns/op with zero managed allocation. The 2026-08-22 stroke checkpoint measured a retained four-point outline query at a 54.53 ns median and 112 B/op. Replacing two triangle figures per stroke rectangle with one closed retained quad reduced the sixteen-ellipse clone-and-widen median from 143.06 µs to 119.02 µs (16.8%) and allocation from 345,744 B to 256,120 B (25.9%) on the same .NET 10.0.11 ARM64 host. The first bilinear clone-and-warp checkpoint measured a 28.260 µs median (28.733 µs mean) with 55.99 KB allocated. The warmed `LibreWinForms` shaped-outline checkpoint measured an 11.439 µs median (11.200 µs mean) with 17.45 KB allocated. These ShortRun measurements have only three measured iterations, so raw artifacts remain the evidence and the results are coarse subsystem checkpoints rather than universal timing claims. The focused suite independently requires exactly zero bytes across warmed span exports and iterator enumeration, at most 256 B per line-outline query, at most 280,000 B for the fixed sixteen-ellipse widening workload, at most 72,000 B for the matching bilinear warp workload, and at most 24,000 B for the warmed shaped-outline workload. Public contract research, curve mathematics, text-outline architecture, and the managed/native applicability audit are recorded in [`docs/research/system-drawing-graphics-path-contract.md`](research/system-drawing-graphics-path-contract.md).

`GraphicsPathBenchmarks.WidenAnisotropicPenClone` guards the `Pen.Transform` inverse-space widening path. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 1.678 µs median (1.697 µs mean, 0.038 µs standard deviation) with 7.16 KB allocated. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse local subsystem evidence. The focused suite independently enforces a 6.5–8.5 KB warmed allocation window and exactly zero allocation across 10,000 managed transform-mutation groups. Contract, geometry, pixel, and boundary evidence is recorded in [`docs/research/system-drawing-pen-transform-contract.md`](research/system-drawing-pen-transform-contract.md).

`GraphicsPathBenchmarks.WidenCompoundArrowPenClone` guards two compound bands, a round join, and a filled adjustable end arrow on the shared widened-geometry path. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 3.757 µs median (3.488 µs mean, 0.594 µs standard deviation) with 9.27 KB allocated. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse local subsystem evidence. The focused suite independently enforces an 8-12 KB warmed allocation window, zero allocation for 10,000 cap-state mutation groups, and exact production pixel/bounds/hit-test behavior for representative compound, arrow, generic-fill, and generic-stroke cases. Contract and remaining differential work are recorded in [`docs/research/system-drawing-custom-cap-compound-pen-contract.md`](research/system-drawing-custom-cap-compound-pen-contract.md).

`GraphicsPathBenchmarks.LowerMaximumBoundaryPathGradient` guards managed lowering of a 128-point path gradient with alternating surround colors, anisotropic focus scales, and a triangular blend curve. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 3.807 µs median (3.706 µs mean, 0.366 µs standard deviation) with 6.34 KB allocated. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse local subsystem evidence. The focused suite independently enforces a 400–1,400-byte allocation window for a smaller canonical brush, while the renderer/native gates prove bounded 128-edge transport and polygon-aware pixels. Contract and remaining differential work are recorded in [`docs/research/system-drawing-path-gradient-contract.md`](research/system-drawing-path-gradient-contract.md).

The 2026-08-21 ARM64 in-process ShortRun checkpoint measured the 64×64 remap at 19.59 µs/op with 16.48 KB allocated. The focused test independently enforces a bounded 16,384–20,000-byte allocation window after warmup. The in-process result is local diagnostic evidence for the restricted development environment; CI uses BenchmarkDotNet's normal isolated toolchain and publishes its JSON result.

`DesktopCaptureBenchmarks.CaptureAndMaterialize64x64` guards the complete typed
provider, owned RGBA snapshot, retained texture command, and destination bitmap
materialization path. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a
578.0 us median (789.3 us mean, 466.4 us standard deviation) and 33.25 KB per
64-by-64 capture. One launch and three measured iterations produced high timing
variance, so this is coarse subsystem evidence rather than a regression
threshold. The focused gate independently permits at most 65,536 managed bytes
across sixteen warmed 16-by-16 captures (4 KiB per operation), including the
unavoidable 1 KiB pixel payload. OS capture latency remains adapter specific
and is not represented by the filling-provider benchmark. Contract and
boundary evidence is recorded in
[`docs/research/system-drawing-desktop-capture-contract.md`](research/system-drawing-desktop-capture-contract.md).

`NativeImageImportBenchmarks.Import64x64IconSnapshot` guards the typed provider
call, guarded synchronous copy, owned bitmap construction, pixel read, and
disposal. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 648.615 ns
median (645.822 ns mean, 15.825 ns standard deviation) and 16.43 KB per import.
The filling provider deliberately excludes OS/GDI handle-decoding latency. The
focused suite independently permits at most 32,768 bytes across sixteen warmed
16-by-16 imports, including the unavoidable 1 KiB owned pixel snapshot per
result. Contract and platform-boundary evidence is recorded in
[`docs/research/system-drawing-native-image-import-contract.md`](research/system-drawing-native-image-import-contract.md).

`NativeDrawingInteropBenchmarks.GetHalftonePaletteDispatch` guards the typed
registry and state-changing, no-inline provider call without native OS work.
The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 0.634 ns median (0.609 ns
mean, 0.074 ns standard deviation) and zero managed allocation. The workload is
close to timer/harness resolution and the three-iteration confidence interval
is wider than the mean, so it is evidence of a nonallocating dispatch path, not
a portable nanosecond latency claim. The focused suite independently requires
exactly zero managed bytes across 10,000 warmed palette dispatches. Contract
and platform-boundary evidence is recorded in
[`docs/research/system-drawing-native-interop-contract.md`](research/system-drawing-native-interop-contract.md).

## Implementation order

API work should proceed in dependency groups:

1. base ownership and shape (`Brush`, `Pen`, `Image`, `Graphics`, `Matrix`);
2. imaging codecs, pixel formats, palettes, locking, and image attributes;
3. drawing primitives, paths, regions, transforms, text, and fonts;
4. complete managed printing model with a typed backend boundary;
5. icons, cursors, native-handle adapters, and platform-specific escape hatches; and
6. remaining design-time converters and metadata.

Adding a type without its managed contract can increase the member-diagnostic count because ApiCompat begins inspecting that type. A subsystem is complete only when its full public shape and normal managed semantics are present, even if an unavailable OS operation fails explicitly at the backend boundary.
