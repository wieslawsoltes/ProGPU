# HMI retained-renderer and pointer probe

This executable renders the actual HMI controls and designer through `HeadlessWindow` and the existing ProGPU compositor. It uses an installed native WebGPU backend; it does not emit illustrative mock screenshots. JSON reflection is explicitly disabled.

```sh
PROGPU_WGPU_BACKEND=vulkan dotnet run --project tests/ProGPU.Hmi.VisualSmoke/ProGPU.Hmi.VisualSmoke.csproj -c Release -- /path/to/font.ttf artifacts/hmi-visual Dark
```

Arguments are an installed font path, output directory, and optional `Light`, `Dark` or `HighContrast` palette (default Light). Run each palette in a separate process. No font is bundled by this test project. CI installs DejaVu Sans and Mesa software Vulkan. For another Vulkan installation, select its ICD through `VK_ICD_FILENAMES` as appropriate.

Outputs include component catalog, glyph atlas, quality/orientation examples, full designer, fitted runtime, and selection screenshots. Assertions cover actual glyph ink, equipment silhouette uniqueness, region bounds, visible quality differences, native transformed pointer activation and topmost overlap selection. The workflow records screenshots rather than accepting generic placeholder thumbnails or mocked data binding as rendering evidence.

These probes do not constitute physical GPU or industrial-device qualification. See [the visual studio guide](../../docs/hmi-visual-studio.md).

The route-editing probe also verifies real waypoint placement, captured drag/release, Escape and capture-loss rollback, blocked-pin recovery, and one undo transaction per drag. It saves ordered-pin and blocked-pin screenshots for the selected palette.
