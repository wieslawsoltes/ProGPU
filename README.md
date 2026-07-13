# ProGPU Substrate Framework

ProGPU is a high-performance, GPU-first UI framework and composition substrate for .NET, built on top of Silk.NET and WebGPU (wgpu-native). It provides a lightweight, low-allocation alternative to traditional heavyweight UI frameworks by routing all vector graphics, text layout, and composition operations directly to the GPU using native WebGPU draw pipelines.

## NuGet Packages

ProGPU release packages are built from `eng/progpu-package-list.sh` by the `Release` GitHub Actions workflow. Samples, tests, diagnostics, and framework shim projects are intentionally not packed.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `ProGPU.Backend` | WebGPU device, swapchain, Silk.NET windowing, and platform backend services. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Backend.svg)](https://www.nuget.org/packages/ProGPU.Backend/) |
| `ProGPU.DirectX` | DirectX-compatible facade and shader-oriented API surface implemented on ProGPU/WebGPU. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.DirectX.svg)](https://www.nuget.org/packages/ProGPU.DirectX/) |
| `ProGPU.Transpiler` | Shader/source transformation helpers used by generated GPU pipelines. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Transpiler.svg)](https://www.nuget.org/packages/ProGPU.Transpiler/) |
| `ProGPU.Compute` | Compute pipeline helpers for GPU-side effects, acceleration, and future hit-test indexes. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Compute.svg)](https://www.nuget.org/packages/ProGPU.Compute/) |
| `ProGPU.Vector` | Vector primitives, paths, geometry, brushes, pens, and rasterization data models. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Vector.svg)](https://www.nuget.org/packages/ProGPU.Vector/) |
| `ProGPU.Text` | Text layout, glyph metrics, and GPU-ready text rendering helpers. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Text.svg)](https://www.nuget.org/packages/ProGPU.Text/) |
| `ProGPU.Scene` | Scene graph, compositor commands, retained visuals, effects, and presentation primitives. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Scene.svg)](https://www.nuget.org/packages/ProGPU.Scene/) |
| `ProGPU.Layout` | Measure/arrange layout substrate shared by higher-level UI adapters. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Layout.svg)](https://www.nuget.org/packages/ProGPU.Layout/) |
| `ProGPU.Virtualization` | Virtualization helpers for large retained visual and item surfaces. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Virtualization.svg)](https://www.nuget.org/packages/ProGPU.Virtualization/) |
| `ProGPU.WinUI` | WinUI-shaped controls and app model implemented on ProGPU. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.WinUI.svg)](https://www.nuget.org/packages/ProGPU.WinUI/) |
| `ProGPU.WinUI.Charts` | Chart controls and chart rendering primitives for the WinUI-shaped layer. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.WinUI.Charts.svg)](https://www.nuget.org/packages/ProGPU.WinUI.Charts/) |
| `ProGPU.WinUI.Designer` | Designer/editor controls and diagnostics for ProGPU WinUI surfaces. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.WinUI.Designer.svg)](https://www.nuget.org/packages/ProGPU.WinUI.Designer/) |
| `ProGPU.Avalonia` | Avalonia integration and compositor backend adapter. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Avalonia.svg)](https://www.nuget.org/packages/ProGPU.Avalonia/) |
| `ProGPU.Uno` | Uno/WinUI integration and compositor backend adapter. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Uno.svg)](https://www.nuget.org/packages/ProGPU.Uno/) |
| `ProGPU.Dxf` | DXF import/rendering support for ProGPU vector scenes. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Dxf.svg)](https://www.nuget.org/packages/ProGPU.Dxf/) |
| `ProGPU.SkiaSharp` | ProGPU-backed portable SkiaSharp compatibility shim used by drawing and imaging adapters. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.SkiaSharp.svg)](https://www.nuget.org/packages/ProGPU.SkiaSharp/) |
| `ProGPU.System.Drawing.Common` | ProGPU-backed portable System.Drawing.Common compatibility shim for LibreWinForms and GDI-style callers. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.System.Drawing.Common.svg)](https://www.nuget.org/packages/ProGPU.System.Drawing.Common/) |
| `LibreWPF.Interop` | LibreWPF portable interop contracts consumed by the ProGPU/Silk.NET SDK lane. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWPF.Interop.svg)](https://www.nuget.org/packages/LibreWPF.Interop/) |

Local package build:

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.10 ./eng/progpu-pack.sh
```

Local publishing reads the API key from `NUGET_API_KEY` without storing it in the repository:

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.10 ./eng/progpu-publish.sh
```

The release workflow validates docs, restores, builds, tests, packs `.nupkg`/`.snupkg` artifacts, and can publish to NuGet.org when `NUGET_API_KEY` is configured. See [docs/release.md](docs/release.md).

## Projects Using ProGPU

### [LibreWPF](https://github.com/wieslawsoltes/wpf)

LibreWPF ports the managed WPF runtime and SDK to the ProGPU/Silk.NET platform. Applications can switch to the custom SDK while retaining familiar WPF source, XAML, controls, and Windows-shaped APIs on supported non-Windows hosts.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `LibreWPF.Sdk` | MSBuild SDK that redirects WPF applications to the portable ProGPU/Silk.NET platform. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWPF.Sdk.svg)](https://www.nuget.org/packages/LibreWPF.Sdk/) |
| `LibreWPF.ProGPU` | WPF host, retained/source replay bridge, input integration, and ProGPU compositor adapter. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWPF.ProGPU.svg)](https://www.nuget.org/packages/LibreWPF.ProGPU/) |
| `LibreWPF.Transport` | Managed WPF assemblies, reference assemblies, themes, XAML build tasks, and runtime metadata. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWPF.Transport.svg)](https://www.nuget.org/packages/LibreWPF.Transport/) |

### [LibreWinForms](https://github.com/wieslawsoltes/winforms)

LibreWinForms provides portable WinForms-shaped APIs hosted by the ProGPU/LibreWPF stack. It preserves the common `System.Windows.Forms` development model while replacing native GDI rendering with the ProGPU-backed compatibility layer.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `LibreWinForms.Sdk` | MSBuild SDK that configures applications for the portable LibreWinForms package set. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWinForms.Sdk.svg)](https://www.nuget.org/packages/LibreWinForms.Sdk/) |
| `LibreWinForms.System.Windows.Forms` | Portable `System.Windows.Forms` API and runtime surface. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWinForms.System.Windows.Forms.svg)](https://www.nuget.org/packages/LibreWinForms.System.Windows.Forms/) |
| `LibreWinForms.WindowsFormsIntegration` | Portable bridge for hosting WinForms content in LibreWPF applications. | [![NuGet](https://img.shields.io/nuget/vpre/LibreWinForms.WindowsFormsIntegration.svg)](https://www.nuget.org/packages/LibreWinForms.WindowsFormsIntegration/) |

### [Avalonia ProGPU Backend](https://github.com/wieslawsoltes/Avalonia/tree/feature/progpu)

The Avalonia ProGPU backend replaces the Skia renderer with a GPU-first WebGPU implementation while preserving Avalonia's rendering contracts. It also exposes an API lease for issuing custom ProGPU vector operations and WebGPU shaders inside an Avalonia frame.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `ProGPU.Avalonia.Rendering` | ProGPU, Silk.NET, and WebGPU rendering platform for Avalonia. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Avalonia.Rendering.svg)](https://www.nuget.org/packages/ProGPU.Avalonia.Rendering/) |

### [Silk.NET Avalonia Backend](https://github.com/wieslawsoltes/Avalonia/tree/feature/progpu)

The Silk.NET Avalonia backend supplies cross-platform desktop windowing, input, surfaces, and WebGPU integration. It is designed to pair with the ProGPU renderer but can host another compatible Avalonia renderer.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `ProGPU.Avalonia.SilkNet` | Cross-platform Silk.NET windowing platform for Avalonia. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.Avalonia.SilkNet.svg)](https://www.nuget.org/packages/ProGPU.Avalonia.SilkNet/) |

### [SkiaSharp Compatibility Shim](https://github.com/wieslawsoltes/ProGPU/tree/main/src/SkiaSharp)

The SkiaSharp compatibility shim implements a portable SkiaSharp-shaped API over ProGPU vector, text, imaging, path operations, and compositing primitives. It lets SkiaSharp-oriented libraries target the ProGPU renderer without loading native Skia binaries.

Encoded images are decoded on the CPU before their pixels enter the GPU texture path. PNG-backed ICO/CUR frames use the common image decoder; bitmap-backed frames support legacy core and modern information headers, indexed 1/4/8-bit color, RLE4/RLE8 streams, RGB555, RGB565 and other validated 16/32-bit channel masks, 24-bit BGR, 32-bit BGRA, and Windows AND-mask transparency. Keeping decode and upload separate preserves deterministic pixel semantics and avoids initializing a WebGPU device for metadata or bitmap-only workflows.

CPU bitmap resizing follows Skia's pixel-center mapping and alpha representation. Nearest and linear modes use fixed one- and four-sample footprints; cubic mode evaluates the requested Mitchell-Netravali B/C kernel over a fixed 4x4 footprint, preserving the distinction between Mitchell and Catmull-Rom. Source rows are read with their actual stride and color order, and output is converted to RGBA8888, BGRA8888, RGB888x, or RGB565 without forcing a WebGPU upload. Nearest sampling is a direct `O(destination pixels)` pass with no source-sized temporary allocation, and same-format texels stay as raw copies. Linear and cubic sampling normalize source pixels once, then run in `O(source pixels + destination pixels)` and `O(source pixels + 16 * destination pixels)` time with `O(source pixels)` temporary storage.

`SKPathBuilder` implements the complete SkiaSharp 4.148 builder surface directly over retained `PathGeometry`. Snapshot and transformed/offset path insertion clone `N` segments in `O(N)` time and storage, while detach transfers the current path and restores an empty winding builder in `O(1)`. Relative commands preserve Skia's current point after close; extend mode merges the first source contour, and reverse insertion reverses contour order, segment order, cubic controls, and arc sweep without flattening. Endpoint, oval, round-rectangle, circle, and tangent arcs remain analytic `ArcSegment` values. A rational conic is represented by 16 midpoint-matched quadratic spans, giving bounded `O(1)` work and storage per conic with a sampled error below `0.001`; native rectangle and round-rectangle direction/start-index rules retain their original contour topology. Primitive construction and metadata-only builder use never initialize WebGPU.

Legacy `SKPath` exposes the complete SkiaSharp 4.148 path, primitive-query, conversion, iterator, and operation-builder surface while retaining ProGPU's public `PathGeometry` bridge. Raw and force-close iterators construct a CPU operation view in `O(N + Q)` time and storage for `N` retained segments and `Q` emitted quarter arcs. Consecutive compatible analytic arcs are coalesced before that view is split at exact 90-degree boundaries, so a retained two-segment oval reports Skia's four rational conics and a 200-degree arc reports 90/90/20-degree conics without increasing render-time geometry. Rational conics keep one internal marker over their 16 midpoint-matched render spans, preserving one conic verb and its weight across clone, transform, offset, extend, and reversal. Point, verb, segment-mask, convexity, and primitive recognition are `O(N)` CPU queries; recursive `ConvertConicToQuads` with subdivision power `p` uses `O(2^p)` time and output storage. Path construction, topology queries, SVG serialization, iterator creation, and fill-rule conversion do not initialize WebGPU.

`SKRoundRect` keeps normalized bounds, four corner radii, and the derived empty/rectangle/oval/simple/nine-patch/complex classification as retained CPU geometry. Construction applies Skia's CSS corner-overlap rule: it finds the minimum side-to-radius ratio in double precision, scales every radius by that common factor, and makes bounded ULP corrections so adjacent float radii never exceed a side. Containment evaluates at most four ellipse equations. Axis-preserving transforms map the eight tangent points and four corner controls, then reconstruct radii from the mapped contour; this preserves corner ordering, mirrors, quarter turns, and native floating-point subtraction while rejecting skew, arbitrary rotation, and perspective. Setters, classification, validation, containment, inset/outset, offset, and transform use fixed `O(1)` work and storage without WebGPU initialization. The public `Radii` ownership boundary returns one four-point copy, while renderer access, `GetRadii`, classification, validation, and transform use the retained array or stack spans and allocate nothing.

`SKPoint`, `SKSize`, `SKSizeI`, `SKRect`, and `SKRectI` implement the complete SkiaSharp 4.148 value surfaces as CPU-only `O(1)` operations with no heap or GPU state. Point normalization, distance, reflection, arithmetic, mutation, equality, and formatting preserve native float behavior, including NaN normalization of the zero vector. Size conversion uses checked truncation, and integer rectangle floor/ceiling modes round each edge inward or outward according to contour direction. Float and integer rectangles retain SkiaSharp's half-open point containment, inclusive versus strict intersection distinction, degenerate edge intersections, standardized coordinates, centered aspect fit/fill, and exact empty/union semantics. Point-based `SKCanvas.DrawLine` and `DrawCircle` overloads add no adaptation allocation or duplicate rendering path; they forward directly to the established scalar implementations.

`SKColor` and `SKColorF` are immutable CPU value types with the complete SkiaSharp 4.148 packed-channel, parsing, mutation-copy, equality, clamp, HSL, and HSV surfaces. Conversion uses the native `0.001` chroma epsilon, the precomputed float `1 / 255` byte scale, and clamped nearest-byte rounding, preserving native one-ULP float results and packed ARGB output. Every operation has fixed `O(1)` time and storage, allocates no render resources, and does not initialize WebGPU. `SKColors.Empty` aliases the packed zero value while the named color table remains static immutable data.

`SKCubicResampler` and `SKSamplingOptions` are immutable `O(1)` sampling descriptors. Filter, mipmap, cubic, and anisotropic constructors initialize only their native mode fields; `default` keeps anisotropy disabled at zero, while the integer constructor clamps its maximum to at least one. Mitchell, Catmull-Rom, and custom B/C coefficients retain exact value identity and flow through the existing renderer without a second translation or allocation. Descriptor creation performs no image sampling, texture upload, shader compilation, or WebGPU initialization.

Color-space transfer functions retain Skia's named sRGB, gamma 2.2, linear, Rec.2020, PQ, and HLG parameter sets. Scalar evaluation reproduces skcms's bounded `O(1)` bit-level log2/exp2 approximation, signed sRGB-like branch, and dedicated HDR equations; inversion preserves the native continuity check and returns empty for non-invertible PQ/HLG markers. XYZ matrices keep nine floats inline rather than an array reference. Equality, indexing, inversion, and concatenation are allocation-free `O(1)` operations; inversion uses Skia's double-precision cofactor path and concatenation uses row-major `A * B` order. Only the public `Values` ownership boundary allocates or copies its fixed nine elements. These CPU operations do not initialize WebGPU.

Font outline APIs preserve Skia's separation between glyph-local geometry and placement metrics. `GetGlyphPath` and `GetTextPath` materialize the font transform as `x' = x * ScaleX + y * SkewX`; horizontal advances use `ScaleX` but not `SkewX`. Path fallbacks, text-blob intercepts, and the canonical 64-unit `GetGlyphPaths` callback therefore consume already transformed outlines and never apply shear twice. Retained fill-text recording bypasses those path APIs and carries `ScaleX` and `SkewX` into a glyph run without rescaling its already-shaped positions. The compositor applies that transform only to glyph-local geometry: vector outlines reuse transform-aware path-cache entries, while atlas text reuses the existing constant-cost vertex shear and scaled quad bounds. `Embolden` follows Skia's `Size / 32` mitered stroke-and-fill rule; generated stroke contours are normalized to the glyph's dominant winding before they are appended, so overlapping segments cannot erase original coverage. Emboldened blobs record that widened path once, while ordinary fonts retain the unchanged glyph-run fast path.

`SKFont` exposes the complete SkiaSharp 4.148 text/glyph surface for UTF-8, native-endian UTF-16, native-endian UTF-32, and glyph-ID buffers. Encoded APIs first validate and count the complete run, then perform a second allocation-free pass to map glyphs, measure, or write caller spans. This preserves one glyph per Unicode scalar, rejects a malformed encoded run without partial output, and lets `BreakText` report consumed UTF-16 code units or encoded bytes exactly as Skia does. Span-returning work is `O(N)` time and `O(1)` auxiliary space for `N` encoded units; array-returning overloads allocate one exact `O(G)` result for `G` glyphs. Text-on-path construction caches repeated glyph outlines and morphs each line, quadratic, conic, and cubic point through a high-resolution contour measure. For `P` baseline samples and `S` glyph path segments, setup is `O(P)`, morphing is `O(G * S * log P)`, and cached glyph-path storage is `O(U * S)` for `U` unique glyphs in the call.

Legacy `SKPaint` text APIs are compatibility adapters over one paint-owned `SKFont` snapshot. Constructing from a font copies its typeface, metrics, scale, skew, hinting, and emboldening state; cloning and `ToFont` also return independent snapshots. `IsAntialias` and `LcdRenderText` derive alias, grayscale-antialias, or subpixel font edging exactly as Skia does. Every string, span, encoded byte, pointer, glyph, measurement, break, position, width, path, and intercept overload delegates to the validated `SKFont`/`SKTextBlob` engines instead of maintaining another decoder or glyph loop. State reads and copies are `O(1)`; encoded adapters retain `O(N)` time and `O(1)` auxiliary parsing space, array-returning APIs allocate `O(G)`, and intercept adapters retain `O(G * S)` path analysis for `G` glyphs with `S` segments. No legacy paint text operation initializes WebGPU.

`SKPaint.GetFillPath` exposes the complete path, builder, cull-hint, resolution-scale, and matrix overload family over one CPU stroker. Fill-only conversion preserves the source fill rule; stroke and stroke-and-fill results use winding coverage. Cull rectangles remain optimization hints and never clip output, while scalar and matrix overloads choose a bounded curve schedule `Q = clamp(ceil(24 * sqrt(max(1, resScale))), 24, 192)`. For `S` retained segments, conversion is `O(S * Q)` time and output storage. Successful calls transactionally replace either destination, failed legacy path calls preserve their destination, and failed builder calls expose the native source-snapshot fallback. Source/destination aliasing is safe. A positive-width stroke succeeds even when an empty path or butt-capped degenerate contour produces no coverage; only a zero-width device hairline has no device-independent fill path. No overload initializes WebGPU.

`SKBlender` and `SKPaint.Blender` preserve Skia's shared paint state in `O(1)`: mode factories validate every `SKBlendMode`, explicit `SrcOver` paint assignment uses the native null fast path, custom blender assignment survives cloning, and reset clears it. Built-in blenders reuse the retained compositor blend scope. `Modulate` is a fixed-function GPU blend with `C = Cs * Cd` and `A = As * Ad`; no destination copy or custom shader is needed. Arithmetic factories retain finite coefficients and premultiplication policy, but drawing currently throws before recording because the required general destination-sampling compositor path is not yet implemented. This explicit boundary prevents a custom arithmetic formula from silently rendering as `SrcOver`.

`SKShader` exposes the complete SkiaSharp 4.148 factory surface as an immutable retained `SKObject` graph. Color, gradient, sweep, noise, picture, and image leaves snapshot caller state; `WithLocalMatrix` and `WithColorFilter` return new nodes and keep their source alive after public disposal. Nested local matrices retain Skia's parent-before-child order, while nested filters execute child-first. Gradient colors and positions are converted once at factory time, so later caller mutation cannot alter output and repeated brush creation only copies the compact stop transport. Up to four nested color filters are carried through drawing in inline state without a heap allocation. Ordinary leaves and source-over conical composition stay on the direct vector or tiled-texture path. General blend-mode and arithmetic composition evaluates destination and source over device coverage, combines them with the existing image-blend compute pipeline, applies filters, and clips target geometry exactly once; this preserves Porter-Duff alpha and antialiased edge coverage instead of blending already-clipped children. A composed full-frame shader uses `O(P)` texel work and temporary storage for `P` device pixels, and `F` post-filters add `O(F * P)` work without CPU readback. Sweep gradients retain arbitrary finite start/end angles in picture archives and map angle to `t` in `O(1)` fragment work before clamp, repeat, mirror, or decal handling; equal-angle repeat/mirror resolves once to the average color and equal-angle decal resolves to empty coverage.

`SKImageFilter` is a SkiaSharp-shaped `SKObject` whose complete SkiaSharp 4.148 factory surface builds an immutable retained DAG without touching WebGPU. Blur, shadow, color, arithmetic and blender composites, morphology, displacement, convolution, merge, offset, image, shader, picture, lighting, tile, compose, matrix-transform, and magnifier overloads normalize into shared node representations. Null graph inputs retain Skia's source-graphic fallback, blur defaults to Decal tiling, default image sampling is Mitchell, kernels and merge spans are copied once, and crop rectangles remain node metadata until evaluation. Ordinary node construction is `O(1)` time/storage; a `K`-coefficient convolution and `N`-input merge use `O(K)` and `O(N)` time/storage. Layer evaluation memoizes by `(source texture, node, color-space policy)`, so shared nodes remain `O(V + E)` per source context and compose can safely rebind the outer graph's source to the inner result. Matrix filters conjugate the local matrix through the layer transform and reuse the sampled texture render path. Magnifier crops and fits its child input first, restricts output to the lens/child intersection, lazily creates one compute pipeline, and applies Skia's squared inset-distance lens weighting with circular `2 * inset` corner transitions; nearest, bilinear, and bicubic modes use one, four, and sixteen reads per output texel with `O(1)` local storage. Built-in and arithmetic `SKBlender` image filters reuse the existing blend and arithmetic compute passes rather than silently changing formulas.

`SKTypeface` exposes glyph counts, PostScript names, fixed-pitch metadata, and exact SFNT table access directly from the parsed font. Table tags preserve directory order; table payloads remain borrowed `ReadOnlyMemory<byte>` internally and are copied only when the SkiaSharp API transfers ownership to a caller. Metadata queries are `O(1)`, tag enumeration is `O(T)` time and storage for `T` tables, and a table read is `O(B)` for `B` copied bytes. Legacy glyph APIs delegate to the same validated `SKFont` encoding engine instead of maintaining a second character-map implementation. `SKTypeface.Empty` short-circuits glyph, metric, and table surfaces without initializing any rendering pipeline. Variable-font clone and design-position APIs remain intentionally absent until the parsed `fvar` axes can be applied to outlines and metrics rather than reported as unsupported no-ops.

`SKPath` metadata is derived directly from the retained figure/segment representation. Point and verb enumeration preserves Skia's move/line/quad/conic/cubic contract, rectangle queries recover closure and winding, oval and rounded-rectangle queries recover their original bounds and corner radii, and linear contour convexity uses consistent cross-product signs. Retained elliptical arcs stay analytic for rendering quality; metadata enumeration alone splits an arc into rational conics spanning at most 90 degrees, including a tolerance at exact quadrant boundaries so a native half-oval remains two conics. Metadata scans are `O(S)` time and `O(1)` auxiliary storage for `S` retained segments. Caller-owned point snapshots are `O(P)` storage for `P` returned points, while single-point lookup remains `O(S)` without initializing WebGPU or flattening the rendered path.

Path iteration exposes Skia's nested raw and cooked iterator identities. Iterator construction snapshots `O(V)` compact operations for `V` path verbs, after expanding each analytic arc to at most four rational-conic operations; each `Next`, `Peek`, and state query is then `O(1)`. Raw iteration preserves untouched output slots and exact conic weights, while cooked iteration inserts one closing line and close verb for closed or force-closed contours. Explicit conic identity and weight use weak segment metadata that follows copies, path appends, and in-place transforms without extending segment lifetime. Cardinal trigonometric values are snapped only within epsilon of `-1`, `0`, or `1`, matching Skia's exact oval controls without changing general rotated arcs. Iteration is CPU-only and never flattens arcs to line lists, initializes WebGPU, submits rendering, or reads pixels.

Relative path commands resolve one current point per verb, including the origin for an empty path and the contour start after `Close`; control points and endpoints are all offset from that same point. In-place offset and transform preserve the active contour and conic metadata, so subsequent commands append without an artificial move. Destination transforms replace the target transactionally from a transformed deep copy, preserve source fill/conic state, and special-case source/target aliasing to transform exactly once. Relative commands are allocation-free `O(1)` mutations; offset and in-place transform are `O(S)` for `S` segments; destination transform is `O(S)` time and storage. `Rewind` follows native Skia by clearing geometry and restoring winding fill.

Rational-conic conversion emits `2^P` ordinary quadratics and `2^(P+1)+1` points for subdivision exponent `P`. Each half split normalizes the two child conics and updates their shared weight to `sqrt((1+w)/2)`, matching Skia's deeper control points rather than recursively interpolating unnormalized homogeneous triples. Conversion is `O(2^P)` time/output storage with `O(P)` recursion depth; managed overloads validate exponent and destination capacity so malformed requests cannot reproduce native buffer overruns. SVG path serialization walks the cooked iterator in `O(V)` time and `O(V)` output for `V` verbs, preserves invariant round-trip float formatting, expands every conic to Skia's fixed 32 ordinary quadratic commands so its rational shape survives SVG, and includes the cooked closing line before `Z`. Both operations are CPU-only.

`SKPathBuilder` owns one retained path and forwards mutations directly into analytic line, quadratic, conic, cubic, arc, and shape storage. Snapshot copies `O(S)` retained segments, detach transfers the current path in `O(1)` and installs an empty winding path, while reset and fill state follow native ownership semantics. `SKPathMeasure` walks raw verbs once and builds a cumulative contour table with `Q = min(64, 1 + ceil(4 * sqrt(resScale)))` samples per curve, matching Skia's sublinear precision growth instead of multiplying every text-on-path curve by a large linear factor. Setup is `O(S * Q)` time/storage for `S` segments; position, tangent, and matrix queries use `O(log(S * Q))` distance lookup followed by exact analytic evaluation. Segment extraction retains original verb kinds: ordinary curves use De Casteljau slicing and rational conics use homogeneous slicing with normalized endpoint weights. This preserves output quality while avoiding per-query flattening, GPU initialization, submission, or readback.

Degree-based oval arcs remain analytic retained segments and expand only through the shared rational-conic iterator. Partial sweeps preserve sign and start angle; `AddArc` maps a full sweep to two half arcs plus close, while rectangle `ArcTo` preserves Skia's distinct full-sweep connector-only behavior. Tangent arcs compute normalized corner vectors, `radius / tan(angle/2)` tangent points, and one signed small circular arc, with degenerate/radius-zero cases reduced to one line. Indexed rectangles rotate four corners; indexed rounded rectangles rotate eight line/arc boundary positions and omit a final straight edge because `Close` supplies it. Consecutive empty moves collapse to the latest move. Reverse append visits contours and segments in reverse order, swaps cubic controls, preserves quadratic/conic controls and weights, and reverses arc sweep. Arc and indexed-shape construction are bounded `O(1)` work; reverse append is `O(S)` time/storage for `S` segments. No construction helper flattens curves or initializes WebGPU.

Path simplify and winding conversion use a CPU planar arrangement for closed linear contours. It computes all segment intersections, splits edges, classifies each fragment by original fill on both sides, discards internal fragments, orients surviving boundaries with fill on the right, deduplicates quantized directed edges, and stitches adjacency-indexed loops. For `E` source edges, `I` intersections, and `F = O(E + I)` fragments, work is `O(E^2 + E*F)` and storage is `O(E + I)`; hash deduplication and contour stitching are expected `O(F)`. Bowties split into faces, overlaps lose interior edges, and even-odd holes reverse for winding fill without GPU work. Curved paths use the existing exact GPU union solver only when a WebGPU context is already active; without one they preserve every analytic curve/conic and normalize only the requested fill rule, never flattening or initializing a device. Result overloads are alias-safe, and the instance boolean-op overload shares the established solver.

`SKRegion` stores canonical, disjoint horizontal rectangle bands. Union, intersection, difference, reverse-difference, XOR, bulk rect assignment, and translation normalize y edges and merged x intervals, so bounds, rectangle/complex classification, containment, and region operations share one coverage model. Normalizing `R` input rectangles costs `O(R^2 log R)` time and `O(R)` storage in the worst case. Arbitrary path assignment clips to another region and samples pixel centers; raw path contours are flattened once with rational conic weights retained, then each pixel performs an allocation-free winding/even-odd scan. For `L` flattened edges and `A` candidate pixels, rasterization is `O(L + A*L)` time and `O(L + R)` storage with no per-pixel geometry allocation or GPU initialization. Rect/clip iterators snapshot `O(R)` bands and advance in `O(1)`; span iterators merge exclusive x-runs in `O(R log R)` setup and `O(1)` per span. Boundary paths preserve canonical coverage through retained rectangles.

Canvas state follows Skia's one-based save-count contract while retaining a zero-based stack internally: `Save` and `SaveLayer` return the prior public count, `RestoreToCount` clamps at one, and `SKAutoCanvasRestore` unwinds all nested state exactly once. Memory, managed, and file-backed streams share cursor, seek, primitive-read, and snapshot behavior. `SKFileStream` reads a file once into immutable memory, so later codec reads are deterministic and `GetMemoryBase` can expose a stable pinned address; opening and reading are `O(B)` for `B` bytes, cursor operations are `O(1)`, and stream snapshots copy `O(B)` owned output.

Canvas clip metadata is cached beside matrix/opacity state rather than reconstructed from retained commands. Axis-aligned rectangle clips use Skia's nearest-device-pixel bounds, arbitrary path clips round outward, and rectangle differences simplify full-edge subtraction while interior holes retain conservative bounds and non-rectangular classification. Device queries are direct `O(1)` reads; local queries outset the device bounds by one raster pixel and inverse-map once through the current matrix in `O(1)`. Save/restore copies the fixed clip state, and `QuickReject` tests these active device bounds without allocating or walking clip history.

Canvas annotations preserve Skia's raster contract: arbitrary, URL, named-destination, and link-destination metadata do not create pixels or retained GPU commands. Data overloads are allocation-free `O(1)` no-ops on raster canvases; string overloads return the caller-owned null-terminated ASCII `SKData` payload required by Skia's annotation API in `O(N)` time and storage for N characters. Document-specific annotation serialization remains isolated from ordinary scene compilation, so SVG raster rendering and annotation-heavy callers pay no GPU or command-list cost.

Advanced `SKCanvasSaveLayerRec` layers reuse the existing offscreen compositor while preserving Skia's record contract. Ordinary layers retain the zero-snapshot path. A backdrop or `InitializeWithPrevious` request copies the pending parent command/buffer state once in `O(C + B)` time/storage for C commands and B pooled values, then renders already-flushed surface pixels and pending commands together into one previous-content texture. Backdrop filters run on that texture before child commands; active clips and layer bounds are replayed once. `F16ColorType` selects an RGBA16F target and matching format-specific compositor, while restore samples the result through the normal texture path. The feature adds a bounded two-pass GPU cost only when previous content is explicitly requested and releases cloned resource leases on restore, failure, or canvas disposal.

Canvas ownership follows Skia's raster/GPU distinction. `Surface` returns the exact owning `SKSurface` while attached; standalone, bitmap, and picture canvases return null. `Context` returns the caller's `GRRecordingContext` for direct GPU surfaces and null for raster semantics. `GRContext` now derives from the Skia-compatible `GRRecordingContext` base and reports the Dawn backend, abandonment state, texture/render-target limits, and sample support without probing or initializing another device. Both canvas properties are identity-preserving allocation-free `O(1)` reads; the retained ProGPU command context remains an internal `DrawingContext` and cannot collide with the public Skia API.

Writable streams expose the complete SkiaSharp primitive, text, packed-integer, managed, file, and dynamic-memory contracts. Fixed-width and packed writes use constant stack storage and `O(1)` work; UTF-8 text writes use `O(N)` time and owned encoding storage for `N` bytes. `WriteStream` copies `L` requested bytes in `O(L)` time with a pooled `O(min(L, 81920))` buffer, and deterministically zero-fills a short source instead of exposing native Skia's uninitialized tail bytes. `SKManagedWStream` leaves its managed stream open by default and transfers disposal only when requested. Dynamic copy and detach operations are `O(B)` time and output storage for `B` buffered bytes; detach atomically resets the writer to zero bytes, while span and pointer copies preserve the source. Invalid file writers remain queryable and reject writes without creating a partial output file.

Document export implements the complete SkiaSharp PDF/XPS creation surface, including path, managed-stream and `SKWStream` ownership, PDF metadata, XPS options, content clipping, close, and abort. Empty and aborted documents preserve native Skia's zero-byte output contract, caller-owned streams remain open, and path overloads own their file writers. Each populated page is captured once and reused by the writer; the raster target and retained command transform both scale by `dpi / 72`, preserving logical page geometry while increasing physical output detail. PDF generation builds a deterministic catalog/page/image graph with escaped metadata, Flate-compressed RGB payloads, and a byte-accurate cross-reference table; XPS generation writes an OPC package with one fixed-page and PNG resource per page. For `P` captured pixels and `B` encoded bytes, either path is `O(P + B)` time and `O(P + B)` owned output storage, while metadata/options and empty-document operations are `O(1)`.

`SKData` uses a reference-counted storage owner for pinned managed bytes or caller-owned external memory. Its `Data` pointer remains stable for the object's lifetime, mutable `Span` and read-only `AsSpan` views are allocation-free, and valid subsets share storage in `O(1)` time and space while remaining alive independently of the parent view. Copy/file/stream factories and `ToArray` remain explicit `O(B)` ownership boundaries for `B` bytes; exact-length stream factories return null on a short read, and an external release delegate runs exactly once after the final shared view is disposed.

`SKBitmap` and `SKPixmap` preserve native metadata-only, owned, and externally installed pixel states. Row-stride-aware bitmap byte counts and spans include inter-row padding but exclude padding after the final logical row; pixmap `BytesSize` remains the packed logical size while its span exposes the complete stride footprint. Allocation, reset, pointer, and immutable-state queries are `O(1)`; zero-initialization and byte snapshots are `O(B)` for the exposed storage size. Installed release delegates run exactly once on replacement, reset, disposal, or finalization, including delegates without a context object.

Pixmap operations stay entirely on CPU memory. Pixel reads normalize premultiplied alpha, read/copy paths convert the common RGBA, BGRA, RGBx, RGB565, ARGB4444, alpha, gray, R8 and RG8 layouts, and erase clips to the destination rectangle. Subset extraction intersects bounds and creates an `O(1)` zero-copy view that retains the original pixel owner. Scaling delegates to the same stride-aware nearest, linear and cubic bitmap kernels, then converts into the destination pixmap. PNG and JPEG encoding convert one straight-alpha RGBA buffer and write directly to managed or Skia streams without creating a texture or WebGPU context; generic WebP and typed WebP entry points currently return false/null until a portable encoder with compatible licensing is available. Pixel queries and view creation are `O(1)`; read/erase are `O(P)` for affected pixels, scaling retains its documented kernel complexity, and encoding is `O(P + B)` time with `O(P + B)` owned storage for `P` pixels and `B` encoded bytes.

Bitmap pixel arrays, color-type copies, shared subsets, alpha extraction, and size/legacy sampling overloads reuse those CPU primitives. Copy conversion builds a complete temporary bitmap and swaps storage only after success, so an invalid format or failed conversion leaves the destination unchanged. Bitmap subsets keep the original allocation alive and share its row stride in `O(1)` time and space. Alpha extraction writes one byte per source pixel into Skia's four-byte-aligned mask rows and reports the zero origin; it is `O(P)` time and `O(P)` storage. Pixel-array get/set and successful color copies are also `O(P)`, while failed format validation remains `O(1)`. Bitmap PNG/JPEG encoding now goes through `PeekPixels` and never performs the previous texture upload/readback.

Bitmap shader creation has the complete Skia overload set. It uploads the current CPU bitmap once into an owned texture, retains independent X/Y tile modes and the local matrix, and carries the requested nearest, linear, mipmapped, or cubic sampling mode into every tiled draw command. Custom cubic shaders preserve their B/C coefficients through `ImageShaderData` and `RenderCommand`, sharing the same coefficient-aware texture shader as direct image draws. Creation is `O(P)` upload time and texture storage for `P` pixels; recording is `O(T)` time and commands for `T` visible tiles, with `O(1)` sampling metadata per command.

Bitmap decoding routes file, byte/span, managed-stream, Skia-stream, data, and codec entry points through one cached CPU decode result. Invalid stream/data/file factories return null and bounds queries return `SKImageInfo.Empty`; the direct byte/span wrappers preserve native SkiaSharp's `ArgumentNullException` when codec creation fails. No metadata or invalid-input path initializes WebGPU. Default bitmaps convert unpremultiplied codec pixels to premultiplied RGBA8888; requested supported storage formats reuse the same stride-aware conversion and nearest scaled sampling. Parsing and conversion are `O(B + P)` time for `B` encoded bytes and `P` output pixels, with `O(B + P)` owned decode/output storage and no duplicate parse per codec.

`SKCodec` exposes the complete SkiaSharp 4.148 single-frame decode surface over that cached CPU image. Factories distinguish missing files (`InternalError`), unknown encodings (`Unimplemented`), and recognized truncated inputs (`IncompleteInput`) without starting WebGPU. Static images retain Skia's zero animation-frame metadata; incremental decode stores only the caller target and performs one full-frame decode, while unavailable scanline and subset paths report `Unimplemented`. PNG and other non-JPEG codecs accept only native dimensions, while JPEG dimensions quantize to the nearest supported eighth-resolution step and clamp to `[1/8, 1]`. The common RGBA/unpremultiplied path copies decoded rows directly into the caller stride with no intermediate bitmap; other supported color/alpha layouts reuse the CPU bitmap converter. Header classification and scaling queries are `O(1)` time and space, stream acquisition is `O(B)`, and decode/copy is `O(B + P)` time with `O(B + P)` codec storage plus only caller-owned output storage for `B` encoded bytes and `P` pixels.

Image metadata uses the complete `SKImageInfo` value contract and SkiaSharp 4.148 color-type inventory. Platform RGBA shifts, exact byte/bit depth for all 29 native color types, checked 32-bit and wide 64-bit row/image sizes, emptiness, opacity, rectangles, equality, and immutable-style `With*` copies are derived without touching pixel storage or initializing WebGPU. Encoded-format metadata includes AVIF and JPEG XL, while the obsolete filter-quality and bitmap-allocation enums retain their native values and attributes for binary/source compatibility. Every query is `O(1)` time and space; overflow remains explicit on the native 32-bit size properties instead of wrapping into an undersized allocation.

Integer rectangle metadata implements the complete SkiaSharp 4.148 `SKRectI` value contract. Edge properties, location/size mutation, standardization, aspect fit/fill, inflation, offset, union, intersection, containment, checked ceiling/floor/round/truncate conversion, equality, hashing, and formatting are all allocation-free. Point containment and ordinary intersection are half-open; inclusive intersection preserves a boundary-touch result, and native `IsEmpty` means exact equality with `SKRectI.Empty` rather than any zero-area rectangle. Aspect resizing computes in floating point around the original center and floors final edges like Skia. Every operation is `O(1)` time and space and cannot initialize WebGPU.

Floating-point rectangle metadata implements the matching complete SkiaSharp 4.148 `SKRect` value contract. Mutable edge, location, and size properties share one four-scalar representation; standardization and aspect fit/fill preserve native reversed-extent and zero-size behavior. Point containment and ordinary overlap are half-open, inclusive overlap retains boundary-touch intersections, and `IsEmpty` means exact equality with `SKRect.Empty`. Union always computes the coordinate envelope, including the empty rectangle's origin, so callers accumulating optional bounds must track whether a first bound exists separately. Creation, conversion, inflation, offset, union, intersection, equality, hashing, and formatting are allocation-free `O(1)` CPU operations and never initialize WebGPU.

Rounded rectangles implement the complete SkiaSharp 4.148 `SKRoundRect` contract with native corner order, specialization types, validation, containment, mutation, and axis-preserving transforms. Per-corner radii use the CSS/W3C overlapping-curves rule: the minimum side-to-radius-sum ratio scales all eight values together, with Skia's double-precision pair adjustment and float-ULP trimming. A zero radius on either axis makes that corner square; inset/outset preserves square corners and collapses over-inset bounds to their center. Transform radii are reconstructed from mapped absolute corner anchors and ellipse centers, preserving SkiaSharp's position-dependent float rounding across translation, reflection, scale, and quarter turns. Construction, normalization, containment, inset/outset, offset, validation, and transform are fixed four-corner `O(1)` CPU work with `O(1)` storage. Public `Radii` allocates its required independent four-point snapshot, while internal path and canvas consumers use a borrowed read-only span and allocate nothing.

Floating-point point metadata implements the complete SkiaSharp 4.148 `SKPoint` value contract. Mutable coordinates, length, squared length, offset, normalization, distance, all point/size arithmetic overloads, `Vector2` conversion, equality, hashing, and formatting are allocation-free. Compatibility includes native zero-vector normalization and SkiaSharp's historical reflection formula, which scales the supplied normal by the point's squared length; the integer point implementation uses the same rule. Every operation is `O(1)` time and space and cannot initialize WebGPU.

Float and integer size metadata implements the complete SkiaSharp 4.148 `SKSize` and `SKSizeI` value contracts. Mutable dimensions, point constructors and conversions, float/integer widening and narrowing, arithmetic, equality, hashing, emptiness, and formatting share allocation-free scalar operations. Float-to-integer narrowing truncates in a checked context, preserving native overflow behavior for out-of-range, infinite, and NaN dimensions. Every operation is `O(1)` time and space and cannot initialize WebGPU.

Matrix metadata implements the complete SkiaSharp 4.148 `SKMatrix` 3x3 projective value contract. Factory methods preserve native identity shortcuts and pivot equations; pre/post concat use direct row-major multiplication in native order; inversion uses double-precision cofactors, the native near-singular threshold, finite-result validation, and specialized tiny-scale handling. Point mapping divides by homogeneous `w`, zero `w` maps to zero, and perspective vector mapping subtracts the mapped origin. Rectangle mapping clips its four homogeneous corners against Skia's `w >= 1/16384` near plane in fixed stack storage before projection, preventing horizon-crossing coordinates from becoming infinite while preserving native bounds. Scalar matrix operations, inversion, and rectangle clipping are allocation-free `O(1)` time and space. Span mapping is `O(N)` time and `O(1)` auxiliary space; array-returning overloads allocate only their documented `O(N)` result. Internal `Matrix4x4` conversion carries both perspective coefficients and `Persp2`, so retained GPU commands do not silently flatten projective transforms.

Rotation-scale text positioning implements the complete SkiaSharp 4.148 `SKRotationScaleMatrix` contract used by Svg.Skia's per-glyph transforms. Identity, translation, uniform scale, radian/degree rotation, anchored scale-rotation-translation, mutable scalar properties, 3x3 matrix conversion, equality, and hashing retain native single-precision evaluation order and float semantics. Every operation is allocation-free fixed-work `O(1)` CPU work; RSXform arrays remain caller-owned and scale linearly only with glyph count.

Four-dimensional matrix metadata implements the complete SkiaSharp 4.148 `SKMatrix44` value contract as a contiguous 16-scalar struct. Construction from `SKMatrix` preserves 2D translation and perspective placement; row/column indexing, major-order conversion, translation, pivoted scale, axis rotation, determinant, inversion, transpose, point mapping, arithmetic, and native pre/post concat order delegate to the same `System.Numerics.Matrix4x4` semantics used by SkiaSharp. Singular inversion returns `Empty`, while span-based major-order copies require exactly 16 entries and remain allocation-free. Every scalar, factory, mapping, algebra, determinant, inversion, and span-copy operation is fixed-work `O(1)` time and space; only the array-returning major-order overloads allocate their documented 16-float result.

Color metadata implements the complete SkiaSharp 4.148 `SKColor` and `SKColorF` value contracts. Integer colors retain native packed AARRGGBB storage, while float colors preserve independent RGBA channels and clamp each channel without rewriting NaN. Conversion to bytes clamps to `[0, 1]` and rounds with Skia's `value * 255 + 0.5` rule; conversion from bytes divides by 255. HSL and HSV conversion uses the native 0.001 chroma epsilon, hue wrapping, and achromatic branches, while byte-returning `FromHsl` and `FromHsv` preserve Skia's truncation after their float conversion. Construction, channel replacement, packing, parsing, conversion, equality, hashing, and color-space arithmetic are allocation-free `O(1)` CPU operations and never initialize WebGPU.

The `SKColors` palette retains SkiaSharp's one-byte struct shape, mutable named static fields, exact CSS packed values, transparent-white `Transparent`, and computed zero `Empty` property. Every palette lookup is allocation-free `O(1)` static metadata access and never initializes WebGPU.

Color-space objects implement the complete SkiaSharp 4.148 `SKColorSpace`, CICP, and ICC-profile contracts without initializing WebGPU. sRGB and linear-sRGB use stable singleton objects; gamma, numerical-transfer, D50 matrix, equality, and gamma-conversion queries are `O(1)` and allocation-free. CICP creation selects the exact Skia D50 matrix and seven-parameter transfer definition in `O(1)`, including PQ/HLG marker semantics and the SMPTE ST 428 degenerate-primary matrix. ICC construction validates the header and bounded tag table, copies the caller profile once into stable pinned ownership, reads RGB XYZ and equal channel TRCs, and canonicalizes native sRGB/linear results. Parametric and single-gamma curves are `O(1)`; sampled curves are compared once against sRGB in `O(S)` time and `O(1)` auxiliary space for `S` samples, matching native rejection of arbitrary table curves. Profile parsing is `O(T + B)` time and `O(B)` owned storage for `T` tags and `B` bytes, while every subsequent renderer query remains `O(1)` with no profile I/O or curve comparison.

Color-space metadata implements the complete SkiaSharp 4.148 `SKColorSpaceXyz` and `SKColorSpaceTransferFn` value contracts. Named D50 matrices preserve Skia's exact sRGB, Adobe RGB, Display P3, Rec. 2020, and XYZ coefficients. Matrix concatenation is direct row-major multiplication; inversion uses double-precision cofactors, rejects zero or non-finite determinants, and returns the native empty value for a singular matrix. Transfer functions preserve Skia's seven scalar parameters, named sRGB, gamma 2.2, linear, Rec. 2020, PQ, and HLG definitions, signed-domain evaluation, PQ/HLG markers, and inverse rejection rules. The evaluator ports `skcms` bounded logarithm/exponent approximations so finite, NaN, and infinity results remain native-compatible without platform color-management dependencies. Construction, indexing, concatenation, inversion, evaluation, equality, and hashing are allocation-free `O(1)` CPU operations. `Values` is also `O(1)` and intentionally allocates only its fixed seven- or nine-scalar ownership snapshot.

Font style metadata implements the complete SkiaSharp 4.148 `SKFontStyle` object contract used by Svg.Skia typeface resolution. Default, enum, and unrestricted integer constructors preserve weight, width, and slant exactly; Normal, Bold, Italic, and BoldItalic are stable singleton properties whose public disposal is protected like native SkiaSharp. Ordinary owned styles release their compatibility handle on disposal. Construction and every metadata query are allocation-bounded `O(1)` CPU operations and never parse a font or initialize WebGPU.

Surface properties implement the complete SkiaSharp 4.148 `SKSurfaceProperties` metadata contract. Pixel geometry, raw 32-bit flags, the typed `SKSurfacePropsFlags` overload, unknown flag bits, device-independent-font detection, and owned-object disposal are retained without creating a surface or GPU context. Construction and queries are allocation-bounded fixed-work `O(1)` CPU operations.

Managed drawables implement the SkiaSharp 4.148 `SKDrawable` hook and canvas contract. Bounds and approximate memory metadata delegate to overridable hooks, drawing composes a supplied matrix inside save/restore isolation, point overloads use translation, generation invalidation is atomic, and the default snapshot records the drawable into a retained `SKPicture`. Metadata and invalidation are `O(1)`; drawing and snapshot are `O(C)` time and storage for `C` recorded commands, with no additional GPU submission or readback.

Retained pictures expose nonzero process-unique IDs, cull bounds, direct operation counts, recursive nested-picture counts, and estimated command/buffer ownership without replaying content. Direct counts are `O(1)`; recursive counts and byte estimates are `O(C + N)` for `C` local commands and `N` nested commands. Recorder overloads share one immutable command-array transfer, clear their active canvas on completion, and can transfer the result into a drawable whose bounds, metadata, drawing, and snapshots retain picture ownership independently. Picture-shader overloads retain tile modes, tile bounds, local matrices, and nearest/linear filtering through the raster-tile texture command instead of silently forcing nearest sampling. Recording and metadata remain CPU-only; shader rasterization is still deferred until the shader is actually drawn.

Picture serialization uses a versioned, process-independent ProGPU archive rather than a process-local object token. It recursively preserves scalar command state, pooled point/double/line/float buffers, analytic paths, built-in vector brushes, pens and dash state, embedded font bytes, texture-patch metadata, vertex meshes, glyph arrays, and nested pictures. Every count, string, archive size, and nesting depth is validated before allocation; serialization and deserialization are `O(B + C)` time and `O(B + C)` owned storage for `B` encoded bytes and `C` commands. GPU textures, static buffers, cache-key objects, and custom extension payloads fail explicitly because they require a separate portable codec or readback contract. The archive round-trips every `SKPicture` stream/data/pointer overload but intentionally is not a native Skia `.skp` interchange format.

Point primitives implement SkiaSharp's `SKPointMode` and `SKCanvas.DrawPoint(s)` contracts through the existing stroke geometry pipeline. Point mode batches zero-length segments, line mode consumes disjoint pairs and ignores an unmatched tail, and polygon mode connects adjacent points without closing the contour. Paint style is ignored as in Skia while stroke width, cap, color, blend, antialiasing, and canvas transforms remain intact. Recording is `O(N)` time and path storage for `N` points, emits one retained draw command, and adds no shader or per-point GPU submission.

Canvas transform convenience APIs preserve SkiaSharp's post-concatenation order across scalar, point, pivot, radians, degrees, skew, 3x3, and 4x4 inputs. Identity scales, empty point transforms, zero skews, and complete degree turns are skipped before matrix arithmetic; pivot operations use the native translate-transform-translate sequence. Every transform is allocation-free `O(1)` CPU work, updates only retained canvas state, and performs no command recording, GPU submission, or readback.

Canvas point/size shape overloads normalize directly into the existing line, rounded-rectangle, ellipse, and circle primitives. Conversion is allocation-free `O(1)` work before the normal retained command or path is created, so an overload never adds a second draw, changes tessellation, or introduces a GPU synchronization boundary.

Picture playback overloads normalize scalar and point positions into a local matrix and compose it with the current canvas transform for exactly one nested retained-picture command. Optional paint blend and opacity scopes wrap that command, while local matrix state is restored in `finally`. Playback setup is `O(1)` and allocation-free apart from color-space conversion caches already required by the nested picture; playback itself remains `O(C)` for `C` nested commands without flattening or GPU readback.

Image draw overloads normalize point, scalar, destination-only, and source/destination inputs into one sampling-aware texture command. Legacy paint filter quality maps once to nearest, linear, linear-mipmap, or Mitchell sampling before the shared path; explicit cubic and anisotropic settings remain intact. Overload setup is allocation-free `O(1)`, recording remains one retained texture command plus required clip/blend/opacity scopes, and no overload performs a readback.

Bitmap draw overloads follow SkiaSharp's adapter model: create one short-lived image view, normalize legacy or explicit sampling, and enter the same image command path. The adapter is `O(1)` metadata work around the bitmap-backed texture, retains no duplicate canvas implementation, and preserves the one texture draw plus required scopes without readback.

Surface draw overloads snapshot once, preserve legacy or explicit sampling, and route through the same image command path. Point overloads are scalar adapters; canvas and surface APIs share one implementation. Setup is `O(1)` around the required snapshot, recording remains one retained texture draw plus scopes, and no additional readback or duplicated renderer path is introduced.

`SKTextBlob` intercept queries use the same path-boundary algorithm as Skia for underline and strike-through avoidance. Each positioned glyph independently tests line, quadratic, and cubic crossings at both horizontal band boundaries, expands the interval with path points strictly inside the band, and returns at most one ordered pair without merging overlaps from neighboring glyphs. `ScaleX`, `SkewX`, vertical placement, and synthetic emboldening are included; rotation-scale runs are intentionally ignored like native Skia. Array, span, and count overloads share the CPU-only engine, with short spans receiving the available prefix. For `G` glyphs and `S` path segments, time is `O(G * S)`, root-solving workspace is `O(1)`, and returned storage is at most `O(G)`.

`SKRotationScaleMatrix` provides the complete SkiaSharp 4.148 identity, translation, uniform-scale, radian rotation, degree rotation, anchored rotation-scale, and matrix-conversion factories used by RSXform text and atlas placement. Every factory is allocation-free `O(1)` CPU math. Combined transforms preserve native operation order: degree conversion multiplies by the pre-divided float radians-per-degree constant, sine and cosine are evaluated in double precision and then cast to float, and anchor compensation is applied in the original float expression order. This keeps all four stored components bit-identical to native without initializing WebGPU.

`SKSurfaceProperties` retains SkiaSharp 4.148 surface metadata as an `O(1)` managed value holder over the normal `SKObject` lifetime. `SKPixelGeometry` uses the native five-value numbering (`Unknown`, RGB/BGR horizontal, RGB/BGR vertical), and `SKSurfacePropsFlags` preserves both the device-independent-font bit and unknown caller bits supplied through the `uint` constructor. Construction and property queries allocate no GPU resource and do not initialize WebGPU; consumers may use the metadata when selecting text rasterization behavior without remapping enum values.

`SKFontStyle` mirrors SkiaSharp 4.148 as an `SKObject`-owned immutable style descriptor. The default constructor produces normal weight/width with upright slant, integer constructors retain caller values without normalization, and the `Normal`, `Bold`, `Italic`, and `BoldItalic` properties return stable process-wide instances. Public disposal is protected for those shared instances while ordinary constructed styles release their managed compatibility handle. Construction, property access, and singleton lookup are `O(1)` and do not initialize WebGPU or inspect font files.

Path-positioned text blobs reproduce SkiaSharp's non-warped text-on-path algorithm. Glyph midpoint distances are aligned over a measured contour, clipped to its half-open length, sampled for position/tangent, shifted back by half advance, and offset along the path normal before becoming rotation-scale matrices. For `G` glyphs and path-measure setup cost `P`, time is `O(G + P)`, storage is `O(G)`, and drawing stays on the retained glyph-run path without per-glyph GPU submission or readback.

Canvas text overloads preserve SkiaSharp's legacy alignment metadata while normalizing point and scalar coordinates into the canonical shaped-text path. Text-on-path selects outline morphing when `warpGlyphs` is true and tangent-positioned rotation-scale blobs otherwise. Wrapper cost is `O(1)`; shaping and path placement remain `O(G + P)` for G glyphs and path-measure setup P, and compositor glyph instances continue to batch without readback or quality reduction.

Core canvas color operations draw a device-space rectangle through the requested blend mode, independent of the current matrix but inside retained clip scopes; float colors are clamped before recording. Arc drawing converts the Skia angle convention into one elliptical path, clamps full turns to one oval, and closes center wedges only when requested. Color/discard/state work is `O(1)`; arc path creation is bounded `O(1)`, emits one retained path command, and performs no readback.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `ProGPU.SkiaSharp` | ProGPU-backed SkiaSharp API compatibility layer. | [![NuGet](https://img.shields.io/nuget/vpre/ProGPU.SkiaSharp.svg)](https://www.nuget.org/packages/ProGPU.SkiaSharp/) |

### [Svg.Skia](https://github.com/wieslawsoltes/Svg.Skia)

Svg.Skia renders SVG 1.1 documents and its supported static SVG 2 subset through a SkiaSharp-shaped canvas. Its W3C and resvg test lanes also exercise `ProGPU.SkiaSharp`, providing broad compatibility and rendering-parity coverage for the shim.

The `Svg.Skia parity` workflow pins the exact Svg.Skia source commit and runs separate native-SkiaSharp and ProGPU-shim checkouts on macOS. The native W3C lane must remain 530 passes with 3 skips; resvg and the non-W3C remainder must stay fully green through the shim. The ProGPU W3C lane validates its exact reviewed difference inventory and uploads both TRX files plus actual PNGs. A resolved row, a new failure, or a changed failure set intentionally fails the inventory gate until the images are reviewed and `eng/svg-skia-w3c-known-differences.txt` is updated in the same change.

| Package | Purpose | NuGet |
| --- | --- | --- |
| `Svg.Skia` | Core SVG-to-SkiaSharp renderer. | [![NuGet](https://img.shields.io/nuget/vpre/Svg.Skia.svg)](https://www.nuget.org/packages/Svg.Skia/) |
| `ShimSkiaSharp` | Backend-neutral SkiaSharp API abstraction used by Svg.Skia integrations. | [![NuGet](https://img.shields.io/nuget/vpre/ShimSkiaSharp.svg)](https://www.nuget.org/packages/ShimSkiaSharp/) |
| `Svg.Skia.JavaScript` | Optional JavaScript execution support for SVG documents. | [![NuGet](https://img.shields.io/nuget/vpre/Svg.Skia.JavaScript.svg)](https://www.nuget.org/packages/Svg.Skia.JavaScript/) |
| `Svg.Controls.Skia.Avalonia` | Avalonia control integration for the Svg.Skia renderer. | [![NuGet](https://img.shields.io/nuget/vpre/Svg.Controls.Skia.Avalonia.svg)](https://www.nuget.org/packages/Svg.Controls.Skia.Avalonia/) |
| `Svg.SourceGenerator.Skia` | Source generator for compiling SVG resources into SkiaSharp drawing code. | [![NuGet](https://img.shields.io/nuget/vpre/Svg.SourceGenerator.Skia.svg)](https://www.nuget.org/packages/Svg.SourceGenerator.Skia/) |

---

## Architectural Hierarchy

The ProGPU framework is built in a modular, layered stack that bridges native graphics APIs and system windowing up to a modern, declarative WinUI-compatible user interface layer.

```mermaid
graph TD
    subgraph L6 ["Layer 6: Application Layer"]
        App["Gallery Dashboard / LOL/s & MotionMark Benchmarks"]
    end

    subgraph L5 ["Layer 5: WinUI Framework Layer"]
        Controls["Grid, StackPanel, ScrollViewer, Border, Pivot, RichTextBlock"]
        FE["FrameworkElement"]
        LN["LayoutNode - Measure & Arrange Sizing Negotiation"]
    end

    subgraph L4 ["Layer 4: Scene Graph & Effects Layer"]
        CV["ContainerVisual / DrawingVisual / Visual with ChangeVersion"]
        ILN["ILayoutNode Interface - Layout and Scene Invalidation"]
        FX["GPGPU Multi-Pass Effects Pipeline - Blur & DropShadow"]
    end

    subgraph L3 ["Layer 3: Compositor, Text & GPGPU Rasterizer"]
        Cache["Compiled Scene Cache - Versions, Targets, Atlases, Layers"]
        Comp["Compositor - Z-Ordered Draw Lists and GPU Buffer Compiler"]
        Text["TTF Layout, Rich Text Command Cache, Glyph Atlas"]
        Rast["Compute-Bound 4x SSAA Glyph and Path Rasterizers"]
    end

    subgraph L2 ["Layer 2: Graphics Infrastructure"]
        Wgpu["WgpuContext - WebGPU Adapter/Device & Swapchain Management"]
    end

    subgraph L1 ["Layer 1: System & Windowing"]
        Silk["Silk.NET Windowing & GLFW OS Event Loop"]
    end

    App --> Controls
    Controls --> FE
    FE --> LN
    LN --> CV
    CV --> ILN
    CV --> FX
    ILN --> Cache
    Cache --> Comp
    FX --> Rast
    Comp --> Rast
    Rast --> Wgpu
    Wgpu --> Silk
```

### Layer Description

1. **System & Windowing (Layer 1)**: Interacts with the operating system event queue and monitors display boundaries via Silk.NET and GLFW. It handles window load, resize, rendering loops, and low-level mouse and keyboard input events.
2. **Graphics Infrastructure (Layer 2)**: Manages physical GPU adapter querying, logical device creation, graphics command queues, and swapchain surface configuration.
3. **Compositor, Text & GPGPU Rasterizer (Layer 3)**: Validates and reuses compiled scenes when their visual versions, target configuration, atlas generations, overlays, and cached layers are unchanged. Cache misses compile high-level commands into ordered draw lists and reusable GPU buffers. Framework adapters retain shaped glyph indices and positions as one glyph-run command; the compositor caches font feature availability and rasterizes glyph and vector outlines analytically in WGSL at physical-pixel resolution.
4. **Scene Graph & Effects Layer (Layer 4)**: Establishes the retained `ContainerVisual`, `DrawingVisual`, and `Visual` hierarchy. Mutations propagate `ChangeVersion` and dirty state so layout, compiled-scene, and `CacheAsLayer` reuse remain correct. Mask and effect passes use offscreen textures and intentionally stay on the dynamic compilation path.
5. **WinUI Framework Layer (Layer 5)**: Implements cached `Measure` and `Arrange`, controls, input, and CPU visual-tree hit testing. The WinUI host disables the compositor's duplicate GPU hit-test index while direct compositor consumers retain it by default.
6. **Application Layer (Layer 6)**: Hosts gallery pages, diagnostics, and opt-in performance workloads. Sample animation and status updates invalidate only the visuals that actually changed.

### Shader Source and Startup Contract

Fixed GPU programs live as individual `.wgsl`, `.glsl`, or `.hlsl` files under the owning project's `Shaders/` directory. `Directory.Build.props` embeds these resources into each assembly, and `ShaderResource` decodes each source once into a process-wide cache. Pipeline owners retain the returned reference in `static readonly` fields, so rendering and compute hot paths perform no shader file I/O, manifest lookup, UTF-8 decoding, helper concatenation, or per-frame source allocation.

Each resource documents its algorithm, time complexity, and storage or bandwidth complexity at the top of the file. Final render and compute modules are self-contained, including analytic curve helpers that were previously concatenated from C# strings. Dynamic systems keep only input-dependent generation in C#: the DirectX HLSL translator emits WGSL from caller programs, WPF effects generate active sampler declarations, and ShaderToy appends user code. Their fixed headers and fragment wrappers are still resource-backed and cached.

`ShaderResourceTests` verifies that every source file is present in its assembly, loaded text matches the checked-in resource, required cost-model documentation exists, and fixed production stage modules do not reappear as C# literals. This keeps shader reuse and performance properties enforceable as the renderer evolves.

---

## Current Frame Architecture and Performance Baseline

`Microsoft.UI.Xaml.Window.RenderFrameCore` records each host phase independently: dispatcher work, rendering callbacks, framebuffer/DPI setup, animation, layout, surface acquisition, compositor work, and presentation. `Compositor.RenderScene` then chooses between a retained fast path and a dynamic compile path:

```mermaid
flowchart TD
    Frame["Window frame"] --> Dispatch["Drain bounded UI work"]
    Dispatch --> Update["Rendering callbacks, animation, cached layout"]
    Update --> Acquire["Acquire physical-pixel surface"]
    Acquire --> Validate{"Compiled scene still valid?"}
    Validate -- Yes --> Reuse["Reuse draw lists, GPU buffers, brushes, hit index, and atlas entries"]
    Validate -- No --> Compile["Compile visual tree and external layers"]
    Compile --> Upload["Upload changed geometry, brushes, and uniforms"]
    Upload --> Raster["Batch pending glyph and path rasterization"]
    Raster --> Capture{"Scene safe to retain?"}
    Capture -- Yes --> Remember["Capture versions, target, atlas generations, and layer textures"]
    Capture -- No --> Dynamic["Keep dynamic path for effects, masks, diagnostics, or DrawingVisual"]
    Remember --> Render["Execute ordered WebGPU render pass"]
    Dynamic --> Render
    Reuse --> Render
    Render --> Present["Present"]
```

The compiled scene cache is enabled by default with `CompositorOptions.EnableCompiledSceneCache`. A hit requires the same root identity and `ChangeVersion`, logical and physical target, viewport, DPI scale, glyph/path atlas generations, tooltip, external layer versions, and valid `CacheAsLayer` textures. Dynamic diagnostics force a miss. Mutable `DrawingVisual` content, masks, and effects are deliberately not retained because their output can change without a stable immutable command contract.

`CacheAsLayer` and compiled-scene reuse solve different costs. `CacheAsLayer` turns a stable subtree into one texture draw while the rest of a scene may still compile. Whole-scene reuse preserves the already compiled draw lists and GPU buffers for a stable frame. Atlas `Generation` values make both paths safe when a glyph/path atlas is cleared or repacked.

The WinUI host sets `EnableGpuHitTesting = false` because `InputSystem` already performs CPU visual-tree hit testing. Direct scene consumers keep the compositor GPU hit-test index enabled by default. This avoids building two indexes for every WinUI frame without changing input behavior.

### Reference Performance

The opt-in sample harness reports wall-clock FPS, per-phase timings, allocation rate, scene-cache decisions, draw counts, and workload throughput. A macOS 120 Hz reference run of the current architecture produced the following results; hardware, window size, and page state affect absolute values.

| Workload | VSync | Wall FPS | Workload throughput | Scene cache |
| --- | ---: | ---: | ---: | ---: |
| LOL/s Benchmark | On | 120.26 | 12,001 LOL/s | Dynamic, 0/480 hits |
| LOL/s Benchmark | Off | 202.48 | 40,429 LOL/s | Dynamic, 0/600 hits |
| Markdown Playground | Off | 519.43 | Static after warmup | 299/300 hits |
| DXF CAD Viewer | Off | 484.09 | Static after warmup | 299/300 hits |

Run the same deterministic workload from the repository root:

```bash
dotnet build src/ProGPU.Samples/ProGPU.Samples.csproj -c Release

PROGPU_SAMPLE_BENCHMARK_PAGE='LOL/s Benchmark' \
PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES=240 \
PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES=480 \
PROGPU_SAMPLE_BENCHMARK_VSYNC=true \
dotnet run --project src/ProGPU.Samples/ProGPU.Samples.csproj -c Release --no-build
```

Set `PROGPU_SAMPLE_BENCHMARK_VSYNC=false` for uncapped throughput, or change the page to `Markdown Playground` or `DXF CAD Viewer` to verify static-scene reuse. The first measured static frame may populate the cache; subsequent frames should report hits unless the page intentionally animates or invalidates.

Rendering quality remains part of the performance contract. The optimized text path retains the glyph index chosen during layout, hoists transform/raster invariants out of glyph loops, and skips color/bitmap table probes only when the parsed font has no such tables. Avalonia solid outline text records one retained glyph run instead of one path per glyph: shaped indices are retained, `Vector2` positions are converted once when the platform glyph run is created, and redraws reuse both arrays. Recording is O(1) with no glyph-count-dependent allocation; compositor compilation is O(G) for G glyphs. Gradient brushes and color/bitmap fonts keep their path or texture fallbacks.

Vector glyphs keep 8x8 path-atlas coverage and use a device-pixel-size transfer calibrated against native Skia: small axis-aligned text preserves fine edge detail, large text receives the slightly stronger coverage needed to match Skia's visual weight, and rotated/reflected text keeps its separately calibrated branch. The physical-size classification includes display DPI, transform scale, and static-buffer zoom, and is computed once per text command for reuse by every glyph. Glyph geometry, subpixel placement, physical DPI rasterization, winding rules, brush opacity, and blend behavior remain unchanged.

Texture resampling follows the same contract. `SKCubicResampler` is an immutable two-coefficient SkiaSharp value with native float equality, hashing, and named Mitchell (`1/3, 1/3`) and Catmull-Rom (`0, 1/2`) kernels. `SKSamplingOptions` is the matching immutable discriminated value for nearest/linear filtering, mipmapping, cubic coefficients, and requested anisotropy. Cubic draws retain B/C through the recorded command and texture vertices, and the WGSL texture shader evaluates the full Mitchell-Netravali kernel. Named and custom kernels therefore remain distinct. Anisotropic draws retain their requested value through recording, clamp only at the portable WebGPU boundary to `[1, 16]`, generate mipmaps, and reuse one lazily created sampler and persistent texture bind group per effective value; ordinary draws do not enter this cache. Value construction, reads, comparison, and hashing are allocation-free `O(1)` CPU operations. Sampler lookup is amortized `O(1)` with at most 15 anisotropic sampler objects, and the common Catmull-Rom render path keeps its original compact polynomial and pixel output.

---

## Technical Specifications: Performance Optimizations

The sections below describe the cooperating layout, scene, text, atlas, batching, effects, and platform optimizations. They share one invariant: cached work is reused only while every input that can affect pixels remains valid.

### 1. WinUI-Compatible High-Performance Layout Caching & Invalidation

#### Sizing Negotiation Lifecycle
Traditional layout systems recursively traverse the entire scene graph every frame to negotiate sizing, causing massive $O(N)$ CPU overhead on complex visual trees even when the UI is static. 

ProGPU introduces a cached sizing negotiation model that short-circuits measurements using layout dirty flags and cached input boundaries:

```mermaid
flowchart TD
    Start["Measure Pass availableSize"] --> Cached{"_isMeasureValid and availableSize == _previousAvailableSize?"}
    Cached -- Yes --> O1Exit["O1 Early Exit - Return Cached DesiredSize"]
    Cached -- No --> Calc["Calculate Margin Insets & Bounds Constraints"]
    Calc --> Override["Execute MeasureOverride child passes recursively"]
    Override --> CacheResult["Store DesiredSize, _previousAvailableSize & set _isMeasureValid = true"]
    
    CacheResult --> ArrangeStart["Arrange Pass finalRect"]
    ArrangeStart --> CachedArr{"_isArrangeValid and _isMeasureValid and finalRect == _previousFinalRect?"}
    CachedArr -- Yes --> O1ExitArr["O1 Early Exit - Return Immediately"]
    CachedArr -- No --> Align["Calculate Offset Coordinates & Horizontal/Vertical Alignments"]
    Align --> OverrideArr["Execute ArrangeOverride child placements recursively"]
    OverrideArr --> CacheResultArr["Store Offset/Size, _previousFinalRect & set _isArrangeValid = true"]
```

- **Measure Cache**: Inside `LayoutNode.Measure()`, if `_isMeasureValid` is true and the incoming `availableSize` matches `_previousAvailableSize`, the pass returns immediately. `MeasureOverride` and recursive child traversals are fully bypassed in $O(1)$ time.
- **Arrange Cache**: Inside `LayoutNode.Arrange()`, if `_isArrangeValid` and `_isMeasureValid` are true and the incoming `finalRect` matches `_previousFinalRect`, the pass short-circuits. Children offsets are not recalculated, and recursive child arrangements are bypassed.
- **Parent Bubble-Up Invalidation**: When layout-affecting properties (such as `Margin`, `Padding`, `WidthConstraint`, `HeightConstraint`, alignments, or child mutations) are changed, they invoke `InvalidateMeasure()` or `InvalidateArrange()`. These clear local flags and bubble up the invalidation recursively to parent nodes, forcing only the dirty subtrees to be re-evaluated during the next frame's deferred layout pass.

#### Decoupled Visual Invalidation
To prevent circular dependencies between the `ProGPU.Scene` assembly (base visual layer) and the `ProGPU.Layout` assembly (WinUI framework layer), the `ILayoutNode` interface is defined in `ProGPU.Scene`:
```csharp
public interface ILayoutNode
{
    void InvalidateMeasure();
}
```
Visual tree mutation methods (`ContainerVisual.AddChild`, `RemoveChild`, `ClearChildren`) check if `this` implements `ILayoutNode`. If so, they invoke `InvalidateMeasure()`, ensuring that any changes in visual tree structure automatically mark the layout path dirty without explicit parent layout references.

#### Retained-Scene Invalidation and Text-Page Responsiveness

Layout validity and pixel validity are separate. A layout property can alter clipping, alignment, padding, or descendant placement even when the final `Size` and `Offset` happen to compare equal. The `LayoutNode` setters therefore invalidate measure or arrange **and** call visual `Invalidate()`, which advances `ChangeVersion` to the compiled-scene root. This prevents a retained frame from displaying stale text or geometry after a layout-only mutation.

Text interaction uses the narrowest valid invalidation path. `RichTextBlock` observes every retained `TextElement` in its inline tree, so changing `Run.Text`, font, size, or foreground advances the owning visual version and invalidates layout without waiting for pointer activity. Hyperlink hover, caret movement, and selection changes dirty only render commands and pixels; they retain positioned characters and do not discard document layout. Single-column Markdown measured with infinite height performs one engine layout instead of a measurement layout followed by an identical second pass, while multi-column measurement reuses scratch lists.

Page navigation keeps stable ownership boundaries:

- Replacing `NavigationView.Content` changes only the `SplitView` content child. The pane and all menu-item visuals remain parented, preserving their layout, theme resources, and cached layer.
- `SplitView` removes and inserts only the child that changed instead of clearing and rebuilding both children.
- Reparenting a dependency-object subtree recursively reapplies theme state only when the resolved theme or theme family actually changes. Moving a cached page between parents with the same theme therefore performs an O(H) ancestor-context comparison for tree height H instead of O(N) invalidation over the page subtree.
- System-font discovery and TextMate grammar loading use one shared background warm-up. Font menu controls are created only when the dropdown is opened. A code editor renders the same source as plain themed runs while grammar loading is pending, then retokenizes on the UI dispatcher with the shared theme grammar. Page activation therefore performs neither a synchronous filesystem font scan nor per-editor grammar initialization.

Large indexed pages remain virtual from source to pixels. `ItemsControl` retains an `IList` source rather than eagerly copying and boxing every item. Attaching a 65,535-glyph source is O(1) time and storage; `UniformVirtualizingGridPanel` realizes and binds only V visible/overscan items in O(V), reuses its recycler-index buffer, and does not allocate a binding closure on each viewport update. Selection and syntax-highlight changes rebind the V active containers in place instead of recycling, reparenting, and remeasuring them. Scrollbar z-order uses an in-parent reorder that invalidates pixels but not layout. The glyph browser represents indices through a read-only computed list, so it allocates neither a 65,535-entry source array nor an internal duplicate. `FontIcon` records the cached raw outline with a public `DrawPath` transform, preserving line, quadratic, cubic, and arc segments without allocating a transformed path per cell or render.

---

### 2. High-Performance Struct Equality and Comparison

Layout caching relies heavily on comparing boundary structs (`Thickness` and `Rect`) on every node. Standard C# struct comparison utilizes generic `ValueType.Equals`, which triggers CPU reflection, runtime boxing, and high memory allocations.

To eliminate this bottleneck, we implemented type-safe, non-boxing, custom equality overloads for both structs:
- **`Thickness`** (Margins and Paddings)
- **`Rect`** (Layout arrangements and clipping boundaries)

Each struct now overrides `Equals(Thickness/Rect other)`, `Equals(object? obj)`, `GetHashCode()`, and provides high-speed operators:
```csharp
public bool Equals(Rect other)
{
    return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
}

public static bool operator ==(Rect left, Rect right)
{
    return left.Equals(right);
}

public static bool operator !=(Rect left, Rect right)
{
    return !left.Equals(right);
}
```
These overloads compile down to direct float comparison instructions, achieving zero-allocation, ultra-fast boundary checks.

---

### 3. VSync-Off Graphics Pipeline Swapchain

To allow graphics and layout benchmarks to be evaluated at their true physical limit, we disabled vertical synchronization (VSync) throttling across all layers of the GPU pipeline:

- **Windowing Layer**: Window options in the main, developer tools, and dynamic window controllers explicitly configure VSync to be disabled:
  ```csharp
  options.VSync = false;
  ```
- **WebGPU Swapchain**: Inside `WgpuContext.ConfigureSwapChain()`, the surface capabilities of the GPU adapter are queried. If `PresentMode.Immediate` is supported, the swapchain present configuration bypasses synchronization lockups:
  ```csharp
  PresentMode presentMode = PresentMode.Fifo; // Fallback VSync
  for (uint i = 0; i < capabilities.PresentModeCount; i++)
  {
      if (capabilities.PresentModes[i] == PresentMode.Immediate)
      {
          presentMode = PresentMode.Immediate; // VSync Off
          break;
      }
  }
  ```
This enables the graphics swapchain to present frames as quickly as the GPU queue is filled, releasing the 60 FPS constraint and allowing framerates to soar into the hundreds or thousands of FPS.

---

### 4. Dynamic Backpressure-Throttled Event Dispatcher

The LOL/s benchmark stresses the visual framework by constantly removing and adding hundreds of poolable text controls to a canvas using a background thread loop. 

- **The Livelock Risk**: If a background thread pushes UI events (like `AddChild` or property changes) to the main thread's dispatcher loop as fast as possible without throttling, it will quickly overflow the main thread's event queue. The main thread then spends entire frame cycles acquiring queue locks to process actions, creating massive lock contention that completely starves the UI thread and freezes the application.
- **The Backpressure Solution**: We introduced a thread-safe `PendingCount` property to the main `UIThread` queue. The background benchmark thread loops continuously without fixed sleep periods, but monitors queue occupancy:
  ```mermaid
  flowchart TD
      Start["Background Task Loop"] --> CheckBackpressure{"UIThread.PendingCount > 100?"}
      CheckBackpressure -- Yes --> Sleep["Thread.Sleep 1ms / Release Monitor Locks"]
      Sleep --> Start
      CheckBackpressure -- No --> Post["Post Action immediately / No Sleep"]
      Post --> UIThread["UIThread.RunPending - Main Thread drains queue"]
      UIThread --> AddChild["AddChild/RemoveChild visual tree mutation"]
  ```
  - **Backpressure Active (>100)**: The background thread sleeps for exactly `1ms`. This releases the queue monitor lock completely and relinquishes the CPU slice, allowing the main UI thread to drain the event queue with zero lock contention. The application remains 100% responsive and immune to livelocks.
  - **Backpressure Inactive (<=100)**: The background thread runs with zero sleep, dispatching new visual mutations to the UI thread continuously to maximize throughput.

---

### 5. Compositor Mesh Compilation via Span-Based Direct Writes

In real-time GPU-based vector rendering, compiling high-level primitives (such as Rectangles, Ellipses, Rounded Rectangles, Paths, Lines, and Bezier curves) into dynamic vertex and index buffers is a major CPU bottleneck. Standard implementation using sequential `.Add(...)` calls on `List<T>` invokes continuous bounds checks, potential array resizing/reallocations, and element copying overhead.

To maximize throughput, the `Compositor` is optimized using high-performance `Span<T>` memory writes:
- **Pre-Allocation Throttling**: Instead of building meshes incrementally, the compositor determines the exact number of vertices and indices required for a primitive beforehand.
- **Backing Buffer SetCount**: The internal list count is directly resized using `CollectionsMarshal.SetCount(list, newCount)` to avoid iterative dynamic reallocation/growth logic inside .NET's `List<T>`.
- **Direct Memory Access**: The internal backing array is extracted as a type-safe memory slice via `CollectionsMarshal.AsSpan(list).Slice(offset, count)`.
- **Fast Assembly Assignment**: Vertices and indices are written directly to indices in the returned `Span<T>` or pre-filled using `Span.Fill(defaultValue)` for uniform values.
- **Bulk Memory Clipping**: Clamping vector coordinates to active clipping boundaries is performed in a single linear pass over the direct `Span<VectorVertex>` reference, bypassing indexed list getters.

```csharp
int originalVertexCount = _vectorVerticesList.Count;
int vertexToAdd = 2 * (N + 1);
CollectionsMarshal.SetCount(_vectorVerticesList, originalVertexCount + vertexToAdd);
var vertexSpan = CollectionsMarshal.AsSpan(_vectorVerticesList).Slice(originalVertexCount, vertexToAdd);
vertexSpan.Fill(baseVertex);
```

This ensures that the mesh compiler achieves zero-allocation dynamic buffer construction, minimal instruction-level overhead, and runs at near-native C-speed.

---

### 6. Retained MotionMark Geometry and Frame Scheduling

In traditional UI and vector engines, every active visual element in an animation loop is modeled as a heap-allocated class object. During high-count stress tests (such as the MotionMark benchmark rendering thousands of dynamically moving curves), these allocations put immense pressure on the .NET Garbage Collector (GC), leading to periodic micro-stutters and frame drops.

ProGPU eliminates this overhead using densely stored value types, retained geometry, and explicit pre-render scheduling:
- **Dense Elements**: Animated shapes are modeled using compact `Element` and `GridPoint` value types, avoiding one managed object allocation per segment:
  ```csharp
  public struct Element
  {
      public SegmentKind Kind;
      public GridPoint Start;
      public GridPoint Control1;
      public GridPoint Control2;
      public GridPoint End;
      public Vector4 Color;
      public float Width;
      public bool Split;
      public SolidColorBrush CachedBrush;
      public Pen CachedPen;
      public PathGeometry CachedPath;
  }
  ```
- **Retained Official Paths**: Logical 80x40 grid points are mapped when elements are generated or the viewport changes. Each segment owns one retained `PathGeometry`; steady frames submit the same geometry references through the public `DrawingContext.DrawPath` API instead of allocating paths and segment objects in `OnRender`.
- **Two Public-API Modes**: Individual mode emits one retained path command per segment. The default grouped mode reuses pooled `PathGeometry`/`PathFigure` containers and retained segment references for each split-delimited group, reducing command compilation without bypassing the renderer.
- **Pre-Render Animation Scheduling**: The visual implements `IAnimatedElement`, so the normal sample-tree update traversal advances it every frame while it is attached. `Update(delta)` mutates split state and invalidates before compositor compilation; `OnRender` is side-effect free. Pointer movement is no longer needed to keep the benchmark active.
- **Time-Normalized Split Work**: The original 0.5% split probability at 60 Hz is accumulated as `0.3 * N * delta` toggles. Updating K due toggles is O(K) rather than scanning all N elements, while grouped command recording remains O(N + G) for N segments and G groups. Persistent geometry storage is O(N); the retained group pool is O(Gmax), bounded by N, after warmup. Pens, brushes, HUD strings, and path containers are refreshed only when their owning settings, theme, geometry, or viewport change.
- **Measured Result**: On the 120 Hz reference macOS machine, the 1,000-element uncapped benchmark improved from 146.9 to 190.4 wall FPS and reduced visual compilation from 3.71 to 2.24 ms per frame. With VSync enabled it sustains 120 FPS with a deliberate `Root version changed` cache miss on every animated frame.

---

### 7. GPGPU Real-Time Multi-Pass Effects Pipeline

Standard graphics engines struggle to apply dynamic blurred effects (such as Gaussian backdrop blurs, soft ambient drop shadows, and neon glowing halos) to standard layout elements in real-time due to high composition and memory transfer overhead. ProGPU overcomes this with a multi-pass offscreen composition and compute processing system.

```mermaid
graph TD
    Subtree["Subtree Render Pass"] -->|Draw Elements 1x MSAA| Src["Source Offscreen Texture"]
    Src -->|Horiz. Dispatch| HCompute["Gaussian Blur Compute Shader Pass 1"]
    HCompute -->|Vert. Dispatch| VCompute["Gaussian Blur Compute Shader Pass 2"]
    VCompute -->|Output Framebuffer| Dest["Destination Blurs/Shadows Texture"]
    Dest -->|Matrix Align and Z-Order Bind| Framebuffer["Primary Swapchain Framebuffer"]
```

- **Dynamic Texture Caching**: Textures (`Source`, `Temp`, and `Destination` buffers) are cached per-element in a specialized dictionary (`_effectTextures`). They are dynamically resized only when the element's actual visual bounds mutate, eliminating frame-by-frame allocation/deallocation thrashing.
- **Offscreen Redirection**: Standard scene-graph rendering in ProGPU uses 4x MSAA for vector geometry. Since WebGPU compute shaders cannot directly read or sample multisampled textures, ProGPU compiles a specialized 1x MSAA offscreen rendering pipeline (`_vectorPipelineOffscreen`, `_textPipelineOffscreen`, `_texturePipelineOffscreen`). When an element has an active effect:
  1. The compositor preserves the active vector batch state and clips.
  2. It redirects all rendering of the element and its entire visual child subtree into the 1x MSAA offscreen `Source` texture using an isolated orthographic projection matrix.
  3. Restores the main batch state after capture.
- **Two-Pass Compute Acceleration**: The compute pass binds the `Source` texture and executes a horizontal-pass WGSL compute shader, writing intermediate results to the `Temp` texture. It then binds the `Temp` texture to execute a vertical-pass compute shader, outputting the final blurred mask to the `Destination` texture.
- **High-Performance Compositing**: The final blurred texture is drawn back onto the main screen swapchain as a textured quad. For drop shadows, the texture is drawn with configurable offsets, blending colors, and alpha multipliers, and the original `Source` texture is composited cleanly on top, maintaining crisp bounds.

---

### 8. GPU-Bound Analytical Vector Path Rasterization

To bypass CPU bottlenecks (e.g. flattening Bezier curves into thousands of lines and performing heavy triangulation), ProGPU integrates a pure GPU-bound vector path rasterizer. The engine computes vector fills analytically directly inside custom WebGPU WGSL compute shaders.

#### Sequential 16-Byte Aligned Struct Layouts
To satisfy WebGPU/WGSL uniform and storage buffer packing requirements, layout metrics are organized into sequentially packed structs matching exact 16-byte memory alignments:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct PathUniforms
{
    public float XStart;   public float YStart;
    public float Scale;    public uint PathIndex;
    public uint AtlasX;    public uint AtlasY;
    public uint Width;     public uint Height;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuPathRecord
{
    public uint StartSegment;  public uint SegmentCount;
    public float MinX;         public float MinY;
    public float MaxX;         public float MaxY;
    public uint Pad0;          public uint Pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
public struct GpuPathSegment
{
    public Vector2 P0;         public Vector2 P1;
    public Vector2 P2;         public Vector2 P3;
    public uint SegmentType;   public uint Pad0;
    public uint Pad1;          public uint Pad2;
}
```

#### Analytical Non-Zero Winding Number WGSL Shaders
The rasterizer counts curve intersections analytically using a horizontal ray casting winding-number algorithm directly in WGSL:

- **Line Intersection**: Evaluates linear roots analytically:
  $$t = \frac{p_y - A_y}{B_y - A_y}$$
- **Quadratic Bezier Intersection**: Solves quadratic equation $(1-t)^2 A_y + 2(1-t)t B_y + t^2 C_y - p_y = 0$ for $t \in [0, 1]$. Valid intersections are checked against the ray, and winding adjustments are updated based on the tangent derivative:
  $$P'_y(t) = 2(1-t)(B_y - A_y) + 2t(C_y - B_y)$$
- **Cubic Bezier Intersection**: Expands the cubic Bezier equation into $a t^3 + b t^2 + c t + d = 0$. The compute shader executes Cardano's formula (`solve_cubic` helper in WGSL) to find up to 3 real roots, updating the winding number according to the cubic tangent derivative:
  $$P'_y(t) = 3 a t^2 + 2 b t + c$$

#### Performance Enhancements & Quality Correctness
- **CPU Path Cache (`_pathGeometryCache`)**: Compiled segment arrays and pre-calculated local bounds are cached for each unique `PathGeometry`. Dynamic frames skip CPU figures traversal, and copy segment spans directly, reducing CPU path compilation times to **0.30ms** for 100,000 shapes.
- **Pixel-Level Bounding Box Shader Skip**: To eliminate GPU rasterization bottlenecks, the fine-rasterization pixel loop performs a screen-space bounding box check:
  ```wgsl
  if (px < inst.screenMinX || px > inst.screenMaxX || py < inst.screenMinY || py > inst.screenMaxY) {
      continue;
  }
  ```
  Pixels outside the shape boundaries immediately bypass local coordinate transforms, 4-sample subpixel loops, and expensive winding calculations. This discards ~95% of active operations per pixel, resulting in a **15x** rendering speedup.
- **4x SSAA Quality Correctness**: Replaced screen coordinates with transformed local coordinates in the Sample 2 containment checks of the `PathRasterizerShader`. This ensures that under high multisampling/supersampling, anti-aliased edge pixels align perfectly, delivering sharp, hardware-accurate vector strokes and fills.

---

### 9. High-Quality Anti-Aliasing & Expanded-Quad Render Padding

Standard Signed Distance Field (SDF) rendering often clips the outer half of strokes or the edges of anti-aliasing gradients because the generated quad boundaries are drawn *exactly* at the shape's mathematical dimensions. This limits pixel operations outside the bounding box, resulting in a rough, aliased border. 

To achieve state-of-the-art vector quality with zero performance degradation, we implemented a dual-stage quad inflation and pixel-distance anti-aliasing framework:

- **Separated-Pass Quad Expansion**: During shape compilation in `Compositor.cs`, drawing of Rectangles, Ellipses, and Rounded Rectangles is divided into independent Brush (fill) and Pen (stroke) passes.
  - **Fill Pass (Brush)**: Inflates bounding quad vertices and `texCoord` offset variables outwards by a padding of `1.5` pixels.
  - **Stroke Pass (Pen)**: Inflates bounding quad vertices and `texCoord` offsets by `thickness / 2.0 + 1.5` pixels.
  This expansion guarantees that the outer half of a stroke of width $T$, as well as its smooth anti-aliasing gradient, are fully rendered without quad boundary clipping.
- **Pixel-Distance WGSL Stroke Anti-Aliasing**: For GPU-expanded Lines, Quadratic Beziers, Cubic Beziers, and elliptical Arcs, the vertex shader computes the exact signed pixel distance from the center spline to the expanded vertex boundaries, passing it to the fragment shader via `gridIndex`. The fragment shader evaluates anti-aliasing dynamically using:
  ```wgsl
  let d_pixels = abs(input.gridIndex);
  let d_shape = d_pixels - input.strokeThickness * 0.5;
  shapeAlpha = 1.0 - smoothstep(-0.5, 0.5, d_shape);
  ```
  This calculates a crisp, subpixel-accurate smoothstep edge transition directly in screen-space pixel coordinates, eliminating aliased jagged edges on all lines and splines.

---

### 10. High-Performance Theming, Styling & Templating Engine

ProGPU implements a lightweight, high-performance, and memory-safe theming, styling, and templating engine designed to emulate the logical capabilities of WinUI 3 but operating with minimal CPU and memory overhead.

```mermaid
flowchart TD
    Reg["DependencyProperty.Register"] -->|Sequential Indexing| DP["Index-Based Property Mapped Arrays"]
    DP -->|Precedence Resolution| GetVal["O1 GetValue Precedence Sweep"]
    Theme["ThemeManager.ThemeChanged"] -->|Lazy Invalidation| Dirty["Set IsThemeDirty = true"]
    Dirty -->|On-Demand Query| GetVal
    
    subgraph Storage ["O(1) Parallel Contiguous Value and Theme Arrays"]
        Local["_localValues"]
        Style["_styleValues"]
        DStyle["_defaultStyleValues"]
        LocalTheme["_localThemeResources"]
        StyleTheme["_styleThemeResources"]
        DStyleTheme["_defaultStyleThemeResources"]
    end
```

#### $O(1)$ Sequential Flat-Array Property Storage
Traditional XAML frameworks store DependencyObject property values in heavy dictionaries (`Dictionary<DependencyProperty, object>`), which trigger expensive hash calculation, collisions, and lookup overhead inside tight render or layout loops.
ProGPU bypasses dictionaries entirely by introducing sequential indexing:
* **Sequential Indexing**: Every registered `DependencyProperty` is assigned a unique, sequential, zero-based `Index` from a thread-safe static list during bootstrap.
* **Direct Array Access**: `DependencyObject` stores properties in a set of parallel contiguous flat arrays (`_localValues`, `_styleValues`, `_defaultStyleValues`, `_effectiveValues`, and `_valueSources`) matching the index sizes.
* **Precedence Resolution**: Property value resolution (`GetValue(dp)`) is simplified to direct index checks on these arrays in $O(1)$ time, resolving values via native priority precedence:
  $$\text{Local} \succ \text{Style} \succ \text{Default Style} \succ \text{Inherited} \succ \text{Default}$$

#### Lazy, Invalidation-Tracked Dynamic Theming
Eagerly traversing and updating dynamic brushes across the entire visual tree on every theme change triggers substantial CPU frame stutters. ProGPU bypasses this via a lazy evaluation pipeline:
* **Visual Tree Invalidation**: When a theme toggle is triggered, `ThemeManager.ThemeChanged` fires. The system recursively propagates a cheap `IsThemeDirty = true` flag down the scene graph (`NotifyThemeChanged`), avoiding immediate value updates.
* **Parallel Flat Theme Mappings**: Dynamic references are stored in parallel arrays (`_localThemeResources`, `_styleThemeResources`, and `_defaultStyleThemeResources`). During subsequent property reads (`GetValue(dp)`), if the dirty flag is set, the system sweeps these parallel arrays, re-evaluates active key lookups against the theme palette, and rebuilds only the affected elements' effective values in a single sequential linear pass.

#### Reflection-Free, Weak Callback Template Bindings
To support lightweight control customization without the heavy reflection, expression compilation, or string-matching of traditional bindings:
* **Index-Based Callbacks**: `DependencyObject` maintains an index-sequential list of callbacks registered via `RegisterPropertyChangedCallback(dp, callback)`. 
* **WinUI-Compliant Tokens**: Registration returns a unique `long` token, allowing surgical unregistration via `UnregisterPropertyChangedCallback(dp, token)`.
* **Weak, Self-Cleaning Template Binding**: `TemplateBinding` coordinates bindings between controls and template roots using weak references (`WeakReference<DependencyObject>`). On every callback trigger, if it detects that the target control has been garbage-collected, the binding automatically unregisters itself from the source object, completely preventing memory leaks.

#### Decoupled Multi-Window & Popup Inspector
To support robust diagnostic capabilities:
* **Multi-Window Visual Inspector**: Refactored the `DevTools` visual tree population (`RefreshVisualTree`) to dynamically traverse all active windows registered in `WindowManager.ActiveWindows` (filtering out the inspector itself), and automatically falling back to the thread-static `InputSystem.Root` for raw Silk.NET window bindings.
* **Popup & Dialog Hierarchies**: Merges active floating popups and dialogs from `PopupService.ActivePopups` as a dedicated branch in the visual tree, making overlay dialogs fully inspectable.
* **Global Invalidation Hub**: Replaced thread-local repaints with a public `InvalidateAllMainWindows()` hub in `DevToolsService`, ensuring hover overlays, inspection borders, and property changes instantly refresh across all active window compositors.

---

### 11. High-Fidelity GPU Text & Retina Rendering (macOS High-DPI Quality)

Traditional GPU engines suffer from low-resolution stretch blurriness on macOS high-DPI (Retina) screens because they configure the SwapChain to match logical coordinates, letting the operating system scale the output. ProGPU achieves true macOS Retina rendering quality while maintaining high performance through four main pillars:

* **Physical-Pixel Backing Store SwapChain**: The WebGPU swapchain and render pipelines are driven directly by the window's physical `FramebufferSize` instead of logical size (e.g. `2560x1600` instead of `1280x800`). This aligns all vector and rasterization outputs exactly 1:1 with hardware pixels, eliminating OS-level linear stretching blur.
* **DPI-Aware Physical Glyph Caching**: Computes the high-DPI scaling factor dynamically (`dpiScale = FramebufferSize.X / Size.X`) and pre-rasterizes glyphs in the `GlyphAtlas` at their **actual physical pixel font size** (`cmd.FontSize * dpiScale`), ensuring that the atlas contains the high-resolution 2x textures.
* **4x Physical Subpixel Snapping**: Snippets the screen-transformed baseline cursor position to physical device pixels (`transPos * dpiScale`) and snaps the horizontal coordinate to the nearest 1/4th *physical* pixel, completely eliminating subpixel blur on the screen.
* **Retina Snap-Back logical mapping**: Snapped physical coordinates of the drawing quad are divided by `dpiScale` before writing them to the vertex buffer, mapping them back to logical space for the compositor's orthographic projection matrix. The GPU hardware then renders the logical quad exactly 1-to-1 with screen physical pixels!
* **Direction-Aware Winding Curve Crossing Corrections**: Replaced the static, direction-agnostic interval checks in both the quadratic and cubic Bezier crossing solvers with **Precise Direction-Aware Half-Open Winding Intervals** based on the vertical derivative sign (`deriv_y`):
  * **Upward Crossing (`deriv_y > 0.0`)**: Valid range is `[0.0, 1.0)` (inclusive of start, exclusive of end).
  * **Downward Crossing (`deriv_y < 0.0`)**: Valid range is `(0.0, 1.0]` (exclusive of start, inclusive of end).
  This eliminates boundary vertex double-counting and zero-counting across all transition types (line-to-curve, curve-to-line, curve-to-curve) in both `GlyphRasterizer` and `PathRasterizer` shaders, completely preventing horizontal seam and drop-out artifacts at curve joins (such as on letters like `G`/`g`).

#### Text Compilation Fast Path and Rich Text Command Reuse

Text-heavy pages avoid repeated work without changing raster quality:

* `TextLayout` stores the resolved `GlyphIndex` beside each positioned glyph. The compositor uses that index directly instead of repeating character-map lookup during every compile.
* `TtfFont` resolves `HasColorGlyphs` and `HasBitmapGlyphs` once after parsing the table directory. Normal outline fonts therefore avoid per-glyph COLR/CPAL/SVG/bitmap probes, while fonts that contain those tables still use the full color or bitmap path.
* DPI/raster size, transform scale, rotation state, basis vectors, synthetic-bold parameters, and Skia font stretch/shear are computed once per text command or glyph run rather than once per glyph. Explicit positions remain in shaped logical coordinates; only glyph-local outlines or atlas quads are transformed, and vector cache keys include the local stretch and shear.
* `RichTextBlock` retains its generated drawing commands until layout, theme, selection, or hyperlink-hover state changes. Markdown and document pages can replay stable text/table commands instead of reconstructing them on every render.

---

### 12. Layered High-DPI Visual Caching (CacheAsLayer)

In high-performance GPU-bound UI frameworks, recursively traversing large, static visual subtrees (such as complex sidebar menus, navigation drawers, and presentation panels) every frame at double physical coordinates (`FramebufferSize`) on macOS Retina screens incurs heavy CPU-to-GPU overhead (layout traversal, vertex mesh generation, matrix multiplications, draw call issuance, and constant buffer uploads).

ProGPU introduces **Layered High-DPI Visual Caching** (`CacheAsLayer`) to completely eliminate redundant rendering loops for static or rarely modified subtrees:

```mermaid
flowchart TD
    Compile["CompileVisualTree node"] --> CacheChecked{"node.CacheAsLayer and Compositor.IsCacheAsLayerEnabled?"}
    CacheChecked -- No --> NormalPass["Standard Pass: Recurse Visual Subtree and Compile Primitives"]
    CacheChecked -- Yes --> DirtyCheck{"node.IsDirty or node.LayerTexture == null?"}
    
    DirtyCheck -- Yes --> RenderOff["Execute RenderOffscreen centered in node.LayerTexture"]
    RenderOff --> MarkClean["Set node.IsDirty = false"]
    MarkClean --> DrawTexture["Compile single DrawTexture command onto Swapchain"]
    
    DirtyCheck -- No --> DrawTexture
```

- **Offscreen Physical Buffering**: When `CacheAsLayer = true` is set on a static visual (like the `NavigationView`'s sidebar pane), the compositor redirects rendering of the node and its entire subtree into an isolated offscreen texture (`LayerTexture`) allocated at exact physical pixel dimensions:
  $$w = \text{logicalWidth} \cdot \text{dpiScale}, \quad h = \text{logicalHeight} \cdot \text{dpiScale}$$
- **O(1) Render Bypass**: On subsequent frames, if `node.IsDirty == false` and the cache is valid, the compositor completely skips visual tree traversal, geometry generation, and command decoding for the entire subtree. Instead, it issues exactly **1 Texture draw call** (rendering the pre-compiled `LayerTexture` back onto the swapchain), achieving an instant **1.77x rendering acceleration**.
- **Razor-Sharp Typography & 1:1 Pixel Alignment**: During offscreen rendering, the projection matrix uses logical boundaries, but text glyphs are snapped and rasterized at the physical `dpiScale` inside `CompileTextCommand`. Drawing this cached layer texture back onto the physical swapchain guarantees perfect **1:1 physical pixel alignment** and native-sharp typography on macOS Retina displays without bilinear filtering blur.
- **Lazy Dirty-State Propagation**: When any child element inside the cached subtree changes (e.g. hovered, clicked, or typed into), invalidation sets `IsDirty = true` and bubbles up to the cached parent node. The compositor automatically detects this dirty state on the next frame, re-runs `RenderOffscreen` to update the cache in a single frame, and marks it clean again.
- **Global Settings Switch**: The caching system can be enabled or disabled completely at runtime globally:
  - **Individual Control**: `Visual.CacheAsLayer = true;`
  - **Global Override**: `Compositor.IsCacheAsLayerEnabled = true / false;` (Toggleable via the Application Settings panel).

---

### 13. Dynamic Z-Ordered Draw Call Batching

In retained scene graphs with interleaved primitive types (such as vector geometries, offscreen computer-generated textures, and rich text visual elements), simple bulk-draw grouping causes Z-order overlap bugs. If all textures or all texts are batched and drawn at the very end of layer compilation, solid backgrounds or overlay vectors can draw on top of pre-rendered textures, resulting in black or empty areas.

ProGPU implements a **Dynamic Z-Ordered Draw Call Batching** mechanism within `Compositor.cs` to achieve optimal batching performance while strictly preserving visual Z-order:
- **Pending Batch Tracking**: Instead of immediate submission, consecutive vector shape and text draw commands are accumulated into contiguous ranges tracked via `_pendingVectorStart` and `_pendingTextStart` pointers.
- **Ordered Flush Commits (`CommitPendingDrawCalls`)**: Whenever a boundary-crossing operation is encountered (such as an offscreen compiled texture draw call or layer bounds transition), the compositor flushes accumulated vector and text batches using `CommitPendingDrawCalls()`. This groups consecutive visual primitives into single drawing calls while guaranteeing they are submitted to the GPU command encoder in the exact Z-order depth traversed by the visual tree.
- **Zero-Allocation Dynamic Offsets**: The batched ranges directly index into GPU-mapped vertex and index backing buffers, avoiding CPU copy operations and preserving near-native rendering speeds.

#### Retained lattice and nine-patch batching

Skia-compatible lattice and nine-patch image draws use the same ordered texture path without multiplying submission overhead:

- The CPU iterator follows Skia's alternating fixed/scalable segment model. With enough destination space, fixed source segments keep their pixel length and scalable segments divide the remainder proportionally. When the destination is smaller than all fixed segments, scalable segments collapse to zero and fixed segments shrink proportionally.
- A call records one `RenderCommand` containing one contiguous `TexturePatch[]`. Transparent and collapsed cells are removed during layout. Fixed-color cells retain their filtered RGBA value beside ordinary source/destination texture cells.
- `CompileTextureCommand` reserves one contiguous vertex/index range, emits four vertices and six indices per visible cell, and creates one `CompositorDrawCall`. Fixed-color cells use the same texture pipeline with a flat per-vertex discriminator, so they avoid texture sampling without causing a pipeline switch or an extra draw.
- For X and Y div counts and C visible cells, layout costs `O(X + Y + C)` time and storage, compositor expansion costs `O(C)`, and GPU submission remains one draw call per lattice operation. This prevents a conventional 9-patch from becoming nine retained commands or nine GPU submissions.

#### Retained vertex meshes

`SKVertices` and `DrawingContext.DrawVertexMesh` provide the matching batched path for arbitrary colored triangle meshes:

- Positions, optional texture coordinates, vertex colors, and 16-bit indices are copied once into an immutable `VertexMesh2D`. Triangle lists, strips, and fans retain their original topology instead of being converted into one retained path per face.
- The compositor transforms each vertex once, normalizes valid faces into one contiguous index range, and leaves invalid indexed faces out of the final count. The entire mesh remains part of the surrounding vector batch and therefore does not add one draw call per triangle.
- Vertex colors travel premultiplied and are combined with the paint brush by the vector shader. The WGSL path implements Skia's Porter-Duff, arithmetic, separable, and non-separable vertex-color blend modes before the ordinary mask and framebuffer blend stages.
- GPU hit testing traverses the same normalized triangles. For V vertices, I input indices, and T output triangles, retained construction costs `O(V + I)` time and storage, compilation costs `O(V + T)`, and hit-index construction costs `O(T)`.

Coons patches use this same mesh path. The tessellator evaluates clockwise top, right, reversed-bottom, and reversed-left cubic boundaries, combines the two ruled surfaces, and subtracts their bilinear corner surface. Device-space boundary lengths select a grid at roughly one partition per 10 pixels with an 8x8 minimum; extreme patches are proportionally limited to 60,000 indices. Corner colors interpolate in premultiplied space and texture coordinates interpolate bilinearly. An `Lx` by `Ly` patch costs `O(Lx * Ly)` CPU time/storage but still records one command and enters one vector batch.

#### Transformed sprite atlases

`SKCanvas.DrawAtlas` reuses the retained texture-patch batch for sprite-heavy scenes:

- Each non-empty source rectangle becomes one patch with an `SKRotationScaleMatrix` converted to a sprite-local scale/rotation/translation. Bounds are accumulated from the four transformed corners; an optional caller cull rectangle remains a conservative quick-reject/hit bound and does not clip individual sprites.
- The whole atlas call retains one patch array, one copied texture resource, one sampler choice, and one indexed texture draw. Nearest, linear, mipmapped, custom cubic, and anisotropic sampling metadata stays uniform across the sprite batch.
- Optional sprite colors are premultiplied once in vertex data. The texture shader treats the sampled sprite as source and the color as destination, implements all Skia blend modes, and then applies paint opacity, masks, the selected texture alpha representation, and the framebuffer blend mode.
- For S visible sprites, layout, bounds, and vertex/index generation cost `O(S)` time and storage. GPU submission remains one draw rather than S texture draws and does not create per-sprite bind groups.

---

### 14. Zero-Allocation Vector Drawing & Skia-like GpuPicture Caching

High-performance vector rendering loops are highly sensitive to Garbage Collection (GC) pressure. Passing coordinate arrays (such as `Vector2[]` for complex polylines, curves, or CAD structures) on every frame forces heap allocation and copying, resulting in massive GC thrashing. 

ProGPU completely eliminates this overhead by introducing a zero-allocation vector drawing engine driven by `ReadOnlySpan<T>` and a Skia-like `GpuPicture` command caching architecture:

```mermaid
flowchart TD
    subgraph AllocPool ["Zero-Allocation Frame Draw (Pooling)"]
        DrawCall["DrawPolyline(Pen, ReadOnlySpan<Vector2> points)"] --> GetPool["Acquire continuous PointBuffer from DrawingContext"]
        GetPool --> CopySpan["Copy points data in bulk using high-speed Span.CopyTo"]
        CopySpan --> RecordCmd["Record RenderCommand with PointBufferOffset and PointBufferCount"]
    end

    subgraph CacheSystem ["Pre-Recorded Caching Loop (GpuPicture)"]
        RecStart["GpuPictureRecorder.BeginRecording(bounds)"] --> RecDraw["Record vector commands into local buffers once"]
        RecDraw --> RecEnd["EndRecording() compiles into immutable GpuPicture"]
        RecEnd --> DrawCache["context.DrawPicture(picture, cameraViewMatrix)"]
        DrawCache --> CompositorPlay["Compositor compiles and plays back directly in-place (Zero-Copy)"]
    end
```

#### Pre-Allocated Continuous Memory Pools
Since `ReadOnlySpan<T>` is a stack-only `ref struct`, it cannot be stored on the heap or inside standard lists. To allow zero-allocation span-based rendering, `DrawingContext` maintains internal pre-allocated continuous memory lists:
* `PointBuffer` (`List<Vector2>`)
* `DoubleBuffer` (`List<double>`)
* `Line3DBuffer` (`List<Line3D>`)
* `FloatBuffer` (`List<float>`)

On every frame refresh, calling `.Clear()` on these buffers resets their logical `Count` to `0` but **retains their internal backing array capacity**. Drawing coordinates are copied into these pre-allocated pools using high-speed bulk `Span<T>.CopyTo` operations. As long as capacity is sufficient, frame-by-frame rendering runs at near-native speed with **absolutely zero heap allocations**.

#### Unified `IRenderDataProvider` Interface
To support both real-time dynamic rendering (where coordinates live in the active `DrawingContext` pools) and cached playback (where coordinates live in static arrays), we introduce the `IRenderDataProvider` interface:
```csharp
public interface IRenderDataProvider
{
    ReadOnlySpan<Vector2> GetPoints(int offset, int count);
    ReadOnlySpan<double> GetDoubles(int offset, int count);
    ReadOnlySpan<Line3D> GetLines3D(int offset, int count);
    ReadOnlySpan<float> GetFloats(int offset, int count);
}
```
Both `DrawingContext` and `GpuPicture` implement `IRenderDataProvider`. Inside WebGPU mesh compilation, the compositor queries coordinate spans directly from the active provider using the offsets and counts recorded in the `RenderCommand`.

#### Skia-like `GpuPicture` and `GpuPictureRecorder`
* **Recording**: Call `GpuPictureRecorder.BeginRecording(bounds)` to retrieve a recording `DrawingContext`. Commands are recorded normally using the zero-allocation span APIs. Call `recorder.EndRecording()` to compile the active lists into an immutable `GpuPicture` object (which allocates static arrays *only once* during compile time).
* **Playback**: Render a pre-recorded picture via `context.DrawPicture(picture)` or apply dynamic camera transitions in GPU-space via `context.DrawPicture(picture, cameraViewMatrix)`.
* **Zero-Copy Playback**: At the compositor level, when a `DrawPicture` command is encountered, it recursively plays back the pre-compiled picture commands directly in-place using the picture itself as the `IRenderDataProvider`, completely avoiding CPU copying or allocation during rendering.

#### Core API Specification

##### 1. High-Performance Zero-Allocation Span Signatures
```csharp
// Draws polylines or polygon outlines directly from stack memory
public void DrawPolyline(Pen pen, ReadOnlySpan<Vector2> points, bool isClosed = false);

// Draws quadratic or cubic B-Spline curves
public void DrawSpline(Pen pen, ReadOnlySpan<Vector2> controlPoints, ReadOnlySpan<double> knots, int degree);

// Draws rational, weighted NURBS curves
public void DrawSpline(Pen pen, ReadOnlySpan<Vector2> controlPoints, ReadOnlySpan<double> knots, ReadOnlySpan<double> weights, int degree, bool isClosed);

// Draws 3D ACIS solids or wireframe boundaries
public void DrawAcisSolid(Pen pen, ReadOnlySpan<Line3D> edges, Matrix4x4 modelTransform);

// Hardware-accelerated dynamic chart line series
public void DrawGpuLineSeries(ReadOnlySpan<float> interleavedCoords, int pointsCount, float thickness, Brush brush);

// Hardware-accelerated dynamic chart scatter series
public void DrawGpuScatterSeries(ReadOnlySpan<float> interleavedCoords, int pointsCount, float radius, Brush brush);
```

##### 2. Backward-Compatible Array-Based Signatures (WinUI Parity)
Wraps standard heap-allocated arrays into `ReadOnlySpan<T>` using `new ReadOnlySpan<T>(array)` and forwards to the high-performance pipeline. Assigns legacy fields (`SplineWeights`, `Edges3D`) on the created `RenderCommand` structures to preserve 100% test compatibility and visual tree diagnostics:
```csharp
public void DrawPolyline(Pen pen, Vector2[] points, bool isClosed = false);
public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, int degree);
public void DrawSpline(Pen pen, Vector2[] controlPoints, double[] knots, double[]? weights, int degree, bool isClosed);
public void DrawAcisSolid(Pen pen, List<Line3D> edges, Matrix4x4 modelTransform);
```

---

### 15. WinUI-Style Cooperating Scroll Virtualization

High-performance viewport virtualization is highly sensitive to coordinate math re-calculation and z-order sorting. To guarantee flawless macOS Retina-quality scrollbar overlay Z-order depth, precise boundary clipping, and locked 60 FPS scrolling speeds, ProGPU implements a **WinUI-Style Cooperating Scroll Virtualization** architecture:

```mermaid
flowchart TD
    subgraph Parent ["ItemsControl (Templated Control)"]
        Border["Border (Chrome Background)"] --> ScrollViewer["ScrollViewer (Viewport Clipping)"]
    end

    subgraph Child ["VirtualizingPanel (Cooperating Child)"]
        Panel["UniformVirtualizingGridPanel / VirtualizingStackPanel"]
    end

    ScrollViewer -->|Hosts Panel inside Content| Panel
    Panel -->|Traverses Visual Tree| ParentQuery{"Parent ScrollViewer found?"}
    ParentQuery -- Yes --> Cooperate["Cooperating Mode: Dynamic Offset Bindings"]
    ParentQuery -- No --> Standalone["Standalone Mode: Fallback ScrollBarOverlay child"]

    Cooperate -->|MeasurePass: DesiredSize.Y = TotalVirtualHeight| ScrollViewer
    ScrollViewer -->|Updates scrollbars and sets VerticalOffset| Cooperate
    ScrollViewer -->|Physically translates panel by -VerticalOffset| Panel
    Cooperate -->|UpdateViewport: Render cells at absolute position row*ItemHeight| Panel
```

#### Dual-Mode Sizing & Viewport Cooperation
* **Cooperating Mode**: When hosted inside a parent `ScrollViewer`, `VirtualizingPanel` dynamically traverses up the visual parent chain (`ScrollViewerOwner`) to establish a direct binding link:
  * **Unified Offsets**: Reading and writing `ScrollOffset` binds directly to `ScrollViewer.VerticalOffset`.
  * **Adaptive Viewport**: The layout viewport bounds (`ViewportWidth` / `ViewportHeight`) scale automatically with the parent `ScrollViewer` window boundaries.
  * **Extent Reporting**: During the measure pass (`MeasureOverride`), the panel computes the total height of all items (`TotalVirtualHeight`) and returns it as its desired size. This informs the `ScrollViewer` of the total scroll extent, sizing the capsule scrollbar perfectly.
  * **Z-Order Supremacy**: The panel's local scrollbar overlay visual is removed, allowing the `ScrollViewer` to draw its native glassmorphic capsule scrollbar in its own `OnRender` pass. Because the scrollbar is rendered *after* all visual children (including the panel and its cell cards) are painted, the scrollbar remains perfectly on top of all item cards and intercepts clicks first.
* **Standalone Mode**: If a `ScrollViewer` is not found, the panel falls back to Standalone Mode, drawing its own internal `ScrollBarOverlay` child visual and intercepting pointer wheel events directly, ensuring full backward compatibility.

#### Absolute Coordinate Mapping (Anti-Drift)
To eliminate floating-point coordinate drift and keep layout compilation cycles fast:
* In cooperating mode, the `ScrollViewer` physically translates its `Content` container by `-_verticalOffset` and `-_horizontalOffset` during the arrange pass.
* The virtualizing panel detects this physical shift and places the active visible cell visuals at their **absolute virtual coordinate coordinates** (e.g., `row * ItemHeight` for grids or `i * ItemHeight` for stack panels) relative to the panel, letting the parent graphics pipeline translate them onto the screen. This reduces layout calculations to simple, zero-copy integer multiplication.

---


### 16. Hardware-Accelerated Static DXF Rendering & Crisp Static Text Buffers

CAD drawings (like DXF files) contain hundreds of thousands or millions of vector elements (lines, circles, polyline arcs, splines, and complex hatches). Recursively compiling these vector primitives from a dynamic visual tree every frame on camera changes (zoom/pan) is CPU-prohibitive.

ProGPU introduces **Hardware-Accelerated Static WebGPU Buffers** (Option B) which compiles all vector primitives once into a static, GPU-mapped vertex/index store (`DxfStaticBuffer`). Panning and zooming are executed entirely on the GPU via updates to the viewport uniforms, maintaining a locked 60+ FPS on massive, million-entity CAD models.

#### The Blurry Text Dilemma
While static geometry scales infinitely on the GPU, TrueType Font (TTF) text is drawn as textured quads pointing to a bitmap-cached `GlyphAtlas`. Zooming in stretches these pre-rendered quads, causing bilinear texture blur because the glyph atlas texture was rasterized at a static zoom scale.

ProGPU resolves this by implementing **Crisp Static Text Buffers via Dynamic Re-compilation**:

```mermaid
flowchart TD
    ZoomChange{"Context.Zoom != _lastZoom?"}
    ZoomChange -- No --> DrawStatic["Draw Static Dxf Buffer - 100% GPU Bound (Panning Free)"]
    ZoomChange -- Yes --> Recompile["Trigger RecompileStaticText on CPU"]
    
    Recompile --> ScaleDPI["Scale effective dpiScale = _currentDpiScale * Context.Zoom"]
    ScaleDPI --> RasterGlyph["Rasterize Glyph at physical FontSize * dpiScale * Zoom inside Atlas"]
    RasterGlyph --> ModelSpace["Divide quad vertex coords by effective dpiScale (cancel out Zoom)"]
    ModelSpace --> WriteGPU["Dynamic Copy-on-Write vertex/index re-upload to GpuBuffer"]
    WriteGPU --> DrawStatic
```

* **Panning is Completely Free**: Since panning does not affect font size or rasterization dimensions, panning a static drawing remains 100% GPU-bound and runs with zero CPU overhead.
* **Retina-Sharp Snapping**: On camera zoom changes, the compositor triggers a surgical, sub-millisecond re-compilation of ONLY the text commands using the new zoom factor:
  $$\text{effectiveDpiScale} = \text{dpiScale} \cdot \text{Zoom}$$
* **Glyph Sizing**: Glyphs are rasterized into the shared `GlyphAtlas` at their exact, high-resolution physical size (`FontSize * effectiveDpiScale`), ensuring pixel-perfect Retina snapping.
* **Automatic Scaling Cancelation**: The compiled quad vertex positions ($v_0, v_1, v_2, v_3$) are divided by `effectiveDpiScale` to map them back to base model/world coordinates. When the vertex shader multiplies them by the custom model-to-screen MVP matrix (which scales by `Zoom`), the zoom factor is mathematically canceled out, mapping the quad 1-to-1 to physical screen pixels with zero texture stretching or blur!

#### High-Performance Zoom & Scaling Optimizations
To support instantaneous zoom transitions on massive CAD models containing thousands of text elements (such as `Schemat IOS Karvina CZ.dxf`), ProGPU integrates three advanced graphics-pipeline optimizations:

1. **$O(\text{TextCount})$ Pre-Filtered Text Records Cache**:
   - *Problem*: Scanning millions of drawing commands recursively on the CPU during zoomed snapping steps to filter out text elements introduced noticeable interface stutters.
   - *Solution*: During the initial compilation of the static buffer, the compositor captures the exact `DrawText` commands and their parent block transformations into a flat `TextRecords` array in the `DxfStaticBuffer`:
     ```csharp
     public struct StaticTextRecord
     {
         public RenderCommand Command;
         public Matrix4x4 Transform;
     }
     ```
     Subsequent snapped zoom changes bypass the drawing hierarchy entirely and recompile only the text records, reducing complexity from $O(\text{TotalElements})$ to a highly efficient $O(\text{TextElements})$ execution.

2. **Discrete Font Snapping & Quad Scaling**:
   - *Problem*: As the camera zoom levels increase, font sizes become extremely large (up to 128f), which rapidly bloats and thrashes the shared `GlyphAtlas` texture ($2048 \times 2048$), triggering frequent cache evictions. Computing 4-way subpixel snap coordinates for huge fonts also increases memory area consumption by $4\times$.
   - *Solution*:
     - **Clamping**: Caps the maximum physical font size rasterized into the atlas to `64f` (instead of `128f`). GPU bilinear filtering scales these large high-resolution sources up without visual quality loss, using $4\times$ less atlas area.
     - **Size Snapping**: Snaps `rasterFontSize` to discrete steps (0.5px steps below 24px, 2px steps above 24px) for perfect cache hit ratios. Quad quad boundaries are scaled proportionally by `scaleRatio = physicalFontSize / rasterFontSize` to ensure mathematical size precision on screen remains 100% exact.
     - **Subpixel Bypassing**: Disables subpixel snapping for font sizes larger than `24f` (since subpixel shifts are visually imperceptible on large characters), saving an additional $4\times$ in atlas footprint.

3. **WebGPU Queue & Driver Submission Batching**:
   - *Problem*: Previously, rasterizing each new glyph synchronously created a temporary uniform buffer, constructed a WebGPU bind group, instantiated a command encoder, and immediately executed a sequential queue submission (`QueueSubmit`). For drawings with thousands of characters, this sequential driver loop caused severe CPU/GPU Metal synchronization bottlenecks on macOS.
   - *Solution*: Implemented nestable batching APIs (`BeginBatch` / `EndBatch`) in `GlyphAtlas.cs` to pool and combine glyph compute dispatches. A normal scene records all new glyphs into one `CommandEncoder` and executes one `QueueSubmit`. The 256 KB uniform ring stores 1,024 256-byte-aligned dispatch records; exceptionally large batches flush before wrap and continue with a fresh encoder, so an unsubmitted dispatch can never observe overwritten uniforms. Submission complexity is $O(\lceil G / 1024 \rceil)$ for $G$ new glyphs, while raster work remains $O(P)$ for $P$ covered glyph pixels.

4. **Stable Atlas Coordinates and Capacity Fallback**:
   - *Problem*: Clearing a full atlas during compilation relocates UVs that earlier text vertices and static buffers still reference. Clearing proactively near capacity can also turn every changing frame into a full glyph re-rasterization cycle.
   - *Solution*: Atlas allocation is transactional: a failed shelf placement does not mutate packing state, cached coordinates, or `Generation`. Existing glyphs remain reusable and the new glyph is rendered from its outline through the high-quality vector path. This avoids missing letters and cache thrash while preserving the same geometry and coverage policy. Capacity probing is O(1); an uncached fallback is O(S + P), where S is outline segment count and P is covered path pixels.

---

### 17. Batched Uniform Storage (Glyph & Path Atlases)

To eliminate one temporary GPU buffer per rasterized item, `GlyphAtlas` uses a fixed aligned ring while `PathAtlas` packs each pending batch into shared uniform, record, and segment buffers:
* **Single Bulk Pre-allocation**: Allocates a `256KB` glyph uniform `GpuBuffer` once at startup. At WebGPU's 256-byte binding alignment this stores 1,024 dispatch records. Path batches use shared packed storage/record/segment uploads sized to their pending work rather than one buffer per path.
* **256-Byte Alignment Compliance**: Follows the WebGPU standard (`minUniformBufferOffsetAlignment` boundary constraint of 256 bytes) by rounding up structural uniform offsets with a fast bitwise operation:
  $$\text{alignedSize} = (\text{SizeOf<Uniforms>} + 255) \& \sim 255$$
* **Fast Queue Copy-on-Write**: Inside glyph batch rasterization, parameters are written directly to the pre-allocated ring buffer at the current `_ringOffset` using `QueueWriteBuffer`, avoiding one buffer allocation per glyph:
  ```csharp
  _context.Wgpu.QueueWriteBuffer(_context.Queue, _uniformRingBuffer.BufferPtr, _ringOffset, &uniforms, (uint)Marshal.SizeOf<GlyphUniforms>());
  ```
* **Binding Slice Offsets**: Dynamic bind groups point to exact ring slices using `Offset = _ringOffset` and `Size = Marshal.SizeOf<Uniforms>()`. The batch is submitted before the next aligned slice would wrap, then continues from offset zero in a new encoder. Normal scenes still require one submission and the hot loop creates no temporary uniform buffers.
* **Generation-Tracked Reuse**: `GlyphAtlas.Generation` changes only on an explicit clear, and `PathAtlas.Generation` changes on clear or repack. The compiled-scene cache records both values so it never reuses UVs after atlas contents move. Glyph capacity exhaustion preserves coordinates and therefore does not increment the generation.
* **Capacity-Safe Path Reservation**: Frame reservation first proves that the requested entries can fit in an empty atlas. An impossible high-DPI reservation is ignored instead of resetting the atlas every frame, preserving static path reuse and avoiding repeated compute rasterization.

---

### 18. Double-Buffered Geometry Swapchains (DxfStaticBuffer)

Updating dense vector meshes and text quads during snapped zoom events can cause severe CPU-GPU hardware execution stalls. If the CPU disposes and recreates vertex/index buffers while the GPU command queue is actively reading from them, the graphics driver is forced to block CPU execution to synchronize hardware lifecycles.

To prevent these stalls and achieve perfectly fluid rendering, we implemented a **Double-Buffering Swapchain** pattern:
* **Asynchronous Back-Buffering**: Maintains dual buffer sets in `DxfStaticBuffer`:
  - Front-Buffers (`TextVertexBuffer`, `TextIndexBuffer`, `TextIndexCount`) currently being drawn by the compositor.
  - Back-Buffers (`_textVertexBufferBack`, `_textIndexBufferBack`, `_textIndexCountBack`) dedicated to accommodating the next camera layout recalculation.
* **Non-Blocking Dynamic Copy**: When `UpdateTextBuffer` is invoked during snapped zooms, it resizes and writes to the back-buffers asynchronously.
* **Zero-Allocation Swapping**: Swaps the front and back buffer references instantly using cheap variable re-assignment on the CPU:
  ```csharp
  var tempVertexBuffer = TextVertexBuffer;
  TextVertexBuffer = _textVertexBufferBack;
  _textVertexBufferBack = tempVertexBuffer;
  ```
* **Static Bind-Group Stability**: Because vertex and index buffer mappings are bound directly via render encoder draw commands rather than static composition bind groups, swapping front/back buffers bypasses bind-group recreation or layout invalidations entirely, ensuring **stutter-free, instant zoom actions**.

---

### 19. Snapped Blur Radii & Stable Effect Pipelines

Offscreen Gaussian blur and drop shadow dispatches are highly sensitive to parameter fluctuations during keyframe animations or hover transitions. Smooth float radius adjustments (e.g. transitioning from `1.0f` to `3.0f`) dynamically modify the computed iteration count:
$$\text{iterations} = \text{Clamp}(\text{Round}(\text{radius} / 2.5), 1, 8)$$
This causes the rendering loop to alter command-buffer layouts and recreate dynamic bind groups frame-by-frame, creating noticeable micro-stutters.

To stabilize effect execution, we implemented a **Snapped Radii Pipeline**:
* **Discrete Increments**: Symmetrically snaps incoming `radius` and `blurRadius` parameters to discrete `0.5f` pixel boundaries at the entry points of `ApplyGaussianBlur` and `ApplyDropShadow`:
  ```csharp
  float snappedRadius = MathF.Round(radius * 2f) / 2f;
  ```
* **Pipeline and Bind-Group Lock**: Snapping ensures that the computed iteration count remains perfectly locked and stable during intermediate keyframes. WGSL shader binding entries, textures, and command layouts remain identical across frame transitions, delivering extremely fluid hover animations and eliminating transient render delays.

---


## Module & Project Architecture Breakdown

The ProGPU solution is partitioned into modular, highly specialized C# projects. Each project governs a specific layer of the UI, vector, or graphics compilation loops:

| Project | Assembly Name | Core Architectural Responsibility | Key Components & Classes |
| :--- | :--- | :--- | :--- |
| **`ProGPU.Backend`** | `ProGPU.Backend.dll` | Low-level hardware infrastructure and WebGPU swapchain orchestration. | `WgpuContext`, `Window`, `Shaders`, `RenderPipelineCache` |
| **`ProGPU.Compute`** | `ProGPU.Compute.dll` | Orchestration of WebGPU GPGPU compute pipelines and parallel filter dispatches. | `ComputeAccelerator`, `ComputeShaders` |
| **`ProGPU.Vector`** | `ProGPU.Vector.dll` | Mathematical primitives, Bezier models, path segment parsing, and atlas mapping. | `PathGeometry`, `PathFigure`, `GpuPathSegment`, `PathAtlas` |
| **`ProGPU.Text`** | `ProGPU.Text.dll` | TrueType parsing, retained glyph identity, word wrapping, line layout, and generation-tracked glyph atlas storage. | `TtfFont`, `GlyphAtlas`, `TextLayout`, `TextRunGlyph` |
| **`ProGPU.Scene`** | `ProGPU.Scene.dll` | Retained visual tree, compiled-scene validation, ordered draw-list/GPU-buffer compilation, optional GPU hit testing, and effects. | `Compositor`, `CompositorOptions`, `CompositorMetrics`, `ContainerVisual`, `DrawingVisual` |
| **`ProGPU.Layout`** | `ProGPU.Layout.dll` | XAML-compatible sizing negotiation lifecycle (`Measure` / `Arrange`) and layout panels. | `LayoutNode`, `StackPanel`, `GridPanel`, `CanvasPanel` |
| **`ProGPU.WinUI`** | `ProGPU.WinUI.dll` | Interactive controls, CPU input/hit testing, frame-phase instrumentation, and command-cached rich documents. | `Window`, `WindowFrameMetrics`, `RichTextBlock`, `ScrollViewer`, `SplitView` |
| **`ProGPU.Virtualization`** | `ProGPU.Virtualization.dll` | Dynamic scrolling viewport orchestration and UI virtualization controllers. | `VirtualizingPanel`, `ViewportInfo` |
| **`ProGPU.Samples`** | `ProGPU.Samples.dll` | Showcase bootstrap, bounded UI scheduling, animation drivers, diagnostics, and repeatable stress/performance workloads. | `MainWindowController`, `SamplePerformanceBenchmark`, `LolsPage`, `UIThread` |

---

## WebGPU WGSL Shader Specifications & Implementations

ProGPU routes all graphics and compute tasks directly to the GPU using specialized WGSL (WebGPU Shading Language) shaders. The following sections detail their purpose, execution pipelines, and exact implementations.

### 1. VectorShader (Rasterization Graphics Pipeline)
- **Role**: Primary graphics pipeline shader for standard UI rendering. Responsible for rasterizing vector shapes (rectangles, ellipses, rounded rectangles) and evaluating Bezier curves and elliptical arcs on the GPU.
- **Why It is Used**: Avoids uploading dense pre-tessellated mesh structures. Instead, it utilizes cheap mathematical Signed Distance Fields (SDFs) and GPU vertex expansion to draw vector primitives with zero CPU overhead.
- **Implementation Mechanics**:
  - **GPU Stroke Expansion & Miter Scaling (`sType == 3u`)**: Expands lines dynamically in the vertex shader. Computes normal vectors ($miterN$) at segment junctions, scales them by $1/\cos(\theta)$ ($miterScale$), and offsets vertices to form precise, variable-thickness miter joints. Passes the signed pixel distance from the center line to the fragment shader via `gridIndex` for zero-cost edge anti-aliasing.
  - **Dynamic Bezier Evaluation (`sType == 5u & 6u`)**: Replaces CPU Bezier flattening. For Quadratics and Cubics, the vertex shader interpolates coordinates directly based on the thread's `vertexIndex` and parametric factor $t \in [0, 1]$, calculating curve positions and tangents to offset vertices outward along normal vectors on the fly, storing signed pixel distances in `gridIndex`.
  - **Dynamic Arc Evaluation (`sType == 11u`)**: Replaces CPU arc flattening for valid path strokes. The compositor sends the transformed ellipse center plus two axis vectors, and the vertex shader evaluates arc positions and tangents parametrically before stroke expansion.
  - **Analytical SDF Fragment Evaluation (`sType < 3u`)**: Computes Signed Distance Fields for Rectangles, Ellipses, and Rounded Rectangles. Anti-aliases boundaries dynamically using screen-space partial derivatives:
    $$\text{fw} = \max(\text{fwidth}(d), 0.0001)$$
    $$\alpha = 1.0 - \text{smoothstep}(-0.5 \cdot \text{fw}, 0.5 \cdot \text{fw}, d)$$
  - **Pixel-Distance Stroke Anti-Aliasing (`sType == 3u \|\| 5u \|\| 6u \|\| 11u`)**: Resolves aliasing for lines, curves, and arcs by evaluating screen-space smoothstep transitions using the interpolated `gridIndex` pixel distance to the stroke boundary:
    $$d_{\text{shape}} = \text{abs}(\text{gridIndex}) - \text{strokeThickness} \cdot 0.5$$
    $$\alpha = 1.0 - \text{smoothstep}(-0.5, 0.5, d_{\text{shape}})$$
  - **Gradient Interpolation**: Evaluates Linear (`brushType == 1u`) and Radial (`brushType == 2u`) gradients dynamically for up to 4 stop colors by calculating projection coordinates and interpolating between bounds using stop offsets.
  - **Batched vertex meshes (`sType == 18u`)**: Interpolates premultiplied vertex colors across triangle lists, strips, and fans, then combines that color with the evaluated paint brush using the requested Skia blend mode. One mesh contributes one retained command and one contiguous vector index range rather than one command per triangle.

### 2. TextureShader (Image, lattice, and sampling pipeline)
- **Role**: Draws ordinary image quads, batched lattice/nine-patch cells, and transformed sprite atlases through one indexed texture pipeline.
- **Why It is Used**: Preserves image Z-order while keeping each retained image, lattice, or atlas operation to one GPU draw, regardless of patch or sprite count.
- **Implementation Mechanics**:
  - Normal cells interpolate source UVs and use nearest, linear, mipmapped, or a bounded 4x4 Mitchell-Netravali cubic sample footprint.
  - Lattice fixed-color cells carry a flat `patchKind` discriminator and return their vertex RGBA directly, performing no image sample. Separate straight and premultiplied encodings preserve blend correctness for both texture alpha modes.
  - Atlas cells carry a flat color-blend mode and paint-opacity value. Sampled sprite color is the source and premultiplied per-sprite color is the destination; bounded Porter-Duff, separable, and non-separable functions match Skia's atlas blend ordering.
  - Every fragment samples the active opacity mask once. Premultiplied image and fixed-color paths scale RGB with coverage; straight-alpha paths retain straight RGB and scale alpha.
  - CPU layout and vertex generation are `O(C)` for C visible cells, fragment work is `O(1)`, and one indexed draw stores four vertices and six indices per cell.

### 3. TextShader (SDF Glyph Render Pipeline)
- **Role**: Specialized graphics shader for high-speed, sharp text display.
- **Why It is Used**: Traditional text rasterization blurs heavily under scaling. The TextShader samples high-precision SDF textures and applies dilation offsets and power-based sharpness filters to ensure text remains crisp at any display size or zoom level.
- **Implementation Mechanics**:
  - Samples the single-channel glyph atlas: `let alpha = textureSample(atlasTexture, atlasSampler, input.texCoord).r;`
  - Applies a dilation scale based on the requested stroke thickness: `let dilated = clamp(alpha * input.strokeThickness, 0.0, 1.0);`
  - Filters sharpness using a power curve driven by the corner radius: `let finalAlpha = pow(dilated, input.cornerRadius);`

### 4. GlyphRasterizerShader (GPGPU Analytical Glyph Rasterizer)
- **Role**: WebGPU compute shader tasked with pre-rasterizing vector glyph outlines into the glyph atlas texture.
- **Why It is Used**: Bypasses slow CPU-based glyph rasterizers entirely, using parallel GPU threads to rasterize outlines directly on the GPU.
- **Implementation Mechanics**:
  - Operates on a $16 \times 16$ thread group.
  - Calculates intersections using a 16x supersampled (SSAA) analytical winding-number raycaster.
  - Solves quadratic equations directly inside the WGSL shader (`solve_quadratic`) to evaluate Bezier curve boundaries, updating winding directions according to the curve's vertical tangent derivative.
  - Writes the calculated coverage mask directly to the storage texture: `textureStore(atlasTexture, writeCoord, vec4<f32>(coverage, 0.0, 0.0, 0.0));`

### 5. PathRasterizerShader (GPGPU Analytical Vector Path Rasterizer)
- **Role**: Advanced WebGPU compute shader that computes analytical non-zero winding fills for arbitrary paths.
- **Why It is Used**: Bypasses CPU segment flattening and triangulation completely, allowing the GPU to raycast complex Bezier geometry directly.
- **Implementation Mechanics**:
  - Computes intersections of horizontal rays with Line, Quadratic Bezier, and Cubic Bezier segments.
  - Features an analytical Cardano's formula solver (`solve_cubic` inside WGSL) to evaluate cubic Bezier roots:
    $$p = b - \frac{a^2}{3}, \quad q = c - \frac{ab}{3} + \frac{2a^3}{27}, \quad D = \frac{q^2}{4} + \frac{p^3}{27}$$
    If $D \leq 0$, it extracts up to 3 real roots using trigonometric cosine angles, updating the winding number according to the tangent derivative $P'_y(t) = 3 a t^2 + 2 b t + c$.
  - Executes 4-point supersampling (SSAA) using subpixel sampling coordinate offsets (`+0.25`, `+0.75`) in local space (`fp2`), achieving hardware-accurate anti-aliased edge coverage.

### 6. GaussianBlur (Horizontal & Vertical Compute Filters)
- **Role**: Parallel compute shaders for high-performance backdrop and glass blurs.
- **Why It is Used**: Bypasses slow pixel shader convolution passes by executing parallel thread blocks directly on texture buffers.
- **Implementation Mechanics**:
  - Operates in two consecutive passes (Horizontal, then Vertical) to split rendering complexity from $O(K^2)$ to $O(K)$ instructions per pixel.
  - Executes an unrolled 5-tap Gaussian kernel using hardcoded weights to avoid memory fetch latency:
    $$\text{color} = 0.0625 \cdot T[-2] + 0.25 \cdot T[-1] + 0.375 \cdot T[0] + 0.25 \cdot T[1] + 0.0625 \cdot T[2]$$
  - Clamps texture coordinate bounds inside `textureLoad` to eliminate edge bleed artifacts.

### 7. DropShadow (Ambient Shadow & Neon Glow Compute Filter)
- **Role**: WebGPU compute shader calculating soft drop shadows and glowing neon halos for layout elements.
- **Why It is Used**: Evaluates dynamic blurring and translation offsets over element boundaries in a single dispatch pass.
- **Implementation Mechanics**:
  - Operates on a $16 \times 16$ thread block.
  - Takes a `Params` uniform block specifying translating `offset`, shadow `color`, and `blurRadius`.
  - Loops over a sliding window of size `[-blurRadius, blurRadius]`.
  - Extracts the source offscreen texture's alpha channel, averages the coverage, and outputs the shifted, blurred, and color-multiplied mask back to the destination buffer:
    $$\text{shadowColor} = \vec{C}_{\text{params}} \cdot (A_{\text{sum}} / \text{count})$$

---

## Development & Diagnostic Tools

ProGPU includes rendering diagnostics and a repeatable in-process frame benchmark:

### 1. TrueType Font Outline Diagnostic Tool (`TtfDiag`)
Located in `tools/TtfDiag/`, this is a generic console tool designed to inspect outline structures, endpoint coordinates, and control points of TrueType fonts. It is especially useful for diagnosing text rendering quality, drop-out artifacts, or glyph parsing inconsistencies.

* **Usage**:
  ```bash
  # Run using the system's Arial font (supplemental) fallback to inspect specific glyphs (e.g. 'G' and 'g')
  dotnet run --project tools/TtfDiag -- Arial Gg

  # Run with an absolute path to a custom font and custom character sequence
  dotnet run --project tools/TtfDiag -- /System/Library/Fonts/Supplemental/Georgia.ttf ABC
  ```
* **Output**: Dumps the exact TrueType outline geometry, closed/filled figure status, segment types (Lines/Quadratic Beziers), and precise coordinates using standard invariant decimal formatting.

### 2. DXF Vector CAD Diagnostic Tool (`DxfDiag`)
Located in `tools/DxfDiag/`, this is a standalone command-line utility to inspect DXF vector files. It lists all available layouts and layers, prints active layout geometric bounds, recursive block hierarchies, nested insert attributes (tags/values), and detects coordinate outliers exceeding absolute limits ($> 1,000,000$). The complete diagnostic trace is saved to `outliers.txt` in the local directory.

* **Usage**:
  ```bash
  # Run on a target DXF drawing file to inspect the default active space layout
  dotnet run --project tools/DxfDiag -- <path-to-dxf-file>

  # Run on a target DXF drawing file and explicitly target a specific layout space (e.g. 'A0')
  dotnet run --project tools/DxfDiag -- <path-to-dxf-file> --layout A0
  ```
* **Output**: Generates a detailed audit of entity counts, viewport settings, block trees, and coordinates, saving the report to `outliers.txt` and logging a summary to the console.

### 3. Sample Frame Benchmark

`SamplePerformanceBenchmark` is disabled during normal sample use and activates only when `PROGPU_SAMPLE_BENCHMARK_PAGE` is set. It selects the requested page, applies the requested VSync mode, warms the renderer, measures a fixed frame count, prints one `[SampleBenchmark] RESULT` line, and closes the app.

The result separates host layout/animation/surface phases from compositor compile/upload/render phases and includes allocated bytes per frame, cache hits and miss reason, draw/vertex counts, and LOL/s workload counters. Use it for before/after comparisons on the same machine, configuration, window state, and page. Do not compare a VSync-limited result with an uncapped run.

---

## Platform Integration & Host Control Embedding (Avalonia & Uno Platform)

ProGPU is designed to act as an embedded high-performance graphics substrate inside standard host XAML frameworks. We provide native integration packages for both **Avalonia** (`ProGPU.Avalonia`) and **Uno Platform** (`ProGPU.Uno`), allowing developers to overlay low-allocation WebGPU rendering canvases directly inside standard desktop applications.

### 1. Hybrid Rendering Architecture

The integration layer hosts a headless, offscreen `WgpuContext` and `Compositor` instance inside a custom control subclass (`Control` in Avalonia, `ContentControl` in Uno). WebGPU renders all visual tree and CAD vectors offscreen, which are then blitted directly to the host's screen.

```mermaid
graph TD
    subgraph UIThread ["Host UI Thread (Input & Sizing)"]
        Size[Sizing Negotiation: Measure & Arrange] --> Input[Pointer Event Capture & Translation]
    end
    
    subgraph GPUThread ["GPU & WebGPU Staging Loop"]
        Input -->|InputSystem.Inject| WG[WebGPU Core Offscreen Render]
        Size -->|Logical Bounds| WG
        WG -->|CommandEncoderCopy| ST[Staging Buffer VRAM]
        ST -->|Sync MapRead| MP[Mapped CPU Pointer]
        MP -->|Direct Pointer Blit| WB[WriteableBitmap 96 DPI]
        WB -->|Invalidate / DrawImage| SCR[High-DPI Retina Screen]
    end
```

---

### 2. High-Performance Direct Bitmap Blitting Pipeline

Due to standard platform-agnostic FFI limitations in `wgpu-native`, raw `WGPUTexture` pointers cannot be shared directly with the compositor's graphics context (Metal/D3D) as `IOSurfaceRef` or `id<MTLTexture>` handles without writing custom native Rust/C++ bridging wrappers. 

To bypass these FFI opaque struct constraints and deliver **100% stable, platform-independent rendering**, ProGPU implements a highly optimized **Direct Bitmap Blitting pipeline**:

*   **Aligned GPU Staging Buffers**: WebGPU allocates a staging buffer backed by `BufferUsage.MapRead | BufferUsage.CopyDst`. The row pitch (`BytesPerRow`) is aligned to the nearest **256 bytes** per WebGPU specifications to satisfy FFI layout requirements:
    $$\text{BytesPerRow} = (\text{width} \cdot \text{bytesPerPixel} + 255) \ \& \ \sim 255$$
*   **Synchronous MapRead Polling**: Each frame, a command encoder executes `CopyTextureToBuffer` from the offscreen target to the staging buffer. The buffer is mapped via `BufferMapAsync`, and the UI thread polls `wgpuDevicePoll` in a light spin loop until mapping completes.
*   **Direct Row Pointer Blitting**: Once mapped, the raw VRAM memory address is extracted. The control performs a high-speed pointer-based copy utilizing native **`System.Buffer.MemoryCopy`** straight into the locked buffer address of the host's high-DPI `WriteableBitmap`:
    ```csharp
    using (var locked = _writeableBitmap.Lock())
    {
        byte* srcBytes = (byte*)mappedPtr;
        byte* dstBytes = (byte*)locked.Address;
        uint rowBytes = _renderWidth * bytesPerPixel;
        
        for (uint y = 0; y < _renderHeight; y++)
        {
            byte* srcRow = srcBytes + (y * _bytesPerRow);
            byte* dstRow = dstBytes + (y * (uint)locked.RowBytes);
            System.Buffer.MemoryCopy(srcRow, dstRow, rowBytes, rowBytes);
        }
    }
    ```
    This row-by-row blitting executes in microseconds on the CPU, achieving near-zero visual overhead and bypassing bilinear filtering blur.

---

### 3. High-DPI Retina Calibration & Anti-Double-Scaling

On macOS Retina displays (e.g. `DpiScale = 2.0`), standard platform-specific graphics renderers often apply the display's scaling factor twice when drawing a high-DPI bitmap, blowing up the layout and creating blurry graphics.

ProGPU resolves this double-scaling bug through strict physical-to-logical coordination:
*   **96 DPI Isolation**: The host `WriteableBitmap` is instantiated at a constant **96 DPI** (`new Vector(96, 96)`), making its logical size match its physical size.
*   **Logical-Bounds Offscreen Rendering**: Viewport dimensions passed to `Compositor.RenderOffscreen` are strictly mapped in **logical coordinates**, while the internal WebGPU pipeline multiplies them by `DpiScale` to align the physical viewport.
*   **Clean Down-Scaling**: During the draw pass, the physical staging bitmap is scaled down into the host control's logical bounds using a standard 1-to-1 stretch layout (`Stretch.Fill` in Uno, `context.DrawImage` in Avalonia). The physical pixels map precisely 1:1 with screen hardware coordinates, yielding absolute razor-sharp text and graphics.

---

### 4. Symmetrical Input Routing & Event Translation

The integration libraries bridge the event-handling loop symmetrically:
*   **Coordinate Translation**: Pointer event handlers (`OnPointerMoved`, `OnPointerPressed`, etc.) intercept native positions, translate them into logical `Vector2` boundaries, and route them to ProGPU's input engine:
    ```csharp
    InputSystem.InjectMouseMove(new Vector2((float)pos.X, (float)pos.Y));
    ```
*   **Input State Invalidation**: Input events mark the active WinUI input state dirty, forcing immediate layouts hit-testing and scheduling dynamic repaint requests to update hover overlays and cursors instantly.

---

### 5. locked High-Refresh Rate VSync Loops (120 FPS+)

To allow embedded graphics and animation benches to run at their physical display limit, standard timer loops are replaced by self-scheduling graphics dispatchers:
*   **Avalonia**: Hooks directly into the system's VSync loop using:
    ```csharp
    TopLevel.RequestAnimationFrame(OnAnimationTick);
    ```
    This self-scheduling tick fires callbacks exactly aligned with the physical monitor's refresh rate, unlocking **120 FPS / 144 FPS** rendering without frame tearing.
*   **Uno Platform**: Subscribes directly to `CompositionTarget.Rendering` to drive the WebGPU command submissions and refresh statistics exactly aligned with each compositor pass.


---

## III. Path 2: Zero-Copy Shared Texture Rendering Pipeline

To bypass the overhead of copying pixels from VRAM to CPU staging buffers and back to VRAM (double-copy blitting), ProGPU implements a cutting-edge **Zero-Copy Shared Texture Rendering Pipeline**. This architecture achieves direct GPU-to-GPU memory sharing between the offscreen WebGPU rendering engine and the host UI composition tree.

```mermaid
sequenceDiagram
    participant WebGPU as WebGPU Engine
    participant OS as OS Shared Resource (IOSurface / D3D11)
    participant Avalonia as Avalonia Compositor Tree
    participant GPU as physical GPU VRAM

    WebGPU->>OS: 1. Render directly to Shared Handle (Zero CPU Copy)
    OS->>GPU: 2. Texture contents persist in VRAM
    Avalonia->>OS: 3. Import Shared Handle via ICompositionGpuInterop
    Avalonia->>GPU: 4. Draw directly from VRAM (Zero Copy / 120 FPS+)
```

### 1. Architectural Overview & Memory Sharing Mechanics
The Zero-Copy pipeline eliminates host CPU copies entirely by allocating a hardware-backed shared OS memory handle directly in C#, wrapping it inside WebGPU as a render target, and importing it into the host visual tree:

| Operating System | Shared Resource Type | Native Handle Reference | Allocation Strategy |
| :--- | :--- | :--- | :--- |
| **macOS** | Apple `IOSurface` | `IOSurfaceRef` (global handle) | CoreFoundation/AppKit unmanaged dictionary creation |
| **Windows** | Direct3D11 Shared Texture | DXGI `HANDLE` (global shared key) | Standalone `ID3D11Device` with `D3D11_RESOURCE_MISC_SHARED` |

### 2. C# Hardware-Backed Allocation Details

#### A. macOS IOSurface Allocation
CoreFoundation and Objective-C runtime P/Invokes are used to construct the surface configuration plist:
*   `IOSurfaceWidth` & `IOSurfaceHeight`: Target dimensions.
*   `IOSurfaceBytesPerElement`: 4 bytes per pixel.
*   `IOSurfacePixelFormat`: `'BGRA'` (packed 32-bit integer `1111970369`).
*   `IOSurfaceBytesPerRow`: Aligned to 256 bytes.
*   `IOSurfaceAllocSize`: Total byte size.

#### B. Windows D3D11 Shared Handle Allocation
Direct COM VTable indexing is utilized to create resources dynamically:
*   `D3D11CreateDevice`: Instantiates a standalone hardware D3D11 device.
*   `CreateTexture2D`: Allocates the texture with `D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE` bind flags and the `D3D11_RESOURCE_MISC_SHARED` misc flag.
*   `QueryInterface`: Extracts the `IDXGIResource` COM pointer.
*   `GetSharedHandle`: Obtains the global shared handle pointer.

### 3. Integrating with Avalonia's `ICompositionGpuInterop`
The host control hooks into Avalonia's composition engine during initialization:
1.  **Query Interop Interface**:
    ```csharp
    var interop = await compositor.TryGetCompositionGpuInterop();
    ```
2.  **Verify Compatibility**:
    Verify that the compositor's graphics backend supports the active platform's handle type (`IOSurfaceRef` on macOS, `D3D11TextureGlobalSharedHandle` on Windows).
3.  **Import Image**:
    Create a `PlatformHandle` from the allocated raw pointer and import it:
    ```csharp
    var platformHandle = new PlatformHandle(_sharedHandle, _gpuHandleType);
    _importedGpuImage = _gpuInterop.ImportImage(platformHandle, properties);
    ```
4.  **Present via Composition Surface**:
    Create a standard `CompositionSurfaceVisual` and assign its `Surface` to a `CompositionDrawingSurface`. On every tick, simply call:
    ```csharp
    _ = _drawingSurface.UpdateAsync(_importedGpuImage);
    ```
    This triggers a hardware-accelerated present, drawing the shared texture directly in the compositor loop without CPU copying.

### 4. The WebGPU FFI Bridge Boundary (Native Integration)
Standard cross-platform `wgpu-native` bindings do not export helper functions out-of-the-box to wrap arbitrary `IOSurfaceRef` or shared `ID3D11Texture2D` handles into WebGPU texture objects. 
To complete the zero-copy pipeline on the WebGPU side, a small custom native wrapper (written in Rust or C++) must bridge the HAL (Hardware Abstraction Layer) boundary:

```rust
// Custom native Rust crate bridging wgpu-core and OS handles
use wgpu_core::hub::Global;
use wgpu_hal::api::{Metal, Dx12};

#[no_mangle]
pub unsafe extern "C" fn wgpuDeviceCreateTextureFromMacIOSurface(
    device_ptr: *mut libc::c_void,
    iosurface_ptr: *mut libc::c_void,
    width: u32,
    height: u32
) -> *mut libc::c_void {
    let global = &*Global::default();
    // 1. Extract raw device representation
    let device_id = std::mem::transmute(device_ptr);
    
    // 2. Fetch the Metal device and wrap the IOSurface handle via wgpu_hal
    let surface: Metal::Texture = Metal::texture_from_raw(iosurface_ptr as *mut _);
    
    // 3. Register the newly created texture inside the wgpu-core context
    let texture_id = global.device_create_texture_from_hal::<Metal>(
        device_id,
        surface,
        width,
        height
    );
    
    std::mem::transmute(texture_id)
}
```

This bridge allows WebGPU command encoders to bind the texture as a standard `RenderPassColorAttachment`, completing the zero-copy pipeline.

### 5. Asynchronous Double-Buffered Update & Polling Architecture

To achieve VSync-locked rendering (120 FPS+) and completely eliminate UI-thread blocking or frame flickering, ProGPU utilizes a high-performance **Asynchronous Double-Buffered Update Loop** driven by a **Dedicated Background Device Polling Thread**.

This architecture guarantees 0% CPU blocking on the main UI thread and prevents read-write VRAM conflicts between the renderer and the host compositor.

```mermaid
sequenceDiagram
    participant UI as UI Thread (RenderFrameAsync)
    participant BG as Background Polling Thread
    participant WGPU as WebGPU Device / Queue
    participant Swap as SwapchainImage (Double Buffered)
    participant Comp as Avalonia Compositor Thread

    UI->>WGPU: 1. Render scene offscreen to WgpuTexture (Image A)
    UI->>WGPU: 2. Queue CopyTextureToStagingBuffer
    UI->>WGPU: 3. Invoke MapBufferAsync (non-blocking Task)
    Note over UI,BG: UI thread yields control immediately
    Loop Continuous Polling
        BG->>WGPU: 4. wgpuDevicePoll(Device, false) every 2ms
    End
    WGPU-->>BG: 5. Mapping complete! Trigger MapCallback
    BG-->>UI: 6. Complete TaskCompletionSource (Resume UI)
    UI->>Swap: 7. CopyMappedToSharedTexture (MemoryCopy / UpdateSubresource)
    UI->>WGPU: 8. BufferUnmap
    UI->>Comp: 9. UpdateAsync (Swapchain Image A)
    Note over UI,Comp: Image A is now bound to Compositor. Swap to Image B.
```

#### A. Double-Buffered Swapchain Image Model (`SwapchainImage`)
A dedicated `SwapchainImage` class encapsulates the graphics assets for a single frame. The host control manages a pool of two swapchain images (`SwapchainImage[2]`):
*   **Compositor Frame Lock**: One image is locked by the Avalonia compositor for current presentation.
*   **Renderer Target**: The other image is being written to asynchronously by the WebGPU rendering loop.
*   **Role Swap**: Once rendering and memory copies are completed, the roles are swapped in an alternating cycle: `_currentWriteImageIndex = (_currentWriteImageIndex + 1) % 2`.

```csharp
private class SwapchainImage : IDisposable
{
    public IntPtr SharedHandle;
    public ICompositionImportedGpuImage? ImportedImage;
    public GpuTexture? WgpuTexture;
    public IntPtr StagingBuffer;
    public uint StagingBufferSize;
    public uint BytesPerRow;

    // Windows Specific Direct3D 11 Resources
    public IntPtr WinD3DDevice;
    public IntPtr WinTexture2D;
}
```

#### B. Continuous Background Device Polling Thread
WebGPU asynchronous operations (such as staging buffer mapping) require the device queue event loop to be polled via `wgpuDevicePoll`.
To keep the UI and Avalonia render threads completely unblocked, ProGPU runs a continuous, low-latency background polling thread that executes `wgpuDevicePoll` every 2 milliseconds:

```csharp
private void StartPolling()
{
    _pollingThread = new Thread(() => {
        while (!_pollingCts.Token.IsCancellationRequested) {
            wgpuDevicePoll(_wgpuContext.Device, false, null);
            Thread.Sleep(2);
        }
    }) { IsBackground = true, Name = "ProGpuDevicePolling" };
    _pollingThread.Start();
}
```

#### C. Asynchronous Non-Blocking Map Pipeline
The buffer mapping callback is wrapped in a standard C# `TaskCompletionSource<bool>`. Calling `await MapBufferAsync(...)` suspends the rendering task without blocking any CPU execution context. The background polling thread completes the mapping asynchronously, waking up the rendering task instantly:

```csharp
private Task MapBufferAsync(IntPtr buffer, MapMode mode, nuint size)
{
    unsafe {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(tcs);
        var userData = (void*)GCHandle.ToIntPtr(handle);
        _wgpuContext.Wgpu.BufferMapAsync((GpuBuffer*)buffer, mode, 0, size, s_mapCallback, userData);
        return tcs.Task;
    }
}
```

#### D. Safe Pointer-Unsafe Segregation
To comply with the C# compiler constraints that prohibit `await` operations inside `unsafe` contexts, ProGPU segregates low-level pointer copying into two dedicated synchronous `unsafe` helper functions:
1.  **`CopyTextureToStagingBuffer`**: Encodes the offscreen render-target texture copy to the staging buffer and submits the command buffer.
2.  **`CopyMappedToSharedTexture`**: Retrieves the staging buffer's mapped range, locks the native OS texture, copies raw bytes row-by-row, unlocks the texture, and unmaps the buffer.

```csharp
// macOS row-by-row IOSurface memory copy
GpuSharingInterop.IOSurfaceLock(image.SharedHandle, 0, null);
void* destPtr = GpuSharingInterop.IOSurfaceGetBaseAddress(image.SharedHandle);
System.Buffer.MemoryCopy(srcRow, destRow, rowBytes, rowBytes);
GpuSharingInterop.IOSurfaceUnlock(image.SharedHandle, 0, null);

// Windows D3D11 UpdateSubresource call via COM VTable index 49
GpuSharingInterop.COMHelper.CallUpdateSubresource(context, image.WinTexture2D, 0, IntPtr.Zero, mappedPtr, image.BytesPerRow, 0);
```

### 6. Graceful Runtime Fallback
If graphics interop is not supported by the environment (e.g. software rendering, missing drivers, or Linux configurations lacking Vulkan opaque handles), the control gracefully falls back to the **Decoupled Render-Thread Blitting Pipeline** (Phase 2). This ensures 100% functionality and visual parity across all host configurations!
