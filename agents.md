# Agent Guidelines & Reference Handbook (agents.md)

Welcome, agent! This document serves as a specialized developer guide and architectural handbook for AI coding agents pair-programming on the **ProGPU** codebase. Read this document carefully to align with our established design patterns, mathematical conventions, and diagnostic tools.

---

## 1. Core Architectural Rules & Conventions

### A0. Reflection-Free WPF Port Support
When adding ProGPU APIs for the WPF port, keep hot paths typed and source-integrated. Runtime reflection is allowed only for diagnostics, compatibility probes, or transitional adapters with a documented removal path; rendering, text, image upload, clipping, hit testing, shader effects, DirectX shims, cache metadata, and platform services should be implemented as reusable ProGPU/Silk.NET primitives or neutral DTO contracts instead of WPF bridge workarounds.

Cross-assembly WPF bridge contracts must not expose shim-owned WPF structs or classes when package-mode apps load the real WPF transport assemblies. Prefer primitive values, package-neutral DTOs, typed registrars, and source-integrated WPF interfaces such as the portable geometry, brush, pen, drawing-content, visual-state, visual-layout, and bitmap-source pixel seams.

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

## 2. Text Outline Diagnostic Tool (`TtfDiag`)

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
