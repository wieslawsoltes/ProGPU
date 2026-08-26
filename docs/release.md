# ProGPU Release Workflow

ProGPU packages are built from the explicit package list in `eng/progpu-package-list.sh`.
The release workflow does not pack samples, tests, diagnostic tools, or framework shim projects.
It also builds the separately versioned Avalonia 11 and 12 integration packages
from `scripts/progpu-package-list.sh`.

Preview.61 advances the successfully published preview.60 boundary with the
complete Avalonia Silk.NET windowing, input, rendering, and WebGPU recovery work
merged in pull request #152. The windowing host keeps client, pointer, and
framebuffer coordinates in the correct DPI domains, updates layout throughout
live resize, and delegates custom-title-bar dragging to native caption behavior.
Routed input covers repeat keys, UTF-32 text, modifiers and shortcuts, pointer
leave, five mouse buttons, two-axis wheels, Win32 touch, XInput 2.2 touch, and
AppKit gestures.

Silk/wgpu-native and Dawn now track loss per exact WebGPU device, reject unsafe
submission after terminal loss, invalidate affected surfaces, drop failed
Avalonia frames cleanly, and recreate only healthy contexts. Dawn presentation
surfaces retain explicit context leases so the device cannot be destroyed before
the old render target. Immutable bitmap CPU sources remain available for
cross-device migration. Deterministic recovery telemetry and CI smoke tests cover
both managed and native presentation paths.

## Preview.61 Avalonia integration and recovery closure

The Silk.NET windowing and input contracts pass 129 tests against Avalonia
12.1.1 and 104 against Avalonia 11.3.20. The Avalonia rendering integration
passes 104 focused contracts, 28 retained-compositor contracts pass against the
pinned Avalonia source, and the complete local managed suites pass 3,808
renderer tests and 240 headless tests.

Interactive validation covers the macOS host plus Windows 11 and Ubuntu VMs in
Parallels. Windows verifies 200% DPI sizing, title dragging, live resize,
keyboard shortcuts, pointer and touch routing, and 1024x800 logical / 2048x1600
physical ControlCatalog output. Native Win32, macOS, and X11 windowing all render
the catalog through ProGPU without the observed clipping regressions. Forced
device loss recovers Silk and Dawn in one frame on Windows and Linux; macOS
recovers Silk in one frame and Dawn Metal/IOSurface in two frames. The recovered
Border page preserves circular image clipping and the expected framebuffer size.

All 27 checks on pull request #152 pass, including the managed platform matrix,
native C++ renderer and compiler matrix, browser WebGPU, mobile and portable
packing, native package creation, six native package consumers, and the new
Avalonia Dawn recovery smokes on Windows, macOS, and Linux. The tagged Release
workflow repeats the repository, native, package-consumer, mobile, and Avalonia
integration validation before publishing.

## Compatibility and continuation

WinUI remains at 4,952 exact of 16,579 official declarations with 11,627
remaining, and the XAML compiler remains pre-MVP. SkiaSharp's official metadata
ledger remains closed at 4,222/4,222 with zero missing. The compatibility
profile continues to accept stable SkiaSharp 2.x through 4.151.1 and
Avalonia.Skia 11.x through 12.1.1 without rewriting consumer assemblies. Its
limits and clean-room provenance remain recorded in
`docs/PROGPU_BINARY_ASSEMBLY_COMPATIBILITY.md`; detailed continuation work is
pinned in `docs/WINUI_API_PARITY.md`, `docs/SKIASHARP_API_PARITY.md`, and
`docs/xaml-compiler/ROADMAP.md`.

## NuGet Packages

