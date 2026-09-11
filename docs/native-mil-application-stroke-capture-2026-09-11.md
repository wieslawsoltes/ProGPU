# Native MIL application stroke capture

## Acceptance and blocker

Acceptance application: LibreWPF SDK external package application, explicit
NativeMilWgpu renderer with the native input index enabled. User action: launch
the application and present its first retained frame. Source translation reached
native scene capture but rejected cubic geometry (command 2719, owner 1559).
After curve admission the same path exposed connected joins, then an open
undashed polyline (kind 0, flags 1025, 18 points, width 1).

## Bounded implementation

- Preserve quadratic/cubic controls in the existing PathStroke wire format.
  Control-hull bounds only prune queries; the shared canonical hit-test shader
  evaluates the curve. Square-cap broad-phase padding includes diagonal corners.
- Capture connected PATH_JOIN geometry through the renderer's shared
  create_join_triangles implementation, preserving its conformal/local-affine
  domain and source owner/clip metadata.
- Extend existing polyline capture to open contours, retaining endpoint caps,
  interior joins, and no artificial closing edge or seam join.
- Include the native failure status in NativeMilException diagnostics.

Original implementation references are ProGPU's own
GpuHitTesting.cs, GpuRenderCommandHitTestCache.cs, GpuHitTesting.wgsl, and
progpu_native_geometry_stroke.hpp. No external source was copied. The shared
scene-builder implementation feeds both wgpu-native and Dawn; no provider-local
shader, WPF-local geometry index, or managed-renderer fallback was introduced.
Existing intrinsic affine preprocessing and shared join construction are reused.

## Verification and remaining gate

The native MIL regression executable passed after adding curve payload, join,
open-endpoint, and whole-index rejection coverage. A paired managed PathStroke
encoding regression passed both cases (zero skips); the managed test build had
65 warnings and zero errors. Both native providers compiled on macOS ARM64.
The final native MIL regression executable passed in 0.74 seconds. Generated
coverage and protocol consistency checks passed without changing their artifacts.
These checks do not qualify all point/region queries or other platforms.

The diagnostic external application still fails with UnsupportedCommand after
these changes. Its next rejection must be localized before application acceptance
can pass. The diagnostic run used explicitly replaced local binaries, not a clean
new package bundle. Temporary native logging was removed from the source.

Final exact-head packages, required CI (including queued macOS jobs), platform
application/interaction validation, and ordered dependent PR updates/merges remain
required. This checkpoint is not application qualification or full DirectX/Direct2D
parity. Broader API expansion remains outside the core delivery critical path.
