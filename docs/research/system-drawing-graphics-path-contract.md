# System.Drawing `GraphicsPath` contract notes

## Scope and provenance

This implementation is clean-room work based on the public .NET 10.0.11 reference assembly, Microsoft Learn API documentation, observable public behavior, and published curve mathematics. No third-party implementation source was consulted or copied.

Primary public contracts:

- [`GraphicsPath`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath?view=windowsdesktop-10.0)
- [`PathData`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.pathdata?view=windowsdesktop-10.0)
- [`PathPointType`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.pathpointtype?view=windowsdesktop-10.0)
- [`GraphicsPath.Flatten`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.flatten?view=windowsdesktop-10.0)
- [`GraphicsPath.AddString`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.addstring?view=windowsdesktop-10.0)
- [`GraphicsPath.IsOutlineVisible`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.isoutlinevisible?view=windowsdesktop-10.0)
- [`GraphicsPath.Widen`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.widen?view=windowsdesktop-10.0)
- [`GraphicsPath.Warp`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.warp?view=windowsdesktop-10.0)
- [`WarpMode`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.warpmode?view=windowsdesktop-10.0)
- [`GraphicsPath.AddCurve`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspath.addcurve?view=windowsdesktop-10.0)
- [`GraphicsPathIterator`](https://learn.microsoft.com/dotnet/api/system.drawing.drawing2d.graphicspathiterator?view=windowsdesktop-10.0)
- [SVG elliptical-arc implementation notes](https://www.w3.org/TR/SVG/implnote.html#ArcImplementationNotes)

The pinned contract used by ApiCompat is `Microsoft.WindowsDesktop.App.Ref` 10.0.11.

## Implemented behavior

`GraphicsPath` is a sealed `MarshalByRefObject` implementing `ICloneable` and `IDisposable`. The public point/type constructors and span overloads lower directly into ProGPU's retained `PathGeometry`; no reflection, native GDI+ handle, or compatibility-shaped proxy is involved.

The slice includes:

- line, cubic Bézier, cardinal spline, closed spline, rectangle, polygon, ellipse, arc, pie, and rounded-rectangle construction;
- deep clone and `AddPath` composition;
- path markers, closing all figures, reset semantics, and public `PathData`/`PathPointType` export;
- allocation-free span export after caller storage has been allocated;
- analytic bounds, matrix transforms, retained fill hit-testing, path reversal, and adaptive flattening;
- retained stroke expansion and outline hit-testing with width-floor, matrix, flatness, cap, join, miter-limit, and dash semantics;
- three- and four-point perspective or bilinear path deformation with matrix and flatness semantics;
- shaped point/rectangle text outlines with fallback, bidi, wrapping, alignment, vertical direction, and font decorations;
- SVG elliptical arcs exported as cubic Bézier spans of at most 90 degrees, retaining the canonical 13-point representation for a complete ellipse.

`GraphicsPathIterator` snapshots the public point/type representation at construction and maintains independent marker, subpath, and path-type cursors. `Rewind` resets all three cursors. Array/ref overloads allocate destination storage only when necessary, while span enumeration and range copies use caller-owned storage without allocation. Ranges copied into another `GraphicsPath` are reconstructed over typed managed geometry; when a marker starts inside a Bézier segment, its preceding anchor is included so the copied geometry remains valid while the method's returned source-range count is preserved.

Cardinal splines use the standard cubic conversion where each tangent is scaled by `tension / 3`. Cubic flattening uses bounded adaptive De Casteljau subdivision and the requested flatness. Arc flattening derives an angular tolerance from radius and flatness.

`Widen` transforms and adaptively flattens the centerline before calling the renderer-neutral `ProGPU.Vector.StrokePathGeometry` service. The service emits nonzero-filled retained quads and join/cap triangles, normalizes winding, and supports scaled even or odd dash patterns without a GDI+ handle. Widths below one use the observable one-unit hairline floor. `IsOutlineVisible` uses the identical triangle stream with an early-exit point sink; line-only paths avoid flattening allocations, while curves preserve the public default flatness. The optional `Graphics` argument does not change path-space geometry, matching observable public behavior.

The stroke service is shared typed vector infrastructure rather than compositor code. It reuses `DashPattern`, `StrokeJoinGeometry`, and `StrokeCapGeometry`, so System.Drawing and future retained consumers use the same cap/join/dash mathematics without renderer reflection or backend coupling.

`Warp` applies the optional matrix and adaptively flattens source curves before calling the renderer-neutral `ProGPU.Vector.PathWarpGeometry` service. Three destination points imply a parallelogram; four points define the complete quadrilateral. Perspective mode uses a projective homography and therefore retains exact straight segments. Bilinear mode blends the four corners and adaptively subdivides diagonals until their mapped midpoint is within the requested flatness of the retained chord. Figure closure, fill rule, fill/stroke flags, and cap metadata are preserved; public path markers are cleared when topology is replaced. The service accepts only typed, line-only `PathGeometry` input and has no GDI+ handle, renderer reflection, or backend dependency.

`AddString` constructs a `ProGPU.Text.TextLayout` over the style-matched `TtfFont`, so OpenType shaping, clusters, ligatures, bidi order, line wrapping, and typed fallback faces are shared with normal ProGPU text. `ProGPU.Text.TextOutlineGeometry` then transforms the immutable cached TrueType or CFF glyph outlines from design units to each shaped baseline and appends them as retained path figures. Point and rectangle overloads share the same path; rectangle layout applies horizontal/vertical alignment, wrapping, RTL, and vertical shaping flags. Underline and strikeout figures use the font's OpenType metrics, with bounded fallbacks for fonts that omit them. Italic faces are selected through `FontManager`, with a managed shear only when the matched face remains upright. The operation does not allocate an atlas, initialize a GPU device, call GDI+, or translate through `SKPath`.

Exact partial-glyph clipping, ellipsis/path trimming, tab-stop expansion, digit substitution, and the `NoFontFallback` override remain broader `StringFormat`/text-layout behavior debt. The outline slice does not invoke the GPU path-boolean solver merely to imitate clipping; those contracts require CPU-available typed layout/clip support and focused parity fixtures before they can be claimed.

## Managed/native applicability audit

This change is managed-only. It exposes and manipulates the existing typed `ProGPU.Vector.PathGeometry` model already consumed by the managed and native rendering paths. Iterator snapshots are managed point/type arrays and introduce no renderer-facing representation. The slice does not alter retained-scene wire formats, shaders, native renderer contracts, or backend selection. Existing renderer, native-build, headless-pixel, and SVG parity gates remain applicable and must stay green.
