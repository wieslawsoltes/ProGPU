# System.Drawing Linear-Gradient Contract and Backend Applicability

Date: 2026-08-21

## Contract sources

This slice is a clean-room implementation based on public Microsoft documentation, the pinned reference assembly, and public runtime observations. Framework implementation source and private implementation details were not used.

- [LinearGradientBrush class](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.lineargradientbrush?view=windowsdesktop-10.0)
- [LinearGradientBrush constructors](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.lineargradientbrush.-ctor?view=windowsdesktop-10.0)
- [GDI+ rectangle/angle constructor contract](https://learn.microsoft.com/windows/win32/api/gdiplusbrush/nf-gdiplusbrush-lineargradientbrush-lineargradientbrush(constrectf__constcolor__constcolor__real_bool))
- [Blend class](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.blend?view=windowsdesktop-10.0)
- [ColorBlend class](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.colorblend?view=windowsdesktop-10.0)
- [LinearGradientBrush.Transform](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.lineargradientbrush.transform?view=windowsdesktop-10.0)
- the pinned .NET 10.0.11 `System.Drawing.Common` reference assembly used by ApiCompat

A public .NET 10 runtime probe established observable container behavior: default `Blend` and `ColorBlend` arrays have one element; a zero count creates empty arrays; a negative count throws `OverflowException`; and the array properties retain and return the caller's array instance. Brush setters and getters are separate ownership boundaries and are tested as defensive snapshots.

For rectangle/angle construction, the GDI+ public contract defines the scalable direction angle by `tan(beta) = (width / height) * tan(phi)`. ProGPU uses the quadrant-preserving vector equivalent `(height * cos(phi), width * sin(phi))`, normalized before finding the two rectangle support lines. Non-scalable construction preserves the supplied direction angle.

## Managed implementation

The managed brush holds official linear colors, optional factor or interpolation-color state, rectangle, wrap mode, gamma mode, and affine transform. It validates ordered endpoint positions, snapshots state at the brush boundary, clones independently, and lowers custom color or factor blends into exact typed `ProGPU.Vector.GradientStop` arrays. Triangular and sampled bell falloffs produce renderable stops instead of API-only state.

Wrap modes lower to repeat, reflect, or pad. Gamma correction selects ProGPU's linear-scRGB interpolation mode. The inverse local transform is supplied as the renderer coordinate transform, preserving the established typed transform convention without reflection or untyped compatibility objects.

## Managed/native and renderer applicability

This slice changes managed stop generation and existing `ProGPU.Vector.LinearGradientBrush` properties only. It does not modify:

- the native command wire or C++ command structures;
- shaders, bind-group layouts, texture formats, or GPU resource lifetime;
- Svg.Skia input contracts; or
- windowing, text shaping, image codecs, or path tessellation.

No native backend fork is required. Focused tests cover public ownership, scalable angles, validation, clone isolation, falloff lowering, transform/spread/gamma mapping, disposal, and bounded eight-stop allocation. Repository renderer/headless and Svg.Skia suites remain the end-to-end parity gates.
