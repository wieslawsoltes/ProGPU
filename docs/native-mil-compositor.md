# Native MIL compositor

## Goal

ProGPU will provide a reflection-free C++ composition endpoint that can consume
LibreWPF's canonical DUCE/MIL command batches, retain the WPF resource graph,
lower it to the existing ProGPU semantic scene ABI, and execute that scene on
the same C++ renderer through either wgpu-native or provider-resolved Dawn.
The existing managed `ProGPU.Scene` compositor remains available as an
independent compatibility lane while parity is established.

This is a clean source-integrated replacement, not a binary shim that scrapes
managed WPF objects. Protocol structs are derived from the MIT-licensed WPF MCG
model and are consumed as typed byte records with explicit bounds checks.

## Protocol findings

WPF's client channel writes each command as:

```text
uint32 item_size_including_header
uint32 MILCMD
byte[] packed_command_fields_and_optional_payload
byte[0..3] dword_alignment_padding
```

`item_size` is at least eight, is divisible by four, and must fit entirely in
the submitted batch. The command packet returned by WPF's reader begins at the
`MILCMD` field and has `item_size - 4` bytes. Resource handles are 32-bit and
belong to a channel-local namespace. The current retail protocol contains 141
commands:

| Range | Area | Count |
| --- | --- | ---: |
| `0x01`–`0x3d` | transport, resources, visuals, targets, glyphs | 61 |
| `0x3e`–`0x56` | nested render-data instruction stream | 25 |
| `0x57`–`0x8d` | retained media, 3D, effect, geometry, brush, drawing resources | 55 |

The transport and render-data streams use the same item framing. The outer
`MilCmdRenderData` packet carries the nested stream byte count and then the
nested command bytes. Commands and structures are packed, so the decoder uses
bounded byte copies and never casts an untrusted address to a command struct.

Protocol-authority checkpoint `8839f00d` replaces the hand-maintained C++
command enum with generated output. Complete-layout checkpoint `4408a86c`
extends `eng/progpu-generate-mil-protocol.py` over WPF's checked-in
`wgx_commands.h` and `wgx_renderdata_commands.h`: all 116 packed top-level
packets plus all 25 nested render-data packets, exactly one layout for each of
the 141 retail commands. The 108 explicit `Pack=1` definitions in
`wgx_commands.cs` are retained as an independent overlap oracle; every shared
size and non-padding field offset/width must agree with the native MCG output.
The neutral manifest records SHA-256 provenance for all four inputs plus the
invalid/debug command sentinels. Standalone ProGPU builds verify
header/manifest agreement; LibreWPF's SDK gate regenerates from its live WPF
source tree and fails on drift.
Managed-producer-oracle checkpoint `563c031c` additionally parses the 25
packed render-data payload structs in `Generated/RenderData.cs`. That producer
metadata agrees exactly with 24 native generated layouts. The sole discrepancy
is legacy `MILCMD_PUSH_EFFECT`: the native `wgx_renderdata_commands.h`
declaration contains only the opcode, while the managed writer emits
`hEffect` and `hEffectInput` as two additional 32-bit handles. The bytes that
WPF actually writes are the wire-framing authority, so the generated command
view is 12 bytes with handle offsets 4 and 8. The manifest records this
producer-authority exception explicitly instead of hiding it in handwritten
C++ metadata.

Native execution intentionally does not approximate the obsolete bitmap-effect
scope. Both scene lowering and retained cache dependency traversal require the
exact 12-byte producer record, read both handles with bounded copies, and then
return `unsupported_command`. A header-only 4-byte command view is
`malformed_batch`. Tests lock down both outcomes so future generator drift
cannot silently reinterpret the following nested record as effect payload.
The decoder applies that generated-size check to the complete nested command
range before semantic dispatch. Correctly framed but unimplemented dynamic
Y-guideline records therefore return `unsupported_command`; short or oversized
records return `malformed_batch`. `DrawVideo` and `DrawVideoAnimate` are now
implemented through the canonical MediaPlayer resource and the live external-
image sideband described below. This keeps capability status separate from
wire corruption for all 25 nested commands and prevents an unsupported feature
from bypassing packet-boundary validation.
Follow-up `d4a1f370` makes the complete retained Visual update family plus
DoubleResource and PointResource consume generated sizes/offsets. That includes
transform/effect/cache/clip/alpha/render-option/content/mask state, variable
guideline collections, scroll clips, and child topology. The generator also
captures MCG's private `BYTEPacking` fields, requires every command header to
remain DWORD-sized, and proves every parsed layout maps to one command;
this preserves the guideline packet's 16-byte payload boundary rather than
misreading its last private packing byte as a 14-byte header. Follow-up
`e93d8919` moves the active top-level and nested render-data readers plus
dependency discovery onto the complete generated layouts. The decoder now has
no numeric `has_exact_size(view, ...)` or direct numeric
`read_at(view.packet, ...)` calls; composite-field component strides and the
separately bounded path-figure mini-protocol remain intentional.

Clean detached `22bf5bf1` qualified that generated Visual-layout checkpoint on
Windows ARM64 in the Parallels VM. MSVC rebuilt the generated C++ header and
both native modules under `/W4 /WX`; all 11 native/Dawn CTests passed, including
the MIL packet/layout suite. Qualified SHA-256 values are
`FB4304088E87A3F07CA59A84B16FEDA21A4DDADBB9377028553740D51B30F290`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL. The Python regeneration/drift check remains a
host/CI gate because the qualification VM intentionally has no Python runtime;
MSVC independently proves the committed generated header is valid ARM64 C++.

Transform-layout checkpoint `4e7d8f55` moves MatrixResource and the complete
retained 2D transform family onto that same generated authority: variable
TransformGroup children, TranslateTransform, ScaleTransform, SkewTransform,
RotateTransform, and MatrixTransform, including every animation-resource
handle. Existing finite-value, resource-type, cycle, and graph validation is
unchanged; only packet size and field-location ownership moved from local
numeric literals to the WPF-generated layouts. The generator drift check and
all 11 native/Dawn CTests pass on Apple Silicon. Clean detached Windows ARM64
MSVC `/W4 /WX` also rebuilt both native modules and passed all 11 tests.
Qualified SHA-256 is
`B514024B7F83A06C5F6FD2CDED7C9677255AD283076B3E61AA096DC633288E48`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL.

Clean detached `e93d8919` qualified the complete 141-layout authority and
decoder migration on Windows ARM64. MSVC rebuilt all generated-header
dependents and both native modules under `/W4 /WX`; all 11 native/Dawn CTests
passed, including the MIL layout and packet suites. Qualified SHA-256 is
`7D4D5087CB7D81893CDE231BEDD22983A0C31323AE1EDF5A87FDDC415E758CB5`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL. The live LibreWPF-to-ProGPU regeneration gate
reports `143 commands, 141 complete packet layouts`.

Decoder-coverage checkpoint adds a second generated authority beside the wire
layout manifest. `eng/progpu-generate-mil-coverage.py` reads the canonical
command table and bounded regions of the actual C++ channel decoder and nested
render-data compiler, then emits
`eng/mil/native-mil-command-coverage.json`. The current implementation has 84
explicit top-level decoder cases, explicit framing/dispatch for all 25
canonical nested render-data opcodes, two non-retail sentinels, and 32 commands
with no native top-level dispatch. Nested dispatch does not claim every value
or resource combination is supported; obsolete effects and other unsupported
forms continue to fail closed after exact framing.
The ledger deliberately calls the first category `top-level-decoder`, not
`parity`: an explicit case proves framing and dispatch ownership, while
value-domain, resource, scene, and pixel parity remain separately tested.

Every `not-dispatched` entry is therefore visible as either a native parity gap
or an intentional transport/platform boundary requiring a typed portable
replacement. The generator rejects unknown implementation command names,
missing nested render-data opcodes, and accidental acceptance of a nested
opcode as a top-level channel packet. Both native build and source-contract
gates reject a stale ledger whenever the protocol or decoder source changes.
This closes the earlier reporting hole where “141 layouts generated” could be
misread as “141 packets implemented.”

Bitmap packet checkpoint closes two of the initial 54 undispatched entries, so
the live ledger now reports 52. `MilCmdBitmapSource` accepts the exact 16-byte
canonical packet only when its process-local `IWICBitmapSource*` field is null;
copied RGBA8 pixels and same-device external textures remain exclusively owned
by the existing typed channel sidebands. A non-null pointer fails
transactionally instead of entering the portable resource graph.
`MilCmdBitmapInvalidate` accepts the exact 28-byte packet, validates the BOOL
and an enabled signed `RECT` against the bound bitmap dimensions, and advances
the retained generation without pixel readback or copying. When the dirty flag
is false, its rectangle bytes are deliberately ignored because WPF's producer
leaves that field uninitialized. Tests cover full and partial invalidation,
pointer rejection, malformed flags/rectangles, generation changes, metrics,
and rollback.

Media packet checkpoint closes two more ledger entries. The exact 20-byte
`MilCmdMediaPlayer` packet now validates its direct-notification BOOL while
requiring the process-local media pointer to be null; live frames remain on the
typed same-device external-image sideband. The exact 48-byte retained
`MilCmdVideoDrawing` resource validates its destination rectangle, canonical
MediaPlayer handle, and optional RectResource animation. `DrawDrawing` then
reuses the existing native `DrawVideo` image-resource path, including animated
bounds, retained dependency traversal, protected deletion, deterministic
external resource identity, and no payload/readback. Non-null media pointers,
invalid notification flags, missing frame bindings, wrong resources, and
invalid graph deletion all fail transactionally. The live ledger is therefore
66 top-level dispatches and 50 undispatched commands.

WriteableBitmap checkpoint closes the canonical
`MilCmdDoubleBufferedBitmap` and `MilCmdDoubleBufferedBitmapCopyForward`
entries. Both packets retain exact WPF framing, require their process-local
`CSwDoubleBufferedBitmap*` and completion `HANDLE` fields to be zero, validate
the back-buffer BOOL, and advance the resource generation transactionally.
New copied-RGBA8 and same-device external-image sidebands bind the current
front buffer to canonical `TYPE_DOUBLEBUFFEREDBITMAP` without reusing or
weakening the existing BitmapSource contract. ImageDrawing/DrawImage now
accept that canonical image type and use the ordinary retained image shader,
sampling, clipping, transform, damage, and external-resource paths. The
managed native package exposes the packet builders and both sidebands for
wgpu-native and Dawn. Native tests cover copied and zero-payload external
front buffers, copy-forward, type separation, pointer/event rejection,
generation, rendering, and rollback. The live ledger is now 68 top-level
dispatches and 48 undispatched commands.

Portable window-target checkpoint closes canonical `MilCmdHwndTargetCreate`,
`MilCmdHwndTargetSuppressLayered`, `MilCmdTargetUpdateWindowSettings`, and
`MilCmdHwndTargetDpiChanged`. The source-integrated packet builder retains
dimensions, clear color, initialization flags, DPI awareness/scales, signed
window bounds, layer/transparency policy, constant alpha, color key,
child/RTL/GDI state, rendering enablement, and the disable cookie, while HWND,
shared-section, master-device, and bitmap handles remain zero. Surface and
present ownership stays in the typed portable host instead of entering the
backend-neutral channel as a Windows pointer.

The decoder follows WPF's native validation and ordering rules: layer kinds
are bounded, transparency is a three-bit mask, BOOL fields are canonical,
numeric state is finite, non-layered targets become opaque, system-managed
layers discard per-pixel alpha, child targets remain enabled, and an
out-of-order enable with a stale disable cookie is ignored without mutating
the retained generation. A disabled window target compiles a valid scene with
no visual work; reenabling with the current cookie restores the retained root
without rebuilding its resources. Native tests cover the complete lifecycle,
stale ordering, scene suppression/restoration, invalid enums, process-handle
rejection, and transactional rollback. Managed tests verify every canonical
packet offset. The live ledger was then 72 top-level dispatches and 44
undispatched commands.

Brush-layout checkpoint `1b4ef706` migrates SolidColorBrush,
LinearGradientBrush, RadialGradientBrush, DashStyle, and Pen packet readers to
generated WPF MCG metadata. Generated fixed-header boundaries now own gradient
stop and dash-array payload starts; all brush transforms, animation handles,
mapping/spread modes, stop colors, pen caps/joins, resource dependencies, and
finite-value validation retain their previous semantics. Apple Silicon passed
the live generator check and all 11 native/Dawn CTests. Clean detached Windows
ARM64 MSVC `/W4 /WX` rebuilt both modules and passed all 11 tests; SHA-256 is
`163F49880179F85857ED4FB02C6F1CEB95C46158B407C87934568239C4FE9E5F`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL.

Geometry-layout checkpoint `f2107a55` migrates LineGeometry,
RectangleGeometry, EllipseGeometry, GeometryGroup, CombinedGeometry, and
PathGeometry to generated WPF MCG sizes and field offsets. Variable group-child
and path-figure payloads now begin at the generated fixed-header boundary. The
nested path-figure/segment stream remains its own strictly bounds-checked MIL
mini-protocol; this change does not conflate its record layout with the managed
top-level packet metadata. Geometry transform, animation, cycle, fill-rule,
finite-value, and path-record validation is preserved. Apple Silicon passed the
generator check and all 11 native/Dawn CTests. Clean detached Windows ARM64
MSVC `/W4 /WX` rebuilt both modules and passed all 11 tests; SHA-256 is
`853802988172C66820819B389E48305613A0488FEB3972C0F2C3BD61EB9CEDAC`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL.

Drawing-layout checkpoint `9d489872` migrates GeometryDrawing,
GlyphRunDrawing, ImageDrawing, DrawingImage, variable GuidelineSet and
DrawingGroup payloads, and BitmapCache to generated WPF MCG metadata. The
generated fixed-header boundary now owns guideline coordinate arrays and
drawing child-handle arrays. Existing drawing/resource-type dependencies,
cycle checks, opacity/render-option validation, cached bounds preservation,
child render-data synthesis, and bitmap-cache policy remain unchanged. Apple
Silicon passed the generator check and all 11 native/Dawn CTests. Clean
detached Windows ARM64 MSVC `/W4 /WX` rebuilt both modules and passed all 11
tests; SHA-256 is
`096EE139F64DDB2D0FEC503424ECBFED98D97AEDCA29E9C9DD80ACF9FDF8FCE8`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL.

Effect checkpoint `1ac97d67` migrates BlurEffect and DropShadowEffect,
including every animation-resource handle and rendering-bias field. Target
checkpoint `ee54c934` migrates GenericTarget creation, root, clear color,
flags, invalidate rectangles, and variable RenderData payload boundaries.
Effect parameter validation, unsupported animation policy, target/resource
ownership, and nested render-data byte preservation are unchanged. Both
commits passed the generator check and all 11 Apple Silicon native/Dawn CTests.
Clean detached `ee54c934`, which contains both commits, rebuilt both Windows
ARM64 modules under MSVC `/W4 /WX` and passed all 11 tests. Qualified SHA-256
is `5B0F5505811EB938A9FDC097B330ECFBD4CFFA0CD7409E9BD1305798FAD35A94`
for `progpu_native.dll` and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDDBC`
for the wgpu-native runtime DLL.

## Architecture

```text
LibreWPF source-built PresentationCore
  DUCE command producer / typed portable producer
             |
             | canonical MIL batch bytes
             v
ProGPU.Native.MIL (C++20)
  framing validator -> channel handle table -> retained resource graph
      -> render-data decoder -> semantic scene lowering -> damage tracking
             |
             | ProGPU semantic scene stream ABI
             v
ProGPU.Native C++ compositor
  wgpu-native adapter       provider-resolved Dawn adapter
                                      |
                           Windows Dawn D3D12 / DXGI path
```

The native MIL layer is backend-neutral. It produces the same semantic scene
stream used by current C++ samples and the managed `ProGPU.Scene.Native`
compiler. Backend selection, device loss, external-image binding, submission
lifetime, hit testing, and render-target ownership remain in the native
compositor.

## Delivery stages and gates

### Stage 0 — protocol foundation (implemented)

- Complete command-ID namespace (`0x01`–`0x8d`).
- Generated command declarations and packed managed packet metadata sourced
  from the checked-in WPF MCG outputs, with standalone and superproject drift
  checks.
- Zero-copy, bounds-checked batch reader.
- Transactional channel state: a rejected batch cannot partially mutate the
  live graph.
- Size-versioned C ABI exported by both native renderer modules and a typed
  allocation-free .NET batch submission owner in `ProGPU.Backend.Native`.
- Initial typed resource, visual, generic-target, and opaque render-data state.
- Strict failure for unknown commands, unsupported commands, invalid handles,
  type mismatches, invalid graph operations, and malformed sizes.

### Stage 1 — complete retained 2D resources

The first Stage 1 vertical slice is implemented in the typed C++ API, the
size-versioned C ABI, and `NativeMilChannel.CompileScene(...)`. It
decodes the exact WPF `MILCMD_SOLIDCOLORBRUSH`, nested
`MILCMD_DRAW_RECTANGLE`, and nested `MILCMD_DRAW_ELLIPSE` records, applies
retained visual offsets and opacity, supports balanced nested
`MILCMD_PUSH_OPACITY`/`MILCMD_POP` scopes, walks the target's visual tree with
cycle/depth validation, and emits the shared pointer-free ProGPU semantic scene
stream. `MILCMD_DRAW_ROUNDED_RECTANGLE` is also lowered with either the
uniform analytic primitive or the exact positive non-uniform vector-path lane.
Ellipse centers/radii are converted exactly to native analytic
bounds, and every primitive kind is reported separately in typed scene metrics.
Scope opacity uses one native isolated layer and is applied once when its
subgraph is popped, preserving overlap semantics instead of multiplying every
draw independently. Retained Visual opacity remains in native semantic state;
malformed opacity and over/underflowed mixed state/layer stacks fail closed.
Typed `MILCMD_MATRIXTRANSFORM`, `MILCMD_TRANSLATETRANSFORM`,
`MILCMD_SCALETRANSFORM`, `MILCMD_SKEWTRANSFORM`,
`MILCMD_ROTATETRANSFORM`, variable-size `MILCMD_TRANSFORMGROUP`,
`MILCMD_VISUAL_SETTRANSFORM`, and nested `MILCMD_PUSH_TRANSFORM` are also
implemented. `MILCMD_DOUBLERESOURCE` and `MILCMD_MATRIXRESOURCE` supply current
animated field values for those transform packets; a nonzero animation handle
replaces its corresponding base packet value exactly. Leaf values are
range-checked and quantized to the same float matrix state used by WPF MIL.
Transform groups retain ordered child handles and resolve them on demand in WPF
row-vector collection order, so child or animation-resource updates are visible
without flattening or rebuilding the group. Cycles, deletion of a live child or
animation dependency, excessive recursion, missing/wrong-type nonzero handles,
nonzero packet padding, and unbalanced scopes fail closed transactionally.
Resolved transforms compose as local visual transform, visual offset, parent
transform, and then nested drawing scopes; draw culling bounds are the
axis-aligned bounds of all four transformed primitive corners.
Transform handle zero retains WPF's defined balanced no-op scope. Animated
transform fields on linear and radial gradient brushes are resolved through
the same retained transform graph. Other animated brushes and pens and other
nested commands deliberately fail closed until their typed resources are
implemented.
The slice is covered by byte-level fixtures that check semantic
brush, transform/opacity state, rectangle, ellipse and rounded-rectangle
primitives, transformed bounds, nested scope, rollback, scene identity,
generation, and tree metrics. The C ABI supports an explicit required-size
query, preserves the original 32-byte metrics caller contract when appending
new metrics, and writes into caller-owned storage; the managed owner returns
the completed semantic stream with typed compilation metrics for direct native
compositor submission.

The current Stage 1 slice implements the exact 52-byte `MILCMD_PEN` resource,
variable-size `MILCMD_DASHSTYLE` resource, and 44-byte nested
`MILCMD_DRAW_LINE` packet for unanimated solid-brush pens. Flat,
square, round, and triangle start/end caps map directly to the reusable ProGPU
geometry-stroke flags; pen brushes share the semantic brush table, and line
width/cap-conservative local bounds are transformed through the same four-
corner affine path. Nonempty dash resources route through ProGPU's reusable
semantic connected-stroke engine, preserving thickness-relative intervals,
offset, dash cap, odd-pattern repetition, and backend-independent execution.
Null pen handles and zero-width/null-brush pens are no-op draws. Thickness and
dash-offset animation, invalid dash values/enums, unresolved handles, nonzero
padding, and zero-total-length dash cycles fail closed transactionally.
Zero-length lines use WPF's horizontal shape-space direction and compose the
configured non-Flat start/end point caps from native cap primitives. For finite
nonzero dash patterns, the offset selects the initial dash/gap exactly, with an
offset on a boundary belonging to the preceding interval as in WPF's
`CDashSequence::Initialize`. Flat/Flat and initial-gap cases are exact no-ops.
The size-stable MIL scene metrics ABI now publishes `line_count` in its former
reserved tail field.

Canonical `MILCMD_LINEARGRADIENTBRUSH` and `MILCMD_RADIALGRADIENTBRUSH`
resources are retained as typed native state. The decoder validates their
84-byte and 108-byte fixed packet prefixes, respectively, plus the exact
24-byte `MilGradientStop` payload stride. `MILCMD_POINTRESOURCE` and
`MILCMD_DOUBLERESOURCE` replace current point, opacity, and radial-radius values
on every scene compilation; referenced transform resources remain live without
retransmitting the brush packet. Deletion of a referenced animation or
transform is rejected transactionally.

Each brush use resolves WPF's ordering in geometry space: relative coordinates
are mapped through the drawing bounds, `RelativeTransform` is conjugated by
those bounds, absolute `Transform` is appended, and the inverse draw and brush
matrices become the shared shader coordinate transform. Fill and ordinary
nondegenerate pen paths therefore share one bounds-correct material across
analytic rectangles/ellipses, retained paths, geometry groups, combined
geometry, lines, and rounded rectangles. Pad, Reflect, Repeat, sRGB, scRGB,
anisotropic radii, and focal origins lower to the existing backend-neutral
ProGPU brush ABI and execute in the same `Vector.wgsl` path for wgpu-native and
Dawn/DirectX.

Gradient stops are stably sorted after WPF double-to-float position
quantization. Out-of-range endpoints are clamped or interpolated in the selected
WPF color space, scRGB packet colors are converted to the shader's sRGB storage
contract, zero stops become an empty material, and one stop becomes a solid
material. The native compiler now directly follows WPF
`CreateWellFormedGradientArray`: positions are coincident only when the strict
relative `10 * FLT_EPSILON` comparison succeeds, redundant middle colors in a
coincident chain are removed, and the retained first/last pair is assigned one
exact offset for a hard transition. Duplicate endpoint groups retain WPF's
asymmetry: the left-most color at zero and right-most color at one are the Pad
outside colors, while the opposite colors remain the exact in-range endpoint
stops. A validated `0x40000000` spread flag stores those two outside colors in
the canonical brush's existing `Colors[0..1]`; `Vector.wgsl`, `Hatch.wgsl`, and
native `Native3D.wgsl` sample them only for `t < 0`/`t > 1`, so the 256-byte ABI
does not widen. This small resource-compilation pass intentionally remains
scalar because stable ordering and the previous normalized stop control every
subsequent decision; unlike the pixel, glyph, and geometry hot loops, it has no
independent SIMD lanes. Oracle tests cover stable sorting, a three-stop
near-coincident chain, a beyond-tolerance pair, and both endpoint directions.
The default native hardware sample now makes its second retained rectangle a
Pad gradient whose span is narrower than the geometry. Its Apple M3 Pro Metal
readback records start outside `250/133/20`, in-range start `0/255/4`, in-range
end `0/255/253`, and end outside `184/51/245`; this keeps the shader branch in
the normal macOS/Windows/Linux native sample gate instead of relying only on
stream inspection.

Exact checkpoint `ba7b5d74f40d554a6267aeabe3807fe989260cc4` was then
qualified in the Windows 11 ARM64 Parallels guest from archive SHA-256
`9A22CC63BB972FD2549C937B88503F4284D8AB3A1874182A87BC9D1EE4376D01`.
Strict MSVC/Ninja rebuilt both native providers, all 11 CTests passed, and the
native D3D12 sample on `Parallels Display Adapter (WDDM)` produced the exact
same four RGB samples as Metal. The complete managed test graph built with zero
warnings/errors and the eight focused stream-builder, 2D gradient, ordinary
Mesh3D gradient, and specular Mesh3D gradient tests passed on D3D12. The staged
provider hashes are
`F46B10C0B21D171D4AF1830F85D7499BF4BE4E43B550A53B3D27145340657EEB`
and
`B32E22C7BCF4A11F7BB64D60199670DEE3E9DDA0718FC006190A55069CDE27DF`.
Cap-only degenerate pen strokes now use the same WPF stroke bounds for both
relative-brush realization and semantic draw culling. WPF's `DrawShape` path
passes `GetStrokeBounds(...)` to a stroke brush that needs bounds; for a
zero-length centerline the existing native cap convention is oriented along
local +X, expands by half the thickness toward each non-flat endpoint, and
expands by half the thickness on both Y sides. A point-degenerate ellipse uses
the corresponding round/round square. One checked helper owns that calculation
for the brush and emitted cap geometry, so an asymmetric Flat/Triangle line and
a round point ellipse cannot drift between mapping and culling. Linear/radial
materials continue through the ordinary canonical GPU brush/stop buffers and
shared fragment shader; there is no CPU rasterization, readback, new ABI, or
backend branch. The calculation is constant-size scalar control work rather
than a lane-independent buffer loop, so SIMD is not applicable. Native MIL
regression coverage asserts the exact relative-gradient endpoints and draw
bounds for both shapes; all eight locally configured CTests pass on Apple
Silicon.

Exact implementation `a124dcb905dc9ef3156856c8685672c3b1feee20` was
qualified in the Windows 11 ARM64 Parallels guest from archive SHA-256
`D38337F1AFB33F7E5C4DA9D6BC08D65AEBC544C4E9E5881CE2FD3BF56A672832`.
Strict MSVC/Ninja rebuilt both native providers without warnings or errors and
all 11 CTests passed, including the asymmetric Flat/Triangle line and round
point-ellipse gradient regression. The native D3D12 sample selected
`Parallels Display Adapter (WDDM)` and reproduced Metal's exact four Pad
samples: `250/133/20`, `0/255/4`, `0/255/253`, and `184/51/245`. The full
managed graph built with zero warnings/errors in 3:52; one builder, five 2D
gradient, one ordinary Mesh3D-gradient, and one specular Mesh3D-gradient test
then passed on D3D12. The specular readback retained 3,304 red-dominant and
3,304 blue-dominant pixels with maximum channel deltas of 134. A first combined
managed test host encountered a native `wgpuDevicePoll` access violation after
the independent native sample. Fresh isolated hosts passed every group,
including the exact ordinary test at the crash site, identifying the event as
VM/provider process-lifetime interference rather than a reproducible MIL or
shader failure. Qualified provider hashes are
`8213074DAB22FBBAD630BEAF8BF87E09522B77730E7D92E5E33812BC9C68590D` for
`progpu_native.dll` and
`0E2C0667243F49475E81B23FF7E56999F7E4095D906B1A283637EB7CC148B47E` for
`progpu_native_dawn.dll`.

The EvenOdd boolean-child implementation at exact ProGPU commit
`1c3bd210932fcd90400696af1bdaf2a18a98c2fd` was qualified in the Windows 11
ARM64 Parallels guest from archive SHA-256
`71443727B66A565CF9D270807976859460B29EFBCBB84511630748A830B2CD37`.
Strict MSVC/Ninja completed all 312 build steps for both native providers and
all 11 CTests passed, including the exact five-node
`leaf leaf difference leaf xor` program for both an EvenOdd fill and vector
clip plus the Nonzero fail-closed regression. The native D3D12 sample selected
`Parallels Display Adapter (WDDM)` and again produced the exact Metal Pad
samples `250/133/20`, `0/255/4`, `0/255/253`, and `184/51/245`. The serial
managed graph built with zero warnings/errors in 4:13.66. As at the preceding
checkpoint, the first combined managed host encountered the unchanged
`wgpuDevicePoll` access violation after the independent native sample; four
fresh hosts passed the builder test, all five 2D gradient tests, ordinary
Mesh3D gradient, and specular Mesh3D gradient. The specular evidence remained
3,304 red-dominant and 3,304 blue-dominant pixels with maximum channel deltas
of 134. Qualified provider hashes are
`D00CEAB00E6E06C18E49D3952DB80A2593B53727BA23B67EB1914013E76AC828` for
`progpu_native.dll` and
`F17C61D361C9C5F51B19E4B602FA052C55A614895886B27DBDD5E8C7B6182FC5` for
`progpu_native_dawn.dll`.

Canonical `MILCMD_GEOMETRYDRAWING` resource `87` and nested
`MILCMD_DRAW_DRAWING` command `0x4a` are retained as typed native state. A
geometry drawing keeps nullable brush, pen, and geometry handles and resolves
them through the same native geometry lowering used by `DrawGeometry`; a null
geometry is the WPF-defined no-op. The channel rejects wrong resource types and
deletion of a live brush, pen, or geometry dependency transactionally. Other
drawing resource kinds remain unsupported and fail closed rather than being
adapted through managed object inspection.

Axis-aligned `MILCMD_DRAW_RECTANGLE` records now accept independent fill and
pen handles. Rectangle pens lower to closed four-point semantic polylines, so
solid and dashed outlines share ProGPU's native join, miter-limit, dash-cap,
offset, odd-pattern, transform, and backend execution rules. Fill-only,
stroke-only, and fill-plus-stroke records remain distinct draws with one shared
brush table; stroke culling expands the local rectangle by half the pen width
before the four-corner affine bounds transform. A solid zero-width or
zero-height rectangle uses WPF's optimized widened-shape result directly: the
degenerate contour has no inner boundary, so ProGPU emits the one exact outer
vector fill. Miter and Bevel joins preserve `Get90DegreeBevelOffset()` and its
miter-limit clamp; Round joins preserve the rounded outer path. Nonempty dash
patterns on one-axis sharp rectangles now retain the original four-point
closed contour and use the shared connected-stroke engine. Its exact reversal
join emits WPF's half-width square for Miter/Bevel and incoming semicircle for
Round. A fully collapsed sharp rectangle now shares WPF's wholly-degenerate
closed-figure rule: a visible initial dash emits a Round/Round point disk and
an initial gap emits no draw, independent of LineJoin and DashCap. Rounded
degenerate rectangle dashes remain fail closed pending their distinct
collapsed-contour traversal.

`MILCMD_DRAW_ELLIPSE` records likewise accept independent fill and pen handles.
Solid ellipse pens lower to ProGPU's exact analytic full-ellipse arc primitive,
including non-uniform radii and affine semantic-state execution. Fill-only,
stroke-only, and fill-plus-stroke records share the native brush table; stroke
culling expands the local ellipse bounds by half the pen width before the
four-corner affine bounds transform. A nonempty dash pattern on a positive-area
ellipse now lowers the full circumference to one closed analytic arc contour
and reuses the native curve-dash compiler, preserving phase, DashCap, affine
state, and exact retained arc spans across the seam. Degenerate ellipse fills
produce no coverage. A solid one-axis ellipse lowers to the exact round-ended
capsule implied by WPF's four SmoothJoin cubic segments; a fully collapsed
ellipse uses the same Round/Round point-disk path as the native widener. Both
retain their geometry-local affine transform without curve flattening.
Nonempty dashes on a one-axis ellipse now traverse its four collapsed quarter
arcs as an exact center/end/center/end closed polyline with forced WPF smooth
Round joins; dash phase, DashCap, affine state, and closed-seam merging remain
in the shared connected-stroke lane. A fully collapsed ellipse keeps the
existing WPF point-disk path when its initial dash interval is visible and is
an exact no-op when the initial interval is a gap.

Uniform-radius `MILCMD_DRAW_ROUNDED_RECTANGLE` records now accept independent
fill and pen handles. Positive-radius solid outlines lower to ProGPU's exact
rounded-rectangle analytic primitive with native stroke thickness, including
affine semantic-state execution and bounds expanded by half the pen width.
Fill-only, stroke-only, and fill-plus-stroke records share the native brush
table. If either radius is zero on a positive-area rectangle, WPF's
`CShape::AddRoundedRectangle` normalizes the shape to a sharp rectangle before
widening; native fill therefore uses the analytic rectangle and stroke keeps
the closed-polyline rectangle path so WPF join and dash metadata are preserved.
Degenerate uniform-radius solid outlines use the same WPF outer widened path
with separately clamped X/Y corner radii, retaining
analytic quarter arcs under affine transforms. Positive independent X/Y radii
reuse the shared vector path and connected-curve stroke lanes. Nonempty dash
patterns on positive-area uniform and independent-X/Y rounded rectangles now
reuse that same closed analytic line/quarter-arc contour and native curve-dash
compiler. Degenerate records with both radii positive now preserve WpfGfx's
canonical 17-point alternating cubic/line contour after independent radius
clamping and reuse the same native curve-dash compiler. Point records reduce
to the visible-initial-dash Round/Round disk or initial-gap no-op. When either
radius is zero, including asymmetric degenerate records, WpfGfx normalizes the
shape to a sharp rectangle before widening; ProGPU now routes those records to
the qualified sharp one-axis or point lane.

The retained fixed-geometry slice implements the exact fixed-size
`MILCMD_LINEGEOMETRY`, `MILCMD_RECTANGLEGEOMETRY`, and
`MILCMD_ELLIPSEGEOMETRY` updates plus nested `MILCMD_DRAW_GEOMETRY`. Each
resource retains its primitive state and optional typed matrix-transform
handle. Line fills remain empty while solid and dashed pens reuse the same
stroke path as `MILCMD_DRAW_LINE`. Rectangle and ellipse resources reuse the
native analytic fill/stroke lowering used by their immediate draw commands,
including uniform rounded rectangles and geometry-local affine transforms.
Positive non-uniform rounded rectangles reuse the same path fill and connected
curve stroke lane. Positive-area zero-axis asymmetric records normalize to the
same sharp rectangle fill/stroke lane as immediate draws. Animated fields,
degenerate zero-axis asymmetric radii, uninitialized or wrong-type resources
fail closed transactionally.

The first retained general-path slice implements canonical variable-size
`MILCMD_PATHGEOMETRY` updates and nested fill-only `MILCMD_DRAW_GEOMETRY`.
The native channel validates WPF's `MIL_PATHGEOMETRY`, `MIL_PATHFIGURE`, fixed
line/quadratic/cubic/arc segments, and poly-line/poly-quadratic/poly-cubic
record links and sizes before committing retained state. Filled contours lower to one
backend-independent ProGPU semantic path batch with EvenOdd/Nonzero fill,
geometry-local affine transforms, and WPF's implicit fill closure for open
figures. Scene compilation now derives local fill and brush-mapping bounds from
the emitted fill segment span: line endpoints, analytic quadratic/cubic
extrema, and exact arc extrema. Packet bounds, unfilled figures, and Bezier
control-point hulls cannot broaden direct or grouped retained fill bounds;
zero-area fill spans become no-ops while their independently retained stroke
topology remains available. Malformed back-links, sizes, padding, flags,
counts, bounds, transforms, and handles roll back transactionally.
Endpoint arcs reuse the neutral native arc resolver shared with SVG glyph
paths, retain center/radii/sweep data in ProGPU's semantic arc record, and
preserve the degenerate-to-line rule. Fill compilation remains independent of
the separately retained stroke topology described below.

The first retained path-pen slice now keeps WPF stroke topology separately
from fill topology while decoding the same canonical figure stream. Line and
poly-line segments lower to ProGPU semantic polylines with the retained pen's
thickness, miter limit, start/end/dash caps, join, thickness-relative dash
intervals/offset, brush, and geometry-local affine transform. `SegIsAGap`
splits open figures into independent runs, using the pen's dash cap at each
gap boundary and preserving the figure start/end caps only at true open-figure
endpoints. Each run restarts the dash sequence as WPF's `CDasher::StartFigure`
does after a geometry gap. For closed figures the
decoder rotates after a gap and joins the stroked tail, implicit closing edge,
and stroked head into one open run, avoiding a false cap at the original figure
start while retaining dash caps at the actual gap. Fully stroked closed figures
remain one closed contour, and open figures
are never implicitly closed for stroking even though WPF fill closure remains
unchanged. A nonempty dash pattern on a closed gapped figure whose continuous
run crosses the original figure start fails closed: WPF resets dash phase at
that start yet joins the two widened pieces, which cannot be represented by
one current semantic polyline without changing phase or join shape. The native
curved-stroke slice retains each exact segment record beside the line topology
and lowers solid line/quadratic/cubic/analytic-arc contours to reusable ProGPU
geometry primitives. Multi-segment and closed contours compose those
primitives with native path-join records. Join tangents come from the exact
segment derivative at each shared endpoint: line direction,
quadratic/cubic endpoint-control fallbacks, or the resolved analytic arc axes
and sweep. A closed contour also emits the final-to-first join, while an open
contour emits native path-cap records for Square, Round, or Triangle start/end
caps using those same exact endpoint tangents; Flat caps remain implicit.
Geometry-gap boundaries use the pen's typed dash-cap value even when the dash
interval list is empty. Geometry-local affine transforms stay on every
primitive, cap, and join; arcs map their resolved center/radii/rotation/angles
into the reusable two-axis analytic arc contract. Discontinuous endpoints or
degenerate tangents adjacent to nondegenerate cap/join composition fail closed
transactionally. A wholly degenerate solid open contour instead composes its
typed start/end (or gap-boundary DashCap) pair around WPF's horizontal
shape-space direction. A wholly degenerate solid closed contour forces
Round/Round caps, matching `CWidener::WidenClosedFigure` and yielding an exact
point disk independent of the pen's line caps. `SegSmoothJoin` is retained per
incoming segment and forces only that endpoint's join to Round, matching WPF
`CWidener::DoSegment`/`CSimplePen::DoCorner`; the closing join uses the last
segment's flag. Finite nonzero dash patterns on wholly degenerate contours use
the same exact initial dash/gap selection as immediate lines. Finite nonzero
dash patterns on nondegenerate retained contours now pass through the native
curve-dash compiler. It scales intervals and offset by the resolved pen
thickness, repeats odd lists logically, normalizes zero entries with the shared
ProGPU epsilon rule, and carries phase across connected line, quadratic, cubic,
and analytic-arc segments. A 32-chord cumulative-length table for each Bézier
and a bounded 64-entry analytic-arc table match the managed ProGPU reference;
only the distance-to-parameter lookup is sampled. Visible quadratic/cubic spans
are retained with De Casteljau subdivision and visible arcs retain exact
center/radii/rotation/sub-sweep data, so final rendering does not flatten a
curve into a polyline. Each visible run reuses the existing native curve body,
cap, and join primitives. True open endpoints keep StartLineCap/EndLineCap,
interior run endpoints use DashCap, SmoothJoin remains Round when a visible run
crosses that source join, and first/final visible runs merge across a closed
seam without coincident caps. Invalid patterns and allocation failure still
fail closed. Unstroked curves remain valid topology gaps and do not prevent
neighboring line runs from using the native path-pen lane.

The native dash compiler stores run metadata, exact curve segments, and join
flags in three flat reusable buffers. It no longer creates two child vectors
for every visible run. One scratch arena is shared by all dashed path commands
in a render stream, so capacity grows only to that stream's high-water mark and
is then reused without touching the transactionally copied channel state. A
dense 256-segment differential fixture produces 64 visible runs, 192 exact
segments, and 128 joins; 32 subsequent compilations must retain identical data
pointers and capacities for all three buffers. This is a structural
steady-state no-allocation contract, not a timing-based speed claim. The
distance tables remain bounded scalar prefix accumulations because each entry
depends on the preceding cumulative chord length; final coverage remains in
the shared GPU geometry path rather than a CPU pixel loop.

This implementation is a C++ port of the ProGPU-owned managed algorithms in
`BezierSegmentGeometry`, `ArcSegmentGeometry`, `DashPattern`, and
`Compositor.TryCreateDashedStrokePath`; it does not copy third-party source.
The semantic decisions were cross-checked against the
[WPF `DashStyle.Dashes` contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.dashstyle.dashes),
[Direct2D custom stroke styles](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1strokestyle),
[Skia dash path effects](https://api.skia.org/classSkDashPathEffect.html), and
[SVG stroke dashing](https://www.w3.org/TR/svg-strokes/#StrokeDashing).

The first retained `MILCMD_GEOMETRYGROUP` slice validates the canonical
variable child-handle payload, group fill rule, optional matrix transform,
typed geometry dependencies, and cycles transactionally. At execution, groups
whose children are identity-local retained `PathGeometry` resources aggregate
their contours into one semantic path batch, so the group's EvenOdd/Nonzero
rule is applied across child overlap exactly as WPF's `CShape` aggregation
does. Affine-transformed line/quadratic/cubic path children are baked into that
shared coordinate space exactly, including WPF's implicit closing fill edge;
their baked segment spans are reduced to exact post-transform curve bounds.
Fixed rectangle and ellipse children join that same batch, including
geometry-local affine transforms and non-uniform rounded rectangles. Rectangle,
rounded-corner, and ellipse contours use WPF's `CFigureData` point order,
radius clamping, and `ARC_AS_BEZIER` cubic constant before applying the child
matrix; line children correctly contribute no fill. The group transform remains
one native path transform. Nested groups recurse through the same bounded
lowerer, compose each group/leaf transform in WPF order, and intentionally
ignore nested fill rules before applying the outer root fill rule. This matches
`CMilGeometryGroupDuce::GetShapeDataCore`, which copies every child figure with
`CShape::AddShapeData` and calls `SetFillMode` only on the resulting outer
shape. Nonzero groups keep that shared contour batch because cross-child winding
cancellation is significant. EvenOdd groups compile the same child leaves into
an exact postfix XOR program; XOR of the child-inside predicates is the outer
EvenOdd result and bounds each raster operation to its own leaf without changing
WPF parity semantics. Every pure left-fold XOR program from two leaves through
the existing 32-child MIL ceiling now executes through a typed split-GPU
program when its overlapping leaves are translated-equivalent: the compiler
emits one ordinary record per leaf, the backend rasterizes the same ordinal
leaf across all split programs in one submitted GPU phase, and a final packed-
coverage compute phase loops over the program's bounded source range and XORs
the masks before the atlas copy. Pending semantic work is flushed once before
those phases and a fresh semantic encoder is restored afterwards. The split is
fixed by phase, not by item; split buffers and bind groups grow only to the
largest qualified leaf count in the batch, while ordinary paths retain the
original single-submission fast path and do not reserve split-program vectors.
Mixed postfix operations that contain this driver-sensitive overlap remain
guarded before scene submission; boolean operand leaves are not conflated with
sibling group contours. Nonsingular
affine transforms on native arc records are
baked without flattening: ProGPU transforms the arc's two ellipse basis vectors,
factors the resulting `T*T^T` metric into orthogonal output axes/radii, projects
the start parameter into that basis, and reverses the sweep exactly when the
affine determinant is negative. A translation-only fast path preserves the
source radii, axis, angles, sweep, and padding bit-for-bit while translating only
the endpoints and center. An EvenOdd group may now contain a CombinedGeometry
child: ordinary descendants retain the compact outer-fill contour leaf, while
the boolean child retains its existing postfix subtree and each subsequent
nonempty child adds one group-level XOR node. DrawGeometry fills and vector
PushClip use this same bounded compiler, including per-point guideline segments
for fills. A `GeometryGroup` containing `PathGeometry`, `LineGeometry`, and
positive-area rectangle/ellipse children may now carry a meaningful pen. The
compiler preserves each path child's original open/closed stroke contours,
treats each line as its own open figure, and routes plain rectangles, analytic
ellipses, and uniform/nonuniform rounded rectangles through the same typed
fixed-shape stroke helper used by direct draw commands. Nested groups recurse
through the same leaf dispatcher up to the existing 256-level visual bound.
Leaf, inner-group, root-group, and drawing transforms compose in WPF order;
singular nested branches contribute no coverage. Dash state resets at each
figure, one root pen brush resolves against the recursively aggregated group
bounds expanded by the existing native miter envelope, and fill submits before
stroke. This mirrors
`CMilGeometryGroupDuce::GetShapeDataCore`, which appends every child figure to
one `CShape`, and `CDrawingContext::DrawGeometry`, which computes aggregate
stroke bounds and strokes that shape after its fill. Native scene fixtures
cover filled and explicitly unfilled closed figures in the same group,
different child transforms, shared brush resolution, exact solid curve bodies,
an independently transformed solid/dashed line, and dashed curve bodies/caps
on both path children. The fixed-shape coverage additionally requires a
20-segment group fill plus solid and dashed plain rectangle, ellipse, and
nonuniform rounded-rectangle strokes under independent transforms. A nested
group fixture reuses the line leaf under an additional translation and requires
the exact composed transform in both solid and dashed output. Collapsed
rectangle/ellipse shapes and boolean geometry remain fail closed until those
stroke contours can be composed without approximation. Exact singular affine
transforms now lower fill and stroke coverage to empty, matching WPF's
zero-determinant area semantics without attempting to invert or factor an arc
basis.

Canonical fixed-size `MILCMD_COMBINEDGEOMETRY` state now retains the optional
matrix transform, two geometry dependencies, and WPF Union/Intersect/Xor/
Exclude operation. Null operands become explicit empty leaves. Identity-local
`PathGeometry` operands lower to ProGPU's native postfix boolean program,
preserving each operand's own fill rule and executing the result in the native
path atlas on every backend. The same shared shallow
fill lowerer now accepts fixed rectangle and ellipse operands, including
geometry-local affine transforms and non-uniform rounded rectangles with WPF's
radius clamping, point order, and cubic constant; line operands become explicit
empty leaves. It also accepts affine-transformed line/quadratic/cubic path
operands using the same exact point/bounds baking as groups while preserving the
operand's own fill rule. A recursively flattened `GeometryGroup` is also a
boolean leaf: its root fill rule is retained while its group/child transforms
are baked into the leaf contours. Combined operands now recurse into that same
bounded geometry DAG and append their two subtrees plus operation in postfix
order. Nested combined transforms compose into descendant leaf transforms;
segment/node appends roll back together on failure, and conservative bounds
cover all nonempty descendants. Group/combined references share one cycle-
checked geometry DAG; deletion and malformed operation updates fail
transactionally. The same exact nonsingular affine arc factorization is shared
by group leaves and arbitrary-depth boolean leaves, including reflected/sheared
arcs and sweep reversal. Singular transformed operands become exact empty
leaves. Stroked operands remain fail closed. Combined children inside an
EvenOdd `GeometryGroup` preserve the child predicate and combine it with
ordinary child predicates through XOR; they are never flattened into raw
contours. Nonzero groups compile ordinary descendants as raw signed-winding
leaves and well-oriented CombinedGeometry results as `+1` predicates, then add
the contributions before the final Nonzero test. A reflected GeometryGroup
outside a boolean child inserts an exact winding negation; a reflection owned
by CombinedGeometry remains inside WPF's boolean solve and is normalized back
to `+1`. Nested GeometryGroup fill rules remain intentionally ignored while an
operand GeometryGroup reached through CombinedGeometry applies its own root
rule. The compiler retains the existing 32-child, 63-instruction, and 16-stack
limits and rolls segment/node appends back together on failure.

This extension preserves the existing numeric values and 48-byte node layout,
appending only typed winding-leaf/add/negate enum values. Backend selection and
CPU raster fallback are unchanged. The native compiler performs one bounded,
order-dependent `O(S + N)` walk over segments `S` and postfix nodes `N`; the
dependency chain is not an independent-lane SIMD candidate. Pixel coverage
remains on the shared GPU path rasterizer on Metal, D3D12, Vulkan, and browser
WebGPU. Exact signed programs default to the bounded inline vector evaluator,
which has no intermediate leaf storage. A typed forced compatibility policy
uses three bounded GPU stages: vectorized raw winding per leaf, eight-lane
postfix evaluation per supersample row, and R8 coverage packing. That staged
path retains 64 signed words per leaf texel plus one two-word predicate mask,
without CPU readback or repacking. The shared path/clip/glyph atlas staging
keeps 256-byte row pitch and separately aligns every copy source offset to 512
bytes for D3D12 placed-texture-footprint parity. The split form for
ordinary boolean programs still adds one packed-u32 compute entry point and
bounded phase submissions, with no CPU readback, repacking, or per-child/per-
item submission. See
[`NATIVE_MIL_NONZERO_BOOLEAN_WINDING.md`](NATIVE_MIL_NONZERO_BOOLEAN_WINDING.md)
for the WPF oracle, transform proof, ABI encoding, and gates.

The focused exact-winding hardware gate passed on Apple M3 Pro Metal and the
Windows 11 ARM64 Parallels WDDM D3D12 adapter with identical dark `5/6/10` and
cyan `51/209/242` probes for mask cancellation/islands, direct-fill
cancellation/islands, and Nonzero-versus-EvenOdd double contours. The Windows
reduction also proved that byte offset 72,960 failed only at the buffer-to-R8
copy while the 512-aligned offset 73,216 completed; the portable allocator now
enforces that placement rule for paths, clips, and glyphs.

The provider-resolved Metal hardware gate now uses the same five-node
`leaf leaf difference leaf xor` program in its retained vector-mask fixture.
It asserts cyan coverage in the surviving outer region, clear coverage in the
Difference hole, and clear coverage where the final XOR island overlaps that
region. The WebScene Dawn provider selected Apple M3 Pro and completed the
live render/readback gate in 3.45 seconds. This qualifies actual shared-WGSL
boolean evaluation rather than only the MIL-to-scene packet compiler.

The standard cross-platform native hardware sample now carries the same
postfix program in a small isolated vector-mask layer so Metal, D3D12, and the
Linux/Vulkan lane share one pixel oracle. Apple M3 Pro Metal reads the surviving
cyan point as `51/209/242` and both the Difference hole and XOR island as the
clear color `5/6/10`; the established four Pad-gradient samples remain
unchanged. The scene executes seven draws from twelve retained commands and
uploads 12,064 vertex bytes. This adds no CPU mask construction, readback on
the rendering path, shader variant, or public ABI; readback exists only in the
validation executable.

Exact sample checkpoint `3bd6bb4084adc545a4555876c7fd4284a7f8c915`
was then qualified in the Windows 11 ARM64 Parallels guest from archive
SHA-256
`B8740F7C484A1B763253185C1DBC395D07A0016B4E691CB86A271F5ABAEEDF89`.
Strict MSVC/Ninja completed all 312 build steps for both providers and all 11
CTests passed. The direct D3D12 sample selected
`Parallels Display Adapter (WDDM)` and reproduced Metal exactly: boolean inside
`51/209/242`, Difference hole `5/6/10`, XOR island `5/6/10`, plus the unchanged
four Pad-gradient pixels. It executed seven draws from twelve retained commands
and uploaded 12,064 vertex bytes. This focused rerun covers the sample-only
checkpoint after the compiler and managed graph qualification above. Qualified
provider hashes are
`C5E90611B1BDB249DB940A11AC6F8C4C5816392FF14BE9A7D5A5246AAD177991` for
`progpu_native.dll` and
`C29207284FDDC19E193A131651F7A70E10ECABF12D1BD9816A6954E3E6808655` for
`progpu_native_dawn.dll`.

The shared WGSL path rasterizer keeps these arcs analytic. It rejects samples
outside each path record's exact bounds, rejects quadratic and cubic work on
rows outside the curve control hull, and tests arc sweep membership with
oriented cross products of the normalized start/end vectors rather than
per-crossing trigonometric reconstruction. The existing half-open endpoint and
derivative rules are unchanged, and the complete path-atlas parity fixture
retains its prior pixel result.

`NativeMilBatchBuilder` and `NativeMilRenderDataBuilder` provide the matching
managed producer for this supported subset. They write the canonical WPF
framing and packed field offsets directly into reusable buffer writers, expose
only typed resource/color/primitive inputs, and are shared by package smoke
tests so LibreWPF does not need private-structure probes or hand-coded arrays.

`MILCMD_PUSH_CLIP` keeps axis-aligned, non-rounded rectangles on the semantic
clip-rectangle fast path. Other retained fixed geometry, paths, recursive
groups, and recursive combined geometry compile to retained vector-mask paths
with the same analytic line/quadratic/cubic/arc segments, fill rules, and
bounded postfix boolean programs used by native fills. Each path is frozen in
logical target coordinates using the transform active at push time, and nested
paths are ordered intersections. Rectangle and vector-mask state remain
independent, so a cheap bounds clip can constrain exact vector coverage without
changing it. Scope records store only arena prefix counts and a retained mask
index; push/pop does not copy accumulated path data. Degenerate and singularly
transformed geometry lower to an exact empty clip; oversized boolean programs
fail closed, and geometry bounds are never substituted for coverage. Fixed
ellipses and rounded rectangles use analytic quarter arcs rather than cubic
circle approximations.

Canonical nested `MILCMD_PUSH_GUIDELINE_SET` now resolves a retained static
`MILCMD_GUIDELINESET` inside the transform active at the push, maps its X/Y
coordinates with WPF float quantization, and scopes the resulting semantic
guideline resource through the ordinary save/restore stack. A null handle
pushes an empty guideline frame, so it temporarily clears an inherited Visual
or drawing-group guideline without unbalancing the stream. The render-data
resource dependency hash includes the pushed handle, keeping retained cache
revisions sensitive to guideline updates. Dynamic guideline resources and the
specialized `MILCMD_PUSH_GUIDELINE_Y1`/`Y2` forms remain fail closed: they need
WPF's per-guideline last-offset, phase timestamp, three-phase subpixel
animation, non-snappable notification, and render-rescheduling state. Those
forms must not be approximated as static coordinates; the native target
scheduling ABI will carry the required clock/state in Stage 2.

Nested `MILCMD_PUSH_OPACITY_ANIMATE` is also decoded as a typed scope. Its base
opacity and optional `MILCMD_DOUBLERESOURCE` handle resolve on every scene
compilation and participate in retained cache dependency hashing. Constant and
animated opacity scopes both emit one isolated full-target semantic layer, so
their alpha is applied once to the completed subgraph and overlapping child
draws do not receive alpha independently. Updating only the double resource
changes the next layer descriptor without retransmitting render data. Null
animation handles preserve the packet's base value; wrong-type/missing handles,
nonzero padding, non-finite values, or values outside `[0,1]` fail closed.

Canonical nested `MILCMD_PUSH_OPACITY_MASK` now consumes WPF's retained
`MilRectF` local-content bounds and brush handle without reconstructing managed
brush objects. Static solid brushes fold to one isolated group-alpha layer;
typed linear/radial gradients compile to the existing GPU brush-mask resource
and are sampled over the packet bounds in the transform active at the push.
The semantic layer carries transformed bounds, applies the mask once at `Pop`,
and therefore preserves overlap semantics and nesting with opacity, clip,
transform, and guideline scopes. Updating only the retained gradient resource
regenerates the mask on the next scene compile while the render-data stream
stays unchanged. Missing/wrong-type resources, malformed LTRB bounds, and
unsupported brush families fail closed.

Retained `MILCMD_DRAWINGGROUP` opacity no longer multiplies alpha into each
child state. The group emits one isolated semantic layer around its typed
transform/clip/guideline child stream, and combines animated group opacity with
a static solid opacity-mask alpha only at the completed group composite.
Overlapping children therefore retain their internal source-over coverage
before group alpha is applied. Source-built WPF supplies exact local content
bounds through the typed
`progpu_native_mil_channel_set_drawing_group_bounds` /
`NativeMilChannel.SetDrawingGroupBounds` sideband. Linear and radial gradient
opacity masks then reuse the backend-neutral GPU brush-mask resource with those
local bounds and the group's active transform; a group packet update preserves
the bound metadata, and a resource-only gradient or DoubleResource update
changes the next scene without rebuilding the child stream. A spatial mask
without exact bounds, or an unsupported brush family, fails closed.

The portable producer contract distinguishes these pre-transform bounds from
the public post-transform drawing bounds through
`PortableDrawingGroupState.HasLocalBounds` / `LocalBounds`. Producers must not
bind `PortableDrawingGroupState.Bounds` to the native sideband: doing so would
apply the DrawingGroup transform twice. Source-integrated WPF implementations
should calculate local bounds with their native drawing-bounds walker so clips,
child transforms, strokes, and nested groups retain WPF bounds semantics.

Exact Windows qualification at implementation commit `b36b241b` rebuilt both
native exports with ARM64 MSVC and passed all 11 native/Dawn CTests. Both
checked-in export allowlists matched, and the project-reference package
consumer built with zero warnings before compiling the focused linear-gradient
DrawingGroup through the wgpu-native and Dawn MIL channels. The live Parallels
D3D12 render reported five semantic resources, two draws, zero coverage-staging
bytes, and a valid 16,384-pixel readback. Qualified SHA-256 values are
`F3FB0D077BE494A6D067C1526C96C56A10A0981E8B9283D8574ABF52FEEBFD85`
for `progpu_native.dll` and
`F002C1FB564334FF21E6F1B18E2FADFD067A955103531A7E1E55B4CC361D6DC8`
for `progpu_native_dawn.dll`.

The native retained 3D replay now treats semantic draw bounds as the actual GPU
viewport. Its camera storage carries target extent plus a localized physical
viewport rectangle; shared WGSL maps mesh/line clip coordinates into that
rectangle and computes line expansion from the viewport extent. Full-target
commands retain the original clip-space result exactly. This reusable ProGPU
primitive is the prerequisite for lowering source-built WPF
`PortableViewport3DScene` data without a bridge-local render-to-texture or CPU
projection workaround.

The framework-neutral managed Mesh3D path now evaluates linear and radial
material brushes in WGSL over mesh UVs. `MeshCompilationEntry.MaterialBrush`
retains the typed ProGPU vector brush; the compiler writes finite gradient
stops from reusable scratch directly into a bounded storage buffer and records
coordinates, inverse affine transform, spread, interpolation, opacity, and
stop range per mesh. A live Metal readback proves distinct red and blue regions
from one linear-gradient quad, while the pre-existing point/spot-light test
guards the expanded 560-byte record ABI. WinUI no longer approximates mesh
gradients with the first stop. Texture-plus-gradient ambiguity and unsupported
brush kinds fail closed; no CPU texture staging or readback is introduced.

The same 560-byte managed record now carries a typed
`MaterialBrushTarget3D` in `MaterialStopMetadata.z`; zero remains the existing
color target and one selects the specular target. This reuses the established
gradient-stop buffer without widening the ABI. The color target retains its
previous shader arithmetic path. A specular target preserves the mesh's
black diffuse pass, multiplies `SpecularColor` by the sampled linear/radial
brush, and applies the result in both the explicit WPF-light loop and the
default ProGPU light rig. Ambient and presentation-only rim contributions are
suppressed for that ordered specular-only pass. Invalid target values and a
non-color target without a typed brush fail closed. WinUI exposes the same
typed target on `DiffuseMaterial.BrushTarget`, and LibreWPF maps its neutral
specular material DTO directly to `MeshCompilationEntry.MaterialBrushTarget`.
No reflection, CPU texture, readback, or per-mesh submission is introduced.
The Apple M3 Pro Metal execution gate uses an opaque red-to-blue brush, black
diffuse RGB, and an explicit point light; its full Mesh3D run observes 3,300
red-dominant and 3,300 blue-dominant pixels with maximum channel deltas of 134
in both directions. The ordinary managed gradient execution test remains in
the same gate to protect the zero/default target.

Exact implementation commit `ed98df5d` is also qualified in the Windows 11
ARM64 Parallels guest from source archive SHA-256
`0EAA66E17840D35DE955854F31C0D9398115D4D7473D451218B363071B68AC50`.
The archive's pinned `microsoft-ui-xaml` gitlink is `25d2cb1c`; its only
required hydrated `generic.xaml` file matched the current submodule at
SHA-256 `4C4085838721C0AFCB1A9EE17591C0655CDDDADB26D330788E08BCD7F1AF8285`.
.NET SDK 10.0.400 rebuilt the complete managed test graph with zero warnings
and errors. All eight focused ordinary/specular compilation, validation, ABI,
and live tests passed. Both live contexts selected
`Parallels Display Adapter (WDDM)`, backend `D3D12`, device type
`DiscreteGpu`; the new specular readback contained 3,304 red-dominant and
3,304 blue-dominant pixels with maximum channel deltas of 134 and no WebGPU
validation/device error. The native C++ sources are unchanged from their prior
strict MSVC/D3D12 qualification.

Exact pushed implementation `8eee2170` is also qualified on Windows ARM64
from an isolated source archive plus the repository's exact pinned
`microsoft-ui-xaml` submodule file at `25d2cb1c`. .NET SDK 10.0.400 built the
complete `ProGPU.Tests` graph with zero warnings and zero errors. The focused
`FullyQualifiedName~Mesh3D` family passed 18/18 in 4.6601 minutes, including
typed linear/radial compilation, the live linear-gradient readback, point and
spot lights, planar video surfaces, and retained scratch reuse. A separate
diagnostic run of the live gradient test selected
`Parallels Display Adapter (WDDM)`, backend `D3D12`, device type
`DiscreteGpu`, and passed in 38.0304 seconds without a WebGPU validation or
device error. This gate proves the same WGSL gradient ABI on Metal and D3D12;
the native C++ route below now consumes the same canonical brush ABI.

Native C++ Mesh3D material replay preserves the public 256-byte mesh record
and reuses the existing 256-byte semantic brush plus 32-byte gradient-stop
records. An optional command payload suffix follows the unchanged camera
prefix with `progpu_native_scene_mesh_3d_materials` and one brush index per
mesh. Legacy camera-only streams therefore retain their exact white material
multiplier. The extended validator accepts only solid, linear, and radial
materials, validates every stop range, and fails closed for all other kinds.
The retained 3D hash normalizes the referenced brush-table ordinal to its
stable identity, so unrelated resource insertion preserves the compiled page
while a material generation change invalidates it. The MIL channel copies the
same material and stop arrays through
`progpu_native_mil_channel_set_viewport3d_scene_materials`; neither caller
memory nor process-local objects survive the call.

The corresponding MIL channel API now binds a copied, pointer-free flattened
scene to a canonical `TYPE_VIEWPORT3DVISUAL` handle. It accepts the public
semantic camera/mesh/vertex ABI plus uint32 indices, validates every finite
field and range transactionally, retains generation identity, and emits the
same shared 3D semantic resource and command used outside WPF. A viewport
without this typed binding fails closed. Exact inherited rectangle and
scrollable-area clips remain typed MIL state and execute as the semantic
viewport composite scissor. Arbitrary geometry clips, opacity masks, and
guideline resources still fail closed until the shared 3D compositor can apply
them exactly; they are never silently dropped. No reflection, WPF object
pointer, CPU projection, or bridge-local mesh renderer is introduced.

`--semantic-viewport3d` is the live cross-backend gate for this path. It sends
a canonical type-40 retained viewport through the MIL sideband, compiles the
resulting semantic mesh, renders it into a strict sub-viewport, reads the GPU
target back, and rejects any colored pixel outside that rectangle. The first
Metal run exposed three previously dormant 3D-pipeline defects: temporary
WGSL-array dynamic indexing rejected by wgpu-native, zero-valued default
stencil compare modes, and treating the ABI position's reserved fourth float
as homogeneous `w`. The shared shader now derives line corners without dynamic
array indexing and constructs mesh positions as `vec4(position.xyz, 1)`; the
pipeline initializes both unused stencil faces explicitly. Apple M3 Pro Metal
qualifies one draw with no CPU projection. The current gate applies a 0.75
axis scale, `[8,6]` retained offset, 0.5 opacity, exact local rectangle clip,
and world-space scroll clip together. The resulting transformed viewport is
`[32,21]-[80,57]`, their effective clip is
`[48,28.5]-[66.5,47.25]`, and all 291 colored pixels occupy
`[48,28]-[66,47]` with the expected half-red center sample. Its material
generation additionally renders one red-to-blue linear gradient through the
native C++ MIL sideband and currently observes 75 red-dominant plus 96
blue-dominant pixels on Apple M3 Pro Metal, with no WebGPU validation error.

The mesh flag bit `SPECULAR_MATERIAL` makes that same canonical brush table a
typed specular-color multiplier. Diffuse and emissive passes retain their
existing material-color multiplication, while a flagged specular pass keeps
the mesh diffuse color black and multiplies `specular_color.rgb` in WGSL. This
preserves WPF's ordered material-pass behavior without widening the public
256-byte mesh record or introducing a CPU texture. Unknown bits and mutually
exclusive front/back flags still fail validation. The live Metal gate now
renders a second retained generation using only the specular term and observes
64 red-dominant plus 85 blue-dominant pixels inside the same 291-pixel clip;
it also requires this readback to differ from the unlit gradient generation.

Exact pushed checkpoint `fd455edf` is qualified from isolated archive SHA-256
`46B06076344DE8518622AD66F5C9BE129C5E6231FAB874066FE83BFFDB6E5201`
in the Windows 11 ARM64 Parallels guest. ARM64 MSVC 19.44 rebuilt both native
providers under `/W4 /WX`, both export allowlists matched, all 11 CTests
passed, and the managed harness built with zero warnings or errors. The live
adapter was `Parallels Display Adapter (WDDM)`, backend `D3D12`, device type
`DiscreteGpu`; its readback exactly reproduced Metal's 291 clipped pixels,
75/96 ordinary-gradient evidence, and 64/85 specular-gradient evidence with no
WebGPU validation/device error. SHA-256 is
`635A68C0D9EDDD54230CC6CB8B37B6EDC8E994D6739AEA75FE006DDA44364EF5`
for `progpu_native.dll` and
`3DC03BD509449F560765FE9B9F73AEAD3DB4440D1CAF409D851834CBB847D722`
for `progpu_native_dawn.dll`.

Exact pushed implementation `318c0b0a` is also qualified from a SHA-256-
verified isolated source archive in the Windows 11 ARM64 Parallels guest.
ARM64 MSVC 19.44 compiled both native providers under `/W4 /WX`; both export
allowlists matched and all 11 native/Dawn CTests passed. The managed
differential harness built with zero warnings and selected
`Parallels Display Adapter (WDDM)`, backend `D3D12`, device type
`DiscreteGpu`. Its live `--semantic-viewport3d` readback preserved the same
291-pixel clipped extent and observed exactly 75 red-dominant plus 96
blue-dominant pixels without a WebGPU validation or device error. SHA-256 is
`9F15D58AE625541CCB327830B94CC8DCB678DCCFE528E95C477368E4E06C2589`
for `progpu_native.dll` and
`09504155E390F0AF8BDA46F7269FE36F0201097714B92060F4B74E470CE973AE`
for `progpu_native_dawn.dll`.

Mesh flags now distinguish the source-compatible two-sided mode from exact
front-only and back-only material entries without changing the scene ABI.
Front entries use the shared triangle-list pipeline with back-face culling;
back entries use the same shader and storage page with front-face culling.
Triangle strips remain normalized to triangle lists before selection. The live
viewport gate renders a front entry and an opposite-winding back entry in
separate retained generations and requires byte-identical readbacks, so both
face pipelines, their WPF material semantics, and the inherited rectangle clip
execute on every gated backend. The same readback also makes retained
axis-preserving transforms, offsets, opacity, and scroll clipping executable
gate requirements instead of packet-only assertions.

The shared native 3D shader now consumes all lighting scalars already carried
by the public mesh ABI. `light_direction.w` scales diffuse and specular terms,
`ambient_color.w` scales the material ambient term, and
`specular_color.w` supplies the bounded specular exponent instead of the old
hardcoded 24. The retained viewport gate runs realistic shading with 0.4
directional intensity, 0.2 ambient intensity, half visual opacity, and a green
specular term. Its center pixel is `77/51/0/255`, matching the composed light
values, and changing shininess from 1 to 256 must change the final GPU image.
This validates the same WGSL on Metal now and on the gated D3D12/Vulkan lanes.
The gate also renders a fourth generation through an orthographic projection,
requires it to differ from the perspective readback, and observes 278 colored
pixels at `[48,28]-[66,47]` inside the same transformed viewport/clip. Both
camera families therefore execute through retained MIL and the shared GPU
projection path.

Native scene validation now rejects negative directional or ambient intensity
and nonpositive shininess before retaining or allocating a mesh page. The WGSL
minimum clamps remain defense in depth for already-validated streams, not an
API policy that silently converts invalid lighting state. Native C++ coverage
checks all three rejection boundaries alongside the existing face-flag guard.

- Implement the remaining 2D/3D resource execution and remaining pen/image/media
  paths, dynamic guidelines, caches, effects, and render-data commands.
- Lower every supported update to stable semantic resource identities and
  generation numbers; unchanged resources must not be rebuilt.
- Add fixture capture/replay comparison against WPF's `CMilDataStreamReader`
  behavior and the existing managed LibreWPF renderer.

### Stage 2 — targets, scheduling, and parity services

- Add connection/partition/channel objects, handle duplication, out-of-band
  target updates, sync-flush replies, tier/capability replies, present/vblank
  scheduling, dirty regions, and device-loss recovery.
- Implement HWND and generic targets without leaking host-specific ownership
  into the retained scene layer.
- Add native hit-test result mapping and compositor diagnostics.

### Stage 3 — effects, 3D, media, and interop

- Complete shader effects, opacity masks, bitmap caches, 3D cameras/models,
  D3DImage/external texture binding, media frames, and color/text parity.
- Keep unsupported shader bytecode and external-handle forms fail-closed until
  a typed conversion or native backend implementation exists.

### Stage 4 — DirectX and DXGI facade parity

- Move the existing managed `ProGPU.DirectX` device/resource/view/pipeline
  compatibility model onto a native handle ABI shared with the MIL endpoint.
- Implement the measured D3D11/D3D12/DXGI/D3DCompiler export surface required
  by real package consumers. Do not attempt an unbounded system-DLL clone.
- On Windows, use Dawn's D3D12 backend for the compositor and explicit shared
  texture/fence paths for D3D11/D3D12 interop. Validate adapter LUID, format,
  alpha mode, row pitch, synchronization, resize, occlusion, and device loss.
- Preserve WebGPU behavior and semantic output across wgpu-native and Dawn;
  backend-specific differences require golden-image and metrics evidence.

### Stage 5 — LibreWPF selection and release gate

- Add an explicit runtime selector: managed portable, native MIL WebGPU, or
  native MIL Dawn/DirectX. The managed portable lane stays buildable and
  testable throughout migration.
- Package native runtimes for Windows x64/arm64 and the existing supported
  non-Windows RIDs. Verify exact ABI and protocol versions at startup.
- Run package-mode Toolkit/AvalonDock, Xceed when licensed, SciChart, input,
  clipping, hit testing, multi-window, DPI, and shutdown tests.

## Windows validation matrix

The primary integration guest is the discovered Parallels Windows 11 ARM64 VM.
The lane records guest OS build, .NET SDK, CMake/MSVC/Clang versions, adapter and
driver identity, Dawn backend, feature/limit set, and whether rendering uses
hardware, WARP, or another fallback. Required comparisons are:

1. Microsoft WPF MIL output on Windows versus LibreWPF managed portable output.
2. LibreWPF managed portable versus native MIL semantic streams.
3. Native wgpu/Dawn output versus Dawn D3D12 output.
4. DirectX compatibility API behavior versus native DirectX where the API is
   intentionally supported.

Tests use deterministic scenes, pixel tolerances, semantic stream hashes,
resource-generation/damage metrics, GPU validation output, and process lifetime
checks. A screenshot alone is not a parity result.

### Windows ARM64 qualification evidence — 2026-08-24

The current branch was qualified in the Parallels Windows 11 ARM64 guest with
OS build `26200.9168`, .NET SDK `10.0.400` / runtime `10.0.11`, Visual Studio
Build Tools `17.14.39`, ARM64 MSVC `19.44`, CMake `3.31.6`, and Ninja `1.12.1`.
The live adapter was `Parallels Display Adapter (WDDM)`, driver
`20.18.2641.57516`, using the D3D12 backend. The complete bounded gate was:

```powershell
.\eng\build-progpu-native-windows.ps1 `
  -Rid win-arm64 `
  -Compiler MSVC `
  -Generator Ninja `
  -BenchmarkProfile Smoke
```

Results:

- Both provider-resolved Dawn and wgpu-native renderer modules built with the
  ARM64 MSVC toolchain; all 11 native tests passed, including the MIL channel
  and Dawn ABI contracts.
- The live C++ sample rendered nine retained commands in five draw calls,
  uploaded 11,616 vertex bytes, completed a D3D12 readback, and emitted its PPM
  plus backend evidence file.
- The managed native-host sample lowered 16 source commands to 13 native
  commands and six draws, uploaded 27,464 vertex bytes plus 55,552 coverage
  bytes, and passed pre-render, post-render general-buffer, and readback-heap
  allocation probes before completing its readback.
- Two- and sixteen-positioned-glyph retained scenes passed exact native versus
  managed pixel parity with zero differing pixels and zero steady-frame
  managed allocations. The Parallels D3D12 profile uses a typed CPU R8 coverage
  atlas fallback in both implementations; normal adapters keep the shared GPU
  compute rasterizer. The 16-glyph native submission measured `0.5108 ms`
  versus `1.2558 ms` for the managed submission in the one-frame diagnostic.
- The 384-command mixed-picture native-only stress completed eight synchronized
  frames at `0.2721 ms/frame`. A separate live differential scene stayed within
  the independent-AA budget (maximum channel delta 2/255, zero pixels over
  3/255, mean absolute delta `0.0000622`). Solid/group opacity, external and
  masked images, semantic mixed scenes, semantic mask/effect chains, retained
  vector clips, blur/drop-shadow, Overlay and ColorDodge blending, and managed
  versus C++ text shaping also passed their declared parity contracts.
- The staged package contains both native renderer variants for `win-arm64`.

The affine-transform checkpoint at ProGPU commit `360a6f7e` was requalified
with the same complete gate. ARM64 MSVC compiled the new matrix resource,
visual-transform, and nested transform-scope paths; all 11 CTests passed,
including transformed semantic-state/bounds fixtures, null no-op scopes, and
transactional failure cases. Live retained rendering/readback, package staging,
the managed-host allocation probes, effect/vector/text cases, and isolated
stress/differential processes all completed. In that follow-up run the native
384-command stress measured `0.1244 ms/frame`; the bounded differential again
had maximum channel delta 2/255 and zero pixels over 3/255. Timing is diagnostic
for this VM, while the pass/fail contracts and recorded adapter identity are
the qualification evidence.

The solid-pen/line checkpoint at ProGPU commit `dadb26a5` was then qualified
with that same complete command from a clean Windows checkout. Both native
modules linked for ARM64, all 11 CTests passed (including cap mapping,
cap-aware transformed bounds, line metrics, Dawn ABI, and transactional
animated/dashed-pen rejection), and the live C++ and managed D3D12 samples
again completed their readback and post-build allocation probes. The bounded
mixed-picture differential remained at maximum delta 2/255 with zero pixels
over 3/255; external/masked images, semantic scenes, mask/effect chains,
vector clips, text shaping, Overlay, and ColorDodge remained inside their
declared contracts. The run reached its normal completion marker and staged
both `progpu_native.dll` and `progpu_native_dawn.dll` in the `win-arm64`
package runtime. One eight-frame synchronized managed-picture native profile
measured `0.6335 ms/frame`; VM timings remain diagnostic rather than release
thresholds.

The retained-dash checkpoint at ProGPU commit `fca6c7a2` was qualified from a
clean Windows checkout with a focused integration gate over the renderer
already covered by the immediately preceding full matrix. ARM64 MSVC rebuilt
both native modules and the MIL executable; the Windows MIL and Dawn contract
tests passed 2/2. The project-reference package consumer then built with zero
warnings and ran live on D3D12, compiling its retained dashed line through both
wgpu-native and Dawn MIL channels before completing renderer readback (`draws=1`,
16,384 pixels). This gate specifically covers variable packet decoding,
thickness-relative intervals/offset, dash-cap semantic-stroke lowering,
transactional validation, both exported ABIs, and managed package production.

The rectangle-pen checkpoint at ProGPU commit `89f0a838` passed the same
focused Windows ARM64 integration lane. MSVC rebuilt both native modules and
the MIL test executable, the MIL/Dawn contracts passed 2/2, and the updated
project-reference package consumer built with zero warnings. Its live D3D12
run compiled a dashed line plus a dashed fill-and-stroke rectangle through both
native MIL exports before completing readback (`draws=1`, 16,384 pixels). The
Windows checkout was clean at the exact qualified commit.

The solid-ellipse checkpoint at ProGPU commit `f24d715f` passed that focused
lane from a clean Windows checkout. The MIL and Dawn contracts passed 2/2, and
the package consumer compiled a fill-plus-solid-stroke ellipse through both
native MIL exports before completing live D3D12 readback (`draws=1`, 16,384
pixels). The same gate isolated and corrected Windows instance creation:
managed `WgpuContext` now supplies wgpu-native's typed D3D12 instance extension
instead of a backend-unspecified descriptor. WebGPU-init-only, render-only, and
combined MIL/render processes all completed with exit code zero under the
Parallels SYSTEM integration session. The C++ retained renderer independently
selected the Parallels D3D12 adapter, rendered nine commands in five draws,
uploaded 11,616 vertex bytes, and completed readback.

The uniform rounded-rectangle pen checkpoint at exact ProGPU commit
`84cdcead` passed the same focused Windows ARM64 lane from a clean checkout.
ARM64 MSVC rebuilt both native modules and the MIL executable, and the MIL/Dawn
contracts passed 2/2. The project-reference package consumer built with zero
warnings, compiled a pen-only rounded rectangle through both native MIL
exports, and then completed managed D3D12 rendering/readback (`draws=1`,
16,384 pixels). The independent C++ retained renderer also selected the
Parallels D3D12 adapter and completed its nine-command, five-draw,
11,616-vertex-byte readback gate.

The retained line-geometry checkpoint at exact ProGPU commit `5c4757c0`
passed the focused Windows ARM64 lane from a clean checkout. ARM64 MSVC rebuilt
both native modules and the MIL executable, and the MIL/Dawn contracts passed
2/2. The zero-warning project-reference package consumer compiled a typed,
transformed `LineGeometry`/`DrawGeometry` pen through both native MIL exports
before completing live D3D12 rendering/readback (`draws=1`, 16,384 pixels).
The independent C++ retained renderer again completed its nine-command,
five-draw, 11,616-vertex-byte D3D12 readback gate.

The retained rectangle/ellipse-geometry checkpoint was qualified at exact
ProGPU commit `a1c0fd81` (feature implementation `bc6b5029`) from a clean
Windows checkout. ARM64 MSVC rebuilt `progpu_native.dll`,
`progpu_native_dawn.dll`, and the MIL executable; the MIL/Dawn contracts passed
2/2. The zero-warning project-reference package consumer compiled transformed
retained line, uniform rounded-rectangle, and ellipse geometry resources
through both native exports, then completed live Parallels D3D12
rendering/readback (`draws=1`, 16,384 pixels). The independent C++ retained
renderer again completed nine commands, five draws, 11,616 uploaded vertex
bytes, and readback. The gate required the consumer's app-local native DLL
hashes to match the freshly rebuilt modules after detecting and replacing an
older incremental-build artifact.

The retained general-path and endpoint-arc checkpoint was qualified at exact
native implementation commit `51550b6e` from a clean Windows checkout with
the complete ARM64 MSVC gate. Strict `/W4 /WX` first exposed an implicit
fill-rule enum conversion in the semantic path resource; the implementation
now keeps the C ABI field type explicit. Both native modules linked, all 11
CTest contracts passed, and the live C++ and managed samples completed D3D12
rendering/readback on the Parallels adapter. The managed sample lowered 16
source commands to 13 native commands and six draws, uploading 27,464 vertex
bytes plus 55,552 coverage bytes. The eight-frame native managed-picture
stress measured `0.1235 ms/frame`; the bounded differential had maximum delta
2/255, zero pixels above 3/255, and mean absolute delta `0.0000622`. Retained
path-atlas qualification rasterized 49 paths with 4,112 path-upload bytes and
727,552 coverage-staging bytes, remaining inside its independent-edge budget
(maximum delta 46/255, 1,048 pixels over tolerance, mean `0.0171`). Exact
Overlay and ColorDodge cases, image/mask/effect chains, vector/text contracts,
and final `win-arm64` package staging also passed.

The per-point MIL endpoint-arc and GPU-first compute-fallback checkpoint at
exact ProGPU commit `a1fd8b2b` then passed the complete clean Windows ARM64
MSVC/D3D12 lane. All 11 native contracts, the independent C++ sample, managed
native sample, Microsoft D3D12HelloTriangle oracle, bounded managed-picture
differential, semantic resource/effect families, text shaping, and package
staging completed on `Parallels Display Adapter (WDDM)`. The guided MIL arc
matched its native reference exactly (`referenceChanged=0`). The automatic
glyph path selected the exact raster-shader fallback, stable managed-picture
replay reported zero allocations and zero coverage staging, and an explicitly
forced incompatible compute path failed before glyph compute resource
creation. ARM64 NEON and managed `Vector<T>` CPU fallbacks now solve curve
crossings once per subpixel scanline and reuse them across the full glyph row;
the direct Windows cold SIMD qualification fell from roughly 123 seconds to 67
seconds without changing the exact `5B6EF4F70536C862` pixel hash.

The next intrinsic checkpoint batches two adjacent pixels per crossing loop:
four NEON/SSE2 winding vectors evaluate 16 horizontal samples from one crossing
broadcast, while each pixel origin is still evaluated with the original
floating-point expression to preserve edge decisions. Final 64-sample coverage
uses the exact positive-integer form `(samples * 255 + 32) / 64`, eliminating
per-pixel floating-point rounding in both SIMD and scalar fallbacks. The Apple
M3 Pro forced SIMD and independent scalar oracle remain byte-identical to the
managed renderer at `5B6EF4F70536C862`, and the full local native/Dawn suite
passes 11/11.

Exact pushed implementation `436c0521` also passes ARM64 MSVC compilation of
both native DLLs and all 11 native/Dawn tests in the Windows Parallels VM.
Forced NEON on the D3D12 adapter reports zero pixel difference and the same
`5B6EF4F70536C862` hash; its synchronized retained frame measured `1.0494 ms`
versus `1.8201 ms` managed in the one-frame qualification sample. A strict
Clang x86_64 cross-architecture syntax pass covers the paired SSE2 branch on
the macOS host pending the ordinary x64 CI/runtime lane.

SIMD follow-up `516eb3d7` adds conservative control-point Y-hull rejection
before quadratic/cubic root solving and adds the explicit
`--rerasterize-glyphs` benchmark mode so timing cannot accidentally measure a
retained cache hit. Four alternating Apple M3 Pro Release runs per variant,
each with three warmups and 30 forced-SIMD rerasterized frames, reduced the
median of per-run submission p50 from 1.8217 ms to 1.3916 ms (-23.6%) and total
synchronized-frame p50 from 3.6040 ms to 3.0045 ms (-16.6%). Submission/frame
p95 improved 2.9429 -> 2.3009 ms and 5.1773 -> 4.4856 ms. Every baseline and
candidate frame remained byte-exact at `5B6EF4F70536C862`; the full 11-test
native/Dawn suite, forced scalar oracle, and strict x86_64 SSE2 syntax compile
also pass.

Exact head `644a8d89` then rebuilt both libraries with ARM64 MSVC and passed
all 11 Windows native/Dawn tests. The zero-warning benchmark build reproduced
the complete 42-glyph forced-NEON D3D12 hash `5B6EF4F70536C862` with zero pixel
difference and 247,808 staging bytes. A bounded rerasterized one-glyph A/B was
also exact at `6C59592F05595EFE`, but large process-startup variance makes it a
correctness gate rather than a Windows timing claim. SHA-256 was
`A9BB8F281F27B332AAACAA0EC35B9E3B26E73D21E839470654D95CB89DDA6A39`
for `progpu_native.dll` and
`97CDBDD4F02442F2D9ACF966C1FF1660C64D7014E9A98FC767B3D9819CB561BF`
for `progpu_native_dawn.dll`.

Intrinsic follow-up `e6ab073e` precompiles the quadratic/cubic control-point
Y hull and Y-polynomial coefficients once per CPU-rerasterized frame. The
subpixel scanline loop keeps the original conservative hull check, root math,
crossing order, winding decisions, and independent scalar oracle, but no
longer rebuilds invariant curve data eight times per pixel row. Four
alternating pre-change/candidate Apple M3 Pro Release runs, each with three
warmups and 30 forced-SIMD rerasterized frames, reduced median per-run
submission p50 from 1.1648 ms to 1.0533 ms (-9.6%) and synchronized-frame p50
from 2.7528 ms to 2.5981 ms (-5.6%). Submission/frame p95 medians improved
2.0873/4.3461 ms to 1.4839/4.0934 ms. All 240 measured frames, all five forced
execution-policy checks, the full 11-test native/Dawn suite, and strict
x86_64 SSE2 syntax compilation retained exact `5B6EF4F70536C862` output.

Exact implementation head `405d139b` then passed the complete unmodified
Windows ARM64 MSVC/D3D12 smoke gate in the Parallels VM. Both native libraries
rebuilt; all 11 native/Dawn CTests, native and managed renderer samples,
Microsoft D3D12HelloTriangle oracle, forced raster/NEON/scalar exact-pixel
routes, typed pre-resource rejection of incompatible forced compute, MIL
guideline/arc deformation, retained mask/effect/blend families, text parity,
bounded differential profiles, and package staging passed on
`Parallels Display Adapter (WDDM)`. SHA-256 is
`C690AED72C3C895778197808C8347656433D6A97DD178F5249A8B4D0C1B56756` for
`progpu_native.dll` and
`552E8CC9441B9A33E89B346758113B52DC13F7A3B1D11F80BF86A3AE90039637` for
`progpu_native_dawn.dll`.

Intrinsic checkpoint `bf20bd66` then changes the raster-row traversal without
changing coverage semantics. It records the eight Y-subscanline crossing spans
in one retained arena, visits X afterward, builds the four NEON/SSE2 sample
vectors once per pixel pair rather than eight times, resets only winding
accumulators between spans, and writes the completed 64-sample coverage pair
directly. Original crossing order, strict sample comparison, floating-point X
expressions, integer quantization, and the independent scalar oracle remain
unchanged.

Four alternating Apple M3 Pro Release A/B runs per variant, each with three
warmups and 30 rerasterized frames, reduced median-of-run submission/frame p50
from 1.0469/2.6249 ms to 1.0199/2.5889 ms at 1x DPI (-2.6%/-1.4%). At 2x DPI,
submission/frame p50 fell from 1.9498/3.5588 ms to 1.7884/3.3814 ms
(-8.3%/-5.0%). All 480 measured baseline/candidate frames retained exact
managed parity at `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x).
The native/Dawn suite passes 11/11, all five forced/default execution routes
are exact, and strict x86_64 SSE2 syntax compilation passes.

The same exact commit rebuilt both ARM64 libraries with MSVC `/W4 /WX` and
passed all 11 CTests in the Windows Parallels VM. The full 42-glyph forced-NEON
D3D12 oracle remained byte-exact at `5B6EF4F70536C862` with 247,808 coverage
bytes. Qualified DLL SHA-256 values are
`EE150A6E7EACF4B7E789C8EE9B0A0A91778D121AE107FCF7700BEC4C7FD588C5` for
`progpu_native.dll` and
`3FF479B331F6548938115C272FE53B03F4AC89872B565941AA0DD34DF75A9B35` for
`progpu_native_dawn.dll`. The Windows result is correctness evidence; process
startup dominates and is not used as a performance comparison.

Follow-up four-pixel NEON batching and packed-byte deferred-reduction
experiments remained pixel-exact but failed the longer grouped no-regression
gate at both 1x and 2x DPI, so they were rejected. The full measurements and
rationale are recorded in `GPU_COMPUTE_FALLBACK_POLICY.md`; the qualified
two-pixel SIMD implementation remains authoritative.

The subsequent signed-mask optimization preserves that two-pixel structure.
NEON/SSE2 comparison results are already all-one integer lanes, so a `+1`
crossing subtracts the mask and a `-1` crossing adds it. This removes one
direction broadcast and four bitwise masks per crossing while retaining the
exact sample expressions, crossing order, 32-bit winding state, reductions,
and scalar oracle. Four alternating 120-frame Apple M3 Pro runs improved the
median-of-run native-submission p50 by 19.4% at 1x and 10.9% at 2x; synchronized
frame p50 improved 18.2% and 3.2%, respectively. All 960 measured frames kept
the qualified hashes `5B6EF4F70536C862` and `706B261418EC5C3B`, the complete
local native suite passed, and the paired SSE2 branch passed strict x86_64
Clang compilation. The default remains GPU-first; this only improves the typed
intrinsic fallback selected by policy or configuration.

An exact empty-subscanline skip was evaluated after that accepted optimization
and deliberately rejected. Across four alternating 120-frame runs per variant,
it improved 2x submission/frame p50 by 7.9%/5.8% but regressed 1x submission
p50 by 7.0% (frame p50 by 0.6%). All 960 frames retained identical baseline and
candidate hashes. Because the branch fails the cross-profile no-regression
gate, the qualified intrinsic path continues to reset and reduce every
subscanline unconditionally.

The corresponding Linux ARM64 checkout at exact commit `28447de4` passed a
strict GCC 13.3 build of the complete 260-object graph, all 10 wgpu-native CTest
contracts, the export allowlist, and live Vulkan allocation/render/readback on
llvmpipe LLVM 20.1.2. Forced compute, raster, SIMD, and scalar glyph paths all
matched their managed renderer exactly at `1F9AE0BB0AC59113`; raster retained
its zero-staging contract. The run exposed two gate portability issues now
closed in source: WebGPU texture-usage flags are normalized by the shared
header compatibility layer, and custom native build directories flow into the
managed benchmark copy target instead of allowing a stale default library.
This is software-Vulkan correctness evidence, not physical Vulkan performance
qualification.

Package-consumer commit `a11ad9fd` then exercised that exact staged native
implementation rather than a source-only fixture. Its app-local SHA-256 hashes
matched the newly staged `progpu_native.dll` and `progpu_native_dawn.dll`; the
consumer built with zero warnings, compiled a transformed retained path with a
quadratic segment and rotated WPF endpoint arc through both MIL exports,
installed the wgpu-native semantic stream, rendered it live on D3D12, and read
back 16,384 pixels. The installed scene reported 15 semantic resources and two
draw calls before the independent renderer smoke completed.

The geometry-group/combined-geometry checkpoint at exact ProGPU commit
`41af1e66` passed the complete Windows ARM64 MSVC gate from a clean checkout.
Both native modules rebuilt under strict warnings and all 11 fresh CTests
passed, including MIL geometry-DAG, boolean-program, null-operand, cycle,
deletion, and transactional rollback cases plus the Dawn ABI contract. The
independent C++ and managed samples completed live D3D12 rendering/readback;
the managed sample again produced 13 native commands, six draws, 27,464 vertex
bytes, and 55,552 coverage bytes with successful allocation probes. The
bounded differential remained at maximum delta 2/255 with zero pixels above
3/255 and mean `0.0000622`; vector, image, mask/effect, text, Overlay, and
ColorDodge contracts also passed, and the package runtime was restaged.

The updated zero-warning package consumer then verified exact SHA-256 matches
for both freshly staged DLLs, compiled two retained paths plus an EvenOdd
`GeometryGroup` and an Exclude `CombinedGeometry` through both MIL exports,
installed the wgpu-native semantic stream, and completed live D3D12 readback.
The installed graph reported 17 semantic resources and two draw calls before
the independent immediate renderer smoke completed its 16,384-pixel readback.

The retained line-path stroke checkpoint at exact ProGPU commit `70c88279`
passed the complete Windows ARM64 MSVC gate from a clean checkout. Both native
modules rebuilt under `/W4 /WX`; all 11 CTests passed, including closed/open
stroke topology, geometry-gap dash caps, affine/dash/pen propagation,
unsupported smooth/curved strokes, and closed-gap dash-seam fail-closed cases.
The independent C++ and managed hosts completed live Parallels D3D12
rendering/readback and allocation probes. The bounded differential remained at
maximum delta 2/255 with zero pixels above 3/255 and mean `0.0000622`; path
atlas, image/mask/effect, semantic-layer, text, Overlay, and ColorDodge
contracts all passed. The synchronized eight-frame native diagnostic measured
`0.0902 ms/frame` on this VM.

The updated zero-warning package consumer then copied the exact staged
`progpu_native.dll` and `progpu_native_dawn.dll`; each app-local SHA-256 matched
its staged source. It compiled the existing path/group/combined graph plus a
transformed dashed closed `PathGeometry` whose first edge is a WPF geometry gap
through both MIL exports, installed the wgpu-native stream, and completed live
D3D12 readback. The installed graph reported 18 semantic resources and three
draw calls before the independent immediate renderer completed its
16,384-pixel readback.

The fixed-child `GeometryGroup` checkpoint at exact ProGPU commit `18ccb55c`
passed the complete Windows ARM64 MSVC gate. Both wgpu-native and
provider-resolved Dawn modules rebuilt under `/W4 /WX`; all 11 CTests passed,
including exact transformed rectangle, ellipse, non-uniform rounded-rectangle,
and empty line-child group contours. The independent C++ and managed hosts
completed live D3D12 rendering/readback and allocation probes. The bounded
mixed differential remained at maximum delta 2/255, zero pixels above 3/255,
and mean `0.0000622`; external and masked images, semantic mask/effect layers,
path atlas, blur/drop-shadow, text, Overlay, and ColorDodge contracts also
passed before fresh `win-arm64` package staging.

The zero-warning project-reference package consumer copied both staged DLLs
app-locally and verified identical source/destination SHA-256 values:
`73fcc3871408d4642d6ace3817b30c36194e9938c36dd60f8e4d09325ec4495f`
for `progpu_native.dll` and
`709e59f97f484dc74dd5693f207dbbe96ba568d1f692b93c6df186e5d535c8c8`
for `progpu_native_dawn.dll`. Both MIL exports compiled the group containing
transformed rounded-rectangle and ellipse children. The installed wgpu-native
stream then completed live D3D12 readback with 18 semantic resources, three
draws, and 16,384 pixels before the independent immediate renderer smoke.

The shared fixed-operand `CombinedGeometry` checkpoint at exact ProGPU commit
`7d0fad61` passed the complete Windows ARM64 MSVC gate. The refactored shallow
fill lowerer compiled into both native modules under `/W4 /WX`; all 11 CTests
passed, including transformed fixed boolean leaves, non-uniform rounded
rectangles, preserved identity-local path operands, and the Dawn contract. Live
C++ and managed D3D12 rendering/readback, allocation probes, and the complete
bounded differential matrix passed. The mixed differential remained at maximum
delta 2/255, zero pixels above 3/255, and mean `0.0000622`; path-atlas,
image/mask/effect, text, Overlay, and ColorDodge results stayed within their
established contracts. The synchronized eight-frame native diagnostic measured
`0.1618 ms/frame` on this VM before fresh package staging.

The zero-warning project-reference consumer then verified exact app-local
matches for the staged modules: SHA-256
`288438736839fc4e673fe4dbd7a714eda8158df181c694d0efd3d92dadf1e984`
for `progpu_native.dll` and
`31b0fe54964b8163b4a1d132359e89de58367b31550020d924c681f6cc4732b6`
for `progpu_native_dawn.dll`. Both MIL exports compiled transformed fixed
rounded-rectangle and ellipse boolean operands, and the installed wgpu-native
stream completed live D3D12 readback with 18 semantic resources, three draws,
and 16,384 pixels.

The affine path-leaf checkpoint at exact ProGPU commit `9634af73` passed the
complete Windows ARM64 MSVC gate. Both modules rebuilt under `/W4 /WX`, and all
11 CTests passed with exact transformed line, quadratic, cubic, implicit fill
closure, conservative bounds, legacy identity-path operands, and transformed-
arc fail-closed coverage. Live C++/managed D3D12 rendering and readback,
allocation probes, text, path atlas, image/mask/effect, Overlay, ColorDodge, and
the bounded differential matrix all passed. The mixed differential remained at
maximum delta 2/255, zero pixels above 3/255, and mean `0.0000622`; the
synchronized eight-frame native diagnostic measured `0.0925 ms/frame` before
fresh package staging.

The zero-warning project-reference consumer verified identical staged/app-local
SHA-256 values:
`6493681ddc832c58b5d549a22cae070839268a7f66d41aae70c0c9450ba59f3f`
for `progpu_native.dll` and
`28a82155eedaa4c1b3c73b982f3c5e8f4e475687eef300946be8d6f1158d4379`
for `progpu_native_dawn.dll`. Both MIL exports compiled a transformed
line/quadratic/cubic path leaf in the retained group, and the installed
wgpu-native stream completed live D3D12 readback with 18 semantic resources,
three draws, and 16,384 pixels.

The recursive `GeometryGroup` checkpoint at exact native implementation commit
`e0281b69` passed the complete Windows ARM64 MSVC gate. Both modules rebuilt
under `/W4 /WX`, all 11 CTests passed, and the MIL contract covered nested group
transform composition, outer-fill-rule ownership, groups as combined-geometry
boolean leaves, cycles, rollback, and transformed-arc fail-closed behavior. Live
C++/managed D3D12 rendering and readback, allocation probes, text, path atlas,
image/mask/effect, Overlay, ColorDodge, and the bounded differential matrix all
passed. The mixed differential remained at maximum delta 2/255, zero pixels
above 3/255, and mean `0.0000622`; the synchronized eight-frame native
diagnostic measured `0.1562 ms/frame` before fresh package staging.

Package-consumer checkpoint `14603fa2` then verified identical staged/app-local
SHA-256 values:
`e6e71dbca0b0e846de332c7bbade0362a9d19f2e4d16eef93aa73dce8640352e`
for `progpu_native.dll` and
`5a0bae5f610cfecf5a945b850a91251c725f97d7592903c78e9e2d24a5fcd79d`
for `progpu_native_dawn.dll`. Its 40-command, 17-resource seed compiled a
recursively transformed affine path group through both MIL exports. The
installed wgpu-native stream completed live Parallels D3D12 readback with 18
semantic resources, three draws, and 16,384 pixels; the separate immediate
renderer smoke also passed.

The recursive `CombinedGeometry` checkpoint at exact ProGPU commit `8bf9a0c5`
(native implementation `6326cdf2`) passed the complete Windows ARM64 MSVC
gate. Both modules rebuilt under `/W4 /WX`, all 11 CTests passed, and the MIL
fixture verified a five-node postfix boolean tree with exact segment offsets,
leaf fill rules, nested group/combined transform composition, operation order,
conservative bounds, and rollback. Live C++/managed D3D12 rendering/readback,
allocation probes, text, path atlas, image/mask/effect, Overlay, ColorDodge,
and the bounded differential matrix passed. The mixed differential remained at
maximum delta 2/255, zero pixels above 3/255, and mean `0.0000622`; the noisy
eight-frame VM diagnostic measured `0.6330 ms/frame` before package staging.

The zero-warning project-reference consumer verified exact staged/app-local
SHA-256 matches:
`6ac27898f1f067854ac3e79bf415ecd41f9f79c3208a0d45618e0cf47047520d`
for `progpu_native.dll` and
`d98b7f7dd3a0315c5420ca5ca63f85354e9daec5bb8ede4468e097fd191dd906`
for `progpu_native_dawn.dll`. Its 42-command, 18-resource channel seed compiled
a nested group/combined boolean tree through both MIL exports. The installed
wgpu-native stream completed live Parallels D3D12 readback with 18 semantic
resources, three draws, and 16,384 pixels; the immediate renderer smoke also
passed.

The affine-arc recursive-geometry checkpoint at exact ProGPU commit `b9011c23`
passed the complete Windows ARM64 MSVC gate on 2026-08-25. Both native modules
rebuilt under `/W4 /WX`; all 11 CTests passed, including reflected/sheared arc
sample equivalence, sweep reversal, singular-transform rejection, exact
translation record preservation, recursive group/boolean arc leaves, outer
fill ownership, and transactional rollback. The independent C++ and managed
D3D12 samples, allocation probes, text, images, masks/effects, Overlay,
ColorDodge, and the bounded differential matrix all passed. The mixed
differential remained at maximum delta 2/255, zero pixels above 3/255, and mean
`0.0000622`. The 49-path atlas retained its existing maximum delta 46/255,
1,048 pixels over tolerance, and mean `0.017107928`; this is an unchanged
historical independent-edge-AA contract rather than a regression. VM timing was
noisy during this run and is intentionally not used as qualification evidence.

The zero-warning project-reference package consumer copied the exact staged
DLLs and exercised both the focused recursive-group arc scene and the broader
recursive group/boolean scene through the wgpu-native and Dawn MIL exports.
They completed live D3D12 readback with 18 semantic resources and three draws;
the focused scene staged 40,960 coverage bytes and the broader scene 41,472.
The staged module SHA-256 values were
`a94dab843f3f253e004e128e6ff9fc4160676691cc467cb0288a6071b0f37025`
for `progpu_native.dll` and
`a1f0c7067bd442b989708f4e7243927074a75e9419f8013d4cdef5d565b59807`
for `progpu_native_dawn.dll`.

At exact safety implementation commit `ef6091e9`, the close-translated-
equivalent EvenOdd diagnostic was converted from a device-removal path into a
deterministic fail-closed contract. The resource update remains transactional,
while semantic scene compilation returns `unsupported_command` before WebGPU
context/device creation. The strict Windows ARM64 build, both modules, all 11
CTests, the independent C++/managed D3D12 readbacks, allocation probes, and the
full differential matrix then passed again. Supported focused and broad package
scenes retained 18 resources, three draws, and 40,960/41,472 coverage bytes;
the guarded diagnostic exited at `NativeMilChannel.CompileScene` without GPU
submission. Fresh staged SHA-256 values were
`5b403c179cc0aa9ae9395b2e486aa36d8574fce510b560acf4c744daba6a0a9b`
for `progpu_native.dll` and
`5a39d04f8dcccae29c093e63dd5e3d5c2effa3b4b68418763afb8f90ee2af856`
for `progpu_native_dawn.dll`.

Translation preservation, bounded XOR leaf execution, row/bounds rejection,
sample-grid changes, and curve approximations had not eliminated the backend
failure; all approximation experiments remain removed and analytic 8x8
rasterization retained. The guard compares typed segment kinds, arc metadata,
translated control/end/center points, invariant radii, and positive overlapping
bounds. Non-overlapping equivalents and non-equivalent mixed leaves keep the
normal GPU path. Pure left-fold XOR forms through the existing 32-leaf MIL
ceiling are resolved by the phased GPU execution checkpoints below; mixed
programs retain this deterministic fail-closed contract.

The isolated curved-stroke implementation at `e0a9d15f`, with the MSVC
portability correction at `42e05f29`, first passed the focused Windows ARM64
lane. The joined/closed contour implementation at `38245edd` then added exact
mixed line/quadratic/cubic/arc segment composition and native tangent joins;
package checkpoint `3816050b` added a closed joined curve to the retained seed.
At that exact checkpoint both wgpu-native and Dawn rebuilt under MSVC `/W4
/WX`, all 11 CTests passed, and the complete bounded Windows D3D12 smoke profile
passed: independent native and managed readback, zero-allocation probes,
retained masks/effects, text shaping, path atlas, images, Overlay, ColorDodge,
and the declared differential scenes. The zero-warning project-reference
package consumer compiled the 46-command, 20-channel-resource seed through both
MIL exports and completed live D3D12 readback with 20 semantic resources, three
draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`1c0e48225057db64eaf97eab5ba239b8be5c365525bc4b68bba58d5f906a7926`
for `progpu_native.dll` and
`efaad18f8ee89a1c53f0dc612e99371f9a3d24cbcfdf66b3129af5875ef1bb74`
for `progpu_native_dawn.dll`.

The curved-cap implementation at `4f5dcc20` then composed Square, Round, and
Triangle open-contour caps as reusable native path-cap primitives with exact
curve endpoint tangents and affine state. ARM64 MSVC rebuilt both native
modules under `/W4 /WX`; the MIL and Dawn contract tests passed. Package
checkpoint `48bea705` applied Round/Triangle caps to the retained analytic arc,
compiled the unchanged 46-command/20-channel-resource seed through both MIL
exports, and completed live D3D12 readback with 20 semantic resources, three
draws, and 41,472 coverage bytes. Exact focused-build SHA-256 values were
`2afaa42721aa4ca9b6faa714755117d518abe23d47878613d6ea585b2dbdb164`
for `progpu_native.dll` and
`e64c515b74c131ae8e7b17eb86e4c301cbb4f07bc58d3bdb5b7644b441106309`
for `progpu_native_dawn.dll`. The complete differential matrix remains
qualified at `3816050b`; this cap checkpoint used the focused strict-build,
contract, and live-package lane.

The smooth-join implementation at `1431509c` then replaced the former broad
rejection with per-segment typed state. The WPF source trace established that
`SegSmoothJoin` is read after widening its segment and passed as `fRound` to the
next `DoCorner`, so the native compiler emits a Round path join regardless of
the pen's default join only at that endpoint. Solid mixed and line-only
contours use this exact geometry composition; dashed smooth joins still fail
closed. Strict ARM64 MSVC rebuilt both modules and the MIL/Dawn contracts
passed. Package checkpoint `6868d909` marked the line-to-quadratic corner in the
closed mixed-curve seed as smooth; both exports compiled it and live D3D12
readback retained 20 semantic resources, three draws, and 41,472 coverage
bytes. Exact focused-build SHA-256 values were
`b932861929989d6f847df95c0562a5629849507bace4e44dcdcc410b11e76237`
for `progpu_native.dll` and
`20806036f956a84b0b217329183f85ca8f130c7c4aba6fc4394705d6df6170e8`
for `progpu_native_dawn.dll`.

The exact rectangle-clip implementation at `37f496f2` added canonical
`MILCMD_PUSH_CLIP` production and native target-space nested intersections.
Local native suites and the typed managed builder test passed. ARM64 MSVC then
rebuilt both native modules under `/W4 /WX`; the MIL and Dawn contract tests
passed. Package checkpoint `d22a94c9` pushed a retained rectangle clip around
the existing mixed scene. Its 48 commands and 21 channel resources compiled
through both exports, and live D3D12 readback completed with 21 semantic
resources, three draws, and 41,472 coverage bytes. Exact focused-build SHA-256
values were
`014999b22d86f2192ea56697dde3d5bc47a88991831a39636a4db26c29fccb69`
for `progpu_native.dll` and
`ba780db29da7fbedd7834768180b9d9976775532f3cf0c50c4d52cc94b56d0b7`
for `progpu_native_dawn.dll`.

The exact geometry-clip implementation at `66d5f74b` then connected retained
fixed, path, group, and combined geometry to the semantic vector-mask resource.
Nested path/group/combined clips preserve analytic arcs, outer group fill
ownership, recursive boolean programs, and push-time affine state; mask arenas
truncate by saved prefix count after pop instead of copying segment vectors.
The same checkpoint replaced fixed ellipse and rounded-rectangle cubic circle
approximations with analytic arc segments. All ten local native tests passed.
Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`, and all 11
native/Dawn CTests passed on the Parallels VM. Package checkpoint `a2502e36`
added the retained arc path as a second clip around the mixed MIL scene. The
unchanged 48-command/21-channel-resource seed compiled through both exports;
live D3D12 readback completed with 23 semantic resources, three draws, and
41,472 coverage bytes. The complete Windows sample, allocation/readback,
differential, text, path, image, mask, effect, and blend smoke matrix passed.
Exact staged SHA-256 values were
`43e452fb73b6e103bc81ab56836c3e68d43a30b8bec7c8931df73ec8f5d05672`
for `progpu_native.dll` and
`9ca1765e660c8cc0d69c8c3eccba3d6971b9c4a05b04a5fc33975dee26e9c938`
for `progpu_native_dawn.dll`.

The dashed closed-gap implementation at `c12e6d60` removed the obsolete seam
rejection for line-only closed figures. The decoder already rotates each open
stroked run to the first edge after its geometry gap, so a run that crosses the
figure start retains one ordered polyline, one dash phase, and DashCap at both
gap boundaries without flattening or splitting. Native tests now assert the
wrapped point order, dash offset, interval count, and cap state. All ten local
native tests passed; strict Windows ARM64 rebuilt both modules and the MIL/Dawn
contract tests passed. Package checkpoint `0048f430` moved the existing line
geometry gap to force a start-crossing dashed run. Both MIL exports compiled
the 48-command/21-channel-resource seed, and live D3D12 readback retained 23
semantic resources, three draws, and 41,472 coverage bytes. Exact focused
SHA-256 values were
`39a28937c25d977310597efb3c6e7f0ed9f077cd8617b2f95582d3cca58e0161`
for `progpu_native.dll` and
`daefb160737962ec81fb78238b44508a7c6a7235c8daf1bc129b0f5df2dda14a`
for `progpu_native_dawn.dll`.

The singular-affine implementation at `f244dc2d` then closed the remaining
zero-determinant fill, stroke, and clip ambiguity. WPF's
`CShapeBase::GetArea` multiplies rectangle area by the absolute 2D determinant
and treats a degenerate general transform as no scannable workspace, so the
native MIL compiler now lowers singularly transformed fixed, path, group, and
combined geometry to exact empty coverage instead of trying to invert or
factor an arc basis. Direct line strokes follow the same rule, and a singular
geometry clip becomes an exact empty clip rather than bounds coverage. All ten
local native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `7b91b21f` added a typed singular `MatrixTransform` scope around
direct and retained draw commands. Both MIL exports compiled its 50-command,
22-channel-resource seed; live D3D12 readback retained 24 semantic resources,
three visible draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`1dec50b6aef18b22f894739a9bff477a31bd0751cae1baabd9d3efc562212b65`
for `progpu_native.dll` and
`83ff9ae3133fbe9ecd789202f10ea5dfc483528f207c8ef3d34af05e45c038d9`
for `progpu_native_dawn.dll`.

The degenerate point-cap implementation at `957adfdd` then matched WPF's
unstarted-widener behavior without manufacturing a line direction from object
shape or flattening. Immediate zero-length lines and wholly degenerate open
path contours use a horizontal shape-space tangent and compose their typed
non-Flat cap halves; wholly degenerate closed contours force Round/Round and
therefore form one point disk. The hot path uses two fixed stack arrays, and
nonempty dashed zero-length strokes remain fail closed until their initial dash
phase is represented exactly. All ten local native tests passed. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`, and all 11 native/Dawn
CTests passed on the Parallels VM. Package checkpoint `9d3d0033` added immediate
and retained open/closed degenerate strokes. Both MIL exports compiled its
52-command, 23-channel-resource seed; live D3D12 readback retained 27 semantic
resources, three draws, and 41,472 coverage bytes. Exact staged SHA-256 values
were
`6afa15e6fff5a41e274674be9678d80f6bb88085078a0685eb3673d5e5467f4e`
for `progpu_native.dll` and
`560c4691baa714d366cc4817c853e41ae8d17a6140d74f06b3db5a505636d666`
for `progpu_native_dawn.dll`.

The degenerate dash-phase implementation at `70b738b7` then applied WPF's
`CDashSequence::Initialize` parity rule to those point caps. Finite nonzero
patterns normalize positive or negative offsets over the effective even-length
cycle, repeat odd source lists, keep exact-boundary offsets on the preceding
interval, emit the cap pair only when that interval is a dash, and emit no draw
when it is a gap. Zero-total-length cycles remain fail closed. All ten local
native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `61ed465d` moved the retained degenerate path onto a boundary-offset
dash resource and pen. Both MIL exports compiled its 56-command,
25-channel-resource seed; live D3D12 readback retained 27 semantic resources,
three draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`53d589f6580afd495e2bcb98d64c23c7acb1b450baf60027a5b7b371618774c3`
for `progpu_native.dll` and
`81a9450fc3af12677152fdb8777ab1ba346c1f5017e425858d476bd6e9076feb`
for `progpu_native_dawn.dll`.

The degenerate ellipse implementation at `bbb4b2c2` then traced
`CFigureData::InitAsEllipse`, whose four cubic segment types all carry
`SmoothJoin`. A zero X or Y radius therefore lowers exactly to one line with
Round/Round ends, while two zero radii reuse the point-disk cap pair. Degenerate
fills remain empty, immediate and retained `EllipseGeometry` paths share the
same lowering, and local affine state stays on the reusable geometry primitive.
Nonempty dashes on a one-axis ellipse remain fail closed with the broader curve
dash gate. All ten local native tests passed. Strict Windows ARM64 MSVC rebuilt
both modules under `/W4 /WX`, and all 11 native/Dawn CTests passed on the
Parallels VM. Package checkpoint `e909fd60` added immediate and retained
one-axis ellipses. Both MIL exports compiled its 58-command,
26-channel-resource seed; live D3D12 readback retained 29 semantic resources,
three draws, and 41,472 coverage bytes. Exact staged SHA-256 values were
`8e235e440a980fcdf63c4770c33a2afbcd9f92a06667671daa33c7406e50457a`
for `progpu_native.dll` and
`2ecd3a808e9ee65d50cae7637e365d00820febb02a63067849ace0b73d54df58`
for `progpu_native_dawn.dll`.

The degenerate rectangle implementation at `762887cb` then followed
`CRectangle::WidenToShape` and `CPlainPen::Get90DegreeBevelOffset` rather than
asking the generic stroke rasterizer to interpret coincident closed edges.
Because WPF omits the inner boundary unless both original dimensions exceed
the full pen width, every zero-area rectangle is exactly one outer figure.
Sharp Miter and Bevel cases lower to the same four- or eight-edge vector path;
Round joins and source-rounded rectangles lower to four analytic elliptical
quarter arcs with WPF's independent dimension clamps. Degenerate fills remain
empty, local affine state remains typed on the path, and nonempty dashed
collapses still fail closed. Immediate and retained fixtures cover line and
point collapses, all three public joins, rounded-source radii, transformed
bounds, and the dash boundary. All ten local native tests passed. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`, and all 11 native/Dawn
CTests passed on the Parallels VM. Package checkpoint `557c67fb` added immediate
Round and rounded collapses plus retained transformed rectangle geometry. Both
MIL exports compiled its 62-command, 28-channel-resource seed; live D3D12
readback retained 32 semantic resources, issued six draws, and staged 61,440
coverage bytes. Exact staged SHA-256 values were
`35610b8e6e6250d8d150e4a855e52a306f28af12dde286b41822baf5d5bab3eb`
for `progpu_native.dll` and
`7f3cf20154beb9c305de9b2477fbd6cb967292da61405afb35b2f46f936fa19a`
for `progpu_native_dawn.dll`.

The non-uniform rounded-rectangle implementation at `e17acda6` then removed
the single-radius analytic-primitive restriction for positive independent X/Y
radii. Immediate and retained rectangles construct the same eight-segment
typed contour: four exact elliptical quarter arcs, four connecting lines, and
the `SmoothJoin` bit on every incoming WPF segment. Fill uses the shared vector
path batch, while solid stroke reuses ProGPU's connected arc/line geometry and
emits eight native Round joins. Geometry-local affine state remains on both
resources and the wgpu-native/Dawn scene stream is identical. Nonempty curved
dashes and asymmetric cases with either radius zero remain fail closed. All ten
local native tests passed. Strict Windows ARM64 MSVC rebuilt both modules under
`/W4 /WX`, and all 11 native/Dawn CTests passed on the Parallels VM. Package
checkpoint `f7fef044` made the immediate package draw non-uniform and changed
the retained `RectangleGeometry` used directly and recursively to independent
radii. Both MIL exports compiled the unchanged 62-command,
28-channel-resource seed; live D3D12 readback retained 34 semantic resources,
issued ten draws, and staged 78,848 coverage bytes. Exact staged SHA-256 values
were
`01dedafe1c059b043a422385f8d04085235d0f0b526be382fc8f3f97d2eb6641`
for `progpu_native.dll` and
`bcc551bf815c18ffb601d517f2c10be702fdf1e0b86a11cdd6b39b95c02b10a9`
for `progpu_native_dawn.dll`.

The zero-axis rounded-rectangle checkpoint then implemented WPF's earlier
`CShape::AddRoundedRectangle` equivalence rule at native commit `9a615714`.
For positive width and height, either zero radius now lowers immediate and
retained rounded-rectangle records to the exact sharp rectangle analytic fill
and closed-polyline stroke while retaining the rounded-rectangle metric.
Degenerate zero-axis asymmetric rectangles continue to fail closed because
they enter WPF's general widener rather than the optimized `CRectangle` path.
All ten local native tests passed. Strict Windows ARM64 MSVC rebuilt both
native modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the
Parallels VM. Package checkpoint `6a4f9f90` compiled an immediate zero-axis
draw and retained zero-axis `RectangleGeometry` through both MIL exports in a
64-command, 29-channel-resource seed. The project-reference consumer built
with zero warnings; live D3D12 readback retained 38 semantic resources, issued
11 draws, and staged 78,848 coverage bytes. Exact qualified SHA-256 values were
`4c773f255b27ef00990ca52b89e428750a4108289de60ba5a50412b19c354d2f`
for `progpu_native.dll` and
`9b7434a0d2bea32861f2b3018078cff8dd183271da4ebffb9657aa4282b83476`
for `progpu_native_dawn.dll`.

The canonical static-transform implementation at `f6f82b91` then added all
remaining two-dimensional static resource packets: Translate, Scale, Skew,
Rotate, and ordered TransformGroup. Leaf transforms follow WPF's float matrix
evaluation, including center translation and modulo-360 angle handling; groups
remain retained dependency graphs and re-resolve current child state for visual,
drawing-scope, geometry, group, combined-geometry, and clip consumers. Native
fixtures cover meaningful ordered composition, nested groups, child updates,
animation rollback, cycle rejection, and referenced-child deletion rejection.
All eight locally configured native suites passed. Strict Windows ARM64 MSVC
rebuilt both native modules under `/W4 /WX`, and all 11 native/Dawn CTests
passed in the Parallels VM. Package checkpoint `8bc860e4` exercised all five
public managed builder APIs through both MIL exports in a 74-command,
34-channel-resource seed. Its identity-equivalent group preserved the prior
live D3D12 contract exactly: 38 semantic resources, 11 draws, and 78,848
coverage bytes. The project-reference consumer built with zero warnings. Exact
qualified SHA-256 values were
`301561a6f02de5a392b042f763134720a9a4b3d29f47b379c1018fc31c429d9c`
for `progpu_native.dll` and
`c3a800ba100508178a0d9f5837b07f9c6428a2bb616b1bb0d6a4708d0529da06`
for `progpu_native_dawn.dll`.

Transform-animation implementation `04ae7747` then added canonical
`DoubleResource` and `MatrixResource` current-value packets and retained their
typed dependencies from every static transform family. Native resolution reads
the current referenced scalar/matrix on each scene compilation, preserves base
values only when the animation handle is zero, and propagates resource updates
through nested groups without transform-packet rewrites. Fixtures cover scalar
and matrix replacement, live updates, wrong resource types, rollback, and
referenced-animation deletion rejection. All eight locally configured native
suites passed. After merging the latest ProGPU `main`, strict Windows ARM64
MSVC rebuilt both complete native modules under `/W4 /WX`; all 11 native/Dawn
CTests passed in the Parallels VM. Package checkpoint `d07ab05d` exercised the
two new resource builders and animated transform handles through both exports
in a 78-command, 36-channel-resource seed. Its identity-equivalent current
values preserved live D3D12 output at 38 semantic resources, 11 draws, and
78,848 coverage bytes; the project-reference consumer built with zero warnings.
Exact qualified SHA-256 values were
`a903edec8bb58e314e2738d64f8246ccc7a9f83e2d0c33755f3855ff043c233e`
for `progpu_native.dll` and
`e19d905e42d5030bf2aded0182fa1c8eb9bfc27f9a974cc3aa4d21b6507d33b0`
for `progpu_native_dawn.dll`.

Native gradient implementation `1a937dbd` and managed packet-builder
checkpoint `5d3b96f0` then added retained linear/radial gradient resources,
`PointResource` current values, bounds-relative mapping, brush transforms,
anisotropic focal radial state, both interpolation modes, all three spread
modes, and stable out-of-range stop normalization. Native fixtures validate
live point/double updates, transform ordering, enum remapping, stop payloads,
wrong-type rejection, and dependency-protected deletion. All eight locally
configured native suites passed. Strict Windows ARM64 MSVC rebuilt both native
modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the Parallels
VM. The managed builder tests passed 6/6 and the project-reference package
consumer built with zero warnings.

Focused package gate `5db0910e` compiled one mixed solid/linear/radial scene
through both MIL exports using 15 commands and six channel resources, then
installed and rendered it on live D3D12. The retained renderer reported five
semantic resources, one batched draw, zero coverage-staging bytes, a valid
submission, and nonblack readback; the following direct render also read back
16,384 pixels. The broader unchanged 78-command/36-resource MIL seed passed
both export contracts. Its dense path/boolean live render remained noisy on
the documented Parallels adapter and was not used as gradient evidence. Exact
qualified SHA-256 values were
`84f9ff3fcc3b1030fba0150891a92d176ea63d5cca7641af97d7f57d36f0cb54`
for `progpu_native.dll` and
`3779ab39f5d324f666eccc2452d0a21caf5ac5c2bea8d9eee2acede9fe8c6bf5`
for `progpu_native_dawn.dll`.

GeometryDrawing implementation `43ef1cf5` and focused package gate
`64206983` then added the exact 24-byte resource-update and 16-byte nested draw
records to the C++ channel and managed packet builders. The native fixture
verifies shared geometry lowering, null-geometry no-op behavior, protected
dependencies, wrong-type rejection, and rollback. All ten local native CTests
passed, the managed canonical-builder filter passed 6/6, and the package
consumer built with zero warnings. After merging current ProGPU `main`, strict
Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`; all 11
native/Dawn CTests passed in the Parallels VM. The focused 15-command,
six-resource GeometryDrawing scene compiled through both MIL exports and
rendered on live D3D12 with three semantic resources, one batched draw, zero
coverage-staging bytes, a valid submission, nonblack retained readback, and
16,384 direct-render pixels. Exact qualified SHA-256 values were
`14636dca53dbecb0defd05a356642ac39cac9982d4ef918dc3d50e538cf99c3a`
for `progpu_native.dll` and
`5abd082989ae7df2b77cd727081f761d1211d5803d71cfd9102056f1a2d6034c`
for `progpu_native_dawn.dll`.

DrawingGroup implementation `49d448af` and focused package gate `85f55ab2`
then added canonical resource type `91` and command `0x8b`, including WPF's
52-byte command-view prefix (56 bytes when framed) followed by its ordered
child-handle payload. Groups retain typed child drawings, transform, exact
geometry clip, static opacity, and live `DoubleResource` opacity dependencies;
nested compilation reuses the same `DrawDrawing`/`DrawGeometry` lowering and
preserves the parent semantic scope. Native fixtures cover live opacity
updates, transformed clips and analytic bounds, protected dependencies,
cycles, wrong child types, and transactional rollback. Opacity masks,
guideline sets, effects/cache state, and nondefault edge, bitmap-scaling, or
ClearType options remain explicit compile-time unsupported states until their
native semantic resources are implemented.

All ten local native CTests passed, the managed canonical-builder filter passed
7/7, and the project-reference package consumer built with zero warnings.
Strict Windows ARM64 MSVC rebuilt both native modules under `/W4 /WX`, and all
11 native/Dawn CTests passed in the Parallels VM. The focused 23-command,
ten-channel-resource DrawingGroup scene compiled through both MIL exports and
rendered on live D3D12 with four semantic resources, one batched draw, zero
coverage-staging bytes, a valid submission, nonblack retained readback, and
16,384 direct-render pixels. Exact qualified SHA-256 values were
`d20b7d78eff8905c7d1130c12980bbe2bc02a70337cbb4461c1279562fe624da`
for `progpu_native.dll` and
`e8c7dce855f34877abe3c211a7970235444402a37bfd940f0a4afbfea5f1a6a2`
for `progpu_native_dawn.dll`.

ImageDrawing implementation `6d99ced4`, focused package gate `03acffe0`, and
expectation correction `46175bf3` then added canonical resource type `89` and
command `0x89` with its exact 48-byte command-view payload (52 bytes framed).
The retained drawing references canonical `TYPE_BITMAPSOURCE` handle `95`.
Because WPF's original `MilCmdBitmapSource` transports an in-process
`IWICBitmapSource*`, portable hosts bind copied straight-alpha RGBA8 pixels to
that handle through the typed
`progpu_native_mil_channel_set_bitmap_source_rgba8` sideband. No process
pointer enters the retained graph or semantic scene. The same compiled scene
image resource and draw command execute through wgpu-native and Dawn.

Native fixtures verify missing-binding failure, exact destination/source/bounds
state, copied upload bytes, resource generation, dependency-protected deletion,
and invalid stride/type rejection. All ten local native CTests passed, the
managed canonical-builder filter passed 8/8, and the project-reference package
consumer built with zero warnings. Strict Windows ARM64 MSVC rebuilt both
native modules under `/W4 /WX`, and all 11 native/Dawn CTests passed in the
Parallels VM. The focused 12-command, five-channel-resource ImageDrawing scene
compiled through both MIL exports and rendered on live D3D12 with two semantic
resources, one image draw, zero coverage-staging bytes, a valid submission,
nonblack retained readback, and 16,384 direct-render pixels. Exact qualified
SHA-256 values were
`d396e5bcc5b9093271878499fafabae9e0b1fb0e7db6fd9aac8379e14ea64749`
for `progpu_native.dll` and
`4fe6051479644bfe40019e5d45570f68c57aeaae5040096b2fc257fe60c405d5`
for `progpu_native_dawn.dll`. Rect animations and canonical MediaPlayer video
now have later typed implementations. D3DImage/shared-surface synchronization,
planar/HDR video, and incremental bitmap invalidation remain explicit follow-up
work.

Animated-value implementation `a7dcd8de` closes the scalar resource and core
render-data animation gap. The native channel now decodes the generated WPF
layouts for ColorResource, RectResource, SizeResource, Point3DResource,
Vector3DResource, and QuaternionResource in addition to the existing Double,
Point, and Matrix resources. Updates require the exact resource type and packet
size, reject non-finite values and negative rectangle/size extents, increment
the retained generation only after validation, and roll back transactionally
with the rest of the submitted batch on failure.

Nested DrawLineAnimate, DrawRectangleAnimate, DrawRoundedRectangleAnimate,
DrawEllipseAnimate, and DrawImageAnimate commands resolve their live Point,
Rect, and Double dependencies during semantic-scene compilation. Static
DrawImage now shares the same path. Direct bitmap draws and retained
ImageDrawing both consume the copied, pointer-free RGBA8 BitmapSource sideband;
ImageDrawing also resolves its RectResource destination animation. Dependency
revision traversal includes every new animation and image handle, so updating a
value resource invalidates retained output without retransmitting render data.
Native fixtures exercise all six newly decoded value-resource families, every
new animated primitive, static and animated image draws, retained animated
ImageDrawing, exact semantic coordinates, and malformed-update rollback.

SolidColorBrush now resolves both its DoubleResource opacity animation and
ColorResource color animation at scene-compilation time. The shared resolver
feeds analytic/path fills, pen brushes, glyph text styles, and uniform opacity
masks, so no draw family can accidentally retain the brush's base value while
another consumes its live value. Retained revision traversal includes both
animation handles, deletion is dependency-protected, and tests mutate both
resources without retransmitting the brush or render data and verify the new
semantic brush payload.

BlurEffect and DropShadowEffect now use the same live retained-resource model.
Blur radius resolves its type-49 DoubleResource; drop-shadow depth, direction,
opacity, and blur radius resolve their four DoubleResources, while shadow color
resolves its type-50 ColorResource. Scene compilation applies WPF's existing
radius truncation, transform scaling, direction mapping, and alpha composition
to the resolved values. The effect-chain and layer revisions incorporate the
entire typed animation dependency graph, so a value-only update invalidates the
correct cached effect without retransmitting the effect or Visual. Every edge
is transactionally type-checked and deletion-protected. Native coverage mutates
all six live values and verifies sigma, offset, color/alpha, inflated bounds,
revision changes, and referenced-resource deletion rejection. Box blur remains
an explicit unsupported kernel; no shader or CPU approximation is substituted.

LineGeometry, RectangleGeometry, and EllipseGeometry now resolve all canonical
animation handles through the same typed retained-value graph. Line endpoints
and ellipse centers consume PointResource values; rectangle bounds consume a
RectResource; rectangle and ellipse radii consume DoubleResource values. The
resolved geometry is shared by direct DrawGeometry, retained GeometryDrawing,
recursive drawing/group traversal, shallow fill collection, and retained clip
lowering, so value-only updates cannot leave a stale geometry in a secondary
consumer. Cache content revisions include every animation edge, referenced
value resources are deletion-protected, and non-finite or negative live
dimensions/radii fail closed during scene compilation. Native coverage mutates
all point, rectangle, and radius resources without retransmitting any geometry
or render data, then verifies the second frame's exact line, rounded rectangle,
ellipse, and cache revision.

Pen thickness and DashStyle offset animations also resolve from their canonical
DoubleResource handles without rebuilding the Pen, DashStyle, or render-data
stream. A single resolved Pen value is passed through immediate and retained
line, path, rectangle, rounded-rectangle, ellipse, degenerate-cap, group, and
combined-geometry stroke decisions; dashed polyline and degenerate-dash logic
read the same live offset. This keeps stroke bounds, brush mapping, analytic and
vector primitives and curve-dash phase/cap/join decisions on one
current-value view. Pen and DashStyle cache revisions include their scalar
animation dependencies, deletion is protected, wrong resource types are
transactionally rejected, and a negative live thickness fails closed during
scene compilation. Native coverage updates only thickness and offset and
verifies the exact second-frame stroke plus cached-layer revision.

Nested PushEffect now matches the current WPF milcore behavior rather than the
obsolete public API's historical intent. The managed producer emits the exact
12-byte record view (four-byte command header plus two managed dependent-
resource indices), but WPF's native executor explicitly disables legacy
BitmapEffect execution and lowers the scope to `PushOpacity(1)`. ProGPU
therefore validates the generated frame, treats both handles as opaque
managed-only values, saves identical render state for Pop matching, and does
not add either value to native cache dependencies. Balanced scopes—including
inside a retained BitmapCache—compile as semantic no-ops; missing Pop remains
`invalid_graph`, and the obsolete four-byte header-only shape remains
`malformed_batch`. Modern typed Visual BlurEffect/DropShadowEffect execution
is unaffected and continues through the native effect resource path above.

Direct DrawingImage replay now uses the same typed retained vector path as an
ImageDrawing that references a DrawingImage. Static and animated DrawImage
resolve the source's canonical Drawing handle, clip to the destination
rectangle, and compose the source-to-destination affine mapping before
recursively compiling the Drawing. A nested `ImageDrawing` now contributes its
live static or animated destination rectangle when its image source is non-null,
then applies the complete current group transform and active clip. This follows
WPF `BoundsDrawingContextWalker.DrawImage` directly: bounds depend on the image
rectangle, not a pixel readback or recursive inspection of image content. A
fill-only `GeometryDrawing` backed by a
positive-area fixed rectangle, rounded rectangle, or ellipse now derives its
current exact bounds directly from typed native geometry and transform state;
animated fixed values and transforms are therefore resolved at scene-build
time instead of being cached in the `DrawingImage`. A fill-only `PathGeometry`
derives bounds from its emitted post-transform fill segments, using endpoint
reduction for lines, analytic derivative roots for quadratic and cubic Beziers,
and canonical ellipse extrema for arcs. The transformed-segment scratch buffer
is reused across image commands in one render stream. Unfilled figures,
control-point hulls, and packet bounds therefore cannot broaden the mapped
content. `DrawingGroup` trees now compose each nested affine group transform
into the leaf geometry before calculating bounds, then union the separately
drawn, transformed child bounds. Rotation and shear are therefore exact for
supported fill leaves; multiple children are safe here because they are
independent draws rather than one fill-rule contour. Static fixed, path, and
geometry-group clip bounds are transformed into the same world space and
intersect every transformed child before child results are unioned, matching
WPF's `BoundsDrawingContextWalker` ordering without transforming an already
broadened union or filling gaps between separately drawn children. Empty and
singular groups are valid empty draws. Group opacity, animated opacity, opacity
masks, guidelines, edge
mode, bitmap sampling, and ClearType state deliberately do not participate in
bounds, also matching the WPF walker. Unsupported clip geometry, stroked fixed
shapes and path/group strokes under non-axis-preserving transforms, and other
unsupported leaf transforms still require the exact drawing-content-bounds
sideband and fail closed when it is absent. The exact native lane also accepts
single-child/nested-single-child `GeometryGroup` chains and composes every group
and leaf transform; multi-child groups remain sideband-only until WPF fill-rule
cancellation bounds have a qualified oracle. Fixed lines plus rectangles,
rounded rectangles, and ellipses—including zero-width, zero-height, and point
degenerates—with a solid Pen now reuse
the renderer's canonical live Pen resolver, cap-aware line bounds, and shared
positive-shape stroke-bounds helper. Animated thickness changes the inferred
DrawingImage mapping without retransmitting the Pen or Drawing. Fixed shapes
still require axis-preserving effective transforms. Lines with flat, square,
triangle, or round caps instead preserve WPF's separate geometry-transform,
pen-widening, and group/world-transform stages. Polygonal caps reduce the
actual stroke vertices; round caps reduce WPF's two canonical cubic arcs.
A missing DashStyle and a DashStyle with an empty interval collection both take
the solid lane; nonempty dashed and path/group
stroke cases remain sideband-only. No bitmap intermediate,
pointer
transport, reflection, or host raster fallback is introduced. Recursive
image/drawing ownership is rejected as `invalid_graph`, and an empty
DrawingImage remains a no-op. Native coverage locks down distinct retained,
direct-static, and direct-animated destination mappings plus sideband-free
fixed, polygonal, curved, arc, independently transformed path,
single-child geometry-group, and nested multi-child drawing-group replay. The
drawing-group case combines a real rectangle clip with animated opacity, an
opacity mask, a guideline resource, aliased edges, nearest image sampling, and
ClearType. The clip intersects only one of two separated children, so the test
verifies the per-child clip-derived destination mapping while retaining a shear
rejection oracle. Separate native coverage verifies exact square-cap mappings
at two animated thickness values, rounded-rectangle and ellipse mappings,
zero-width rectangle, collapsed-axis ellipse, and point-ellipse mappings, plus
geometry- and group-level shear oracles and nonempty-dashed-Pen rejection. The same Pen
then succeeds after its DashStyle resource updates to empty intervals without
retransmitting the Pen or Drawing.
Checkpoint `30fcf084` extends that same sideband-free lane to general affine
`DrawingGroup` transforms by carrying the composed transform and active clip
down to each supported leaf. Native coverage verifies the original nested
axis-aligned mapping, an exact sheared mapping, destination clipping, and valid
empty results for singular and childless groups. The complete Apple native
suite passes 8/8. A clean commit archive then rebuilt all 136 focused target
steps in the Windows 11 ARM64 Parallels guest with MSVC `19.44.35228.0` under
`/W4 /WX`; the focused CTest passed in 0.79 seconds. Host and guest source
SHA-256 values matched at
`AB4E6081F6C40332A3F776D3AB417E67D3CB9DF292C5685AED39848B84DFEE08`
for `progpu_native_mil.cpp` and
`61622AB8BA666678074B5298B53062BA55C70C90E97FB047763FC461F2B23767`
for its test. The guest executable SHA-256 was
`DBDA27CD933D4D4A17B4FE70D55204A6481A16B9AA29DC8BF886974C3C82C6A4`.
Checkpoint `6a7652a9` then makes `ImageDrawing` a first-class leaf in that
sideband-free bounds walk. The differential fixture wraps a vector-backed
ImageDrawing in a sheared DrawingGroup and another DrawingImage, verifies the
derived general-affine source mapping and destination clip, updates the
ImageDrawing through a live `RectResource`, and verifies the new mapping
without retransmitting either DrawingImage. The complete Apple native suite
passes 8/8. A clean archive rebuilt all 136 focused target steps under Windows
ARM64 MSVC `19.44.35228.0` with `/W4 /WX`; focused CTest passed in 4.22
seconds and direct execution returned zero. Host and guest hashes matched at
`6ACDD31B6F3E964AC1F2420FCD1193EF58907276766AD996FFBD50917276DEEB`
for `progpu_native_mil.cpp` and
`0B239F7E78D98642A27BC09D5B0C60636034E0B967FA6B76A2ADEC824B7D11C8`
for its test. The guest executable SHA-256 was
`08C83E4E428AD441321281AC701D05B28CF62B4D65B815B7F5ADA999E932BAAB`.
Checkpoint `14e870f5` adds `GlyphRunDrawing` as another exact bounds leaf.
Canonical WPF `MilCmdGlyphRunCreate.ManagedBounds` already contains
`ComputeInkBoundingBox()` offset by `BaselineOrigin`, exactly the rectangle
consumed by `BoundsDrawingContextWalker.DrawGlyphRun`; ProGPU therefore uses
that typed packet field directly and does not reconstruct metrics or inspect
font outlines for bounds. A null foreground brush or empty managed ink box is
a valid empty draw. Coverage renders the pointer-free SFNT glyph both directly
and through a sheared DrawingGroup/DrawingImage, checks the full affine mapping,
destination clip, and transformed glyph command bounds, and retains the
existing grayscale/ClearType/aliased text gates. Apple native tests pass 8/8.
A clean archive rebuilt all 136 focused target steps under Windows ARM64 MSVC
`19.44.35228.0` with `/W4 /WX`; focused CTest passed in 0.84 seconds and direct
execution returned zero. Host and guest hashes matched at
`FA158FC69DB80D9885CA00B1AFCC909836F0DAF0A81388587D2BDE37852BF398`
for `progpu_native_mil.cpp` and
`B2F34697CDC24C4C2DE6E133860754A0BCBC71653ACEED91F68C7EE26B87CA6E`
for its test. The guest executable SHA-256 was
`48153630050BEDA01C79EDF0D9B4F7FE4EF5CBE881B89DD577820352D5E93604`.
Checkpoint `34529979` extends solid line-stroke inference to general affine
effective transforms for flat, square, and triangle caps. The bounds helper
transforms the actual four strip vertices plus cap vertices and reduces those
world-space points, avoiding the incorrect transform-of-local-AABB shortcut.
Coverage checks sheared square- and triangle-cap mappings while retaining the
nonempty-dash rejection and live empty-DashStyle success. Apple native tests
pass 8/8. A clean archive rebuilt all 136 focused target steps under Windows ARM64 MSVC
`19.44.35228.0` with `/W4 /WX`; focused CTest passed in 1.00 second and direct
execution returned zero. Host and guest hashes matched at
`00917E307C125E7F8573BBC487C3E51395191EF84986C1768E3ABCADD73E64A2`
for `progpu_native_mil.cpp` and
`8EC1AC257B61C7F7A4888002B54392A751EE2BEE4391CB2B1B0BA4428C751CF6`
for its test. The guest executable SHA-256 was
`FE386E7FB3B93E0BE7125E8AD60B7005CBF6D2264C1A12FAA8A4EB2CC38A2051`.
Checkpoint `cd3e70c3` completes that line-cap lane and corrects the initial
affine implementation against live Windows WPF. `Geometry.Transform` maps the
line spine before WPF widens it by the Pen; `DrawingGroup.Transform` maps the
already widened stroke afterward. ProGPU now keeps those stages separate.
Round caps use the exact WpfGfx `ARC_AS_BEZIER` constant and analytic cubic
derivative roots. In the Windows 11 ARM64 Parallels oracle, the same 8-unit
round-capped line and `[1,.25,.5,1,0,0]` matrix produced
`15.999053955078125,18.499053955078125,28.00189208984375,13.00189208984375`
as `Geometry.Transform` bounds and
`15.526884078979492,18.375919342041016,28.946229934692383,13.248161315917969`
as `DrawingGroup.Transform` bounds; native mappings for both are locked down,
alongside the corresponding square and triangle WPF oracles. Apple native
tests pass 8/8. A clean archive rebuilt all 136 focused target steps under
Windows ARM64 MSVC `19.44.35228.0` with `/W4 /WX`; focused CTest passed in
1.62 seconds and direct execution returned zero. Host and guest hashes matched
at `C1A647F6BDF4650C78ED2FEE52F4BC4AE1CC43D4E544539F426716F4E3BA7E0F`
for `progpu_native_mil.cpp` and
`554160BFEF4E6B8F5A4A2BF2B08A2CEA69DD45B916FFEBDDA671BBC6E983E08D`
for its test. The guest executable SHA-256 was
`02BB1A776DBDC7A019D12BC362944B8253324334C97EF2B5796FFEB48F0AD2EE`.
Checkpoint `c16178cd` extends the same WPF transform ordering to positive-area,
non-rounded rectangle strokes. It transforms the closed rectangular spine by
`Geometry.Transform`, constructs normalized edge strips and exact outer miter
intersections (or bevel vertices), and only then applies the current
`DrawingGroup` transform. For rectangle `[20,10,30,15]`, thickness 8, and
matrix `[1,.25,.5,1,0,0]`, live PresentationCore returned
`17.532926559448242,9.0101261138916016,52.434144973754883,34.479745864868164`
for `Geometry.Transform` and `19,10,49.5,32.5` for
`DrawingGroup.Transform`; the native image mappings lock down both. The bevel
oracle is also exact at
`21.422290802001953,11.119429588317871,44.655414581298828,30.261139869689941`.
WPF clips over-limit miters rather than reducing them to the current native
bevel tessellation, so that case fails closed until a shared clipped-miter
outline is implemented. Round joins, rounded rectangles, ellipses, and dashed
fixed shapes remain separate parity lanes. The complete Apple native suite
passes 10/10. A clean archive rebuilt all 136 target steps under Windows ARM64
MSVC `19.44.35228.0`; `/W4 /WX` appeared on 161 Ninja flag lines, focused
CTest passed in 0.96 seconds, and direct execution returned zero. Host/guest
hashes matched (`D70BEB9B...267072` native, `83DB8A55...58449F` test,
`C7CD2AD9...A3A5F` archive); the guest executable SHA-256 was
`D7F63F0EAEE4574872B88D20DD2E6E75C2DE71706D7094D342CC607434211CC8`.
Checkpoint `d1025caf`, with strict-MSVC cleanup and internal coverage through
`026ce1a7`, closes that clipped-miter gap in rendering and bounds together.
The shared native stroke tessellator now implements WpfGfx
`CSimplePen::DoLimitedMiter`: it derives the two clip points from the incoming
and outgoing unit directions, pen radius, and nominal miter limit, then emits a
three-triangle fan. Rectangle bounds use the identical formula before the
world transform. The live `MiterLimit=1` oracle above now maps exact WPF bounds
`20.276056289672852,10.497797012329102,46.94788932800293,31.50440788269043`
instead of failing closed. Apple native tests pass 10/10. A clean exact archive
built 153 MIL/internal target steps with Windows ARM64 MSVC `19.44.35228.0`;
161 Ninja flag lines carry `/W4 /WX`, and both focused tests passed in 2.71
seconds. Direct executions returned zero. Host and guest hashes matched
(`924F4560...2F6FF6` stroke header, `7E305BF5...B641C2` MIL,
`0E2EC09A...FAB1E5` MIL test, `10CD93E7...3D16D0` internal test, and
`0995611D...222AD` archive). Guest executable SHA-256 values were
`707D8EEAE6830997761661664FA7A3D3955822D29500382E828FC093996962D4`
for MIL and
`4A36E7E6E3AE5B00D34252F5556793FC31A46A96AC61B7D8A4DFE277210D1958`
for the internal topology gate.
Checkpoint `269005a5` extends exact affine rectangle bounds to WPF round joins.
The implementation follows WpfGfx `CSimplePen::RoundCorner` and
`GetBezierDistance`: it applies the 0.25 default widening tolerance/refinement
threshold, emits the same one- or two-cubic outer arc, and evaluates analytic
cubic derivative roots after the `DrawingGroup` transform. It therefore does
not replace the rounded outline with a transformed circle or broad local AABB.
For rectangle `[20,10,30,15]`, thickness 8, and matrix
`[1,.25,.5,1,0,0]`, live Windows PresentationCore returned
`20.999963760376,10.9998416900635,45.5000743865967,30.5003185272217`
for `Geometry.Transform` and
`20.5268840789795,10.875919342041,46.4462299346924,30.748161315918`
for `DrawingGroup.Transform`; both native image mappings are locked down.
The complete Apple native suite passes 8/8. The exact commit archive rebuilt
153 MIL/internal target steps under Windows ARM64 MSVC `19.44.35228.0`; 161
Ninja flag lines carry `/W4 /WX`, both focused CTests passed in 4.01 seconds,
and direct execution returned zero. Host and guest hashes matched for the
archive (`1CAB3180...F60569`), MIL source (`D6730AFD...91BDB`), and MIL test
(`8581A4D5...FCD3F`). Guest MIL and internal executable SHA-256 values were
`865BB142...99714` and `287ECC0A...48117`.
Checkpoint `aadd184f` adds the corresponding exact affine ellipse-stroke
bounds profile. It reconstructs the WPF ellipse as four float-quantized
`ARC_AS_BEZIER` cubics, applies `Geometry.Transform`, and reproduces
`CBezierFlattener` hybrid-forward-differencing with the 0.25 tolerance before
offsetting each emitted tangent by the pen radius. The implementation uses
fixed-size arrays and applies the current `DrawingGroup` transform only after
widening. For center `(20,30)`, radii `(10,5)`, thickness 8, and matrix
`[1,.25,.5,1,0,0]`, live WPF returned
`20.719608306884766,25.423517227172852,28.560783386230469,19.152963638305664`
for `Geometry.Transform` and
`20.239826202392578,25.299463272094727,29.520347595214844,19.40107536315918`
for `DrawingGroup.Transform`; native mappings lock down both. When WPF's
thick-stroke refinement threshold would add extra `RoundTo` cubics, this first
profile fails closed and a regression test preserves that boundary. Apple
native tests pass 8/8. The exact archive rebuilt 153 MIL/internal steps under
Windows ARM64 MSVC `19.44.35228.0`; 161 Ninja flag lines carry `/W4 /WX`, both
focused tests passed in 1.57 seconds, and direct executions returned zero.
Host/guest hashes matched (`471096C4...D6234D` archive,
`F55BB225...ACFAF8` MIL source, `1BE90C6C...5D51BE` MIL test). Guest MIL and
internal executable hashes were `BB97651B...85AAE6` and
`2B2E729A...8F8FE7`.
Checkpoint `7521787d` supersedes the earlier thick-stroke fail-closed boundary.
The fixed-array ellipse walker now mirrors WpfGfx `CPen::AcceptCurvePoint`:
when the flattened tangent turns far enough at a large pen radius, it emits
the previous-tangent-to-chord `RoundTo`, the chord offset pair, and the
chord-to-current-tangent `RoundTo`. The round-corner helper preserves WpfGfx's
very-flat bevel endpoint and otherwise contributes the same one/two cubic
arcs and analytic world-space extrema, without allocations. At thickness 64,
the same ellipse and matrix produced live PresentationCore bounds
`-7.13843107223511,-2.55054593086243,84.2768588066101,75.101090669632`
for `Geometry.Transform` and
`-10.9766893386841,-3.54298257827759,91.9533739089966,77.0859665870667`
for `DrawingGroup.Transform`; tests retain both thick mappings as well as the
thickness-8 profile. The complete Apple native suite passes 8/8. The immutable
archive SHA-256 is `FA85410E...E3E01`; its exact sources rebuilt under Windows
ARM64 MSVC `19.44.35228.0` with 161 `/W4 /WX` Ninja flag lines. Both focused
CTest binaries passed in 1.39 seconds and direct executions returned zero.
Host/guest source hashes matched (`8B71B473...77D4D` MIL and
`691DAD8A...CE95` MIL test); guest MIL/internal executable hashes were
`037D26AF...7BFBD` and `B5635F7D...6EE5`.
Checkpoint `e4d1d2c8` closes the corresponding shared managed-renderer gap:
`StrokeJoinGeometry` now emits the same WPF clipped-miter three-triangle
fallback through both its allocation-free span writer and allocating
compatibility API. Checkpoint `2f55b1ba` below subsequently scopes this WPF
rule to typed MIL/WPF consumers, preserving standard joins for generic
ProGPU, Svg.Skia, and downstream callers instead of weakening either parity
gate.
The previously failing 96-polyline Apple Metal comparison is byte exact
(`max=0`, zero differing pixels, identical `C67040E2A28F2507` frame hashes),
with matching 3,408 vertices and 5,112 indices. The full managed suite passes
3,880/3,880 and the Apple native suite passes 10/10.
Checkpoint `5a47e701` extends the same exact bounds lane to affine rounded
rectangles. The shared fixed-array contour walker now accepts WPF's alternating
smooth cubic/line topology, keeps Geometry.Transform widening separate from
DrawingGroup/world mapping, tests HFD flatness in device space, and scales the
pen refinement threshold by the world transform's maximum singular value.
That last rule preserves the `RoundTo` cubic extrema which are intentionally
absent when a pre-widened local polyline is merely transformed afterward. For
rectangle `[20,10,30,15]`, radii `(5,3)`, thickness 8, and matrix
`[1,.25,.5,1,0,0]`, live PresentationCore returned
`22.42738151550293,11.999236106872559,42.645235061645508,28.501526832580566`
for `Geometry.Transform` and
`21.880094528198242,11.876118659973145,43.739809036254883,28.747763633728027`
for `DrawingGroup.Transform`; native mappings lock down both. Apple native
tests pass 10/10 and the MIL-only configuration passes 8/8. The immutable
archive rebuilt 153 steps under Windows ARM64 MSVC `19.44.35228.0`; 161 Ninja
flag lines carry `/W4 /WX`, both focused tests passed in 2.57 seconds, and
direct executions returned zero. Host/guest hashes matched (`21C131E2...D8C4D`
archive, `3D67CDD5...2B43C` MIL source, `45ABD421...F6D0E` MIL test); guest
MIL/internal executable hashes were `F7E4E8F7...626C1` and
`62A9AE08...F175`.
Checkpoint `f308c676` adds the shared 180-degree reversal primitive needed by
collapsed dashed contours. When WPF join semantics are requested, managed
`StrokeJoinGeometry` and the C++ backend emit WPF's half-width three-triangle
square for Miter/Bevel reversals and an eight-triangle incoming semicircle for
Round. Native MIL one-axis sharp
rectangles retain their four canonical points; one-axis ellipses retain four
ordered collapsed quarter traversals and force Round smooth joins. Both stay
inside the typed semantic stroke resource, so DirectX, WebGPU, managed, and
native CPU geometry consume the same dash phase, DashCap, seam, affine, and
join rules without readback or host-specific fallback. Live Windows
PresentationCore oracles lock down all four DashCap bounds, the `2.0` boundary
versus `2.01` initial-gap transition, and Miter/Bevel/Round reversal outlines.
Apple passes 10/10 native CTests and 3,883/3,883 managed tests. The 96-polyline
Metal differential remains byte exact (`C67040E2A28F2507` on both sides,
3,408 vertices and 5,112 indices); the dash differential retains matching
31,840/47,760 vertex/index counts and its existing one-channel edge budget.
The immutable archive SHA-256 is
`DD7E6B9D66305527E0F20F3445619F393943B00BEAABD4FEA88CD8450526491A`.
Windows ARM64 MSVC `19.44.35228.0` rebuilt 178 steps with 161 `/W4 /WX`
Ninja flag lines; both focused CTests passed in 2.98 seconds and direct
executions returned zero. Host/guest source hashes matched
(`992C8391...0BD0` stroke header, `7A2FCB07...30A` MIL,
`02062853...CCF` internal test, `2C2DAFF5...DEA9` MIL test,
`86CA4F83...D94A` managed geometry, and `9B378FDC...5CD6` managed tests).
Guest MIL/internal executable hashes were `A5272CE9...FAAC` and
`30295F83...D0D6`.

Checkpoint `2f55b1ba` makes that qualification explicit with the typed
`WpfJoinSemantics` policy. The original managed join APIs and native polyline
flags retain standard renderer behavior; explicit managed WPF APIs and every
MIL-created semantic stroke opt in to both clipped-miter and 180-degree
reversal geometry. Native validation rejects this semantic flag with hairline
or fixed-device strokes, so an incompatible forced combination fails closed.
The pinned Svg.Skia W3C gate is restored to its reviewed inventory: native
530/533 with three skips and ProGPU 486/533 with 44 reviewed differences and
three skips. The three previously new differences (`animate-elem-35-t`,
`painting-stroke-07-t`, and `shapes-polyline-02-t`) pass individually. Apple
passes 10/10 native CTests and 3,885/3,885 managed tests. The generic
96-polyline differential is again byte exact with 3,360 vertices, 5,040
indices, and identical `DE73D991697DAB3F` hashes. Dash differential topology
matches at 31,776 vertices and 47,664 indices with zero pixels outside the
one-channel edge tolerance (native `34DBC0EA94EF5BDB`, managed
`D09D785B5B327753`). The immutable source archive SHA-256 is
`4420B6E1D842FDD4F2C9101FC7C438773FB53707A11060F2DD5A6F17EF8867D6`.
Its exact sources rebuilt 257 steps on Windows ARM64 with MSVC
`19.44.35228.0`; all 10 CTests passed in 29.51 seconds and both MIL/internal
executables returned zero directly. Host/guest hashes matched for all six
changed source and test files. Guest MIL/internal executable SHA-256 values
were `3082D4214B1B6147A8BD40B2D6B9A56D39A2B3FE929AADF071043A4E42DD56CC`
and `0328FBF9528582E54D7E90F4051290ACD1C7F419709DAE16BA3A0D87EE4CE872`.

Checkpoint `0f72b5f1` closes the fully collapsed sharp-rectangle dash case.
The MIL compiler reuses the typed degenerate-cap stroke lane, including its
finite dash normalization and initial visible/gap selection, and forces the
Round/Round caps required by WPF for a wholly degenerate closed figure. It
therefore emits one backend-independent point disk or an exact no-op without
CPU readback, a special renderer, or host inspection; rounded degenerate
rectangle dashes remain fail closed. A live Windows 11 PresentationCore raster
oracle with thickness 8 and dash array `[1,1]` returned the same 8-by-8 disk
(60 covered pixels, alpha sum 12,452) for every LineJoin and DashCap at offset
`1.0`, and no covered pixels at `1.01`. Apple passes all 10 native CTests. The
immutable source archive SHA-256 is
`5BE5A14AA65021CA1D1273623169F766DBA834E968C9BEFF9A37BF0D96FBFFE3`.
Its exact sources rebuilt all 257 steps under Windows ARM64 MSVC
`19.44.35228.0`; all 10 CTests passed in 24.33 seconds and the focused
MIL/internal executables returned zero directly. Host/guest source hashes
matched at `D89307B4A78DB4BE457647F25C5C9DD1BC1305D3BE9535E444F1A0C693C3F90D`
and `D756F7E0138D44FAF012D34FF704A4A0EFCD6EAA03EF9AADDF8924C0BFC5C5AA`.
Guest MIL/internal executable SHA-256 values were
`7E907A8ADD470AEFA5904EB51FCCD697C648992BAE37E94283666E7A27FC07D4`
and `72633B0DB0A4B5A1908F6EB92AA8C0D469A3DB197A5EE09923B4712C60E7C1F3`.

Checkpoint `35edc9c6` closes dashed degenerate rounded rectangles when both
radii are positive. The MIL compiler independently clamps the radii, builds
WpfGfx's exact 17 float-point alternating cubic/line contour with
`ARC_AS_BEZIER`, and sends it through the existing typed curve-dash compiler.
Vertical, horizontal, asymmetric-radius, and fully collapsed records therefore
share the same dash phase, DashCap, smooth joins, retained cubic spans, affine
state, and backend execution as other curved MIL strokes. The point case
naturally reduces to the qualified Round/Round disk or no-op. A live Windows
PresentationCore oracle covered six uniform/asymmetric vertical, horizontal,
and point profiles across every DashCap and seven offsets; coverage locks the
phase-dependent bounds, alpha totals, and pixel hashes. Apple passes all 10
native CTests. The immutable source archive SHA-256 is
`C9C4FD6BB74BF15EAB6CBD03408C36F23DF945C205B3A6FE038CE4520F62720D`.
Its exact sources rebuilt all 257 steps under Windows ARM64 MSVC
`19.44.35228.0`; all 10 CTests passed in 24.07 seconds and both focused
executables returned zero. Host/guest source hashes matched at
`667394D8B2BF70C10C14B9695144F4066EC6680A41F6B2B64E1C334EBD2AC2C0`
and `DAC859981EF978FCCDC1C7CEEF6E382F611DA2B23A0E01BF37E535B35AB89549`.
Guest MIL/internal executable SHA-256 values were
`DE5C145CA0529B82B292B43E558509476AE62C95C63819805115D0F77D0D37DD`
and `61ADE59E104E6D29FC4FDA04550FE2CFAE34C871455E3510E04ED07F606823C7`.

Checkpoint `649fe3a5` completes the remaining degenerate zero-radius
normalization. It follows WpfGfx `CShape::AddRoundedRectangle` directly: if
either radius is zero, the rounded record is a sharp rectangle before any
widening or dashing. Vertical and horizontal one-axis records therefore reuse
the four-point WPF semantic polyline and reversal joins; visible/gap point
records reuse the qualified Round/Round disk decision. Tests cover both
asymmetric orientations and point phases and retain invalid brush-handle
failure after removing the obsolete early unsupported result. Apple passes all
10 native CTests. The exact archive SHA-256 is
`E831663733B21EF2232F11F3225F27DDABDF1FF2198F6625DE157C4CD6C491BE`.
Against the fully qualified `35edc9c6` parent build, MSVC rebuilt the exact two
changed sources through the 7-step incremental graph; all 10 Windows ARM64
CTests passed in 7.53 seconds and both focused executables returned zero.
Host/guest source hashes matched at
`89D7E319A6E51F9AFBAA79DD21921D64645F7EF5B2F92C9BF1D1147801500858`
and `F16CB5EAA918C04A47BFB895A4682046D22BF195B56688A2428AA18746E7B63F`.
Guest MIL/internal executable SHA-256 values were
`F78174BC5DF1E31207F37BDD10AA56ED680EB965BA5E0B7F5A1107D97E666AED`
and `61ADE59E104E6D29FC4FDA04550FE2CFAE34C871455E3510E04ED07F606823C7`.
The exact `18e72815` sources also rebuilt the changed library and test target
under Windows ARM64 MSVC 19.44 with `/W4 /WX`; the focused native MIL test
passed in 1.67 seconds. The first Windows pass caught and removed one recursive
lambda depth-name shadow that Apple Clang does not diagnose, so the strict
cross-compiler lane remains part of this bounds checkpoint rather than a later
cleanup.

The follow-up exact `022a44cc` clip/state checkpoint rebuilt the changed native
MIL library and test executable in the Windows 11 ARM64 Parallels guest with
MSVC 19.44 and the existing `/W4 /WX` configuration; the focused test passed
in 1.70 seconds. Guest SHA-256 values matched the host at
`848F81F686BFDF674496E49F19B7F07ECD7256E20C46A02FB0DDB61A8A4F5E95`
for `progpu_native_mil.cpp` and
`2BA928A65CE70E9907A3DAB7BAC41805E6EA5AFB038E97B1263C7102D1F8C14C`
for the test source.

Per-child clip-distribution hardening at exact `b83c1b5f` was then rebuilt in
the same Windows MSVC `/W4 /WX` lane and passed the focused test in 1.12
seconds. Guest hashes again matched the host:
`3737C2B5604E0C707AED7459CE874A5943E200DE7D4BBEDC49D7FAE210DA03DD`
for `progpu_native_mil.cpp` and
`6F60A9C752F945EA5F45162776EA1CC77657CDE8174B75FDB7C61DCFB046BCCA`
for the test source.

Exact line-stroke checkpoint `f5c3245d` also rebuilt cleanly in that Windows
MSVC `/W4 /WX` lane; the focused native MIL test passed in 2.20 seconds. Guest
SHA-256 values matched the host at
`B22CCDF1FA93964896E01D2FA1B666BC911AC332AF1E25707276D83A7185B752`
for `progpu_native_mil.cpp` and
`686408284DC0146C6F3EE60BAA431DDA379A0700460FC2092E5ABD78AF72A803`
for the test source.

Positive rectangle/rounded-rectangle/ellipse stroke checkpoint `81b5e0c4`
rebuilt cleanly in the same Windows MSVC `/W4 /WX` lane and passed the focused
native MIL test in 2.53 seconds. Exact guest hashes again matched the host:
`4CD38BAC4ACB4DE61EC2E2459265AC1056373E6DA34AA6ED6C3DE14C83604595`
for `progpu_native_mil.cpp` and
`B094837862908657F648F67C5819171D2EACC1BF7FF19B8BF0BB64C1255E32C7`
for the test source.

Degenerate fixed-shape checkpoint `dbec7d09` passed the same Windows MSVC
`/W4 /WX` build and focused native test in 1.69 seconds. Exact guest hashes
matched the host at
`B47CA86FCCD04BE70529E2DC1355688432EB3A9C8685628BC3E2726CCDC363B5`
for `progpu_native_mil.cpp` and
`CD79B51484FCFA6047C84BFD3EB95FD78D12918DBA6E7E084762E6B0E3810D7A`
for the test source.

Empty-solid-DashStyle checkpoint `cc629948` passed the Windows MSVC `/W4 /WX`
build and focused native test in 1.93 seconds. Exact guest hashes matched the
host at
`680D3CBE0D571BEC95F1B8CD796D2C2FAB1380D392B39AA21166C68CE0D1FBF7`
for `progpu_native_mil.cpp` and
`BD6A99C80293C154F7114E4FD867C2B0B663BD72171EAA0C88CC24018493FECC`
for the test source.

`NativeMilRenderDataBuilder.DrawImage(...)` now exposes the same canonical
static packet to typed hosts. It validates finite nonnegative destination
bounds and a nonzero image handle, writes the required zero padding, and is
covered byte-for-byte by the managed native interop suite. This keeps WPF,
WinUI, and Avalonia adapters from hand-encoding the protocol.

The canonical BitmapSource command still contains a process-local
`IWICBitmapSource*`, so the portable decoder deliberately does not accept that
packet or BitmapInvalidate as proof of portable pixels. The later canonical
MediaPlayer lane binds packed live frames as typed same-device external images.
D3DImage/shared-surface synchronization, planar/HDR media, and incremental
bitmap invalidation remain separate typed interop work; none are approximated
by pointer scraping or stale copied bytes.

The Windows gate captures the expected forced-compute rejection with
`System.Diagnostics.Process` rather than a PowerShell native-error pipeline.
This preserves the same typed adapter-incompatibility and unsafe-WebGPU checks
under both Windows PowerShell 5 and PowerShell 7: stderr is evidence to inspect,
not a host-level terminating error before the exit code and message can be
validated.

The exact clean `a7dcd8de` checkpoint passed the complete Apple Silicon native
gate, including generated-protocol drift, all local CTests and export checks,
live Metal execution, the managed/native differential matrix, and both
Microsoft DirectX sample oracles. Clean detached Windows qualification then
rebuilt the modified MIL compiler, both exports, and the test executable with
ARM64 MSVC under `/W4 /WX`; all 11 native/Dawn CTests passed. The live Parallels
D3D12 smoke lane passed fastest raster selection, forced raster, forced
intrinsic SIMD, the bounded scalar oracle, and the expected typed rejection of
forced compute on the unqualified adapter. SIMD retained-glyph output was exact
against managed output with hash `5B6EF4F70536C862`. The remaining retained
image, mask, clip, effect, blend, mixed-semantic, text, and stress profiles all
met their declared exact or bounded contracts.

The Microsoft D3D12HelloTriangle and D3D12HelloTexture contracts produced the
same SHA-256 on Windows D3D12 and macOS Metal:
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and
`591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.
Qualified Windows binaries are
`CD33CEEE182F2A77403B96F4D23DF7FBB1A61AEFAD66C927D3282C4A461236C3`
for `progpu_native.dll`,
`50362916F0026C1B016A2496F89547B1814C4F3BCA2D414CCC6B39B2E12B84F6`
for `progpu_native_dawn.dll`, and
`9F73E41536B3BD96A0A44692EA65888C9DE004B19FBF5DE90489768667FBBDBC`
for the pinned wgpu-native runtime DLL.

GlyphRun implementation `c8efc666`, transport optimization `6c762f2b`, focused
package gate `b21fd324`, and fixture correction `fa8d6a33` next added canonical
`MilCmdGlyphRunCreate` command `0x3a`, retained `GlyphRunDrawing` resource type
`88`/command `0x88`, and nested `DrawGlyphRun` command `0x49`. The canonical
glyph-create command retains its 76-byte fixed command view followed by glyph
indices, advances, optional X/Y offsets, and DWORD padding. Its embedded
`IDWriteFont*` field is always zero outside the source process. Portable hosts
instead bind copied SFNT/TTC bytes, face index, and bold/italic simulations to
the glyph-run handle through the typed
`progpu_native_mil_channel_set_glyph_run_font_sfnt` sideband. Identical font
payloads share one retained native byte buffer across glyph-run resources.

Scene compilation decodes TrueType outlines through `progpu_native_text`,
caches the resulting semantic outline resource by glyph-run handle and raster
size, and emits positioned glyphs through the shared semantic text command
consumed by wgpu-native and Dawn. Direct render-data glyph commands and
retained GlyphRunDrawing resources therefore share the same font decode,
positioning, style, transform, and renderer path. Missing font bindings,
invalid fonts/faces/styles, wrong resource types, deletion of referenced
glyphs/brushes, and malformed packets fail closed or roll back transactionally.

All ten local native CTests passed, the managed canonical-builder filter passed
10/10, and the project-reference package consumer built with zero warnings.
Strict Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`; all 11
native/Dawn CTests passed in the Parallels VM. The focused 14-command,
six-channel-resource glyph scene compiled direct and retained glyph draws
through both MIL exports and rendered on live D3D12 with three semantic
resources, one batched draw, 13,312 coverage-staging bytes, a valid submission,
nonblack retained readback, and 16,384 direct-render pixels. Exact qualified
SHA-256 values were
`f75a6e979f52d5a606294cb1698c48efcb6a96b78e961f23820495af1697d510`
for `progpu_native.dll` and
`e95c7107f76ef1bb221b0784919fe5bd8f72ac8c004ef016db4794b8e7a5d399`
for `progpu_native_dawn.dll`.

This checkpoint intentionally supports solid foreground brushes, horizontal
TrueType `glyf` outlines, logical-pixel raster sizing, and static glyph state.
Sideways text, gradient/tile text brushes, CFF/CFF2 and variable/color/bitmap
glyphs, target-DPI-aware raster selection, text decorations, and incremental
font-resource registration remain explicit parity work.

DrawingImage implementation `6071925d` then added canonical resource type
`59` and command `0x71` with its exact 12-byte command view (16 bytes framed).
The canonical update retains only the referenced Drawing handle. Portable
hosts bind drawing shapes that cannot yet be derived natively through
`progpu_native_mil_channel_set_drawing_image_bounds`; unlike WPF's original
in-process resource graph, those bounds are not present in the packet. The
native compiler derives the exact live bounds of positive-area, fill-only
fixed-geometry `GeometryDrawing` leaves and fillable `PathGeometry` leaves,
including independently transformed curves, plus single-child geometry-group
chains and default-state drawing-group trees with axis-preserving transforms
without that sideband. Missing or nonfinite bounds for every other shape fail
closed, while a null Drawing remains a canonical no-op.

Scene compilation recursively reuses retained GeometryDrawing,
GlyphRunDrawing, ImageDrawing, and DrawingGroup lowering. It scales and
translates source bounds into the destination, intersects the destination with
the active clip, and protects both DrawingImage-to-Drawing and
ImageDrawing-to-DrawingImage dependencies. Axis-preserving destination clips
become scissors; rotated or sheared clips become exact four-edge semantic
vector masks rather than broadened rectangle bounds. Cycles and invalid image
source types fail closed transactionally.

All ten local native CTests passed, the canonical managed packet test passed,
and the project-reference package consumer built with zero warnings. Strict
Windows ARM64 MSVC rebuilt both modules under `/W4 /WX`; all 11 native/Dawn
CTests passed in the Parallels VM. The focused 19-command,
eight-channel-resource DrawingImage scene compiled through both MIL exports
and rendered on live D3D12 with four semantic resources, one batched draw,
zero coverage-staging bytes, a valid retained readback, and 16,384
direct-render pixels. Exact qualified SHA-256 values were
`85ef5bb9c18505b97f11bf40302a8d93c50d3bd13b7afbd412fac55b7ba67cf1`
for `progpu_native.dll` and
`bae571f2a8d3cf707c92919613c8a5bece2f6e462b19c9bcd6167cd0ea66bc2c`
for `progpu_native_dawn.dll`. DrawingImage used as an ImageBrush source,
animated destination rectangles, incremental source-bounds updates, and
effects/cache state remain explicit follow-up work.

Bitmap-scaling checkpoint `ebe966b6` next made DrawingGroup's canonical
`bitmapScalingMode` field executable for nested retained images, including
bitmap-backed ImageDrawing reached through DrawingImage or another group.
Unspecified inherits the parent scope, LowQuality/Linear selects shared linear
sampling, and NearestNeighbor selects shared nearest sampling. That checkpoint
initially mapped HighQuality/Fant to ProGPU's Mitchell-Netravali cubic sampler;
the later Fant checkpoint corrects the mapping because WPF uses Fant as an
area prefilter rather than a bicubic reconstruction kernel. The state is
host-neutral and is consumed identically by wgpu-native and Dawn.

The native fixture verifies nearest and Fant payload selection, and all ten
local CTests passed. Strict Windows ARM64 MSVC rebuilt both modules under `/W4
/WX`; all 11 native/Dawn CTests passed. The existing DrawingImage package
scene then passed both exports and live D3D12 retained/direct readback. Current
qualified SHA-256 values are
`812312ae4d91c30a363f801985d2f881a6aa528709331f0985279756a5337790`
for `progpu_native.dll` and
`8cf312ffadac52d7109239de3fee4f25e34358bcc963e6f33965799fe3d9f607`
for `progpu_native_dawn.dll`.

Static-guideline implementation `d4112930`, package gate `59851d8c`, and
validator correction `dab52e58` next added the exact uniform-offset subset of
WPF `GuidelineSet`. Canonical type `92` and command `0x8c` use the WPF
20-byte fixed view followed by X and Y double arrays. Static arrays are sorted
as WPF does. A DrawingGroup's handle at offset 36 selects the set for its
children; no handle inherits the parent scope and an explicitly empty set
disables snapping for that scope.

WPF only constructs an active snapping frame under a finite scale/translate
transform. Each static coordinate is transformed to device space with WPF's
float evaluation, and its device offset is computed with the native
`CFloatFPU::OffsetToRounded` tie rule (half coordinates choose the numerically
larger integer). One coordinate per axis is therefore an exact uniform
translation for every semantic draw family. ProGPU stores those device-space
coordinates in resource kind 17, references them from the unchanged 64-byte
semantic state using flag bit 2, and resolves the DPI-dependent offset in the
shared state cursor used by wgpu-native and Dawn. A rotated/sheared transform
pushes WPF's equivalent empty frame. Dynamic pairs and multiple coordinates
require piecewise geometry deformation and deliberately fail closed in this
first slice.

The public package gate builds a focused 19-command, eight-channel-resource
scene, compiles it through both MIL exports, and requires live retained
readback. Integration testing exposed and fixed a registry-boundary defect:
the typed kind-17 validator existed, but the known-resource range still ended
at kind 16. A public scene-validation regression now covers a guideline
resource referenced by state. All ten local native CTests and the focused
managed builder/producer suites passed. Strict Windows ARM64 MSVC rebuilt both
exports under `/W4 /WX`; all 11 native/Dawn CTests passed. The fresh app-local
DLLs compiled the focused scene through both MIL channels and rendered on live
D3D12 with five semantic resources, one batched draw, zero coverage-staging
bytes, a valid nonblack retained readback, and 16,384 direct-render pixels.
Qualified SHA-256 values are
`9a76e7a16eb989cad3932e4d24e9e3ca1247069d8bd14114120cf073e038a270`
for `progpu_native.dll` and
`4a9e55ff26301d50138c7f02cd8be02645541ea29dd37081e2f787d2cc69c8b7`
for `progpu_native_dawn.dll`.

Solid-opacity-mask implementation `f9f49b86` and package gate `9ecc8a9b`
then added the exact spatially uniform subset of canonical DrawingGroup
opacity masks. A transform-free static SolidColorBrush contributes
`brush.Opacity * brush.Color.A` to the inherited group opacity, so applying
that value before recursively lowering the group's children is exactly
equivalent to WPF's alpha-mask composition for every semantic draw family.
The mask remains a retained canonical brush dependency and updates through
the normal SolidColorBrush command; no backend-specific mask material or
semantic ABI expansion is required.

At this checkpoint the decoder distinguished unsupported and invalid input:
known linear or radial gradient masks failed with `unsupported_command`, while
a missing or wrongly typed brush handle failed with `invalid_handle`. The
later typed DrawingGroup-bounds sideband closes the static linear/radial subset
by mapping those masks through the reusable semantic GPU brush-mask resource.
Tile, animated, and other unsupported spatial brush families remain fail
closed. Native tests cover the initial solid alpha product, retained brush and
DoubleResource updates, missing-bound rejection, exact transformed layer
bounds, and gradient mask resource mapping.

The public package consumer added `--mil-drawing-group-only` to the JIT,
NativeAOT, build, release, and package-verification lanes. All ten local MIL
CTests passed; strict Windows ARM64 MSVC rebuilt both exports under `/W4 /WX`
and all 11 native/Dawn CTests passed. Fresh app-local DLLs compiled the
focused scene through both MIL exports and rendered on live D3D12 with four
semantic resources, one batched draw, zero coverage-staging bytes, a valid
nonblack retained readback, and 16,384 direct-render pixels. Qualified
SHA-256 values are
`3b5aa2a63c1335877e8ca49ecb37abcc705be1a9940a77fbf5f19150219f69c1`
for `progpu_native.dll` and
`341e01504aeb9380a33676704f1712cf25ee433f80554344bb080a3e0514be93`
for `progpu_native_dawn.dll`.

The qualification push also corrected both checked-in exported-symbol
allowlists to include the public DrawingImage bounds sideband entry point.
Linux ARM64 had built successfully and passed all 15 native suites before its
symbol-surface guard exposed that earlier packaging omission.

Aliased-edge implementation `55bf8628` and package gate `3f5f72dc` then
enabled canonical DrawingGroup `EdgeMode.Aliased` for vector content. The
group scope inherits WPF's unspecified value and makes an explicit aliased
value sticky for descendants. Shared semantic analytic primitives and
polylines receive their existing edge-aliased flags, while vector-path fills,
strokes, caps, and joins select the one-sample raster path instead of the
eight-sample antialiasing grid. Image and glyph sampling are intentionally
unchanged, and clip-mask geometry remains exact rather than being broadened
or converted into an aliased bounds clip.

The canonical parser continues to validate the existing DrawingGroup edge
field and rejects values outside Unspecified/Aliased transactionally. Native
tests assert the emitted primitive flag, while the public
`--mil-drawing-group-only` scene now carries Aliased through both native MIL
exports. All eight configured local native suites passed. Strict Windows
ARM64 MSVC rebuilt `progpu_native.dll` and `progpu_native_dawn.dll`; all 11
native/Dawn CTests passed. After the fresh DLLs and `wgpu_native.dll` were
copied beside the project-reference consumer, both exports compiled the
focused scene and live D3D12 retained/direct rendering completed with four
semantic resources, one draw, zero coverage-staging bytes, and 16,384 direct
pixels.

Vector-only ClearType-hint implementation `db057403` and package gate
`4af0b1c5` next consume the final canonical DrawingGroup render-option field
without overstating text parity. WPF's `CDrawingContext::PushRenderOptions`
uses `ClearTypeHint.Enabled` to call `SetClearTypeHint(true)`, and the software
render target consults that state only while deciding whether `DrawGlyphRun`
may use ClearType on an alpha surface. It has no vector or image effect.

ProGPU therefore carries Enabled as inherited scope state and accepts exact
non-text subtrees. Any nonempty direct or retained glyph run reached under
that state returns `unsupported_command` before a semantic scene is
published, because the current shared native glyph rasterizer is grayscale
and silently drawing it would be false parity. Source validation continues
to accept only Auto/Enabled. Native tests cover an accepted vector subtree
and a rejected real SFNT-backed GlyphRunDrawing subtree.

All eight configured local native suites, the managed canonical builder
contract, and the zero-warning project-reference package build passed.
Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local, both
MIL exports compiled the hinted vector scene and live D3D12 retained/direct
rendering completed with four semantic resources, one draw, zero
coverage-staging bytes, and 16,384 direct pixels. True ClearType text remains
an explicit shared glyph-rasterization/backend follow-up.

Canonical visual render-options implementation `7db3ddb9` and package gate
`0e1b4029` next add `MilCmdVisualSetRenderOptions` (`0x21`) to the retained
visual protocol. The decoder consumes the canonical 36-byte payload and the
WPF flag mask for bitmap scaling, edge mode, compositing mode, ClearType hint,
text rendering mode, and text hinting mode. Bitmap scaling, aliased edges, and
the vector-only ClearType subset are retained per visual and inherited through
child visuals into nested DrawingGroup/ImageDrawing content. Default values
remain no-op/inherit, matching WPF's `PushRenderOptions` behavior.

Unknown flags and invalid enums are malformed input. Known compositing,
TextRenderingMode, and TextHintingMode flags return `unsupported_command`
transactionally, and a visual ClearType hint reached by real glyph content
also fails closed until the shared text rasterizer provides true ClearType.
Native tests cover root-to-child inheritance, nearest-neighbor image sampling,
aliased vector output, rejected glyph content, and transactional rejection.
All eight configured local native suites and the managed canonical packet test
passed. Strict Windows ARM64 MSVC rebuilt both exports; all 11 native/Dawn
CTests passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local,
the visual-to-DrawingGroup inheritance scene compiled through both exports and
live D3D12 retained/direct rendering completed with four semantic resources,
one draw, zero coverage-staging bytes, and 16,384 direct pixels.

Native text render-option implementation `83f9febd` next completes the text
fields already present in canonical `MilCmdVisualSetRenderOptions` (`0x21`).
The C++ channel now validates and retains WPF `TextRenderingMode`
Auto/Aliased/Grayscale/ClearType and `TextHintingMode` Auto/Fixed/Animated,
then applies those inherited values only while compiling glyph content.
CompositingMode remains a known, transactional `unsupported_command`.

The glyph compiler maps WPF modes onto the existing shared semantic text
styles consumed by `Text.wgsl`: Aliased selects threshold coverage, Grayscale
selects gamma-corrected monochrome coverage, and ClearType selects the shared
RGB-shifted coverage path. `ClearTypeHint.Enabled` promotes an otherwise Auto
text-rendering scope to ClearType, matching the current managed ProGPU WPF
policy. Explicit text-rendering mode wins over that hint.

Auto and Fixed hinting use the same physical-placement policy as the managed
native-scene compiler. Axis-preserving glyph runs at raster sizes up to 24 px
snap Y to an integer pixel, keep X on one of four quarter-pixel phases, and
select a phase-specific retained outline. Larger glyphs snap both axes to
integers. Animated text and rotated, sheared, or reflected placement remain
unsnapped. Each unique glyph/raster-size resource retains four outline records
that share decoded SFNT path segments, so phase selection does not duplicate
outline decoding or managed objects.

The local native build and all ten configured CTests pass, including retained
SFNT glyph tests for grayscale, ClearType hint promotion, explicit Aliased plus
Fixed snapping, and Animated unsnapped placement. The canonical managed packet
test also passes. Package checkpoint `c7139459` adds
`--mil-text-render-options-only` to source, package, release, and NativeAOT
lanes. Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn
CTests passed. With fresh native, Dawn, and wgpu-native DLLs copied app-local,
the focused ClearType/Fixed scene compiled through both exports and live D3D12
rendered three semantic resources, one draw, 53,248 coverage-staging bytes,
and 16,384 direct pixels. Qualified hashes are
`4703ddeaebf3ddea3ce7f503e935093e79cabb5bac5c3d26ff2890444f011fa2`
for `progpu_native.dll` and
`9de7c391543e027410523b75dc8a394255ca1045e2359f9049f27ba387939a15`
for `progpu_native_dawn.dll`.

This establishes parity with ProGPU's current managed WebGPU/DirectX text
modes, not pixel identity with WPF's DirectWrite glyph hinting or system
display parameters; those remain an explicit platform-text parity gate.
Canonical DrawingGroup has no text-rendering or text-hinting fields, so
object-level DrawingGroup text options continue to fail closed.

Retained Visual clip implementation `f134b690` next adds canonical
`MilCmdVisualSetClip` (`0x1f`) and `MilCmdVisualSetScrollableAreaClip`
(`0x28`). The channel retains the geometry dependency and optional scroll
rectangle, protects live dependencies from deletion, validates finite
nonnegative rectangles, and keeps batch updates transactional.

The scene compiler follows WPF's ordering for the exact rectangle subset.
Scrollable-area clips are transformed by the parent scope before the Visual's
offset and transform, snapped inward with ceiling left/top and floor
right/bottom, and intersected with the inherited scissor. A Visual carrying a
scroll clip also snaps its offset through parent device space before mapping it
back into local space. The regular Visual clip is applied after the Visual
offset and transform. Plain RectangleGeometry with an axis-preserving effective
transform becomes a shared semantic scissor; rounded, rotated/sheared, ellipse,
and arbitrary path Visual clips return `unsupported_command` instead of being
broadened to bounds.

Package checkpoint `909d6ae8` adds `--mil-visual-clip-only` to JIT,
NativeAOT, package verification, build, and release lanes. All ten local native
CTests, the canonical managed packet test, and the zero-warning package-consumer
build pass. Strict Windows ARM64 MSVC rebuilt both exports and all 11
native/Dawn CTests passed. Fresh app-local native, Dawn, and wgpu-native DLLs
compiled the focused clip scene through both MIL exports and rendered on live
D3D12 with three semantic resources, one draw, zero coverage-staging bytes, a
nonblack retained readback, and 16,384 direct-render pixels. Qualified binaries
from 2026-08-25 17:11 are 1,960,448 bytes with SHA-256
`0261b5eda34a53db96526e7b27709b052619da561d468d5b131945ed475d54d8`
for `progpu_native.dll`, and 1,999,360 bytes with SHA-256
`9068358ec8f291c261943eef95849c1eac78397bb0446b83d395e9ae5c330116`
for `progpu_native_dawn.dll`.

Exact rounded/path Visual clips remain a reusable ProGPU vector-mask task.
Layout clips are a source-built WPF producer concern and are not claimed by
these canonical Visual commands.

Static solid Visual opacity-mask implementation `070bed14` next adds canonical
`MilCmdVisualSetAlphaMask` (`0x23`). The retained Visual holds and protects its
Brush dependency, while a shared native uniform-mask resolver now serves both
Visual and DrawingGroup scopes. For a transform-free, nonanimated
SolidColorBrush, `Brush.Opacity * Color.A` composes into inherited Visual
opacity for every shared semantic draw family. Retained brush updates change
the next compiled scene generation without rebuilding managed objects.

Missing or wrongly typed handles are invalid; known gradient/spatial masks
return `unsupported_command`; clear-then-delete succeeds; and deletion while
referenced fails transactionally. Package checkpoint `cfe13009` adds
`--mil-visual-opacity-mask-only` to JIT, NativeAOT, package verification,
build, and release lanes. All ten local native CTests, the canonical managed
packet test, two focused typed producer tests, and the zero-warning consumer
build pass.

Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. Fresh app-local DLLs compiled the focused mask through both MIL exports
and live D3D12 rendered three semantic resources, one draw, zero
coverage-staging bytes, a nonblack retained readback, and 16,384 direct pixels.
Qualified binaries from 2026-08-25 17:25 are 1,961,472 bytes with SHA-256
`a76fe43b7e7a26b6ccaab71e80261e2704f0308c03c3e3a35abc4d80ff66038c`
for `progpu_native.dll`, and 1,999,872 bytes with SHA-256
`ac396e3973a2bc5a851925dff0d97f3cf43ebaaaa7b332df797cfbc3946341cd`
for `progpu_native_dawn.dll`. Gradient, tile, transformed, animated, and other
spatial Visual masks remain part of the reusable ProGPU mask-target work.

Static Visual guideline implementation `31cd23ca` next adds canonical
`MilCmdVisualSetGuidelineCollection` (`0x27`), including its packed UInt16
counts and trailing float coordinates. The native channel retains sorted
coordinates, validates padding, payload size, and finite float values, and
keeps multi-guide packets transactionally valid while returning
`unsupported_command` at scene compilation until piecewise deformation exists.

The exact zero/one guide per axis subset reuses the shared semantic GuidelineSet
resource already consumed by WebGPU/Dawn and DirectX. Scale/translate mapping
preserves WPF's float conversion and target-space coordinate behavior;
rotated/sheared Visuals push an empty snapping frame. Unlike DrawingGroup, each
WPF Visual resets the parent guideline frame before applying its own. Native
tests prove both mapped values and that a child Visual with no guidelines does
not inherit the root's resource.

Package checkpoint `50710315` adds `--mil-visual-guideline-only` to JIT,
NativeAOT, package verification, build, and release lanes. All ten local native
CTests, the canonical managed packet test, two focused typed producer tests,
and the zero-warning consumer build pass. Strict Windows ARM64 MSVC rebuilt
both exports and all 11 native/Dawn CTests passed. Fresh app-local DLLs compiled
the focused scene through both MIL exports and live D3D12 rendered four
semantic resources, one draw, zero coverage-staging bytes, a nonblack retained
readback, and 16,384 direct pixels. Qualified binaries from 2026-08-25 17:39
are 1,964,032 bytes with SHA-256
`36406b7138010c2c3b47e136a32efa62f07e148027640b68edafc0b67ea07318`
for `progpu_native.dll`, and 2,002,432 bytes with SHA-256
`deaa21c42b156f0aa5f78bcb10593bfc53c650da69dab320cb625fdcd8a585be`
for `progpu_native_dawn.dll`. Multiple guides per axis remain the same explicit
piecewise-deformation gap as canonical GuidelineSet.

Retained Visual effect implementation `93929c07` next adds canonical
`MilCmdVisualSetEffect` (`0x1d`), `MilCmdBlurEffect` (`0x6e`), and
`MilCmdDropShadowEffect` (`0x6f`) with resource types 36 and 37. The channel
retains and protects the Visual's effect dependency, applies resource updates
on the next compiled generation, rejects deletion while referenced, and keeps
failed batches transactional. Managed builder checkpoint `7f02bd4a` also
carries the WPF blur kernel and rendering bias through the neutral portable
effect DTO rather than reconstructing effect state in a host bridge.

The exact Gaussian mapping follows WPF milcore rather than treating WPF's
logical radius as WebGPU sigma. WPF truncates Radius to an integer, scales it
by the smaller orthogonal transform row length, truncates and caps the physical
kernel radius at 100, then uses `radius / 3` as standard deviation. ProGPU feeds
that sigma into the existing shared semantic blur pass. DropShadow computes
the WPF local offset `(depth * cos(direction), -depth * sin(direction))`, maps
it through the normalized orthogonal transform, and runs the existing shared
blur, shadow-composite, and source-composite passes. Both wgpu-native/Dawn and
DirectX therefore consume the same retained scene and effect descriptors.

This initial checkpoint was intentionally narrower than general WPF Effect parity.
Gaussian BlurEffect and DropShadowEffect with an orthogonal effective transform
are accepted; the later animated-value checkpoint also resolves every canonical
effect animation handle from typed DoubleResource/ColorResource state. Box blur,
shear, and composition with an active Visual clip or an uncached spatial
opacity mask return `unsupported_command`. Non-unit opacity also remains
unsupported for uncached Visuals. A cached Visual is the bounded exception:
its retained page is already an isolated input, so uniform opacity and one
typed linear/radial spatial mask can be applied while drawing that page into
the outer effect layer. WPF applies Visual effect after
opacity-mask/opacity and before the final clip; the current semantic layer does
not yet represent separate inflated-source and final-composite clip regions,
so accepting the remaining combinations would silently change ordering. The
native effect used a conservative full-target isolated layer. The bounded-
effect checkpoint below replaces that allocation for typed LibreWPF Visuals
while retaining full-target compatibility for native callers that have not
supplied bounds.

Native regressions cover blur sigma, drop-shadow direction/color/opacity,
dependency lifetime, Box rejection, and modifier-combination rejection. All
ten local native CTests, the canonical managed packet test, the focused typed
LibreWPF producer tests, and the zero-warning project-reference consumer build
passed. Package checkpoint `6702b9b7` adds `--mil-visual-effect-only` to JIT,
NativeAOT, package verification, build, and release lanes.

Strict Windows ARM64 MSVC rebuilt both exports and all 11 native/Dawn CTests
passed. Export checks found `progpu_native_mil_channel_create` and
`progpu_native_mil_channel_build_scene` in both DLLs. Fresh app-local native,
Dawn, and wgpu-native DLLs compiled the retained DropShadow scene through both
MIL exports and live D3D12 rendered four semantic resources, two draws, zero
coverage-staging bytes, a nonblack retained readback, and 16,384 direct pixels.
Qualified binaries from 2026-08-25 18:05 are 1,973,248 bytes with SHA-256
`eb55945dff526f5535fd7c10795e2e0e91baea787aac6c165ab7cfea3fa4c4cf`
for `progpu_native.dll`, and 2,011,648 bytes with SHA-256
`2c9c1f5fc1ee4f41b9361280d53a32201e3b4215c3cd70a0c0cf68c130766eda`
for `progpu_native_dawn.dll`.

### BitmapCache execution design

Canonical cache support is the next retained Visual slice. WPF defines
`MilCmdVisualSetCacheMode` (`0x1e`) as a 12-byte command view containing the
Visual and CacheMode handles. `MilCmdBitmapCache` (`0x8d`) is a 28-byte command
view for resource type 94 and carries RenderAtScale, an optional animated
DoubleResource handle, SnapsToDevicePixels, and EnableClearType. The native
resource clamps the current scale to zero or greater; a scale sufficiently
close to zero draws no cached content.

WPF's `CMilVisualCache` evidence also fixes the execution model. Cache bounds
are local Visual content bounds. Realization dimensions are the ceiling of
those bounds multiplied by RenderAtScale and system DPI, capped by the backend
texture limit. Cache update renders only the Visual's content and descendants,
ignoring the root Visual's outer offset, transform, clip, opacity, mask, and
effect. Normal tree rendering then draws the cached texture through the outer
Visual transform; opacity is applied at that composite, and
SnapsToDevicePixels post-offsets the transformed cache origin by its fractional
device coordinates. WPF may reuse the cache as effect input only under its
explicit no-opacity/no-mask/no-inflation conditions.

The shared native representation therefore needs an owner-keyed retained
cache page, not the existing depth-indexed temporary layer slot. Its stable
identity is independent of command position and sibling updates; a content
revision invalidates raster content, while placement, opacity, clip, mask, or
other outer-state changes only rebuild the composite. Cache pages participate
in the existing 256 MiB bounded layer budget, carry explicit logical bounds,
raster scale, pixel-snap, and text-mode fields, and are released when their
owner disappears or the device is lost. Nested caches require independent
pages rather than sharing one slot at a materialized depth.

The first executable checkpoint now adds
`PROGPU_NATIVE_SCENE_LAYER_CACHE_CONTENT`. It uses the existing
`composite_revision` field as a nonzero stable owner identity and
`content_revision` as the nonzero pixel version, assigns up to 16 owners to a
separate persistent GPU-slot pool, and keys reuse independently of the whole
scene hash. The preflight rejects backdrop caches and duplicate owners, charges
cache and effect pages to the shared layer budget, and preserves the page
across an unrelated sibling update. Texture extent/generation or content
version changes force a redraw; disappearance, owner replacement, and device
teardown release or invalidate the page. Portable C++ validation, managed
builder validation, and the Dawn/Metal provider integration test cover the
contract. The typed Visual-bounds checkpoint now prevents the cache from
becoming a full-target allocation. Portable hosts bind source-built WPF
descendant bounds through
`progpu_native_mil_channel_set_visual_cache_bounds`; the compiler transforms
those local bounds into an explicit local raster page. Missing/nonfinite/empty
metadata fails closed.

The next executable checkpoint adds
`PROGPU_NATIVE_SCENE_LAYER_CACHE_LOCAL_SPACE` without changing the exact
64-byte layer record. For this flag, bounds are the zero-origin raster-page
extent and `reserved0` names a preceding canonical transform-only State
resource that places the cached quad in its parent. The target cursor allocates
that page independently of target placement, content states translate/scale
the Visual-local bounds into the page, and the shared WebGPU/DirectX executor
composites all four page corners through the typed affine state. The same
record therefore supports translation, scale, rotation, shear, parent-layer
localization, and non-unit RenderAtScale while retaining one owner-keyed page.

The canonical MIL checkpoint is now executable on that foundation. The C++
channel decodes the exact 12-byte `MILCMD_VISUAL_SETCACHEMODE` command and the
exact 28-byte `MILCMD_BITMAPCACHE` resource update for canonical resource type
94. A Visual retains its typed cache handle, a BitmapCache retains its optional
type-49 DoubleResource animation, and deletion of either live dependency fails
transactionally. Packet booleans must be canonical zero or one, non-finite
scale values fail closed, and animated scale is resolved from the live resource
on every scene compilation.

`NativeMilBatchBuilder` exposes the same contract through
`NativeMilResourceType.BitmapCache`, `NativeMilBitmapCache`,
`SetBitmapCache(...)`, and `SetVisualCacheMode(...)`. The WPF-neutral
`IPortableBitmapCacheSource`/`PortableBitmapCache` seam carries the current
scale, snapping, and ClearType values without referencing PresentationCore or
using reflection; source-built LibreWPF is responsible for publishing it.

For the currently executable local-space subset, the MIL compiler emits one
owner-keyed cached semantic layer around the Visual subtree. The stable owner
identity is derived from scene identity plus Visual handle; the pixel revision
walks the typed Visual, render-data, brush, pen, transform, geometry, drawing,
image, glyph, effect, guideline, nested-cache, and animation dependency graph.
Consequently an unrelated sibling update preserves the cache version, while a
brush/resource update inside the cached subtree changes it without managed
invalidation assistance. Exact resolved scale zero suppresses the cached
subtree. Exact source-built Visual descendant bounds are required through the
typed channel sideband and become a zero-origin page sized by RenderAtScale and
frame DPI; missing bounds fail closed instead of allocating the full render
target. Root offset, transform, and opacity are excluded from the pixel
revision and applied only by the composite, so changing placement reuses the
page. Positive finite static or animated RenderAtScale values resize and
rerasterize it, and exact resolved scale zero suppresses the subtree.
SnapsToDevicePixels transforms the exact local bounds through outer placement,
floors the resulting world-space left/top, and applies the fractional
correction only to the cached-page composite. EnableClearType controls the
cache raster scope: false suppresses requested subpixel glyph styles to
grayscale, while true permits the existing inherited or explicit ClearType
mode without forcing unrelated text into ClearType.
The cache-root boundary now follows `DrawCacheVisualTree` as well: root Visual
render options, clip, guidelines, opacity, transform, and offset are excluded
from retained pixels, while descendant Visual state remains raster state.
Exact root/ancestor rectangle clips and one static guideline per axis travel in
the typed local-cache composite State resource and are resolved only when the
page quad is drawn. Composite-only clip, guideline, placement, opacity, and
SnapsToDevicePixels changes therefore retain the page; RenderAtScale,
EnableClearType, bounds, and descendant content changes rerasterize it.
NearestNeighbor bitmap scaling now sets the additive
`PROGPU_NATIVE_SCENE_LAYER_CACHE_NEAREST` composite flag. Each retained page
owns linear and nearest texture bindings over the same view, so changing only
that cache-root sampling policy selects the exact nearest sampler without
rerasterizing or expanding the 64-byte layer record. The flag is valid only
with `CACHE_LOCAL_SPACE`; the C++, managed, and serialized-scene validators
reject every other use.

HighQuality/Fant bitmap scaling now uses the additive
`PROGPU_NATIVE_SCENE_LAYER_CACHE_FANT` composite flag and the canonical
`PROGPU_NATIVE_IMAGE_SAMPLING_FANT` value. WPF's native renderer enables a Fant
prefilter only when either source-axis footprint exceeds sqrt(2), then returns
to ordinary interpolation for reconstruction. The shared ProGPU shader keeps
that activation threshold and integrates the destination-pixel parallelogram
with a fixed stratified 4x4 area footprint; rotation and shear therefore
participate in the footprint instead of being reduced to one scalar scale. The
fixed footprint is a deterministic, bounded GPU approximation of WIC Fant
rather than a claim of byte-for-byte WIC output. ProGPU's explicit `CUBIC`
sampling value remains the separate Mitchell-Netravali API. The Fant flag is
local-cache-only and mutually exclusive with nearest; changing only
linear/nearest/Fant policy preserves the page content revision and skips its
content pass.

The cache-root spatial opacity-mask checkpoint reuses the existing
host-neutral `LAYER_MASK_BRUSH` resource and shared WebGPU/DirectX compositor.
A canonical linear or radial gradient brush is resolved against the exact
Visual-local cache bounds, then emitted as a composite-only typed mask with the
same outer placement and SnapsToDevicePixels correction as the retained quad.
Changing only mask opacity, stops, animation values, mapping mode, or typed
brush transforms rebuilds the mask/composite while preserving the cached
content revision and skipping the content pass. A transform-free solid mask
continues to fold into uniform layer opacity. Local-cache validation now permits
the optional typed mask reference while effects remain excluded. Inherited
mask composition, mask/effect ordering, gradient-mask plus guideline
composition deliberately fail closed until each has an explicit ordering
representation.

The pinned provider/Dawn Metal hardware gate now exercises the local page
directly: its first 24x18 render performs one content and one composite pass, a
composite-only translation reuses the page with zero content passes, and a
0.5 RenderAtScale update reallocates/rerasterizes a 12x9 page. The complete
package-mode managed Dawn render/readback and forced device-loss recovery pass
at provider revision `02823bf8d2e56548b2780d6b92ae7065be1d8605` and Dawn
revision `710c33013c53ab2700d332c25ff51430251a8cc4`.
The post-raster root-state regression additionally changes only the composite
clip on the retained local page and observes zero content passes on the next
live Metal frame. The nearest-sampling regression then changes only the
composite sampler on that same page and again observes zero content passes.
The dedicated Fant regression downsamples alternating one-pixel stripes with a
phase-misaligned transform. On Apple M3 Pro Metal it changes the cache policy
without rerasterization (`passes=1/1->0/1`) and narrows the interior red-channel
range from `43/117/213` to `106/130/149` (min/mean/max).
All 12 provider-configured native CTests, the base export allowlist,
package-mode managed Dawn render/readback, and forced device-loss recovery pass
with unchanged capture hashes.

Windows ARM64 qualification for this exact cache-bounds checkpoint completed
on 2026-08-25 from clean detached commit `dd3857a4` in the Parallels Windows 11
VM. MSVC rebuilt both `progpu_native.dll` and `progpu_native_dawn.dll` under
`/W4 /WX`; all 11 native/Dawn CTests passed, the base and Dawn export contracts
passed, and both independent C++ and managed samples rendered through the live
`Parallels Display Adapter (WDDM)` D3D12 device. The managed retained sample
lowered 16 source commands to 13 native commands, issued six draws in one
submission, and passed pre-build and post-build allocation/readback checks. The
bounded differential matrix completed its opacity, zero-copy image/mask,
retained semantic scene, mask/effect chain, vector clip, image effect, Overlay,
ColorDodge, and text shaping contracts. The staged ARM64 package DLL SHA-256
values are `D17701FB0669A241183AF064080A1FD1ADD29AE1B000A531CCE5E7307B2650C6`
for `progpu_native.dll` and
`02414A74F7C6CB1A84F2846D5E5B701102E4812B5AEFCBA25688AE881592BD42`
for `progpu_native_dawn.dll`. This qualifies the preceding exact-bounds
target-space checkpoint on DirectX; Windows qualification of the new
local-space/RenderAtScale checkpoint is tracked separately.

The local-space/RenderAtScale checkpoint was then qualified on 2026-08-25 from
the clean detached ProGPU documentation commit `1a75a958` (native
implementation `dee81dff`) in the same Windows 11 ARM64 VM. Strict MSVC rebuilt
both native modules under `/W4 /WX`; all 11 native/Dawn CTests and both export
contracts passed. The independent C++ and managed samples selected the live
`Parallels Display Adapter (WDDM)` D3D12 adapter and completed retained render,
allocation, and pixel-readback checks. The expected Parallels-only retained GPU
hit-test deferral remained isolated to that optional probe. The complete
bounded D3D12 smoke matrix passed opacity, zero-copy image/mask, retained
semantic scene, mask/effect chain, vector clip, image effect, Overlay,
ColorDodge, and managed/C++ text-shaping contracts; the final Overlay and
ColorDodge scenes were pixel-exact. The win-arm64 package was staged with
SHA-256 `FBC4EC3D71A1BB63CA2DE3A092C7F25D63747C47C40AF7FC9D19EA4A379FE5B4`
for `progpu_native.dll` and
`ECC81DF8437FE0C4EC8BB18D9692E248048F04270471E04DC053BF7610E5B173`
for `progpu_native_dawn.dll`. This closes the strict DirectX gate for the
current local-space cache execution subset.

The combined SnapsToDevicePixels/EnableClearType checkpoint was qualified on
2026-08-25 from clean detached ProGPU commit `bff32414` in the same Windows 11
ARM64 VM. Strict MSVC rebuilt both native modules under `/W4 /WX`; all 11
native/Dawn CTests and both export contracts passed. The independent C++ and
managed samples selected the live `Parallels Display Adapter (WDDM)` D3D12
adapter and completed retained render, allocation, and pixel-readback checks.
The expected Parallels-only retained GPU hit-test deferral remained isolated to
the optional probe. The bounded D3D12 differential matrix passed group opacity,
zero-copy image/mask, retained semantic, mask/effect, path-atlas, image-effect,
Overlay, ColorDodge, and managed/C++ text-shaping contracts; Overlay was
pixel-exact. The staged win-arm64 package contains nine files and the native DLL
SHA-256 values are
`768BE3DB0A8970334FE6B4574370CCC96E63A653C94B9ECBD769FAEAD3825891` for
`progpu_native.dll` and
`FC95E25FF8E5313D6151F199E236D376E28C9FF7243AD0887F8FA360B89AA73E` for
`progpu_native_dawn.dll`. This closes the strict DirectX gate for the current
local-space, RenderAtScale, snapping, and ClearType cache subset.

The post-raster cache-root State checkpoint was qualified on 2026-08-26 from
clean detached ProGPU commit `7eb17727` in the same Windows 11 ARM64 VM.
Strict MSVC rebuilt the modified MIL compiler, scene validation, composite
executor, and both native modules under `/W4 /WX`; all 11 native/Dawn CTests
and both export contracts passed. The independent C++ and managed samples used
the live `Parallels Display Adapter (WDDM)` D3D12 adapter and passed retained
render, allocation, and pixel-readback checks; both managed builds completed
with zero warnings. The expected Parallels-only retained GPU hit-test deferral
remained isolated to the optional probe. The complete bounded differential
matrix passed group opacity, zero-copy image/mask, retained semantic,
mask/effect, path-atlas, image-effect, Overlay, ColorDodge, and managed/C++
text-shaping contracts; group opacity, zero-copy image, Overlay, and ColorDodge
were pixel-exact. The staged nine-file win-arm64 package has SHA-256
`B2258721E6AFA621ADB5AC6E284DBF392342288A5620B22156667EE357E7D710` for
`progpu_native.dll` and
`73327D9C482EEE4F387789A9B2561220FD41C8659A4C781AF094CBFC8FB2C3E1` for
`progpu_native_dawn.dll`. This closes the strict DirectX gate for exact
rectangle post-raster clips, one static composite guideline per axis, and
cache-root raster/composite state separation.

The cache-root NearestNeighbor checkpoint was qualified on 2026-08-26 from
clean detached ProGPU commit `625a0961` after merging current `main`. ARM64
MSVC rebuilt the modified MIL compiler, scene validators, retained-layer
resources/compositor, and both native modules under `/W4 /WX`; all 11
native/Dawn CTests and both export contracts passed. The independent C++ and
managed samples selected the live `Parallels Display Adapter (WDDM)` D3D12
adapter and passed retained rendering, allocation, and pixel readback. To stay
inside the Parallels guest memory envelope, the two managed Release builds
were repeated serially with `-m:1 -nr:false`; both completed with zero warnings
and zero errors. The complete bounded differential matrix passed its
mixed-picture native stress and one-rectangle managed parity, group opacity,
zero-copy image/mask, retained semantic, mask/effect, path-atlas, image-effect,
Overlay, ColorDodge, and managed/C++ text-shaping contracts. Group opacity,
zero-copy image, Overlay, and ColorDodge were pixel-exact; bounded
mixed-picture parity had maximum channel difference 2 and no pixels above 3.
The staged win-arm64 package DLL SHA-256 values are
`8CFCBD3BFCC362611EC4A1DB0F17684838C2E1EA1DC30F3EA994B04C63709E2D` for
`progpu_native.dll` and
`9BFB20223CCC046B2280B2B3A8F25E353C916FB001118B3DC5DC47C744968D5F` for
`progpu_native_dawn.dll`. This closes the strict DirectX gate for exact
linear/NearestNeighbor retained-page sampling without changing the cache
content revision.

The cache-root spatial opacity-mask checkpoint was then qualified at exact
ProGPU commit `7497ff59` (native implementation `a3d6b0fd`). A dedicated
backend-neutral semantic scene renders one 24x18 owner-keyed local page through
a linear GPU brush mask, changes only mask opacity from 1.0 to 0.5, and requires
the second frame to perform zero content passes and one composite pass. It
passed on both the Apple M3 Pro Metal adapter and the Parallels Display Adapter
D3D12 backend with identical sampled green-channel evidence `0/112 -> 56`.
The exact Windows run rebuilt under ARM64 MSVC `/W4 /WX`, passed all 11
native/Dawn CTests and both export contracts, completed zero-warning managed
sample/benchmark builds, both independent D3D12 samples, and the complete
bounded image/mask/effect/vector/text/blend matrix, then staged the package
from a clean detached checkout. The win-arm64 DLL SHA-256 values are
`8B1C5FCD58EA5794D14C9F6E75F84B5BDFF890A3B8BAA9054B195D2BC6F63622`
for `progpu_native.dll` and
`E6920A87784984ED82F1E172DD441B8909499DCA8CEC149B145C45B811236D89`
for `progpu_native_dawn.dll`. This qualifies composite-only linear gradient
mask changes and retained-page reuse on DirectX as well as Metal; radial
resource normalization remains covered by the MIL compiler regression.

The native Fant checkpoint was qualified on 2026-08-26 from clean detached
ProGPU commit `ac38938b`. The strict Windows ARM64 MSVC lane passed all 11
native/Dawn CTests and both export contracts, both zero-warning managed builds,
the independent C++ and managed renderer allocation/readback samples, and the
complete bounded D3D12 image/mask/effect/vector/text/blend matrix. The dedicated
Parallels Display Adapter (WDDM) Fant gate changed only the composite sampling
policy, retained the page (`passes=1/1 -> 0/1`), and narrowed the alternating
one-pixel-stripe red range from `0/63/255` to `64/135/191` (min/mean/max). The
staged win-arm64 DLL SHA-256 values are
`FACAE389AC4EC1A818004D3C881B301342BC22C1C3E3E145B5660E03715FFF65` for
`progpu_native.dll` and
`A39DCD04927D02D7EDFB08E747AB08C7CF8FAEE620A45B52162CC1C58169C0FA` for
`progpu_native_dawn.dll`. Together with the Apple M3 Pro Metal gate, this
qualifies the shared bounded Fant path and composite-only cache reuse on both
native WebGPU implementations and DirectX.

The composite-only multi-guideline checkpoint follows WPF's `CSnappingFrame` and
`CShapeClipperForFEB` boundary. Static arrays are sorted, transformed to device
space with WPF float arithmetic, and reversed under a negative axis scale so
they remain increasing. Zero or one coordinate per axis produces the existing
uniform transform offset. With multiple coordinates, WPF transforms every
figure start, line end, and cubic control/end point to device space, chooses the
nearest guideline independently by binary search, and applies that guide's
precomputed round-to-pixel offset; an exact midpoint keeps the lower guide.
Rotation or shear produces an empty snapping frame.

For a cache-root guideline collection, the affected geometry is only the four
retained-page composite vertices. The append-only semantic capability marks a
multi-guide resource as composite-only, validates that it is
referenced exclusively by a local-cache composite State, and snaps those four
absolute target-space coordinates before parent-target localization. At that
checkpoint it deliberately did not make multi-guide resources legal for
ordinary semantic draw states, where WPF requires point-by-point path
deformation; the separate append-only capability below now covers the first
ordinary path subset.

ProGPU commit `9eb46b92` also closes the cache-root spatial-mask/guideline
ordering gap without changing the public ABI. When a local retained layer owns
a typed brush mask and its composite State owns static guidelines, the shared
C++ executor sends both through the same `semantic_state_cursor`. The cached
quad retains its existing per-corner snapping. The exact Visual mask rectangle
uses those same snapped corners to derive a separable axis-aligned affine
coverage frame before the gradient is rasterized. WPF disables this guideline
frame under rotation/shear, so the four bounds corners are sufficient for this
bounded cache-root case. Brush coordinates deliberately remain in their
original target-space frame while the mask geometry deforms, matching WPF's
shape-snapping boundary. Per-draw masks and general path geometry are unchanged.

The first implementation is now executable in both the native C++ and managed
pointer-free builders. Counts are bounded to the canonical UInt16 packet range,
coordinates must be finite and independently sorted, negative-scale MIL
mapping reverses the source traversal, and the managed builder writes directly
to its caller-owned arena without a large temporary stack allocation. Builder
and serialized-scene validation reject a composite-only State on SAVE, PUSH,
or draw commands; only a `CACHE_LOCAL_SPACE` layer's composite State may carry
it. Cache-root MIL compilation also omits that State from the ordinary outer
save so the deformed frame cannot leak into retained-page rasterization.

Native, managed, and MIL regressions cover flag/count mismatch, unsorted input,
ordinary-State rejection, cache-composite acceptance, exact midpoint selection
of the lower guide, mapped/negative-scale payload coordinates, and
content-revision stability across a guideline-only update. The updated Apple
M3 Pro Metal gate additionally carries a horizontal gradient mask and compares
the guideline-driven output with an independently constructed affine reference.
It keeps the retained page across baseline/guided/reference frames
(`1/1 -> 0/1 -> 0/1` content/composite passes), changes 40 pixels, moves the
masked extent from `[21,8]-[25,15]`/red 1,881 to
`[21,9]-[25,15]`/red 1,617, and matches the reference byte for byte
(`referenceChanged=0`). Exact DirectX qualification completed on 2026-08-26
from clean detached commit `9eb46b92`. ARM64 MSVC passed all 11 native/Dawn
CTest cases under `/W4 /WX`, both export allowlists, two zero-warning managed
builds, independent native and managed D3D12 allocation/readback samples, the
complete bounded smoke suite, and nine-file staging. D3D12 reproduced Metal
exactly: `passes=1/1->0/1->0/1`, baseline `[21,8]-[25,15]`/red 1,881, guided
`[21,9]-[25,15]`/red 1,617, affine reference `[21,9]-[25,15]`/red 1,617,
`changed=40`, and `referenceChanged=0`. The staged base DLL was 2,001,920 bytes
with SHA-256 `FF3EAAB807826914615FD98EEEC5EBACB6E783EB8E3A4061178D785CD5B95780`;
the Dawn DLL was 2,039,808 bytes with SHA-256
`1B181A7CF2692164C809D8799539A1FDB8839688C6C01B66AF11F326E39908D1`.

ProGPU commit `7889fa17` closes the remaining regression/qualification hole for
this same cache-root implementation by retaining the guideline packet while an
outer Gaussian effect owns the final output. The native MIL test now proves the
outer effect layer encloses a local cache whose composite State still owns the
guideline set and whose layer still owns the brush mask. The live gate wraps
the cached mask/guideline scene in a two-pass Gaussian effect and compares the
entire blurred result with the same independent affine reference. Apple M3 Pro
Metal executes `2/2/2 -> 1/2/2 -> 1/2/2` content/composite/effect passes,
changes 69 pixels, moves the extent from `[19,6]-[27,17]`/red 1,876 to
`[19,7]-[27,17]`/red 1,617, and remains byte-identical to the reference
(`referenceChanged=0`). This adds no product branch or ABI: it proves WPF's
mask -> cache-root guideline deformation -> effect ordering through the shared
semantic layer executor. Exact DirectX qualification completed on 2026-08-26
from clean detached commit `7889fa17`. ARM64 MSVC rebuilt both base and Dawn
modules under `/W4 /WX`; all 11 native/Dawn CTest cases, both export allowlists,
two zero-warning managed builds, independent native and managed D3D12
allocation/readback samples, and nine-file staging passed. D3D12 reproduced
Metal exactly: `2/2/2 -> 1/2/2 -> 1/2/2`, baseline
`[19,6]-[27,17]`/red 1,876, guided and affine reference
`[19,7]-[27,17]`/red 1,617, `changed=69`, and `referenceChanged=0`. A transient
Parallels Tools command-channel disconnect occurred later in the smoke tail;
the remaining semantic-layer-effect, text-shaping, vector-clip, image-effect,
Overlay, and ColorDodge commands were rerun individually with the script's
unchanged arguments against the same binaries and all passed. The guest ended
clean at the exact commit. The staged base DLL was 2,001,920 bytes with SHA-256
`AD812584A2F7E549755320A44CA76ED5C20DB5DAD1BD66006EB2D0C7B98F0C2D`;
the Dawn DLL was 2,039,808 bytes with SHA-256
`1B181A7CF2692164C809D8799539A1FDB8839688C6C01B66AF11F326E39908D1`.

ProGPU commit `80560d34` adds the first ordinary per-point static
multi-guideline lane without weakening the cache-only contract. The
pointer-free guideline prefix gains the mutually exclusive append-only
`GUIDELINE_PER_POINT` flag; zero/one-guide resources retain the established
zero-flag affine fast path and cache composites retain
`GUIDELINE_COMPOSITE_ONLY`. Native and managed builders validate flag/count
agreement, sorted finite coordinates, direct command-family use, and reject a
per-point State as a local-cache composite.

The first execution subset is one conventional non-boolean semantic path per
draw resource, containing line, quadratic, or cubic segments. The executor
composes the path and complete Visual/DrawingGroup transform, maps every start,
control, and endpoint into absolute target space, applies WPF nearest-guide
selection and lower-guide midpoint ties independently, then rebases the
deformed points into a materialized parent target. It publishes an identity
path transform and recomputes conservative control-hull coverage bounds.
Analytic arcs, multiple/shared or boolean paths, primitives, strokes, meshes,
points, images, glyphs, and 3D remain explicitly fail closed with
`UNSUPPORTED`; dynamic leading/driven pairs remain rejected by MIL. This keeps
the initial capability exact instead of silently substituting a uniform
translation or affine approximation.

The MIL compiler now maps multi-value static Visual and DrawingGroup guideline
collections through the same float scale/translate/reversal rules and emits a
per-point resource for ordinary render state. The public managed builder only
accepts a directly referenced per-point State on `DRAW_PATH`; scoped SAVE is
allowed so MIL subtree state can flow to the executor, which rejects any
unsupported descendant family before rendering. Native stream, scene-builder,
MIL, cursor, managed interop, and LibreWPF packet regressions cover the new
flag and the negative family/arc/boolean boundaries.

The Apple M3 Pro Metal differential authors a fractional rectangle path,
deforms its four line endpoints through two guides on each axis, and compares
the result with an independently authored already-deformed path. Baseline red
sum is 37,536; guided and reference red sums are both 40,800; 48 pixels change
from baseline and `referenceChanged=0`. All ten local native CTests, the
80-test native managed interop class, the zero-warning benchmark build, and
72/72 focused LibreWPF MIL compiler tests pass. The prescribed Windows smoke
script now runs `--semantic-per-point-path-guideline`. Exact DirectX
qualification completed on 2026-08-26 from clean detached implementation
commit `80560d340d6d12eb5e4f846cbcac61a53a482b24`. ARM64 MSVC rebuilt the base
and Dawn modules under `/W4 /WX`; all 11 native/Dawn CTests, both export
allowlists, two zero-warning managed Release builds, independent native and
managed D3D12 allocation/readback samples, managed/C++ text-shaping parity,
the complete bounded differential smoke profile, and runtime staging passed.
The Parallels Display Adapter D3D12 gate reproduced Metal exactly: baseline
`[10,8]-[25,17]`/red 37,536, guided and independently deformed reference
`[10,8]-[25,17]`/red 40,800, `changed=48`, and `referenceChanged=0`. The guest
remained clean and the full script exited normally. The staged base DLL was
2,004,480 bytes with SHA-256
`D1F0CF2A09D021523B3F42D43C7E1549CB5FD1DF5FCACEB0FBA3A07CF12FC34D`;
the Dawn DLL was 2,042,368 bytes with SHA-256
`DB359E0C6155530B87DFC7183E4BE071455964F84B9A3D1ED9DAE20A2AB7148F`.

The hosted GCC 13.3 compatibility lane subsequently exposed C-enum versus
`0U` conditional-expression warnings in the new guideline builder and older
MIL aliased-primitive flag assembly. ProGPU commits `c6080cb0` and `84b0258d`
normalize those ABI flag writes to explicit `std::uint32_t` values without
changing packet layout or rendering behavior. The exact GCC 13.3 Linux ARM64
lane then compiled all 260 strict C++20 objects with
`-Wall -Wextra -Wpedantic -Werror`, passed 10/10 CTests and the native export
allowlist, and rendered the retained sample plus GPU hit-test/readback through
Vulkan llvmpipe. This is compiler/software-adapter qualification; it is not
reported as physical Vulkan-device evidence.

ProGPU commit `2f8cf3c9` extends ordinary per-point deformation from one path
to any number of path records whose segment ranges are ordered and disjoint.
That boundary matches WPF's figure-by-figure `CShapeClipperForFEB` traversal
without allowing one immutable segment slot to be snapped twice. Overlapping,
shared, or out-of-order ranges return `UNSUPPORTED` before submission;
boolean programs and analytic arcs retain their existing fail-closed results.
Commit `dab5db6f` upgrades the live differential to render a line-only
rectangle plus a second quadratic/line/cubic/line figure in one `DRAW_PATH`
resource, snapping both
curve control points, and also submits a deliberately shared-range scene to
prove the negative contract at the native render boundary. Apple M3 Pro Metal
reports baseline `[10,8]-[35,25]`, red 37,536, green 11,542; guided and
independently deformed reference `[10,8]-[35,26]`, red 40,800, green 13,045;
`changed=76` and `referenceChanged=0`. The same qualification is now part of
the common macOS/Linux build script as well as the Windows D3D12 script. WPF
turns `ArcSegment` records into one to four cubic Beziers in `ArcToBezier`
before `CSnappingTask` traverses the core shape, so exact arc parity requires a
separate WPF-compatible lowering checkpoint rather than snapping analytic arc
metadata as if it were an ordinary point tuple.

Clean detached implementation/package commit `885fa670` passed the complete
Windows 11 ARM64 qualification on 2026-08-26. MSVC rebuilt both native modules
under `/W4 /WX`; all 11 native/Dawn CTests, both export allowlists, two
zero-warning managed Release builds, independent native and managed D3D12
allocation/readback samples, the complete bounded differential smoke profile,
and package staging passed. The Parallels Display Adapter D3D12 differential
reproduced Metal exactly: baseline `[10,8]-[35,25]`, red 37,536, green 11,542;
guided/reference `[10,8]-[35,26]`, red 40,800, green 13,045; `changed=76` and
`referenceChanged=0`. The guest remained clean at the exact commit. Qualified
win-arm64 SHA-256 values are
`73D76B0211CDDDB46383359B4F9833DF551BC2E4123C9E09CFA646CD0AD63F1C`
for `progpu_native.dll` and
`450EBC621B482275377C15EB26FFD0CBF90679D8BE4B87152C3F23A055A326B9`
for `progpu_native_dawn.dll`.

### Microsoft D3D12 sample oracle

ProGPU commit `0624a2e3` adds a source-pinned cross-platform graphics oracle
based on Microsoft's `D3D12HelloTriangle`. The gate does not vendor or port the
Windows sample. It checks out Microsoft DirectX Graphics Samples commit
`213dd4fd4918ea009dd8f35adee1aff1f2ecaba4` into an ignored worktree, verifies
the selected upstream files against checked-in SHA-256 values, and applies a
small reviewable capture patch only to that generated checkout. The upstream
project remains MIT-licensed and is built as authored with its declared
`Microsoft.Direct3D.D3D12` 1.618.3 Agility SDK plus DXC package.

The NuGet package is useful here as a native-runtime oracle input: it supplies
the headers and app-local D3D12 runtime selected by the Microsoft executable.
It is not a .NET binding and does not by itself provide ProGPU a DirectX
backend. ProGPU continues to execute the same retained renderer through its
typed Dawn/wgpu-native provider. Aligning that provider to the sample's
app-local Agility runtime would additionally require a deliberately qualified
host-executable export/runtime-selection contract, so the gate records the two
runtime provenances separately instead of claiming alignment that has not been
implemented.

Windows captures the native sample through WARP, while ProGPU renders the
equivalent semantic vertex scene through D3D12. macOS and Linux render that
same ProGPU scene through Metal and Vulkan; they do not try to run a native
D3D12 executable. The aggregate CI job validates four interior probes and a
bounded whole-image differential, then publishes every PPM, JSON manifest, and
comparison report. The bounded tolerance protects the gate against legitimate
cross-adapter edge-rasterization variation while still rejecting color,
interpolation, viewport, orientation, or coverage regressions.

On 2026-08-26, the complete ARM64 Parallels user-session capture succeeded
with WARP and produced a 1280x720 PPM with SHA-256
`1269AE803032CC2BF6AD717E8491CC19BAF7F9FD5C6B233F8C0012D2DFA53933`.
The ProGPU D3D12 frame on the Parallels Display Adapter and the Apple M3 Pro
Metal frame produced the identical PPM hash. Both differentials report maximum
channel difference 0, mean absolute channel difference 0, zero changed
channels/pixels, and zero difference at all four probes. Linux/Vulkan is part
of the hosted aggregate gate and remains separately identified evidence.

Hosted GitHub Actions run `32957387184` then completed the aggregate
`DirectX sample oracle (D3D12/Metal/Vulkan)` gate successfully. The ProGPU
candidates ran on Microsoft Basic Render Driver/D3D12, Apple Paravirtual
device/Metal, and llvmpipe LLVM 20.1.2/Vulkan. All three candidate PPM files
and the native Windows/WARP PPM have the same
`1269AE803032CC2BF6AD717E8491CC19BAF7F9FD5C6B233F8C0012D2DFA53933`
SHA-256. Each candidate reports RGBA SHA-256
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`,
maximum channel difference 0, mean absolute channel difference 0, zero
channels and pixels over tolerance, and four zero-difference probes. The
published `progpu-directx-sample-differential` artifact contains all four PPM
files, their manifests, and the aggregate JSON report.

Hosted build run `32959809523` repeated that exact four-frame result at commit
`885fa670` and completed all 27 jobs successfully. This run also proves the
corrected 26-command DrawingGroup package fixture through the
source-independent native package job and every runnable JIT/NativeAOT desktop
package consumer; no package failure is hidden behind the image-only
aggregate.

Commit `a4ae5576` extends the same pinned gate to Microsoft's
`D3D12HelloTexture`. The native WARP oracle keeps the upstream point sampler,
256x256 eight-by-eight black/white checkerboard, affine UVs, triangular raster
boundary, clear color, and 1280x720 viewport. ProGPU expresses that semantic
contract with a typed nearest-sampled image resource plus edge-aliased cover
triangles, then runs it unchanged through D3D12, Metal, and Vulkan. This
validates texture upload/layout, point sampling, affine UV placement, clear
color, orientation, and the triangle boundary; it does not claim that the
current scene ABI exposes a single arbitrary textured-vertex-mesh command.

The 2026-08-26 Apple M3 Pro Metal capture, Parallels Display Adapter D3D12
capture, and native Microsoft ARM64/WARP capture are byte-identical. Their
1280x720 PPM SHA-256 is
`480B613A9F4FA0E799E46D310E7A3AB9F917B9B60CDA035A2E2718CBF2391397`;
the ProGPU RGBA readback SHA-256 is
`591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.
Explicit clear, black, and white interior probes also pass. The aggregate CI
retains bounded edge tolerance for independent adapters even though these
three qualification captures are exact.

The Ubuntu 24.04 ARM64 Parallels guest also rendered the ProGPU candidate
through Vulkan llvmpipe and produced that exact PPM SHA-256. Its native RGBA
readback SHA-256 was
`AE1BC0D52F98442D79358971BC466A4289904014237851367C6665F9291EFEA3`.
This proves deterministic software-Vulkan agreement with the WARP, D3D12, and
Metal captures; it is not labeled as physical Vulkan hardware qualification.

The same WARP program returns `DXGI_ERROR_NOT_CURRENTLY_AVAILABLE`
(`0x887A0022`) when launched by the Parallels service account because that
session cannot create the required presentation environment. The prescribed
Parallels path therefore launches GUI validation with `prlctl exec
--current-user`; compilation and non-GUI checks may remain service-hosted. The
native sample's app-local Agility runtime also returns `E_FAIL` on the
Parallels hardware adapter, while ProGPU's independently selected D3D12 runtime
passes on that adapter. WARP is consequently the deterministic Microsoft
semantic oracle; ProGPU's hardware D3D12 lane remains the backend execution
qualification. Neither result is mislabeled as proof that the two processes
loaded the same D3D12 runtime.

The exact Windows qualification completed from clean detached latest-main-
integrated commit `d99acbc8`. ARM64 MSVC rebuilt both modules under `/W4 /WX`,
all 11 native/Dawn CTests and both export allowlists passed, both managed
Release builds completed with zero warnings, the independent C++ and managed
D3D12 samples passed allocation/readback checks, and the complete bounded
semantic/image/mask/effect/vector/text/blend smoke matrix completed before the
nine-file package was staged. The win-arm64 DLL SHA-256 values are
`F65DA33BFCE4242A869369052E4C52C3CDB67951988FFCB740E85173A74D2C75` for
`progpu_native.dll` and
`E445C3DED9FC741EFECEDC4764A5AE84C120A4FECD15293058504C39ED8E400F` for
`progpu_native_dawn.dll`. This qualifies the same reusable composite executor
on Metal/WebGPU and DirectX/D3D12.

The nested-cache/effect ordering checkpoint follows the render walk rather
than treating a cache as a flat scene optimization. WPF's
`DrawCacheVisualTree` invokes the cache root's content directly and walks each
child normally. Consequently, a child cache is an independent retained page,
and that child's own opacity/mask/effect executes while producing the parent
cache's pixels. The cache root's own modifiers stay outside its page. WPF's
`PreSubgraph` orders an image-effect visual as clip, outer effect layer, then
inner opacity-mask/opacity isolation; `CanUseCacheAsEffectInput` removes the
inner copy only when opacity is one, no mask exists, and the effect does not
inflate bounds.

The native compiler now emits the exact bounded subset as parent cache, child
effect layer, child local cache. Uniform child opacity is stored on the child
cache composite and therefore executes once on the isolated page before the
outer effect. It is not lowered to per-primitive opacity. The parent's content
revision includes the child's composite state and effect generation; the
child page's content revision excludes those cache-root modifiers. A child
move or effect change therefore rerasterizes the parent while reusing the
child page, while moving the parent cache root keeps both pages. Arbitrary
geometry clips and inflated-bound tightening remain fail closed or conservative
as documented above.

Native MIL regressions assert layer nesting and revision separation across
child movement, parent movement, and effect mutation. The live
`--semantic-nested-cache-effect` gate renders two owner-keyed pages around a
Gaussian effect. On Apple M3 Pro Metal, first/stable/child-moved frames execute
`3 -> 0 -> 2` content/effect-input passes and `2 -> 0 -> 2` effect passes; the
stable output is byte-identical, while moving the child changes 572 pixels,
moves the nonzero extent from `[3,3]-[28,24]` to `[8,3]-[33,24]`, and preserves
the red sum at 24,576.

The identical gate passed on the Parallels Display Adapter D3D12 backend from
clean detached ProGPU commit `b3b4f784` with the same pass counts, extents,
changed-pixel count, and red sum. The strict Windows ARM64 MSVC `/W4 /WX`
qualification passed all 11 native/Dawn CTests and both export allowlists, two
zero-warning managed Release builds, the independent C++ and managed D3D12
allocation/readback samples, and the complete bounded
semantic/image/mask/effect/vector/text/blend smoke matrix. The nine-file staged
win-arm64 package contains SHA-256
`424D1A11F6D398D1AC1F206B2686345882143DEBE7D3140037FBBD0D7EF09EBA`
for `progpu_native.dll` and
`A4BB52C578C71DCDBE3297F9CC7D1DEC4BD13D4046F600D1C6966AA60EC0FD2A`
for `progpu_native_dawn.dll`.

The cached spatial-mask-before-effect checkpoint composes the existing typed
brush-mask and effect primitives in WPF order. The compiler pushes the effect
layer first, then the local cache layer carrying uniform opacity and its
linear/radial brush mask. Popping the cache applies mask and opacity once to
the isolated retained bitmap; popping the outer layer then executes Gaussian
blur or drop shadow. The mask/effect state remains excluded from the cache
root's pixel revision, so changing either retains the source page while its
parent cache, when present, still observes the changed descendant output.

MIL regression coverage clears the earlier spatial-mask/effect rejection,
asserts outer-effect/inner-cache command order, verifies both mask and uniform
opacity remain on the cache composite, and proves the retained content
revision is unchanged. The live `--semantic-cache-mask-effect` Metal gate
renders a half-opacity cached page through a linear opacity mask and Gaussian
blur. First/stable/mask-changed content pass counts are `2 -> 1 -> 1`: the
source page is reused while WPF's post-cache mask/effect composition still
executes. Effect passes remain `2 -> 2 -> 2`; the stable output is
byte-identical, and halving only mask opacity changes 164 pixels, narrows the
red extent from `[21,7]-[31,24]` to `[22,7]-[30,24]`, and reduces red sum from
756 to 372. Inherited mask composition and spatial mask plus guideline
ordering remain fail closed.

The same gate passed with identical metrics and pixels on the Parallels
Display Adapter D3D12 backend from clean detached ProGPU commit `bb550c79`.
The strict Windows ARM64 MSVC `/W4 /WX` lane passed all 11 native/Dawn CTests,
both export allowlists, two zero-warning managed Release builds, independent
C++ and managed D3D12 allocation/readback, the complete bounded
semantic/image/mask/effect/vector/text/blend smoke matrix, and nine-file
package staging. Qualified win-arm64 SHA-256 values are
`FFA0223D369BF89F48E4A9A271318BE7B057022899A3D8B8AA2532BDA44F3C30`
for `progpu_native.dll` and
`7A98FA8A4A69E11886ED6879D430295BAD370F88D463B4E638847D1F8CBE6836`
for `progpu_native_dawn.dll`.

The effect final-output clip checkpoint adds one append-only semantic layer
flag without changing the 64-byte layer descriptor. On a materialized
non-local layer, `LAYER_COMPOSITE_STATE` makes `reserved0` reference a
preceding identity-transform, unit-opacity, clip-only State. The shared
WebGPU/DirectX executor resolves that rectangle as a target-local scissor while
restoring the layer, after the effect chain has sampled the complete isolated
input. Validation rejects the flag on local caches or non-materialized layers,
as well as transformed, masked, guideline-bearing, wrong-kind, or missing
State resources.

MIL compilation now moves the combined current rectangle clip from the
ordinary saved State to the outer effect layer's composite State. When the
effect consumes a local cache, the inner cache composite also omits that clip;
cache opacity and a supported spatial mask remain inside the effect. The
resulting order is final rectangle clip, effect, then cache opacity/mask. This
matches WPF's `PreSubgraph` behavior and avoids truncating blur or drop-shadow
input. At this checkpoint uncached opacity/effect and arbitrary geometry
clip/effect combinations still failed closed; the bounded uncached-opacity
checkpoint below resolves the root/local uniform-opacity subset.

Native and managed builder regressions cover canonical acceptance plus
transformed/non-materialized rejection. MIL regressions cover both uncached
and cached effect input, assert that the outer effect owns the clip-only State,
and assert the inner cache State is un-clipped. The live
`--semantic-cache-effect-clip` gate passes on Apple M3 Pro Metal with
content/effect-input passes `2 -> 1 -> 1` and Gaussian passes `2 -> 2 -> 2`;
both later frames report effect-cache hits. The stable frame is byte-identical.
Narrowing only the final clip changes 428 pixels and crops the red extent from
`[6,4]-[33,27]` to `[14,8]-[25,21]`, while every pixel inside the clip remains
byte-identical to the previously blurred output and every outside pixel is
black. That explicitly proves post-effect clipping rather than source
truncation.

The same gate passed with identical metrics and pixels on the Parallels
Display Adapter D3D12 backend from clean detached ProGPU commit `234687b7`.
The strict Windows ARM64 MSVC `/W4 /WX` lane passed all 11 native/Dawn CTests,
both export allowlists, two zero-warning managed Release builds, independent
C++ and managed D3D12 allocation/readback, the complete bounded
semantic/image/mask/effect/vector/text/blend smoke matrix, and nine-file
package staging. Qualified win-arm64 SHA-256 values are
`86062D03035829A8E6B7DA8CC52EC63FB9E4F3BEA15A91C4C8530B5AFC89D952`
for `progpu_native.dll` and
`CF01D087373FD1580EBE1A5B72BC2314CDCE2AEFA4FE02DBF782C88F3DB11C91`
for `progpu_native_dawn.dll`.

The bounded-effect checkpoint broadens the existing append-only Visual-bounds
sideband without changing the MIL or semantic scene ABI. Despite its retained
`set_visual_cache_bounds` name for binary compatibility, the value is the
source-built Visual's exact descendant bounds and now drives both BitmapCache
page sizing and temporary effect isolation. LibreWPF supplies that value from
`IPortableVisualBoundsSource` whenever a Visual has a cache or effect and fails
closed if the typed snapshot is absent. The native channel preserves a
conservative full-target layer only for older direct consumers that omit the
optional sideband.

The compiler maps descendant bounds through the effective affine transform,
then expands them with WPF's already resolved physical kernel radius. Gaussian
blur inflates every edge by `floor(floor(Radius) * minimumScale)`, capped at
100. DropShadow unions the unmodified source with the translated shadow bounds
inflated by that same radius. A zero-radius effect retained solely to apply a
final clip uses the exact transformed source bounds. The final effect-output
clip remains independent and is never intersected into the sampling extent,
so the optimization cannot truncate blur or shadow input.

Native MIL tests cover the compatibility full-target layer, exact blur,
drop-shadow, and zero-radius bounds. The live `--semantic-bounded-effect` gate
renders the same Gaussian scene once with full-target isolation and once with
the exact inflated extent. On Apple M3 Pro Metal, the layer shrinks from
`96x64` to `28x24`, layer storage from 24,576 to 2,688 bytes, and effect storage
from 73,728 to 8,064 bytes. The output remains byte-identical (`changed=0`) at
extent `[24,14]-[51,37]`, red sum 48,960.

The same gate passed with identical allocation metrics and pixels on the
Parallels Display Adapter D3D12 backend from clean detached ProGPU commit
`ef811a7c`. The strict Windows ARM64 MSVC `/W4 /WX` lane passed all 11
native/Dawn CTests, both export allowlists, two zero-warning managed Release
builds, independent C++ and managed D3D12 allocation/readback, the complete
bounded semantic/image/mask/effect/vector/text/blend smoke matrix, and
nine-file package staging. Qualified win-arm64 SHA-256 values are
`09B17325EFC71E90131AAA4538F883C4D3C9EAFFA3A54539BCE50E18FB07F47B`
for `progpu_native.dll` and
`CE4A5E6E81F11DB499E8B160A550A14701F4D050EC80AC484C5CEEA57BA92F0A`
for `progpu_native_dawn.dll`.

The uncached opacity-before-effect checkpoint extends that bounded layer stack
without adding an ABI flag. WPF's order is final clip, outer effect, then
uniform opacity over the isolated source. For an uncached effect Visual with no
inherited opacity, MIL now emits the bounded effect layer followed by a bounded
`FORCE_ISOLATION` layer carrying the combined local uniform alpha. The saved
draw State and child content scope reset opacity to one, preventing the alpha
from being multiplied independently into overlapping primitives. The inner
layer is restored first, so Gaussian blur or drop shadow samples the completed
half-opacity Visual. A zero-radius blur still retains the opacity layer, while
an optional final rectangle clip remains on a separate outer clip layer.

Inherited non-unit opacity and spatial opacity masks remain fail closed: the
compiler does not move an ancestor's group boundary across a descendant effect.
The implementation reuses the exact typed Visual descendant bounds qualified
above, remains reflection-free, and executes through the same semantic layer
stack on WebGPU and DirectX.

Native MIL tests assert outer-effect/inner-opacity order, exact source/effect
bounds, zero-radius retention, final-clip placement, and inherited-opacity
rejection. The live `--semantic-uncached-opacity-effect` Metal gate compares
two overlapping opaque rectangles under group opacity against a single
half-opacity union reference and a deliberately incorrect per-primitive-alpha
variant. Group/reference output is byte-identical; the incorrect variant
changes 420 pixels and raises the overlap sample from 128 to 188. The qualified
stack executes `2/2/2` content/composite/effect passes and produces extent
`[5,5]-[46,30]`, red sum 65,536.

The same gate passed with identical metrics and pixels on the Parallels Display
Adapter D3D12 backend from clean detached commit `a47d80b5`. The complete
Windows ARM64 MSVC `/W4 /WX` lane passed all 11 native/Dawn CTests, both export
allowlists, two zero-warning managed Release builds, independent C++ and
managed D3D12 allocation/readback, the bounded
semantic/image/mask/effect/vector/text/blend smoke matrix, and nine-file package
staging. Qualified win-arm64 SHA-256 values are
`07E97B185A066124719A2593CBE2AD7762B9FF00FEB406255B428FC7CF2BA85D`
for `progpu_native.dll` and
`35744D6CAF0F8C7789D7DE0E7EFA0985529A27217C7F65613BD0889487D879B2`
for `progpu_native_dawn.dll`.

The typed effect-clip producer checkpoint closes the source-built WPF gap for
the exact rectangle subset already represented by the native effect composite.
WPF's `ScrollableAreaClip` contract describes a simple rectangle clipped in
world space for pixel alignment and explicitly disables accelerated scrolling
when a rotation exists above the Visual. ProGPU therefore accepts a Visual
geometry clip only when it is a sharp `RectangleGeometry` whose effective
matrix preserves axes, and accepts the typed scroll rectangle only when its
parent matrix preserves axes. Their intersection remains the final composite
clip outside the effect input.

Rounded rectangles, ellipse/path clips, and rotation/shear remain fail closed;
the compiler never substitutes their transformed bounding box. LibreWPF
performs the same first-line check through
`IPortablePrimitiveGeometrySource` before emitting MIL, while ProGPU repeats
the effective-transform check at scene compilation where the complete ancestor
state is known. Native tests cover one-axis rounding, transformed scroll clips,
and combined geometry/scroll clipping on an effect; focused LibreWPF compiler
tests cover typed acceptance and rejection without reflection or a managed
rendering fallback.

At implementation commit `3403e841`, all 10 local native CTests, the base
export allowlist, the Apple M3 Pro Metal sample, and the live retained-cache
effect final-output clip gate pass. The Metal gate retains the established
`2 -> 1 -> 1` content and `2 -> 2 -> 2` effect pass sequence, changes 428
pixels from wide `[6,4]-[33,27]` to clipped `[14,8]-[25,21]`, and reduces the
red sum from 48,960 to 32,960.

The same metrics and pixels pass on the Parallels Display Adapter D3D12 backend
from a clean detached `3403e841`. The complete Windows ARM64 MSVC `/W4 /WX`
lane passes all 11 native/Dawn CTests, both exports, two zero-warning managed
Release builds, independent C++ and managed D3D12 readback, the full bounded
smoke matrix, and nine-file staging. Qualified win-arm64 SHA-256 values are
`991F9301B71660FEF89DDA9A4D1E6400D01C92EFAD10B521D3C58BB12482D0F9`
for `progpu_native.dll` and
`616B0650CF74D5D84FB45D908DB6285A82760B59E6A8D56313D827B6885038C7`
for `progpu_native_dawn.dll`.

The uncached spatial-mask-before-effect checkpoint generalizes the bounded
source isolation layer. A typed linear/radial Visual opacity mask is resolved
to the existing semantic brush-mask resource and attached to the same inner
`FORCE_ISOLATION` layer as uniform opacity; that completed source is then
sampled by the outer Gaussian/drop-shadow layer. Cached Visuals retain the
already-qualified local-page mask path, and static solid masks continue to
collapse to uniform alpha. No ABI or backend-specific execution branch is
added.

Only a mask owned by the effect Visual is moved into this scope. An inherited
mask or inherited non-unit opacity remains fail closed because moving an
ancestor's group boundary across a descendant effect changes WPF composition.
MIL tests cover uncached gradient mask plus opacity, bounded layer order, and
the absence of cache flags. The expanded
`--semantic-uncached-opacity-effect` Metal gate executes `2/2/2`
content/composite/effect passes, samples red `36/217` across the gradient, and
produces extent `[7,5]-[47,30]`, red sum 65,264. A deliberately reversed
post-effect mask changes 666 pixels and produces `[10,10]-[41,25]`, red sum
56,038.

The same ownership proof is byte-for-byte stable on the Parallels Display
Adapter D3D12 backend from clean detached implementation commit `3c22b004`:
`2/2/2` passes, samples `36/217`, 666 pixels changed by the deliberately wrong
post-effect mask, masked extent `[7,5]-[47,30]` with red sum 65,264, and wrong-
order extent `[10,10]-[41,25]` with red sum 56,038. The complete Windows ARM64
MSVC `/W4 /WX` lane passes all 11 native/Dawn CTests, both export allowlists,
two zero-warning managed Release builds, independent C++ and managed D3D12
allocation/readback, the full bounded differential smoke matrix, and nine-file
package staging. Qualified win-arm64 SHA-256 values are
`F7B72CAF58C8B4675A3B26FBBC4B62D314F26737CFFC9DC625F1E2BF640A681C`
for `progpu_native.dll` and
`6921A4037372B7A327370DA2035750FD48E791164BD2B5E0407E05F3A01C4A14`
for `progpu_native_dawn.dll`.

The inherited-opacity ownership checkpoint follows WPF's per-Visual effect
stack rather than multiplying ancestor alpha into descendant draw state.
`CMilVisual::HasEffects` treats non-unit opacity as a Visual effect;
`CDrawingContext::PreSubgraph` pushes that node's opacity boundary before
walking children, while an effect-owning child pushes its image effect outside
its own opacity/mask layer. The corresponding `PostSubgraph` pops those layers
per node. An ancestor opacity layer must therefore remain outside a descendant
effect.

For an uncached Visual without its own effect, native MIL now uses exact typed
descendant bounds to emit a bounded `FORCE_ISOLATION` layer carrying that
Visual's local opacity. Descendant state receives only the still-unisolated
ancestor remainder; a child effect consequently emits its own outer effect and
inner local opacity/mask layers inside the ancestor group. Cache roots and
effect-owning Visuals keep their previously-qualified specialized paths.
Callers without exact bounds retain compatibility for simple flattened draws,
but an unresolved inherited-opacity/effect boundary continues to fail closed.
No ABI, reflection, managed rendering fallback, or backend-specific branch is
added.

Native MIL regressions first prove the missing-bound rejection, then bind the
typed parent extent and assert parent opacity -> child effect -> child local
opacity layer order, bounded dimensions, absence of cache flags, and unit
opacity on all descendant scene States. The Apple M3 Pro Metal gate reports
`2/2/2` content/composite/effect passes. The correct ancestor group keeps
exclusive/overlap red samples at `128/128`, extent `[4,4]-[41,31]`, and red sum
67,186. Deliberately flattening the alpha into the child and sibling reaches
`128/189`, changes 392 pixels, and yields `[5,5]-[41,30]`, red sum 74,382.

Clean detached `a3affb9d` produces identical ownership evidence on the
Parallels Display Adapter D3D12 backend. The strict Windows ARM64 MSVC
`/W4 /WX` lane passes all 11 native/Dawn CTests, both export allowlists, two
zero-warning managed Release builds, independent C++ and managed D3D12
allocation/readback, the complete bounded differential smoke matrix, and
nine-file package staging. Qualified win-arm64 SHA-256 values are
`32B4876D3930276798732AF91C5D0C866A4A189FED22BEAF7C93016E6006B8C1`
for `progpu_native.dll` and
`636748FE9C8E29EA5687625E5EF0B77E77017F62FFD463139B36E75162A13DC6`
for `progpu_native_dawn.dll`.

The inherited-opacity-mask ownership checkpoint extends the same per-Visual
boundary to typed linear/radial masks. An ordinary uncached Visual with exact
typed descendant bounds now emits one bounded outer `FORCE_ISOLATION` layer
carrying its local opacity and optional semantic brush-mask resource. Native
MIL resets the isolated local alpha before compiling content and children, so
a descendant effect and its child-local opacity/mask remain inside the
ancestor mask. This is the WPF `PreSubgraph`/`PostSubgraph` ownership model;
the mask is not redistributed into effect inputs or sibling primitives.

LibreWPF may consequently publish the existing bounds sideband for every
typed Visual opacity mask rather than requiring a cache/effect on that same
Visual. A spatial mask without exact bounds returns `unsupported_command` in
the native compiler, while the reflection-free producer fails earlier. Solid
masks continue to collapse to uniform alpha but use the same bounded Visual
group when the producer supplies bounds. Cached and effect-owning Visuals keep
their previously qualified specialized mask paths. No ABI, callback, managed
fallback, or backend-specific execution branch is added.

Portable native regressions assert parent mask -> child effect -> child local
opacity ordering, exact group/mask bounds, gradient stops, and unit descendant
State alpha. The Apple M3 Pro Metal differential compares the correct common
ancestor mask with a deliberately flattened child/sibling mask. Correct output
executes `2/2/2` content/composite/effect passes, samples red `60/200`, and
produces `[6,4]-[41,31]`, red sum 66,698. The flattened variant executes
`3/3/2`, changes 420 pixels, and produces `[6,5]-[41,30]`, red sum 74,122.

Clean detached implementation commit `9fb7c4aa` produces identical evidence
on the Parallels Display Adapter D3D12 backend. The strict Windows ARM64 MSVC
`/W4 /WX` lane passes all 11 native/Dawn CTests, both export allowlists, two
zero-warning managed Release builds, independent C++ and managed D3D12
allocation/readback samples, the complete bounded differential smoke matrix,
and nine-file runtime/SDK staging. Qualified win-arm64 SHA-256 values are
`A4A917F47FBA3BA246BCE9D61C1160384C660F8D07D0BA06A02292BDFDAC0018`
for `progpu_native.dll` and
`743FE185F4D4C900CA1B7F5B18AD85BEAAD47CEA592315AF22D81E625DF0393D`
for `progpu_native_dawn.dll`.

The nested-mask ownership checkpoint proves that this boundary composes rather
than replacing descendant masks. A parent horizontal opacity mask remains the
outer subtree group; inside it, the child Visual emits its Gaussian effect and
then its own vertical opacity mask/local-opacity isolation. Both masks retain
independent Visual-local bounds, transforms, gradient stops, and semantic
resource indices. The generalized per-Visual planner already emits this
parent mask -> child effect -> child mask order, so no additional ABI or
backend-specific path is required.

Native regressions assert both mask resources, their distinct 48x30 and 32x24
bounds/mappings, the three-layer order, child-local alpha, and unit descendant
States. LibreWPF publishes bounds and two canonical mask packets through its
existing typed contracts. The Apple M3 Pro Metal differential compares the
correct common-parent/nested-child stack with a deliberately flattened parent
mask. Correct output executes `3/3/2` content/composite/effect passes, samples
red `28/200`, and produces `[7,4]-[41,29]`, red sum 59,308. The flattened
variant executes `4/4/2`, changes 348 pixels, samples `29/200`, and produces
`[6,5]-[41,28]`, red sum 63,032.

Clean detached `66592f2c` produces identical nested-mask evidence on the
Parallels Display Adapter D3D12 backend. The strict Windows ARM64 MSVC
`/W4 /WX` lane passes all 11 native/Dawn CTests, both export allowlists, two
zero-warning managed Release builds, independent C++ and managed D3D12
allocation/readback samples, the complete bounded differential smoke matrix,
and nine-file runtime/SDK staging. Qualified win-arm64 SHA-256 values are
`9BC233F2462CCA5CE5A9BA31A296BEF80E22D6982D5B706F9756D9F62EC6CB97`
for `progpu_native.dll` and
`743FE185F4D4C900CA1B7F5B18AD85BEAAD47CEA592315AF22D81E625DF0393D`
for `progpu_native_dawn.dll`.

The nested cached-mask checkpoint closes the corresponding retained-page
ownership and invalidation slice. A cache-root horizontal mask remains
composite-only around a child effect and independently cached child vertical
mask. Changing only the root mask preserves both root and child content
revisions. Changing the child mask preserves the child raster revision but
invalidates the root page, because the child's masked/effected composite is
part of the root page content. Native tests assert all three layer descriptors,
both mask payloads, and these revision relationships.

The live Apple M3 Pro Metal sequence renders first/stable/root-mask/child-mask
frames. Content passes are `3 -> 0 -> 0 -> 2` and effect passes are
`2 -> 0 -> 0 -> 2`: the root-only mask change is composite-only, while the
child mask change reuses child pixels and rebuilds only the owning parent/effect
composition. Pixel changes are `0/379/161`; extents/red sums progress from
`[12,6]-[33,25]`/23,482 to `[12,6]-[33,25]`/11,772 and
`[12,6]-[33,24]`/11,266. The existing semantic cache, effect, and brush-mask
resources require no ABI or backend fork.

The exact DirectX qualification completed on 2026-08-26 from clean detached
commit `f8bd57b5`. ARM64 MSVC passed all 11 native/Dawn CTests under `/W4 /WX`,
both export allowlists, two zero-warning managed builds, independent native and
managed D3D12 allocation/readback samples, and the complete bounded smoke lane.
The Parallels D3D12 live gate reproduced Metal exactly: content passes
`3 -> 0 -> 0 -> 2`, effect passes `2 -> 0 -> 0 -> 2`, pixel changes
`0/379/161`, and the same three extents/red sums above. The staged win-arm64
package contained nine files; SHA-256 was
`3E5617D3A46F3B2F26A0F727796277A7A9C026C00188EE88BE1D21C320CF8483`
for `progpu_native.dll` and
`743FE185F4D4C900CA1B7F5B18AD85BEAAD47CEA592315AF22D81E625DF0393D`
for `progpu_native_dawn.dll`.

The intrinsic glyph fallback continues to require measured no-regression on
both normal and high-DPI workloads. An exact ARM64 experiment replacing its
pairwise NEON coverage-count reduction with `vaddvq_u32` horizontal adds was
rejected: four alternating 120-frame runs per variant regressed 2x
submission/frame p50 by 16.8%/6.1%, while 1x frame and tail latency also
worsened. Both variants retained zero pixel difference at
`5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). The qualified pairwise
reduction remains in source; the ignored A/B reports preserve the negative
evidence locally.

The next accepted SIMD checkpoint removes wasted odd-row tail work without
changing the qualified two-pixel loop. A dedicated 8-sample NEON/SSE2 winding
kernel handles the final pixel of an odd-width glyph raster, instead of
running four sample vectors and two coverage reductions then discarding the
second result. Four alternating 120-frame A/B runs per variant remained exact
at `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). Median
submission/frame p50 improved 1.7587/5.3875 -> 1.6904/5.0735 ms at 1x and
2.1352/6.1027 -> 2.0084/5.9048 ms at 2x; p95 improved in every comparison.
The local native suite passes 10/10 and the SSE2 source passes strict x86_64
Clang warnings-as-errors compilation.

A conservative right-bound early-exit candidate was also exact and rejected.
Eight 120-frame 1x runs per variant kept hash `5B6EF4F70536C862` and zero
pixel difference, but median submission/frame p50 regressed
1.3922/4.9143 -> 1.6106/5.2848 ms (+15.7%/+7.5%), with worse p95 medians as
well. The qualified SIMD loop therefore remains branch-free across full pixel
pairs; typed outline bounds are not used as an assumed hot-path shortcut.

Scalar and native-vector precomputed sample-offset candidates were likewise
measured with separately hashed dylibs copied into the benchmark output before
each alternating process. Both were pixel-exact. The refined NEON/SSE2 vector
form improved submission p50 at 1x and 2x, but eight-run 2x synchronized-frame
p50/p95 regressed 5.6951/8.4109 -> 5.8623/8.5459 ms. It was rejected under the
same cross-profile no-regression rule, and the ignored reports retain the
negative evidence.

Precomputing exact line-segment deltas in the existing curve metadata was also
measured and rejected. Four alternating 120-frame runs per variant remained
byte-exact at `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x). The 2x
submission/frame p50 improved 1.7558/5.5705 -> 1.7332/5.0904 ms, but 1x
regressed 1.0949/5.1557 -> 1.1324/5.3494 ms and frame p95 worsened
7.5805 -> 7.9913 ms. The qualified implementation therefore avoids the extra
metadata loads and computes deltas only for intersecting lines.

A second exact experiment skipped the first redundant winding reset in every
pair and odd-tail kernel. Eight alternating 120-frame runs improved 1x
submission/frame p50 1.1608/4.9743 -> 1.0915/4.7015 ms and 2x
1.7850/5.9484 -> 1.7218/5.7874 ms, but 2x frame p95 regressed
7.9814 -> 8.1111 ms (+1.6%). The loop-index branch therefore failed the full
no-regression gate and was rejected.

An additional crossing-layout experiment split positive and negative winding
positions into separate `float` arrays and compile-time-specialized the
NEON/SSE2 updates. Although this halved the per-crossing element size, the two
offset streams and two hot traversal loops regressed initial 120-frame
submission/frame p50 from 1.0344/5.3215 to 1.5310/5.9844 ms at 1x and from
1.6587/5.0745 to 2.5752/6.2036 ms at 2x. Both candidates retained exact hashes
`5B6EF4F70536C862` and `706B261418EC5C3B` with zero channel difference. The
result was decisive enough to stop before the longer alternating matrix; the
qualified interleaved crossing layout remains unchanged.

Precomputing all eight row-local crossing `span` descriptors was also exact
and rejected. The candidate replaced the repeated offset-based `subspan`
construction in each pixel-pair and odd-tail loop with a stack-resident span
array built once per raster row. Apple M3 Pro Metal 120-frame gates retained
hashes `5B6EF4F70536C862` (1x) and `706B261418EC5C3B` (2x), but submission p50
regressed `1.4922 -> 1.7465` ms at 1x and `1.9650 -> 2.6905` ms at 2x;
synchronized-frame p50 changed `5.5365 -> 5.2648` and
`6.1749 -> 6.3856` ms respectively. The added descriptor loads and stack
traffic therefore outweigh any saved view arithmetic, and the source retains
the compiler-optimized offset array with inline `subspan` construction.

Follow-up offset-width and paired-accumulation experiments were exact but also
rejected. Checked 32-bit scanline offsets improved all 2x medians, yet regressed
1x synchronized-frame p50 by 2.0% across eight alternating 120-frame runs per
variant. A base-span-only source rewrite produced a byte-identical dylib,
confirming Clang already hoists that view. Keeping both pixel totals in one
NEON `uint32x2_t` improved submission p50 by 3.0% at 1x and 5.3% at 2x, but
regressed 1x frame p50 by 3.8% and 2x frame p95 by 2.2%. Every report retained
zero channel difference at `5B6EF4F70536C862`/`706B261418EC5C3B`; both
source candidates were reverted under the cross-profile no-regression rule.

The accepted NEON follow-up instead folds exact integer lane reduction. It
adds the low/high 0-or-1 coverage vectors before reducing their halves, saving
one vector add per pixel without a new branch, metadata read, or floating-point
change; SSE2 remains unchanged. Eight alternating 120-frame runs stayed exact
at both hashes. Submission/frame p50 improved 1.0547/4.6895 ->
1.0211/4.4603 ms at 1x and 1.7792/5.4060 -> 1.6849/5.0955 ms at 2x; p95
improved in all four comparisons. The ten-test local native suite and strict
x86_64 SSE2 syntax compile pass.

The next exact NEON candidate converted nonzero winding lanes to coverage bits
with integer absolute-value and unsigned minimum instead of
compare/invert/shift. Eight alternating 120-frame runs per variant preserved
the 1x/2x hashes `5B6EF4F70536C862`/`706B261418EC5C3B` and zero channel
difference. It was rejected because 1x submission/frame p95 regressed
1.4299/7.2335 -> 1.5391/7.3918 ms and 2x synchronized-frame p50 regressed
5.6484 -> 5.8287 ms, despite improvements in the other 2x measures. The
qualified folded reduction remains unchanged.

The exact pushed `deb50413` source also rebuilds with ARM64 MSVC/Ninja and
passes all ten non-Dawn native CTests in the Windows 11 Parallels VM. That run
qualifies the changed NEON source under the Windows compiler and runtime; the
Apple measurements above remain the performance evidence.

Exact current head `23f6848d` subsequently completed the extended Windows 11
Parallels ARM64 MSVC/Ninja D3D12 smoke/package lane. Both providers built with
zero warnings, all 11 native/Dawn CTests passed, allocation/readback samples
completed, and automatic raster, forced raster, forced SIMD, bounded scalar,
and typed forced-compute-rejection routes behaved as declared. The full SIMD
hash remained `5B6EF4F70536C862`; Box blur remained byte-exact at
`D77D5DC8AC370BCE`. Both Microsoft D3D12 sample oracles and the retained
cache/effect/mask/clip/text/blend matrix passed, including byte-exact Overlay
and ColorDodge. Staged SHA-256 is
`9D2E6713B9CF8EE97B58B6ED8BB6B73A4C4DF19AED9C5AF5248C0DF522D45266`
for `progpu_native.dll` and
`51BA93113AB6CA6D76DE29BD5DE83C8397808C44EDD21F277244772779B353EC`
for `progpu_native_dawn.dll`.

The WPF Box blur checkpoint closes the second canonical `KernelType` without a
managed or CPU rendering fallback. Native MIL accepts kernel 1, retains live
animated radius dependencies, and emits a typed reusable Box group effect;
unknown kernel values still fail closed. The backend selects uniform weights
inside the existing horizontal/vertical WebGPU compute passes, preserves the
Gaussian default, and publishes `PROGPU_NATIVE_CAPABILITY_GROUP_BOX_BLUR` plus
typed C/C# factories. The `--group-box-blur` integration gate uses an
independent two-pass RGBA8 CPU oracle only after GPU readback for validation;
it is not a product fallback. Apple M3 Pro Metal is exact at radius 2/1x with
hash `22A8BEC63E7C7494`; at 2x its maximum difference is 1/255, no pixel exceeds
tolerance, and mean absolute error is 0.000455 byte/channel.

The portable managed compositor now exposes that shared shader selection as
`BlurEffect.KernelType` and `ComputeAccelerator.ApplyBoxBlur(...)`. Box keeps
the same cached two-pipeline/two-uniform-buffer resource family as Gaussian,
uses the native path's floored physical radius bounded to 128, and remains
fully GPU-resident. Gaussian is still the source-compatible default. Headless
WebGPU tests execute both kernels without adding another shader or pipeline
and assert bounded parameter layout plus distinct transparent-edge output.

Exact native Box checkpoint `0866b919` completed the full Windows ARM64
Parallels D3D12 lane from LibreWPF `8dabd9d84`: strict MSVC `/W4 /WX`, 11/11
native/Dawn CTests, native and managed samples, forced raster/NEON/scalar
parity, expected typed compute rejection, Microsoft D3D12 triangle/texture
oracles, retained cache/effect/mask/clip/text/blend profiles, and package
staging. The new Box profile was byte-exact against its independent two-pass
oracle at hash `D77D5DC8AC370BCE`. SHA-256 is
`3A64CFDD974448B71F8BF645AFCBDE95DC10C64256F73D7CEF1E12776DB3DA20`
for `progpu_native.dll` and
`B77C8A4157D4432F8C74F6067DE2944F96C8ECE0FA16B4967B24D330326DD70A`
for `progpu_native_dawn.dll`.

Exact pre-Box Windows checkpoint `edd98b71` completed the full Parallels D3D12
Smoke/package lane after the PowerShell 5 expected-failure harness was made
host-independent. ARM64 MSVC `/W4 /WX` rebuilt both providers, all 11 CTests
passed, forced raster/SIMD/scalar glyph routes remained exact, forced compute
failed at the typed pre-resource boundary without WebGPU errors, Microsoft
D3D12 triangle/texture oracles passed, and the complete cache/effect/text/blend
matrix staged nine files. SHA-256 is
`0E13CD164AB5449DA7FEFB44F7FE26DE76E2200B16EAC047BFBAA1589C5A3C07`
for `progpu_native.dll` and
`F58F610CF3513C275C59254510D646C3B7F2BA175B3927F6679ABC36067A8721`
for `progpu_native_dawn.dll`.

Portable WPF animation-clock resources now have narrow current-value contracts
for `Double`, `Point`, `Size`, and `Rect`. The contracts carry only neutral
scalar/portable structs and deliberately expose neither `AnimationClock` nor
WPF runtime types. Source-built WPF can publish the value at scene compilation
time and use `IPortableInvalidationSource` to reschedule compilation whenever
the clock invalidates. This is the typed basis for retaining canonical MIL
animation handles in LibreWPF instead of reflecting over generated resource
classes or silently replaying their stale base values.

`NativeMilBatchBuilder` now writes canonical type-50 `SizeResource` and type-52
`RectResource` packets in addition to the existing double/point resources.
`NativeMilRenderDataBuilder` exposes the complete animated 2D draw packet
family: line, rectangle, rounded rectangle, ellipse, and image. These writers
preserve every optional animation handle and canonical zero-padding field, and
validate finite nonnegative geometry before the batch crosses the native ABI.
Focused managed native-interop coverage passes 87/87 with exact byte offsets
for every new packet.

The implementation sequence is intentionally architectural:

1. Add a semantic cached-layer descriptor and persistent owner-keyed page pool
   shared by wgpu-native/Dawn and DirectX.
2. Prove cache hits survive unrelated sibling/composite changes, while content,
   scale, size, text mode, and device generation changes invalidate the page.
3. Decode canonical BitmapCache/Visual packets into that descriptor and retain
   dependency lifetime transactionally. (Implemented with local-space bounds,
   composite-only placement, and positive finite RenderAtScale.)
4. Publish neutral typed cache state from source-built WPF and emit it from
   LibreWPF without reflection.
5. Qualify composite clip/mask/guideline/sampling ordering, nested cache
   lifetime, effects ordering, and LibreWPF package lanes. (Exact rectangle
   composite clips, static composite guidelines including the bounded
   multi-guide cache-root subset, linear/radial cache-root
   opacity masks, NearestNeighbor sampling, and the combined
   snapping/ClearType checkpoint are implemented and qualified on live Metal
   and D3D12. Fant/HighQuality sampling is implemented as a bounded shared
   shader prefilter and qualified on both adapters. Nested child cache plus
   uniform-opacity-before-effect ordering is also implemented and qualified
   on Metal and D3D12.)

The persistent page and composite-transform path are executable, but full cache
parity is not claimed until the remaining post-raster state and ordering
policies above are exact. Treating BitmapCache as a no-op, an ephemeral full-target
layer, or a depth-slot effect-cache alias would preserve neither WPF pixels nor
its performance contract and remains explicitly excluded.

Transform-bearing SolidColorBrush packets now match WPF's native resource
contract. WPF's generated `CMilSolidColorBrushDuce::ProcessUpdate` validates
and registers both absolute and relative transform resources, while
`GetBrushRealizationInternal` intentionally realizes only animated color times
animated opacity because a uniform color has no brush-coordinate dependence.
ProGPU now retains those two typed transform handles, validates their resource
types transactionally, protects them from deletion, and includes their
generations in dependency revision traversal. Scene brush color and opacity
remain transform-invariant on every backend; no fake coordinate mapping or
managed fallback is introduced. Native coverage exercises nonidentity absolute
and relative transforms, the unchanged semantic brush payload, wrong-type
rollback, and referenced-transform deletion rejection.

## Dynamic guideline parity design

WPF dynamic guideline arrays are ordered pairs `(leading, shiftToDriven)`, not
two independent snap coordinates. Native WPF transforms the leading coordinate
to device space, derives its snap offset, then derives the driven offset from
the same leading offset plus the pixel-rounded separation. Its retained state
moves through Start, Quiet, Animation, Landing, and Flight phases. The critical
animation window is 200 ms, follow-up scheduling uses 50 ms intervals, landing
converges in 0.05-pixel steps, jumps of at least three pixels do not animate,
and non-scale/translate transforms enter Flight without applying a snap.
Rendering into a VisualBrush also suppresses the animated correction because
WPF does not retain independent path history for repeated brush uses.

The legacy scene-build ABI cannot represent that behavior exactly. It learns
the actual DPI only after the immutable semantic scene has been compiled,
accepts neither a monotonic timestamp nor a VisualBrush-use flag, returns no
`needMoreCycles` scheduling result, and its managed wrapper commonly performs a
size query followed by a copy build. Advancing a phase during both calls would
double-step the state. Recreating `NativeMilChannel` for every compilation also
discards the per-resource history. Consequently dynamic GuidelineSet and the
compact PushGuidelineY1/Y2 forms remain fail closed; treating each pair as two
static coordinates or assuming DPI 1 would be observably wrong.

The required append-only implementation is:

1. Add a versioned build-request struct carrying actual X/Y DPI, monotonic
   time, a stable request serial, and whether the content is being realized for
   a VisualBrush path.
2. Keep per-channel, per-guideline-resource, per-axis pair state containing the
   phase, bump time, last supplied leading coordinate, and last applied offset.
3. Resolve each pair once per request into explicit leading/driven offsets in
   the semantic guideline resource, and return a versioned scheduling result
   with `needs_more_cycles` plus the next due time.
4. Make size-query/copy idempotent by caching the compiled request under its
   serial (or by separating compile from copy); only the first compile may
   advance the state machine.
5. Keep the native channel alive for the corresponding retained WPF source and
   feed scheduler invalidation back through the typed portable render service.
6. Differential-test every phase, large jumps, integer driven separation,
   non-axis transforms, VisualBrush suppression, resource replacement/deletion,
   DPI changes, and the compact Y1/Y2 packet rewrite against Windows WPF.

This design leaves existing static-guideline packets and build callers binary
compatible while providing enough state for exact Metal, Vulkan, and DirectX
execution. No backend-specific dynamic-guideline approximation is permitted.

The first append-only ABI/state checkpoint now implements items 1 and 4. Both
native providers export
`progpu_native_mil_channel_build_scene_with_request` alongside the unchanged
legacy entry point. Its 64-byte, size-versioned request carries target, scene,
generation, X/Y DPI scale, monotonic nanoseconds, a nonzero request serial, and
the VisualBrush flag. Its 32-byte result echoes the serial and reserves typed
`needs_more_cycles`, next-due-time, and stream-size fields. Unknown flags,
nonzero reserved fields, zero identities, nonfinite/nonpositive DPI, DPI above
the bounded 65,536 scale limit, undersized structs, and invalid destination
contracts fail closed.

The native channel caches the immutable stream, metrics, and result under the
full request key. A size query, its copy, and any repeated identical request
therefore return the same bytes without recompilation. Reusing a live serial
with different frame fields is rejected as a caller contract error; a request
with a new serial or any successful transactional MIL/sideband mutation
invalidates the cache.
Failed mutations do not discard a valid cached frame. Cache state is separate
from the copy-on-write resource graph, so transactional batch candidates do
not duplicate compiled scene buffers. The C++ API exposes the cached stream as
a borrowed span whose lifetime ends at the next successful mutation or
different request.

`ProGPU.Backend.Native` publishes the same typed request/result flags and
`NativeMilChannel.CompileScene(NativeMilSceneBuildRequest)`. Its managed
size-query/copy pair reuses the serial and verifies the echoed serial and byte
count before returning `NativeMilStatefulCompiledScene`. The source package
consumer exercises this path through both wgpu-native and Dawn and compares it
byte-for-byte with the legacy compiler. The full dual-provider build and all
12 configured native CTests pass on macOS; the focused project-reference
managed MIL package smoke also passes for both providers.

This checkpoint deliberately returns no scheduling flag yet and continues to
reject dynamic GuidelineSet compilation. It establishes the idempotent clock,
DPI, context, scheduling-result, and cache contract required by the subsequent
per-resource phase-state implementation without claiming dynamic-guideline
pixel parity prematurely.

The following backend-neutral semantic checkpoint adds the representation the
phase machine will emit. `PROGPU_NATIVE_SCENE_GUIDELINE_EXPLICIT_OFFSETS` is an
append-only guideline-resource flag: after the existing sorted X and Y
coordinate arrays, the payload carries one finite physical-device-pixel offset
for every X and Y coordinate. The existing resource header and scene ABI sizes
do not change. Static resources remain byte-for-byte unchanged, while explicit
offsets compose with the existing composite-only/per-point modes and are
consumed by the shared semantic state cursor before provider-specific draw
execution. This avoids backend-specific animation code and CPU readback.

The typed scene builder verifies matching coordinate/offset counts, sorted
finite coordinates, finite offsets bounded to WPF's one-pixel driven-offset
range, multi-guide mode consistency, and the existing resource caps. The scene
validator independently checks the extended payload size and every offset.
Focused tests prove builder/validator round-trip and DPI conversion: a stored
physical offset is divided by target DPI exactly once when applied to logical
scene state. The complete configured native/provider matrix remains 12/12.
At that checkpoint MIL dynamic resources remained rejected pending retained
phase history and scheduling decisions.

The retained phase checkpoint now implements canonical dynamic GuidelineSet
pairs for the versioned stateful build path. Each pair keeps WPF's Start,
Quiet, Animation, Landing, and Flight state, the low 29 bits of bump time,
last device-space leading coordinate, and last physical offset on its retained
resource. The transition code follows `CDynamicGuideline` directly: a 200 ms
critical window detects movement, jumps of at least three pixels suppress
animation, animated frames use zero leading offset, and Landing converges by
0.05 physical pixels per requested cycle. The driven coordinate combines the
leading coordinate with the pixel-rounded pair separation, preserving WPF's
stable text/decorator gap.

Animated or landing state sets `NEEDS_MORE_CYCLES` and returns a saturated
next-due monotonic timestamp 50 ms after the request. Repeating the same request
serial returns the cached scene/result and cannot advance Landing twice.
Compilation copies the resource graph only when it contains dynamic guideline
resources, commits phase history only after a complete successful scene build,
and discards it on failure. Ordinary stateful/static and all legacy builds do
not pay that copy; the legacy ABI continues to reject dynamic resources because
it has no clock, DPI, or idempotency contract.

VisualBrush requests suppress phase mutation and emit ordinary grid-derived
leading correction plus the rounded driven separation, matching milcore's
multi-path workaround. Rotation/shear moves every pair to Flight and emits no
snapping frame; the next axis-aligned use re-enters Quiet from current device
coordinates. Resource replacement resets history transactionally, while
unrelated successful resource batches retain it through the channel's existing
copy-on-write graph.

Focused native tests cover initial Quiet, movement into Animation, the 200 ms
timeout, the first 0.05-pixel Landing step, exact 50 ms scheduling,
VisualBrush suppression, legacy fail-closed behavior, and same-request byte
idempotency. They also prove that a later unsupported render-data record rolls
back an already advanced Landing step, a shear enters Flight without emitting
a guideline resource, the next axis-aligned build reinitializes from current
coordinates, and a four-pixel jump stays in Quiet. The full configured
native/provider matrix passes 12/12. Two limits remain explicit for the next
slice: nonuniform X/Y DPI fails closed until the shared state cursor accepts
per-axis DPI, and compact PushGuidelineY1/Y2 records still require lowering
into the same retained pair state.

The compact render-data checkpoint closes the second limit. Canonical
`PushGuidelineY1` is lowered to one Y-axis dynamic pair with zero driven shift;
`PushGuidelineY2` retains its leading coordinate and offset-to-driven value.
Each render-data resource owns phase state by stable packet offset, matching
milcore's per-`RenderData` guideline-kit lifetime. Updating that resource
clears all compact histories just as WPF reconstructs its kits, while unrelated
resource updates preserve them. The stateful compiler recognizes compact
records before choosing its transactional graph copy, so a later failing
command cannot leak phase mutation; legacy builds reject them before even
allocating history.

Both compact forms flow through the same Start/Quiet/Animation/Landing/Flight
implementation and explicit-offset semantic resource as retained
`GuidelineSet`; no backend-specific path was added. Focused tests prove Y1
initial snapping, movement into Animation and scheduler feedback, Y2 driven
gap stabilization, render-data replacement reset, Y-only semantic shape, and
legacy fail-closed behavior. At this checkpoint, nonuniform X/Y DPI was the
remaining native dynamic-guideline limitation, and `NeedsMoreCycles` still had
to be consumed by the typed LibreWPF render scheduler.

The scheduler-timing checkpoint adds the portable managed half of that
contract. `NativeMilSceneBuildTiming.TryGetContinuationDelay(...)` validates
the request/result serial and known result flags, converts the absolute
monotonic native due time to a relative `TimeSpan`, and rounds fractional
100-nanosecond ticks upward so a UI host never advances a phase early. Overdue
work returns a zero delay and completed scenes return no continuation. The
helper has no dispatcher dependency and is shared by WPF, WinUI, and Avalonia;
each host remains responsible only for submitting the returned delay to its
typed scheduler and waking its native event loop. Package-consumer coverage
executes future, fractional-tick, complete, and overdue cases before compiling
the same MIL stream through wgpu-native and Dawn.

The per-axis DPI checkpoint closes the remaining native dynamic-guideline
limitation. The shared phase resolver now performs X movement, three-pixel
jump detection, landing, logical-coordinate conversion, and explicit physical
offset calculation with `dpi_scale_x`; the Y resolver independently uses
`dpi_scale_y`. It no longer rejects a valid nonuniform frame request. Exact
tests cover a retained X pair at 1.25x/1.5y and compact Y1/Y2 phase state at
1.25x/2.0y, including axis-specific initial offsets, Animation feedback, and
the driven Y gap after render-data replacement. Both providers consume the
same semantic output, and the full configured native/provider matrix remains
12/12.

The managed canonical render-data builder now exposes typed
`PushGuidelineY1(double)` and `PushGuidelineY2(double, double)` methods. They
emit the exact 12-byte and 20-byte generated MIL command layouts inside the
standard DWORD-aligned size framing, reject non-finite coordinates before
writing, and keep compact guideline production available to every managed
host without raw packet construction. The public builder surface therefore
matches the compact records already consumed by the native phase
implementation.

The host-target checkpoint exposes `NativeSceneExternalTarget` and matching
`NativeCompositor.RenderScene(...)` overloads. A WPF, WinUI, or Avalonia host
that already owns an acquired WebGPU texture view can now submit the installed
semantic scene directly without transferring the surface texture reference,
creating a second view, copying pixels, or manufacturing a `GpuTexture`.
Width, height, and nonzero view identity are validated before entering the C
ABI. The typed contract makes the remaining device-domain, configured-format,
render-attachment-usage, and submission-lifetime obligations explicit because
only the host that acquired the opaque view can prove them.

The existing `GpuTexture` overload delegates to the same frame builder and
retains its stronger context, format, sample-count, usage, disposal, and
generation validation. Project-reference package coverage renders the compiled
MIL scene through both the owned-texture and host-owned-view overloads and
waits for the external-target submission. Provider resolution remains inside
the compositor, so this is one API for wgpu-native and Dawn rather than a
WPF-specific backend fork.

Exact implementation checkpoint `b97b99e3` completed the full Windows 11
ARM64 Parallels D3D12 smoke/package lane from an immutable source archive.
MSVC 19.44 rebuilt both native providers in the 312-step `/W4 /WX` graph and
all 11 available native/Dawn CTests passed. Automatic and forced raster plus
forced NEON retained exact glyph pixels at `5B6EF4F70536C862`; the bounded
one-glyph scalar oracle retained `6C59592F05595EFE`; and forced compute failed
at the typed pre-resource incompatibility boundary without a WebGPU/device
error. The ProGPU Microsoft D3D12HelloTriangle and D3D12HelloTexture oracles
retained SHA-256 values
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and
`591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.

The same run completed the native mixed-picture stress (8.945 ms/frame in
this correctness-oriented VM run), bounded managed/native differential,
external and masked images, cache/guideline/Viewport3D/effect/clip fixtures,
text shaping, Box blur, effect chains, Overlay, ColorDodge, and runtime package
staging. The VM did not have PowerShell Core and Parallels Tools guest RPC was
unavailable, so the existing script ran under Windows PowerShell 5.1 with an
`IsWindows` variable defined only in that child process; execution policy and
machine state were not changed. This is complete evidence for the checkpoint's
declared Parallels lane, not physical-D3D12 performance evidence or a claim
that the remaining MIL stages are complete.

Two adapter-specific limitations remain explicit. Retained GPU hit-test
readback is deferred on the Parallels display adapter because its blocking
readback path stalls, although the retained D3D12 render/readback sample passes.
The legacy managed renderer also removes the Parallels D3D12 device on the
dense 384-command mixed-picture workload; the same workload passes through the
C++ renderer, so this adapter's gate keeps full native stress and a bounded
managed differential as separate processes. Neither is evidence of
full DirectX/MIL parity; Stages 1–5 remain open until their listed protocol and
integration surfaces are implemented.

## Translated binary EvenOdd XOR GPU checkpoint

Exact ProGPU checkpoint `d4ca87d92877eb38b84106a6bdd0bcb0f02d72c3`
closes the canonical translated-equivalent two-leaf EvenOdd overlap that was
previously fail closed. The typed boolean compiler recognizes only the exact
`leaf leaf xor` postfix shape and emits two ordinary leaf records plus one
packed coverage-combine record. Path fills and vector clips batch leaf A and
leaf B independently, submit those GPU phases in order, then execute the shared
packed-u32 XOR kernel and atlas copy. A semantic caller flushes pending work
once and receives a fresh encoder after the split phases. Normal scenes do not
allocate the two extra uniform buffers or bind groups and retain the existing
single-submission path. There is no CPU coverage readback, CPU repacking,
per-leaf submission, new public ABI, or managed fallback.

The immutable archive
`ProGPU-native-phased-xor-d4ca87d9.zip` has SHA-256
`CBA84443FA2EFC2AE74A6677370E9C6CF4E69729FEC5FEF15EC27E5EBEEB3DA2`.
Apple M3 Pro Metal rebuilt the native backend, passed all 10 configured CTests,
and read the permanent binary-XOR sample as cyan `51/209/242`, clear overlap
`5/6/10`, and cyan `51/209/242`. The same exact archive was hash-copied into
the Windows 11 ARM64 Parallels guest. Strict MSVC/Ninja rebuilt both native
providers, all 11 CTests passed, and the real D3D12 sample on
`Parallels Display Adapter (WDDM)` reproduced the same three pixels. The sample
executed nine draws from fifteen retained commands and uploaded 12,512 vertex
bytes.

The complete bounded Windows D3D12 integration profile also passed. It built
the managed graph with zero warnings/errors, passed the managed native sample,
qualified automatic and forced raster-shader glyph paths, intrinsic-SIMD CPU,
and scalar-reference modes with exact pixel parity, and proved that forced
native compute fails closed with the typed unsupported-adapter diagnostic.
HelloTriangle and HelloTexture DirectX contracts rendered through ProGPU with
SHA-256 `AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and `591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.
The retained masks, effects, 3D, text, images, cache, vector-clip, box-blur,
effect-chain, Overlay, and ColorDodge gates all remained within their declared
differential contracts; Overlay and ColorDodge were exact. Final staged binary
SHA-256 values are
`894B9C4337FED2134245E0D59ED17E0A0BCBBE52988681453393D3462F48CA97`
for `progpu_native.dll` and
`BEC988C393985D52900A787F667501ADB4F7A3CB8ADC6A29EDF9497C7D7BFF4B`
for `progpu_native_dawn.dll`.

## Translated ternary EvenOdd XOR GPU checkpoint

Exact ProGPU checkpoint `db4ffef23ef0a679e4a84b8a54ffbb4fc1991d0e`
extends the ordered split-GPU program to the exact
`leaf leaf xor leaf xor` postfix shape. Three contiguous leaf records feed
phase-batched A, B, and C raster work, and the existing 32-byte packed coverage
record now carries the optional C offset and a typed leaf count. The shared
combine shader calculates `A xor B xor C` before the atlas copy. Phase-C
buffers, bind groups, upload bytes, and submissions are created only when at
least one ternary program is present; ordinary and binary scenes keep their
prior resource paths. Path fills and retained vector clips use the same typed
record builder and shader. No CPU readback, CPU repacking, per-item submission,
managed fallback, or public ABI change was introduced.

At this exact checkpoint, the MIL compiler test accepted three translated-
equivalent EvenOdd leaves and explicitly proved that the equivalent four-leaf
program remained fail closed; the generalized checkpoint below supersedes that
temporary ceiling.
Internal tests assert the three contiguous GPU records. The permanent native
sample covers the five non-overlap/overlap regions and requires cyan, black,
cyan, black, cyan. Apple M3 Pro Metal rebuilt the backend, passed all 10 CTests,
and produced `51/209/242`, `5/6/10`, `51/209/242`, `5/6/10`, and
`51/209/242` for those regions.

The immutable archive `ProGPU-native-ternary-xor-db4ffef2.zip` has SHA-256
`70FFC3367638D5EDFA13DF9740578BFE808E57DED511625B08312C6B6B321807`.
Every changed source file was hash-checked after copying that archive into the
Windows 11 ARM64 Parallels guest. Strict MSVC/Ninja rebuilt both native
providers, all 11 CTests passed, and `Parallels Display Adapter (WDDM)` produced
the same five D3D12 pixels. The expanded sample executed eleven draws from
eighteen retained commands and uploaded 12,960 vertex bytes.

The complete bounded Windows D3D12 profile passed again: managed/native
readback, automatic and forced raster-shader paths, forced intrinsic-SIMD CPU,
forced scalar-reference CPU, typed fail-closed native compute, DirectX
HelloTriangle/HelloTexture, retained effects/masks/3D/text/images/cache, vector
clips, box blur, effect chains, Overlay, and ColorDodge. HelloTriangle and
HelloTexture retained SHA-256 values
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and `591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.
Final staged binary SHA-256 values are
`C47649929661AC238ABD41CFCEA0486BE7F839AF0D6FD5E3023C4591F77AE020`
for `progpu_native.dll` and
`AE1B4271CB9D16296539170BA3C0191D45A149847A14E4B51C66F4B47A530A07`
for `progpu_native_dawn.dll`.

## General pure left-fold EvenOdd XOR GPU checkpoint

Exact implementation checkpoint `402ecfb9` generalizes the fixed A/B/C split
to every pure `leaf leaf xor ... leaf xor` program through the existing
32-child MIL ceiling. Portability checkpoint `c5fb5244` renames a resource
handle that shadowed an MSVC declaration; it changes no algorithm or ABI. The
32-byte combine record now carries a source base, stride, and count. Path and
clip execution retain one phase-batched source buffer and bind group per leaf
ordinal, and the shared WGSL combine kernel loops over each program's bounded
source range. The result is exact ordered XOR parity without CPU readback,
CPU repacking, managed fallback, public ABI change, or per-item submission.
Ordinary scenes keep their single-submission path and do not reserve the outer
split-program vectors. Mixed boolean postfix programs remain typed fail closed.

Native tests cover binary, ternary, quaternary, mixed-program rejection, and
the full 32-leaf record boundary. The permanent native sample adds a four-leaf
vector clip and requires seven alternating regions. Apple M3 Pro Metal rebuilt
the backend, passed all 10 configured CTests, and read the quaternary regions
as cyan/black/cyan/black/cyan/black/cyan. The complete sample executed thirteen
draws from twenty-one retained commands and uploaded 13,408 vertex bytes.

The immutable full source archive
`ProGPU-native-general-xor-c5fb5244.zip` has SHA-256
`D68DE4BDB753A1FCB3E7E2C6DF3DBF9C55D9BEE68FAD6F57BFD8FF43BDF2574E`.
It remains the provenance artifact. After the Parallels shared-folder full-
archive extraction stalled, the ten changed files were also transferred in the
exact delta archive `ProGPU-native-general-xor-delta-retry-c5fb5244.zip`,
SHA-256
`224E41DA07D2531FC93C5AD5DAF866FAC4F31A4DBFC3D54FC60E03FF61D2B538`.
Every delta source and destination hash was checked before rebuilding the
previously qualified source tree.

Strict Windows ARM64 MSVC/Ninja rebuilt both providers, all 11 CTests passed,
and `Parallels Display Adapter (WDDM)` executed the sample through D3D12 with
the same binary, ternary, and quaternary pixel sequences. The complete bounded
integration matrix passed managed/native readback, automatic and forced raster
shader, forced intrinsic-SIMD CPU, forced scalar-reference CPU, typed fail-
closed native compute, retained masks/effects/3D/text/images/cache, vector
clips, box blur, effect chains, Overlay, and ColorDodge. ColorDodge was pixel-
exact with matching native/managed FNV-1a
`41DAE69420EE7C25`. HelloTriangle and HelloTexture retained SHA-256 values
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and `591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.
Final binary SHA-256 values are
`07CB46633DE7AB2D872475CF2682D8AAA493D2CACDC707924948F625F0DDBA39`
for `progpu_native.dll`,
`E29F579504B66A974BD14786E4A6D9D4AACDDC1E30C07B9DA814196C6BBE7598`
for `progpu_native_dawn.dll`,
`372174B1FD90D370AB333301EAD0B0CC72895BE172E7AAAABD4D0C7C8BA3B5A6`
for `progpu_native_sample.exe`,
`C9920BE3B258F55D9101F23B4EC610666D1D2D5E606D6E5B9A8E1F911D71D6EB`
for `progpu_native_mil_tests.exe`, and
`EF4FDFB21F49F9BAD8A078E03D219C60BF40A843A8CDFB529CD724AF18BD44FD`
for `progpu_native_internal_tests.exe`.

## General mixed boolean GPU-mask checkpoint

Exact implementation checkpoint `73319afa8c6cd7326b9c24769f95985b86f5bb56`
removes the remaining typed rejection for overlapping translated-equivalent
leaves inside mixed postfix boolean programs. A phased leaf kernel stores all
64 supersamples as two packed `uint32` words per target pixel. The shared
combine kernel evaluates the original bounded postfix program over those masks,
including Difference, Intersect, Union, XOR, and ReverseDifference, and performs
one final R8 average. Boolean coverage is therefore combined before
quantization; the earlier fractional-edge XOR error cannot reappear.

The compiler retains the ordinary single-dispatch program for non-overlapping
mixed paths. It selects the phased route for pure left-fold XOR and for a mixed
program only when its conservative transformed bounds identify overlapping
translated-equivalent leaves. All leaf ordinals are batched by phase, not
submitted per item. The path-fill and retained vector-clip families share the
same mask records, shader modules, and combine evaluator. No CPU coverage
readback, CPU repacking, scalar mask construction, public ABI change, or
managed fallback was added.

Native tests cover every boolean opcode, mixed Difference-then-Union ordering,
translated overlap detection, safe single-dispatch retention, 64-sample
packing, and fractional-edge quantization. Apple M3 Pro Metal passed all 10
configured CTests and the complete native build gate. The permanent sample
requires cyan/black/cyan for the mixed program and exact fractional XOR edge
pixels `28/108/126`, black overlap `5/6/10`, and `28/108/126` on the far edge.
It executes 18 draws from 28 commands and uploads 14,528 vertex bytes.

The immutable full archive
`ProGPU-native-mixed-boolean-73319afa.zip` has SHA-256
`791BDEC1D4D18124A1AB6A55B866A6F4B4F502EEE1BF5B89E41F7CCEA7043E80`;
the changed-file archive has SHA-256
`7188E717842C04D4BC28708B65709FB17548FF5AE2DA3AEDAD56D42CDFC851BC`.
The exact full archive was hash-verified and rebuilt in the Windows 11 ARM64
Parallels VM. MSVC/Ninja completed the 312-target `/W4 /WX` build, all 11
native/Dawn CTests, both zero-warning managed builds, the live D3D12 C++ and
managed samples, every compute policy, text shaping, native stress, complete
bounded differential profile, and package staging. Forced native compute
failed closed before resource creation on the Parallels adapter; automatic and
forced compatible raster shader, intrinsic SIMD, and scalar-reference paths
all passed their declared contracts. Overlay, ColorDodge, group box blur, and
the mixed boolean pixels were exact. The Microsoft HelloTriangle and
HelloTexture semantic oracles retained SHA-256
`AE1BC0A9B0623BACAB15BE1706FFA3E7FC15E33676A66F05C969C1B86A66FEA3`
and `591CC311F35E3C2612F529C3D4D7061FC93751A9B8614BF588A73599B0AA2790`.

The staged Windows SHA-256 values are
`11DBB21369E7BFB375650AEFFB2A0DD2F21626ED2250FC01F3F583F0D7688009`
for `progpu_native.dll`,
`FC6E3796EB62F435606AC8821D947262FACF7E751FDAC471D55FD2EE6AB2AC64`
for `progpu_native_dawn.dll`,
`39FE55A2EC80559016C004B0187C1A4F2535A2FD7AC25F253A67C7364A52CE02`
for `progpu_native_sample.exe`,
`31A9EFA953990FAF7AE867C4EF07CDEC75609173F031A06B4B7B51B3D85207A9`
for `progpu_native_mil_tests.exe`, and
`416524D7A93D3606A054169B256617BBA73AB941DE0BE36828049F29537AE3C0`
for `progpu_native_internal_tests.exe`. The 130,829-byte host-preserved
terminal evidence file has SHA-256
`F01C054984C9A24B241A203DAAF3390219E165769D524FEF36F3650476A071C5`.
This qualifies correctness on the virtual Parallels D3D12 adapter, not
physical Windows GPU performance.

## DirectX retained-texture boundary

`ProGpuDirectXTexture2D` now implements the framework-neutral
`IProGpuInvalidatingTextureSource` contract for GPU-backed, shader-bindable,
single-layer, single-sample, non-depth textures. A WPF, WinUI, or Avalonia
retained recorder can acquire the existing same-device `GpuTexture` through a
typed lease, draw it into a `GpuPicture`, and let
`GpuPictureNativeSceneCompiler` lower that draw to a native external-image
resource. This route performs no CPU pixel readback, repacking, upload, or
per-item native call, and it does not expose a WebGPU pointer as managed
ownership.

The DirectX resource owns a `SharedGpuTextureSource`; deferred recordings own
borrowed reference-counted leases. Disposing the DirectX wrapper removes the
owner reference but cannot invalidate a texture still used by a retained
picture. The backend texture is released after the final picture/recording
lease is released. `WritePixels`, writable unmap, render/compute/copy
completion, mip generation, and resize advance one content generation and
raise `TextureChanged`, allowing retained hosts to rebuild only dependent
commands.

The boundary fails closed for metadata/CPU-only textures, texture arrays,
multisampled textures, depth/stencil formats, and resources without
`ShaderResource` usage. It never converts those cases to a CPU bitmap. The
consumer-side `DrawingContext.TryRetainTexture(..., requiredContext, ...)` and
native compositor still enforce the WebGPU device domain, live view, format,
dimension, sample count, usage, alpha, and role requirements before binding.
Focused tests cover supported acquisition, unsupported-shape rejection,
invalidation, owner-before-borrower disposal, and pointer-free native MIL scene
compilation.

Exact checkpoint `5bae678a` passes all 3,875 managed tests on Apple ARM64 and
the 3/3 focused lease/invalidation/native-lowering gate in the Windows 11 ARM64
Parallels VM. The Windows run used SDK `10.0.400`, runtime `10.0.11`, and an
immutable commit archive with the pinned `microsoft-ui-xaml` `generic.xaml`
hydrated at SHA-256
`4C4085838721C0AFCB1A9EE17591C0655CDDDADB26D330788E08BCD7F1AF8285`.
Detailed current-user diagnostics for the native-MIL external-image test report
`Parallels Display Adapter (WDDM)`, backend `D3D12`, and the test passes in
480 ms. This is direct ARM64 D3D12 correctness evidence for the ownership and
lowering seam, not physical-adapter performance evidence.

## Native MIL live MediaPlayer checkpoint

ProGPU `f5f7988b` implements canonical WPF `DrawVideo` (`0x4b`) and
`DrawVideoAnimate` (`0x4c`) over resource type 1 (`TYPE_MEDIAPLAYER`). The new
`progpu_native_mil_channel_set_media_player_external_image` sideband publishes
only the live frame dimensions. Scene compilation emits a semantic external-
image resource with no pixel payload or process pointer; the existing
`NativeCompositor.BindSceneExternalImages` table supplies the current same-
device `GpuTexture` view immediately before scene installation. Static and
typed animated destination rectangles share the ordinary image shader,
sampling, transform, clip, opacity, damage, and backend paths.

External MediaPlayer resources are emitted first in ascending MIL-handle order,
which gives the host deterministic scene resource IDs `1..N`. The binding
generation is the requested semantic-scene generation. Missing sideband state,
wrong resource types, malformed packets, nonfinite/negative rectangles, invalid
animation handles, zero dimensions, and textures from another device fail
closed. Replacing the binding table retains the new views transactionally; the
managed host keeps each typed lease alive until the next table replacement.
There is no CPU readback, RGBA repack, staging upload, or per-frame bitmap
resource mutation.

The neutral `PortableMediaPlayerFrame`/`IPortableMediaPlayerSource` contract
lives in `ProGPU.Wpf.Interop`, so WPF, WinUI, Avalonia, and media producers can
publish the same lease-backed frame without host-specific reflection. The
native MIL test verifies static and animated video commands, external-resource
identity, zero payload bytes, dimensions, generation, and fail-closed sideband
validation. The managed packet test locks the exact 48-byte WPF records.
Apple Silicon validation passes the native MIL CTest and managed packet test;
Ubuntu 24.04 ARM64 qualification from exact archive `bb2313ab` builds the full
native shared library with GCC 13.3.0, passes the native MIL CTest 1/1, and
exports `progpu_native_mil_channel_set_media_player_external_image`. Qualified
Linux `libprogpu_native.so` SHA-256 is
`17a2e5fd74de64a3697b98b41245a747c75850292573407346cda8671e7dba3a`.
Windows 11 ARM64 qualification from the same archive builds the full DLL with
MSVC 19.44.35228.0, passes the native MIL CTest 1/1 in 3.61 seconds, and
exposes the same new C export. Qualified Windows `progpu_native.dll` SHA-256 is
`0eeb5e34086b753ac6abd93192c3def9aaec9559fb71cca053e33c7fdfbe258d`.

This checkpoint covers one packed RGBA/BGRA same-device plane. D3DImage shared-
surface import, keyed synchronization, planar NV12/P010 video, color-space/HDR
metadata, and protected content remain separate typed interop work; none may
fall back through CPU pixels.

## Native MIL external BitmapSource checkpoint

ProGPU `cfebce57` extends the same zero-copy semantic resource contract from
MediaPlayer to canonical WPF `TYPE_BITMAPSOURCE` (95). The new
`progpu_native_mil_channel_set_bitmap_source_external_image` ABI binds only
validated dimensions. During scene compilation, external bitmap and media
descriptors are merged and sorted by their globally unique MIL handles before
any payload-backed image is emitted. That makes resource IDs deterministic
across mixed Win2D/image/video scenes while preserving the previous ordering
when no external bitmap exists.

An external bitmap draw reuses the ordinary image shader, sampling, transform,
clip, opacity, damage, and backend paths. Its semantic image resource has zero
payload bytes; the host must bind a same-device texture lease for the scene
generation before installation. Rebinding the handle to copied RGBA8 pixels or
back to an external image replaces the storage mode transactionally. Missing
state, wrong resource type, zero/oversized dimensions, and duplicate external
identity fail closed. Native CTest coverage verifies copied-to-external mode
replacement, dimensions, row bytes, generation, zero payload, and invalid
bindings; the managed native backend builds with zero warnings.

The base native export allowlist pins both
`progpu_native_mil_channel_set_bitmap_source_external_image` and
`progpu_native_mil_channel_set_d3d_image_external_image`. Every Linux, macOS,
and Windows native package lane therefore rejects an implementation that
compiles these canonical sidebands but omits them from the public ABI.

The exact tracked delta from the prior qualified archive through documentation
head `4ece2969` rebuilds and relinks on both Parallels guests. Ubuntu 24.04
ARM64 passes the native MIL CTest 1/1 in 0.03 seconds, exposes the new export,
and produces `libprogpu_native.so` SHA-256
`c7633cc318977e69373c5d26d0bceed24de86d52bfe8b6506fe731ad14b24f54`.
Windows 11 ARM64 with MSVC 19.44.35228.0 passes the same CTest 1/1 in
2.50 seconds, exposes the new export, and produces `progpu_native.dll` SHA-256
`fc627fff1240a9f06ae4e785101f9052b9dac8dbe600ae1a331d094087d79fdf`.

This is the native compositor endpoint used by portable Win2D `CanvasBitmap`
and synchronized D3DImage/Direct2D providers. It does not itself import
an `ID2D1*`, D3D9 surface, or DXGI shared handle; those platform providers must
publish the already-validated ProGPU texture through the typed lease contract.

## Canonical D3DImage checkpoint

ProGPU `72c9d794` and `20918afb` implement canonical `TYPE_D3DIMAGE` (97),
`MilCmdD3DImage`, and `MilCmdD3DImagePresent` on the native C++ channel. The
portable packet writer preserves WPF's exact 24-byte update and 16-byte present
layouts while writing zero for the process-local COM pointers and event
handle. Nonzero pointer/event values fail closed. A Windows adapter must first
import its D3D9/DXGI resource into an `IProGpuTextureLeaseSource`; lease acquire
and release own keyed-mutex, shared-fence, and backend-transition semantics.

`PortableD3DImageFrame` carries validated dimensions, a nonzero retained
content version, and the neutral backend image. The typed
`progpu_native_mil_channel_set_d3d_image_external_image` sideband binds that
descriptor to the canonical resource. BitmapSource, MediaPlayer, and D3DImage
external images are sorted together by MIL handle before semantic resource IDs
are assigned, so the pointer-free scene and host lease table stay deterministic.
The native regression covers update/present generations, zero-payload external
image drawing, invalid type/dimensions/version, and raw handle rejection. The
Apple Silicon native MIL CTest passes, the shared library links and exports the
new ABI, and the managed canonical packet test passes.

The exact `1f1d921b` source checkpoint also rebuilt and relinked the focused
native targets on both Parallels guests. Ubuntu 24.04 ARM64 passes the native
MIL CTest 1/1 in 0.02 seconds and exports
`progpu_native_mil_channel_set_d3d_image_external_image`; SHA-256 is
`21798600a4c5d4f4a58d6ea456b5919fa782164d4ebf0ab9f40f1949dcb0ea2e`
for `libprogpu_native.so` and
`c485873cf4d532ab956a44ed729a399486805ba83d815b624fe1c64c8844f3bb`
for `progpu_native_mil_tests`. Windows 11 ARM64 rebuilds under MSVC, passes the
same CTest 1/1 in 7.28 seconds, and exposes the ABI as export ordinal 36;
SHA-256 is
`81f1078e89d9f9f8e4bfdcead25ebc8a84e3d6c425350c865217ff74cb50bd5d`
for `progpu_native.dll` and
`d94382db3f1087573615c91ff983cd2343b6144b68c4f3db160f7c59f0f8568f`
for `progpu_native_mil_tests.exe`.

## Windows Direct2D COM producer checkpoint

ProGPU `59045316` adds the separate Windows-only
`progpu_native_direct2d` library. It creates genuine system
`ID2D1Factory1/2`, `ID2D1Device/1`, `ID2D1DeviceContext/1`, and
`ID2D1Bitmap/1` objects over a BGRA8-unorm premultiplied D3D11 target. The
texture exposes an NT shared handle and keyed mutex; the versioned descriptor
also carries actual adapter LUID, DPI, dimensions, initial synchronization
keys, software-adapter state, and content version. Callers may select an exact
adapter, hardware with explicit WARP fallback, or forced WARP.

This is a genuine Direct2D/DXGI producer for the existing typed D3DImage lease
boundary, not a fake `d2d1.dll` or a partial COM vtable implementation. COM
pointers remain confined to the Windows header and process. The portable MIL
packet retains zero pointers and zero event handles.

ABI v9 and package `ProGPU.Direct2D` bind that producer lifecycle to Dawn's
same-adapter shared-texture import. `ProGpuDirect2DSurface` owns the native
surface through Dawn, implements `IProGpuContextTextureLeaseSource`, and
publishes `TextureChanged` only after one transactional native
`BeginDraw`/`EndDraw` has returned ownership to WebGPU. The draw session exposes
a safe caller-owned reference to the genuine `ID2D1DeviceContext1`; active
deferred ProGPU leases reject producer entry. Both sides use the Dawn-qualified
zero-key mutex profile while a separate monotonic content version records
successful producer writes. `ProGpuDirect2DD3DImageSource` adapts this texture
source to the neutral `IPortableD3DImageSource` and
`IPortableInvalidationSource` contracts, so it flows through the already-
qualified D3DImage sideband with no CPU copy or new scene resource. It fails
closed while content version is zero and does not own or dispose the wrapped
surface.

The native owner also creates a genuine WinRT `IDirect3DDevice` from its exact
`IDXGIDevice` via `CreateDirect3D11DeviceFromDXGIDevice`. The regression
unwraps it through `IDirect3DDxgiInterfaceAccess` and requires the original
`ID3D11Device` identity, establishing Win2D `CanvasDevice` activation without
a second device or adapter-crossing copy. The registered CanvasDevice factory's
official `ICanvasFactoryNative::GetOrCreate` now wraps the provider's exact
`ID2D1Device1` as a real CanvasDevice and its exact target `ID2D1Bitmap1` as a
real CanvasRenderTarget. The managed outer producer scope transfers the keyed
mutex from Dawn to Win2D without beginning a competing native Direct2D context,
then restores Dawn ownership and invalidates the shared image after the caller
disposes its CanvasDrawingSession. It never owns
the caller's apartment initialization and never searches for or loads the
Win2D DLL; missing package registration and missing WinRT initialization are
separate typed failures.

ABI v6 completes the device/target reverse round trip through the official
`ICanvasResourceWrapperNative::GetNativeResource` contract. Typed managed
methods return caller-owned exact `ID2D1Device1` and `ID2D1Bitmap1` references;
the provider supplies the cached CanvasDevice and target DPI rather than asking
managed callers to compose raw COM arguments. Canonical `IUnknown` comparison
proves both returned resources are the original provider objects, so wrapping
does not introduce a second Direct2D domain or copy.

ABI v7 adds genuine device-context-domain `ID2D1SolidColorBrush` creation and
reusable native device-domain Win2D wrap/unwrap operations. Public managed
methods keep the raw generic seam internal, enforce solid-brush handle kinds,
and protect each borrowed pointer with `DangerousAddRef`. The official Win2D
projection returns a real `CanvasSolidColorBrush`; reverse unwrapping preserves
the original brush's canonical `IUnknown` identity, and the packaged oracle
uses the projected brush in an actual `CanvasDrawingSession.FillRectangle`
call rather than validating metadata alone.

ABI v8 adds zero-copy pinned managed stop spans, genuine
`ID2D1GradientStopCollection1` creation with explicit color-space/precision/
extend/interpolation state, and genuine linear/radial brush creation with typed
geometry, opacity, and affine transforms. The collection safe handle is
kind-checked and protected with `DangerousAddRef` across creation. Both brushes
reuse the v7 generic native Win2D seam while public managed methods remain
kind-specific. The package oracle reads projected stops and geometry, proves
exact reverse identity, and draws both resources.

ABI v9 adds genuine rectangle, rounded-rectangle, ellipse, path,
transformed, and combined Direct2D geometries. A blittable batched path ABI
preserves figure fill/close and line/quadratic/cubic/arc semantics without
per-segment P/Invoke. The managed surface also consumes the same neutral
`PortablePrimitiveGeometry` and `PortableGeometryPath` DTOs used by source-
built WPF. Boolean combinations execute through Direct2D, and kind-checked
Win2D wrapping projects the result as a real `CanvasGeometry` while reverse
unwrapping must return the original canonical `ID2D1Geometry` identity.

Dawn ownership transitions run outside the Direct2D provider state lock. This
preserves one lock order when a render submission already owns the WebGPU
render lock and requests a texture lease, and prevents the producer thread from
holding `_gate` while entering Dawn. Managed binding uses source-generated
`LibraryImport` and `SafeHandle`; reflection, dynamic native loading, delegate
synthesis, readback, and repacking are absent.

The archived ABI v1 baseline on Windows 11 ARM64 MSVC `/W4 /WX` builds the
provider and regression. The test
queries every advertised base/versioned COM interface, verifies multithread
protection and bitmap target state, performs a real Direct2D clear and filled
rectangle, reopens the NT handle, and completes the keyed-mutex sequence
`0 -> 1 -> 2 -> 3`. CTest passes 1/1 in 7.74 seconds and all eight C exports
are present. SHA-256 is
`f115ea21f43c218444a2d9fd9ebb622e073a5b3cafb52ec1745990e7984e498c`
for `progpu_native_direct2d.dll` and
`cab7f76311cd5115a0f8f84ee680115eb6481c6842eb45a85eea0633c08292fc`
for `progpu_native_direct2d_tests.exe`. ABI v5 extends the native test with
nested/unmatched draw rejection, the zero-key Dawn handoff, and generic
GUID-based COM `QueryInterface` success plus `E_NOINTERFACE` failure, and
optional registered Win2D CanvasDevice and CanvasRenderTarget wrapping. The
ABI v5 Windows qualification verified all 14 exports and staged
`progpu_native_direct2d.dll` for both Windows RIDs. ABI v5 at exact
implementation commit `f751cd0b` was independently compiled and executed in
the Windows 11 ARM64 Parallels VM with MSVC 19.44 and Windows SDK 26100. The
regression exits zero and the exact 14-export audit passes, including the
CanvasDevice and CanvasRenderTarget wrapper exports. SHA-256 is
`d9224ee806635ba3086d299912bb7bd2d9cf52a7ef56451ae54656058e7175d8`
for the DLL and
`0e8fc690ba5bd4a7a40d461d1691f8efd32dbef7338ae90a1635ccc5b0f2e02d`
for the executable. That isolated run had no registered Canvas/Win2D AppX
package and therefore also qualifies the explicit runtime-unavailable path for
both typed wrappers.

The package-deployed success gate is separately qualified from exact ProGPU
source `d201494a` in the same Windows 11 ARM64 Parallels VM. Its full-trust MSIX
contains official Microsoft Win2D 1.4.0, projects the returned COM pointer as a
real `CanvasRenderTarget`, and creates a real `CanvasDrawingSession`. A clear
plus 48x48 fill on a 64x64 target produces an exact transparent corner and
center ARGB `(255,32,96,192)` through validation-only `GetPixelColors()`;
content version advances `0 -> 1`, native wrapping returns `S_OK`, and the
reported adapter is `Dawn D3D12`. The gate requires a pre-provisioned package-
signing certificate thumbprint, verifies private-key and trust stores, and
never mutates certificate trust. Set `PROGPU_RUN_REAL_WIN2D_INTEGRATION=1` to
include it in the complete Windows native build lane. A merely booted VM or a
stalled Guest Tools login is not recorded as a pass.

ABI v6 at exact ProGPU `1be881ca` was independently rebuilt in that guest with
the same MSVC/SDK toolchain and `/W4 /WX`. The native regression exits zero,
and the current exact 15-export audit passes. SHA-256 is
`160037e11339ec6ad38a3cc2bc121ca6da5ba73ad3fd25c29d9eb8d030a132d9`
for the DLL and
`46884523bd6ba4700c8113ac9df2f09689b134d429327a07d9fcd083511159ec`
for its test executable. The signed official-Win2D 1.4.0 package gate reports
both native device and bitmap identity matches as true, while preserving the
transparent corner, center ARGB `(255,32,96,192)`, content version `0 -> 1`,
and `Dawn D3D12` evidence. Broader Win2D resource families must pass this same
forward-wrap/reverse-unwrap identity gate.

ABI v7 at exact ProGPU `4f5e614f` was independently rebuilt in the same guest
with MSVC 19.44, Windows SDK 26100, and `/W4 /WX`. The native regression exits
zero and the exact 18-export audit passes. SHA-256 is
`6c35ac88938fbdc483b6a932d1180a1fd041ead3097c4ef51bce2b31ad5e301c`
for the DLL and
`edb201be9ab6f1783d679bcafd8872c3f5c1495bcc9b8738c3235b5177f44d42`
for its test executable. The signed official-Win2D 1.4.0 package gate reports
the real `Microsoft.Graphics.Canvas.Brushes.CanvasSolidColorBrush` type,
exact native solid-brush identity, exact brush and center ARGB
`(255,224,48,96)`, a transparent corner, content version `0 -> 1`, and
`Dawn D3D12`. Device and bitmap identity remain exact in the same run.

ABI v8 at exact ProGPU `8e62b5e5` was independently rebuilt in that guest with
MSVC 19.44, Windows SDK 26100, and `/W4 /WX`. The native regression exits zero
and the exact 21-export audit passes. SHA-256 is
`c291eac6efc959acd39ba1bdea03d80e8e9025b001c145c13b4c174f003ffc96`
for the DLL and
`712ba33d7cd121bb8a7d3c68585c3895c00ad5575e4cdc64971783857d2020a3`
for its test executable. The signed Win2D 1.4.0 oracle reports real linear and
radial gradient brush types, exact native identities, projected two-stop and
geometry metadata, exact solid/linear/radial sample ARGB values
`(255,224,48,96)`, `(255,32,160,224)`, and `(255,64,192,96)`, a transparent
corner, content version `0 -> 1`, and `Dawn D3D12`.

ABI v9 at exact ProGPU `0b96328e` was independently rebuilt in that guest with
MSVC 19.44, Windows SDK 26100, and `/W4 /WX`. The native regression exits zero
and the exact 27-export audit passes. SHA-256 is
`83a67ee9007902ca477bada185ea99d298f879b8798b91aad18d4bf996eda29e`
for the DLL and
`eb9cdf5346e8f72ae49b2486051298a7bbce44bd83bde36b554dee50d7b8f0fa`
for its test executable. The signed Win2D 1.4.0 oracle built from exact app
commit `3a058643` reports a real `CanvasGeometry`, exact reverse native
identity, and boolean-exclude geometry sample ARGB `(255,240,208,32)` while
the hole retains solid ARGB `(255,224,48,96)`. The transparent corner,
solid/linear/radial samples, content version `0 -> 1`, and `Dawn D3D12` remain
unchanged. Stable JSON plus best-effort stage evidence survives MSIX cleanup.

ABI v16 extends the same Windows COM producer with a genuine shared
`IDWriteFactory3`, caller-owned `IDWriteTextFormat1` resources, and typed
`ID2D1RenderTarget::DrawText` submission for both shared-surface and
command-list transactions. Font family, locale, and text enter as explicit
UTF-16 spans; the creation path alone NUL-terminates family/locale for
DirectWrite, while the hot draw path consumes the pinned caller span directly
without copying, readback, repacking, reflection, or per-glyph calls. The
device-independent Win2D factory path wraps and unwraps the exact format with
null CanvasDevice and zero DPI so official `CanvasTextFormat` can consume the
provider object without introducing another resource domain. Invalid state or
unknown options fail closed. The native regression covers format metadata,
pre-draw rejection, a real text command, and deferred EndDraw success; the
export audit grows from 45 to exactly 47.

Exact ProGPU implementation `6a87f320` was rebuilt twice with `/Brepro` in the
Windows 11 ARM64 Parallels guest using MSVC 19.44, Windows SDK
10.0.26100.0, and `/W4 /WX`. Both warning-clean builds produce identical
artifacts. The focused native regression exits zero and `dumpbin` matches the
47-export allowlist. SHA-256 is
`6BC503DBE9BB5506B709CA6D97D8B78F82F302BF33BCE4352B104722DA05FCDC`
for `progpu_native_direct2d.dll` and
`8C634D6EC4963786D87D5E87BEE5FBD83F6B843A8BCE535E0E9149CB806FCDC5`
for `progpu_native_direct2d_tests.exe`. The native run qualifies genuine
DirectWrite factory/format creation and Direct2D text submission. Official
Win2D `CanvasTextFormat` projection remains a distinct signed-package oracle;
the native evidence is not used as a substitute for that gate.

ABI v17 adds retained genuine `IDWriteTextLayout4` creation from explicit
UTF-16 text, an existing typed format, and positive finite layout bounds.
DirectWrite owns the retained text copy after the synchronous creation call;
the provider does not retain the caller span or create a parallel text buffer.
Both shared-surface and command-list transactions submit the reusable layout
through `ID2D1RenderTarget::DrawTextLayout`. The Win2D factory path treats a
layout as device-associated before testing its inherited text-format
interface, supplies the surface's exact CanvasDevice, and reverse-unwraps the
exact `IDWriteTextLayout4`. This matches the pinned Microsoft
`ResourceManager`/`CanvasTextLayout` implementation and avoids accidentally
using the null-device rule that is correct only for `CanvasTextFormat`.
Invalid dimensions, origins, options, resource kinds, and draw state fail
closed. The native export allowlist grows from 47 to exactly 49.

ABI v18 adds mutable typed range formatting to that retained layout. One
pointer-free descriptor selects font size, numeric weight, style, stretch,
underline, strikethrough, and an optional separately validated `ID2D1Brush`
drawing effect for a nonempty UTF-16 range. The managed API pins no strings,
keeps the layout and optional brush alive across the synchronous native call,
and rejects unknown flags, overflow, malformed selected values, and non-brush
effects before interop. The native regression reads every selected value and
canonical drawing-effect identity back from `IDWriteTextLayout4`; the official
Win2D gate observes the same state through `CanvasTextLayout`, mutates a second
range through Win2D, and draws the shared object. The allowlist grows from 49
to exactly 50 exports without adding reflection, CPU text fallback, readback,
or per-character native calls.

ABI v19 adds genuine device-independent `IDWriteTypography` resources and
retained-layout OpenType feature assignment. A bounded pinned span of typed
name-tag/parameter pairs crosses managed/native once, DirectWrite copies each
feature into its owned typography object, and `SetTypography` applies that
object to a nonempty UTF-16 range. The Win2D resource seam uses the correct
null-device/zero-DPI rule for official `CanvasTypography`, validates the feature
array through the projection, and reverse-unwraps the exact native identity.
Malformed tags, feature counts, ranges, and COM kinds fail closed. The export
allowlist grows from 50 to exactly 52 without reflection, text readback, or
per-feature managed/native calls.

ABI v20 adds the genuine DirectWrite font resource boundary required by WPF's
already-shaped glyph runs. The typed provider resolves a system face as an
`IDWriteFontFaceReference`, creates `IDWriteFontFace5`, and submits pinned
glyph-index, optional advance, and optional offset spans through
`ID2D1DeviceContext::DrawGlyphRun` in either surface or command-list draw
transactions. The operation neither reshapes text nor copies/readbacks pixels,
and rejects mismatched spans, non-finite state, invalid bidi levels, wrong COM
kinds, oversized runs, and inactive draws. Official Win2D `CanvasFontFace`
wrapping follows its documented device-independent
`IDWriteFontFaceReference` mapping and must preserve exact COM identity. The
allowlist grows from 52 to exactly 55 exports. This is the native MIL text seam
used before adding the color-glyph enumerator/paint-tree layers; it does not
replace the portable cross-platform glyph DTO path.

Windows MSVC qualification is recorded by GitHub Actions Build run
`33326634929`, job `99297867722`. The provider and native test compile/link
under the warning-as-error lane, `progpu_native_direct2d_tests` passes in
0.16 seconds, the complete native CTest suite passes 11/11, and the exact
55-export Direct2D allowlist is accepted.

ABI v21 extends that shaped run with GPU-native color-font rendering. It
prefers `ID2D1DeviceContext7::DrawGlyphRunWithColorSupport`; down-level Windows
10 uses `IDWriteFactory4::TranslateColorGlyphRun` and dispatches each returned
representation through `ID2D1DeviceContext4` bitmap, SVG, or outline drawing.
Only `DWRITE_E_NOCOLOR` selects monochrome rendering, while a missing required
COM interface or other translation failure fails closed. No font bitmap/SVG
payload crosses into managed or CPU fallback code. A typed diagnostic reports
the selected context7, translated-context4, or no-color path, and the exact
allowlist grows from 55 to 56 exports.

Windows MSVC qualification is recorded by GitHub Actions Build run
`33327156224`, job `99299265980`. The provider and regression compile/link
under the warning-as-error lane, `progpu_native_direct2d_tests` passes in
0.14 seconds, all 11 native suites pass, and the exact 56-export allowlist is
accepted.

ABI v22 adds genuine `ID2D1SvgDocument` creation/drawing through
`ID2D1DeviceContext5`. The UTF-8 source is exposed by a bounded borrowed
`IStream` for the synchronous parse, so there is no retained caller pointer or
intermediate XML buffer. Surface and command-list draws temporarily apply the
requested viewport and origin, restore both states, and reject foreign-factory
resources or inactive draws. Official Win2D `CanvasSvgDocument` wrapping and
reverse unwrapping preserve canonical COM identity. This Windows-native SVG
resource lane complements rather than replaces ProGPU's portable retained SVG
path lowering, and the exact allowlist grows from 56 to 58 exports.

Windows MSVC qualification is recorded by GitHub Actions Build run
`33328289063`, job `99302278126`. The provider and native regression
compile/link under the warning-as-error lane, the focused Direct2D test passes
in 0.49 seconds, all 11 native suites pass, and the exact 58-export allowlist
is accepted.

ABI v23 makes loss of that Direct2D/D3D11 resource domain explicit. Every
surface and managed COM safe handle carries one monotonic resource generation.
The native provider registers the optional `ID3D11Device4` removal event,
polls it without blocking, confirms its HRESULT with
`ID3D11Device::GetDeviceRemovedReason`, and persistently classifies Direct2D's
`D2DERR_RECREATE_TARGET` as terminal too. The managed owner invalidates its
shared generation token before another safe-handle operation, raises one typed
`DeviceLost` notification, reports the same terminal state to Dawn, and
requires a new device domain plus rebuilt resources. Cross-generation resource
use fails before entering COM. The allowlist grows from 58 to 59 exports;
physical adapter-removal/recreation remains an explicit Windows integration
gate rather than a synthetic success claim.
Exact implementation `d67fe1bf` is qualified by GitHub Actions Build run
`33329548704`, dedicated MSVC job `99305585595`: the warning-as-error provider
and regression compile/link, the focused Direct2D test passes in 0.15 seconds,
all 11 configured native suites pass, and the exact 59-export allowlist is
accepted. ClangCL Windows job `99305585623` also passes the focused test in
0.14 seconds and all 12 native suites before an unrelated later managed Dawn
readback loses Microsoft Basic Render Driver; that downstream software-D3D12
failure is not Direct2D qualification evidence.

ABI v24 adds a typed native geometry-analysis seam for MIL bounds and hit-test
parity. The isolated Direct2D provider now calls genuine `ID2D1Geometry`
`GetBounds`, `GetWidenedBounds`, `FillContainsPoint`, `StrokeContainsPoint`,
`CompareWithGeometry`, `ComputeArea`, `ComputeLength`, and
`ComputePointAtLength`. Managed resources must match the surface's monotonic
generation, optional stroke styles remain strongly kind-checked, and every
borrowed safe handle is protected across the native call. Rectangle results
are converted from Direct2D edge coordinates into the portable size form;
invalid scalar, point, transform, and tolerance inputs fail closed with
zeroed outputs. The exact allowlist is 67 exports. This lets the Windows MIL
bridge use native Direct2D analysis without reflected WPF geometry shapes or a
CPU tessellation detour. Simplify/outline/widen/tessellation realization sinks
remain explicit follow-up work.
Exact ProGPU implementation `13f6906b` is Windows-qualified by GitHub Actions
Build run `33330942215`: dedicated MSVC job `99309300180` passes the focused
Direct2D test in 0.25 seconds and all 11 native suites under warning-as-error,
while ClangCL x64 job `99309300268` passes it in 0.14 seconds and all 12 native
suites before the unrelated later Dawn software-adapter loss.

The Windows renderer gate distinguishes hardware qualification from the
software-only Microsoft Basic Render Driver lane. Hardware and Parallels run
the complete managed retained sample at 640x360. The Basic Render Driver
compiles the same complete 16-command picture and validates its full native
stream plus exact compiler/parser counters without submitting it. GPU
execution uses a bounded four-source-command analytic managed scene at
320x180 and 0.5 DPI, covering nested/direct solid rectangles and a linear
gradient in one coalesced retained batch. It must still submit once, preserve
zero uploads on the second frame, read back, and pass solid, gradient, outside,
and background pixel probes. Repeated full managed path/glyph coverage runs
spent about 80 seconds before deterministic device loss even at half target
size, proving resolution was not the controlling cost. No CPU renderer or
retry is substituted; the full C++ D3D12 sample still runs on the software
adapter, and full managed GPU execution remains mandatory on hardware and
Parallels.

The distinct mixed-picture benchmark applies the same explicit adapter
qualification. Microsoft Basic Render Driver compiles and transactionally
updates its full 384-item native stream, verifies exact command/draw counters
and retained snapshot reuse, then runs a live one-item managed/native pixel
differential after one warm frame establishes retained glyph state. Its dense
managed path and, independently, its repeated full native-only profile can
remove the CPU-only D3D12 device. Parallels retains the full 384-item,
four-warmup/eight-iteration C++ profile plus bounded live parity, while
hardware Windows retains the complete 384-item differential. The Basic check
still initializes D3D12 and submits both renderers for pixel comparison; it is
not a CPU substitute or a reduced full-stream validation.

The portable Win2D oracle separately preserves its complete frame on every
backend. Microsoft Basic Render Driver partitions independent feature groups
with `CanvasDrawingSession.Flush()` while retaining automatic GPU-first
selection, every original pixel probe, and the final D3D12/Metal/Vulkan
comparison. There is no intermediate readback or CPU composition. Partitioning
exposed a native retained-target defect for isolated layers: Canvas now calls a
typed full-target-preserve entry and the C++ isolated-layer root pass loads the
existing attachment. Partitioned Metal is byte-identical to the established
full frame and reports 17+2 native draws after the additional boundaries.

The same hosted CPU-only adapter explicitly defers the two forced
signed-winding compute profiles. Its inline four-rectangle rerasterization
reached the final readback only after roughly 100 seconds and then lost the
device, after every preceding differential had passed. Exact native
validators and compiler contracts still run there; forced inline and staged
GPU execution remain required on Windows Parallels/hardware, Metal, and
Vulkan. This is a named CI-adapter limitation, not an automatic-policy change
or a CPU fallback.

ABI v25 closes that follow-up without publishing arbitrary COM sink pointers.
The provider materializes simplify, outline, and widen results as genuine
same-factory `ID2D1PathGeometry1` resources. Its tessellation sink writes
directly to a managed caller span, counts the full immutable result, and
returns a typed insufficient-buffer result for deterministic size-query/retry;
there is no per-triangle allocation or callback into managed code. Filled and
stroked `ID2D1GeometryRealization` resources are created and drawn through
`ID2D1DeviceContext1` in both target and command-list producer scopes. Every
geometry, stroke style, realization, and brush remains generation- and
kind-checked, while invalid options, transforms, tolerances, widths, buffers,
and producer state fail closed. The exact allowlist becomes 74 exports.
Final checkpoint `9dc74d09` passes the managed aggregate lane in Build run
`33332388195`, job `99313260684`. Dedicated MSVC job `99313260762` compiles and
links the provider under warning-as-error, passes the Direct2D test in 0.14
seconds, and passes all 11 native suites. Corrected native-identical commit
`84ece34c` passes ClangCL x64 job `99312705172` in 0.15 seconds and all 12
native suites; the final commit changes only managed operation-label scope.

ABI v26 then adds typed immediate Direct2D vector drawing without creating a
second scene implementation. Both shared-target and command-list producer
sessions expose clear, affine transform get/set, line, rectangle,
rounded-rectangle, ellipse, and geometry fill/stroke operations. The managed
owner validates resource kind and monotonic generation and holds each borrowed
`SafeHandle` reference across the call; the native boundary repeats finite
geometry checks and `QueryInterface` validation. This gives the Windows MIL
and Win2D lanes a native `ID2D1DeviceContext1` rendering seam while Metal,
Vulkan, Linux, macOS, and browser hosts continue to use the same portable
ProGPU retained vector pipeline. No pointer-shaped COM state enters MIL or
WebGPU, no CPU readback/repack is introduced, and the exact Direct2D export
allowlist grows from 74 to 86. Command-list coverage exercises all operations,
transform round-trip, and an exact BGRA shared-texture readback pixel;
portable managed contracts pass 5/5 with zero warnings. Corrected checkpoint
`f1b1ca18` is qualified by Build run `33333671491`, dedicated MSVC job
`99316705077`: the provider and pixel regression compile/link under
warning-as-error, the focused Direct2D test passes in 0.16 seconds, all 11
native suites pass, and the exact 86-export allowlist is accepted. Exact
clip/layer cross-ordering is reserved for a unified LIFO
draw-state ABI rather than being approximated with a separate clip counter.

ABI v27 supplies that unified draw-state ABI. A bounded, allocation-free native
stack tags every layer and axis-aligned clip, so mixed scopes unwind in exact
reverse order during normal disposal, failed completion, and destruction.
Cross-kind pops return `DrawingStateMismatch` without consuming state. Managed
layer and clip ref scopes use the same depth sequence and fail before native
entry when disposal is out of order. Typed bitmap/image draws are added on top:
bitmap calls preserve optional destination/source rectangles, opacity,
interpolation, and a complete 4x4 perspective matrix; image calls preserve
optional offset/rectangle plus all Direct2D interpolation and composite modes.
All COM resources stay kind/generation checked and borrowed under safe-handle
protection. No COM identity enters MIL, no CPU copy or command-array allocation
is introduced, and portable hosts keep using the shared retained vector/image
pipeline. The exact allowlist becomes 90 exports. The native command-list test
uses the new clip, bitmap, and image operations, rejects a layer pop while a
clip is above it, and retains the exact BGRA shared-texture oracle. Managed
contracts pass 5/5 with zero warnings. Exact checkpoint `10ef4c1a` is qualified by GitHub Actions
Build run `33334553038`, dedicated MSVC job `99319045125`: warning-as-error
compile/link succeeds, the focused Direct2D regression passes in 0.16 seconds,
all 11 native suites pass in 1.05 seconds, and the exact 90-export gate is
accepted.

ABI v28 at checkpoint `ac10d4af` adds the Direct2D drawing-state properties
needed by native Win2D drawing sessions without introducing a second state
model. Both target and command-list sessions expose typed geometry and text
antialiasing, primitive blend, DIP/pixel unit mode, two 64-bit tags, and DPI
get/set operations. Each call requires the active producer. Enum values and DPI
are checked in managed and native code; `(0, 0)` retains Direct2D's exact
reset-to-96-DPI meaning while half-zero, negative, infinite, and NaN inputs fail
closed. The native test round-trips all state and restores defaults before the
existing pixel oracle. The exact allowlist becomes 102 exports, the managed
contracts pass 5/5, and the package build has zero warnings. Windows execution
is qualified by GitHub Actions Build run `33335230522`, dedicated MSVC job
`99320851539`: warning-as-error compile/link succeeds, the focused Direct2D
regression passes in 0.17 seconds, all 11 native suites pass in 1.07 seconds,
and the exact 102-export gate is accepted.

ABI v29 at checkpoint `2086632e` adds typed mutable Direct2D brush state for
native Win2D resource parity. Common opacity/affine transform, solid color,
linear endpoints, and radial center/origin/radii can be set and queried without
reflection, raw managed COM calls, or command allocation. Resource generation,
interface kind, finite coordinates/transforms, opacity, and radii are validated
on both boundaries, with `DangerousAddRef` protecting every borrowed handle.
The native regression restores the solid brush before the existing exact-BGRA
oracle. The allowlist becomes exactly 110 exports; managed contracts pass 5/5
and the package builds with zero warnings. Build run `33336026310`, dedicated
MSVC job `99322989531`, qualifies the checkpoint: warning-as-error compile/link
succeeds, the focused Direct2D regression passes in 0.14 seconds, all 11 native
suites pass in 1.02 seconds, and the exact 110-export gate is accepted.

ABI v30 at checkpoints `96735d95`/`058f6f1f` adds live bitmap/image-brush mutation on the
same typed resource domain. Bitmap-brush sampling/tiling and nullable bitmap
binding, plus image-brush source rectangle/sampling/tiling and nullable image
binding, round-trip without CPU pixels or arbitrary COM calls from managed
code. Getters return caller-owned interfaces and native tests prove exact COM
identity, null detach semantics, and restored state before later pixel and
Win2D gates. Managed generation/kind checks and `DangerousAddRef` pair with
native `QueryInterface` validation for creation and mutation. The exact
allowlist becomes 118 exports; portable contracts pass 5/5 and the package
builds with zero warnings. Corrected checkpoint `058f6f1f` is qualified by
Build run `33336912843`, dedicated MSVC job `99325361848`: warning-as-error
compile/link succeeds, the focused Direct2D regression passes in 0.18 seconds,
all 11 native suites pass in 1.33 seconds, and the exact 118-export gate is
accepted.

ABI v31 at checkpoint `2d24157d` adds non-readback bitmap metadata, bounded
caller-span upload, and same-generation GPU bitmap-to-bitmap copy. The typed
descriptor exposes pixel/DIP dimensions, DPI, format, alpha mode, and bitmap
options. Upload validation proves the source pitch and full byte extent before
pinning the caller span for the synchronous call; copy validation proves both
rectangles and rejects canonical COM self-identity. Native coverage draws the
mutated bitmap into the existing shared target and checks distinct exact BGRA
pixels for the upload and GPU copy. No CPU readback, staging fallback, repack,
reflection, or managed command allocation is added. The exact allowlist becomes
121 exports; portable contracts pass 5/5 and the package builds with zero
warnings. Immutable archive SHA-256
`CBEF4F7F71DE3B61B43CE0A1C2C14941B0589C6440C92F0CD7553FA4DBAE82E3`
is qualified in the Windows 11 ARM64 Parallels VM: MSVC 19.44/Windows SDK
10.0.26100.0 compile and link under `/W4 /WX`, the exact 121-export comparison
passes, and the focused live regression passes 1/1. The resulting DLL SHA-256
is `07751974494C643CF899F60988AED1335EC10BF493E26142099528D4041B7C1C`.
Build run `33337753262`, x64 native job `99327677774`, independently passes the
Direct2D regression in 0.17 seconds and all 12 native suites in 1.14 seconds;
its later managed WebGPU sample failure is a Microsoft Basic Render Driver
device-loss event after the Direct2D qualification completed.

ABI v32 at implementation `3f5078af` plus MSVC oracle fix `8e812820` adds the
first real Direct2D command-list ingestion seam for the ProGPU C++ backend.
`ID2D1CommandList::Stream` targets an internal allocation-free
`ID2D1CommandSink1`; the sink validates a mixed clip/layer LIFO stack and emits
only a 64-byte pointer-free summary of state, clear, draw, fill, text, image,
clip, layer, and unsupported callback counts. It does not retain Direct2D
resources or put COM identity in MIL/WebGPU. Audit mode reports unsupported
operation classes, while strict mode fails `EndDraw`/`Stream` with `E_NOTIMPL`
for non-null text rendering parameters, GDI metafiles, meshes, and opacity
masks. Resource conversion and native scene emission deliberately remain the
next stages. The managed package builds with zero warnings, contracts pass 5/5,
and the allowlist is exactly 122 exports. Incremental Windows 11 ARM64 MSVC
19.44/SDK 10.0.26100.0 compiles the vtable under `/W4 /WX` and passes the live
supported/fail-closed command-stream regression 1/1; provider SHA-256 is
`E2A0F827107450E5C6D0ED8C2CA3C8C20656F6A32C1A6361DB788C14117CD1D3`.
Clean-checkout Build run `33339953074` is pending.

ABI v33 at implementation `bb4818bf` performs the first end-to-end COM command
translation. A strict `ID2D1CommandSink1` converts finite transforms,
source-over/DIPs state, solid brushes, rectangle fills/strokes, flat-cap lines,
edge-antialias selection, and one leading clear into ProGPU's existing native
semantic scene builder. The clear remains typed frame metadata; it is not
smuggled into the retained command stream. All other state, resource, and
operation classes fail closed with `E_NOTIMPL`, a typed reason, and the exact
one-based callback index. This is intentionally an admitted subset, not a
claim that arbitrary `ID2D1*` streams already translate.

The AOT-safe two-pass managed/native API measures the exact byte count and then
writes directly into caller-owned storage. The scene contains no COM pointer,
reflection shape, CPU pixel readback, repack buffer, or raster fallback. The
Windows provider links the backend-neutral C++ scene builder, keeping the
result usable by D3D12, Metal, Vulkan, and WebGPU and preserving the invariant
that DirectX interop is not a second scene implementation. Managed build is
warning-free, contracts pass 5/5, and the allowlist is exactly 123 exports.
Incremental Windows 11 ARM64 MSVC 19.44/SDK 10.0.26100.0 qualification compiles
under `/W4 /WX`, passes the live regression 1/1 in 3.35 seconds, decodes three
translated draws from the scene header, verifies fail-closed DirectWrite state,
and reports exactly 123 exports. Provider SHA-256 is
`0C552556B68BDB2F34B9B4ADA552B1DBBC2EB25A247483ED27710787CBF787D2`;
clean-checkout MSVC compatibility job `99339089791` on checkpoint `b91df2da`
passes; its longer Windows renderer jobs were superseded by ABI v34.

ABI v34 at implementation `c4dca894` translates nested aliased Direct2D
axis-aligned clips. The sink captures the active transform at each push,
computes the target-space rectangle, intersects it with the live parent clip,
and emits native scene state plus balanced save/restore commands. This keeps
the already-pushed clip fixed when later commands change transform and makes
scroll/viewport clipping reusable by every ProGPU backend. The supported depth
is the native scene maximum of 64 with an explicit capacity failure.

Per-primitive antialiased rectangle clips remain typed `E_NOTIMPL`: the current
native rectangle clip is a scissor and must not impersonate a coverage mask.
The Windows oracle decodes the seven-command stream and verifies the transformed
outer `[3,5,37.5,22.5]` and nested `[15.5,12.5,25,15]` state payloads exactly,
then proves that the antialiased mode returns no partial stream. Managed build
is warning-free, contracts pass 5/5, the export allowlist remains 123, and the
incremental Windows 11 ARM64 MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX` build plus
live regression pass. Provider SHA-256 is
`9C38D9BFFC95D7453EDCA5F3D63B53C973C1E24F9DDA2EB3214477BF497464AE`;
clean-checkout ABI v34 CI qualification is pending.

ABI v35 at implementation `226085da` translates genuine Direct2D linear and
radial gradient brushes into the existing backend-neutral semantic brush
table. Typed COM queries snapshot finite endpoints, center/origin/radii,
opacity, affine brush state, and at most 65,536 ordered stops only during
`ID2D1CommandList::Stream`; no COM identity enters the retained scene. Clamp,
wrap, and mirror reuse ProGPU pad/repeat/reflect shaders. Radial origin is
stored as Direct2D center plus origin offset.

Direct2D brush space is target-relative, so the stored coordinate mapping is
`inverse(active draw transform) * inverse(brush transform)`. The synchronous
identity cache includes the active draw transform in its key. A focused
positive oracle reuses one brush under two draw transforms and verifies that
it produces distinct canonical brush entries, then decodes the radial entry
and all six auxiliary stops. sRGB-to-sRGB straight interpolation is admitted;
premultiplied interpolation is admitted only for uniform stop alpha, where the
result is equivalent. Varying-alpha premultiplied interpolation, other color
spaces, and non-invertible transforms return typed unsupported state without
a partial stream. Source buffer precision is not emulated with CPU
quantization: ProGPU keeps the finite float stops and qualifies output through
the shared cross-backend pixel differential.

Managed contracts pass 5/5 and the package builds warning-free. Windows 11
ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 compiles provider and test
under `/W4 /WX`; the live regression passes 1/1 in 1.70 seconds (2.01 seconds
total under concurrent VM load), and the allowlist remains exactly 123
exports. The incremental three-file qualification payload SHA-256 is
`B545679CDCC7C81A826A333D3975C8BB7E8ED977A58FFBFC0601D4431DAAA368`;
the resulting provider SHA-256 is
`E5651DF33F23EB909FF2AB42F2A4E3592CDE81E21B57B3ADABFF38F493FDC2ED`.
Clean-checkout ABI v35 CI qualification is pending.

ABI v36 at implementation `e9788c5e` translates genuine Direct2D filled
geometries into the canonical semantic path family. A typed
`ID2D1SimplifiedGeometrySink` snapshots cubic and line contours during
command-list streaming, drops hollow figures from fill data, preserves
open/closed topology and fill rule, and caps the retained resource at
1,048,576 finite segments. The Direct2D draw matrix remains the path transform
and typed geometry bounds become conservative command bounds. No COM pointer,
CPU raster, pixel readback, or Windows-only retained object enters the scene.

Per-primitive edges use the shared eight-sample GPU path lane. Aliased path
edges and opacity-brush geometry fills remain fail-closed until exact coverage
and mask semantics exist; stroked `DrawGeometry` remains a separate slice so
caps, joins, miter limits, and dashes are not silently reduced. The positive
Windows oracle decodes a transformed winding line/cubic path, its explicit
closing edge, and the absence of hollow-figure segments. A negative oracle
proves aliased fill returns typed unsupported state without partial output.

Managed contracts pass 5/5 and the package builds warning-free. The 96 KiB
incremental payload SHA-256 is
`4BD4A70EE6575824BF33F37118434A185405F4BE3B484ADE2AE4B53374820F54`.
Windows 11 ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 compiles provider
and test under `/W4 /WX`; CTest passes 1/1 in 3.00 seconds (3.51 seconds
total). The export allowlist remains 123 and provider SHA-256 is
`12467CF6BE48235928B396A76AD5AE0AAD15CAA3E1949AB8A4E9BA4323EB744A`.
Explicit matrix field assignment also closes the ClangCL anonymous-union
warning found by the ABI v35 clean runner. ABI v36 clean qualification is
complete for the Direct2D slice: clean Build run `33345291817`, Windows x64
job `99348168246`, compiled both provider and test under ClangCL `/W4 /WX` and
passed all 12 native CTests, including Direct2D in 0.17 seconds. Its later
managed WebGPU readback lost the Microsoft Basic Render Driver, so the overall
job is red for an unrelated post-native-test infrastructure failure. Clean
MSVC compatibility job `99348168261` passed independently.

ABI v37 implementation `163fa686` translates genuine Direct2D stroked geometry
without introducing a second renderer. `ID2D1CommandSink::DrawGeometry`
passes the finite nonnegative width, original `ID2D1StrokeStyle`, and active
draw matrix to `ID2D1Geometry::Widen`. Direct2D therefore owns exact caps,
joins, miter, dash, and `ID2D1StrokeStyle1` normal/fixed/hairline transform
semantics. The typed simplified-geometry sink captures the resulting filled
outline, `GetWidenedBounds` supplies target-space bounds, and the scene retains
that outline as an identity-transformed pointer-free ProGPU path. Brush
translation still observes the active draw transform, including gradient
coordinate mapping.

This is a one-time Direct2D geometry conversion followed by the existing
cross-backend GPU path rasterizer; no CPU pixel rasterization, readback,
repacking, or retained COM identity exists. Per-primitive edges use the shared
eight-sample path lane. Aliased path edges, invalid typed input, unsupported
widening, and segment-cap overflow fail closed. The Windows oracle compares
the translated segment count with an independently widened genuine Direct2D
path and decodes both the original transformed fill and identity-transformed
custom dashed/beveled/capped stroke. Managed AOT contracts pass 5/5 and the
package builds with zero warnings. The final 95,520-byte incremental payload
SHA-256 is
`304477EB0796599D9015E7652DF15AEA53A61A79B69B93CFBD52101F7CA41974`.
Windows 11 ARM64 Parallels with MSVC 19.44/SDK 10.0.26100.0 compiles provider
and test under `/W4 /WX`; focused CTest passes 1/1 in 40.29 seconds (78.46
seconds total under concurrent guest load). No native export is added.

ABI v38 implementation `a308e7df` adds exact full-target uniform-opacity
Direct2D layers. The command sink accepts `D2D1::InfiniteRect()`, finite
opacity, no geometric/opacity mask, and `D2D1_LAYER_OPTIONS1_NONE`, then emits
the existing ProGPU isolated-layer command. Group opacity is consequently
applied once during composition, including overlapping descendants, and the
temporary Windows `ID2D1Layer` identity is not retained. ProGPU's shared GPU
layer executor owns pooling and replay on D3D12, Metal, Vulkan, and WebGPU; no
CPU pixel fallback or readback is introduced.

Axis clips and layers now share a bounded command-scope stack, preserving the
Direct2D rule that their push/pop pairs may nest but may not overlap. Wrong
pop order and unbalanced completion report typed drawing state. Exact finite
content bounds, geometric masks, opacity brushes, background initialization,
and ignored-alpha targets remain fail-closed pending their separate native
bounds/mask/backdrop resources.

The Windows oracle decodes a 37.5% source-over layer containing two overlapping
rectangles inside a valid outer axis clip and requires save/push/pop/restore
ordering. Its negative command list proves `INITIALIZE_FROM_BACKGROUND` emits
no partial scene. Managed AOT contracts pass 5/5 and the package builds with
zero warnings. Windows 11 ARM64 Parallels, MSVC 19.44/SDK 10.0.26100.0,
recompiles provider and test under `/W4 /WX`; the fresh executable exits zero.
The final 97,082-byte payload SHA-256 is
`84A118A67091ED4DA4854B1B00A4AEB26F760073D22A729DFCE1B8460859C270`.
Provider SHA-256 is
`305C1D7D3BC72F0CFC016778721CC36D90FDC91ABE1F9FCDE5DA2A8C5CFEF121`,
and all 123 exports exactly match the allowlist.

ABI v39 implementation `35a8fadc` admits finite Direct2D layer content bounds
under an axis-preserving active draw transform. Bounds are converted at push
time into exact target-space semantic-layer bounds, preserving scale,
translation, reflection, and push-time transform semantics. Rotation and
shear fail closed instead of broadening the layer to an axis-aligned box;
those cases require an exact transformed mask/coverage resource. Full-target
layers remain transform independent.

The Windows oracle captures an identity outer clip, then maps content bounds
`[1,2,21,22]` through `[2,0,0,0.5,7,9]` and decodes exact target bounds
`[9,10,40,10]` on the 37.5% source-over group. Managed contracts pass 5/5 and
the AOT package is warning-free. Windows 11 ARM64 Parallels rebuilds provider
and test from deleted objects under MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`;
the fresh executable exits zero. Payload SHA-256 is
`EDCD1850DABE2055AC05B6ACAC5583ADA8899C5A7806FC8A177551FF7D03B282`;
provider SHA-256 is
`C42A075E13706B42F7AA617CA437A194B20076BB538F5C2E91520A4F28BFE81E`,
with an exact 123-export allowlist match.

ABI v40 implementation `21be13a9` adds genuine Direct2D geometric layer masks
without adding a Direct2D-specific renderer. For per-primitive antialiasing,
the command sink simplifies the mask geometry to its filled line/cubic path,
retains fill rule and an eight-sample coverage request, and composes
`maskTransform * activeDrawTransform` as required by Direct2D's documented
layer coordinate system. The resulting pointer-free ProGPU vector-mask
resource is referenced by the existing semantic layer command. Finite content
bounds intersect exact Direct2D mask bounds, and full-target layers become
mask-bounded.

The shared ProGPU path rasterizer and layer compositor execute the mask on
D3D12, Metal, Vulkan, and WebGPU. Translation performs no CPU pixel work,
readback, repacking, or per-segment submission and retains no COM identity.
Empty filled geometry produces an exact empty layer. Aliased masks, opacity
brushes, backdrop initialization, ignored alpha, non-finite transform
composition, and unsupported geometry fail closed with typed diagnostics.

The Windows Direct2D oracle decodes the mask resource, line/cubic topology,
fill rule, sample grid, composed transform, and content/mask bounds
intersection, with bounds independently obtained from the genuine source
`ID2D1Geometry`. A negative command list requires an aliased mask to emit no
partial scene. Managed AOT contracts pass 5/5 and build with zero warnings.
Windows 11 ARM64 Parallels rebuilds the provider and test from deleted objects
under MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`; the fresh executable exits zero.
The 170,496-byte provider SHA-256 is
`21CB1B6F5DD483A6E6F1F3546D76C1EC158A22F042120AA8A503247CF58B4789`,
with all 123 exports matching the allowlist.

ABI v41 implementation `b84845fb` maps finite Direct2D opacity-brush layers
onto ProGPU's existing GPU brush-mask resource. Genuine solid, linear, and
radial `ID2D1Brush` instances are translated into pointer-free material and
gradient-stop data. Local content bounds plus the active draw transform define
coverage, while the retained inverse draw/brush coordinate mapping preserves
Direct2D target-space brush evaluation. The brush alpha is multiplied with the
isolated layer during composition, after the layer's uniform opacity.

The shared brush-mask rasterizer produces R8 coverage on D3D12, Metal, Vulkan,
and WebGPU without CPU pixel fallback, readback, repacking, per-stop GPU
submission, or retained COM identity. Full-target opacity-brush layers remain
typed unsupported until content-derived layer bounds can feed the resource.
Geometric-mask plus opacity-brush composition remains typed unsupported until
the scene builder exposes its already executable composite-mask serializer.

The Windows oracle decodes a real transformed two-stop Direct2D linear brush,
including exact target bounds, local mask bounds, active transform, stops,
brush opacity, and inverse draw/brush coordinates. Managed AOT contracts pass
5/5 and build warning-free. Windows 11 ARM64 Parallels rebuilds provider/test
from deleted objects under MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`; the fresh
native executable exits zero. The 176,640-byte provider SHA-256 is
`50FD9745C40EE045B53F06D1CD089B48F20BABC502D48DB014BAD795A3466C7F`,
with all 123 exports matching the allowlist.

ABI v42 implementation `f56ebe75` combines finite Direct2D geometric and
opacity-brush masks through the typed composite-mask serializer introduced at
`1ce62657`. One pointer-free resource retains the brush child, exact vector
path/segments, and shared stops. The existing backend creates both R8 masks and
multiplies them with `ClipCompose.wgsl`; D3D12, Metal, Vulkan, and WebGPU retain
the same renderer and no CPU pixel fallback or readback is introduced.

The native oracle requires a real transformed line/cubic geometry plus a real
two-stop linear gradient to decode as two components with one brush, one path,
three segments, and two stops, alongside the exact content/mask bound
intersection. Managed AOT build is warning-free and contracts pass 5/5.
After the Windows VM's existing restart restored Guest Tools, the exact source
archive SHA-256
`E01D2B571D8C11CCC41A3639DEBE5C4DB4B08CE571A60B0C4EE4802F80DEFBAC`
was extracted and confirmed as ABI v42. Windows 11 ARM64 Parallels rebuilt the
provider/test cleanly with MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`, and the
native executable exits zero. The 181,248-byte provider SHA-256 is
`D20084AFFC6C8FE39C2F10EBBBA565BB8CA0D6C0771B595A33C5527135F09698`.

ABI v43 begins the explicit ProGPU-owned Direct2D COM facade. The native API
creates a retained scene recorder and returns a caller-owned genuine
`ID2D1CommandSink1*` whose `IUnknown`, base sink, and versioned sink queries
share canonical identity. Applications can invoke supported Direct2D COM
callbacks directly and then serialize the finished recording into the same
pointer-free semantic scene used by the MIL replacement on D3D12, Metal,
Vulkan, and WebGPU. The recorder holds an independent sink reference, accepts
an optional allocation reserve hint, rejects incomplete or unsupported
recordings with typed HRESULT/reason data, and retains no COM pointer in the
scene. This is an explicit factory API, not a replacement `d2d1.dll`; future
factory, geometry, resource, and device-context vtables will build on the same
typed recorder and fail-closed rules.

The exact ABI v43 source archive SHA-256 is
`93F348B9C81F8D8211D24D9D0D145F620DD2EFBF9930D009B3826A8E46B4B05C`.
Windows 11 ARM64 Parallels rebuilds the provider/test cleanly with MSVC
19.44/SDK 10.0.26100.0 `/W4 /WX`; the native oracle exits zero. `dumpbin`
matches all 127 allowlisted exports exactly. The 183,296-byte provider hash is
`A6B2D9CFA4222846D91081F793BB3D6BAFC1F8C93854933DDD528BFE988D2533`,
and the test executable hash is
`08A3E37727EA14A579D6333E3E20914D15DE17F4F016AE10E6EC368F330A474D`.

ABI v44 extends that COM facade with an explicit ProGPU-owned
`ID2D1Factory1`/`ID2D1RectangleGeometry` dependency slice. The factory exposes
canonical base identity and `ID2D1Multithread`; it creates immutable finite
rectangles while every unimplemented resource family returns `E_NOTIMPL` with
a null output. Rectangle geometry supports `GetRect`, transformed `GetBounds`,
fill containment, `Simplify`, `Tessellate`, area, length, and
point-at-length. Geometry/factory ownership follows COM reference rules.

Passing that geometry to the ProGPU `ID2D1CommandSink1::FillGeometry` recorder
calls its standard Direct2D simplification and bounds vtables and emits the
same portable vector-path scene used by the native MIL renderer on D3D12,
Metal, Vulkan, and WebGPU. No system Direct2D geometry, pointer serialization,
CPU pixel fallback, or second renderer is introduced. The entry point remains
an explicit ProGPU API and does not shadow `D2D1CreateFactory` or `d2d1.dll`.
Focused managed contracts pass 5/5. The exact implementation checkpoint is
`123d2371`; its committed source archive SHA-256 is
`7F903F5B62FBA969359F8363E4E7C11495F9F76730CDBCADEAE4EA3AE021071A`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native oracle exits zero. `dumpbin` matches all
128 allowlisted exports exactly. The 191,488-byte provider SHA-256 is
`3D90668C81E5113EF5A3C1B86EC13CC5B4B6E09B2C070F753CF5276AE8BCB033`;
the 111,104-byte test executable SHA-256 is
`7910843D99080398B21DDD8F383FBEBBCB99E662B76338800C97034844B4C722`.

ABI v45 adds a mutable ProGPU-owned `ID2D1SolidColorBrush` in the explicit
compatibility-factory domain. It exposes canonical resource/brush/solid-brush
COM identity, retains its factory, synchronizes valid color/opacity/transform
state, and fails invalid creation closed. The direct recorder oracle now uses
only ProGPU-owned factory, rectangle, brush, and command-sink objects; standard
brush vtable reads still lower the two draws to one shared pointer-free scene
brush. No system Direct2D resource, CPU fallback, or alternate renderer remains
in that oracle. Focused managed contracts pass 5/5. The exact implementation
checkpoint is `73b6ff5e`; its committed source archive SHA-256 is
`59A755509F2E3FF32B8A4C5FE5C32CB7C8752C10B2A02F84276393D2FC157DDA`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native oracle exits zero. `dumpbin` matches all
129 allowlisted exports exactly. The 195,584-byte provider SHA-256 is
`4126FB918B4A577BB728BF1E0B27E35E388185841223BBAD4044FD80DEE836ED`;
the 113,664-byte test executable SHA-256 is
`5B6EC4E52D17BB185A3E513A22628CC9BF93AE98AF28AFFD90F2FC448DFEB45C`.

ABI v46 adds ProGPU-owned `ID2D1PathGeometry1` and `ID2D1GeometrySink` COM
objects behind both standard factory path-creation vtables. The one-shot sink
records lines, cubics, quadratics, arcs, segment flags, fill mode, and figure
state, publishes an immutable snapshot only on successful `Close`, and counts
implicit closing edges in the same public segment index space as Direct2D.
Canonical `IUnknown`/resource/geometry/path/path1 and
`IUnknown`/simplified-sink/geometry-sink identities retain their factory and
shared path storage according to COM lifetime rules.

The supported analysis slice includes exact vocabulary streaming, transformed
line/cubic/arc bounds, cubics-and-lines or flattened-line simplification,
ordinary non-overlapping fill containment/area, length, point-at-length, and
point-plus-segment queries. Complex overlapping/self-intersecting area,
strokes, widened bounds, tessellation, outlines, geometry compare, and boolean
combination remain explicitly gated and fail closed. The direct recorder now
consumes this ProGPU path through standard COM vtables and lowers it to the
same pointer-free vector scene rendered on D3D12, Metal, Vulkan, and WebGPU;
there is no system-Direct2D resource dependency, CPU pixel fallback, or second
scene implementation. The Windows oracle differentially compares counts,
bounds, and flattened length with a genuine system `ID2D1PathGeometry1`.
Focused managed contracts pass 5/5. The exact implementation checkpoint is
`3f42538c`; its committed source archive SHA-256 is
`32A3ECA03C6C721B505D40A6638A7D55E139C6132E65C296DFFFBD4D2A633EC3`.
Windows 11 ARM64 Parallels rebuilds it cleanly with MSVC 19.44/SDK
10.0.26100.0 `/W4 /WX`, and the native differential oracle exits zero.
`dumpbin` matches all 129 allowlisted exports exactly. The 225,280-byte
provider SHA-256 is
`681EC3239D4B235BDD0E024A9D3C1DCD5D0444F8F1ACD3CB6FE31F0DC8A6940B`;
the 118,272-byte test executable SHA-256 is
`1845C2C96B3B8AA0DA46D909384AB3D417AB607205EAB921C84AC626FB084586`.

ABI v47 adds an immutable ProGPU-owned `ID2D1StrokeStyle1` to both standard
factory stroke-style creation vtables. It preserves canonical
resource/stroke/stroke1 identity, factory ownership, cap/join/miter metadata,
normal/fixed/hairline transform policy, predefined dash kind/offset, and a
copied custom-dash array. Invalid or non-finite metadata and malformed custom
patterns fail closed. The Windows differential oracle compares every getter
and dash interval with a genuine system Direct2D resource.

This resource is the first half of the retained-stroke dependency. ProGPU
already renders a pointer-free semantic stroke batch with caps, joins, miters,
dashes, and transforms on every qualified backend; the direct COM recorder
will translate compatible path figures and this resource into that existing
batch instead of invoking CPU `Widen`, manufacturing filled outlines, or
creating a second rendering path. Focused managed contracts pass 5/5; Windows
11 ARM64 Parallels rebuilds exact implementation checkpoint `71118006` with
MSVC 19.44/SDK 10.0.26100.0 `/W4 /WX`, and the native differential oracle
exits zero. `dumpbin` matches all 129 allowlisted exports with zero differences.
The committed source archive SHA-256 is
`FF58C3EF89AADB24AA5E1A88416F399F75CCD1D9DB559180333B274441AAF999`;
the 228,864-byte provider SHA-256 is
`D259FFBF25B8F9B2950A1DBE876901175D4EC31E7BFBE665324678BAEE68E095`;
the 120,320-byte test executable SHA-256 is
`E2C71C12741DB7A71C01EBCE510664BBE20E693131C21DA4DCCC2C1ACAF54CAE`.

ABI v48 translates compatible Direct2D `DrawGeometry` path strokes to the
existing pointer-free semantic stroke batch. Its bounded simplified-geometry
capture emits one retained polyline per figure and preserves open/closed
topology, caps, uniform joins, miter limit, normal/fixed/hairline policy,
predefined or custom dash intervals, dash cap/offset, active transform, and
brush indirection. Flattening tolerance is transform-aware; no COM pointer,
widened outline, CPU pixel buffer, or per-item GPU submission enters the scene.

Direct2D may attach per-segment un-stroked or forced-round join hints while
flattening curves. The current uniform-stroke descriptor cannot represent
those exactly, so hinted genuine system geometries retain the qualified
Windows `Widen` path and ProGPU-owned cases without that fallback fail closed.
The direct COM oracle separately proves a compatible ProGPU-owned rectangle
and fixed custom stroke style serialize as `STROKE_BATCH`; the existing cubic
oracle continues to cover the Windows hinted fallback. Exact per-segment scene
metadata is the next dependency before portable curved-stroke coverage can be
claimed.

Focused managed contracts pass 5/5. The exact implementation checkpoint is
`2d7809f9`; its committed source archive SHA-256 is
`D010D1EF377FE30D47FCA9411EC1921BDC20A04F69637B53B2DDB53FD25E5F8F`.
Windows 11 ARM64 Parallels rebuilds it with MSVC 19.44/SDK 10.0.26100.0
`/W4 /WX`; the full native oracle exits zero and `dumpbin` matches all 129
allowlisted exports with zero differences. The 243,712-byte provider SHA-256
is `ECC61FFBA903F53532094CD5A7492CA1F9DEC828CB1C91BE08EB0241FB020587`;
the 121,856-byte test executable SHA-256 is
`21C542CEFF8805DB694A4D449891486F2A4F094BF4A0BD428ABF8F9063B3C23D`.

ABI v49 adds exact normal-transform Direct2D curved-path strokes without a new
scene record. The COM recorder captures Direct2D's `CUBICS_AND_LINES`
vocabulary, retains per-segment stroke/join flags, and converts it into the
same analytic line/cubic/path-cap/path-join primitives already consumed by the
portable C++ renderer. Un-stroked gaps become bounded open runs with dash caps;
forced round joins are shifted from Direct2D's incoming-segment convention to
the semantic compiler's outgoing-edge convention, including both closed-figure
seams.

The line-only uniform case remains the existing `STROKE_BATCH` fast path, so
ABI v48 fixed custom rectangles and large WPF polyline workloads do not pay the
analytic expansion cost. Curves and per-segment joins use the shared
`progpu_native_semantic_path_stroke.hpp` compiler and MIL curve-dash run
splitter. The output remains pointer-free and backend-neutral; there is no CPU
outline widening, pixel readback, repacking, or per-segment GPU submission.
Fixed/hairline curved paths remain explicitly gated until device-space dash
distance is qualified, with the genuine Windows `Widen` fallback retained and
ProGPU-owned unsupported cases failing closed.

The direct COM oracle requires a ProGPU-owned mixed path to serialize a
`GEOMETRY_BATCH` containing a cubic and forced-round join. A portable native
MIL unit test independently compiles the shared helper and compares its exact
primitive sequence. Focused managed contracts pass 5/5 and the native MIL
oracle exits zero on macOS. Exact implementation checkpoint `aecb6883` has
committed source archive SHA-256
`CDE728391DE0F7EE8F9E504BEE215B4E1B6D6C7A81701864FE3516B07700D51C`;
Windows 11 ARM64 build `10.0.26200.9168` in Parallels 26.4.1 extracts and
builds its exact 1,731,087-byte native qualification archive, whose SHA-256 is
`ED54C0F280595EC92B2D182C3E7AC02E49A494F5D969257DD62D9B3ED0B162F1`.
MSVC 19.44.35228.0 compiles the provider and oracle with `/W4 /WX`; the native
Direct2D oracle exits zero and the export table contains the expected 129
symbols. The 265,216-byte `progpu_native_direct2d.dll` SHA-256 is
`FDA4E04F94D3DA60C6C8574C6D8196ADCB16ACF654DD6DC1A8AF2342017BAFC9`;
the 122,880-byte test executable SHA-256 is
`3D285A96AA096967ACB5E4A6AA1DCD46B1D040CA6603AFC54804360707B6A7DA`.

ABI v50 completes the portable curved-stroke transform-policy matrix. Fixed
and hairline paths now retain their analytic segments while the shared dash
splitter measures distance through the active transform's linear component.
Translation is deliberately excluded because it cannot change arc length and
would reduce precision for small curves at large world offsets. The resulting
distance-to-parameter lookup therefore follows device-space geometry without
flattening or replacing the emitted local quadratic, cubic, or arc span.

Normal strokes continue to measure and scale dashes in local space. Fixed
strokes measure in device space while retaining the supplied stroke width;
hairlines measure in device space with a one-unit dash scale, store zero scene
thickness, and ignore the caller's stroke width as required by Direct2D. Fixed
and hairline flags remain mutually exclusive and malformed forced states fail
closed. The line-only uniform case remains the one-record `STROKE_BATCH` fast
path; analytic curves still compile to one pointer-free `GEOMETRY_BATCH`, with
no CPU widening, pixel readback, repacking, or per-segment submission.

The portable MIL oracle differentiates normal, fixed, and hairline dash ends
for the same cubic under a non-uniform transform and verifies the resulting
flags and thickness. The Windows COM oracle records the three matching
`ID2D1StrokeStyle1` policies through a genuine Direct2D command list and
requires three distinct analytic curved batches. Focused managed ABI contracts
pass 5/5 and the native MIL oracle exits zero on macOS.

Direct2D compatibility ABI v51 adds a ProGPU-owned
`ID2D1EllipseGeometry`. It keeps genuine resource/geometry/ellipse COM
identity and factory parentage, exact affine support-function bounds,
inverse-transform containment, tolerance-controlled path metrics, and the original
ellipse descriptor. A closed four-cubic path is constructed once with the
resource, with a zero-length endpoint line after each of the first three cubics
to match the system `Simplify(CUBICS_AND_LINES)` transcript. It is then reused
by the shared path simplifier and scene compiler,
whose typed fill/stroke sinks elide those zero-length continuation markers
after a real segment so they add no GPU primitive or invalid tangent,
so filled and stroked ellipses enter the same backend-neutral vector resources
as other Direct2D paths. No runtime reflection, widened CPU bitmap, readback,
or per-frame path reconstruction is introduced. The construction work is a
fixed four-segment operation rather than a SIMD-eligible bulk loop. Focused
managed ABI contracts pass 5/5; Windows native qualification passes in the
ABI-v52 MSVC job described below.

Direct2D compatibility ABI v52 adds ProGPU-owned
`ID2D1RoundedRectangleGeometry`. The immutable COM resource preserves the
original descriptor and factory identity while retaining one four-line,
four-cubic path whose corner radii are bounded by the rectangle half-extents.
The same shared path analysis and pointer-free scene compiler handle
containment, tolerance-controlled metrics, fills, and strokes across D3D12,
Metal, Vulkan, and WebGPU. It performs no reflection, CPU pixel conversion,
readback, or per-frame path reconstruction. The constant eight-segment
constructor is scalar because it has no useful independent-lane bulk work.
Focused managed ABI contracts pass 5/5.

Ellipse length/point delegation applies the system Direct2D half-tolerance
subdivision threshold. The Windows ellipse oracle measured `19.2537` at public
tolerance `0.25`; ProGPU previously returned `19.1810`, while the retained
cubic at effective threshold `0.125` returns the system value. Arbitrary paths
and rounded rectangles keep the caller's unscaled tolerance. The rounded
oracle measured `35.6731`; incorrectly applying the ellipse compensation
returned `35.7652`. Recursive subdivision has data-dependent child
termination, so it is not an independent-lane SIMD workload; emitted-edge
reduction remains bounded and allocation ownership is unchanged.

The field-level recorder diagnostic also resolves the ABI-v50 Parallels
failure: scene serialization returns success/S_OK, writes exactly
`17,936/17,936` bytes and eight commands, and retains one brush. The prior test
expected two brushes even though the semantic builder correctly deduplicates
the same solid brush across draw transforms. ABI v52 now locks down the
canonical one-brush result. The archived ABI-v50 execution remains negative
qualification evidence because that executable exited 1, but it no longer
indicates a provider write defect.

The command-list oracle exposed immediately afterward also predated ABI v50's
analytic curved-stroke compiler. It incorrectly required both fill and stroke
as `PATH_BATCH`; the translator correctly emits one fill `PATH_BATCH` and one
analytic `GEOMETRY_BATCH` stroke. ABI v52 now locks down that resource split,
while the later recorder checks retain detailed cubic, round-join, gap, dash,
fixed-device, and hairline assertions.

The hosted Windows
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33417514376/job/99571802634)
passes at implementation `e5a75a9b`. MSVC builds the complete provider and all
11 native CTests pass, including the system-Direct2D ellipse and
rounded-rectangle differentials plus recorder/resource canonicalization. This
qualifies ABI v52 on Windows x64. The ClangCL x64/ARM64 lanes still stop only
at the three pre-existing missing-braces warning-as-error sites; this slice
introduces no additional ClangCL warning.

Direct2D compatibility ABI v53 adds a ProGPU-owned
`ID2D1TransformedGeometry`. It retains its immutable source, original affine
matrix, and factory identity, and composes the stored transform before each
caller world transform with double intermediates and finite-range validation.
Supported analysis, simplification, and scene-recording calls delegate through
that composed matrix, so nested transformed resources and normal
`FillGeometry` lowering reuse the retained source without a copied path,
per-frame rebuilding, CPU readback, or backend-specific command. Sources from
another factory and malformed transforms fail closed. Rectangle relation and
Boolean calls now preserve the stored matrix while independently applying the
candidate transform. Non-rectangle combinations remain typed unsupported
rather than dropping either transform.

The affine rectangle Boolean engine keeps the exact axis-preserving grid
tracer, then handles the general case with bounded pairwise edge splits,
coincident-edge endpoint splits, two-sided Boolean membership classification,
directed-boundary deduplication, and fixed-array contour tracing. Union,
intersection, xor, and exclusion are covered with both the candidate and
source geometry transformed, including identical rectangles, full and partial
shared edges, and same-side collinear overlap. All work is allocation-free
analytic topology; no CPU pixels, readback, repacking, or backend-specific
execution path is introduced. The Windows oracle sends the same operations
through genuine system Direct2D and compares result predicates across the
focused probe lattice for every mode.
All 17 local native CTests and 10 managed Direct2D contracts pass. Windows 11
ARM64 Parallels then recompiles the focused compatibility target with MSVC
19.44 under explicit `/W4 /WX` and passes the genuine system-Direct2D
differential. No COM ABI or export-list change is required.

The native oracle covers COM/source/factory identity, metadata, exact affine
bounds, containment, area, length, point-at-length, simplified topology,
invalid creation, pointer-free semantic fill translation, and a non-commuting
stored-plus-world differential against system Direct2D. Focused managed ABI
contracts pass 5/5. Exact `998c9ec2` passes the hosted
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33420113029/job/99580305821):
the provider and tests compile with MSVC, the system-Direct2D differential
passes, and all 11 native CTests pass. This qualifies ABI v53 on Windows x64.

Direct2D compatibility ABI v54 adds a ProGPU-owned `ID2D1GeometryGroup`.
The immutable resource retains ordered child and factory COM identities,
alternate/winding metadata, and one multi-figure path built from its sources.
A typed forwarding sink preserves the one group fill mode while suppressing
the source simplifiers' child-local mode publication after the first figure.
Transformed children enter that path through their composed simplification;
bounds, containment, metrics, topology, and semantic fills then reuse the same
pointer-free scene path without per-frame child expansion, CPU readback, or a
backend-specific group command.

Nested groups retain and republish their immutable multi-figure child path, so
the outer group's alternate/winding mode remains authoritative as required by
Direct2D's concatenated-figure contract. Null children, invalid modes,
cross-factory resources, and excessively deep transformed-source chains still
fail closed. The native oracle compares identity, ordered sources, two
independently positioned members, nested winding-over-alternate containment,
analysis, simplified topology, failure behavior, and world-transformed output
with system Direct2D. Focused managed ABI contracts pass 5/5.

The first `0e93f94e` Windows run compiled and linked but failed group creation
because child simplifiers attempted to republish their fill mode after the
target sink's first figure. Corrected `ada83ef7` uses the typed forwarding sink
described above and passes the hosted
[`Native C++20 compiler compatibility (MSVC)` job](https://github.com/wieslawsoltes/ProGPU/actions/runs/33422845973/job/99589327621):
the system Direct2D group differential, semantic recorder, and all 11 native
CTests pass. ABI v54 is qualified on Windows x64.

## Managed glyph row-reuse SIMD checkpoint

Managed ProGPU checkpoints `2960fb39` and `ffb285af` bring the explicit
intrinsic glyph fallback to the native row traversal already used by the C++
MIL renderer. The managed rasterizer collects all eight Y-subscanline crossing
spans before visiting X, builds each pixel's Vector256 or Vector128 horizontal
sample vectors once, applies every span without scalar lane extraction, and
writes the exact integer-quantized coverage byte directly. It retains one
pooled crossing arena, stack-resident offsets, the independent scalar oracle,
and the existing output allocation. Unsupported 256-bit setup is not executed
on Vector128-only runtimes.

All 19 focused managed differential tests pass. Eight alternating Apple M3 Pro
processes improved median p50/p95 from 218.649/227.590 to
205.471/212.347 us/glyph (6.0%/6.7%) with checksum 175 and 4,120 B/glyph on
every run. The immutable final archive rebuilt with zero warnings under .NET
SDK 10.0.400 in Windows 11 ARM64 Parallels; three .NET 10.0.11 Vector128 runs
retained the checksum and allocation. Source and archive SHA-256 matched
between host and guest (`45BA556F...CD3FE0C` and
`C6A295B3...E1E242F`). The local Windows and Rosetta x64 runtimes both report
`Vector256=False`; actual Vector256 execution remains a required x64 CI or
hardware qualification rather than an inferred claim.

## Managed glyph direction-partition SIMD checkpoint

Checkpoint `f8c6cc7e` follows the row-reuse work by partitioning each bounded
crossing block into positive and negative X-coordinate ranges. The managed
Vector128/Vector256 hot loops apply direction-specific mask accumulation with
no per-crossing direction field or branch. Ref-plus-offset access removes the
per-pixel span construction exposed by the follow-up managed CPU trace, and
the logical pooled crossing payload falls from eight to four bytes per root.
Checked root bounds, the 8x8 sampling grid, exact integer coverage
quantization, scalar oracle, GPU-first execution policy, and native C++ path
are unchanged.

The expanded differential suite passes 21/21 on Apple ARM64, Windows 11 ARM64
Parallels, and Ubuntu ARM64. Eight alternating Apple M3 Pro pairs improved
median process p50/p95/p99 from 208.648/240.219/302.034 to
174.606/222.808/262.180 us/glyph while preserving checksum 36 and
4,120 B/glyph. Three Windows ARM64 and three Ubuntu ARM64 processes preserved
checksum 175 and the same allocation. A self-contained Windows x64 publish
reported `Vector256=True` and retained exact output across three processes;
that VM lane qualifies behavior only, not physical-x64 performance. Exact
archive, WinUI submodule, and executable hashes plus rejected experiments are
recorded in `GLYPH_CPU_FALLBACK_SIMD_RESEARCH.md`.

## Processed PCM16 intrinsic-SIMD checkpoint

Checkpoint `e6236472` removes the remaining whole-buffer scalar loop from the
typed-effect PCM16 export path. Windows, Linux, and Android now delegate float
effect output to one `MediaPcm16ProcessedAccumulator` kernel. It widens valid
float lanes to double, applies alternating Q15 levels, preserves
away-from-zero rounding, clamps contributions, and saturates Int64 additions.
Non-finite input falls back at the containing vector so the exact invalid lane,
exception message, and earlier writes remain compatible. The independent
scalar oracle covers vector tails, float extrema and subnormals, midpoint
rounding, Int64 overflow, and NaN partial writes; the focused Windows/Android
consumer tests and allocation gate pass.

Four alternating 1,024-frame Apple M3 Pro runs measured median p50 3.705 us
for `Vector128` versus 8.064 us scalar (2.18x). The self-contained x64 binary
then ran in the Windows 11 ARM64 Parallels guest with `Vector256=True` and
measured median p50 28.571 versus 38.003 us (1.33x). Median p95 improved on
both platforms, Windows median p99 was effectively equal, all output matched,
both paths allocated zero bytes per block, and the guest executable SHA-256
was `4FA9ECCA268E4F7D51D860CEFC5D4138A3544A8CBE67BE35858FD838D81A9F5B`.
These are Apple ARM64 and emulated Windows x64 qualifications, not a
physical-x64 performance claim.

## PCM16 float-normalization intrinsic-SIMD checkpoint

The next media CPU checkpoint removes the duplicated scalar PCM16-to-float
loops before typed effect processing. Windows Media Foundation, Linux, and
Android now use one allocation-free `MediaPcm16FloatConverter`. Its two-vector
unrolled `Vector256`/`Vector128` lanes widen Int16 to Int32 and normalize with
the exact power-of-two scale; only a bounded tail remains scalar. Every result
bit matches the independent `sample / 32768f` oracle across seeded full-range
input, signed extrema, vector boundaries, and tails. Destination bounds and
1,000 repeated allocation-free calls are also gated, all three platform source
contracts require the shared kernel, and the full managed suite passes
3,877/3,877.

Three fresh 48,000-frame Apple M3 Pro runs measured median p50 10.451 us for
the unrolled `Vector128` implementation versus 33.191 us scalar (3.18x).
Four fresh 1,024-frame runs of the same self-contained source in the Windows 11
ARM64 Parallels guest measured median p50 1.492 us for `Vector256` versus
14.874 us scalar (9.97x). Both environments produced identical checksums and
zero allocation; the guest executable SHA-256 was
`95ECEAE96594EAE211491850692CD76FBDDC908800D69CCD1E59779A2E3B557F`.
The Windows evidence qualifies the emulated x64 route, not physical x64.

## Apple float-stereo layout intrinsic-SIMD checkpoint

The Apple AVFoundation real-time mix tap no longer performs two whole-buffer
scalar stereo transposes around every typed audio-effect callback. The shared
`MediaFloatStereoLayoutConverter` interleaves and deinterleaves four float
frames per lane. ARM64 uses the architecture's paired `ST2`/`LD2` zip/unzip
memory operations; x86/x64 uses SSE unpack/shuffle operations; both retain a
bounded scalar tail and allocate nothing. Mono remains a direct span copy.
Channel counts above two retain the dependency-strided scalar transpose because
their lanes are not the independent stereo pattern implemented by this kernel.
The differential oracle covers empty input, vector boundaries and tails,
sentinel preservation, length rejection, exact round trips, and 1,000 repeated
allocation-free conversions. Apple source-contract coverage requires both
shared calls, the macOS Apple-media project builds with zero warnings, and the
complete managed suite passes 3,878/3,878.

The first explicit ARM ZIP/UZP experiment was rejected because its four-frame
managed load/shuffle/store loop measured slower than the scalar oracle. Using
the native interleaved-memory operations instead produced three fresh Apple M3
Pro 1,024-frame callback-round-trip runs with median p50 `0.269` versus `1.241
us/block` (4.61x), median p95 `0.603` versus `20.251`, and median p99 `0.649`
versus `23.248 us/block`. Four alternating runs of the self-contained `win-x64`
binary in the Windows 11 ARM64 Parallels guest retained the cold-host outlier
and measured median p50 `0.934` versus `1.369 us/block` (1.47x), p95 `6.477`
versus `38.155`, and p99 `7.581` versus `39.467 us/block`. Both environments
were exact and allocation free. The guest executable SHA-256 was
`E38F889B495687BDFFBE61747FAA51ED3C60446092613B28DA8CEC5E0E56EDD8`;
the Windows result qualifies emulated x64 correctness and relative performance,
not physical-x64 performance.

## Canonical MIL 3D camera and transform checkpoint

The portable native channel now decodes and executes twelve additional WPF
commands: `Viewport3DVisualSetCamera`, `Viewport3DVisualSetViewport`, both
`Rotation3D` resources, all three camera resources, and the complete five-type
`Transform3D` family. The generated coverage ledger consequently advances from
72 to 84 top-level decoder cases and reduces undispatched commands from 44 to
32. The managed `NativeMilBatchBuilder` writes the exact generated layouts,
including the mixed double/float projection-camera records, variable group
children, animation handles, and WPF's positive/negative-infinity
`Rect.Empty` sentinel.

Execution follows WPF's row-vector conventions. Transform-group children are
appended in collection order; rotate-about-center and scale-about-center retain
their pre/core/post composition; camera transforms are inverted and prepended
to the view matrix. Axis-angle rotations normalize nonzero axes and treat an
axis whose squared length is at most `FLT_MIN` as identity. Quaternion,
perspective, orthographic, and matrix cameras all resolve into the existing
backend-neutral semantic camera ABI. Perspective projection uses WPF's
horizontal field of view, orthographic height is derived from viewport aspect,
and positive-infinite far planes retain WPF's unbounded projection form.

Animated packet fields are validated only when their animation handle is zero,
matching generated WPF producers that may leave the unused static bytes
unspecified. The referenced `Double`, `Point3D`, `Vector3D`, and `Quaternion`
resources remain strongly typed. Missing resources, wrong resource kinds,
cycles, noninvertible camera transforms, invalid camera bases, invalid
projection domains, and malformed viewports fail transactionally. Dependency
hashing and deletion protection traverse camera, rotation, transform-group,
and animation edges, so changing an animation invalidates the retained page
without rebuilding unrelated resources.

This checkpoint deliberately keeps one typed hybrid boundary. Mesh, model,
material, and light arrays still enter through the copied pointer-free
`set_viewport3d_scene` sideband; canonical MIL camera and viewport state
override the sideband's flattened camera and rectangle during compilation.
There is no per-frame managed camera flattening, object pointer, reflection,
or CPU projection. A null camera, `Rect.Empty`, or zero-sized viewport produces
no mesh draw successfully. Full canonical `Visual3D`, model, mesh, material,
and light packets are the next 3D slice and will remove the remaining sideband
scene description.

Native executable coverage resolves animated axis-angle/translation state,
quaternion rotation, scale, matrix transform, group ordering, perspective,
orthographic, and matrix cameras into retained mesh draws. It verifies camera
positions and projection coefficients, viewport override and empty suppression,
invalid-group rollback, and row-vector composition. The managed differential
test verifies every packet size, command identifier, representative field
offset, animation handle, matrix row, resource value, and empty-rectangle wire
value. The no-provider native test and focused managed test pass on Apple
Silicon. Exact implementation checkpoint `8235ca39` is also compiler/runtime
qualified in the Windows 11 ARM64 Parallels guest (OS build
`10.0.26200.9168`) with MSVC 19.44, CMake 4.4.3, and Ninja 1.12.1. The
immutable source archive has SHA-256
`0aa033b4be7fed56266d0e464318261989742353b49bd24387999444f1b8ff8a`.
All 136 Release build steps completed with `/W4 /WX`, and the focused
`progpu_native_mil_tests` CTest passed. This qualification covers the native
compiler, canonical decoder, transactional retained state, and runtime math;
it does not by itself claim live D3D12 rendering coverage for this camera and
transform slice. The existing semantic viewport/D3D12 lane remains the visual
backend gate until canonical mesh, model, material, and light packets remove
the typed scene sideband.

The boundary described above is historical as of the following checkpoint;
the sideband remains only as a compatibility input when no canonical
`Viewport3DVisualSet3DChild` binding has been received.

## Canonical MIL Visual3D, model, mesh, material, and light checkpoint

The native channel now decodes and executes the remaining seventeen WPF 3D
scene commands: `Viewport3DVisualSet3DChild`, all five Visual3D topology/state
mutations, `Model3DGroup`, ambient/directional/point/spot lights,
`GeometryModel3D`, `MeshGeometry3D`, `MaterialGroup`, and the three concrete
material resources. The generated coverage ledger advances from 84 to 101
top-level decoder cases and reduces undispatched commands from 32 to 15.

Resource identifiers come from WPF's processed `MIL_RESOURCE_TYPE` authority,
including its abstract base-class slots. This matters for wire compatibility:
the concrete cameras are 7/8/9, `Transform3DGroup` is 27, and translate/scale/
rotate/matrix transforms are 29/30/31/32. Native compile-time constants,
managed `NativeMilResourceType` values, and differential tests now lock those
canonical identifiers rather than using a locally contiguous approximation.

Visual3D children retain one-owner topology, indexed insertion/removal, cycle
rejection, optional content, and optional transform state. Model and material
groups retain ordered typed children and reject cycles transactionally.
Dependency hashing and deletion protection traverse viewport child, visual,
model, geometry, material, brush, light, transform, and animation edges. A
failed graph update therefore leaves both the previous semantic stream and its
retained revision intact.

`MeshGeometry3D` consumes WPF's exact variable payload order and element
widths: float3 positions, float3 normals, double2 texture coordinates, and
32-bit triangle indices. Index processing matches WPF realization: an invalid
index truncates the suffix, incomplete triangles are dropped, and a missing
index collection uses consecutive non-indexed triangles. Missing or short
normal collections use indexed face accumulation and normalized vertex sums;
supplied normals override generated values. Missing texture coordinates become
zero. The indexed accumulation has loop-carried scatter dependencies, so it is
not a valid lane-independent SIMD kernel; its bounded three-component face
cross products remain scalar. The independent per-vertex normalization runs in
four-vertex ARM64 NEON or SSE2 batches with IEEE square-root/divide, finite and
degenerate-lane masking, and a bounded scalar tail. The canonical test drives
one full vector block plus its tail and compares supplied and generated normals
to scalar expected values. GPU projection, lighting, material sampling, and
rasterization remain in the shared semantic WebGPU path.

Scene compilation makes two deterministic passes over the typed graph: lights
first and geometry second. This gives every emitted material pass the final
bounded light table without rebuilding mesh vertices. Row-vector Visual3D,
Model3DGroup, GeometryModel3D, and light transforms compose in WPF order.
Normals use inverse-transpose matrices. Ambient, directional, point, and spot
records lower to the existing backend-neutral light ABI, including transformed
positions/directions, attenuation, range, and cone cosines.

Ordered `MaterialGroup` leaves produce ordered draw passes. Diffuse material
retains diffuse/ambient colors, specular retains color and exponent, emissive
selects unlit shading, and `BackMaterial` produces an explicit back-face pass.
Solid and linear/radial gradient brushes reuse the ordinary retained brush
compiler; no CPU readback, per-frame managed flattening, object pointer, or
backend-specific mesh command is introduced. A canonical child binding always
wins over the compatibility sideband, including an explicitly null child.

`NativeMilBatchBuilder` exposes span-based writers for the same seventeen
commands. Variable child and mesh arrays write directly into the canonical
DWORD-aligned batch buffer without intermediate arrays. Static values are
validated only when their animation handle is zero, matching generated WPF
packet behavior; malformed byte counts, wrong resource types, missing handles,
ownership conflicts, cycles, and non-finite resolved values fail closed.

The native end-to-end test constructs a viewport solely from canonical WPF
resources—no `set_viewport3d_scene` call—and verifies four material passes,
generated normals, all four light kinds, transformed mesh state, camera and
viewport payloads, dependency-protected deletion, transactional invalid
material rollback, Visual3D cycle rejection, child removal, and reinsertion.
The managed differential test verifies every added command identifier and
packet size plus representative fixed, animation, variable-array, and resource
identifier offsets. The focused native CTest and 97 managed native-interop
tests pass on Apple Silicon. The complete local no-provider native suite passes
12/12.

Exact implementation checkpoint `a2b8d045` is compiler/runtime qualified in
the Windows 11 ARM64 Parallels guest (OS build `10.0.26200.9168`) with MSVC
19.44.35228.0, Visual Studio Build Tools 17.14.39, CMake 4.4.3, and Ninja
1.12.1. The immutable source archive matched on host and guest with SHA-256
`e90924eae1e0d8aef96b70f58c0103e3cda51e20b87ca98cada64836231373d2`.
The changed `progpu_native_mil.cpp`, MIL interop, and expanded native packet
oracle compiled under `/W4 /WX`; the focused `progpu_native_mil_tests` CTest
passed 1/1 in 4.35 seconds.

The attempted all-target build stopped separately in the existing Windows
Direct2D include path because the Windows SDK `near` macro expands the
`curve_dash::detail::near(...)` identifier. That pre-existing Direct2D build
failure is not evidence against this MIL checkpoint and was not modified as
part of it. Live D3D12 visual qualification remains required before this
canonical scene slice is called backend-qualified; the evidence here qualifies
the Windows compiler, ARM64 intrinsic execution, decoder, transactional graph,
scene serialization, and packet oracle.

## Direct2D path Boolean checkpoint

Portable `ID2D1PathGeometry::CombineWithGeometry` now handles every Direct2D
combine mode for arbitrary normalized contour counts, including concave paths,
crossings, containment, disjoint components, nested holes, identical operands,
and positive collinear overlap. Curves enter through each operand's
tolerance-qualified `Outline`; only the input operand receives the caller
transform. Canonical alternate-fill contours are tagged by operand and passed
to the shared split/classify/trace graph. The implementation builds the entire
boundary graph before invoking the caller's sink, so allocation failure,
unsupported topology, or numerical ambiguity cannot expose a partial result.

The independent edge-bounds broad phase executes in four-lane ARM64 NEON or
SSE2 batches with a bounded scalar implementation only on architectures that
provide neither intrinsic family. Exact intersection insertion, directed-edge
deduplication, and smallest-positive-turn contour tracing remain scalar because
they mutate dynamically ordered topology and do not have independent output
lanes. The operation never rasterizes pixels, reads back from a GPU, or creates
per-edge submissions.

The focused optimized and address/undefined-sanitizer CTests pass on Apple
Silicon. Clean Windows 11 ARM64 and x64 Parallels builds recompile the portable
Direct2D core, provider, and oracle with MSVC 19.44 under `/W4 /WX`. The
Windows-only fixtures record the same rectangle-versus-concave-path and
multi-component-versus-nested-hole operations through genuine system Direct2D
and ProGPU, then compare dense point lattices for union, intersection, xor, and
exclusion; all four modes pass.

The follow-up comparison lane reuses the same normalized component/hole sets
and intrinsic-SIMD boundary broad phase. Transactional exclusions in both
directions establish equality and containment; intersection and exact
post-AABB boundary contact distinguish interior overlap, touching, and
separation without CPU rasterization. Local optimized and sanitizer fixtures
cover every relation. Clean Windows ARM64/x64 builds compare transformed
simple paths plus multi-component containment/equality, nested-hole separation,
and shared-boundary containment with genuine
`ID2D1PathGeometry::CompareWithGeometry`; all system relations pass.

The next path slice implements default solid-miter
`StrokeContainsPoint` for one simple closed contour. It inverse-maps the query
through arbitrary invertible affine world state, evaluates independent segment
distances in four-lane NEON/SSE2 batches, and handles convex and concave join
wedges in the topology-dependent scalar pass. Styled, flagged, open,
multi-figure, self-intersecting, or singular cases stay typed fail closed.
Optimized and sanitizer fixtures cover body, interior, exterior, convex miter,
concave join, and nonuniform transformed points. The Windows ARM64 system
oracle confirms the same results through genuine Direct2D; probes are kept
outside Direct2D's flattening-tolerance fuzz band when asserting strict
exterior results.

Same-factory solid bevel, miter, and miter-or-bevel style state now enters the
closed-path containment lane through typed COM getters. The SIMD body remains
shared while the dependent join pass selects the bevel wedge or qualified
miter extension and limit. Windows probes distinguish a bevel-clipped corner
from its included wedge and match genuine Direct2D after another clean ARM64
30-target rebuild. Round joins add an exact vertex disk over the same SIMD
body, and Windows inside/outside arc probes also match. Dashed styles remain
fail closed at that checkpoint pending common run geometry.

The dashed containment follow-up now consumes the compositor's existing
`curve_dash::try_create_runs(...)` output instead of maintaining a second dash
algorithm. Built-in and custom patterns, offsets, and all four dash-cap modes
share renderer-exact visible runs. Their independent line bodies are packed
into the existing four-wide NEON/SSE2 stroke-distance pass, while the ordered
cap and join topology remains scalar. The optimized and sanitizer suites pass;
genuine Windows Direct2D x64 and ARM64 differentials match body, gap, flat-cap,
and round-cap probes at a 0.001 flattening tolerance. Dashed
`GetWidenedBounds` now shares those runs and collects exact body, cap, and join
extrema. Round caps and joins add transform-dependent support points so affine
rotation, skew, and non-uniform scaling remain exact while the final transform
and bounds reduction keeps its four-wide NEON/SSE2 implementation. Flat and
round bounds, including a non-uniform transform, match genuine Direct2D on
Windows x64 and ARM64. Dashed `Widen` now emits isolated closed outlines for
joined line runs with flat, square, triangle, or round caps. Round
semicircles are two cubic quarter arcs, and the full ordered line/cubic command
set is constructed before sink replay. All endpoints and cubic controls are
batch-transformed by the existing NEON/SSE2 point path. Dense portable regions
match `StrokeContainsPoint`; genuine Direct2D x64 and ARM64 cap queries and
widen calls pass. The Windows seam probe confirms that closed figures retain
`DashCap` at the source seam, matching the compositor's original shared dash
semantics. Bevel, qualified miter, and miter-or-bevel joins now share one typed
offset-side builder. The low-limit miter-or-bevel path emits the two bevel
vertices, while qualifying miters emit the exact offset-line intersection;
dense containment and genuine Direct2D x64/ARM64 corner differentials pass.
Round joins annotate the outer side edge with its typed center and emit one or
two cubic circular spans during outline assembly. Their dense widened-region
probes and genuine Direct2D x64/ARM64 corner/widen differentials pass.
Over-limit `Miter` joins now compute the limit-normal plane and intersect it
with both outer offset edges, while `MiterOrBevel` keeps the two bevel points.
Separate inside-tip and outside-tip probes distinguish the clipped shape from
both alternatives on Windows x64 and ARM64. This completes all cap/join
combinations for dashed line runs in the simple closed-path domain; curved,
multi-figure, and general open-path widening remain broader geometry work.

The next containment increment removes the open-figure rejection for one
typed path figure. It reuses the intrinsic-SIMD segment-body buffers and the
same scalar dependent join predicates, but does not synthesize a close edge.
Solid source endpoints use `StartCap`/`EndCap`; visible runs produced by the
shared open curve-dash walker use those source caps only at actual endpoints
and `DashCap` at internal splits. Optimized and ASan/UBSan fixtures cover
solid/dashed body, gap, miter/round joins, flat source caps, and square dash
caps. The Windows 11 Parallels oracle recompiles and compares the same
portable/system `ID2D1PathGeometry::StrokeContainsPoint` probes successfully
on ARM64 and x64. Open `GetWidenedBounds` now shares the typed cap/join extrema
and four-wide affine transform/reduction path. It also preserves the
system-observed conservative source envelope for an endpoint inside a dash
gap. Default, round-join, dashed-square-cap, and nonuniform transformed bounds
pass locally and against genuine Direct2D on both Windows architectures. Open
`Widen` now reuses the joined outline builder for solid and dashed paths,
prepares all figures transactionally, retains round edges as cubics, and
batch-transforms endpoint/control data before caller-sink replay. Dense local
regions match `StrokeContainsPoint`; default and square-dashed captures match
genuine Direct2D on Windows ARM64 and x64. The shared curve-dash run buffer now
also publishes a terminal visible-point bit when the source ends exactly on a
gap-to-dash transition. Direct2D applies `DashCap` only on the new run's start
side and the source `EndCap` on the other side, producing a half-cap rather
than a symmetric dot; explicit walker, containment, and widened-output tests
lock down that behavior. Both the reusable semantic
path-stroke compiler and the MIL compositor consume the bit directly and
append only the qualified cap-only GPU primitives; no degenerate body, CPU
rasterization, readback, or per-item submission is introduced.

The following query increment partitions flattened path edges by their typed
figure index and enables mixed independent open/closed figures for
`StrokeContainsPoint` and `GetWidenedBounds`. Dash phase restarts per figure;
closed seams and open source caps stay figure-local. Containment is the union
of the figure predicates, while widened bounds union per-figure SIMD
reductions. A closed-square plus open-polyline fixture covers solid and dashed
body/gap cases and matches genuine Direct2D on Windows ARM64 and x64.

`ID2D1PathGeometry::Outline` also accepts multiple independent or
point-touching filled contours and non-touching alternate-fill nesting. Every
contour is tolerance-flattened and normalized before topology analysis; a
containment-depth pass reverses odd-depth hole boundaries so the result is fill
invariant. Winding nesting retains signed source contributions, sums ancestor
winding, omits redundant same-fill boundaries, and reverses true holes.

Two simple contours that cross or share a positive-length edge now use the
same generalized N-contour normalizer directly. Alternate fill and opposing
winding contributions select xor, while equal winding contributions select
union. The shared four-lane NEON/SSE2 edge-bounds broad phase rejects
independent boundary pairs; the dependent
split/classify/trace stages remain scalar. Contact-only T-junctions keep both
figures and insert the contact point into the touched edge before replay,
matching the system line callback transcript. Replay uses Direct2D's alternate
fill callback and leaves caller segment flags unchanged. Local optimized and
sanitizer filled-region tests plus Windows ARM64/x64 callback-count and dense
disjoint/corner-and-T-point-touch/shared-edge/alternate-overlap/winding-overlap/
alternate-hole/winding-hole differentials qualify the lane.

The generalized N-contour normalizer handles larger interacting sets in one
transaction. It splits every proper crossing and positive collinear overlap,
evaluates alternate parity or retained signed winding on both sides of each
sub-edge, removes internal/coincident boundaries, and traces the complete
result. Pair broad phases remain four-wide NEON/SSE2; side classification and
graph traversal are dependency-bound scalar stages. A one-million-segment cap
prevents unbounded replay state. Three-overlapping-rectangle tests match
genuine Direct2D ARM64/x64 callback counts, dense pair/triple membership,
alternate XOR area 15, and winding union area 20.

A single proper transverse self-intersection is detected through the same
four-lane NEON/SSE2 edge-bounds broad phase, solved once in double precision,
and split into two simple lobes before ordinary winding normalization. The
dependent lobe walk remains scalar. Local and genuine Direct2D ARM64/x64
bow-tie tests match the two-figure/six-line callback transcript, dense fill,
and area. Alternate-fill contours with multiple distinct proper crossings now
split every nonadjacent pair after the same SIMD broad phase, probe parity on
both sides of each sub-edge, discard internal edges, and trace all filled
lobes. Alternate and winding five-crossing pentagrams match genuine Direct2D
callback topology, area, and dense regions on Windows ARM64/x64. Winding
inputs now decompose into signed positive/negative integer layers before the
whole path is normalized. Mixed figures can therefore add or cancel part of a
self-intersection's winding magnitude: a reverse-wound square subtracting one
of a pentagram center's two layers matches genuine Direct2D callbacks, area,
and dense regions on ARM64/x64 without becoming a hole. Repeated or triple
crossing points, collinear or endpoint-ambiguous intersections, and numerical
invalidity remain transactional typed failures.

`ComputeArea` now invokes that normalized Outline transaction into a private
caller-owned contour sink and reduces the signed shoelace areas. The result
therefore subtracts holes, treats alternate overlap as xor, treats
equal-direction winding overlap as union, and preserves zero-area point/shared
contacts without independently summing source figures. The reduction has a
loop-carried sum and remains scalar; the independent boundary-pair work is
still four-wide NEON/SSE2 in the shared normalizer. Local optimized and
sanitizer hole/overlap checks plus genuine Direct2D ARM64/x64 shared-edge,
alternate-overlap, winding-overlap, corner-contact, and T-contact area
differentials pass, including arbitrary interacting simple-contour counts, the
qualified bow tie, alternate/winding five-crossing pentagrams, and mixed
signed-layer cancellation. Ambiguous crossings fail closed with the output
already initialized to zero.

Portable `ID2D1PathGeometry::Tessellate` now starts from the identical
normalized Outline contours. It associates each hole with the smallest
containing positive component, bridges holes rightmost-first without pixel
readback or raster fallback, and ear-clips every prepared component before
mutating the caller sink. Duplicate bridge endpoints are topological aliases,
while collinear bridge points are removed by the shared polygon cleanup. This
qualifies disjoint/overlapping/self-intersecting components, a single hole,
multiple holes, and nested islands. Optimized and sanitizer tests plus genuine
Direct2D ARM64/x64 differentials compare signed area and dense triangle
coverage; triangle ordering/count is intentionally topology-independent.

The same normalized self-intersection contours are now qualified as direct
inputs to `CompareWithGeometry` and `CombineWithGeometry`. Alternate-star
center holes report disjoint from a center rectangle, while mixed-winding
stars containing that rectangle report contains. All four Boolean modes match
their source predicates over dense local lattices and genuine Direct2D
ARM64/x64 outputs. Repeated/triple crossings and ambiguous contacts remain the
transactional rejection boundary; an arbitrary count of distinct proper
crossings no longer blocks these downstream COM consumers.

`Widen` now consumes that same partition and prepares the complete mixed-
figure transaction before caller-sink replay. Closed null/default strokes add
validated outer and inner rings; open solids and qualified dashed figures add
joined outlines, typed caps/joins, terminal half-caps, and cubic round edges.
Every point/control is batch-transformed through NEON or SSE2 before the sink
receives alternate-fill, force-unstroked closed figures. Dense local output
matches the union `StrokeContainsPoint` predicate. Genuine Direct2D ARM64 and
x64 validate successful multi-figure output, the default line-only transcript
region, and dashed output against the system containment oracle. Convex closed
figures now build paired typed side contours for solid bevel/round/miter joins
and for a dash run covering the complete source seam; round joins remain cubic
GPU paths. Dense local and Windows ARM64/x64 system-containment differentials
cover bevel, round, and full-cover custom dash output. Convex null/default
strokes omit the inner alternate-fill ring when inward erosion collapses or
reverses it, so exact-collapse and fully-consumed interiors match the system
implementation. Styled bevel, round, miter, and miter-or-bevel widening also
accepts non-convex closed figures whose flattened inner and outer side
contours remain simple and properly contained. That topology validation is
transactional and precedes every caller-sink callback. Dense concave
bevel/round output matches both `StrokeContainsPoint` and genuine Direct2D on
Windows ARM64/x64. Split/self-intersecting erosions and invalid topology
remain typed fail closed.

The native query/widening lane also preserves typed Direct2D path-segment
stroke flags. A `FORCE_UNSTROKED` edge splits the flattened figure into
independent open runs without a bridge segment: dash phase restarts per run,
true source endpoints use `StartCap`/`EndCap`, and split boundaries use
`DashCap` even when the dash style is solid. Closed figures rotate after the
last omitted edge to retain one coherent cyclic run. A
`FORCE_ROUND_LINE_JOIN` bit marks only the incoming source-segment boundary,
so curve-flattening subdivisions do not acquire synthetic round joins. The
same metadata drives solid/dashed containment, widened bounds, and
transactional cubic `Widen` output. Dense local lattices and genuine Windows
ARM64/x64 system-Direct2D differentials qualify all three operations without
CPU rasterization or pixel readback.

The open solid lane is also qualified over tolerance-flattened cubic and
quadratic source segments. Nearly collinear float directions are treated as a
straight continuation across containment, widened bounds, and outline
construction, preventing clipped multi-unit miter spikes from numerical
cross-product noise. Round-join containment is restricted to its actual outer
circular sector instead of accepting a full vertex disk. Dense local curve
output matches `StrokeContainsPoint`, and portable/system curve probes plus
successful `Widen` transcripts pass on Windows ARM64 and x64.

`GetWidenedBounds` now shares that default-miter path domain. Segment offsets
and miter extrema are constructed before the world transform; independent
candidate transforms and min/max reductions execute four-wide through NEON or
SSE2. This preserves Direct2D's stroke-before-transform result under
nonuniform affine matrices. Ordinary, concave, zero-width, and transformed
bounds match the genuine Windows system implementation after a clean 30-step
ARM64 core/provider rebuild, while optimized and sanitizer tests remain green
locally.

The closed-figure `Widen` output lane accepts a simple contour with a
nonnegative null/default miter stroke width, including concave contours whose
offset rings remain simple and non-collapsed. Outer
and inner offset contours, miter limits, and surviving inner topology are
validated transactionally before the caller sink is touched. Their independent
world transforms execute four-wide through NEON or SSE2, after which the sink
receives winding-fill figures with an in-place reversed inner contour and no
segment-flag mutation. Local tests compare a dense widened-fill lattice with
`StrokeContainsPoint`; Windows ARM64/x64 oracles compare convex, concave, and
styled output with genuine system Direct2D. Zero-width paths emit the system
`WINDING` callback and no figures; rectangles retain the system's two
coincident alternate-fill contours; transformed rectangles emit winding and
no figures. Callback-count comparisons prove that no Widen lane changes caller
segment flags. Unsupported collapsed or split/self-intersecting non-convex
offsets remain fail closed.

## Native local-cache geometry-clip checkpoint, 2026-09-05

Native MIL BitmapCache replay applies exact cache-root/ancestor vector clips
to the retained output, including a GPU composite with linear/radial gradient
opacity masks. Cache pixels and their own nested clips use an isolated clip
frame. Composite brush guidelines and SnapsToDevicePixels remain separate
from world-space output geometry. Mask-only changes preserve cached content
revision, and unchanged warm GPU replays perform no cache-content passes.

The same gate covers two sibling caches, nested caches, multi-guideline
gradient masks, and twice-resolution pages. The latter also repairs native
target scissoring that previously truncated oversized cache pages to the
window dimensions. See `NATIVE_CPP_ENGINE_SPECIFICATION.md` for exact
assertions and platform qualification. These additions do not remove the
remaining Viewport3D, programmable effect, tile-brush, nested effect-domain,
DirectX/Direct2D/Win2D, or final platform-parity gates.

## Viewport3D output geometry checkpoint, 2026-09-05

Viewport3D visuals now isolate exact vector output clips using the same native
layer masks as ordinary retained composition. Effects and BitmapCache reuse
their existing output isolation; no per-mesh mask support is advertised and no
CPU pixels are introduced. Typed cache bounds accept both Visual and
Viewport3DVisual resources. Depth targets include retained cache slots, and
group composites transition to depth-free passes while storing mesh depth for
later continuations.

Seven raw-MIL GPU cases cover sibling/ancestor clips, plain output, identity
and twice-resolution caches, gradient cache masks, blur, cached blur, and
nested caches. A later green plane behind each red/blue plane must remain
occluded. Identity-cache pixels equal uncached pixels exactly; unchanged
cache pages are not rerasterized. An uncached outer blur still performs two
sibling effect-content passes around the retained mesh pages. This does not
claim arbitrary mixed 2D/3D bundle, guideline, transformed-viewport, or full
lighting/shading-mode parity; those remain explicit implementation gates.

## Mixed 2D/3D composition checkpoint, 2026-09-05

Native draw bundles now carry a per-bundle depth requirement. Compilation
splits at 2D/3D transitions as well as clip/target/mask changes, and replay
switches compatible render passes without discarding stored mesh depth. A
frame containing 3D uses this transition-aware path even without isolated
layers; the all-2D single-pass path is unchanged. Layer-free successful frames
also clear stale layer diagnostics from the preceding scene.

Three additional raw-MIL cases draw cyan 2D content before and after the
red/blue Viewport3D siblings: exact vector clips, rectangular direct-to-window
clips without isolation, and nested retained caches. Cold/warm pixels and
submission counts are asserted alongside depth ordering. This is ten GPU
variants in total, not a claim of every possible draw-family transition or
complete Viewport3D transform/guideline/shading parity.

## Unchanged Viewport3D sidebands, 2026-09-05

`NativeMilViewport3DSnapshot` owns an immutable exact-byte baseline of the
camera, viewport, mesh/vertex/index/light arrays, materials, and gradient stops.
LibreWPF retains it only after a successful native binding. An unchanged update
skips the ABI call; changed producer arrays (even the same array instances) bind
and replace the baseline. Topology replacement creates new baselines with the
replacement channel, and disposal releases them. Capture failure after a partial
update keeps the existing fail-closed rebuild requirement.

The C++ MIL channel independently recognizes identical fully validated sidebands
and preserves both resource generation and compiled-scene cache. The shared
channel serves wgpu-native and Dawn; no backend-specific shader or ABI version
was added. Exact comparison includes reserved fields. Managed ABI tests prove
there is no implicit padding and mutate every byte, so neither hash collisions
nor source-object identity can conceal a change. Native tests cover all payload
families, invalid reserved/index values, and identical compiled output.

Matching uses runtime-intrinsic span comparison in managed code and platform
`memcmp` in C++; no new whole-buffer scalar comparison loop exists. The owned
managed baseline costs O(B) memory per viewport and a copy at initial/changed
binding; matching is O(B), allocation-free. The C++ check reuses existing owned
vectors. This does not optimize producer flattening or change GPU selection,
shaders, lighting, geometry quality, or device-loss semantics. The legacy managed
mesh extension uses its own pooled viewport resources, not this MIL ingress;
there is no second shader/mesh algorithm to port for this change.

The reusable component workload is in `tools/ProGPU.NativeMil.Benchmarks`.
Application frame-rate and producer-side allocation claims require separate
end-to-end evidence. See the engine specification for research and qualification.

## Tile-brush ingress and source DPI checkpoint, 2026-09-05

The native channel now decodes and retains canonical `MilCmdImageBrush`,
`MilCmdDrawingBrush`, and `MilCmdVisualBrush` packets (148-byte payloads).
The managed batch builder emits every field, including both mappings,
stretch/alignment, flip modes, animation/transform references, source handles,
and cache hints. MIL tile-mode numbering is **not** the portable host enum's
numbering; producers must map explicitly. Resource deletion protects all six
dependency slots, and retained fingerprint traversal follows those references.
Malformed updates roll back the complete batch, including resource generations.
Canonical `Rect.Empty` and WPF's unconstrained cache-threshold hints are retained;
cache hints must never drive unchecked allocation or extent arithmetic.

This ingress checkpoint was **resource-protocol support, not tile-brush rendering parity**.
The subsequent single ImageBrush subset below now paints selected fills; general
sampled-brush resolution remains unsupported. BitmapCacheBrush is also
unfinished. The coverage ledger records dispatch (104 top-level, 25 nested,
12 undispatched), not pixel/API completion. Do not infer rendering support from
the three new dispatch entries.

Copied and same-device `BitmapSource`/`DoubleBufferedBitmap` bindings now retain
source DPI atomically with content through additive `_with_dpi` C entry points.
Old C entry points and managed overloads retain their 96-DPI behavior. Both axes
must be finite/positive with finite natural dimensions; rejected bindings do not
change pixels, external-image state, generations, or compilation caches. No new
per-image metadata P/Invoke, pixel conversion, or readback is introduced. The
C++ `get_bitmap_source_dpi` query is a CPU-only diagnostic/planning surface.

LibreWPF carries DPI from the same portable pixel snapshot or from typed native
image providers, includes both axes in retained comparisons, and passes them in
the content binding. `IPortableNativeImageSource` adds default 96-DPI metadata for
existing providers. The source-built WPF image carrier snapshots that metadata,
forwards it through the interface/clone, and uses WPF's existing pixel-to-DIP
conversion for natural Width/Height. Explicit DrawImage rectangles and physical
texture sizes are unchanged. The existing pixel-copy adapter's scalar conversion
loop is unchanged by this metadata work; replacing it with matched intrinsic SIMD
remains part of the full goal, not a claim made by this checkpoint.

### Rendering continuation

WPF repeats a mapped **base viewport**, including transparent padding after
Uniform/None stretch; it does not generally repeat the entire bitmap. Viewbox
selects the source mapping, not a clip; Viewport clips the tile. Repeated
viewports need boundary-aware filtering. The full renderer must implement
this distinction before enabling repetition, using the same-device image and
exact path-mask infrastructure already shared with Direct2D. A retained mapped
tile texture can preserve padding and support Drawing/Visual sources; a direct
shader-domain path needs equivalent boundary/filtering semantics. Neither may
read pixels back, enumerate unbounded tiles, or submit once per tile. New sampling
shaders must be canonical sources consumed by managed and native backends.

Remaining gates include animated rectangles/opacity, absolute and relative brush
transforms, anisotropic source DPI, all stretch/alignment/flip combinations,
cropping/padding, exact fill/stroke/text/mask use, Drawing/Visual capture and cycles,
cache hints/retention/device loss, and Windows native-WPF pixel comparisons.
No tile-rendering performance or end-to-end parity claim is made here.

## Single ImageBrush rendering checkpoint, 2026-09-05

Original C++ MIL lowering now paints `TileMode.None` ImageBrush resources backed
by copied or same-device BitmapSource/DoubleBufferedBitmap sidebands on
non-rounded rectangular fills. Relative/absolute viewbox and viewport, all four
stretch modes, alignment, anisotropic source DPI, animated rectangle/opacity
resolution and absolute/relative brush transforms reuse retained typed state.
Source mapping is clipped by the transformed viewport and separately by the
paint geometry. Non-axis-aligned viewport clips use the existing exact vector
mask, never loose bounds. Viewbox itself is **not** a source clip.

This is O(1) mapping per fill and one image primitive, not CPU tile synthesis or
per-tile submissions. Existing shared image/filter/clip shaders run on the same
device; no new shader fork, readback, pixel repacking or compute fallback exists.
Saved image state owns opacity/clipping with an identity transform because the
image record already carries the full transform. No SIMD or application
performance claim is made for fixed-size mapping.

Qualification and reproduction:

- `progpu_native_direct2d_webgpu_tests` checks eight full nearest-neighbor frames
  against independent integer rectangles, warm-stream equality and five-command/
  one-draw/one-submission budgets. A ninth skew fixture checks exact viewport
  exclusion. MIL CPU tests reject all four repeat/flip modes without replacing
  caller output.
- Set `PROGPU_NATIVE_MIL_IMAGE_BRUSH_CAPTURE_DIR` to an absolute directory during
  the GPU test to capture nearest and linear variants. The original public-API
  `eng/mil/ProGpuWpfImageBrushOracle.cs` compares eight linear frames against
  native Windows WPF: every RGB channel within 1 and opaque output. It writes
  PNG evidence and a result including the WPF assembly identity. Run its wrapper
  with `powershell.exe -NoProfile -Sta -File` and `-ProGpuCaptureDirectory` /
  `-OutputDirectory`, or compile the C# executable against WindowsBase,
  PresentationCore and System.Xaml and pass those directories. No VM script
  policy changes are necessary.
- macOS native 15/15, Windows ARM64 native 16/16, LibreWPF 1,479/1,479 and the
  source-built native host smoke pass. macOS linear frames match Windows ARM64
  native .NET Framework WPF across 32,768 pixels. The CI comparison job requires
  separate D3D12/Metal/Vulkan captures; exact-head hosted completion is separate.
  Ubuntu ARM64 GCC 13 MIL CPU tests also pass with ASan, UBSan and leak detection
  enabled (0.80 seconds); this is not a Linux GPU image comparison.
  After merging latest main `102e39e5`, managed tests pass 3,936/3,936, headless
  tests 240/240, LibreWPF 1,479/1,479 and the source-built host passes again.
- Windows WPF software RenderTargetBitmap blended this two-pixel ImageBrush
  when nearest-neighbor was requested, while ProGPU used nearest. The comparison
  explicitly sets Linear on both sides; it does not relax pixel thresholds or
  claim nearest-WPF parity. Further sampling qualification remains required.
- Enabling linear captures on the Parallels Display Adapter (WDDM), D3D12,
  exposes a separate failure: MIL carries the correct Linear enum but the GPU
  returns nearest-filtered pixels. A fresh engine reproduces it; changing the
  sampler LOD clamp did not fix it and that experiment was reverted. The normal
  16-target Windows regression above did not enable linear captures and is not
  evidence that this new comparison passes on Parallels hardware.
  `--mil-image-brush-only` reproduces the focused hardware test without the
  unrelated Direct2D cold shader workload; `--mil-image-brush-software` explicitly
  selects a CPU WebGPU adapter or fails if unavailable. Neither changes the
  production adapter policy or silently substitutes a different device.
  The focused test passes on Windows ARM64 Microsoft Basic Render Driver
  (D3D12/WARP), including the linear-sampler probe, eight captured variants and
  skew exclusion. All eight WARP linear captures then match native Windows WPF
  across 32,768 pixels at the same tolerance 1. This isolates the observed
  mismatch to the Parallels-adapter path, not a general Windows comparison
  failure; a qualified same-device remedy still belongs to the full goal.

Remaining: repeated/flipped base tiles, DrawingBrush/VisualBrush and DrawingImage/
D3DImage brush sources, arbitrary/rounded/elliptical fills, strokes, text, masks,
cache hints and device-loss/cycle handling for captured tiles. Animation/alignment
extremes, external-image brush pixels and transformed target cases need broader
differential coverage. Unsupported uses fail closed. The earlier general image
draw transform composition should also be audited; this checkpoint fixes
transform ownership only for its new brush lowering.

## Implementation-first sequencing, 2026-09-05

At the user's request, continue feature implementation and compilation before
the final exhaustive validation pass. Do not repeatedly run VM/image/sample,
unit/headless, sanitizer or benchmark suites between implementation slices.
Keep authoring regressions alongside features, regenerate ABI/protocol sources
when their authorities change, compile the changed dependency graph, and commit
the implementation with its outstanding qualification listed. Existing CI gates
remain enabled; a queued or historical pass does not qualify a new head. Nothing
in this sequencing change reduces the full MIL/DirectX/Direct2D/Win2D goal or
permits declaring unvalidated work complete.

### Configurable same-device base-level image sampling

The preliminary explicit-nearest plus explicit-linear shader experiment on the
Parallels D3D12 adapter completed the focused native ImageBrush test and matched
all eight native Windows WPF oracle frames (32,768 pixels, RGB tolerance 1).
This was before the implementation-first request and before the final policy
integration below. It is evidence for the algorithm, **not qualification of the
integrated implementation**. An earlier linear-only experiment passed the linear
probe but changed subsequent nearest warm replay (case 1, byte 5200: 255 to 250),
so the compatible path must cover both nearest and linear sampling.

Implemented, runtime qualification deferred:

- `WgpuContext.ImageSamplingPreference` is an initialization-only typed policy;
  `ImageSamplingPath` reports the resolved implementation. The environment
  selector is `PROGPU_IMAGE_SAMPLING=auto|native-sampler|explicit-shader`.
  Automatic chooses explicit reconstruction for Parallels Display Adapter on
  D3D12 and retains the native sampler elsewhere. Forced native sampling fails
  closed on that known-unqualified adapter. No adapter/device is substituted.
- The managed compositor snapshots the path at construction (no adapter-name
  scans per image). Native compositor creation/recovery maps it to the generated
  `PROGPU_NATIVE_ENGINE_IMAGE_EXPLICIT_SHADER_SAMPLING` flag. Raw C/C++ clients
  supply that flag after adapter qualification; the C engine cannot identify an
  opaque external device's adapter. Child engines inherit the flag, and browser
  and Dawn entry points accept it independently of mutually exclusive glyph
  fallback flags. Native flags now have generated C# literal authority.
- Original canonical `ProGPU.Backend/Shaders/Texture.wgsl` reconstructs nearest
  with one base-level `textureLoad`, and linear with four loads and bilinear
  interpolation at texel centers. Per-tap clamp/repeat/mirror addressing reuses
  the original ProGPU bicubic helper. Texture alpha representation is retained
  during interpolation. Regular, masked and color-matrix image entry points
  share selection and avoid an unused hardware sample before reconstruction.
- Original managed `Compositor.CompileTexture` and native semantic-image plus
  direct-image vertex producers carry the same reserved coefficient encoding
  (-128 nearest, -64 linear). This changes no wire record size, resource lease,
  retained upload/submission count or image ownership boundary. Selection and
  vertex preparation are O(1) per quad; fragment work is bounded 1/4 texel loads
  with O(1) private storage. There is no CPU fallback, readback or pixel repacking.
- This is a **base-level image policy**, not a universal sampler override.
  Cubic/Fant, mipmapping, anisotropy, masks' own sampling and programmable effect
  sampler algorithms remain separate. Do not claim that their Parallels behavior
  is qualified by these eight images, or replace them with level-zero sampling.

Research/provenance: the existing [cross-engine design record](progpu-avalonia-rendering-research.md)
covers Skia/SkParagraph, Direct2D/DirectWrite/Win2D, WebRender, Vello/Parley and
HarfBuzz. This slice preserves its retained-scene, device-domain, lazy pipeline,
batched GPU and CPU shaping boundaries; it changes no culling, glyph/font cache,
font fallback, DPI placement or layout policy. The additional public contract
references are [WGSL textureLoad](https://www.w3.org/TR/WGSL/#textureload) and
[textureSampleGrad](https://www.w3.org/TR/WGSL/#texturesamplegrad). Adopt explicit
same-device texel access and original ProGPU addressing; reject CPU synthesis,
per-item passes and foreign implementation code. No performance speedup is
claimed without the deferred matched measurements.

Deferred final gates for this slice:

Compilation checkpoint: the Release `ProGPU.Tests` dependency graph builds with
zero warnings/errors (test execution deliberately deferred), and the macOS
Clang C++20 `progpu_native_direct2d_webgpu_tests` target builds and links. The
native C# contract was regenerated. `origin/main` was fetched with zero commits
missing from this feature branch. These are build facts, not runtime passes.

1. Execute the new `ImageSamplingPolicyTests`, native Dawn flag-combination
   cases, native image/semantic/Direct2D tests, managed/headless suites, and
   shader resource/ABI checks against the final binaries.
2. Repeat the final integrated Parallels hardware/WARP and Metal/Vulkan oracle
   comparisons, including alternating nearest/linear warm draws. Add managed
   versus native forced-path comparisons for repeat/mirror seams, alpha,
   color-matrix/mask entry points, external textures, transforms, and one-pixel
   dimensions; retain independent native-sampler controls on qualified adapters.
3. Run device-recreation and child-engine cases, browser/Dawn builds and live
   provider cases, all RID packaging, and unchanged-workload retained upload,
   allocation and submission counters. Add final Instruments/Release matched
   measurements before making any speed claim.
4. Execute the broader MIL, DirectX samples, native Windows comparisons,
   package-mode SDK/Toolkit/AvalonDock and available licensed Xceed/SciChart
   gates; require every final-head PR CI gate to pass before integration.

## Vector and shared-image single-tile sources, 2026-09-05

Implementation-first continuation (runtime verification deferred): canonical
`DrawingBrush` now replays its drawing graph through the native MIL decoder for
`TileMode.None` rectangle fills. `ImageBrush` additionally accepts DrawingImage
and synchronized D3DImage sources through the existing native image/source
bindings. BitmapSource/DoubleBufferedBitmap remain on their direct image path.

DrawingBrush relative Viewbox mapping includes the drawing bounds origin;
DrawingImage instead has a zero-origin natural image extent and its existing
image replay removes the underlying drawing's bounds origin. Existing exact
typed DrawingImage bounds are reused when supplied; otherwise the existing
native drawing-bounds traversal is used. Empty sources are no-ops. D3DImage
requires the existing live external-image binding and keeps its pixel extent;
no texture lease, synchronization, upload, or native-handle contract changes.

Vector tiles use one existing isolated GPU layer: brush opacity and the exact
paint/viewport mask apply once when that layer is restored, not once per child
primitive. Source drawing opacity and source clip/mask scopes remain inside the
layer. Its source scope has independent clip scratch; outer vector mask prefixes
are retained, not overwritten by nested decoding. DrawingBrush recursion uses a
borrowed fixed-size DrawDrawing packet with the existing depth and active-resource
checks. DrawingImage uses the original nested image replay and adds the brush
handle to the same active-resource set. No CPU raster surface, pixel conversion,
temporary WPF shape, or reflection bridge is introduced.

This extends original ProGPU `append_drawing_image`, drawing-bounds inference,
`append_render_stream`, and isolated-layer output clipping from checkpoint
`419047e6`; no foreign implementation is copied. The existing cross-engine
research record and single-tile mapping design remain applicable. Compilation is
O(D) for D visited drawing records (plus existing bounds inference when needed),
with bounded recursive/cycle tracking; fixed source/viewport mapping is O(1).
Rasterization/composition stays on GPU. The current isolated vector tile can
occupy a target-sized layer; tighter retained tile-page allocation and reuse are
still required before performance qualification. This is not a SIMD speed claim:
there is no new CPU pixel kernel, and resource-graph traversal is dependency-based.

Paired implementation: LibreWPF's native compiler now emits DrawingBrush packets
from `IPortableTileBrushSource` plus typed drawing contracts, and removes the
DrawingImage rejection from ImageBrush. Null brush content becomes a native null
source. The existing managed portable `WpfDrawingReplay` already dispatches
DrawingBrush/DrawingImage sources and scopes brush opacity; it is not replaced
or disabled. The final differential gate must compare these two replay paths.

Build evidence only: native C++20 MIL/image targets built before an unrelated
artifact-directory removal; final native MIL sources and new source/cycle
fixtures were subsequently compiled in a separate temporary Release build.
LibreWPF's changed compiler and authored batch regressions also build. No new
runtime, GPU image, sanitizer or performance tests were executed, as requested.
The generated coverage ledger was refreshed; its counts remain 104 top-level,
25 nested and 12 undispatched, and do not measure semantic completeness.

Deferred checks include vector bounds-origin mapping, DrawingImage versus
DrawingBrush equivalence, overlapping child opacity, nested source masks and
effects, non-axis-aligned viewport clips, cycles and output preservation,
external-image device loss, and managed/native/Windows WPF pixels. The new native
fixtures cover DrawingBrush and DrawingImage source construction plus cycles;
the new LibreWPF tests cover typed packet emission without bitmap conversion.
They are compiled but not yet run.

Remaining brush implementation is explicit: VisualBrush graph capture, retained
base-tile repeat/flip sampling, arbitrary/rounded/ellipse fill coverage, strokes,
text and opacity-mask uses, and cache hints/recovery. Repetition must sample the
**mapped and clipped base tile**, not simply wrap the original source bitmap.
The next shared primitive should retain one GPU-owned tile page per source and
mapping generation, render its source subtree once, and composite one paint
quad with inverse-mapped repeat/mirror coordinates. Its cache key must include
source revision, source bounds, viewbox/viewport mapping, raster scale/format and
device domain; opacity and final paint clipping belong to the composite rather
than tile content. Same-device tile-image views require typed leases and bounded
page residency. Both renderers must consume the same tile sampling shader and
quality policy. Do not substitute CPU tile synthesis or per-tile submissions.

## VisualBrush source replay and shared recursion, 2026-09-05

Implementation-first continuation: `TileMode.None` VisualBrush rectangle fills
now enter the existing native `append_visual` renderer inside the isolated
single-tile GPU layer. Canonical Visual state, child traversal, retained content,
clips, opacity, effects and cache behavior use the same executor as onscreen
visuals, subject to that executor's existing capability limits. Brush opacity
and final paint/viewport masks remain outside source replay. No WPF-shaped
object, reflection adapter, CPU rasterizer or new shader fork is introduced.

VisualBrush natural bounds come from the existing typed Visual descendant-bounds
sideband (cache-named for ABI compatibility). They are not guessed from the
destination viewport or arbitrary UI properties. The source bounds origin is
included in relative Viewbox mapping, like DrawingBrush. Missing native source
bounds fail closed. The LibreWPF producer explicitly emits these bounds for
visual brush sources, including when an already-recorded onscreen visual is
referenced; known empty typed bounds become a null/no-op brush source.

Native visual and drawing traversal now share one active-resource set across
VisualBrush boundaries. A cycle cannot evade detection by re-entering the visual
renderer through a drawing or brush with a fresh local set. Mixed source nesting
is bounded by the existing maximum depth. The compile context carries the scene
identity and a nested brush-source depth. Dynamic guidelines suppress animation
while rendering a brush source without changing the caller's request flags or
the surrounding onscreen phase. Brush-mode cached visual pages have separate
owner/content identities from onscreen pages because their guideline realization
may differ even within the same frame.

LibreWPF source production distinguishes a brush reference from visual parenting.
Multiple brushes and the ordinary visual tree may reference one source visual
handle, regardless of traversal order, while multiple actual parents and active
visual/drawing source cycles still fail closed. Exact bounds are emitted once per
source handle. Source traversal and graph checks are O(V + E) with O(V) identity
sets for V visual/drawing resources and E references; no per-frame property scan,
CPU pixel work or new independent-lane CPU kernel was added. Native retained
rendering continues to own GPU work. No performance improvement is claimed.

Provenance/applicability: original ProGPU native visual execution and dynamic
guideline policy from `87747134`, plus LibreWPF's typed source compiler. The
existing managed `WpfDrawingReplay.TryReplayPortableVisualBrushFill` already
uses `WpfVisualTreeRenderer.ReplaySubtree`; that path remains available and must
be compared with the native path in the final differential pass. Existing
cross-engine source/scene/resource ownership research remains applicable; no
foreign implementation was copied.

Compilation checkpoint: the Release Apple Clang C++20 native MIL target and the
LibreWPF compiler/test graph build. The initial managed rebuild reported existing
WinForms compatibility/style warnings; the subsequent incremental build completed
with zero warnings/errors. Native VisualBrush source/cycle fixtures and managed
source-reuse (both tree orders), multiple-parent, and self-reference regressions
are authored and compiled. They have not been executed. The MIL source-digest
ledger was regenerated; runtime/VM/image/sanitizer/performance/CI qualification
remains deferred under the requested implementation-first sequence.

Final validation must include source root transforms/offsets, exact descendant
bounds under clipping/effects, mixed VisualBrush/DrawingBrush/DrawingImage cycles,
maximum-depth failure, nested dynamic guidelines, simultaneous onscreen and brush
cache ownership, invalidation and device loss, shared-source producer order, and
native Windows WPF versus both ProGPU renderers. Repeated/flipped retained tile
pages, broader fill/stroke/text/mask brush coverage and cache performance remain
implementation work; this checkpoint does not complete those requirements or
the full goal.

### Implementation-first checkpoint: curved primitive tile-brush fills

Native MIL single-tile image, DrawingImage, DrawingBrush and VisualBrush fills
now accept ellipses and rounded rectangles, including unequal corner radii.
The fill shape is appended to the inherited vector-clip chain before the paint
bounds and transformed viewport are intersected. Bounds remain scissors/mapping
metadata, not replacements for curved geometry. The path carries the complete
geometry-to-target affine transform exactly once; brush transforms affect source
placement and viewport, not the paint shape. Isolated vector-source layers apply
the resulting mask and brush opacity to the completed source image.

This reuses original ProGPU full-ellipse arc encoding, rounded-rectangle segment
emission and native vector mask execution. No foreign renderer code, managed
reflection, CPU image synthesis, readback or per-pixel fallback was introduced.
Fixed shape emission adds one ellipse segment or eight rounded-rectangle segments
directly to reusable clip scratch, without constructing stroke contours. Emission
is O(1) time/space per primitive; serializing the bounded inherited mask chain is
O(S + P + B) in segments, paths and boolean nodes. Rasterization stays on the
existing shared GPU path. No new independent-lane CPU kernel or performance claim
is introduced. Managed applicability: the portable WPF tile-brush ellipse/geometry
replay already exists and remains unchanged as the comparison implementation.

The Release Apple Clang C++20 MIL library and regression executable compile.
An initial test compilation used an incorrect mask-resource enum; it was corrected
to the existing layer-mask resource plus vector-chain kind. Sixteen source/shape/
transform cases are authored, including inherited ellipse clips, asymmetric
rounding, skewed brush viewports and transformed paint geometry. They are compiled
but **not executed**. The source-digest ledger was regenerated, not verified.

Final qualification must execute these cases and compare native Windows WPF,
ProGPU Windows, macOS and Linux images, including edge aliasing, extreme/clamped
radii, mirrored/nonuniform transforms, clip-stack restoration, overlapping vector
content opacity, and degenerate shapes. Arbitrary path/group/boolean fills,
tile-brush strokes/text/masks and repeated/flipped retained tile capture remain
implementation work. This checkpoint is not a parity or release qualification.

### Implementation-first checkpoint: path/group/boolean tile-brush fills

Following the curved-primitive checkpoint, native MIL now feeds already-lowered
PathGeometry, GeometryGroup and CombinedGeometry fill programs into the same
single-tile brush replay. This covers image, DrawingImage, DrawingBrush and
VisualBrush sources without a new host-specific rendering path. Existing path
fill rules, selected per-point segment data and group winding/boolean programs
are reused. Geometry transforms remain on the mask and brush-use mapping; brush
transforms do not deform the paint shape. Existing pen execution is unchanged:
tile-brush pens and previously unsupported combined-geometry strokes are not
claimed as implemented by this change.

The native helper appends fill segments to inherited clip scratch, rebases only
leaf segment references in bounded postfix boolean programs, and retains the
existing 64-path/63-instruction limits. Empty boolean programs now publish zero
offsets in geometry, transformed-rectangle and curved-primitive clip descriptors,
as required by the existing native mask contract. In particular, a simple paint
or viewport clip following a boolean geometry mask no longer carries a spurious
nonzero program offset. No geometry is reduced to its bounding rectangle.

Provenance/applicability: original ProGPU MIL fill lowering, typed scene mask
descriptors and GPU vector-clip execution; the managed portable geometry tile
replay remains the counterpart. No foreign code or new shader algorithm is
introduced. Appending uses contiguous vector range copies (O(S + B) time and
scratch); the conditional reference fixup is bounded protocol metadata handling
over at most 63 instructions, not an image/geometry compute fallback. Existing
GPU execution owns rasterization. Performance, memory residency and SIMD claims
require the deferred measurements; none are made here.

The native Release library and test executable compile. The authored matrix is
now 40 source/shape/transform cases, adding curved path data, even-odd geometry
groups, difference geometry, local geometry transforms and boolean masks followed
by skewed viewports. These cases have **not run**. The MIL source-digest ledger
was regenerated; no runtime, VM, pixel, sanitizer, benchmark or CI qualification
was performed. Final tests must additionally cover nested mixed winding rules,
all boolean operators, inherited boolean clips, sibling clip restoration,
per-point guidelines and paths with nonfilled/open figures against native WPF.

Remaining brush implementation includes repeat/flip retained tile capture,
tile-brush strokes, glyph/text and opacity-mask use, plus cache ownership and
invalidation integration. Existing broader MIL/DirectX/Direct2D/Win2D and final
cross-platform qualification gates remain open.

### Implementation-first dependency: occupied tile-page sampling

Repeated/flipped MIL tiles need a GPU page containing the **mapped base tile**,
including transparent stretch padding. A pooled texture may be larger than that
page. Wrapping normalized allocation UVs would repeat unused storage, and merely
clamping addressed UVs would break bilinear seams. The next capture stage therefore
uses an explicit occupied-extent sampling primitive, now implemented in the shared
`ProGPU.Backend/Shaders/Texture.wgsl` module.

`patchKind = -2` identifies a premultiplied zero-origin tile page. Vertex color RG
contains occupied integer texel width/height, A is composite opacity, and UVs are
normalized to the occupied page rather than the allocation. The existing address
fields select clamp/repeat/mirror independently per axis. Base-level nearest and
linear use respectively one and four texel loads, applying addressing separately
to each tap. Masked, unmasked and semantic-color-matrix entry points share this
sampling function and interpret tile opacity without treating the extent as color.
Existing ordinary image, atlas, cubic and Fant encodings remain separate.

Original ProGPU-owned provenance is the explicit base-image sampling implementation
at `723f91ce`: its texel-center and integer addressing algorithms are now shared
between full-image and occupied-extent samplers. No foreign implementation was
copied. The native `try_write_tile_page_quad` in the existing geometry-base helper
and managed `ProGPU.Vector.TileImageVertices.TryWriteQuad` produce matching four-
vertex payloads from a target rectangle and target-to-tile affine transform.
Both reject invalid extents, unsupported address values, nonfinite coordinates and
opacity, and leave caller output untouched on failure. Vertex generation is O(1)
time and fixed stack storage; managed coordinate math uses runtime-intrinsic
`Vector2`, while native four-corner emission is bounded protocol construction,
not a whole-buffer CPU compute fallback. Shader work is O(1) per covered pixel,
with no CPU pixel synthesis, repacking, readback or per-tile submission.

Research/contract references: the [WPF TileBrush overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/tilebrush-overview)
separates content, base tile and output area; [Direct2D image brush properties](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1helper/nf-d2d1_1helper-imagebrushproperties)
separate source extent, axis extension and interpolation. These contracts inform
the extent/address separation, not implementation text. The existing
[cross-engine rendering research](progpu-avalonia-rendering-research.md), including
Skia/SkParagraph, DirectWrite/Direct2D/Win2D, WebRender, Vello/Parley and HarfBuzz,
continues to govern bounded page residency, retained identity and immutable source
ownership. This change does not alter shaping, layout, fonts or glyph caches.

Build-only result: native MIL library/test executable and the managed renderer
test project compile; the final managed build has zero warnings/errors. The native
shader embedding was regenerated. Matched encoder cases cover all nine axis-mode
pairs with nearest/linear, negative/repeated coordinates, a 3x5 occupied page in a
64x64 allocation, output-tail preservation and transactional invalid extents.
They are **not executed**. Embedding WGSL is not GPU pipeline compilation; shader
validation, pixel parity and all runtime/VM/benchmark/CI qualification remain deferred.

This is an explicit shader primitive, not a new automatic fallback decision.
Callers must own a same-device premultiplied page lease and select a qualified
sampling path. It must not silently replace cubic, Fant, mipmapped, anisotropic or
forced-native-sampler modes. The primitive is not yet called by MIL tile capture;
MIL repeat/flip remain fail-closed. Remaining integration: retained capture and
generation/device/raster-scale keys, source-to-page mapping and padding, bounded
leases/eviction, one output-quad scene operation, typed configuration/diagnostics,
managed/native callers and final seam/alpha/mask/transform differential images.

### Implementation-first checkpoint: retained tile composite scene operation

The occupied-page sampler is now connected to native retained-layer restoration.
`PROGPU_NATIVE_SCENE_RESOURCE_TILE_COMPOSITE` (18) carries one pointer-free,
64-byte record: output bounds in parent logical target coordinates, an affine
output-to-normalized-tile mapping, and independent clamp/repeat/mirror address
modes. `PROGPU_NATIVE_SCENE_LAYER_CACHE_TILE` extends the existing 64-byte layer
record: `reserved1` references that resource, while `reserved0` remains an
identity-transform, clip-only final composite state. Tile layers require the
existing zero-origin local-content cache and reject Fant. Ordinary layer flags,
layouts and source capture remain unchanged.

The native scene builder and serialized-stream validator enforce the resource
kind, exact size, finite bounds/mapping, reserved zeros, address range, required
local-cache state and composite-state restrictions. Native scope hashing includes
the tile resource's contents instead of its table ordinal, so enclosing retained
content responds to mapping changes without confusing resource reordering with
pixel changes. The tile page itself still uses the existing caller-owned content
revision and cache-owner identity: final mapping, clipping and opacity are
composite state, not inputs to its captured pixels.

At restore, the GPU-backed native executor emits exactly four tile vertices and
uses the existing layer-slot source texture, occupied extent, lifetime and bounded
cache ownership. The output quad is positioned relative to the parent target;
UVs are computed in parent logical coordinates before that translation. This
uses the prior canonical occupied-page shader, with nearest/linear selected by
the existing cache sampling flag. It does not emit one command per visible tile,
copy pixels to CPU, or create a second GPU resource owner. Construction remains
O(1), plus existing retained-stream and cache work; no speedup is claimed.

Managed/native applicability: the C header generates `NativeSceneTileComposite`;
`NativeSceneStreamBuilder.TryAddTileCompositeResource` and `NativeSceneLayer` expose
the paired typed stream contract and matching failure rules. The public C++ named
module exports the descriptor/address modes and has an import-based consumer
case. The original ProGPU local-layer execution and occupied-page sampling at
`97eeba44` are the implementation provenance. Cross-engine ownership and WPF/
Direct2D source/base-tile research remain as recorded in the preceding section;
no foreign source implementation was introduced.

Build-only work includes the GPU-backed Apple Clang C++20 shared library, the
header-based native MIL test executable, the LLVM Clang 22.1.8 C++20 named-module
consumer, and the managed renderer test project.
The latter compiles with zero warnings/errors. A native test initially requested
a validator symbol outside the MIL executable's link graph; the builder case now
checks stream production there, with full stream validation remaining in its
own final gate. Positive resource/layer construction and invalid addressing,
wrong resource kinds and unsupported Fant combinations are authored, not run.
Generated C# was refreshed. No runtime, GPU shader validation, images, VMs,
sanitizers, benchmarks or hosted-CI qualification ran.

MIL repeat/flip is still fail-closed: the scene operation is ready for explicit
consumers, but MIL must still record source content into a page, define exact
source/mapping/raster-scale revisions, apply same-device leases and invalidation,
and select/report a qualified execution policy without overriding forced sampler
or high-quality modes. Managed renderer capture/recording integration also remains.
Final qualification must include oversized pool padding, seams, nested local
targets, output-only changes with unchanged page contents, enclosing-cache
invalidation, resource-table reordering, malformed streams, eviction/device loss,
all alpha/mask cases and native Windows WPF comparisons.

### Implementation-first checkpoint: MIL repeated source capture

MIL Tile/FlipX/FlipY/FlipXY brushes now lower through retained base-page capture
and the preceding tile-composite operation for base-level nearest/linear sampling.
ImageBrush bitmap and synchronized D3DImage sources, DrawingImage, DrawingBrush
and VisualBrush use the same mapped page and output primitive. The authored image
matrix currently exercises bitmap/DrawingImage/DrawingBrush/VisualBrush; the shared
D3DImage route still requires its final live binding/fence test.

The producer first computes the ordinary Viewbox/Viewport/Stretch/alignment map,
then captures it into a zero-origin GPU page. Raster dimensions derive from the
viewport basis lengths under the brush/paint transform and uniform device DPI.
Occupied page extents are whole physical pixels. Float layer bounds are adjusted
down by at most one ULP when needed so the executor's ceil operation cannot add
an unintended transparent texel. Unrepresentable extents fail closed; native
device limits and existing page budgets still govern allocation.

Paint geometry is clipped before capture setup but applied to the restored output,
not to page content. Repetition has no single-viewport output clip. The source is
mapped/clipped by the page target, preserving Uniform/None transparent padding;
one target-to-normalized-tile transform and independent repeat/mirror axes cover
the final output quad. Brush opacity applies once at restoration, including vector
sources with overlapping primitives. Bitmap capture saves an identity transform
state because its image record already owns the complete source-to-page mapping.
Vector sources retain their existing visual/drawing traversal and isolated clip
scratch. No CPU image synthesis, pixel readback or per-visible-tile draw loop was
added.

Cache owner identity includes scene/brush identity, source-to-page mapping, source
extent, raster extent/DPI and inherited sampling/text/render options. Source graph
generations extend that identity into a content revision. Output-only translation,
opacity and repeat/mirror selection do not enter captured pixels. Native cache
revision traversal now follows Visual/Viewport3D source subtrees reached through
brush dependencies, not merely the visual handle's own generation. Resource and
visual revision recursion are depth-bounded and cycles fail closed. Graph hashing
is O(R) for R visited dependency occurrences, with bounded recursion sets (shared
subgraphs are not memoized by this traversal); emission is O(1) per brush plus
the existing source traversal/scene work. GPU residency/reuse uses the existing
local-layer owner and slot budget. No performance claim is made without the final
retention and timing measurements.

Sampling is configurable through the existing `PROGPU_IMAGE_SAMPLING`/typed context
preference. Automatic and explicit-shader select the required occupied-page shader;
forced native-sampler is incompatible and rejects tiled restoration. The new
generated engine flag `PROGPU_NATIVE_ENGINE_IMAGE_REQUIRE_NATIVE_SAMPLING` (16)
preserves that forced preference for native/Dawn/browser engines and is mutually
exclusive with explicit sampling (8). Raw C hosts requesting forced native behavior
must set 16; zero remains automatic. `NativeCompositor.TilePageSamplingPath` exposes
the separate resolved tile-page policy, so ordinary image sampler diagnostics do
not imply that occupied pages use hardware addressing. Tile capture rejects Fant,
cubic/mip/anisotropic modes rather than reducing their quality; nonuniform DPI also
remains explicitly unsupported by this initial capture path.

Original ProGPU MIL single-tile source replay, cache revision traversal, local-page
ownership and shared occupied-page sampling from `1c6d7524` are the provenance.
Managed applicability: typed LibreWPF source production already emits every tile
mode; NativeCompositor propagates the forced sampling contract, while the portable
managed WPF replay remains available alongside native MIL. Moving the managed
portable recorder onto this same retained capture operation remains work; its
historical tile loop is not presented as equivalent GPU batching performance.

Build-only checkpoint: the Apple Clang C++20 GPU shared library and native MIL test
executable compile, and the managed renderer test project builds with zero warnings/
errors. Native regressions now author 32 repeat-mode/source/filter combinations,
elliptical paint plus inherited clipping, a fractional viewport with Uniform padding,
address-mode resource assertions and repeated source-cycle rejection. Existing 40
single-tile shape cases remain. Typed tile-policy cases are authored on the managed
side. **None have run.** C# constants and the MIL coverage source digest were
regenerated; no runtime, GPU shader validation, VM/image, sanitizer, benchmark or
hosted-CI qualification was performed.

This supersedes the earlier blanket repeat/flip rejection for the stated sampling/
DPI scope only. Final qualification must include fractional DPI and phase, skew/
reflection, viewbox origins/crops, padding/seams against poisoned pool storage,
shared-source and same-brush different mappings, output-only reuse, nested cached
visual changes, effect sampling beyond a tile edge, D3DImage updates/fences, forced
native rejection before output, device loss/eviction and native Windows WPF parity.
High-quality filtering/nonuniform DPI, tile-brush strokes/text/opacity masks,
managed retained capture adoption and broader MIL/DirectX/Direct2D/Win2D gates remain.

### Implementation-first checkpoint: occupied-page Fant sampling

Repeated MIL ImageBrush, DrawingBrush and VisualBrush capture now accepts Fant in
addition to nearest and linear. This supersedes the Fant rejection in the previous
capture checkpoint; cubic, mipmapped/anisotropic modes and nonuniform DPI remain
unsupported. `CacheFant` is permitted with `CacheTile`, while simultaneous nearest
and Fant flags remain invalid. Native restoration preserves the tile-page vertex
marker so ordinary cached-image Fant handling cannot replace its occupied extent
or addressing behavior. No ABI layout or numeric constant changed.

The canonical `Texture.wgsl` now shares the existing ProGPU bounded Fant footprint
algorithm between ordinary images and occupied tile pages. It retains the square-
root-of-two threshold and fixed 4-by-4 stratified footprint. Occupied-page samples
use explicit bilinear texel loads with repeat/mirror addressing per tap, bounded
to the occupied page rather than the pooled allocation: at most 64 loads per
fragment. The unwrapped UV enters the footprint calculation and each stratum is
addressed independently, preserving mirror phase under skew/reflection. This is
the existing bounded ProGPU filter, not a claim of exact general WPF resampling.
Ordinary image Fant retains its existing hardware-sampler path; extending the
full-image explicit-sampling policy to Fant is separate remaining work.

Both native and managed quad encoders emit the Fant coefficient. The managed
encoder exposes typed `TileImageSampling` and preserves its boolean overload as
a compatibility forwarder. Native and managed scene builders accept the same
tile/filter contract. This reuses original ProGPU texture filtering, occupied-page
addressing and local-layer capture code; no foreign implementation or CPU pixel
fallback was introduced. CPU emission remains a fixed four-corner operation;
filtering stays in the shared GPU shader, without readback or per-tile submissions.

Build-only evidence: the GPU-enabled Apple Clang C++20 shared library and native
MIL test executable built successfully; the managed test project built with zero
warnings and errors. Authored native repeat/source/filter coverage now contains
48 combinations, and quad encoding covers 27 filter/address combinations, with
paired managed encoder and layer-contract cases. These cases were compiled but
**not executed**. The MIL coverage source digest was regenerated. Shader embedding
in a C++ build is not GPU pipeline validation. No runtime, image, VM, sanitizer,
benchmark or CI qualification was performed for this checkpoint.

Final qualification must cover minification around the threshold and large
footprints, mirrored/skewed seams, transparent padding and poisoned pool storage,
premultiplied alpha and once-only opacity, plus native Windows WPF comparison.
No performance or pixel-parity claim is made. Managed retained-capture adoption,
tile strokes/text/opacity masks, the remaining filtering/DPI work and broader
MIL/DirectX/Direct2D/Win2D completion remain open.

### Implementation-first checkpoint: explicit full-image Fant policy

The `PROGPU_IMAGE_SAMPLING=explicit-shader` policy and native engine flag 8 now
also select explicit Fant reconstruction for direct images, retained scene images
and ordinary Fant cached-layer restoration. This supersedes the full-image Fant
policy limitation recorded in the preceding tile checkpoint. Automatic selection
is unchanged (including the existing Parallels D3D12 adapter rule); forcing native
sampling retains hardware Fant where that policy is allowed. Tile pages retain
their independent explicit-only policy and occupied-extent vertex marker.

The shader encoding adds coefficient -256 outside the cubic B/C domain. Both
managed command compilation and native image execution select that coefficient
without changing the requested kernel; native default Fant remains -32. The
existing shared 4-by-4 footprint and square-root-of-two threshold are unchanged,
but each explicit stratum uses four base-level loads with per-tap addressing.
Unwrapped UVs preserve mirror phase. The low-minification branch uses four loads;
the larger-footprint branch is bounded to 64. Masked and color-matrix image paths
use the same sampling function. This reuses original ProGPU filtering/addressing
code from the previous checkpoint, with no CPU readback, extra pass or new shader
fork. Coefficient selection is constant-time metadata work, not a CPU pixel kernel.

Ordinary direct images now encode clamp in the V-address field rather than the
historical value 1 (repeat). Fant cached layers explicitly encode clamp on both
axes; retained image address flags remain authoritative. Ordinary cached layers
still use their existing texture-allocation UVs; this change does not retrofit
tile-page occupied-extent isolation into every cached-layer sampling mode.

Managed encoding regressions cover native versus explicit nearest/linear/Fant,
already-encoded Fant, cubic and mipmapped preservation. Native compile-time
encoding cases cover all current sampling values under default, forced-native and
explicit flags. These are encoding contracts, not pixel evidence. Final runtime
qualification must exercise direct and retained images, cached layers, fractional
source crops, clamp/repeat/mirror edges, masked/color-matrix paths, alpha, rotated
and sheared footprints, threshold/large-footprint minification, and native versus
explicit captures on macOS/Linux/Windows. The existing retained-cache Fant gate
must run under both policy settings on qualified native-sampler adapters, and
explicit mode on Parallels D3D12. No speed or exact WPF parity claim is made.

Build-only evidence: the GPU-enabled C++ shared library and native MIL test
executable compile, and the managed test project builds with zero warnings/errors.
Native compile-time encoding assertions compiled; managed cases were not run.
Validation execution remains deferred by user request; C++/C# compilation is the
only implementation-stage check. Cubic/mipmap/anisotropy support for repeated MIL
capture, nonuniform DPI, managed retained-capture adoption, tile strokes/text/
opacity masks and the broader MIL/DirectX/Direct2D/Win2D goal remain open.

### Implementation-first checkpoint: occupied-extent layer restoration

Explicit image sampling now also governs nearest/linear restoration of ordinary
materialized semantic layers, including retained local-content caches. Fant
restoration in explicit mode uses the same occupied-page encoding. This closes
the allocation-UV limitation recorded in the previous full-image Fant checkpoint
for this restore path: all three filters clamp each tap to the captured content's
width/height, not the larger leased texture. Native-sampler mode retains the
existing sampler-based restoration; repeated tiles retain their independent
explicit-only policy and repeat/mirror metadata.

The new native `try_encode_captured_page_quad` and managed
`TileImageVertices.TryEncodeCapturedPageQuad` reuse the original occupied-page
encoder. They preserve the four already transformed/snapped positions, replace
full-page UVs with exact unit corners, and publish occupied dimensions plus
once-only opacity. These APIs require a full-page quad in canonical corner order,
not a crop or arbitrary UV mapping. Conversion is transactional and allocation-
free, with fixed four-corner metadata work and stack storage; this is not a CPU
image kernel. The canonical shader performs the existing 1/4/64-load bounded
nearest/linear/Fant work. No shader fork, image readback, new capture pass or
resource-ownership transfer is introduced.

Native semantic POP_LAYER selects the encoder for non-tile, nonempty captures
under engine flag 8. The current managed semantic layer submission shares that
native executor; the standalone managed encoder is available to portable retained
capture adoption, which remains unfinished. Ordinary source-image crops retain
their existing typed UV path rather than being misidentified as full captures.

Authored paired encoder cases preserve skewed positions and opacity, check exact
unit UVs for a 3-by-5 capture in a 64-by-128 pool allocation, cover all three
filters, preserve a destination sentinel, and reject invalid positions/extents
transactionally. The GPU-enabled C++ shared library/native MIL test executable
and managed test project compile (managed: zero warnings/errors). The authored
cases were not executed. Runtime execution is deferred. Final gates must poison pool
padding and exercise transformed cache restoration, nearest/linear/Fant,
mask/effect and fixed-blend composition, eviction/reuse, and native versus explicit
policy on macOS/Linux/Windows. No fidelity or performance claim follows from
encoding coverage or compilation alone.

Nonuniform-DPI investigation: MIL build requests and dynamic guidelines carry
independent X/Y scales, but `progpu_native_frame` and semantic frame submission
still use a scalar DPI for coordinate-to-pixel mapping. Removing the repeated
capture equality guard alone would not implement end-to-end anisotropic target
mapping. The guard is retained. A paired frame/host coordinate contract, including
per-axis guideline offsets and cache raster extents, remains required; source
bitmap X/Y DPI metadata is a separate already-supported contract. Broader
MIL/DirectX/Direct2D/Win2D implementation remains open.

### Implementation-first checkpoint: tile-brush line and fixed-shape pens

Native MIL now routes tile-brush pens on nondegenerate DrawLine (including its
animated form), DrawRectangle, DrawEllipse and DrawRoundedRectangle through the
shared native stroke compiler and a GPU-rasterized geometry alpha mask. The
existing tile-source path then paints through that mask. ImageBrush, DrawingBrush
and VisualBrush sources retain their existing single/repeated/flip capture and
nearest/linear/Fant policies. Missing source data, source cycles and unsupported
sampling continue to fail through the same typed source contract.

The implementation reuses original ProGPU `semantic_path_stroke::compile`, MIL
fixed-shape contours, native line pen bounds, `add_geometry_mask`, Vector.wgsl
and tile restoration. Caps, joins, miter limits, dash intervals and animated dash
offsets are passed to the canonical stroke compiler, not recreated as CPU pixels
or a second shader implementation. Geometry primitives carry the complete paint
transform. An outer isolated layer owns the stroke mask, so inner paint/viewport
clip construction cannot replace it; inherited clipping remains inside and brush
opacity is applied once by tile painting. This uses GPU mask rasterization plus
an isolation/composite pass, not a claim of zero additional GPU work.

Rectangle brush placement uses the existing positive fixed-shape pen bounds;
line placement uses the existing line/cap bounds. Sharp rectangles emit four
closed line segments; ellipses and rounded rectangles reuse their canonical arc/
curve contours. CPU work is stroke/dash compilation and retained primitive
emission, with sequential dash/join dependencies, not a scalar image fallback.
No reflection, readback, CPU pixel synthesis or per-visible-tile submission was
added. Managed applicability is already source-integrated: LibreWPF
`WpfNativeMilSceneCompiler.ResolvePen` consumes `IPortablePenSource`, resolves
its brush through `AddPortableBrush`, and writes the same native pen/dash records.
No managed bridge workaround or ABI change is needed for this consumer change.

Build-only evidence: the GPU-enabled Apple Clang C++20 shared library and native
MIL test executable compile. The native fixture authors 160 combinations of four
source kinds, four stroke shapes, five tile modes and solid/dashed pens, with
skewed brush mapping, transformed paint, inherited clipping, Fant and opacity.
Cap selection spans all four cap values; this is not a full independent cap/join
cross-product. Cases assert a retained geometry mask with unit mask opacity.
**No test executable or GPU pipeline was run.** The MIL source-coverage digest was
regenerated; managed product code and the ABI are unchanged in this checkpoint.

Final qualification must compare stroke coverage, caps/joins/miter clipping,
dash phase/seams, degenerate geometry, thick/short strokes, smooth closed seams,
source bounds/crops, source-cycle rejection, alpha/opacity and transformed nested
clips against native Windows WPF and the portable path. General PathGeometry,
GeometryGroup/CombinedGeometry and degenerate tile pens remain open, as do tiled
glyphs/opacity masks, nonuniform target DPI, remaining filters and the broader
MIL/DirectX/Direct2D/Win2D goal. No pixel-parity or performance claim is made.

### Implementation-first checkpoint: multi-contour tile path pens

Native MIL DrawGeometry now paints tile-brush PathGeometry strokes through the
same native geometry-mask route as fixed-shape pens. All parsed stroke contours
append to one canonical stroke primitive arena and one alpha mask; the tile brush
is painted once for the complete path, not once per contour. Closed/open state,
smooth joins, dash intervals/offsets and the parser's start/end dash-cap markers
for unstroked-segment breaks remain authoritative. Fill segments are not used as
a replacement for the stroke contours. Nondegenerate LineGeometry resources also
share the DrawLine tile-pen adapter, including the effective geometry/paint
transform. Path placement retains the existing native path-pen bounds expansion.

The implementation reuses original ProGPU MIL contour extraction and
`semantic_path_stroke::compile` append semantics; it does not add a curve
flattening algorithm, CPU mask bitmap, shader fork or managed reflection adapter.
Each contour restarts the existing dash compiler as normal path replay does.
Primitive/brush arenas share the mask's lifetime, with work/storage proportional
to the existing stroke/dash expansion. Empty flat-capped open contour runs can be
omitted; nonempty degenerate cap geometry remains unsupported by this adapter.
Source-built LibreWPF already emits the typed PathGeometry/LineGeometry, pen and
brush contracts, so the change remains in ProGPU's native consumer.

The geometry-mask contract currently carries no guideline-resource reference.
Tile pens now explicitly reject active guideline/per-point snapping instead of
silently omitting it. This is a documented remaining typed-contract gap, including
fixed-shape tile pens from the previous checkpoint, not a claim of snapping parity.

The brush fixture now uses `progpu_native_mil_channel_build_scene_with_request`
with identical request/serial for sizing and copy. Its old legacy entry point
could not supply the compile context required by repeated pages and VisualBrush
sources. Earlier fixture matrices were compiled but never executed; correcting
the producer does not retroactively qualify them. The independent general clip
fixture retains its legacy API coverage.

Authored coverage adds 64 path combinations (curve/arc/multiple figures/broken
open contours, four sources, single/FlipXY, solid/dashed) with distinct start/end/
dash caps and exactly one stroke-mask assertion. The fixed-shape/source/tile/dash
matrix gains 40 LineGeometry cases, bringing it to 200. A guideline rejection
case preserves its destination sentinel. The GPU-enabled C++ shared library and
native MIL test executable compile successfully; none of these cases have run.
The MIL source-coverage digest was regenerated. Runtime execution remains deferred;
final gates must compare overlapping contours, cap substitutions at stroke
breaks, smooth joins, dash restart/seams, transformations, opacity and source
mapping against native Windows WPF and portable replay.

GeometryGroup/CombinedGeometry tile pens, degenerate caps, guideline-aware masks,
tiled glyphs/opacity masks, per-axis target DPI and the broader MIL/DirectX/
Direct2D/Win2D goal remain unfinished. Build/encoding evidence is not runtime,
pixel-parity or performance evidence.

### Implementation-first checkpoint: nested GeometryGroup tile pens

Native MIL GeometryGroup pens now use the tile-brush stroke-mask path. The
existing depth-bounded group traversal resolves nested group/child transforms,
then appends each path, nondegenerate line, positive rectangle/rounded rectangle
or ellipse stroke into one group-owned primitive arena. Only after all children
succeed does the group create one GPU geometry mask and paint the tile brush
once. This preserves group-wide brush mapping and avoids applying brush opacity
again at every child overlap. CombinedGeometry children and degenerate children
retain their explicit unsupported behavior.

Stroke compilation and mask painting are now separate internal typed operations.
The same contour compiler, fixed-shape contour construction and line adapter are
reused by direct draws, PathGeometry and grouped strokes. Source path contours are
borrowed synchronously, not copied into synthetic managed shapes. Native collection
uses amortized arena growth; GPU ownership stays with the existing mask/layer/tile
resources. CPU work remains the existing dependency-ordered stroke/dash expansion
and group traversal, not a scalar pixel fallback. No shader fork, readback or
per-visible-tile submission was introduced.

For tile groups, conservative pen expansion is included before each child's
scale/shear while computing group stroke bounds. Expanding only the transformed
group by one unscaled pen width could crop a scaled child's mask. The grouped tile
brush uses this shared expanded group bound for placement and mask storage; final
WPF image comparisons must qualify that conservative placement policy, especially
large child scales, miter limits, gaps and nonuniform transforms. Existing non-tile
group brush handling is unchanged. Active guideline masks remain fail-closed.

Authored fixture coverage adds 40 basic rectangle/ellipse group combinations and
40 nested mixed rectangle/ellipse/line/path combinations, across four source kinds,
five tile modes and solid/dashed pens. Nested cases use distinct group/child/paint
transforms, clipping, Fant and half-opacity, and assert exactly one unit-opacity
stroke mask for the whole group. The GPU-enabled C++ shared library and native
MIL test executable compile successfully, and the MIL source digest was
regenerated. These cases have not been executed. Final gates
must cover nested/shared children, overlap alpha, dash restarts, source reuse and
cycles, large child scales, clip nesting, mask eviction/reuse and native Windows
WPF versus portable rendering on the requested platform matrix.

Combined-geometry boundary stroking, degenerate caps, guideline-aware masks,
tiled text/opacity masks, nonuniform frame DPI and broader MIL/DirectX/Direct2D/
Win2D completion remain open. Compilation and encoding assertions are not parity
or performance qualification.

### Implementation-first checkpoint: shared CombinedGeometry stroke boundaries

Native MIL now lowers a CombinedGeometry pen from the actual boolean result's
closed boundary, rather than rejecting all pens or stroking the original operand
edges. Union, intersection, XOR and difference route through ProGPU's portable
Direct2D `CombineWithGeometry`/`Outline` implementation. The resulting contours
feed the existing native stroke compiler for solid/gradient pens or the one-mask/
one-paint tile-pen path. Fill remains before stroke. Empty results produce no
stroke; active guidelines remain explicitly unsupported by this adapter.

Two internal typed Direct2D helpers bridge canonical native fill segments to the
portable geometry sink and extract outline contours transactionally. Lines,
quadratics and cubics retain their original representation on input; native arcs
convert to endpoint arcs, splitting full ellipses into two arcs rather than
introducing a separate flattening algorithm. The MIL resolver recursively builds
native geometry groups and combined operands with their transforms, using one
local portable factory and RAII references. There is no operating-system COM
activation, Windows dependency, reflection or external application COM contract.
MIL now privately links the existing portable Direct2D core, already included in
native installation/export targets. No ABI or managed producer change is required.

The chosen outline tolerance targets one quarter physical pixel, conservatively
scaled by the effective transform Frobenius norm and the larger requested DPI
axis. Nested group/combined transforms tighten the tolerance before intermediate
boolean flattening, preventing later magnification from reusing a coarser local
boundary. Unrepresentable tolerances and core capacity/unsupported results fail
closed. This is an implementation quality target, not proof of a global error
bound or native WPF pixel parity. Final gates must qualify topology, cusps, tiny
features, joins on flattened curves and large nested transforms.

Provenance is original ProGPU portable Direct2D path/arrangement code, its existing
intrinsic classification/bounds work and MIL's native path/stroke compiler.
Bounds are obtained from that same geometry core rather than adding another
per-point scalar bounds pass. CPU topology/flattening and retained record emission
remain dependency-ordered; GPU stroke/tile rendering adds no CPU image readback
or pixel synthesis. This is not a new GPU boolean-boundary algorithm, and no
performance claim is made. Native request/scene caching and this additional
geometry compilation cost must be measured in final qualification.

Authored cases cover the four operations on overlapping rectangles, including
signed boundary area and exclusion of hidden operand edges, plus 64 MIL
operation/source/solid-or-tile/dash combinations with simultaneous tile fill and
pen, transformed geometry/paint, clipping, half opacity and Fant. No case has
been executed. Direct2D core, GPU-enabled native renderer and MIL test executable
compilation are the implementation-stage evidence; the MIL source digest is
regenerated. Runtime, shader/image, Windows VM/macOS/Linux parity, sanitizers,
performance and CI qualification remain deferred.

CombinedGeometry as a stroked GeometryGroup child still needs integration in the
group collector. Degenerate cap geometry, guideline-aware stroke masks, tiled
glyphs/opacity masks, per-axis target-frame DPI, remaining filters and broader
MIL/DirectX/Direct2D/Win2D requirements remain open.

### Implementation-first checkpoint: combined children in stroked groups

Native MIL `GeometryGroup` stroking now accepts `CombinedGeometry` children,
including repeated references below different nested transforms. The bounds pass
resolves the actual boolean boundary through the existing portable Direct2D core
and retains each occurrence's outline for the subsequent stroke pass. Both passes
walk depth-first; checked handle/cursor pairing preserves empty boolean results
without rebuilding topology. Child transforms are applied once, and flattening
tolerance uses the complete child-to-target transform and request DPI. Tile pens
still collect all group stroke primitives into one geometry mask and paint once;
solid/gradient pens reuse the ordinary path stroker.

Provenance is the original ProGPU `resolve_combined_stroke_outline` adapter and
portable Direct2D path implementation introduced in the preceding checkpoint;
no foreign source, OS COM activation, new shader algorithm, readback, or managed
bridge workaround was introduced. Bookkeeping is O(C) for C combined occurrences,
with O(P) retained outline-point storage in addition to the existing bounded core
solver. Topology traversal remains dependency-ordered CPU work; existing core
intrinsic kernels are unchanged. This is native MIL resource-consumer completion,
not a change to shared managed/native rasterization or the public scene ABI. The
managed portable producer and renderer remain unchanged; differential qualification
against their existing geometry replay is still required at the final gate.

The Release WebGPU-enabled native library and MIL regression executable compile.
128 additional fixture cases cover four boolean modes, four tile sources, solid
and tiled pens, dashed and continuous strokes, and identical/nonidentical operands
under repeated nested transforms. Tests, GPU execution, image parity, contract
verification, benchmarks, VM validation, and CI qualification were **not run** in
this implementation-first checkpoint. The coverage ledger was regenerated; the
latest fetched ProGPU main has no commits missing from this feature branch.

This supersedes the preceding combined-child stroke limitation. Degenerate cap
geometry, guideline-aware tile masks, tiled glyph/opacity masks, nonuniform target
DPI, remaining tile filters, managed retained-capture adoption, and the broader
DirectX/Direct2D/Win2D goal remain open. Compilation does not establish parity.

### Implementation-first checkpoint: degenerate tile-pen caps

Zero-length `DrawLine`/`LineGeometry` and collapsed path contours now paint tile
pens through the same analytic cap primitives and dash-phase selection as ordinary
native MIL strokes. The existing `append_degenerate_cap_stroke` has an optional
primitive collector; ordinary draws retain their original builder path, while tile
masks supply target-space transforms and collect at most two caps. No separate cap
shape approximation or shader was introduced. Asymmetric flat/square/round/triangle
ends are preserved; both flat ends emit nothing, a dash gap emits nothing, and a
collapsed closed contour follows the existing ordinary-path round-cap-pair rule.
The group collector can consume these primitives without additional brush paints.

Provenance: original ProGPU `append_degenerate_cap_stroke`,
`try_degenerate_cap_stroke_bounds`, and semantic path-stroke tangent detection.
Cap generation has O(1) time/storage (at most two primitives); dash phase retains
the existing dependency-ordered O(D) interval traversal. There is no independent
pixel loop, CPU readback, or new SIMD kernel. This completes another native MIL
consumer route; shared managed/native shaders and scene contracts are unchanged.
Managed/native/WPF differential qualification remains required at the final gate.

The WebGPU-enabled Release native library and MIL test executable compile.
1,024 authored cases cover four brush sources, all start/end cap pairs, continuous
and dashed pens, on/gap phases, zero-length line commands/resources, and collapsed
open/closed paths. These cases are **not executed**; image parity, verifiers,
benchmarks, VM integration, and CI qualification remain deferred. The coverage
ledger was regenerated. This is not a cap-pixel-parity or speed claim.

Degenerate fixed rectangle/ellipse tile pens and guideline-aware geometry masks
remain open, together with the other MIL/DirectX/Direct2D/Win2D goal work.

### Implementation-first checkpoint: collapsed fixed-shape tile pens

Direct fixed-shape MIL draws now accept tiled pens when rectangle/rounded-rectangle
width or height, or an ellipse radius, collapses to zero. Ordinary collapsed
rectangle outline construction was extracted without changing its line, rounded
corner, bevel, or miter rules; undashed tiled rectangles paint through that exact
filled outer path. Dashed rectangles retain the original closed perimeter or WPF
rounded-rectangle path. Collapsed ellipses share the ordinary four-point traversal
for dashes, round-ended line coverage for a nonzero remaining axis, and the shared
round cap pair when both radii vanish. Brush opacity, placement, transform, clip,
and Fant sampling remain on the existing tile source path.

The original ProGPU `append_degenerate_rectangle_stroke` outline block and
`append_degenerate_ellipse_stroke` point ordering are the implementation provenance.
The extracted helpers have O(1) setup/storage; dashed emission adds O(D) dash pieces.
There is no new CPU pixel algorithm, SIMD fallback, shader fork, reflection, or
readback. This is native MIL consumer integration; managed replay and the shared
managed/native shader/scene ABI remain unchanged. Matched output qualification
against existing managed replay and Windows WPF is still a final-gate requirement.

Release WebGPU-enabled native-library and MIL-test executable builds pass.
432 new fixture cases cover four sources, three fixed shapes, three collapse axes,
continuous/dashed pens, three joins, and on/gap dash phases. Cases are authored and
compiled, **not executed**. Coverage metadata was regenerated; runtime/image/VM
tests, verifiers, benchmarks, and CI qualification remain deferred. No pixel parity
or performance claim follows from the builds.

Collapsed fixed children in `GeometryGroup` still need integration with the single
group coverage collector. Guideline-aware tile masks and the remaining broad
MIL/DirectX/Direct2D/Win2D scope remain open.

### Implementation-first checkpoint: collapsed fixed children in tile-pen groups

The native MIL group bounds/stroke walkers now accept collapsed rectangle and
ellipse children for tiled pens, including nested transforms. Each child's stroke
extent is expanded before its transform; all child coverage is collected before
the one group brush paint. Dashed/ellipse coverage reuses the geometry primitive
collector. Undashed collapsed rectangles retain their exact filled outer contours
in a path/segment arena instead of approximating them as ordinary line strokes.

Primitive-only groups keep the existing geometry-mask resource. Mixed filled and
stroked groups use the existing retained picture-mask resource with a white nested
scene containing at most one geometry batch and one path batch. This preserves
GPU alpha composition and one tile paint without CPU pixel readback or per-child
submission. The picture executor currently creates a same-device child engine and
target-space intermediate; its residency, startup, submission, and frame-time costs
must be measured and optimized/qualified in the final performance phase. This is
not evidence that the mixed-mask route is the fastest qualified default.

Provenance: original ProGPU fixed-shape adapters from the preceding checkpoint,
`semantic_scene_builder::draw_geometry`/`draw_paths`/`add_picture_mask`, and the
existing picture-mask GPU executor. No public ABI, canonical shader, or managed
renderer semantics changed. Managed replay remains the paired differential
reference for this native MIL resource-consumer work. Bookkeeping and nested-scene
serialization are O(P + S) time/storage in collected primitives/paths P and segments
S; topology/dash traversal retains its dependency-ordered algorithms. No independent
CPU pixel loop or new SIMD kernel was introduced.

The Release WebGPU-enabled native library and MIL-test executable compile. Added
720 fixture cases across sources, collapse axes, joins, dash phases, and all tile
modes; they assert one picture mask for mixed coverage or one geometry mask for
primitive-only coverage. They are **not executed**. The coverage ledger was
regenerated; runtime/image/VM tests, verifiers, benchmarks, and CI qualification
remain deferred. Non-tiled collapsed fixed children remain explicitly unsupported
in the group walker; completing those brush-mapping semantics, guideline-aware
masks, and the broader MIL/DirectX/Direct2D/Win2D scope remains required.

### Implementation-first checkpoint: non-tiled collapsed group strokes

Collapsed fixed rectangle and ellipse children now also work with ordinary
non-tiled group pens. The group walker passes its already-resolved brush index to
the original direct-shape degenerate stroke routines. Those routines accept an
optional supplied material and otherwise retain their direct-draw brush resolution.
This includes the collapsed-point cap branch and the rounded dashed-path branch;
relative gradients must not acquire a new mapping for each child.

Provenance is the original ProGPU direct fixed-shape stroke implementation and the
existing group material-resolution contract. The new adapter adds O(1) work per
child with no additional brush resolution, GPU mask, pixel loop, or shader change.
Existing contour/dash work and SIMD kernels are unchanged. This is native MIL
consumer completion, not a shared renderer ABI/algorithm change; managed portable
replay remains the differential reference to qualify at the final gate.

The Release WebGPU-enabled native library and MIL regression executable compile.
144 authored cases cover solid and relative linear-gradient pens, nested and flat
groups, three collapse axes, three joins, and dash on/gap phases. They check one
shared material table and index across the emitted group strokes. They are **not
executed**. Coverage metadata was regenerated; runtime/image/VM tests, verifiers,
benchmarks, and CI qualification remain deferred. This supersedes the preceding
non-tiled collapsed-group rejection; guideline-aware tile masks, mixed-picture-mask
performance qualification, and the broader MIL/DirectX/Direct2D/Win2D scope remain
incomplete.

### Implementation-first checkpoint: typed nested-scene guideline ownership

Added `semantic_scene_builder::copy_guideline_set_from` to the shared include/module
C++ builder surface. A nested mask scene can now copy a builder-validated guideline
resource directly, preserving static/composite/per-point flags, coordinates, and
resolved dynamic offsets. It receives a destination-owned resource index, identity,
generation, and byte storage; no parent index leaks into the child scene and no
whole-scene serialization/parsing or dynamic guideline re-evaluation is required.
The copy is self-alias-safe and rejects non-guideline/out-of-range resources before
mutation. Source reset does not invalidate the copied payload.

Algorithm provenance is the existing ProGPU builder resource ownership and
guideline layout. Copy cost is O(G) time/storage in guideline coordinates and
optional offsets using owned bulk byte copies, not a new scalar numerical loop.
This C++ builder-only ownership operation does not change the C scene ABI, managed
guideline behavior, or canonical shaders. Managed snapshot ownership remains the
paired contract; runtime parity is not established by this API addition.

Authored include-based cases compare copied bytes/flags, destination metadata,
self-copy, invalid-resource non-mutation, and source-reset independence for three
guideline forms. The import-based consumer exercises explicit-offset copying.
Native library, MIL/native/internal test executables, and the LLVM C++20 module
consumer compile; tests were **not executed**. Runtime/image/VM tests, verifiers,
benchmarks, and CI qualification remain deferred.

This is a prerequisite, **not completed guideline-aware tile masking**. Executor
preflight currently permits per-point deformation only for `DRAW_PATH`, rejecting
stroke-primitive draws. Subsequent mask integration must preserve single/composite
translation versus per-point path deformation, avoid double-snapping at layer
restore, and retain shifted mask storage bounds. Existing fail-closed tile-mask
guards stay in place until those execution semantics are implemented.

### Implementation-first checkpoint: uniform tile-pen guideline translation

Single-coordinate-per-axis snapping now reaches native MIL tile-pen coverage,
mask storage bounds, and brush placement. The C++ builder exposes an allocation-free
`try_uniform_guideline_translation` query over its typed resource. It and the
semantic executor share the original native WPF rounding implementation and
explicit dynamic physical-offset handling. Unsupported multi-coordinate or
per-point resources fail without mutating the result; they are not approximated
as a translation. The build request must supply a uniform target DPI.

The MIL adapter bakes the one target-space displacement into stroke/cap/path
transforms and mask bounds. An isolated paint state clears the original guideline
resource before its layer is pushed/restored, preventing a second layer-restore
snap. Existing group collectors retain one paint, and un-guided draws do not gain
the additional save/restore scope. Path tile-clip construction uses the same
translation. Combined-geometry strokes no longer reject uniform guidelines solely
because a guideline resource is present. Per-point deformation remains explicitly
unsupported for tile masks.

Provenance: original `wpf_guideline_offset` and `semantic_state_cursor` offset
rules, now shared in the internal semantic-state header; the builder query and MIL
adapter are O(1), allocation-free coordinate work. No numerical buffer loop, new
SIMD fallback, CPU readback, or shader fork was introduced. Public scene C records
are unchanged; the include/module C++ query surface and point export are updated.
Managed replay retains its existing guideline algorithm and remains the paired
differential reference; this native consumer integration is not a managed behavior
change or a parity claim.

The Release native library, MIL/internal regression executables, and LLVM C++20
module consumer compile. Added 400 MIL fixture combinations plus a paired static
translation case, builder checks for negative half-integers/explicit offsets and
rejection non-mutation, and an import-based query case. The old blanket-rejection
fixture now targets true multi-guideline deformation. All are authored/compiled,
**not executed**. Coverage metadata was regenerated. Runtime/image/VM tests,
verifiers, benchmarks, and CI qualification remain deferred.

Multi-guideline/per-point tile-mask deformation, remaining tile consumers and
nonuniform frame DPI, mixed-picture-mask performance qualification, and the full
MIL/DirectX/Direct2D/Win2D objective remain open. Uniform snapping still requires
pixel-level qualification, including nested clipping and layer restore.

### Implementation-first checkpoint: tiled glyph-run foregrounds

Native MIL `DrawGlyphRun` and `GlyphRunDrawing` now accept ImageBrush, DrawingBrush,
DrawingImage-backed ImageBrush, and VisualBrush foregrounds through the existing
tile material path. One nested semantic scene records the complete run as white
alpha coverage, using the ordinary native font-sideband decoding, unique-glyph
outline map, placement, advances/offsets, and style-simulation implementation. The
source brush is painted once through that picture mask; no per-glyph scene, path
draw, CPU pixel copy, or submission is introduced. Ordinary solid text bypasses
this mask path and retains its existing glyph resources.

Coverage is alpha-only: aliased text remains aliased and other text uses grayscale
instead of RGB ClearType coverage. Run bounds must be supplied by the typed MIL
resource; absent/empty placement bounds do not trigger a synthetic em-size brush
rectangle. Uniform guidelines use the preceding shared translation and isolated
restore handling. Clip and brush opacity are applied outside the white coverage
scene. Font data remains a pointer-free SFNT sideband owned by the channel; a missing
font fails before publishing the caller's scene.

Provenance is original ProGPU `append_glyph_run`, `semantic_scene_builder` picture
masks, and `append_single_tile_brush`. Preparation is O(G + S) time/storage for
positioned glyphs G and decoded outline segments S, with the ordinary four-phase
outline generation retained. The nested run owns its own builder-resource map;
cross-builder resource indices are not reused. Repeated mask runs currently rebuild
their coverage resources when compiling a changed scene, and the existing picture
executor creates a child engine/target-space intermediate. Those reuse/residency
and submission costs require final performance qualification; this is not a
fastest-path claim. Existing intrinsic font kernels and canonical shaders are
unchanged. Managed replay remains the paired image/text reference for this native
MIL consumer addition.

The WebGPU-enabled Release native library and MIL-test executable compile. Added
320 cases covering four tile sources, five tile modes, both glyph command forms,
uniform guidelines, and four style-simulation combinations, plus missing-font
non-mutation coverage. They assert one picture mask with one retained glyph draw
per run. Tests are authored/compiled, **not executed**; runtime/image/VM testing,
verifiers, benchmarks, and CI qualification remain deferred. Coverage metadata was
regenerated. Gradient glyph foregrounds, per-point guidelines, remaining opacity
mask/consumer work, and the broader MIL/DirectX/Direct2D/Win2D scope remain open.

### Implementation-first checkpoint: gradient glyph-run foregrounds

Both native MIL glyph command forms now accept linear and radial gradients through
the same whole-run white coverage scene used for tile foregrounds. The original
`resolve_gradient_scene_brush` / `resolve_brush_index` implementation owns relative
or absolute mapping, brush transforms, animated parameters, normalized stops,
interpolation, spread, degeneracy, and opacity. One world-space analytic rectangle
paints the material through the run mask. Its draw state is identity because the
gradient coordinate mapper already includes the inverse run transform; inherited
clip/opacity are retained, and scope state is restored afterward. Rectangle edge
antialiasing is disabled because glyph coverage owns the boundary. Solid text
continues to bypass this offscreen route.

Research/provenance: this extends original ProGPU `append_glyph_run`, gradient
mapping, analytic drawing, and picture masks, without foreign implementation code.
The existing [cross-engine research](progpu-avalonia-rendering-research.md) provides
the shaped-run, retained resource, and GPU offscreen design: preserve CPU shaping
and one retained run rather than repeat layout or submit each glyph. The public
[Direct2D glyph drawing contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawglyphrun)
also separates the positioned glyph run from its foreground brush. No new shaping,
font discovery, atlas eviction, worker scheduling, or device-loss policy is added.
Existing canonical gradient/glyph shaders and intrinsic font kernels are unchanged.

Native-only applicability: this closes a MIL consumer rejection; managed brush and
text rendering already supplies the paired semantic reference and is not given a
new algorithm or wire contract. Coverage preparation remains O(G + S) time/storage
for glyphs G and decoded outline segments S. Gradient normalization retains its
existing stop-dependent ordering and allocation costs; one material draw adds
O(1) command work/storage. There is no CPU pixel fallback or readback, and no
per-glyph gradient mapping. Picture-mask child-engine, intermediate extent, and
changed-scene cache reuse costs remain unqualified, so this is not a performance
claim or a final default-path qualification.

Added 384 unexecuted scene cases across both gradients, two mapping modes, three
spread modes, two interpolation modes, both glyph command forms, uniform
guidelines, and four style combinations. They include inherited clipping and run
transforms, plus absolute/relative brush transforms for linear gradients, and
assert one material table, picture mask, and outer paint. Another 24 failure cases
cover missing-font non-mutation. Compile-only builds and regenerated command
coverage accompany this change; runtime/image/VM tests, verifiers, benchmarks, and
CI qualification remain deferred at the user's request. This supersedes the
preceding gradient-foreground rejection, not the open per-point guideline,
nonuniform-DPI, remaining MIL consumer, DirectX, Direct2D, or Win2D work.

### Implementation-first checkpoint: physical-DPI glyph preparation

Native MIL glyph preparation now receives the stateful build request's target DPI
for solid, gradient, and tile foregrounds, including `GlyphRunDrawing` and nested
run-coverage scenes. Outline raster size is `clamp(em * dpi * transformScale, 4, 128)`.
The positioned glyph's atlas-to-logical scale remains `em / rasterSize`: raising
coverage resolution does not enlarge logical geometry. The existing resource key
already includes raster size, so distinct physical sizes do not reuse the wrong
outline resource. The frame-request cache already includes both DPI fields.

Baseline snapping now transforms to logical target coordinates, multiplies by
target DPI, selects the existing quarter-pixel X phase / whole-pixel baseline
policy, divides back to logical coordinates, and applies the inverse transform.
Animated and rotated/reflected placement retain their existing no-snap behavior.
Legacy builds without a frame request retain DPI 1. Native frames still expose
one DPI scalar; unequal requested X/Y DPI now fails closed for glyph draws rather
than rendering text at an invented average. Per-axis frame support remains work
to implement, not a claimed supported configuration.

Provenance/applicability: this brings the native MIL consumer into alignment with
original managed `ProGPU.Scene/Compositor.cs` `ResolveTextRasterization`,
`ResolveGlyphRasterSize`, and `ResolveTextPlacement`. Managed rendering already
uses physical target scale and needs no duplicate implementation change. The
existing [cross-engine research](progpu-avalonia-rendering-research.md) separates
reusable shaping from raster resolution; the public
[Direct2D high-DPI contract](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-high-dpi)
keeps DIPs distinct from physical pixels. No foreign implementation was used.
Font decoding, canonical raster shaders, intrinsics, resource ownership, and
placement cache topology are unchanged. Preparation remains O(G + S), glyphs G
and outline segments S, with O(1) extra scalar work per position and no new
per-frame arrays or submissions. The existing atlas clamp and nonuniform-transform
phase behavior are not newly qualified by this change.

Added 42 unexecuted scene cases for seven font/DPI combinations, three foreground
families, and both command forms; four font/DPI pairs match managed
`TextRenderingModeRenderTests.UiTextRasterizationPreservesItsPhysicalFontSize`.
Assertions cover physical raster size, inverse geometry scale, physical snapping,
phase selection, unchanged logical bounds, and one run draw. Another 21 cases
cover anisotropic-target rejection without output mutation. Native-library and
MIL-test compilation are the only qualification in this phase. Runtime images,
VM comparison, verifier, benchmark, and CI execution/qualification remain deferred.

### Implementation-first checkpoint: tile opacity masks in render data and drawings

`PushOpacityMask` and `DrawingGroup.OpacityMask` now accept ImageBrush,
DrawingImage-backed ImageBrush, DrawingBrush, and VisualBrush. The new spatial-mask
adapter compiles one bounded rectangle through the existing native MIL tile-source
path into an owned child scene; a picture mask consumes that scene's alpha.
Gradient masks retain their existing direct brush-mask representation, and solid
masks retain their uniform-alpha path. This adds no parallel brush mapper, shader,
bitmap converter, or per-visible-tile submission loop.

Mask preparation starts with opacity one, no inherited clip/mask, no parent
guideline resource, and alpha-compatible text state. Brush opacity is evaluated
inside the source scene, while group opacity and content clipping remain outside.
As with gradient opacity masks, content guidelines do not deform the mask's
material coordinate field. Exact group bounds are required through the existing
typed sideband. The child owns its brush/image/glyph maps and clip scratch; source
cycle detection and bounded recursive depth remain shared with native replay.
Missing group bounds and cyclic source graphs fail before caller output is
published. Empty/nonpositive spatial bounds remain unsupported.

Provenance: original ProGPU `append_render_stream`, `append_single_tile_brush`,
and semantic picture-mask ownership/execution. One fixed stack packet uses the
generated MIL rectangle layout, following the existing internal DrawDrawing
framing pattern. The [WPF opacity-mask contract](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/opacity-masks-overview)
defines mask alpha independently of brush RGB and combines it with content
opacity. The existing [cross-engine research](progpu-avalonia-rendering-research.md)
informs retained source reuse and GPU offscreen composition rather than CPU
readback. No foreign implementation code was used. Managed replay remains the
semantic reference; this is a native MIL consumer extension, with canonical
shaders, shared SIMD kernels, C ABI, and managed algorithms unchanged.

Scene preparation adds O(C + R) source command/resource storage and traversal on
top of the existing brush-source algorithms, plus O(1) stack framing. Pixel work
stays in the current same-device picture/tile executor. Its child engine,
intermediate allocation, nested submission, and changed-scene reuse costs remain
unqualified; this is not a throughput or fastest-path claim.

Added 480 unexecuted scene cases across four sources, five tile modes, both mask
boundaries, nearest/linear/Fant sampling, transforms, active static guidelines,
and DPI 1/2. They assert one owned picture mask and isolated initial state. Four
missing-bounds and six source-cycle cases cover non-mutation failures. Native
library and MIL-test compilation are the only qualification in this phase.
Visual-level masks, effect/cache visual masks, nonuniform target DPI, remaining
filters/consumers, and the full DirectX/Direct2D/Win2D scope remain open. Runtime
images, Windows comparisons, verifiers, benchmarks, and CI qualification remain
deferred until the final validation phase.

### Implementation-first checkpoint: visual, effect, and cached tile opacity masks

The spatial opacity-mask compiler is now a reusable native MIL member shared by
render-data scopes, DrawingGroup, ordinary Visual, effect-bearing Visual, and
BitmapCache composition. A typed replay context carries the frame request,
existing active-resource set, recursion depth, and metrics. Child mask scenes
continue to own their resource maps and clip scratch. Visual source cycles cannot
escape detection by entering a separately constructed mask scene.

Visual masks require exact source-built descendant bounds. Ordinary visual
composition retains its bounded outer opacity/mask layer. Effect-bearing visuals
retain the existing effect/source-composite ordering; cached visuals attach the
mask to cache composition, outside reusable content pixels and inside an outer
effect when present. Brush/visual opacity is not folded into the child mask's
initial state a second time. No bitmap-cache content-invalidation or ownership
rule is replaced by this change.

Tile masks with cache-output vector clips use the existing GPU composite-mask
resource: one picture component plus exact world-space clip paths. Clips are not
replaced with their bounding rectangles. Uniform cache-root guideline translation
is baked into the picture's material transform so it follows the snapped cache
quad, while world clips stay fixed. Gradient masks retain their existing direct
deformation path. Multi-point cache-root picture deformation fails closed pending
executor support; per-axis target DPI and cache pixel-snapping edge cases still
need implementation/qualification. Those gaps are not declared parity-complete.

Provenance/applicability: original ProGPU mask/tile compilation, visual composition,
semantic picture masks, composite masks, and uniform-guideline query. The existing
[cross-engine design record](progpu-avalonia-rendering-research.md) and
[WPF opacity-mask contract](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/opacity-masks-overview)
inform retained source reuse and alpha-only composition. No foreign code, public
ABI, shader, CPU pixel fallback, or managed algorithm is introduced. Managed visual
mask replay remains the paired semantic reference for this native MIL consumer
integration. Preparation retains the shared source algorithm costs plus O(C + R)
scene ownership for commands C/resources R and O(K) clip serialization for K clip
segments; the uniform shift is allocation-free O(1). Picture child-engine,
intermediate, submission, and changed-scene reuse costs remain unmeasured.

Added 960 unexecuted cases across four sources, ordinary/cached/effect/cached-effect
boundaries, five tile modes, three sampling modes, brush/visual transforms,
uniform visual guidelines, and DPI 1/2. Assertions cover picture/composite mask
ownership, retained exact clips, and cache-layer presence. Another 36 cases cover
missing bounds, source cycles, and unsupported multi-point cache deformation with
unchanged output. Native library and MIL-test compilation remain the only current
qualification. Final image/Windows/VM, verifier, benchmark, and CI gates are still
deferred. This closes the preceding visual-consumer rejection, not the complete
MIL/DirectX/Direct2D/Win2D or rendering/performance goal.

### Implementation-first checkpoint: cached picture-mask guideline deformation

The native picture-mask executor now receives the same composite state/cursor as
the cache quad, both for direct picture masks and picture components multiplied
with exact vector clips. `semantic_state_cursor::try_composite_rectangle_inverse`
snaps opposite rectangle corners with the existing nearest-guideline algorithm
and returns the separable inverse deformation. Sampled-mask UV coordinates compose
that inverse in physical target coordinates, preserving the existing alpha-only
sampling path. A collapsed snapped rectangle has zero mask opacity; invalid or
unrepresentable mappings fail closed. World-space vector clips remain undeformed.

MIL no longer rejects multi-coordinate cached tile-mask guidelines or pre-bakes a
uniform offset: both uniform and multi-coordinate cases go through this single
executor path. Uncached masks and picture masks without composite guidelines keep
their prior sampling path. Per-point draw guidelines remain independent and are
not reapplied at cache composition. Source-extent pictures support axis-aligned
transforms on this path; transformed non-axis-aligned source-extent pictures with
composite guidelines remain unsupported. MIL visual guidelines are disabled under
rotation/shear by the existing source-state policy.

Provenance is original ProGPU cache-quad corner snapping, semantic-state nearest
guideline lookup, picture-mask sampling, and canonical shader affine-mask support.
No new shader, wire record, public C ABI, pixel readback, CPU image kernel, or
per-tile submission is introduced. The [existing rendering design research](progpu-avalonia-rendering-research.md)
continues to govern device-owned retained composition. This is native semantic
picture-mask transport work; managed composition remains the paired image
reference and receives no duplicated algorithm. Runtime comparisons must confirm
material/edge behavior, especially fractional cache scales and zero-area edges.

The inverse needs O(log X + log Y) time for X/Y guideline coordinates and O(1)
allocation-free storage. Uniform composition adds fixed arithmetic to the existing
sampling uniforms, with no extra draw or texture beyond the existing picture mask.
Existing child-engine/intermediate and residency costs remain unqualified.

Authored native internal regressions cover independent endpoint shifts, inverse
corner round-trips at four DPIs, explicit offsets, collapsed coverage, and invalid
input output preservation. The eight MIL cached multi-guideline rejection cases
are now positive scene cases; their opposing guidelines use different offsets.
Native library, MIL-test, and internal-test executables compile. Tests are not
executed. Final image/Windows/VM comparisons, verifier, benchmark, and CI gates
remain deferred. This supersedes the prior cached-picture multi-coordinate
rejection, not the broader per-point path/stroke or per-axis target-DPI backlog.

### Implementation-first checkpoint: physical BitmapCache origin snapping

`BitmapCache.SnapsToDevicePixels` now floors the retained bitmap origin in physical
target pixels, converts the fractional displacement back to logical coordinates,
and applies the same displacement to cache composition and its opacity-mask
transform. It uses the composed raster-origin translation, not the minimum of a
transformed bounding box, which differs under rotation/reflection. Raster geometry,
cache content revision, and transform basis are unchanged. Negative positions
retain floor semantics. Legacy builds use DPI 1; unequal target X/Y DPI fails
closed in this scalar-frame path, and non-finite physical coordinates are rejected.

Provenance/applicability: original managed `ProGPU.Scene/Compositor.cs`
`ApplyAndDrawLayer` already multiplies the transformed local origin by target DPI,
floors physical coordinates, and translates cache composition back in logical
units. This change brings native MIL cache placement into that contract, without
changing the managed implementation. The [existing cross-engine research](progpu-avalonia-rendering-research.md)
continues to govern retained cache ownership and DPI separation. No foreign
implementation, shader, ABI, image kernel, readback, or submission is added.
The calculation is fixed-work O(1), allocation-free scalar metadata arithmetic.

Added 320 unexecuted paired cases (640 scene builds when run) across four mask
sources, five DPIs, positive/negative/integer offsets, identity/affine transforms,
and cached/cached-effect visuals. Assertions compare snapped composition and mask
displacement and preserve content revisions, raster bounds, and transform basis.
Native-library and MIL-test compilation are the only current qualification.
Windows image parity, fractional-scale behavior, broader per-axis DPI, and
performance remain unqualified; final runtime/VM, verifier, benchmark and CI
gates remain deferred with the full goal still open.

### Resource-preserving portable pen state

`ProGPU.Wpf.Interop` now exposes `IPortablePenStateSource` and the neutral value
snapshot `PortablePenState`. Unlike the existing `PortablePen` compatibility DTO,
it retains a brush resource implementing the typed brush/tile-brush contracts.
It carries thickness, cap/join/miter settings, dash offset, and publisher-owned
read-only dash memory. Null brush state remains representable; unavailable state
must fail closed rather than falling back to an older reduced descriptor.

This original ProGPU contract separates paint-resource identity from stroke
geometry, consistent with the public [WPF Pen.Brush contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.pen.brush?view=windowsdesktop-10.0).
The existing in-repository `PortablePen`/PresentationCore pen metadata defines
the stroke fields; no foreign renderer implementation is ported. Export snapshots
cost O(D) time/storage for D dash entries and retain brush identity in O(1).
No pixel processing, readback, shader change, or C ABI change is introduced.

Both ProGPU's PresentationCore shim and source-built LibreWPF publish the new
contract. The native MIL producer consumes it before the compatibility contract,
reuses the common brush graph serializer and native pen encoder, and therefore
allows image/drawing/visual tile pen resources to reach the existing C++ stroke
coverage and material implementation. Shared fill/stroke brushes reuse one handle.
The managed compositor's existing pen conversion remains unchanged; migration of
its tile-pen replay to retained brush resources is still an open follow-up, not
claimed complete by this transport checkpoint. Existing scalar/gradient consumers
continue using `IPortablePenSource` without an API break.

Authored cases cover shim brush identity, dash snapshot ownership, all four native
tile source forms, shared fill/stroke handles, cap/join/miter/dash transport, and
unavailable-state rejection. Tests, GPU/VM/image comparison, benchmarks, verifiers,
and CI qualification remain deferred under the implementation-first sequence.
ProGPU.Tests Release compilation succeeds with zero warnings/errors; the source-built
LibreWPF PresentationCore build also succeeds (four unrelated font-source warnings).

### Implementation-first checkpoint: nonpainting pen and empty drawing bounds

Native `DrawingImage` natural-bounds inference now distinguishes pen resource
presence from pen contribution. A pen with a null brush no longer rejects a valid
filled geometry, including path geometry that previously entered the fixed-stroke
only branch. A pen with a brush but zero resolved thickness uses unwidened geometry
bounds, even when there is no fill. Animated thickness is resolved before that
decision. Null geometry and drawings with neither a fill brush nor a contributing
pen return empty bounds, allowing the image replay to remain an empty operation
instead of rejecting the containing scene.

This follows source-built WPF `Pen.ContributesToBounds` (pen and brush presence,
not thickness) and `BoundsDrawingContextWalker.DrawGeometry` (geometry presence
and either fill or pen contribution). Those sources define the contract; this is
an original adaptation of ProGPU's existing typed pen resolution and geometry
bounds code, not a copied foreign renderer algorithm. The public
[GeometryDrawing contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometrydrawing?view=windowsdesktop-10.0)
describes its separate fill and stroke resources. Positive-width stroke inference
retains its existing supported shapes and explicit unsupported cases; general
path/dashed-stroke natural bounds are not claimed complete.

The new dispatch adds fixed-work typed metadata checks with existing hash lookup,
no resource arrays, pixel loop, shader, readback or ABI change. Managed WPF and
ProGPU managed rendering remain unchanged. Authored native fixtures cover positive
and zero-width brushless pens, zero-width brushed pens with/without fills,
animated-to-zero thickness, unpainted drawings and null geometry, asserting native
image-mapping state or its absence. The required MIL coverage manifest is
regenerated. Native library and MIL test executable compile; tests, native Windows
image/VM comparison, benchmarks, verifier and CI qualification remain deferred
under implementation-first sequencing. The full MIL/DirectX/Direct2D/Win2D goal
remains open.

### Implementation-first checkpoint: shared native path and dashed-stroke bounds

Native MIL natural bounds for `DrawingImage`/`DrawingBrush` sources now route
positive-width path pens through ProGPU's native Direct2D path widening. This
includes line/quadratic/cubic/arc contours, caps, smooth joins, split unstroked
runs, custom dashes and animated dash offset. Dashed fixed lines, positive-area
rectangles/rounded rectangles and ellipses use the existing native path builders
and the same widening endpoint. Existing analytic solid fixed-shape bounds stay
on their exact helpers. Filled-figure bounds are unioned through the existing
native fill bounds reader; hollow stroke figures are not made fillable.

The new internal `create_native_stroke_geometry` shares the canonical native
segment emitter with fill conversion but retains explicit open/closed state,
rejects disconnected segments, and shifts MIL outgoing smooth-join metadata to
Direct2D's incoming-segment flags. A return to the start point does not close an
open stroke. Full arcs still split into endpoint arcs, with a smooth internal
seam. No public COM/C ABI/module contract changes are introduced.

Geometry transforms are applied to the spine with the existing native `Simplify`
implementation before widening. DrawingGroup/world transforms apply afterward.
Curve bounds use the existing native flattening/widening approximation with the
WPF default 0.25 tolerance tightened by a conservative world-matrix norm. This
is not a claim of analytically exact curved/dashed extrema or Windows pixel
parity. Native widening still rejects unsupported closed topology; collapsed
contours without a representable widened area fail explicitly rather than losing
cap paint. General stroked geometry groups/combined geometries, degenerate dashed
fixed shapes, and exact curve/dash differential qualification remain open.

Provenance: original ProGPU native path conversion, Direct2D widening, dash and
fixed-shape builders; source-built WPF `Geometry.StandardFlatteningTolerance`,
`GetBoundsInternal` and `BoundsDrawingContextWalker.DrawGeometry` supply contracts,
not copied foreign rendering algorithms. Traversal/storage is O(S) for source
segments plus existing bounded flattening/dash work and its temporary records.
Spine conversion occurs only for a nonidentity geometry transform. Double-to-float
dash adaptation uses NEON/SSE2 lanes and a bounded scalar tail where available;
contour/segment traversal is state-dependent topology work, not a pixel fallback.
There are no new shaders, readbacks, pixel loops or managed WPF workarounds.

Authored fixtures cover open-returning contours, explicit closure, malformed
join counts/discontinuity, exact line/rectangle image mapping, nonuniform spine
transform ordering, dashed line mapping, fixed dashed shape compilation and
curved/smooth/animated-dash scene construction. The MIL coverage manifest is
regenerated. Native library, MIL tests and Direct2D compatibility tests compile;
fixtures are not executed. Windows/VM and cross-platform images, allocation/stress,
benchmarks and CI qualification remain deferred under implementation-first
sequencing. The independent managed WPF implementation is unchanged.

### Implementation-first checkpoint: composite geometry source bounds

Native drawing-source bounds now accept multi-child/nested `GeometryGroup` and
`CombinedGeometry` inputs. Filled bounds come from the existing ProGPU native
boolean/outline solver, not the union of operand rectangles. Even-odd duplicate
fills can therefore produce an empty drawing source. Stroked groups separately
visit original child contours, so fill cancellation cannot erase a visible pen;
combined-geometry strokes use the actual combined boundary, never operand edges.
The root combined outline is reused for its fill and pen queries.

Every ancestor geometry transform is accumulated before widening, while the
drawing/world transform remains post-widen state. Existing singleton-group fill
bounds retain their exact native leaf path; empty groups return without creating
native geometry. Fixed leaf stroke paths share the same internal factory used by
dashed fixed-shape bounds. The outlined native path DTO now contains canonical
fill segments as well as stroke contours, and its metadata bounds describe those
actual segments rather than the original operands' potentially larger extent.

This exposed and fixes a shared native Direct2D dash-order issue: stroke queries
must not reverse a closed contour to normalize its winding before splitting its
dash pattern. Bounds and hit queries retain original order. Dashed `Widen` also
retains it; a fully visible closed run chooses inward/outward sides from its
original winding after splitting. Undashed offset-contour construction retains
its existing canonical orientation. The fix belongs in ProGPU's path core and
benefits native Direct2D consumers as well as MIL.

Provenance is original ProGPU boolean, native path, fixed-shape, dash and widening
code. The WPF typed geometry graph supplies topology and transform contracts.
There is no copied foreign renderer, managed bridge workaround, pixel readback,
new shader or public ABI/module change. Complexity is bounded graph traversal
plus the existing outline/flattening/dash costs and O(B) retained boundary data;
shared handles are processed per transformed occurrence. Composite outlines and
temporary native path objects are not yet retained across bounds queries, and
no performance improvement is claimed without later measurement.

Authored MIL cases cover distinct/repeated group children, alternate-fill
cancellation with and without a pen, winding fill, union/intersect/xor/exclude,
empty combined results, combined children in groups, nested geometry transforms,
post-widen DrawingGroup transforms, and empty groups. Authored Direct2D cases
cover counter-clockwise partial/full dashed widening, hit queries and bounds.
Native library, MIL/Direct2D compatibility and Direct2D WebGPU test targets
compile; test execution and all VM/image/benchmark/CI qualification remain
deferred. Curved composite bounds retain the native solver's tolerance, while
unsupported/self-intersecting stroke topology and collapsed-cap cases still fail
closed. Full MIL/DirectX/Direct2D/Win2D parity is not established.

### Implementation-first checkpoint: crossing queries and point-cap bounds

Native MIL path bounds now benefit from a typed distinction between Direct2D
stroke queries and widened-outline construction. Bounds/hit queries operate on
the original strips and joins, so closed self-intersections, retraces and zero
signed area no longer enter the outline-only simple-polygon rejection path.
Closed two-edge retraces are queryable too. This removes the O(P²) pairwise
topology predicate from query preparation; actual query and curve/dash costs
remain, and no measured speedup is claimed. Widened-outline construction retains
its explicit simple-topology limitation rather than publishing an invalid fill.
The native hit kernel also recognizes the outer round semicircle at a 180-degree
reversal, without turning it into a full disk extending past short flat-ended
segments on the inner side.

When a native MIL stroke contour collapses to a point, bounds now query that
actual transformed spine and reuse the existing transformed line-cap support
with the canonical X-axis point tangent. Open contours keep their start/end or
gap-cap choices; closed point contours use MIL's existing round pair. Flat/flat
point contours and point caps hidden by their dash phase produce empty bounds.
Fixed zero-length line sources accept visible cap bounds and empty flat pairs.
Nonpoint contours that fail to produce a widened area are not treated as point
caps, preserving fail-closed behavior for unresolved tiny/all-gap cases.

The zero-length dash visibility calculation is now one allocation-free native
method shared by cap replay and bounds. It preserves odd-pattern repetition,
animated/negative offsets, overflow checks and the existing inclusive dash-end
rule. Its checked prefix/phase traversal is O(D) dependency-carrying metadata
work; point-cap extrema reuse bounded native support calculations. Existing
SIMD strip/hit and curve kernels remain authoritative, with no new CPU pixels,
GPU readback, shader, public ABI/module surface or managed WPF workaround.
Provenance is original ProGPU path-query, MIL cap and dash code, not a copied
foreign rendering algorithm.

Authored Direct2D fixtures cover bow-tie bounds/hits with solid and dashed pens,
two-edge round retraces, short open reversals and explicit unsupported complex
`Widen`. Authored MIL mapping/empty-source fixtures cover bow ties, open/closed
point paths, square/round/triangle/flat cap combinations, odd dash patterns,
positive/negative animated phase boundaries and fixed point lines. Native library,
MIL/Direct2D compatibility and Direct2D WebGPU test targets compile; fixtures are
not executed. Full Direct2D point-cap/widened-outline parity, transform-induced
collapse replay fidelity, tiny/all-gap nonpoint contours and cross-platform/VM
images, benchmarks and CI qualification remain open. Bounds support is not a
claim that these final rendering/qualification requirements have been met.

## Implementation-first checkpoint: compound native stroke widening

The shared native Direct2D path core now materializes crossing/retraced closed
strokes, multi-edge open and dashed strokes, and mixed open/closed figures as a
nonzero-fill union of consistently oriented segment strips, outer joins and
endpoint caps. This removes the simple-polygon requirement from those `Widen`
paths and prevents opposite contour orientation from canceling overlapping
stroke coverage. Bounding-box-collapsed insets use the same compound path instead
of retaining an inverted inner hole. Simple single-contour solid cases retain
the existing compact outline, with compound fallback on unsupported topology.

Implementation/provenance: `progpu_native_direct2d_path.cpp` reuses the original
ProGPU `append_stroke_side_join`, `append_circular_arc_segments`,
`build_terminal_dash_outline`, dash-run splitter and affine transform kernel.
No foreign source, second renderer, shader fork, CPU pixels/readback, managed
bridge workaround or public ABI/module change is introduced. The managed
`ProGpuDirect2DSurface.WidenGeometry` delegates to its provider; this patch changes
the native compatibility provider's previously rejected geometry construction,
not the independent managed WPF renderer or its stroke rasterizer.

For P flattened line/dash segments, compound assembly and owned output use O(P)
time/space after flattening/dash splitting. AArch64 NEON and x86 SSE2 compute two
double-precision line frames per batch with a bounded scalar tail. Frames are
allocated once per contour/run; the intrinsic kernel uses caller-owned spans.
Bounded join/cap pieces retain small owned vectors. Transforms gather all pieces
per run/contour into one O(P) buffer and use the existing SIMD affine kernel.
Topological assembly and variable-length output walks remain sequential; no
per-piece GPU submission is added. Existing compact closed-contour eligibility
and topology checks still have O(P²) worst-case cost. Further scratch pooling and
compact-offset topology work remain open; no speedup is claimed without profiling.

Fixtures authored in `progpu_native_direct2d_compat_tests.cpp` cover bow ties,
round reversals, overlapping ring/line figures, collapsed rectangle insets,
all four joins, distinct source/dash caps, odd dash patterns/negative phase,
reflection/shear and odd/even segment counts. Explicit coverage probes accompany
fill-versus-stroke grids; old fixed-output-figure expectations are replaced with
coverage checks. The native library and MIL/Direct2D compatibility/WebGPU test
targets compile. No tests, image/VM comparison, benchmarks, verifiers or CI
qualification ran. Point-only/tiny contours, transform-collapse replay fidelity,
remaining compact-offset edge cases and full DirectX/Direct2D/Win2D parity remain
open. See the Direct2D work log for the public-contract reference.

## Implementation-first checkpoint: convex inset eligibility

Native `Widen` now limits compact inner/outer rings to simple strictly convex
source contours whose inset edges still advance in their source edge directions.
An inset may invert and regain positive signed area before either source bounding
box axis collapses; area/nesting checks alone must not turn that inverted contour
into an unpainted hole. Both the default miter and explicit-style compact paths
now fall back to the established compound stroke when the inset loses an edge.
Non-convex/collinear closed sources use compound strips/joins/caps directly.
This changes representation, not stroke semantics or supported join quality.

`convex_inset_preserves_edges` in the original native Direct2D path core is an
allocation-free O(P) predicate over caller-owned spans, with paired NEON/SSE2
double dot products and a bounded scalar tail. The explicit-style compact path
also prepares line frames once with `prepare_stroke_line_frames` and reuses them
for both sides' `append_stroke_side_join` calls. That adds one O(P) frame buffer
while removing repeated normal calculation at those calls; no timing improvement
is claimed. Compound output and the existing compact topology checks retain
their previously documented complexity. No foreign implementation, raster
fallback, shader, managed workaround, public ABI or module surface changes.

Authored triangle/kite/concave-notch fixtures exercise odd/even contour counts,
both source windings, all joins, multiple widths, reflected/sheared transforms,
an explicit filled point beyond the triangle inradius and the preserved narrow
two-figure ring. Coverage grids use the independently queried stroke as an
oracle. The native library and MIL/Direct2D compatibility/WebGPU test targets
compile; fixtures, Windows/VM/images, benchmarks, sanitizers, verifiers and CI
qualification remain unexecuted. Managed provider applicability is unchanged
from the compound-widening checkpoint. General point/tiny-contour behavior and
transform-collapse fidelity remain implementation work; complete platform and
DirectX/Direct2D/Win2D parity is not established by these builds.

## Implementation-first checkpoint: actual dashed-stroke source bounds

MIL drawing-source inference now measures emitted dashed stroke outlines instead
of using the public Direct2D `GetWidenedBounds` query's original-spine-inclusive
envelope. Actual fill bounds are still unioned separately by the existing typed
fill collector. A partial dash on a hollow path therefore does not acquire an
unpainted tail, and an all-gap diagonal/curve does not become a positive-area
source simply because its original path has a bounding rectangle.

The shared internal `get_widened_outline_bounds` helper consumes `Widen` through
a constant-storage simplified sink. It reuses original ProGPU
`transformed_point_bounds` for line spans and `include_cubic_bounds` for analytic
curve extrema, without recording a second path or storing sink-side point arrays.
The bounds and explicit `has_outline` output publish only on success; the public
Direct2D query and public COM/C/module contracts are unchanged. MIL point contours
still use the established cap-pair/phase logic. For an empty nonpoint dash result,
one solid-outline probe confirms that flattening retained geometry before the
result is accepted as empty. Tiny curves lost by flattening remain fail-closed.
An emitted outline collapsed to zero area by the world transform has no
DrawingImage source area. Solid ordinary-stroke bounds keep their existing path.

Provenance/applicability: original ProGPU native `Widen`, dash/compound/compact
helpers, affine SIMD bounds and cubic extrema are the implementation sources.
The LibreWPF WpfGfx `CShapeBase::GetTightBounds` fill/stroke/hollow contract was
consulted only for observable bounds semantics, not copied or translated.
The managed WPF renderer continues to use its source-integrated geometry bounds;
this closes a native MIL adaptation gap rather than adding managed workarounds.
No shaders, CPU pixels/readback or per-item GPU submissions are introduced.

For P emitted stroke segments, the sink costs O(P) work and O(1) state plus one
small owned sink object. It retains the widening algorithm's existing work and
O(P) temporary outlines; the all-gap probe is one additional bounded widening,
not a retry loop. Line spans use the existing SIMD reducer; current-point/cubic
root traversal is sequential with constant work per segment. No performance
claim is made. Fixtures cover partial/all-gap line bounds, a positive-area
all-gap diagonal and curve, tiny nonzero lines, output preservation on failure,
and analytic transformed cap bounds versus recorded geometry. Native library,
MIL and Direct2D compatibility/WebGPU test targets compile, and the MIL source
digest was regenerated. Tests, verifiers, images/VMs, profiling and CI qualification
remain deferred. General point-only Direct2D behavior, lost tiny curves,
transform-collapse replay fidelity and full platform parity remain open.

## Implementation-first checkpoint: pen-aware small-curve flattening

The native Direct2D stroke flattener now accounts for pen width as well as
centerline distance. Nonconstant coincident-endpoint curves cannot immediately
become a point. Each accepted span requires forward control-polygon tangents
within a pen-normal angular error bound; half the pen-space tolerance is reserved
for centerline deviation and half for normal deviation. A conservative
twice-sine bound avoids square roots. Fixed double lanes use NEON/SSE2 with no
heap allocation in the predicate. Recursive subdivision retains the existing
depth-20 limit but stroke processing now fails rather than relaxing its criterion
at that limit. Overflow-safe `std::midpoint` is used for subdivision.

`GetWidenedBounds`, `StrokeContainsPoint` and `Widen` share this stroke mode.
Flattened edges within one original curve publish smooth round joins regardless
of the user's join choice between original segments, preserving curve reversals
and cusp coverage. Non-stroke traversal retains its existing tolerance/depth
policy. The implementation extends original ProGPU `flatten_cubic`, `visit_path`
and `build_flat_polylines`; no foreign implementation, new shader, raster
fallback or public ABI/module change is introduced.

MIL now uses the existing emitted-outline bounds collector for curved solid
strokes too: conservative full-disk public-query supports are not exact
DrawingImage source bounds for short round reversals. Point-cap handling remains
separate. The managed Direct2D `WidenGeometry` entry remains provider-backed;
the source-integrated managed WPF/analytic GPU rendering paths do not consume
this native COM-query flattener and are not duplicated or changed here.

For C input cubics and P generated spans, work is O(C + P), output O(P), and
recursive stack O(20), with a maximum 2^20 spans per input cubic before the
existing work limit. Pen-aware refinement can produce more geometry than the
old centerline-only approximation; no performance improvement is claimed.
Authored fixtures cover large and tiny quadratic reversals with analytic
stroke-only bounds and cap-side hit points, all join styles, a tiny 2D loop at
coarse/fine tolerances, and solid/on/gap MIL DrawingImage mapping. Native library,
MIL and Direct2D compatibility/WebGPU test targets compile; the coverage source
digest was regenerated. No tests, Windows/VM/images, benchmarks, verifiers or
CI qualification ran. This replaces the blanket tiny-curve flattening rejection
for representable curves within the subdivision budget, not general point-only
Direct2D or transform-collapse replay qualification. Precision-limit cases and
the remaining full platform/DirectX/Direct2D/Win2D goal remain open.

## Invariants

- No reflection or private managed field scanning in the product bridge.
- No pointer-shaped WPF objects in public package contracts.
- All protocol reads are bounds checked; unknown required data fails closed.
- Channel batches are transactional at the ProGPU boundary.
- Resource identity and generation are stable across unchanged frames.
- Native renderer APIs remain reusable by WPF, WinUI, and Avalonia.
- DirectX is a backend/interop surface over the shared renderer, not a second
  scene implementation.
