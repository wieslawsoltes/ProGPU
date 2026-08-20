# Native C++ command parity

`GpuPictureNativeSceneCompiler.GetCommandCapability` is the executable
command-family inventory. `NativePictureCompilerTests` enumerates every
`RenderCommandType` and fails when a new command has no explicit native route.
Payload validation remains stricter than this structural classification and
always returns a typed failure without partially committing a scene.

| Route | Commands | Current boundary |
| --- | --- | --- |
| Direct draw | Rect, ordinary and combined path fill, exact general-path stroke, text, image, analytic geometry, polyline/spline, point/mesh/chart, glyph, and 3D line/mesh families | Combined fills retain the canonical bounded GPU postfix program. Combined-boundary strokes remain a typed exclusion because the managed renderer has no exact combined-stroke contract; operand-outline approximation is forbidden. |
| State scope | Clip, opacity, geometry-mask, opacity-mask, and blend push/pop | Canonical affine rectangles/rounded rectangles use the 1–4-node analytic fast path. Arbitrary line/quadratic/cubic/arc geometry, combined-path boolean clips, and 5–64 nested intersect/difference clips use retained GPU vector masks. Solid opacity folding is implemented; gradient/picture opacity-mask content remains a typed exclusion. |
| Nested picture | `DrawPicture` | Immutable retained children are recursively flattened with state-boundary validation. |
| Built-in extension | `DrawExtension` | Line/spline/chart/3D/hatch built-ins are selected by stable extension ID; hatch boundaries reuse retained path batches and shared hatch material kinds, while unknown or object-backed extensions fail closed. |
| Explicitly unsupported | `DrawStaticDxf`, `DrawVisual` | Static DXF and embedded visual commands retain live managed/GPU ownership and cannot enter the pointer-free immutable scene contract. |

Image effects include external same-device RGB textures, zero-copy paired
R8/RG8 or Tier-1 R16/RG16 luma/chroma views, nearest/linear/cubic sampling,
affine color operations, luminance-to-alpha, spherical projection, explicit R8
texture masks, bounded analytic state-mask chains, and the shared shader's
bounded blur footprint. External image
bindings are ordered by immutable resource id plus primary/chroma/mask role, so
changing a view invalidates the retained image page without copying plane or
mask pixels across the C# / C++ boundary. The managed-authoritative separable
Gaussian implementation is now shared directly with C++: filterable RGB,
R8/RG8 planar, and Tier-1 unfilterable R16/RG16 sources run two retained
horizontal/vertical GPU passes inside the native command encoder, with no
steady-replay interop call or CPU upload. An identity first pass also converts
an unblurred Tier-1 planar source before fused effects. Image effects and bounded
analytic state-mask chains share one four-group shader layout instead of a CPU
materialization or extra texture pass.

This inventory distinguishes parity work from intentional ownership
boundaries; it must not be used to convert an unsupported command into a silent
no-op or an approximate fallback.

The arbitrary clip route is a direct cross-language port of ProGPU-owned
`Compositor.PushGeometryMask` and `PathAtlas.CompileFillPath` behavior. The
managed compiler emits one immutable vector-mask resource and contiguous path
and segment arenas; C++ validates those arenas once, rasterizes the unchanged
canonical `PathRasterizer.wgsl`, composes ordered nodes with
`ClipCompose.wgsl`, and copies the resulting R8 coverage entirely GPU-to-GPU.
Stable replay performs one scene submission with no retained upload or managed
allocation. The native atlas normalizes every packed UV only after the final
atlas size is known, so a later path that grows the atlas cannot invalidate an
earlier path's sampling coordinates.

Direct combined fills reuse that exact representation and GPU program rather
than materializing a CPU path operation. Semantic path records reference one
dense resource-local segment arena; later records may share or overlap already
covered ranges, so transformed instances retain one immutable outline instead
of duplicating segment bytes. Optional resource-local postfix ranges remain
canonical and contiguous. C# and C++ validate gap-free segment coverage,
canonical leaf ownership, finite bounds and fill rules, at most 63
instructions, and a maximum evaluation depth of 16 before any GPU resource is
created. Native x64/arm64 replay consumes the fixed-width records directly;
wasm32 performs one checked translation into host-sized offsets. The existing
`PathRasterizer.wgsl` evaluates both ordinary and combined fills, so this adds
no shader fork, CPU boolean flattening, per-command interop, or stable-frame
upload.

General-path strokes retain their source line, quadratic, cubic, and analytic
arc records. The managed compiler materializes dash intervals only when the
immutable picture changes, then transfers exact curve descriptors plus cap and
join topology. C++ compiles those records into the shared vector shader's GPU
curve/arc/cap/join lanes; ordinary conformal arcs remain one analytic quad,
while non-conformal local-width outlines are expanded once and retained.
