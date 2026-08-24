# ProGPU rendering and Silk.NET windowing

The ProGPU integration ships the same two package IDs in separate Avalonia 12 and
Avalonia 11 version lanes:

| Avalonia lane | Package | Assembly | Purpose |
| --- | --- | --- | --- |
| 12.1.1 | `ProGPU.Avalonia.Rendering` `12.1.1-preview.56` | `Avalonia.ProGpu` | ProGPU/WebGPU rendering backend |
| 12.1.1 | `ProGPU.Avalonia.SilkNet` `12.1.1-preview.56` | `Avalonia.SilkNet` | Cross-platform Silk.NET windowing backend |
| 11.3.20 | `ProGPU.Avalonia.Rendering` `11.3.20-preview.56` | `Avalonia.ProGpu` | Shared-source ProGPU/WebGPU rendering backend |
| 11.3.20 | `ProGPU.Avalonia.SilkNet` `11.3.20-preview.56` | `Avalonia.SilkNet` | Shared-source Silk.NET windowing backend |
| native ABI | `ProGPU.Backend.Native` `0.1.0-preview.56` | `ProGPU.Backend.Native` | Experimental typed C++ renderer host with isolated wgpu-native and provider-resolved Dawn binaries for Linux, macOS, and Windows x64/arm64 |
| native ABI | `ProGPU.Backend.Dawn` `0.1.0-preview.56` | `ProGPU.Backend.Dawn` | Exact-ABI Dawn device, IOSurface shared memory, and timeline fences |

The Avalonia 12 packages are built against exactly Avalonia `12.1.1`; the
Avalonia 11 packages are built against exactly Avalonia `11.3.20`. Both lanes
use ProGPU `0.1.0-preview.56`. They intentionally use `ProGPU.*` package IDs;
no `Avalonia.*` package ID is published by the normal NuGet.org release lane.
The separately gated private/local replacement lane described below produces
an exact-identity `Avalonia` package and must never be pushed to NuGet.org.

The NuGet package page uses `docs/progpu-package-readme.md`. Keep its install, startup, API lease, and troubleshooting instructions current when package contracts change.

The original package artwork is maintained as `build/Assets/ProGpuAvaloniaIcon.svg` and rendered to `build/Assets/ProGpuAvaloniaIcon.png`. NuGet uses the PNG, and both files are included in each integration package.

## Development projects

All four integration projects live in this repository and reference the local
ProGPU runtime projects. Build the Avalonia 12 lane with:

```bash
dotnet build src/ProGPU.Avalonia.Rendering/ProGPU.Avalonia.Rendering.csproj
dotnet build src/ProGPU.Avalonia.SilkNet/ProGPU.Avalonia.SilkNet.csproj
```

Build the Avalonia 11 lane with:

```bash
dotnet build src/ProGPU.Avalonia.Rendering.V11/ProGPU.Avalonia.Rendering.V11.csproj
dotnet build src/ProGPU.Avalonia.SilkNet.V11/ProGPU.Avalonia.SilkNet.V11.csproj
```

The v11 projects contain no duplicated backend implementation. They source-link
the Avalonia 12 project sources and define `AVALONIA11`; conditionals are limited
to concrete Avalonia API differences. Warning `AVA3001` is expected because the
backends implement Avalonia platform interfaces that are intentionally private.
The package dependencies are exact pins, so upgrading Avalonia requires a
matching integration build.

The four integration projects are explicitly packable even though the
repository defaults new projects to non-packable. Validate the complete
exact-source replacement stack with:

```bash
./tools/pack-avalonia-progpu-stack.sh
```

This builds the exact-identity `Avalonia` replacement, compares its public ABI
with the separately pinned source-replacement Avalonia lane, packs the ProGPU runtime dependency closure and
both Avalonia integration lanes, and rejects runtime reflection in the
renderer and Silk.NET assemblies.

## Control Catalog defaults

The source-built Control Catalog starts with Silk.NET windowing and ProGPU
rendering when no renderer argument is supplied:

```bash
./integration/AvaloniaSourceControlCatalog/run.sh
```

