# ProGPU rendering for Avalonia

These packages run Avalonia 12 on the ProGPU/WebGPU renderer with either
Silk.NET windowing or Avalonia's existing native windowing backend.

| Package | Purpose |
| --- | --- |
| `ProGPU.Avalonia.Rendering` | Avalonia renderer backed by ProGPU and WebGPU |
| `ProGPU.Avalonia.SilkNet` | Cross-platform Silk.NET desktop windowing backend |

Version `12.0.5-preview.46` is built against exactly Avalonia `12.0.5` and
ProGPU `0.1.0-preview.46` on .NET 10. Avalonia 11 applications use the same
package IDs at `11.3.18-preview.46`, built against exactly Avalonia `11.3.18`.

## Install

Reference the renderer, windowing backend, text shaper, and font package:

```xml
<ItemGroup>
  <PackageReference Include="Avalonia" Version="12.0.5" />
  <PackageReference Include="Avalonia.Fonts.Inter" Version="12.0.5" />
  <PackageReference Include="ProGPU.Avalonia.Rendering" Version="12.0.5-preview.46" />
  <PackageReference Include="ProGPU.Avalonia.SilkNet" Version="12.0.5-preview.46" />
</ItemGroup>
```

The integration packages carry exact dependencies on their supported Avalonia and ProGPU versions. Upgrade the five references together when a matching integration preview is released.

## Configure the app

Configure Silk.NET windowing, ProGPU rendering and managed text shaping, and the Inter font before starting the desktop lifetime:

```csharp
using Avalonia;
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

`UseRegionDirtyRectClipping = false` is the current recommended setting for the ProGPU renderer. `UseSkia()` remains a compatibility alias for `UseProGpu()`, but the explicit ProGPU name avoids confusion with Avalonia's Skia renderer.

Silk.NET schedules animation frames at the primary display's reported refresh
rate and stops submitting when Avalonia has no queued invalidation. For a
fixed-rate diagnostic run, set `PROGPU_AVALONIA_RENDER_FPS` to a value from 24
through 360 before process startup.

Start the app normally:

```csharp
[STAThread]
public static void Main(string[] args) =>
    BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
```

## Use Avalonia Native windowing with Dawn

Avalonia 12 applications may keep Avalonia's macOS windowing backend while
using the same ProGPU compositor:

```csharp
public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>()
        .UseAvaloniaNative()
        .UseProGpu()
        .With(new ProGpuOptions
        {
            UseDawnMetalPresentation = true,
            RequireDawnMetalPresentation = true
        })
        .UseProGpuTextShaping()
        .WithInterFont();
```

This path uses WebGPUSharp/Dawn to import Avalonia's drawable IOSurface and
exchanges Metal shared-event timeline values before presentation. It performs
no CPU readback or full-frame copy. Strict mode currently requires the
source-built Avalonia.Native lane whose CAMetalLayer produces an
uncompressed, Dawn-importable BGRA IOSurface. Leave
`RequireDawnMetalPresentation` false for package compatibility; an unsupported
drawable then releases the attempted Dawn device and uses Avalonia's
framebuffer surface.

## Use the ProGPU API lease

Custom controls can submit ProGPU scene commands from an Avalonia custom draw operation. Acquire `IProGpuApiLeaseFeature` only inside `ICustomDrawOperation.Render`, dispose the lease before returning, and pass `CurrentTransform` to transform-aware ProGPU methods.

```csharp
using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.ProGpu;
using Avalonia.Rendering.SceneGraph;
using ProGpuBrush = ProGPU.Vector.SolidColorBrush;
using ProGpuRect = ProGPU.Scene.Rect;

public sealed class ProGpuControl : Control
{
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.Custom(new DrawOperation(
            new Rect(0, 0, Bounds.Width, Bounds.Height)));
    }

    private sealed class DrawOperation : ICustomDrawOperation
    {
        public DrawOperation(Rect bounds) => Bounds = bounds;

        public Rect Bounds { get; }

        public void Render(ImmediateDrawingContext context)
        {
            var feature = context.TryGetFeature<IProGpuApiLeaseFeature>();
            if (feature is null)
                return; // A different Avalonia renderer is active.

            using var lease = feature.Lease();
            var fill = new ProGpuBrush(
                new Vector4(0.09f, 0.72f, 0.96f, 1f));

            lease.DrawingContext.DrawRoundedRectangle(
                fill,
                null,
                new ProGpuRect(
                    0,
                    0,
                    (float)Bounds.Width,
                    (float)Bounds.Height),
                12,
                12,
                lease.CurrentTransform);

            // The active ProGPU.Backend.WgpuContext is also available when
            // custom resources or direct WebGPU work are required.
            var gpuContext = lease.WgpuContext;
        }

        public bool HitTest(Point point) => Bounds.Contains(point);

        public bool Equals(ICustomDrawOperation? other) =>
            other is DrawOperation operation && operation.Bounds == Bounds;

        public void Dispose()
        {
        }
    }
}
```

The lease also exposes `CurrentOpacity`, `PixelSize`, and `Dpi`. Avalonia's current opacity is already represented by the active ProGPU command stack, so do not multiply it into brush alpha a second time.

Lease rules:

- Acquire and dispose the lease on the render thread within the same `Render` call.
- Do not retain the command recorder, `WgpuContext`, native handles, or other leased objects.
- Do not issue Avalonia drawing-context calls while the ProGPU API is leased.
- Balance every ProGPU push command with its matching pop command.
- Treat `IProGpuApiLeaseFeature` and `IProGpuApiLease` as unstable preview APIs.

## Run a WGSL shader through the lease

ProGPU's built-in ShaderToy extension compiles WGSL and encodes it into the compositor's active WebGPU render pass. Record the extension command through the leased drawing context so Avalonia transform, clipping, opacity, and frame ordering remain intact.

Keep each fixed shader in its own `.wgsl` file and embed it with a stable logical name:

```xml
<ItemGroup>
  <EmbeddedResource Include="Shaders/*.wgsl"
                    LogicalName="$(AssemblyName).Shaders.%(Filename)%(Extension)" />
