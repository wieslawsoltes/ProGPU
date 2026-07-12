# Agent Guidelines & Reference Handbook (agents.md)

Welcome, agent! This document serves as a specialized developer guide and architectural handbook for AI coding agents pair-programming on the **ProGPU** codebase. Read this document carefully to align with our established design patterns, mathematical conventions, and diagnostic tools.

---

## 1. Core Architectural Rules & Conventions

### A0. Reflection-Free WPF Port Support
When adding ProGPU APIs for the WPF port, keep hot paths typed and source-integrated. Runtime reflection is allowed only for diagnostics, compatibility probes, or transitional adapters with a documented removal path; rendering, text, image upload, clipping, hit testing, shader effects, DirectX shims, cache metadata, and platform services should be implemented as reusable ProGPU/Silk.NET primitives or neutral DTO contracts instead of WPF bridge workarounds.

Cross-assembly WPF bridge contracts must not expose shim-owned WPF structs or classes when package-mode apps load the real WPF transport assemblies. Prefer primitive values, package-neutral DTOs, typed registrars, and source-integrated WPF interfaces such as the portable geometry, brush, pen, effect, bitmap-effect input, shader-effect sampler kind/image-source metadata, drawing-content, render-data, invalidation, visual-state, visual-bounds, visual-layout, and bitmap-source pixel seams.

### A. Rendering Quality & DPI-Aware Text Snapping
ProGPU achieves high-performance vector graphics and text rendering matching macOS Retina quality. When modifying or extending text visuals (`TextVisual.cs`, `Compositor.cs`, `GlyphAtlas.cs`):
* **Framebacks**: Always back the swapchain with physical framebuffer pixels (`FramebufferSize`) rather than logical window coordinates to prevent OS-level linear stretching.
* **Glyph Atlas scale**: Rasterize glyphs into the atlas at their actual physical dimensions by applying the current `DpiScale`.
* **4-Way Subpixel Snapping**: Snapped positions must be calculated in physical coordinates snapped to 1/4th of a physical pixel, then snap-backed (divided by `DpiScale`) for logical projection matrix orthographic drawing.

### B. Precise Winding Intersection Rules (Anti-Artifacting)
When modifying GPGPU compute rasterizers (`Shaders.cs`):
* **Scanline Boundary Crossings**: To prevent horizontal line artifacts and drop-out seams at transition vertices (e.g. line-to-curve, curve-to-line, or curve-to-curve), the ray-casting algorithm must use **Precise Direction-Aware Half-Open Winding Intervals** based on the vertical derivative sign (`deriv_y`) at the intersection point:
  * **Upward Crossing (`deriv_y > 0.0`)**: Use the half-open interval `[0.0, 1.0)` (inclusive of start, exclusive of end).
  * **Downward Crossing (`deriv_y < 0.0`)**: Use the half-open interval `(0.0, 1.0]` (exclusive of start, inclusive of end).
* Apply this rule consistently across both `GlyphRasterizerShader` and `PathRasterizerShader` for all quadratic and cubic segments to avoid boundary vertex double-counting or zero-counting.

### C. WinUI Dynamic Theming & Styling
When creating or refactoring UI controls:
* **No Static Color Brushes**: Never instantiate solid brushes statically or assign them directly via static references in layout constructors (e.g., `new SolidColorBrush(...)` or `ThemeManager.GetBrush(...)`).
* **Dynamic Brushes**: Always bind brushes dynamically using `new ThemeResourceBrush("BrushKey")`. This registers the resource with the control's theme map (`_localThemeResources` / `_styleThemeResources`) so they automatically invalidate and re-resolve against the palette during theme switches (Light/Dark).
* **Property Invalidation**: Ensure visual elements invalidate both their measure/arrange states and visual caches on theme updates to force high-DPI assets and colors to repaint.

---

## 2. Rendering Performance Regression Contract

Rendering performance and pixel quality are one contract. A change is not acceptable when it improves FPS by bypassing required invalidation, lowering raster quality, changing DPI/subpixel behavior, or silently dropping dynamic content.

### A. Preserve Compiled-Scene Correctness

When editing `Compositor`, `Visual`, atlases, effects, layers, or host frame code:

* Every mutation that can affect pixels must invalidate the owning visual and propagate `ChangeVersion` to the compiled-scene root. Never mutate retained command data behind the cache without invalidation.
* Keep cache validation sensitive to logical/physical target dimensions, viewport, DPI, glyph/path atlas generations, tooltips, external layers, and `CacheAsLayer` texture identity/dirty state.
* Increment atlas `Generation` whenever cached UV contents are cleared, moved, or repacked. Do not increment it for a no-op capacity probe.
* Do not make mutable `DrawingVisual`, masks, effects, or dynamic diagnostics cacheable until they have an explicit immutable version contract and regression tests.
* Keep `EnableCompiledSceneCache` and `EnableGpuHitTesting` independently configurable. WinUI uses CPU visual-tree hit testing and must not rebuild the duplicate GPU index by default.
* A compiled-scene hit may skip CPU compilation and uploads, but it must still execute the render pass and current clear/presentation behavior.

Required focused tests live in `LayerRenderTests` and `CompositorReviewRegressionTests`. Cover at least unchanged reuse, visual invalidation, resize/DPI/target invalidation, mutable drawing visuals, atlas reset/repack, and hit-testing option behavior when changing these contracts.

### B. Protect Hot Paths

