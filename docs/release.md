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

The ProGPU.CAD plan grid now preserves exact active-VPORT isometric drafting
state. Left, Top, and Right use their documented 90/150, 30/150, and 30/90
degree pairs; point acquisition chooses the Euclidean-nearest equal-aspect
triangular-lattice point with fixed O(9), allocation-free work; and Ortho uses
the active pair. The shared desktop/browser shell edits SNAPUNIT, SNAPSTYL, and
SNAPISOPAIR in the same generation-safe grid command. Isometric display reuses
the existing one-quad affine dot primitive because lined GRIDSTYLE does not
follow the isometric lattice. The pinned ACadSharp.ProGPU fork now emits VPORT
DXF groups 77/78, with DXF/DWG round-trip coverage and no native ABI or shader
fork.

The same desktop/browser CAD shell now cycles drawing-persisted SNAPISOPAIR
Left to Top to Right with F5 or Ctrl+E. Each cycle changes only the active
VPORT pair as one generation-safe reversible edit, immediately refreshes the
isometric grid and Ortho basis, retains a dormant plane under rectangular
SNAPSTYL, and blocks instead of discarding staged grid-panel values. The browser
reserves both shortcuts from its reload/navigation defaults. This input/edit
follow-up reuses the existing paired renderer and changes no shader or native
ABI.

Shared desktop/browser F8 now edits drawing-persisted ORTHOMODE through one
exact reversible command, while F10 retains profile-scoped Polar Tracking and
changes the drawing only when mutual exclusion must commit ORTHOMODE=0. The
controls use the same paths, active point prompts are reevaluated immediately,
Undo/Redo refreshes both constraint state and controls, staged grid-panel input
is preserved, and browser defaults are reserved. The pinned ACadSharp.ProGPU
fork now emits `$ORTHOMODE` in default DXF header output with three versioned
round-trip regressions. No shader, native ABI, GPU resource, or native scene
compiler changes.

Shared desktop/browser F9 and the Grid/Polar snap controls now edit drawing-
persisted active-VPORT SNAPMODE through one exact reversible command, while
SNAPTYPE and POLARDIST remain profile-scoped as Autodesk specifies. PolarSnap
quantizes only an acquired polar path, uses Snap X spacing when its configured
distance is zero, keeps object snaps authoritative, and leaves typed direct
distance exact. Grid and Polar types are mutually exclusive, a disabled F9
retains its type, active prompts refresh immediately, staged grid input is
protected, and the browser reserves F9. Exact DXF/DWG SNAPMODE round trips and
zero-allocation warm queries pass. This input/edit slice changes no shader,
native ABI, GPU resource, or native scene compiler.

Shared desktop/browser polar tracking now supports the profile-scoped
POLARADDANG contract: up to ten semicolon-separated invariant-degree angles,
explicit enablement, absolute non-incremental path arbitration, periodic
normalization, and immediate pending-prompt reevaluation. The ten values live
inline and every warm pointer query remains allocation-free bounded `O(A)` for
`A <= 10`. Object snap still wins, additional paths compose with PolarSnap, and
typed direct distance preserves its exact length. Invalid lists fail closed and
produce no drawing generation. Last-segment-relative POLARMODE is never guessed
from MOVE/COPY state and now uses only a real LINE segment context. No shader,
native ABI, GPU resource, or native scene compiler changes.

Shared desktop/browser ProGPU.CAD now provides bounded LINE authoring. Clicks or
typed absolute/relative Cartesian and polar points create a contiguous sequence
of separate LINE entities; object snap, grid, Ortho, polar, PolarSnap, and exact
direct distance reuse the shared point-acquisition pipeline. Relative polar mode
uses the actual previous authored segment and fails closed when none exists.
Accepted segments remain a retained transient picture, `U` removes only the
latest segment, Close is available after two segments, and Enter/Escape finish.
Completion captures current entity properties and publishes the entire sequence
as one generation-safe Undo/Redo command with DXF/DWG round trips. The ordinary
line renderer is unchanged, so this adds no shader, native ABI, or one-sided
managed/native rendering path.

The shared ProGPU.CAD plan grid now defaults to AutoCAD's lined model-space
GRIDSTYLE and exposes a shared desktop/browser Dots toggle. Autodesk documents
GRIDSTYLE as registry-backed host state, so the toggle intentionally changes no
DXF/DWG value or document generation. Minor lines remain one physical pixel and
every persisted GRIDMAJOR line is two physical pixels under affine rotation,
anisotropic scale, and shear. Dots and lines use the same one-quad canonical
managed/native shader path with no new ABI record or per-lattice upload.

The ProGPU.CAD continuation also adds atomic multi-source populated-layer merge.
The shared desktop/browser shell queues generation-stamped source layers and
commits them to one explicit target as one history generation; Undo restores
every exact entity and viewport frozen-layer reference. DXF/DWG round trips and
native picture replay cover the result. The ACadSharp feature branch supplies a
preflighted range removal with one immutable typed notification, while its master
branch remains synchronized with upstream. This is a document/UI transaction
change, not a renderer optimization, and makes no performance-improvement claim.

