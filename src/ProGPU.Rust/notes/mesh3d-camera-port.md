# Original Mesh3D camera producers

Source: `src/ProGPU.WinUI/Controls/Viewport3D.cs`, SHA-256
`6a9e079f1e32dfed8294f760f1e5f177f0a814a2e3e8037205415933b49558b9`.

`mesh3d::ProjectionCamera`, `PerspectiveCamera`, `OrthographicCamera` and
`Camera` retain original defaults, raw properties, change decisions, atomic
SetView, LookAt, view and projection. Camera matrices use logical viewport aspect
independently of physical truncation/MSAA. The shared Mesh3D sample now consumes
both public camera producers instead of private hand-built camera matrices.

The view preserves Position + LookDirection before look-at subtraction and
inverse-transform-before-view order. Singular transforms leave the source view;
nonfinite/degenerate arithmetic remains explicit IEEE output, admitted separately
by the renderer. Perspective retains degree conversion order, original fov/near/
far/order rejection and positive-infinite-far branch. Orthographic width/aspect/
plane arithmetic has no extra domain clamp. The matrix formulas are independently
written mathematical expansion of the owned source's public System.Numerics
calls; no external runtime implementation was read or copied. Default-runtime
trig/normalization/inversion bit equivalence remains a later differential gate.

`CameraNavigation` ports initialization, ordered orbit/pan deltas, sign-only wheel
zoom, Shift/orthographic key panning, radius/width/pole clamps and atomic view
application. It reports real property-change and viewport-invalidation counts;
width changes outside ApplyCameraState reset navigation initialization exactly as
OnCameraChanged does. The host calls camera_changed for external property changes
or owner replacement. Focus/enablement, pointer capture and drag threshold,
selection gestures, event subscriptions and platform key translation remain
independent host input adapters. No event callbacks are retained in GPU packets.

Independent XYZ and cofactor lanes use safe SIMD; dependent reductions and
controller transitions preserve order. All operations are bounded O(1), without
allocations or GPU work. Source formatted only: no build, tests, execution,
fixtures, artifacts or pixel/package qualification.