- `ProGPU.Backend`
- `ProGPU.Backend.Native`
- `ProGPU.Backend.Dawn`
- `ProGPU.Media`
- `ProGPU.Media.Editing`
- `ProGPU.Media.Scene`
- `ProGPU.WinRT`
- `ProGPU.Windows.Media`
- `ProGPU.Linux.Media`
- `ProGPU.Text.Shaping`
- `ProGPU.Browser`
- `ProGPU.DirectX`
- `ProGPU.Transpiler`
- `ProGPU.Compute`
- `ProGPU.Vector`
- `ProGPU.Text`
- `ProGPU.Fonts.Inter`
- `ProGPU.Fonts.Noto`
- `ProGPU.Scene`
- `ProGPU.Scene.Native`
- `ProGPU.Voxel`
- `ProGPU.Layout`
- `ProGPU.Virtualization`
- `ProGPU.WinUI`
- `ProGPU.Voxel.WinUI`
- `ProGPU.WinUI.Themes.Fluent`
- `ProGPU.WinUI.Charts`
- `ProGPU.WinUI.Designer`
- `ProGPU.Xaml`
- `ProGPU.Xaml.Roslyn`
- `ProGPU.Xaml.SourceGenerator`
- `ProGPU.Xaml.Workspaces`
- `ProGPU.Xaml.Cli`
- `ProGPU.Avalonia`
- `ProGPU.Uno`
- `ProGPU.Dxf`
- `ProGPU.SkiaSharp`
- `ProGPU.BinaryCompatibility`
- `ProGPU.System.Drawing.Common`
- `LibreWPF.Interop`
- `ProGPU.Android`
- `ProGPU.iOS`
- `ProGPU.Android.Media`
- `ProGPU.Apple.Media`

## Avalonia Integration Packages

- `ProGPU.Avalonia.Rendering` `12.1.1-preview.61`
- `ProGPU.Avalonia.SilkNet` `12.1.1-preview.61`
- `ProGPU.Avalonia.Rendering` `11.3.20-preview.61`
- `ProGPU.Avalonia.SilkNet` `11.3.20-preview.61`

These packages are packed on the portable runner and published after the
`0.1.0-preview.61` runtime package set so their exact ProGPU dependencies are
available first.

## Local Package Build

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.61 ./eng/progpu-pack.sh
PROGPU_PACKAGE_OUTPUT=artifacts/packages-avalonia/Release ./scripts/progpu-pack.sh
```

The script writes packages and symbol packages to `artifacts/packages/Release` by default.
Set `PROGPU_PACKAGE_OUTPUT` to use a different folder.
The default `all` group requires macOS with the Android and iOS workloads. Linux
can validate the portable set with `PROGPU_PACKAGE_GROUP=portable`; use
`PROGPU_PACKAGE_GROUP=mobile` on macOS for the two mobile host packages. The
release workflow combines and re-verifies both outputs before publishing.

## Local Package Publishing

`eng/progpu-publish.sh` packs the explicit shipping package set and pushes each package with `--skip-duplicate`; `dotnet nuget push` discovers and uploads the matching symbol package automatically. It requires the API key in the environment and never writes the key to the repository:

```bash
read -rsp "NuGet API key: " NUGET_API_KEY
export NUGET_API_KEY
PROGPU_PACKAGE_VERSION=0.1.0-preview.61 ./eng/progpu-publish.sh
./scripts/progpu-publish.sh
unset NUGET_API_KEY
```

The runtime publisher completes before the Avalonia integration publisher so
the latter's exact ProGPU dependencies are available first. Both targets
default to NuGet.org. Set `NUGET_SOURCE` to publish to another v3-compatible
feed.

## GitHub Actions

- `Build` restores, builds, and runs the main ProGPU test project on Linux, macOS, and Windows, packs portable packages on Linux, and packs mobile packages on macOS.
- `Docs` verifies that README/package documentation stays in sync with the release package list.
- `Browser Pages` publishes the shared browser gallery with WebAssembly AOT and deploys it to GitHub Pages after changes reach `main`.
- `Release` validates and packs portable packages and the Avalonia integration lanes on Linux, packs mobile packages on macOS, verifies the combined runtime dependency closure, publishes runtime packages followed by Avalonia packages, and creates a tag-driven GitHub Release.

Manual releases use `workflow_dispatch` with a package version. Tag releases use tags named `v*`,
for example `v0.1.0-preview.61`.

## NuGet Publishing

Publishing to NuGet.org is intentionally gated:

- Manual workflow runs push only when the `publish` input is true.
- Tag runs that match `v*` push after validation.
- The workflow requires the repository secret `NUGET_API_KEY`.

The publish step uses `dotnet nuget push --skip-duplicate` against `https://api.nuget.org/v3/index.json`.
Tag runs create the matching GitHub Release with `gh release create --generate-notes` and attach the built `.nupkg` and `.snupkg` assets.
