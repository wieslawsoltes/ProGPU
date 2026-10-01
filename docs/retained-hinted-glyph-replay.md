# Retained hinted glyph replay

The native unmasked paint pipeline binds an explicitly empty group 2 between its
atlas and paint texture groups. A nonempty mask-chain layout with no binding is
invalid on the actual Dawn retained-bundle path, even when the fragment entrypoint
does not sample a mask. The engine owns and releases this empty layout/group;
single/chained masks retain their original bindings. Managed paint already binds
its existing mask slot unconditionally. Package pixel checks remain exact and now
report first differing coordinates/channel and maximum byte delta on failure.
Scene submission metrics are per-call; the original glyph metric is cumulative.
Their independent assertions require exactly one submission under each contract.

Native color paint preserves the original source alpha representation per command.
Materials and straight-alpha textures use the existing straight fragment
entrypoints with color `SrcAlpha` and alpha `One`, matching ordinary native Text.
Premultiplied texture inputs retain their direct premultiplied fragment output
and color `One`; they never take an unpremultiply/reblend round trip. R8 mask
targets retain the original mask entrypoints and `One` for both components.
Preparation and encoding use the same retained kind/flag policy, preserving
source draw order and lazy engine-owned unmasked/single/chained variants.

This restores the original multiplication stage as well as its real-number
source-over equation. Shader RGB multiplication followed by blend `One` is not
byte-equivalent to original straight RGB with fixed-function `SrcAlpha` on UNORM
targets: the actual packaged DX12 x64/ARM64 overlap controls differed in 99 RGBA
bytes by one, including red 3 versus 2 at (11, 13). Stock Linux and Dawn Windows
controls also reported this class of exact-byte difference. The original
constant-gradient, fractional, overlap and cold/warm full-RGBA gates remain
unchanged. Device-free native policy and managed wiring/retirement controls do
not establish GPU correction; the final package binaries must pass those gates.
Managed paint already selects output/blend representation from its actual
texture alpha mode, with ordinary materials remaining straight, so this native
pipeline correction does not change managed policy or the canonical shaders.

This explicit consumer API preserves one original prepared paragraph generation.
It is implementation work, not Display selection, source-editor/input admission,
native/package qualification or an application performance claim.

## Primary-source design gate (2026-10-01)

