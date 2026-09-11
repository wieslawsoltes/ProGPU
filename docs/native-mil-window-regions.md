# Native MIL host window regions

## Core implementation checkpoint — 2026-09-08

LibreWPF now serializes a host window region into canonical MIL geometry and
attaches it to a host container above both the main visual and owner-surface
popups. The existing ProGPU C++ geometry compiler and renderer perform clipping.
This removes the native host's blanket region rejection; partial viewports and
independent X/Y DPI remain guarded until their shared frame contract is extended.

The outer rectangle excludes a nonzero-fill group of clipped, same-winding hole
rectangles. Overlapping exclusions remain excluded, unlike even-odd fill. Empty,
nonfinite and nonintersecting holes are ignored; an invalid nonempty outer bound
fails closed. A region with no holes uses the ordinary exact rectangle clip.
An empty region clears the host region, preserving the existing host convention.

ProGPU's `append_boolean_geometry` calls `append_group_fill_leaf` for the hole
group. These rectangle contours become one leaf, so the complete difference has
three boolean nodes regardless of hole count. Segment and packet storage grow
linearly, not the shader's boolean stack. No new geometry algorithm, shader,
CPU rasterization, pixel readback or per-hole GPU submission is introduced.

`PortableWindowRegion` owns a snapshot of exclusions and exposes a read-only span
as well as the compatibility read-only list. Callers must install a new region
to change it. This applies equally to managed and native retained consumers.
The host marks replay dirty on installation. Equal-topology rectangle movements
use existing mutable MIL packet updates; structural changes may rebuild the
channel. The host container does not inherit the application's root transform.

## Provenance, costs and paired applicability

Original ProGPU source is `9819c970`: `PortableWindowRegion`,
`NativeMilBatchBuilder` geometry writers and native MIL
`append_group_fill_leaf`/`append_boolean_geometry`/`append_geometry_clip`.
Original LibreWPF source is `9a5392c3f`: `SetWindowRegion`, managed
`TryCreateWindowRegionClip`, the native scene compiler and session delta path.
The new behavior adapts typed host state into those existing reusable contracts;
it does not copy another engine or add a WPF-specific native renderer.

[WPF fill-rule contracts](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometrygroup.fillrule)
and [geometry combination contracts](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.geometrycombinemode)
inform the nonzero/difference representation. General renderer, text and cache
architecture remains as documented in [popup composition](native-mil-popup-composition.md).
No typography, glyph cache, surface allocation, startup or DPI policy is changed.

Snapshot allocation is O(N) once per region installation. Host serialization and
native contour construction are O(N); packet writing/resource ordering are
dependency-driven control work, not a new whole-buffer CPU pixel fallback.
Existing shared clip execution and configured GPU/SIMD policy remain authoritative.
No speed claim is made without the deferred benchmarks.

## Authored coverage and remaining qualification

- ProGPU managed fixture: immutable array/list snapshot and read-only views.
- LibreWPF producer fixtures: clipped overlapping holes, container placement above
  root/popups, mutable rectangle movement and clearing the region.
- ProGPU native fixture: 64 overlapping rectangular exclusions compile to one
  nonzero hole leaf and a three-node difference program.

Managed fixtures and the portable native MIL fixture target are compilation
checkpoints only. Tests, full renderer/Windows builds, pixel comparisons,
managed/native differentials, VM/input/lifetime runs and exact-head PR CI remain
deferred until the core feature freeze. This does not implement OS-level shaped
window input regions or qualify transparency, separate popup surfaces, DPI
transitions or cross-platform visual parity.
