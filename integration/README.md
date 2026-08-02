# Avalonia integration samples

These projects validate the ProGPU Avalonia renderer, retained compositor,
text shaper, Silk.NET windowing backend, direct Dawn presentation on Avalonia
platform windows, package replacement, and the original Skia reference
backend.

Run all commands from the repository root:

```bash
cd /Users/wieslawsoltes/GitHub/ProGPU-clean-preview27
```

The source-host scripts prepare the pinned Avalonia 12.0.5 checkout
automatically. The first invocation can therefore take longer than subsequent
runs.

## Backend matrix

The ControlCatalog profiler accepts the following backend names:

| Backend | Windowing | Rendering and compositor | Text shaping |
|---|---|---|---|
| `source-progpu` | Silk.NET/GLFW | ProGPU WebGPU/Dawn, retained scene | ProGPU OpenType |
| `source-progpu-harfbuzz` | Silk.NET/GLFW | ProGPU WebGPU/Dawn, retained scene | HarfBuzz |
| `source-progpu-native` | Avalonia Native, Win32, or X11 | Direct Dawn surface, ProGPU retained scene | ProGPU OpenType |
| `source-progpu-native-harfbuzz` | Avalonia Native, Win32, or X11 | Direct Dawn surface, ProGPU retained scene | HarfBuzz |
| `skia` | Avalonia Native, Win32, or X11 | Original Avalonia retained compositor and Skia renderer | HarfBuzz |

Strict native presentation expects these paths:

| Platform | Presentation path |
|---|---|
| macOS | `DawnMetalIOSurface` |
| Windows | `DawnD3D12HWND` |
| Linux/X11 | `DawnVulkanXlib` |

## Quick launch matrix

| Sample | Default configuration | Alternate configurations |
|---|---|---|
| `ProGPU.Samples.Avalonia` | Silk.NET + ProGPU compositor + ProGPU text | HarfBuzz, shared-image readback, copied texture fallback |
| `AvaloniaSourceRenderDemo` | Silk.NET + ProGPU compositor + ProGPU text | HarfBuzz |
| `AvaloniaSourceSandbox` | Silk.NET + ProGPU compositor + ProGPU text | HarfBuzz |
| `AvaloniaSourceControlCatalog` | Silk.NET + ProGPU compositor + ProGPU text | HarfBuzz, Avalonia platform windowing with direct Dawn |
| `AvaloniaSkiaControlCatalogReference` | Avalonia platform windowing + Skia + HarfBuzz | Page selection only |
| `ProGpuAvaloniaPackageSmoke` | Package-only Silk.NET + ProGPU + ProGPU text | Local packages, exact-identity replacement packages, NuGet.org, NativeAOT |

The `AvaloniaControlCatalogHarness` and `AvaloniaSourceSampleHost`
directories contain shared telemetry/host code and are not standalone
applications.

## Embedded ProGPU sample gallery

`ProGPU.Samples.Avalonia` embeds the high-performance ProGPU samples in an
Avalonia shell. It always uses Silk.NET windowing and the ProGPU compositor.
Select one of `Charting`, `Dxf`, `Drawing`, `MotionMark`, `Markdown`, `Glyphs`,
`DataGrid`, or `Designer`:

```bash
dotnet run \
  --project src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj \
  --configuration Release \
  -- \
  --sample Drawing
```

Use HarfBuzz while keeping the same windowing and rendering paths:

```bash
dotnet run \
  --project src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj \
  --configuration Release \
  -- \
  --sample Drawing \
  --harfbuzz
```

On macOS, shared texture memory is enabled by default. Disable it to measure
the copied-texture fallback, or enable diagnostic shared-image readback:

```bash
dotnet run \
  --project src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj \
  --configuration Release \
  -- \
  --sample Drawing \
  --disable-shared-texture-memory

dotnet run \
  --project src/ProGPU.Samples.Avalonia/ProGPU.Samples.Avalonia.csproj \
  --configuration Release \
  -- \
  --sample Drawing \
  --shared-image-readback
```

The presentation status in the sample header should normally report
`SameDeviceTexture`. The readback and copied-texture modes are diagnostic
comparison paths, not the preferred steady-state configuration.