* Never unconditionally invalidate the sample root each frame. Invalidate only the visual whose content changed.
* Do not update animations twice. `Window.RenderFrameCore` owns the core `UpdateAnimations` call; sample callbacks should update only sample-specific state.
* Keep status/diagnostic text updates rate-limited. Rebuilding `RichTextBlock` inlines every frame defeats both its command cache and whole-scene reuse.
* Preserve `TextRunGlyph.GlyphIndex`; do not repeat character-map lookup during compositor compilation. Cache font table capability flags and hoist transform, raster-size, basis, and bold invariants out of glyph loops.
* Preserve the allocation-free double-queue dispatcher. Do not copy the pending queue into a new `List<Action>` per frame, execute newly posted work recursively in the same drain, or enqueue one delegate per benchmark element.
* Keep producer backpressure bounded. The LOL/s workload batches element mutations and limits pending work separately for VSync and uncapped runs so background production cannot starve rendering.
* Before reserving atlas capacity, prove that the requested entries fit in an empty atlas. An impossible reservation must not reset a useful atlas every frame.

### C. Required Performance and Quality Gates

Run the full renderer gates from the ProGPU repository root:

```bash
dotnet test src/ProGPU.Tests/ProGPU.Tests.csproj -c Release
dotnet test src/ProGPU.Tests.Headless/ProGPU.Tests.Headless.csproj -c Release
```

The current baseline is 1,145 renderer tests and 149 headless tests. Update these counts only when tests are intentionally added or removed.

Build once, then measure the exact final binaries:

```bash
dotnet build src/ProGPU.Samples/ProGPU.Samples.csproj -c Release

PROGPU_SAMPLE_BENCHMARK_PAGE='LOL/s Benchmark' \
PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES=240 \
PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES=480 \
PROGPU_SAMPLE_BENCHMARK_VSYNC=true \
dotnet run --project src/ProGPU.Samples/ProGPU.Samples.csproj -c Release --no-build

PROGPU_SAMPLE_BENCHMARK_PAGE='LOL/s Benchmark' \
PROGPU_SAMPLE_BENCHMARK_WARMUP_FRAMES=300 \
PROGPU_SAMPLE_BENCHMARK_MEASURE_FRAMES=600 \
PROGPU_SAMPLE_BENCHMARK_VSYNC=false \
dotnet run --project src/ProGPU.Samples/ProGPU.Samples.csproj -c Release --no-build
```

On the current 120 Hz reference machine, the protected targets are about 120 FPS and 12,000 LOL/s with VSync, and at least 36,000 LOL/s uncapped. The current measured values are 11,996 and 42,463 LOL/s. Compare on the same machine and window state; investigate a repeatable drop greater than 10% before merging.

Also run `Markdown Playground` and `DXF CAD Viewer` uncapped. Stable pages should miss once and then report nearly all `sceneCacheHits`; animated pages and LOL/s should report a useful miss reason rather than a false hit.

Validate Svg.Skia from its repository with `UseProGpuSkiaSharpShim=true`:

```bash
dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release \
  -p:UseProGpuSkiaSharpShim=true -p:ProGpuSourceRoot=/absolute/path/to/ProGPU \
  --filter 'FullyQualifiedName~resvgTests'

dotnet test tests/Svg.Skia.UnitTests/Svg.Skia.UnitTests.csproj -f net10.0 -c Release --no-restore \
  -p:UseProGpuSkiaSharpShim=true -p:ProGpuSourceRoot=/absolute/path/to/ProGPU \
  --filter 'FullyQualifiedName!~W3CTestSuiteTests&FullyQualifiedName!~resvgTests'
```

The current shim baseline is 927 resvg passes with 37 explicit inventory skips and 1,147 remaining passes. The W3C image lane has 59 established threshold differences, 464 passes, and 3 skips versus native SkiaSharp's 523 passes and 3 skips. Performance-only work must not add a fixture, increase an image error, or change a previously matching result. Compare the exact fixture/error list against the parent commit; intentional parity improvements should reduce the difference set and include their own image-focused review.

Record benchmark result lines and test totals in the commit or task summary. Do not claim a performance fix from subjective interaction alone.

---

## 3. Text Outline Diagnostic Tool (`TtfDiag`)

To debug text rendering quality, glyph geometry errors, or parsing inconsistencies, we maintain a generic command-line outline extractor tool under `tools/TtfDiag/`.

### How It Works
The tool loads any standard TrueType Font (`.ttf`) file, decodes its index tables, parses the outline contours using `TtfFont` parsing loops, and dumps the exact figure coordinates, segment types (Lines/Quadratic Beziers), endpoint coordinates, and control points in clean `CultureInfo.InvariantCulture` formatting.

### How to Run
Execute the tool from the root directory using the `dotnet` CLI:
```bash
# Run with macOS system Arial fallback to inspect specific glyphs (e.g., 'G' and 'g')
dotnet run --project tools/TtfDiag -- Arial Gg

# Run with an absolute path to a custom font and custom character sequence
dotnet run --project tools/TtfDiag -- /System/Library/Fonts/Supplemental/Georgia.ttf ABC
```

### Typical Output Format
Use the precise printed segments to check if coordinate coordinates contain anomalies, bounds issues, or flat segment joints:
```text
[TtfDiag] Loading font: /System/Library/Fonts/Supplemental/Arial.ttf
[TtfDiag] Units per Em: 2048, Total Glyphs: 3381

================================================================================
Glyph: 'G' | Unicode: U+0047 | Glyph Index: 42
================================================================================
Figures count: 1
  Figure 0: StartPoint = (844, 575), Closed = True, Filled = True
    Segments count: 29
      Segment 00: [Line]  -> End: (844, 747)
      Segment 03: [Quad]  -> End: (1170, 32.5) | Ctrl: (1322, 90)
```

Use this tool immediately whenever user reports "jagged glyph edges", "horizontal/vertical line artifacts crossing characters", or "weird transformations".
