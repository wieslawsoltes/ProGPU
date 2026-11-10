# Original retained late-camera vector route

Production implementation only; source formatted, with no compilation, tests,
fixtures, execution, shaders checks, benchmark results or generated artifacts.

Normal CompilePicture/CompileVisualCommand adds **100** to each emitted vector
shape after compilation. The separate static-DXF route adds **200** and is not
aliased to this route. The original canonical Vector WGSL is unchanged.

Archive replay now admits late-camera rectangles, rounded rectangles, ellipses,
circles, triangles, quads, point batches, dot/device grids, lines, quadratic/cubic
curves, polylines and paths. It retains the source model transform separately from
CameraView * enclosing placement. Source gradient/noise/hatch brush transforms
and pen policy retain original local/non-local selection and inverse-before-brush
composition. Existing analytic and complete stroke preparation produce geometry;
dash/cap fill fallback retains its actual atlas route, never a proxy envelope.

Private Scene LateCamera records retain command ownership through deferred
geometry decisions, source placement, opacity, masks and blend admission. Empty
marked commands retain camera selection. Compilation keeps each command's camera
for coverage, but the last marked command selects the common target camera exactly
as the original does. Shape marking uses independent SIMD lanes with a bounded
scalar tail. The existing renderer already carries that actual compiled camera.

Path coverage scale/phase derives from model * command camera, retaining the
original axis/non-axis scale choice, phase grid, quantization and raster scale.
Quad positions/brush coordinates retain the original model frame. Late paths never
select exact atlas-copy pixels. Direct rounded/sharp path selection preserves its
original model-only geometry decisions and offscreen policy; text-derived path
gamma includes the active camera. Receiving clip/scissor and source hit metadata
remain independent. Source placement composes the camera, not the model geometry.

Ordinary atlas text now has its own connected late-camera route; see
`late-camera-text-port.md` for the original post-compilation flag order and shared
uniform distinction. Remaining explicit gates: hinted and native text transports,
spline/extension/series/mesh/texture/backdrop commands, marked picture/visual/scope
commands, non-affine model admission and Scene-to-archive export of this private
execution wrapper. The original archive wire still retains source camera fields;
reading/replay is not a claim that arbitrary lowered Scene records are losslessly
serializable. Bounded direct-R8 mask source family admission remains separate.
No original source/provider/package/native/browser pixel or performance
qualification follows from this authored production increment.

Original owned source SHA-256:

| Source | SHA-256 |
|---|---|
| `src/ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
| `src/ProGPU.Backend/Shaders/Vector.wgsl` | `6d2d92d59341ea91dc33f851c08467f4b4dcd7f270b5f1f638b9f45006e481ed` |
| `src/ProGPU.Scene/RenderCommand.cs` | `dff6b8f8c375dcb8b2a2bae357145416bd48d05a5c12ebdbbbe0710dd98858f8` |