Run every embedded sample with both text shapers in fresh processes:

```bash
PROGPU_AVALONIA_SAMPLE_TEXT_SHAPERS='progpu,harfbuzz' \
PROGPU_AVALONIA_SAMPLE_WARMUP_FRAMES=120 \
PROGPU_AVALONIA_SAMPLE_MEASURE_FRAMES=300 \
PROGPU_AVALONIA_SAMPLE_REPEATS=3 \
./tools/profile-avalonia-samples.sh \
  artifacts/manual-avalonia-samples
```

Filter the run to one or more sample keys:

```bash
PROGPU_AVALONIA_SAMPLE_FILTER='^(Drawing|Glyphs|Markdown)$' \
PROGPU_AVALONIA_SAMPLE_TEXT_SHAPERS='progpu,harfbuzz' \
./tools/profile-avalonia-samples.sh
```

### Embedded-sample options

| Option or variable | Default | Purpose |
|---|---:|---|
| `--sample <key>` | `Charting` | Select the embedded sample |
| `--harfbuzz` | off | Replace ProGPU shaping with HarfBuzz |
| `--shared-image-readback` | off | Enable diagnostic shared-image readback |
| `--disable-shared-texture-memory` | off | Disable the default macOS shared-texture path |
| `PROGPU_AVALONIA_SAMPLE_FILTER` | `.*` | Regular expression applied to sample keys |
| `PROGPU_AVALONIA_SAMPLE_TEXT_SHAPERS` | `progpu` | Comma-separated `progpu`/`harfbuzz` matrix |
| `PROGPU_AVALONIA_SAMPLE_WARMUP_FRAMES` | `120` | Warmup frames per fresh process |
| `PROGPU_AVALONIA_SAMPLE_MEASURE_FRAMES` | `300` | Measured frames per fresh process |
| `PROGPU_AVALONIA_SAMPLE_REPEATS` | `1` | Fresh process count per sample and shaper |
| `PROGPU_AVALONIA_SAMPLE_SKIP_BUILD` | `0` | Reuse existing Release binaries when set to `1` |
| `PROGPU_AVALONIA_SAMPLE_BENCHMARK_HOLD_MS` | `0` | Keep a completed run alive for profiler attachment |

The profiler writes per-run JSON and logs plus `summary.json`, `summary.md`,
and `failures.tsv` under its output directory.

## RenderDemo

Run RenderDemo with Silk.NET windowing, the ProGPU renderer and compositor,
and ProGPU text shaping:

```bash
./integration/AvaloniaSourceRenderDemo/run.sh
```

Compare the same renderer with HarfBuzz:

```bash
./integration/AvaloniaSourceRenderDemo/run.sh --harfbuzz
```

Run bounded hardware smokes that exit after 60 rendered frames and write
typed telemetry:

```bash
./integration/AvaloniaSourceRenderDemo/run.sh \
  --smoke-frames 60 \
  --smoke-output /tmp/progpu-renderdemo.json

./integration/AvaloniaSourceRenderDemo/run.sh \
  --harfbuzz \
  --smoke-frames 60 \
  --smoke-output /tmp/progpu-renderdemo-harfbuzz.json
```

## ProGPU Sandbox

Run the Sandbox with ProGPU text shaping:

```bash
./integration/AvaloniaSourceSandbox/run.sh
```

Compare with HarfBuzz:

```bash
./integration/AvaloniaSourceSandbox/run.sh --harfbuzz
```

Run bounded hardware smokes:

```bash
./integration/AvaloniaSourceSandbox/run.sh \
  --smoke-frames 60 \
  --smoke-output /tmp/progpu-sandbox.json

./integration/AvaloniaSourceSandbox/run.sh \
  --harfbuzz \
  --smoke-frames 60 \
  --smoke-output /tmp/progpu-sandbox-harfbuzz.json
```

### RenderDemo and Sandbox options

| Option | Default | Purpose |
|---|---:|---|
| `--harfbuzz` | off | Replace ProGPU shaping with HarfBuzz |
| `--smoke-frames <count>` | interactive | Exit after the requested positive frame count |
| `--smoke-output <path>` | none | Write the bounded-smoke JSON result |

## ControlCatalog interactive runs