ControlCatalog uses the managed ProGPU OpenType shaper by default. Pass
`--harfbuzz` to use Avalonia's previous HarfBuzz backend for correctness and
performance comparison.

The independent Skia/HarfBuzz reference process is
`integration/AvaloniaSkiaControlCatalogReference`.

The main-window title identifies the configured windowing platform, rendering
platform, compositor, and text shaper. ProGPU lanes replace the configured
rendering label after the first rendered frame with the observed presentation
path (Silk.NET WebGPU surface, Dawn Metal IOSurface, Dawn D3D12 HWND, Dawn
Vulkan Xlib, or Avalonia framebuffer). The Skia reference lane reports Skia,
the Avalonia retained compositor, and HarfBuzz. This uses typed startup and
frame-diagnostic contracts only; it performs no reflection or runtime assembly
probing.

## Official source sample hosts

The RenderDemo and Sandbox integration hosts compile the unchanged official
Avalonia 12.1.1 sample projects from the prepared pinned source tree. The
ProGPU-owned startup assemblies replace only platform selection:

```bash
./integration/AvaloniaSourceRenderDemo/run.sh
./integration/AvaloniaSourceSandbox/run.sh
```

Both use Silk.NET windowing, the ProGPU renderer/compositor, the compatible
`Avalonia.Skia` contract assembly, and ProGPU OpenType shaping. Pass
`--harfbuzz` to either host for a shaping comparison. Neither host copies an
Avalonia sample into the ProGPU tree.

Their bounded hardware smoke mode exits after observing real ProGPU frames and
writes the presentation path, draw count, retained-scene count, and fallback
count:

```bash
./integration/AvaloniaSourceRenderDemo/run.sh \
  --smoke-frames 4 \
  --smoke-output /tmp/progpu-renderdemo.json

./integration/AvaloniaSourceSandbox/run.sh \
  --smoke-frames 1 \
  --smoke-output /tmp/progpu-sandbox.json
```

The preparation script is safe to rerun. It applies the retained-compositor
foundation and then the focused text, ControlCatalog, package, native
presentation, and source-sample patches without applying overlapping legacy
hunks twice.

## Silk.NET window chrome

The Silk.NET backend implements Avalonia window chrome through typed native
controllers. Requests made before native-window creation are retained and
replayed when the window is initialized. Supported contracts include extended
client area and margins, title-bar height hints, system-decoration modes,
managed decoration requests, minimize/maximize/resize permissions, taskbar
visibility, topmost/enabled state, owners, native size constraints, backdrop
selection, move/resize drags, and popup shadow hints.

On macOS, transparent, blur, acrylic, and Mica hints map to the closest native
vibrancy/backdrop behavior and popup shadows use `NSWindow` shadow state. On
Windows, popup shadow hints use the native drop-shadow window class style.
Unsupported combinations retain deterministic Avalonia state instead of using
reflection or probing private runtime objects.

Run the package-only native chrome contract with:

