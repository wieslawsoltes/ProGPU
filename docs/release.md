# ProGPU Release Workflow

ProGPU packages are built from the explicit package list in `eng/progpu-package-list.sh`.
The release workflow does not pack samples, tests, diagnostic tools, or framework shim projects.
It also builds the separately versioned Avalonia 11 and 12 integration packages
from `scripts/progpu-package-list.sh`.

Preview.56 advances the successfully published preview.55 boundary with
retained-scene submission, effect, clipping, picture-cache, and resource-
lifetime improvements. Eligible stable managed and native scenes can replay
retained WebGPU render bundles, compatible texture draws are batched, and
submission-retirement polling remains bounded. The managed renderer also adds
GPU color-matrix and isolated blend effects plus anisotropic drop shadows with
matched native contracts.

WinUI remains at 4,952 exact of 16,579 official declarations with 11,627
remaining, and the XAML compiler remains pre-MVP. These are continuation
ledgers rather than behavioral-completion claims. Detailed remaining work is
pinned in `docs/WINUI_API_PARITY.md`, `docs/SKIASHARP_API_PARITY.md`, and
`docs/xaml-compiler/ROADMAP.md`.

## Preview.56 closure and continuation

The release includes analytic rectangular and rounded-ring difference clips,
ordered host-backdrop capture, retained picture admission and reuse, texture-
content generation stamps, stable effect-scene reuse, and serialized wgpu-
native lifetime operations. Render-bundle replay fails closed for mutable,
upload-dependent, masked, or effect scenes, and releases bundles before every
captured resource generation. Optional managed WinRT geometry contracts do not
change rendering or the native ABI.

The next WinUI parity branch starts from the immutable preview.56 tag. It must
retain the official NuGet metadata comparator and proceed through API-contract
markers, retained WebGPU Composition families, behavior-complete XAML control
and property-system clusters, removal of accidental ProGPU-only declarations,
and matched rendering/performance validation. The exact baseline remains 4,952
of 16,579 declarations; behavior, accessibility, device-loss, and rendering
quality remain independently gated.

The XAML compiler remains pre-MVP. Preview.56 retains automatic projection of
changed stable XAML identities to detached Roslyn metadata diagnostic origins.
The five remaining product blockers are runtime capability adapters; atomic
metadata apply, XAML publication, joint commit, and recovery;
namescope/resource/template fine patching with safe fallback; cross-platform
stress, performance, visual, accessibility, and collectible-context gates; and
published-feed host and productization evidence.

SkiaSharp's official metadata ledger remains closed at 4,222/4,222 with zero
missing. The release preserves the established Svg.Skia image-difference
inventory while extending retained scene behavior beneath the compatibility
surface. Rendering performance and pixel correctness remain one contract; no
universal performance-superiority claim is made.

The preview.56 implementation boundary passed the managed renderer and headless
suites at 3,796 and 240 tests, the native C++ 9-suite CTest matrix, a byte-exact
managed/native rectangle differential, real Dawn/Metal retained-bundle replay,
and the repository's Build, Docs, Browser Pages, and Svg.Skia parity workflows.
Design, lifetime, complexity, cross-engine research, and matched performance
evidence are recorded in `docs/RETAINED_GPU_SUBMISSION_OPTIMIZATION.md` and
`docs/NATIVE_CPP_PERFORMANCE_BASELINE.md`.

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
- `ProGPU.System.Drawing.Common`
- `LibreWPF.Interop`
- `ProGPU.Android`
- `ProGPU.iOS`
- `ProGPU.Android.Media`
- `ProGPU.Apple.Media`

## Avalonia Integration Packages

- `ProGPU.Avalonia.Rendering` `12.0.5-preview.56`
- `ProGPU.Avalonia.SilkNet` `12.0.5-preview.56`
- `ProGPU.Avalonia.Rendering` `11.3.18-preview.56`
- `ProGPU.Avalonia.SilkNet` `11.3.18-preview.56`

These packages are packed on the portable runner and published after the
`0.1.0-preview.56` runtime package set so their exact ProGPU dependencies are
available first.

## Local Package Build

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.56 ./eng/progpu-pack.sh
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
PROGPU_PACKAGE_VERSION=0.1.0-preview.56 ./eng/progpu-publish.sh
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
for example `v0.1.0-preview.56`.

## NuGet Publishing

Publishing to NuGet.org is intentionally gated:

- Manual workflow runs push only when the `publish` input is true.
- Tag runs that match `v*` push after validation.
- The workflow requires the repository secret `NUGET_API_KEY`.

The publish step uses `dotnet nuget push --skip-duplicate` against `https://api.nuget.org/v3/index.json`.
Tag runs create the matching GitHub Release with `gh release create --generate-notes` and attach the built `.nupkg` and `.snupkg` assets.