Use `--page <name>` to open a particular page. Useful page names include
`Buttons`, `Composition`, `Acrylic`, `BitmapCache`, `Canvas`, `TextBlock`, and
`OpenGL`.

Run Silk.NET windowing, ProGPU rendering, and ProGPU text shaping:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --page Composition
```

Use HarfBuzz with the same windowing and rendering backend:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --harfbuzz \
  --page Composition
```

Before the first interactive native-windowing run, build the appropriate
Avalonia platform native library and Dawn integration:

```bash
PROGPU_AVALONIA_BACKENDS=source-progpu-native \
PROGPU_AVALONIA_BUILD_ONLY=1 \
./tools/profile-avalonia-controlcatalog.sh
```

Run Avalonia platform windowing with direct Dawn presentation and ProGPU text
shaping:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --native-windowing \
  --page Composition
```

Run the native presentation path with HarfBuzz:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --native-windowing \
  --harfbuzz \
  --page Composition
```

### Diagnostic fallback modes

Allow flattened composition fallback instead of requiring the native retained
composition scene:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --allow-composition-fallback \
  --page Composition
```

Allow presentation fallback when direct Dawn presentation cannot initialize:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh \
  --native-windowing \
  --allow-dawn-presentation-fallback \
  --page Composition
```

These switches deliberately weaken validation and are intended for diagnosing
initialization failures.

### ControlCatalog command-line options

| Option | Default | Purpose |
|---|---:|---|
| `--page <name>` | ControlCatalog default | Open a named catalog page |
| `--harfbuzz` | off | Replace ProGPU shaping with HarfBuzz |
| `--native-windowing` | off | Use Avalonia Native, Win32, or X11 instead of Silk.NET/GLFW |
| `--allow-composition-fallback` | off | Permit flattened rendering when retained composition is unavailable |
| `--allow-dawn-presentation-fallback` | off | Permit non-Dawn presentation if strict native Dawn startup fails |

## Original Skia ControlCatalog

Run a bounded Skia/HarfBuzz reference process through the profiler:

```bash
PROGPU_AVALONIA_BACKENDS=skia \
PROGPU_AVALONIA_PAGE_FILTER='^Composition$' \
PROGPU_AVALONIA_WARMUP_FRAMES=60 \
PROGPU_AVALONIA_MEASURE_FRAMES=120 \
PROGPU_AVALONIA_SCREENSHOTS=1 \
./tools/profile-avalonia-controlcatalog.sh \
  artifacts/manual-skia
```

For an interactive Skia window on macOS, prepare the application and native
library first:

```bash
PROGPU_AVALONIA_BACKENDS=skia \
PROGPU_AVALONIA_BUILD_ONLY=1 \
./tools/profile-avalonia-controlcatalog.sh
```

Then launch the built application:

```bash
DYLD_LIBRARY_PATH="$PWD/integration/AvaloniaSkiaControlCatalogReference/bin/Release/net10.0/runtimes/osx/native${DYLD_LIBRARY_PATH:+:$DYLD_LIBRARY_PATH}" \
dotnet integration/AvaloniaSkiaControlCatalogReference/bin/Release/net10.0/AvaloniaSkiaControlCatalogReference.dll \
  --page Composition
```

On Linux, use `LD_LIBRARY_PATH` instead of `DYLD_LIBRARY_PATH`. On Windows,
prepend the native runtime directory to `PATH`.

## Automated backend comparison

Run representative pages across all renderer, windowing, and text-shaper
configurations:

```bash
PROGPU_AVALONIA_BACKENDS='source-progpu,source-progpu-harfbuzz,source-progpu-native,source-progpu-native-harfbuzz,skia' \
PROGPU_AVALONIA_PAGE_FILTER='^(Buttons|Composition|BitmapCache)$' \
PROGPU_AVALONIA_WARMUP_FRAMES=120 \
PROGPU_AVALONIA_MEASURE_FRAMES=300 \
PROGPU_AVALONIA_REPEATS=3 \
PROGPU_AVALONIA_SCREENSHOTS=1 \
./tools/profile-avalonia-controlcatalog.sh \
  artifacts/manual-backend-comparison
```

