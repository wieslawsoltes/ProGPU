# System.Drawing Matrix Contract and Backend Applicability

Date: 2026-08-21

## Contract sources

This slice is a clean-room implementation based on public contracts and affine-math documentation, not on framework implementation source:

- [Matrix class](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.matrix?view=windowsdesktop-10.0)
- [Matrix constructors](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.matrix.-ctor?view=windowsdesktop-10.0)
- [MatrixOrder](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.matrixorder?view=windowsdesktop-10.0)
- [TransformPoints](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.matrix.transformpoints?view=windowsdesktop-10.0)
- [TransformVectors](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.matrix.transformvectors?view=windowsdesktop-10.0)
- [Matrix supplemental remarks](https://learn.microsoft.com/dotnet/fundamentals/runtime-libraries/system-drawing-drawing2d-matrix)
- the pinned .NET 10.0.11 `System.Drawing.Common` reference assembly used by ApiCompat

The public contract is a sealed, `MarshalByRefObject`-derived disposable affine matrix. Rectangle constructors map the source rectangle's upper-left, upper-right, and lower-left corners to three parallelogram points. `Prepend` applies a new operation before the stored operation; `Append` applies it after. Point transforms include translation, while vector transforms omit it. Array and `ReadOnlySpan<T>` overloads update the supplied backing storage in place.

## Managed implementation

ProGPU stores the six affine elements in `System.Numerics.Matrix3x2`. Parallelogram construction solves the two source axes independently. Composition, rotation about a pivot, shear, inversion, point/vector transforms, cloning, value equality, disposal, and validation are implemented without a GPU device or native graphics handle.

The existing typed `Value` bridge remains as a ProGPU extension, while the official `MatrixElements` property is now the public compatibility surface. No runtime reflection, private-field scan, or untyped transform probe is involved.

## Managed/native and renderer applicability

This change does not modify:

- the ProGPU native command wire or C++ structures;
- shader source, bind-group layout, texture format, or GPU resource lifetime;
- path tessellation, text shaping, or Svg.Skia lowering; or
- the renderer's `Matrix3x2`/`Matrix4x4` conventions.

Existing drawing consumers already lower the typed matrix value into the shared renderer transform. The new code fills managed API and composition behavior around that same value. Therefore no native backend fork is required. Renderer/headless and Svg.Skia parity remain repository-level regression gates; focused tests additionally prove parallelogram mapping, append/prepend order, pivot rotation, inverse round trips, span mutation, vector translation exclusion, disposal, and zero-allocation warmed point batches.
