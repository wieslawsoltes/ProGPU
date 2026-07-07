# ProGPU Release Workflow

ProGPU packages are built from the explicit package list in `eng/progpu-package-list.sh`.
The release workflow does not pack samples, tests, diagnostic tools, or framework shim projects.

## NuGet Packages

- `ProGPU.Backend`
- `ProGPU.DirectX`
- `ProGPU.Transpiler`
- `ProGPU.Compute`
- `ProGPU.Vector`
- `ProGPU.Text`
- `ProGPU.Scene`
- `ProGPU.Layout`
- `ProGPU.Virtualization`
- `ProGPU.WinUI`
- `ProGPU.WinUI.Charts`
- `ProGPU.WinUI.Designer`
- `ProGPU.Avalonia`
- `ProGPU.Uno`
- `ProGPU.Dxf`
- `LibreWPF.Interop`

## Local Package Build

```bash
PROGPU_PACKAGE_VERSION=0.1.0-preview.1 ./eng/progpu-pack.sh
```

The script writes packages and symbol packages to `artifacts/packages/Release` by default.
Set `PROGPU_PACKAGE_OUTPUT` to use a different folder.

## GitHub Actions

- `Build` restores, builds, and runs the main ProGPU test project on Linux, macOS, and Windows, then packs the explicit shipping package set.
- `Docs` verifies that README/package documentation stays in sync with the release package list.
- `Release` runs docs validation, restore, build, tests, package creation, artifact upload, NuGet publish, and tag-driven GitHub Release creation with generated release notes.

Manual releases use `workflow_dispatch` with a package version. Tag releases use tags named `v*`,
for example `v0.1.0-preview.1`.

## NuGet Publishing

Publishing to NuGet.org is intentionally gated:

- Manual workflow runs push only when the `publish` input is true.
- Tag runs that match `v*` push after validation.
- The workflow requires the repository secret `NUGET_API_KEY`.

The publish step uses `dotnet nuget push --skip-duplicate` against `https://api.nuget.org/v3/index.json`.
Tag runs create the matching GitHub Release with `gh release create --generate-notes` and attach the built `.nupkg` and `.snupkg` assets.