Omit `PROGPU_AVALONIA_PAGE_FILTER` to run every discovered ControlCatalog
page. The output directory contains `summary.json`, `summary.md`, per-run JSON
telemetry, logs, optional screenshots, and `failures.tsv`.

Use a fixed Silk.NET refresh rate for controlled comparisons:

```bash
PROGPU_AVALONIA_RENDER_FPS=60 \
PROGPU_AVALONIA_BACKENDS='source-progpu,source-progpu-harfbuzz' \
PROGPU_AVALONIA_PAGE_FILTER='^Composition$' \
./tools/profile-avalonia-controlcatalog.sh
```

`PROGPU_AVALONIA_RENDER_FPS` accepts values from 24 through 360.

### Profiler options

| Variable | Default | Purpose |
|---|---:|---|
| `PROGPU_AVALONIA_BACKENDS` | `source-progpu` | Comma-separated backend matrix |
| `PROGPU_AVALONIA_PAGE_FILTER` | `.*` | Regular expression applied to page names |
| `PROGPU_AVALONIA_WARMUP_FRAMES` | `120` | Warmup frames per fresh process |
| `PROGPU_AVALONIA_MEASURE_FRAMES` | `300` | Measured frames per fresh process |
| `PROGPU_AVALONIA_REPEATS` | `1` | Number of fresh processes per page/backend |
| `PROGPU_AVALONIA_SCREENSHOTS` | `0` | Set to `1` to capture a PNG |
| `PROGPU_AVALONIA_BUILD_ONLY` | `0` | Set to `1` to prepare and build without launching |
| `PROGPU_AVALONIA_SKIP_BUILD` | `0` | Set to `1` to reuse already-built binaries |
| `PROGPU_AVALONIA_RENDER_FPS` | display refresh | Fixed Silk.NET rate from 24 through 360 |

The profiler also supports focused retained-scene fixtures:

```bash
PROGPU_AVALONIA_LAYOUT_CLIP_FIXTURE=1
PROGPU_AVALONIA_GEOMETRY_CLIP_FIXTURE=1
PROGPU_AVALONIA_BITMAP_CACHE_FIXTURE=1
PROGPU_AVALONIA_EFFECT_FIXTURE=1
PROGPU_AVALONIA_OPACITY_MASK_FIXTURE=1
PROGPU_AVALONIA_INHERITED_DRAWING_OPTIONS_FIXTURE=1
PROGPU_AVALONIA_TOPOLOGY_FIXTURE=1
PROGPU_AVALONIA_ADORNER_FIXTURE=1
```

Set only the fixture required by the selected page and test.

## Retained and flattened comparisons

The ProGPU retained Avalonia scene is enabled by default. Make that explicit:

```bash
PROGPU_AVALONIA_RETAINED_SCENE=1 \
./integration/AvaloniaSourceControlCatalog/run.sh \
  --page Buttons
```

Disable the retained scene to exercise the flattened path:

```bash
PROGPU_AVALONIA_RETAINED_SCENE=0 \
./integration/AvaloniaSourceControlCatalog/run.sh \
  --allow-composition-fallback \
  --page Buttons
```

Disable incremental scene-page reuse for an exact comparison:

```bash
PROGPU_AVALONIA_INCREMENTAL_SCENE_PAGES=0 \
./integration/AvaloniaSourceControlCatalog/run.sh \
  --page Buttons
```

Run the retained-versus-flattened pixel contract:

```bash
./tools/test-avalonia-progpu-retained-pixels.sh
```

## Package-consumer sample

The package smoke consumes ProGPU through package references only; it has no
project references to ProGPU.

Build fresh local integration packages and run them:

```bash
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

Build and run the complete exact-identity Avalonia replacement stack:

```bash
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Test versions already published on NuGet.org:

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh nuget
```

Exercise shared-device multi-window creation and destruction:

```bash
PROGPU_PACKAGE_SMOKE_MULTI_WINDOW=1 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Exercise extended client area, title-bar margins, backdrop selection, window
actions, and native owner propagation:

```bash
PROGPU_PACKAGE_SMOKE_WINDOW_CHROME=1 \
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Publish and execute a standalone NativeAOT application on macOS arm64:

```bash
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_RUNTIME_IDENTIFIER=osx-arm64 \
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Use the corresponding runtime identifier on other platforms, for example
`osx-x64`, `linux-x64`, `linux-arm64`, or `win-x64`.

