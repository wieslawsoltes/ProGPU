# Native MIL rebase onto merged CAD main

## Boundary and provenance

PR #155 merged at 2026-09-10 08:28:30 UTC. Its merge commit,
`73cda9a5243e3bea75e0c8c2fc4d4ecdaf44889d`, is the new base for PR #139.
The old feature tip was `549f1b55`; the shared base was `102e39e5`.
All 1,173 non-merge feature commits were replayed, ending at `7825173e`.
Historical main-merge commits were flattened, not reintroduced as merge commits.
Conflict resolutions retain both upstream CAD contracts and native MIL contracts.

The original dirty ProGPU checkout was not reset, stashed, cleaned or built.
Rebase and compilation use an isolated linked worktree. The earlier incomplete
merge worktree is preserved as a recovery/reference snapshot, not a build input.

Acceptance application: the source-built LibreWPF MVP, with ProGPU's existing
CAD samples retained as upstream consumers. User actions: native startup and
retained drawing, including optional viewport materials. Blocking path: the
shared native scene ABI, shader storage/pipelines and managed source adapter.
Bounded outcome: integrate latest main without dropping either implementation;
this is not a new Direct2D/Win2D expansion or application qualification.

## Integrated contracts

- Native ABI **4** replaces ABI 3. Mesh wire records are 264 bytes: CAD image
  resource/factors remain at offsets 248/252; WPF light offset/count are 256/260.
  The internal 16-byte-aligned GPU record is 272 bytes, including explicit tail
  padding. Generated C# layouts, native asserts and package consumers agree.
  Old native payloads must not be paired with the new managed assemblies.
- CAD image/tiling flags retain bits 0–2. Front/back/specular use bits 3/4/5;
  edge-display bits 8–11 remain unchanged. Triangle and strip face pipelines,
  edge/occluded-edge pipelines, material images, source lights and gradient
  buffers retain their independent lifetimes.
- Nine storage bindings have stage-specific visibility (six vertex, five
  fragment), rather than requesting nine buffers in every shader stage.
- Managed CAD edge shaders retain the complete 560-byte mesh array stride,
  including source light/gradient fields, even though edge classification does
  not consume those fields. The existing three-shader declaration/stride
  fixture remains authoritative; omitting the tail would misaddress later meshes.
- CAD shading values 0–6 remain unchanged. `WpfLighting=7` explicitly selects
  the source uniform-light path even with an empty light range. Source unlit
  material callers select `Flat=2`, not the old ambiguous numeric zero.
  LibreWPF's source adapter and qualification fixture migrate together.
- Canonical rational quadratic/cubic winding remains in the shared
  `PathRasterizerCommon.wgsl` after the feature's staged rasterizer extraction.
  Rational-curve tests and staged-rasterizer source fixtures are both retained.
- Shared brush validation retains CAD hatch-pattern sets and source pad/conical
  outside-color flags; inappropriate gradient flags remain rejected for hatches.
- Retained depth uses main's per-operation pass/bundle ownership while retaining
  feature cache identity, revision and target-preservation contracts.
- Package classification includes 77 projects. Qualified checkout provenance and
  recursive submodules remain enabled together in CI. No CI gates are bypassed.

Automatic-merge cleanup removes duplicate finite-vector helpers, shader writes
and depth fields. A root EditorConfig boundary prevents an enclosing unrelated
checkout's analyzer severities from leaking into this repository's dependencies;
it neither disables analyzers nor edits ACadSharp's pinned source/configuration.

## Build and qualification status

The managed renderer/test/sample dependency graph compiled in Release after
recursive dependency initialization. Native benchmark sources also compiled.
The native build-only lane includes wgpu-native, Dawn, SDK static libraries and
test/sample compilation, with Apple Clang C++20 and warnings as errors.
The macOS ARM64 native graph completed all 351 initial targets (with incremental
rebuilds after integration fixes); both provider libraries and all configured
test/sample executables linked. This uses the supported header compatibility
mode, not C++ module qualification. The final managed graph build completed with
zero warnings/errors in 60.01 seconds; benchmark compilation took 20.75 seconds.

Generated native contracts and MIL coverage were regenerated from the rebased
tree; coverage remains 105 top-level and 25 render-data commands, with 11
undispatched commands. These counts are not parity claims.

Runtime tests, shader execution, export/package verifiers, Windows/Linux/macOS
application comparisons, performance measurements and PR CI review remain in
the final qualification phase. Compilation and staged files do not qualify SDK
startup, native input, modality, device recovery or broader DirectX/Direct2D
parity. Existing delivery gates and deferred scope remain unchanged.
