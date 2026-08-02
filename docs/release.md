# ProGPU Release Workflow

ProGPU packages are built from the explicit package list in `eng/progpu-package-list.sh`.
The release workflow does not pack samples, tests, diagnostic tools, or framework shim projects.
It also builds the separately versioned Avalonia 11 and 12 integration packages
from `scripts/progpu-package-list.sh`.

Preview.39 continues the media and SkiaSharp performance line from preview.38.
It retains the native zero-copy media player, standalone editing packages,
WebGPU effects, packed retained paths, shared immutable surface snapshots,
compact typed gradients, inline rounded-rectangle radii, and the complete
SkiaSharp 4.151.0 public metadata ledger. This release moves immutable
`SKImage` format, color-space, portable-pixel, row-width, texture
classification, and ownership state into one shared root storage object. Each
zero-copy subset keeps only its dimensions, composed origin, storage reference,
and lazy optional state. Exact checksums, native
comparisons, managed allocation counters, macOS Instruments, EventPipe, and
Metal System Trace are recorded in
`docs/SKIASHARP_API_PARITY.md`. WinUI remains at 4,952 exact of
16,579 official declarations with 11,627 remaining. These numbers are explicit
continuation ledgers; metadata equality alone is not a behavioral compatibility
claim. Detailed work remaining is
pinned in `docs/WINUI_API_PARITY.md`, `docs/SKIASHARP_API_PARITY.md`, and
`docs/xaml-compiler/ROADMAP.md`.

## Preview.39 closure and continuation

The release boundary includes the reusable, framework-neutral media engine,
native platform media/audio providers, WebGPU presentation and effects, the
standalone `ProGPU.Media.Editing` project, and the media-player/editor samples.
The WinUI-shaped media surface remains reusable by Avalonia, LibreWPF, and
LibreWinForms without making the editor API part of the official WinUI parity
claim.

The next WinUI parity branch starts from the immutable preview.39 tag. It must
retain the official NuGet metadata comparator and proceed through API-contract
markers, retained WebGPU Composition families, behavior-complete XAML control
and property-system clusters, removal of accidental ProGPU-only declarations,
and matched rendering/performance validation. The exact baseline remains 4,952
of 16,579 declarations; behavior, accessibility, device-loss, and rendering
quality remain independently gated.

The XAML compiler remains pre-MVP. Preview.39 retains preview.38's automatic
projection of changed
stable XAML identities to detached Roslyn metadata diagnostic origins. The five
remaining product blockers are runtime capability adapters; atomic metadata
apply, XAML publication, joint commit, and recovery; namescope/resource/template
fine patching with safe fallback; cross-platform stress, performance, visual,
accessibility, and collectible-context gates; and published-feed host and
productization evidence.

SkiaSharp's official metadata ledger remains closed at this boundary. Three
alternating exact-checksum Apple M3 Pro Release pairs measured immutable image
subset construction/disposal at `26.450 ns/op`, down from preview.38's
`29.312 ns/op` (`9.76%`), and managed allocation at `65.693 B/op`, down from
`105.694 B/op` (`37.85%`). This is `10.82%` more operations per second. The
official SkiaSharp 4.151.0 comparison measured `339.927 ns/op` and `104.021`
managed B/op versus ProGPU's `26.450 ns/op` and `65.693` managed B/op; the
official counter excludes native allocations, so no total-memory comparison is
claimed. Matched Time Profiler, EventPipe, Allocations plus VM Tracker, and
Metal System Trace lanes corroborate the sustained view-path improvement and
show identical target Metal resource counts and peak allocation.
Remaining behavioral and performance depth is tracked explicitly in
`docs/SKIASHARP_API_PARITY.md`.

## NuGet Packages

- `ProGPU.Backend`
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
- `ProGPU.System.Drawing.Common`
- `LibreWPF.Interop`
- `ProGPU.Android`
- `ProGPU.iOS`
- `ProGPU.Android.Media`
- `ProGPU.Apple.Media`

## Avalonia Integration Packages

- `ProGPU.Avalonia.Rendering` `12.0.5-preview.39`
- `ProGPU.Avalonia.SilkNet` `12.0.5-preview.39`
- `ProGPU.Avalonia.Rendering` `11.3.18-preview.39`
- `ProGPU.Avalonia.SilkNet` `11.3.18-preview.39`

These packages are packed on the portable runner and published after the
`0.1.0-preview.39` runtime package set so their exact ProGPU dependencies are
available first.

## Local Package Build

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.39 ./eng/progpu-pack.sh
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
PROGPU_PACKAGE_VERSION=0.1.0-preview.39 ./eng/progpu-publish.sh
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
for example `v0.1.0-preview.39`.

## NuGet Publishing

Publishing to NuGet.org is intentionally gated:

- Manual workflow runs push only when the `publish` input is true.
- Tag runs that match `v*` push after validation.
- The workflow requires the repository secret `NUGET_API_KEY`.

The publish step uses `dotnet nuget push --skip-duplicate` against `https://api.nuget.org/v3/index.json`.
Tag runs create the matching GitHub Release with `gh release create --generate-notes` and attach the built `.nupkg` and `.snupkg` assets.