- [Skia shaped text](https://docs.skia.org/docs/dev/design/text_shaper/),
  [SkParagraph public contract](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h)
  and [Skia font-scaler separation](https://skia.org/docs/user/tips/) separate
  positioned text from glyph rendering. Adopt original positions and source
  identity; reject re-shaping and a second font decoder at replay.
- [DirectWrite/Direct2D](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
  separates reusable layout/interaction from rendering and supports spatial
  glyph brushes. [Win2D CanvasTextLayout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
  exposes retained formatted text. Adopt retained lifetime and paint separation;
  adapt spatial paint to ProGPU's existing canonical brush sampler and Text
  coverage in each original occurrence, preserving the caller's brush domain.
  The [public DrawGlyphRun contract](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nf-d2d1-id2d1rendertarget-drawglyphrun)
  keeps the original positioned glyph run separate from its selected brush; it
  does not establish ProGPU overlap or pixel parity. Reject automatic platform
  measuring/rendering-policy substitution.
- [WebRender font-instance contract](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
  keeps rendering flags in instance identity. Its [historical overview](https://github.com/servo/servo/wiki/Webrender-Overview)
  describes retained display lists, viewport visibility and atlas/quad batching;
  it is historical design context, not proof of today's implementation. Adapt
  exact immutable generation/outline identity to the existing atlas cache and
  culling; reject assuming a glyph ID alone determines physical coverage.
- [Vello architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md)
  and [renderer overview](https://github.com/linebender/vello) distinguish
  production-oriented CPU/GPU rendering from the experimental compute renderer.
  [Parley concepts](https://github.com/linebender/parley/blob/main/doc/concept.md)
  distinguish itemization and shaped runs. Preserve ProGPU's existing selectable
  CPU/SIMD/compute/shader algorithms rather than importing a foreign renderer,
  changing defaults or claiming compute is invariably faster.
- [HarfBuzz shaping contract](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
  reinforces that shaping owns script-specific substitution/positioning. Keep
  the original logical/source maps, fallback runs and explicit variable instance
  under the originating read lease; replay performs no Unicode/font execution.

No foreign implementation is copied. These are architectural comparisons; the
production algorithms reused here are original ProGPU GlyphAtlas/Text.wgsl,
native glyph execution, registered materials and texture sampling.

The caller keeps an owned `NativeCompiledPicture` live throughout any cached or
rendered use and explicitly disposes it at retirement, retrying a failed teardown.
Parent compilation acquires independent original geometry uses before flattening
new child candidates; it never disposes a borrowed caller/renderer cache. Ordinary
snapshots without hinted sources preserve their existing copied-byte behavior.

## Decisions across the complete rendering contract

| Concern | Consumer decision |
| --- | --- |
| Startup and pipeline initialization | Preparation allocates immutable records; existing raster pipelines remain lazy until real work. |
| Shaping/layout and display-list reuse | One original producer read lease survives caller disposal; every occurrence, including no-ink and repeated descriptors, is retained. |
| Visibility | Cull only actual physical padded raster bounds mapped into the logical frame. Brush bounds and source input are separate. |
| Cache keys/eviction | Geometry identity plus original outline slot; reuse existing atlas eviction/residency/generation, not a parallel unbounded coverage cache. |
| Upload and batching | Upload selected original segments on demand through existing staging; use existing CPU/SIMD/compute/shader coverage and draw batching. |
| Worker preparation | No new worker scheduling or native context concurrency; preparation borrows the existing destruction-excluding producer read lease. |
| DPI, phase and hinting | Exact prepared DPI, scale one and phase zero at raster replay; original positions remain unsnapped. Nonidentity bases and static zoom stay explicit gates. |
| Fallback/variables | Preserve producer-selected font/face/axes and every source owner. Never substitute design outlines or fallback after atlas failure. |
| Device loss/atlas generations | Existing atlas lifetime remains authoritative; failed or abandoned encoders invalidate unpublished physical coverage. |
| Paint | Solid glyph styles or direct per-occurrence canonical coverage and spatial paint; source Rect remains unchanged, with original outer opacity/clip/blend. |

## Source domain and private coverage storage

The typed hinted-geometry accessor shares the existing command reference payload
slot with other mutually exclusive command kinds. It adds no per-command storage
or wrapper allocation. Command copies preserve the original geometry identity;
recording, picture and compiled-scene leases still own its lifetime explicitly.
The existing 576-byte command-size gate remains unchanged.

The recorded `Rect` remains the caller's original ink/paint domain. Private
culling and atlas storage cover the selected floor/ceil raster bounds, including
the canonical four-physical-pixel padding, so a fractional source edge cannot
clip nonzero glyph coverage by a second rectangle pixel-center test.

ProGPU gradient points are local coordinates transformed by the brush's original
coordinate matrix; they are not implicitly normalized to a draw rectangle.
Private storage must not change those points or that matrix. Texture paint keeps
its original source/destination rectangles, extension policy and brush transform;
storage growth is not permission to stretch, retile or move the original paint.

## Required evidence before integration

The initial white-mask spatial design was rejected: its R8 intermediate can
requantize gamma-corrected coverage, and
unioning white coverage before translucent paint changes overlapping occurrence
composition. Source arithmetic demonstrates both risks; it is not a GPU receipt.
Exact full-pixel fractional-edge and overlapping-occurrence differentials remain
authored and unrun. The replacement directly evaluates the original shared
registered material or texture sampler and canonical Text coverage in every
original glyph fragment; changing assertions, excluding those occurrences or
substituting higher precision alone would not resolve the complete contract.

The dedicated lazy pipeline retains the original 96-byte glyph instance. Its
unused final four bytes carry an exact unsigned paint-record index through a new
vertex attribute; legacy text attributes and style lookup stay unchanged. One
96-byte immutable paint record owns original source coordinates, opacity, brush
index or source UV endpoints, original texture corners and sampling policy.
Material paint reuses the exact original registered brush/stops. Bounded texture
paint emits the original two hardware triangles, including independently snapped
corners; glyph tile-frame guards prevent ink outside the original allocation.
Extended texture paint uses only its already-admitted original axis mapping.
The shared shader helpers also remain the production algorithms for ordinary
Text, Vector and Texture draws, rather than a separate algorithm copy.

Both managed replay and native scene lowering now use this direct-paint route.
The additive native command retains a 32-byte prefix, one exact paint record
and every original positioned glyph; legacy glyph/style records are unchanged.
Retained texture bindings use owned resource ID/generation and actual view and
sampler identity, not resource ordinals or batch counts. Material paint retains
the original brush remap and effective state opacity. Actual device limits and
complete pipeline/resource initialization remain fail-closed before encoding.
These are implementation statements, not executed GPU or package receipts.

Bounded image quads may increase fragment work relative to glyph-only geometry.
The architecture is not a measured speedup; whole-pixel, lifetime, resource,
sampling/opacity/blend and complete package gates remain unqualified.

Matched cold/first-interaction timings, sustained-scroll percentiles/worst frames,
allocation/residency measurements, raw GPU differential/image controls, browser
AOT and complete stock/Dawn JIT/NativeAOT package cases remain required. Run them
against the same final binaries after implementation stabilizes; no measurement
or success is inferred from source review, existing CI or pipeline acquisition.
Static-buffer refresh and optional incremental pages need their own transactional
producer/atlas ownership contracts before those routes are admitted. Ordinary
compiled-scene retained replay must preserve
the full brush contract without weakening assertions, deadlines or defaults.

The Windows Dawn hinted-text package fixture now explicitly selects the owned
system-WARP factory from PR #238. Its selected RID opts into the optional Dawn
NuGet payload for both ordinary and NativeAOT publication. Generic forced
fallback, non-Windows Dawn selection and all stock-provider cases are unchanged.
The complete Build requires both companion producers before packing, then runs
every original hinted paragraph case and the direct-paint differentials against
the actual packaged native Dawn renderer. No scenario, pixel assertion, deadline
or independent stock-provider check is removed. The successful narrow WARP
readback receipts are prerequisites, not proof of these full renderer cases.
This integration now depends on both the hinted-resource stack and PR #238.
