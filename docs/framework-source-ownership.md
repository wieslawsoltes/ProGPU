# Framework source ownership and reproducible builds

Framework-specific source is maintained by its framework repository:

| Source owner | Projects | Public identity |
| --- | --- | --- |
| LibreWPF | `src/ProGPU.Wpf.Interop` | `LibreWPF.Interop` package; `ProGPU.Wpf.Interop` assembly |
| LibreWPF | `src/ProGPU.Compatibility/WindowsBase`, `PresentationCore` | Existing private compile-time assembly identities |
| LibreWinForms | `src/ProGPU.Drawing/System.Drawing.Common`, `.Tests`, `.Benchmarks` | `ProGPU.System.Drawing.Common` package; `System.Drawing.Common` version `10.0.0.0` |

The move preserves original implementation/test bytes, public types, signing key
and package IDs. The original files and history remain available in ProGPU commit
`3f1454a01dba3d6338c56fabfde7bf6a160c5c62`. Independent Microsoft reference inputs
are unchanged. Native/managed renderer algorithms and platform admission are
unchanged; this work changes source ownership and the build/package graph.

Before a source build, run:

```sh
git submodule update --init --recursive
python3 eng/progpu-prepare-framework-sources.py
dotnet build src/ProGPU.Samples.Desktop/ProGPU.Samples.Desktop.csproj -c Release
```

`eng/framework-sources.json` records full LibreWPF and LibreWinForms commit SHAs.
The preparer creates sparse, detached checkouts under `external/LibreWPF` and
`external/LibreWinForms`, without initializing their nested ProGPU submodules.
It rejects dirty source checkouts, unexpected remotes and symbolic-link targets.
These source dependencies are ordinary disposable build inputs; they are not
additional development worktrees. Keep local changes before refreshing them.

The current source refresh selects LibreWPF
`f9fc6aa1578a97cd6cf7ba8393c6cbd0cdba1b33` and LibreWinForms
`72deff85037420f9c8b4ce7de82de336bbc6193f`. This includes the source taskbar
interop contract, exact device text rows, integer border coverage and corrected
Drawing smoothing/text state. The native-context state control expects the
original `None` smoothing and `SystemDefault` text defaults both before saving
and after restoration; its transforms and recorded drawing commands stay exact.
An unchanged manifest would omit these framework fixes from producer packages.
The complete producer and downstream package/application gates remain required
for this combined source graph; framework CI alone is not its qualification.

Local coordinated development can supply `LibreWpfSourceRoot` and
`LibreWinFormsSourceRoot` as MSBuild properties, or environment variables for
the packaging scripts. Pass an explicit `ProGpuSourceRoot` as a global MSBuild
property when building against a different producer; paths work with or without
a trailing separator. All core dependencies refer back to that one producer.
Project references do not introduce extra global-property variants that would
build the same core output concurrently. Direct project builds
also recognize the documented producer sparse-checkout layout. A standalone
framework checkout uses its own pinned ProGPU submodule.

ProGPU continues to assemble the existing package closure from these recorded
source owners. No package is renamed and no dependency is dropped: Drawing still
depends on the real Native and Dawn backends and their exact static SDK/runtime
payloads. Package metadata and README identify the source owner. Consumers retain
exact version, strong-name, source/package hash and native receipt checks.

The whole producer Build must succeed before consumers stage native payloads.
Both framework repositories still require their complete source/package and
application gates before release. A source relocation or isolated project build
does not qualify native rendering, Display text, input, or desktop behavior.
