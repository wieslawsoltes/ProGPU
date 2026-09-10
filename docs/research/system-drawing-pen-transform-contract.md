# System.Drawing pen-transform contract

## Source contract

This slice restores the complete .NET 10 `Pen.Transform` family: defensive get/set ownership, reset, prepend/append multiplication, translation, scaling, and rotation. The public matrix is ordinary managed state, is copied on assignment and retrieval, follows `MatrixOrder`, survives cloning, and remains protected on immutable known-color pens. A directly assigned or multiplied matrix must be finite and invertible; scaling can still produce the singular state accepted by the transform-operation surface.

The public shape is checked against the pinned `System.Drawing.Common` 10.0.11 reference assembly. Observable state and composition were checked against the repository's source-reused upstream WinForms tests. The portable geometry and rendering implementation is original ProGPU code.

## Typed anisotropic stroke model

A pen transform changes the pen tip, not the path centerline. For the linear portion `P` of an invertible pen matrix and a source centerline `C`, ProGPU constructs the exact affine model:

`P × stroke(P⁻¹ × C)`

The inverse maps the centerline into circular-tip space, the existing typed stroke widener applies width, joins, caps, and dashes there, and the result is mapped back through `P`. The centerline is therefore unchanged while non-uniform scale, rotation, and shear produce the correct anisotropic tip. Translation is retained by the public matrix contract but intentionally excluded from tip geometry. A singular operation produces an empty stroke instead of a fabricated scalar-width fallback.

The widened nonzero-fill geometry is shared by `GraphicsPath.Widen`, transformed outline hit testing and bounds, and `Graphics` rendering. Retained drawing records a normal typed brush-and-path command, so the managed renderer, bitmap backend, picture ownership, clipping, compositing, and graphics transform continue through existing ProGPU contracts. No HDC, GDI+ handle, runtime reflection, private-field scan, or alternate fake drawing object is introduced.

## Quality and performance gates

Eight focused tests cover defensive state ownership and cloning, matrix order and invalid matrices, anisotropic bounds, translation-neutral geometry, outline hit testing, dash gaps, retained command lowering, singular behavior, production bitmap pixels, zero managed allocation across 10,000 warmed transform-mutation groups, and a 6.5–8.5 KB allocation window for the matching widened-polyline workload. The full drawing suite passes 298/298.

`GraphicsPathBenchmarks.WidenAnisotropicPenClone` records the cost and allocation of the actual inverse-transform, flatten, widen, and forward-transform path. The 2026-08-27 ARM64/.NET 10.0.11 ShortRun measured a 1.678 microsecond median (1.697 microsecond mean, 0.038 microsecond standard deviation) with 7.16 KB allocated. One launch, three warmups, three measured iterations, and denied process-priority elevation make this coarse local subsystem evidence. Work is linear in the flattened centerline and emitted stroke triangles; adaptive flatness is tightened when the tip can amplify error. The focused allocation window and zero-allocation mutation test independently guard geometry and scalar state.

ApiCompat removes eleven missing-member suppressions. Measured debt falls from 12 missing types, 123 missing members, 15 other diagnostics, and 150 total to 12 missing types, 112 missing members, 15 other diagnostics, and 139 total, with no new incompatibility or stale suppression.