The ProGPU.CAD publishing path now writes bounded white-paper raster PDF and
single-page PNG output directly from retained physical print-job pages. It
preserves mixed media, quarter-turn orientation, collated/uncollated copy order,
explicit DPI, physical lineweights, plotted ACI 7, and shaped retained content;
PDF embeds the exact same RGB raster verified against PNG. Per-page, total-pixel,
dimension, and encoded-byte budgets plus staging make validation, replay,
encoding, and pre-commit cancellation destination-safe. The shared
desktop/browser shell exposes supported-selected-page PDF/PNG picker output.
This is a portable CPU publishing adapter; vector PDF/SVG, printer submission,
GPU readback, color management, and GPU/native pixel certification remain.

The shared ProGPU.CAD desktop/browser editor now supports selected-set
`Move points…` and `Copy points…`: one WCS-XY base point plus one second point
define the exact displacement. Hovering records only a fixed-device guide and
translated selection bounds, with no entity mutation, snapshot publication, or
scene rebuild; the second click commits through the existing one-generation
transactional MOVE/COPY commands. Escape and document/selection teardown cancel
without edits, source handles remain selected, and coincident MOVE/COPY behavior
is explicit. The same prompts now accept bounded typed coordinates and exact
running Intersection/Endpoint/Midpoint/Center/Quadrant/Node/Nearest points and
base-referenced Perpendicular or Tangent second points;
Nearest covers documented linear, conic, polyline, POINT, and rational-spline
families without flattening; Tangent keeps every exact root on documented conic,
bulge-arc, and rational-spline families. Grid, remaining object snaps,
UCS/arbitrary-camera planes, COPY Multiple, and full geometry ghosting remain.
This workflow changes no renderer,
shader, GPU/native ABI, or persistence contract and makes no performance-
improvement claim.

The same continuation adds bounded external `.lin` library loading and reload to
the shared desktop/browser shell. A clean-room ASCII parser retains simple,
text, and SHX-shape descriptors; one reversible command rejects collisions or
replaces existing definitions without changing their object identity, handles,
or references. Upright `U=` entries remain typed and are reported as unchanged
because the persisted dependency flags cannot distinguish them. Focused parser,
rollback, Undo/Redo, DXF/DWG persistence, UI, and managed/native picture tests
cover the workflow. ACadSharp feature commits `ff65795e` and `3d074ec4` provide
atomic segment replacement and restore DXF complex-linetype STYLE handles, while
ACadSharp `master` stays exactly synchronized with upstream. This is document IO
and edit orchestration, changes no renderer hot path, and makes no performance
claim.

ProGPU.CAD now includes an explicitly non-browser path store over its existing
caller-owned stream API. Path saves serialize to a unique same-directory file,
defer the session saved-generation commit, flush and close the staged file, and
replace the destination only after successful serialization and cancellation
checks. Failures preserve the prior destination, keep the session dirty, and
clean only the owned staging file. Auto save format recognizes `.dxf`/`.dwg`;
path loads retain the normalized absolute source name while keeping content
detection and resource limits in the stream store. DXF/DWG round trips,
replacement, cancellation, serialization/commit failure, progress, cleanup,
and missing-directory policy are covered. This is filesystem orchestration and
does not change rendering, shaders, native ABI, or managed/native parity.

The CAD shell now exposes bounded block-attribute value editing for exactly one
selected INSERT. A generation-tagged catalog distinguishes reference-owned
variable ATTRIB values from definition-owned constant ATTDEF values and variable
ATTDEF defaults, including explicit duplicate-tag occurrences and
multiline/hidden metadata. All three commands update single-line plus embedded
MTEXT payloads transactionally. Constant edits retain block-definition identity
and affect every INSERT instance; variable-default edits preserve assigned
values on existing references while future INSERTs inherit the new default.
Undo/Redo, DXF/DWG round trips, shared desktop/browser controls, and
managed/native retained-text compilation are covered. The existing TEXT/MTEXT
renderer, caches, shaders, and native ABI are unchanged, so this slice makes no
rendering-performance claim.

ProGPU.CAD now parses and renders regular and extended compiled AutoCAD-86 Big
Fonts through a clean-room indexed container, strict persisted drawing-code-page
mapping, immutable primary/Big-Font resolution, and the existing retained SHX
path pipeline. Extended composite primitives preserve caller advance, apply
independent documented width/height scaling, and retain transformed arcs as
analytic ellipses. TEXT, horizontal MTEXT, complex linetypes, ordered desktop
discovery, exact selection, printing, managed/native picture replay, and
DXF/DWG code-page/style/content round trips share the same font pair. The change
adds no shader, texture, upload, or native ABI and makes no performance claim;
format provenance and the cross-engine audit are recorded in
`PROGPU_CAD_SHX_BIGFONT_RESEARCH.md`.