Validate restore and build without opening a window:

```bash
PROGPU_INTEGRATION_BUILD_ONLY=1 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Reuse an existing local replacement feed:

```bash
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_PACKAGE_SOURCE="$PWD/artifacts/avalonia-replacement" \
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

Reuse already-built ordinary integration packages:

```bash
PROGPU_REUSE_PACKAGES=1 \
PROGPU_PACKAGE_SOURCE="$PWD/artifacts/packages/Release" \
PROGPU_PACKAGE_SMOKE_FRAMES=60 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

Override package versions when validating another preview:

```bash
PROGPU_RUNTIME_PACKAGE_VERSION=0.1.0-preview.40 \
PROGPU_INTEGRATION_PACKAGE_VERSION=12.0.5-preview.40 \
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

## Focused integration validation

Run the renderer and platform contracts:

```bash
dotnet test tests/ProGPU.Avalonia.ContractTests/ProGPU.Avalonia.ContractTests.csproj \
  -c Release

dotnet test tests/ProGPU.Avalonia.SilkNet.ContractTests/ProGPU.Avalonia.SilkNet.ContractTests.csproj \
  -c Release

dotnet test tests/ProGPU.Avalonia.HeadlessPixelTests/ProGPU.Avalonia.HeadlessPixelTests.csproj \
  -c Release
```

Run the pinned Avalonia compositor and text suites:

```bash
./tools/test-avalonia-progpu-compositor.sh
./tools/test-avalonia-progpu-text.sh
```

Run the ABI, reflection, and clean-room checks:

```bash
PROGPU_AVALONIA_ROOT="$PWD/.worktrees/avalonia-12.0.5" \
./tools/validate-avalonia-source-abi.sh

./tools/validate-avalonia-progpu-no-reflection.sh \
  artifacts/avalonia-replacement

./tools/verify-avalonia-clean-room.sh --enforce
```

## Retina and DPI validation

Desktop windows keep Avalonia layout in logical coordinates while allocating
the presentation target in physical pixels. On a 2x Retina display, a
1024x800 ControlCatalog window must therefore report
`WindowRenderScaling: 2`, `WindowPhysicalWidth: 2048`, and
`WindowPhysicalHeight: 1600`. ProGPU telemetry additionally reports
`RenderTargetWidth`, `RenderTargetHeight`, and `DpiScale`.

Capture the text-heavy comparison with both ProGPU windowing paths, both text
shapers, and Skia:

```bash
PROGPU_AVALONIA_BACKENDS='source-progpu,source-progpu-harfbuzz,source-progpu-native,source-progpu-native-harfbuzz,skia' \
PROGPU_AVALONIA_PAGE_FILTER='^(Buttons|TextBlock)$' \
PROGPU_AVALONIA_WARMUP_FRAMES=60 \
PROGPU_AVALONIA_MEASURE_FRAMES=180 \
PROGPU_AVALONIA_SCREENSHOTS=1 \
./tools/profile-avalonia-controlcatalog.sh \
  artifacts/avalonia-retina-text
```

For `ProGPU.Samples.Avalonia`, the embedded shared texture must also be
physical-sized. A 1020x743 logical viewport on the same display should report
`RenderTargetWidth: 2040`, `RenderTargetHeight: 1486`, and `DpiScale: 2`:

```bash
PROGPU_AVALONIA_SAMPLE_FILTER='^Glyphs$' \
PROGPU_AVALONIA_SAMPLE_TEXT_SHAPERS='progpu,harfbuzz' \
PROGPU_AVALONIA_SAMPLE_WARMUP_FRAMES=60 \
PROGPU_AVALONIA_SAMPLE_MEASURE_FRAMES=180 \
./tools/profile-avalonia-samples.sh \
  artifacts/avalonia-retina-embedded
```

The Avalonia compositor metrics can intentionally report `DpiScale: 1` while
its target is physical-sized because Avalonia's retained command transform
already contains the logical-to-physical scale. The embedded ProGPU compositor
owns its logical projection and therefore reports the actual display DPI.