```bash
PROGPU_PACKAGE_SMOKE_WINDOW_CHROME=1 \
PROGPU_PACKAGE_SMOKE_FRAMES=8 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

Run every ControlCatalog page in a fresh Release process and collect FPS,
frame-time, allocation, retained-memory, physical-footprint, and ProGPU GPU
resource metrics with:

```bash
./tools/profile-avalonia-controlcatalog.sh
```

The default is the pinned source-built Avalonia runtime, ProGPU renderer,
managed ProGPU shaper, and Silk.NET windowing. Set
`PROGPU_AVALONIA_BACKENDS=source-progpu,source-progpu-harfbuzz` for an
apples-to-apples shaping sweep of the replacement runtime. The `skia` lane is
the official Avalonia Skia/HarfBuzz reference process. Use
`PROGPU_AVALONIA_PAGE_FILTER` to run a
regular-expression subset. The
generated `summary.json`, `summary.md`, per-page logs, optional screenshots, and
`failures.tsv` are written under `artifacts/avalonia-controlcatalog-profile`.
The Silk.NET render timer follows the primary display refresh rate; set
`PROGPU_AVALONIA_RENDER_FPS=60` (or another value from 24 through 360) when a
fixed-rate comparison is required.

The macOS non-Silk presentation matrix is selected with:

```bash
PROGPU_AVALONIA_BACKENDS=source-progpu-native,source-progpu-native-harfbuzz,source-progpu,skia \
PROGPU_AVALONIA_PAGE_FILTER='^Buttons$' \
./tools/profile-avalonia-controlcatalog.sh
```

The native lane builds the exact Avalonia.Native source into isolated Xcode
DerivedData, installs it into the ControlCatalog output, deletes the
intermediates, and launches `--native-windowing` with Dawn presentation
required. Results include physical render-target dimensions and DPI so a
lower-resolution surface cannot appear to be a memory optimization.

Run the pinned Avalonia text conformance corpus against the ProGPU shaper with:

```bash
./tools/test-avalonia-progpu-text.sh
```

The script builds the exact official 12.1.1 source-lane test executable and runs the complete
text-formatting namespace plus `GlyphRunTests`. The test bodies and assertions
remain upstream; only the typed ProGPU test bootstrap selects
`ProGpuTextShaper`.

Run the pinned Avalonia retained-compositor revision contract with:

```bash
./tools/test-avalonia-progpu-compositor.sh
```

This verifies that visual-state changes advance the retained scene revision
without invalidating immutable draw-list content, while replacing draw-list
content advances both contracts.

On a desktop with a working Silk.NET/WebGPU surface, run the retained versus
flattened pixel contract with:

```bash
./tools/test-avalonia-progpu-retained-pixels.sh
```

The script compares exact PNG bytes for Buttons, Composition, Acrylic,
BitmapCache, Canvas, AdornerLayer, Clipboard, HeaderedContentControl,
Notifications, bounded native geometry/text-option fixtures, native blur and
offset/color/opacity drop-shadow fixtures, and all BitmapCache option fixtures.
It requires
gradient and recorded-picture opacity masks, unclipped and clipped adorners,
including rotated conic gradients, standard visual effects, and the
geometry-clip workload to remain inside the retained ProGPU scene with zero
fallback nodes.

The full-compositor replacement design and its package compatibility gates are
in
[`AVALONIA_COMPOSITOR_BACKEND_ARCHITECTURE.md`](AVALONIA_COMPOSITOR_BACKEND_ARCHITECTURE.md).

## Pack locally

Pack the ProGPU `0.1.0-preview.56` portable runtime packages first, then pack
both integration lanes:

```bash
PROGPU_PACKAGE_GROUP=portable ./eng/progpu-pack.sh
./scripts/progpu-pack.sh
```

The package-only application can perform that sequence and then restore in an
isolated package cache:

```bash
PROGPU_INTEGRATION_BUILD_ONLY=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

Set `PROGPU_PACKAGE_SOURCE` to use another absolute local package directory. The
expected integration output is eight files: a `.nupkg` and `.snupkg` for each of
the four package/version entries.

## Exact-identity private replacement package

Build the complete private/local replacement stack with:

```bash
./tools/pack-avalonia-progpu-stack.sh
```

This creates an `Avalonia` `12.1.1` package from the exact official tag plus
the reviewed typed compositor seam, then compiles the ProGPU renderer against
that seam. It also packs the ten-package `avalonia-runtime` dependency
closure (`Backend`, `Backend.Dawn`, `Text.Shaping`, `Transpiler`, `Vector`,
`Text`, `Compute`, `Scene`, and `SkiaSharp`) from the same checkout. The
verifier compares every
packed `lib` and `ref` assembly for
`net10.0` and `net8.0` against the official package with strict ApiCompat,
checks assembly identities, verifies the packed `Avalonia.Base` bytes are the
validated source build, and requires the package provenance notice.

Because its package ID and version intentionally collide with the official
package, this artifact is only for an isolated private/local feed. It is the
actual NuGet bait-and-switch lane: applications keep
`PackageReference Include="Avalonia" Version="12.1.1"` while the feed supplies
the ProGPU source build. It must not be published to NuGet.org.