ProGPU.CAD now also honors the remaining compiled UNIFONT metadata roles.
Encoding 1 performs strict one- or two-byte character-identity mapping through
the drawing's persisted code page for TEXT, horizontal MTEXT, complex
linetypes, selection, printing, and managed/native retained replay. Encoding 2
is a typed non-text shape-file role: it is excluded from text and alternate-font
resolution while its named and numbered definitions reuse the standalone SHAPE
pipeline. Original synthetic fixtures cover DXF/DWG persistence and bounded
failure behavior; independent AutoCAD conformance with licensed encoding-1 and
encoding-2 artifacts remains an explicit gate. No shader, native ABI, or replay
resource contract changed, and no performance or image-quality improvement is
claimed. The evidence boundary and cross-engine audit are recorded in
`PROGPU_CAD_SHX_PACKED_UNIFONT_RESEARCH.md`.

Definition entries in that catalog now also expose their persisted ATTDEF
prompt. The shared shell can edit either constant or variable definition
prompts through the selected INSERT with explicit tag/occurrence addressing,
the documented 256-code-unit limit, locked-layer authorization, and exact
Undo/Redo identity. Assigned ATTRIB values are never rewritten; prompt changes
survive DXF/DWG round trips and affect future insertion interaction only. This
is document/history state, so managed/native picture replay, shaders, caches,
uploads, DPI behavior, and device-loss contracts remain unchanged.

The same definition-only surface can rename an ATTDEF tag. New tags use the
DXF 256-code-unit boundary, reject whitespace and `!`, normalize to uppercase,
and retain duplicate-occurrence addressing. Existing ATTRIB tags and assigned
values stay exact until the separate `Sync properties` edit is requested; Sync
then applies the new tag without changing the assigned value. Exact definition
identity/order validation, locked-layer rejection, Undo/Redo, duplicate tags,
DXF/DWG persistence, and shared-shell behavior are covered.

Definition-only mode editing now covers Invisible, Verify, Preset, and Lock
position while preserving constant and multiline ownership. Variable reference
modes change only through the explicit synchronization edit; assigned values
remain exact. Invisible changes flow through the existing snapshot visibility,
printing, managed picture, and native-picture paths with no new shader or cache.
ACadSharp feature commit `faf19483` adds version-aware DXF group-280 position
lock persistence for both ATTDEF and ATTRIB; DWG already carried that field.
Focused mode, duplicate-occurrence, locked-layer, Undo/Redo, synchronization,
managed/native retained-output, shared-shell, and DXF/DWG regressions cover the
slice.

Attribute-definition Constant mode is now a structural, bounded edit rather
than a cosmetic flag change. One command transitions single-line or multiline
ATTDEF ownership and synchronizes every reference to the selected block:
variable-to-constant removes ATTRIBs, constant-to-variable creates transformed
references from the definition default, and Undo/Redo retains exact values,
embedded MTEXT, handles, order, and XData. Synchronization now emits references
only for variable definitions and removes malformed constant references.
ACadSharp feature commit `cb6d92ec` applies the same invariant to new INSERTs
and `UpdateAttributes`. The shared dynamically themed shell exposes the explicit
`Constant (sync all)` control. Managed snapshot and native-picture regressions
confirm the existing retained text path changes ownership without a shader,
ABI, cache, upload, DPI, or device-loss fork.

The same selected-INSERT workflow now includes a bounded `Sync properties`
edit. It synchronizes entity, text, tag, mode, and transform-baked geometry
properties from the block definitions across every registered reference while
preserving assigned values. Duplicate tags and tag renames retain deterministic
value ownership; the complete batch is preflighted and exactly reversible.
Locked references, XRef/unloaded or dynamic blocks, malformed multiline data,
and source/target collections above the bounded limits are rejected before any
mutation. Structural differences now add default-valued references, remove
obsolete references, and restore exact ATTRIB/SEQEND identities and handles
through Undo/Redo. Inactive handles live only in private history-owned leases;
capacity eviction, redo replacement, divergence reset, and Clear release them.
Following Autodesk's ATTSYNC warning, the same transaction now clears bounded
XData application payloads from each matching INSERT and its ATTRIB/SEQEND
sequence while leaving definition-owned BlockRecord/ATTDEF XData intact. Undo
restores the exact registered AppId and payload identities, Redo clears them
again, and the shared shell reports the cleared payload count. ACadSharp feature
commit `ac9301e5` supplies the constant-time application-entry count used by the
normal XData-free preflight path.
Focused semantic, lease-lifecycle, persistence, shared-shell, and managed/native
retained-picture regressions cover the slice.

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
- `ACadSharp.ProGPU`
- `ProGPU.CAD`
- `ProGPU.CAD.Native`
- `ProGPU.SkiaSharp`
- `ProGPU.BinaryCompatibility`
- `ProGPU.System.Drawing.Common`
- `LibreWPF.Interop`
- `ProGPU.Android`
- `ProGPU.iOS`
- `ProGPU.Android.Media`
- `ProGPU.Apple.Media`

`ACadSharp.ProGPU` is the net10.0 package built from the reviewed ACadSharp
feature commit pinned by the ProGPU submodule. It is packed before `ProGPU.CAD`
at the same ProGPU version; `ProGPU.CAD` has an exact dependency on that distinct
identity so NuGet cannot substitute upstream `ACadSharp`.

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
