# ProGPU Release Workflow

ProGPU packages are built from the explicit package list in `eng/progpu-package-list.sh`.
The release workflow does not pack samples, tests, diagnostic tools, or framework shim projects.
It also builds the separately versioned Avalonia 11 and 12 integration packages
from `scripts/progpu-package-list.sh`.

Preview.62 advances the successfully published preview.61 boundary with a
second measured optimization round for the ProGPU-backed SkiaSharp compatibility
layer. Avalonia's common bounded SaveLayer containing one analytic rounded
rectangle now retains the exact `PushClip`, `DrawRoundedRect`, `PopClip` stream
in typed fields instead of allocating a general picture-command collection,
transform table, and per-layer typed arrays. A single cleared canvas-local
transient context can be reused by sequential layers; nested layers cannot
borrow an active context and the slot cannot grow into an unbounded pool.

The optimized path preserves geometry, clip and draw transforms, presentation
dependencies, antialiasing, pen semantics, effect ownership, and command order.
All other layer shapes continue through the existing exact compact picture
path. This is a SkiaSharp front-end storage change: the managed and native scene
compilers receive the same expanded commands, so no one-sided renderer change
is required.

## Preview.62 retained SaveLayer optimization closure

The alternating three-process Release comparison uses official SkiaSharp
4.151.0 with 32 warmups and 24 samples in each process. All 62 semantic
checksums match. The `avalonia-layer-recording` median improves from 3,847.625
to 2,673.188 ns/op (-30.5%), p95 from 14,958.313 to 4,333.313 ns/op (-71.0%),
and managed allocation from 8,189 to 6,131 B/op (-25.1%). The ProGPU-to-official
median ratio improves from 6.808 to 4.512; official allocation counters exclude
native Skia command storage, so they are not treated as equal total-memory
accounting.

Matched 50,000-operation Xcode Allocations/VM Tracker, Time Profiler, and Metal
System Trace captures preserve the checksum. Managed allocation falls from
3,309 to 1,205 B/op (-63.6%); persistent heap plus anonymous VM changes by
+0.16%, with zero target Metal resources, submissions, waits, spills, potential
hangs, hang risks, or command-buffer errors. The Release validation includes
the 110-test `SkCanvasStateTests` suite, 3,809 core tests, 240 headless tests,
Avalonia and Silk.NET contract lanes, all 307 XAML tests, and the official
SkiaSharp metadata gate at 4,222/4,222 with zero missing declarations. Detailed
research, complexity, rejected experiments, and profiler evidence are recorded
in `docs/AVALONIA_SKIA_RETAINED_COMMAND_STREAM_RESEARCH.md`.

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
- `ProGPU.CAD`
- `ProGPU.SkiaSharp`
- `ProGPU.BinaryCompatibility`
- `ProGPU.System.Drawing.Common`
- `LibreWPF.Interop`
- `ProGPU.Android`
- `ProGPU.iOS`
- `ProGPU.Android.Media`
- `ProGPU.Apple.Media`

## Avalonia Integration Packages

- `ProGPU.Avalonia.Rendering` `12.1.1-preview.62`
- `ProGPU.Avalonia.SilkNet` `12.1.1-preview.62`
- `ProGPU.Avalonia.Rendering` `11.3.20-preview.62`
- `ProGPU.Avalonia.SilkNet` `11.3.20-preview.62`

These packages are packed on the portable runner and published after the
`0.1.0-preview.62` runtime package set so their exact ProGPU dependencies are
available first.

## Local Package Build

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.62 ./eng/progpu-pack.sh
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
PROGPU_PACKAGE_VERSION=0.1.0-preview.62 ./eng/progpu-publish.sh
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
for example `v0.1.0-preview.62`.

## NuGet Publishing

Publishing to NuGet.org is intentionally gated:

- Manual workflow runs push only when the `publish` input is true.
- Tag runs that match `v*` push after validation.
- The workflow requires the repository secret `NUGET_API_KEY`.

The publish step uses `dotnet nuget push --skip-duplicate` against `https://api.nuget.org/v3/index.json`.
Tag runs create the matching GitHub Release with `gh release create --generate-notes` and attach the built `.nupkg` and `.snupkg` assets.