Exercise a clean package-only consumer and fail if restore selects the official
binary instead of the validated local replacement:

```bash
PROGPU_INTEGRATION_BUILD_ONLY=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

The isolated consumer restores into a fresh NuGet package directory, builds
against the local feed, and validates the exact assembly identities selected
by the project. The replacement package tool performs the package-content and
ABI checks before the consumer is launched.

Publish and execute that same exact restored stack under NativeAOT:

```bash
PROGPU_PACKAGE_SOURCE=artifacts/avalonia-replacement \
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_SMOKE=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

This is a runtime gate, not only a linker check. The native executable must
render frames, use the retained compositor, and report zero fallback nodes.
The host RID is selected automatically; an explicit compatible RID can be
provided through `PROGPU_INTEGRATION_RUNTIME_IDENTIFIER`. The isolated
packages, AOT compiler output, and published binary are removed when the
runner exits.

The final preview.27 replacement stack passed this gate from the exact
`artifacts/avalonia-replacement` bytes. The isolated package-only build
completed with zero warnings, the macOS arm64 NativeAOT executable was
22,266,520 bytes and rendered 40 frames with zero fallback nodes, and the
ordinary smoke rendered 28 frames with zero fallbacks. The multi-window smoke
rendered 70 aggregate frames across two retained scenes; the survivor remained
renderable after both owner-first and borrower-first shared-device disposal.
The checked-in gate also caught and fixed input-device list mutation during
owner disposal. Its NativeAOT runner now restores the IL compiler into the
isolated cache and rejects an IL-only self-contained publish.
NativeAOT analysis warnings were confined to the third-party Silk.NET loader
and ImageSharp assemblies; `Avalonia.ProGpu.dll` and `Avalonia.SilkNet.dll`
remain clean under the runtime-reflection metadata audit.

## Publish to NuGet.org

Keep the API key out of command history and repository files. From Bash:

```bash
read -rsp "NuGet API key: " NUGET_API_KEY && printf '\n'
export NUGET_API_KEY
./scripts/progpu-publish.sh
unset NUGET_API_KEY
```

`progpu-publish.sh` repacks, validates all expected artifacts, and pushes each package with `--skip-duplicate`; `dotnet nuget push` discovers and uploads the matching symbol package automatically. Override `NUGET_SOURCE` only when publishing to another NuGet-compatible server.

Release order:

1. Tag and publish ProGPU `0.1.0-preview.56`.
2. Confirm the required ProGPU packages are available from NuGet.org.
3. Pack and test both Avalonia integration lanes.
4. Publish the two `12.1.1-preview.56` packages and the two
   `11.3.20-preview.56` packages.

## Consume the packages

```xml
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.1.1" />
  <PackageReference Include="Avalonia.Fonts.Inter" Version="12.1.1" />
  <PackageReference Include="ProGPU.Avalonia.Rendering" Version="12.1.1-preview.56" />
  <PackageReference Include="ProGPU.Avalonia.SilkNet" Version="12.1.1-preview.56" />
</ItemGroup>
```

Configure both backends before starting the desktop lifetime:

```csharp
using Avalonia.Rendering.Composition;

public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>()
        .UseSilkNet()
        .UseProGpu()
        .With(new CompositionOptions
        {
            UseRegionDirtyRectClipping = false
        })
        .UseProGpuTextShaping()
        .WithInterFont();
```

`UseSkia()` remains available as a compatibility alias for the ProGPU renderer, but `UseProGpu()` avoids ambiguity with Avalonia's Skia package.

Use `IProGpuApiLeaseFeature` from `ICustomDrawOperation.Render` for scoped
access to the ProGPU scene command recorder and active `WgpuContext`. The
lease lifetime and package-facing API contract are documented in
`docs/progpu-package-readme.md`.

For Avalonia 11 applications, use the same package IDs with
`11.3.20-preview.56` and pin the Avalonia packages to `11.3.20`.

The cross-engine design review and validation evidence are recorded in
`docs/progpu-avalonia-rendering-research.md`.