</ItemGroup>
```

Load the source once through ProGPU's cached resource loader. `ApiLeaseWave.wgsl` should define `mainImage` and document its algorithm, time complexity, and space or bandwidth complexity at the top of the file.

```csharp
using System.Numerics;
using ProGPU.Backend;
using ProGPU.Scene.Extensions;

private static readonly string s_wgsl =
    ShaderResource.Load<ProGpuControl>("ApiLeaseWave.wgsl");

var width = (float)Bounds.Width;
var height = (float)Bounds.Height;
var shaderRect = new ProGPU.Scene.Rect(0, 0, width, height);
var shader = new ShaderToyParams
{
    Rect = shaderRect,
    Resolution = new Vector3(width, height, 1),
    Time = 0,
    TimeDelta = 1f / 60f,
    Frame = 0,
    FrameRate = 60,
    ShaderKey = "MyAvaloniaWgslShaderV1",
    ShaderSource = s_wgsl
};

lease.DrawingContext.DrawExtension(
    ProGPU.Scene.CompositorBuiltInExtensions.ShaderToy,
    dataParam: shader,
    transform: lease.CurrentTransform);
```

Keep `ShaderKey` stable for a stable shader source so ProGPU can reuse the compiled WebGPU pipeline. Resource loading and UTF-8 decoding occur once, outside the frame hot path.

## Validate local and published packages

The repository includes a package-only integration app that exercises startup and the ProGPU API lease without project references.

Use packages freshly built from the checkout:

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

Use the exact-identity Avalonia 12.0.5 replacement package from an isolated
local feed:

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

The replacement mode verifies that restore selected the locally validated
`Avalonia.Base` binary. This private-feed artifact deliberately shares the
official `Avalonia` package ID and version and must not be published to
NuGet.org.

Use packages from NuGet.org only:

```bash
./integration/ProGpuAvaloniaPackageSmoke/run.sh nuget
```

For a non-interactive restore and build check:

```bash
PROGPU_INTEGRATION_BUILD_ONLY=1 ./integration/ProGpuAvaloniaPackageSmoke/run.sh local
PROGPU_INTEGRATION_BUILD_ONLY=1 ./integration/ProGpuAvaloniaPackageSmoke/run.sh nuget
```

For an exact package-only NativeAOT publish and retained-rendering smoke:

```bash
PROGPU_REUSE_REPLACEMENT_STACK=1 \
PROGPU_INTEGRATION_NATIVE_AOT=1 \
PROGPU_INTEGRATION_SMOKE=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh replacement
```

The Silk.NET host registers its GLFW window and input implementations through
their public typed registration APIs, so trimmed applications do not depend
on assembly discovery.

Set `PROGPU_INTEGRATION_PACKAGE_VERSION` to test another integration preview.
For the Avalonia 11 lane, set both versions:

```bash
PROGPU_AVALONIA_PACKAGE_VERSION=11.3.18 \
PROGPU_INTEGRATION_PACKAGE_VERSION=11.3.18-preview.46 \
PROGPU_INTEGRATION_BUILD_ONLY=1 \
  ./integration/ProGpuAvaloniaPackageSmoke/run.sh local
```

The Avalonia 11 renderer contains its shared-source HarfBuzz adapter, so the
package-only v11 configuration does not reference `Avalonia.HarfBuzz`; the
ProGPU rendering initializer registers the managed shaper directly.

## Troubleshooting

- `No text shaping system configured`: call `UseProGpuTextShaping()` after
  `UseProGpu()`. Reference `Avalonia.HarfBuzz` and call `UseHarfBuzz()` only
  when deliberately selecting the comparison backend.
- Missing default typeface: reference `Avalonia.Fonts.Inter` and call `WithInterFont()`.
- Incomplete dirty-region updates: set `UseRegionDirtyRectClipping = false`.
- `IProGpuApiLeaseFeature` is unavailable: verify that `UseProGpu()` selected the active renderer.

Source and issues are available in the
[ProGPU repository](https://github.com/wieslawsoltes/ProGPU).
