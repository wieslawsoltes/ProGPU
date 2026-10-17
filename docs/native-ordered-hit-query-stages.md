# Ordered GPU hit-query stages

## Scope and status

Acceptance application: **ProGPU.Wpf.ShowcaseApp**. User action: pointer selection
and geometry-region queries against a presented native MIL owner index. The
blocking path is the Windows system-WARP execution of the canonical query shader.
The managed and C++ product dispatchers support ordered stages using retained
device/index-owned resources. Automatic selection now chooses this GPU path on
owned D3D12/FXC and D3D12/DXC devices; other devices and explicit caller choices are unchanged.
See the [compiler-specific selection decision](#automatic-d3d12fxc-selection).
The DXC extension and its pending qualification are recorded below.
Final application/package qualification remains required before merge. The
six-binding point/bounds/ellipse entrypoints remain available and independent.

The staged probe now passes all **120 complete result buffers** on system ARM64
WARP with the verified DXC configuration, matching the independent Metal/original
shader reference. This closes the isolated staged-query differential, not the
final package/default or Showcase qualification gates. The full native consumer
also passes with the product dispatcher on Metal and system ARM64 WARP/DXC;
the Windows package-production/default configuration remains a separate gate.

The implementation is original ProGPU code, refactored from
`src/ProGPU.Vector/Shaders/GpuHitTesting.wgsl` at `20bbf7f1`. No foreign renderer
implementation was copied. Both product providers embed that canonical file.
The staged dispatcher in `tests/ProGPU.HitQuery.ContractTests` is native-WebGPU
test code, not a third renderer or CPU geometry fallback.

## Contract

1. A shared resumable BVH iterator emits candidates in the original node/local
   reference/LIFO-child order, preserving the 64-entry stack and query filtering.
2. A GPU clip pass uses the original world-space clip predicates. It does not
   clear clip metadata: that metadata still controls precise-region admission
   and whether a primitive can fully contain the query.
3. Independent 64-lane primitive-family passes use the original exact query
   predicates, including the existing curve sampling, caps and tolerances.
4. A single invocation merges surviving candidates in original traversal order,
   using the shared ordered owner insertion logic. Summary participation and
   traversal counters are reduced locally and published explicitly.

Grouping complete queries by primitive family was rejected: equal-depth overwrite,
duplicate-owner retention, insertion and full-list rejection depend on original
order. Sorting by depth alone does not reproduce those rules.

Internal binding 7 stores a 32-byte header and eight-byte candidate records. The
header contains count, overflow and a three-word indirect dispatch at offset 16.
A GPU copy transfers those 12 dispatch bytes to a separate indirect buffer;
writable storage and indirect reads must not alias in a dispatch usage scope.
Normal probe execution uses one submission and no intermediate CPU readback.
`--trace-stages` deliberately submits and waits after each stage for fault
isolation; its timings are not normal-path performance measurements.

The legacy entrypoints cannot reach candidate storage through their call graph.
A constant false argument alone was insufficient for automatic layout inference;
the shared iterator keeps traversal reuse without adding a seventh legacy binding.
Without a forced execution policy, the probe also executes actual
`GpuHitTestEngine` calls to check the six-binding product layout. `--product`
requires the resolved ordered path and compares public product queries too.

For N primitives, K candidates, S path segments and result capacity R, average work
is O(log N + K*(S+R)), worst-case O(N*(S+R)). Staged scratch is O(M) for M retained
index references; classification uses nine fixed family passes plus clipping.
The original 1/16/24-piece stroke walks now share segment sampling and one
intersection call site per query shape. Evaluation order, first/last cap ownership,
rational weights, unknown-segment exclusion and point endpoint tests are unchanged.

## Reproduction and evidence

```sh
dotnet run --project tests/ProGPU.HitQuery.ContractTests -c Release -- \
  --reference-shader /absolute/original-20bbf7f1.wgsl \
  --write-reference /absolute/new/reference.bin
dotnet run --project tests/ProGPU.HitQuery.ContractTests -c Release -- \
  --expected-results /absolute/reference.bin
```

On Windows, use the existing verified DXC feature/compiler configuration and an
isolated apphost, then pass `--software-adapter --staged-only --expected-results`
with the independently captured reference. Staged-only execution cannot publish
its own reference. References are created without overwriting existing files,
and every byte of all 17 result slots is checked, including counters and unused
slots. Missing/truncated/trailing reference data and candidate overflow fail.

The expanded fixture has 160 interleaved primitives, duplicate owners, equal and
mixed depths, all eight defined primitive kinds plus an unknown-kind control,
curved clips, point/region-only and invisible records, transformed strokes, all
cap kinds, line/quadratic/cubic/arc/rational/unknown/degenerate segment cases.
It checks three query shapes, four capacities and ten locations: **120 queries**.
macOS Metal matches the pre-stroke-refactor reference byte-for-byte, as well as
the original shader at `20bbf7f1` and current legacy/staged GPU implementations.
CI fetches that exact baseline shader for the macOS/Linux differential and retains
the reference artifacts; existing Windows/native package gates remain unchanged.
The smaller 60-query reference
also qualified the shared iterator before stroke sampling was consolidated.

Reference SHA-256:

- 60-query reference: `345a7f19e76fcca47819b2753251c6375ac9f8b3221d1d9e27505c0dafb66bcb`.
- 120-query pre-stroke reference: `d89271ad04fb1d944f86fa5352f37b360955e52d79f58dcccc3e8955c5653275`.

System ARM64 WARP first completed all 20 original point cases, then crashed in
the bounds path-stroke stage. Separating clips alone did not fix that crash.
Consolidated stroke sampling completed that stage and all 40 expanded point cases;
the first bounds comparison then exposed a precise-test counter difference
(expected 107, actual 139), with matching owner/intersection output. Moving counters
to local state alone did not resolve that Windows difference. The gate remains
strict; passing hits without passing counters is not qualification.

Counting returned candidates outside the resumable iterator's nested loops
resolved the difference: system WARP then completed all 120 comparisons, including
every counter and unused result slot, with exit 0. The matched run used one normal
submission per query, not the per-stage diagnostic waits. It used DXC 1.8.2502.8,
the pinned feature-enabled wgpu-native binary and system WARP 10.0.26100.9278.
The entire count path remains on the GPU. The deliberately undersized one-record
candidate buffer also fails explicitly in the macOS negative probe.

This dense fixture primarily exercises references retained in the root node;
multi-level traversal, sparse/multi-subtree hits and maximum device capacities
still require dedicated coverage before a staged product default is admitted.

The existing monolithic query still crashed on this system runtime after the
stroke refactor. Do not infer a product repair from the staged probe. Exact loaded
compiler, native dependency and system WARP paths were observed; no testing-only
WARP was bundled and no system files or runtime defaults were changed.

The full `ShaderResourceTests`/`GpuHitTestingTests` project could not build in the
recovered source-only checkout because its pinned ACadSharp and WinUI theme
submodules are absent. An initial `--no-restore` invocation produced no tests and
is not counted as a pass. The independent contract project has no such dependency.

## Required before product admission

- Extend the passing system ARM64/Metal differential to x64, Linux, multi-level
  BVHs and device limits. Retain original deadlines/fences and exact counters.
- Qualify the paired product dispatchers (below) on the final package/host graph,
  including failure recovery, pending owner-map leases and large admitted device
  capacities. Oversized indices currently reject rather than chunking dispatches.
- Measure cold/warm query latency, allocation and residency against the same
  final legacy/native binaries; retained family selection alone is not a benchmark.
- Finish compiler-feature packaging and final native package/Showcase input,
  popup/DPI/lifetime gates. No dependency pin or merge advances from this probe.

## Shared indirect dispatch — 2026-09-14

`IWebGpuApi.ComputePassEncoderDispatchWorkgroupsIndirect` now forwards the original
compute-pass/buffer identity and 64-bit byte offset through Silk and Dawn. The
browser adds opcode 54 with a 16-byte payload (pass u32, buffer u32, offset u64);
its producer and actual JavaScript decoder reject offsets beyond JavaScript's
exact integer range instead of rounding. Existing packet opcodes/layouts and
version remain unchanged. Buffer bounds, alignment, usages and device ownership
remain WebGPU validation, not CPU argument readback. The C++ Dawn provider resolves
the same procedure through its current device dispatch scope; missing procedures
reject provider initialization. Borrowed devices never enter a native-only API.

The original ProGPU shared API/protocol and procedure-table patterns are the
implementation provenance; no third-party implementation was imported. The
[WebGPU indirect-dispatch contract](https://gpuweb.github.io/gpuweb/#dom-gpucomputepassencoder-dispatchworkgroupsindirect)
defines three u32 arguments in a 12-byte indirect buffer and a four-byte-aligned
offset. Each forwarded command is constant-time with no argument readback or
geometry work; browser packet storage is fixed-size and shared packet batching
is unchanged. GPU buffers/pipelines remain owned by the existing context and
submission lifetime; this API creates no resource or separate queue submission.

Validation:

- All 120 complete original-shader query buffers match using the shared API on
  wgpu-native Metal and Dawn Metal (`--dawn`). Both also match the independently
  recorded pre-refactor reference. The Dawn test borrows `DawnGpuContext.Context`
  and disposes the owning Dawn context once, after query resources.
- The Windows ARM64 system-WARP rerun passes the same 120 independent reference
  comparisons through the shared API (exit 0, DXC). The isolated
  `C:\ProGPU.OrderedQueryStages-System-Strokes-SharedApi` run observed the actual
  staged wgpu-native DLL, pinned compiler pair and system `d3d10warp.dll`; no
  development WARP or system replacement was used. This remains the diagnostic
  dispatcher, not the native package consumer.
- Eleven actual `BrowserWebGpuApiTests` pass in a focused linked test project
  (six new cases). The full repository test project remains dependent on the
  absent source submodules; this focused run is not a full-suite pass.
- `node --experimental-vm-modules eng/progpu-test-browser-indirect-dispatch.mjs`
  executes the actual browser decoder with test resources: four exact offsets,
  two inexact offsets, two malformed payloads and one stale handle. This is
  transport testing, not real-browser device qualification.
- The native provider forwarding fixture compiles with strict C++20/Apple Clang
  against pinned Dawn headers and passes exact handle/u64 forwarding, missing-
  procedure rejection and dispatch-scope restoration. It is also called by the
  full native Dawn contract executable; standalone success is not a full native
  renderer build.
- CI includes browser decoder checks and the independent Dawn/Metal differential,
  in addition to the existing native provider and original-shader query gates.

This shared-API checkpoint preceded the product connection below.

## Paired product dispatch — 2026-09-14

Managed hosts configure `WgpuContext.HitTestExecutionPreference` with
`Automatic`, `SinglePass` or `OrderedStages` before resource construction.
`PROGPU_HIT_TEST_EXECUTION=auto|single-pass|ordered-stages` supplies the process
default; invalid values fail. `HitTestExecutionPath` reports the resolved path.
At this earlier checkpoint Automatic resolved to SinglePass; the subsequent
compiler-specific decision is recorded below. Shared surfaces inherit their actual device
owner's selection and limit snapshot. Raw C hosts opt in using the generated
`PROGPU_NATIVE_ENGINE_ORDERED_HIT_QUERIES` flag (32); old native libraries reject
the unsupported flag rather than selecting another path.

`GpuOrderedHitQueries` belongs to one retained `GpuHitTestDeviceIndex`. It leases
the common layout from the same device resource domain and uses a retained
pipeline cache, compiling only collection, merge, the requested clip shape and
the actual retained primitive families. It retains separate single/list bindings,
an M-record candidate buffer and a 12-byte indirect buffer. Disposing the index
queues the usual completion-safe resource releases. No shader/pipeline/bind-group
creation or index upload repeats for warm queries of an already requested family.

The C++ dispatcher uses the same canonical entrypoints, family mask and ordered
passes. Candidate/argument buffers follow its existing immutable index generation;
pipeline resources follow engine/device lifetime. Index replacement publishes all
new resources together, and failed allocation releases only unpublished resources.
Existing pending token/map, owner snapshot, query completion and browser packing
contracts remain in place. Query dispatch remains one submission; native desktop
readback retains its separate submission for the existing Vulkan hazard contract.

Both sides validate actual storage and compute limits before admission. Native
queries read the owning device limits through its provider. Managed owned devices
capture limits at initialization; Dawn and browser factories publish their actual
device snapshot through immutable `WgpuComputeLimits`. Other borrowed hosts must
provide that snapshot—default/zero limits do not grant staged admission. Existing
external initialization method signatures are preserved. The candidate buffer is
`32 + 8*M` bytes; limits cover buffer/storage size, seven storage bindings, 64 X
invocations and `ceil(M/64)` workgroups. Oversized indices explicitly reject, with
no scalar/managed geometry fallback and no readback to calculate dispatch sizes.

If GPU collection overflows, merge publishes the reserved UINT_MAX summary hit
marker through the ordinary result readback. Admitted M is strictly below that
value, so valid hit counts cannot collide. Both product readers reject the marker
before copying any owner results to the caller; native map/token state is released
as an explicit failed query. This avoids another readback/map lifetime.

### Product evidence and remaining limits

- The full project-reference native consumer passes on Metal and system ARM64
  WARP with DXC, including real retained MIL pixels, native owner/generation
  isolation, repeated waits, participation and region-first queries. The loaded
  native product DLL hash on Windows is
  `16490f220019d0f1125349bc6ec2b1a10e11a1765529d5be75f0a787148235d5`.
  The Windows stdout hash is
  `fd6ad3e6bacb62772714e9eb51ed40385aaafec3bfb84a06b953ff62a9cd2eea`.
  These are staged project-reference binaries, not proof of the final NuGet graph.
- The matched system-WARP/FXC run fails during compute pipeline creation with
  D3DCompile X3511 (forced loop unrolling failed). The pinned wgpu error conversion
  subsequently aborts on a NUL in that diagnostic. No owner-query submission or
  readback completed in this run. Splitting dispatch therefore does not qualify
  FXC: the next Windows release blocker is the DXC-capable dependency/compiler
  package graph, followed by exact packaged runtime and adapter qualification.
- The dense probe matches 120 complete buffers plus 100 public managed product
  queries on wgpu-native Metal, Dawn Metal and system ARM64 WARP/DXC. Public list
  APIs do not execute zero-capacity queries; single-point summary is separately
  covered. Public output comparison retains untouched caller-tail records and
  the managed API's existing -Infinity empty-depth initialization (the raw native
  probe uses -FLT_MAX). No other result fields or tolerances are normalized.
- The sparse fixture adds root-local coverage and separated child subtrees.
  All 168 complete buffers and 140 public queries match the original shader on
  Dawn Metal, current single-pass Metal and staged Metal. The independent original
  Dawn reference SHA-256 is
  `12e78381009b7417e38f04fd88f6f4d6b54f302b0f091a50d756956a1a658530`.
  Historical `20bbf7f1` through wgpu-native Metal reports `nodes_visited=1` where
  Dawn's original shader and the new local reduction report 11 or 13. That old
  compiler-path discrepancy is retained explicitly; counters are not ignored.
  CI cross-checks complete sparse outputs against Dawn's original shader, rather
  than treating the broken old Metal counter as authoritative or waiving it.
- Both C++ providers build on Apple Clang and Windows ARM64 MSVC; 19 native tests,
  20 focused managed policy/browser tests, nine actual browser-decoder checks and
  generated native contract verification pass. The full managed test checkout
  still requires its missing external source submodules.
- Linux/macOS native package lanes now run the full consumer with explicit
  ordered stages in addition to their unchanged default and NativeAOT gates.
  Windows keeps its existing gates; the DXC-enabled package gate still needs the
  feature-capable dependency payload and cannot use the stock FXC-only binary.
- Large-capacity runtime, sparse Windows/x64, browser device execution, exact final
  package CI, qualified automatic selection and Showcase source/runtime gates
  remain required. VM configuration/system libraries were not changed; WARP is
  not redistributed. CPU work here is one dependency-free fixed family-mask
  reduction over strided metadata per index, not geometry or a compute fallback.

This connection changes no font/layout, clip geometry, owner identity, ordering,
sampling quality or source input policy. It shares original ProGPU traversal and
predicates; the cross-engine and WebGPU research decisions above remain applicable.

## Full-capacity paired query qualification — 2026-09-14

Acceptance remains **ProGPU.Wpf.ShowcaseApp** pointer/region selection through
the presented native owner index. This checkpoint closes a missing regression
fixture, not automatic-selection or application admission.

`--large-capacity` adds 320 distinct overlapping owners and 320 higher, lower
and equal-depth duplicates to the existing mixed-shape fixture. Combined with
`--sparse`, it retains root-local references and separated multi-level subtrees.
The five capacities are 0, 1, 4, 16 and 256, with all **257 output records**
compared byte-for-byte, including counters, ordering, duplicate retention and
unused slots. At least one query must actually fill the 256-entry list.
There are 210 queries; 23 fill the entire list in the qualified reference.

`--admitted-index` constructs a separate native-scene-admissible fixture: its
otherwise unknown primitive and segment controls are bounds and line records.
The original raw-shader unknown-kind fixture is unchanged and still runs.
This is a different input/reference, never normalization of returned output or
relaxation of native scene validation. `--native-product` requires this input and
installs it using the existing `GpuPictureNativeSceneCompiler` packed index
contract and `NativeSceneStreamBuilder`, then executes actual C++ queries.
Every summary and returned record is compared exactly; caller tail sentinels
must remain untouched. The existing full package consumer separately retains
owner-map/generation/repeated-map lifetime coverage.

New coverage shares the original ProGPU fixture, canonical shader and product
APIs. No production geometry, shader, dispatch preference or result reader has
changed. Both managed and native product calls are exercised, rather than treating
the standalone stage dispatcher as a substitute for either implementation.
Fixture preparation is bounded O(N) host work and O(N + R) host storage; retained
GPU algorithm and resource complexity are unchanged. Per-query test readbacks
and comparisons are diagnostics, not an application hot-path implementation.

Evidence:

- Original `20bbf7f1` shader through Dawn Metal versus current ordered shader:
  all 210 full buffers and 182 managed public queries match, for both raw and
  native-admissible inputs.
- Current wgpu-native Metal: 210 complete buffers, 182 managed queries and 210
  C++ product queries match the independent native-admissible Dawn reference.
- Windows x64 running in the ARM64 Windows 11 Parallels VM, default FXC and
  system WARP: the same 210/182/210 comparisons pass, including all 23 full lists.
  Native DLL comes from CI run `34800231224`, commit `5a4cc291`, SHA-256
  `4f710c54a3a97b287bd4c4f3ea469120ecdbeeae3984385fc57220745087e9447`.
  The stock wgpu DLL hash is
  `4971fce5b4d93fc10b65d01cbcc57f9f35ad1bc479e654737974e4ad2e265be6`.
  This is an explicitly staged diagnostic graph, not an unchanged NuGet consumer.
- Native ARM64 in the same VM also passes all 210/182/210 comparisons and 23
  full lists, with `--require-fxc` checking the selected compiler. Its matching
  CI native DLL SHA-256 is
  `574767a342cc5b59b67665e2e27a8ebb0e1f223b377e1264d12b10cc6d09fa12`.
  The original 168-query sparse fixture also still matches its earlier independent
  reference, including all 140 managed product queries. Release builds have no
  warnings/errors; workflow lint and documentation checks pass.
- Independent native-admissible reference SHA-256:
  `ba0a01e4d409f5570fbc62f5b8e22334a5facf4a289c0e0af2fb0925ef0071c3`.
  Raw unknown-kind large reference SHA-256:
  `e814fa67212ee3dd5f131b8cb785ec3d470fb90196fb4072cdfb1b968f899d39`.

CI preserves the existing default/native/package gates. Linux and Metal add the
large raw differential; Dawn also emits the independent admitted reference.
Separate Windows x64/ARM64 jobs consume that exact run's reference and native
runtime, require actual FXC, and compare both product implementations. These jobs
do not select DXC, redistribute WARP, waive counters or change process defaults
outside their explicit ordered-query environment.

The preceding default-path diagnosis remains unresolved: CI run `34800231224`
completed the ordered package consumers but its default Windows x64 ellipse
readback reached the job deadline; ARM64 failed after bounds submission. In the
local VM, a direct six-binding single-rectangle ellipse probe compiled under
FXC then exited with `0xC0000005`. The identical shader returned correct full
records on Metal. An isolated one-result insertion specialization still crashed
and was **not** included in production. This evidence does not establish a
missing synchronization barrier: the query has one invocation, and the
[WGSL program-order contract](https://www.w3.org/TR/WGSL/#program-order)
orders its own memory operations. The precise compiler/runtime fault is not yet
isolated. VM configuration, system libraries and execution policy were unchanged.

Maximum result-list coverage is not maximum device-buffer/dispatch capacity.
Final CI, matched latency/residency, native source/package/Showcase qualification,
and a separately reviewed automatic-selection decision remain required. No
dependency pins or merges advance on this checkpoint.

## Automatic D3D12/FXC selection

The automatic product policy now selects ordered GPU queries when the initialized
owned device reports both D3D12 and the actual FXC compiler. It does not select a
different adapter, install DXC, inspect OS/adapter-name strings, retry after a
failed query, read candidates to the CPU or weaken result admission.

| Requested query policy | Actual device/compiler | Resolved path |
| --- | --- | --- |
| Automatic | Owned D3D12 / FXC | OrderedStages |
| Automatic | DXC, Metal, Vulkan or unknown/borrowed compiler | SinglePass |
| SinglePass or OrderedStages | Any device | Explicit choice unchanged |

This is a compiler-specific selection decision, not a claim that a passing
standalone probe fixed the monolithic compiler/runtime fault. The repeated stock
FXC failures include access violations, bounds termination and ellipse summaries
with one hit but zero list records. Ordered dispatch retains the exact shader
predicates and traversal order while reducing individual pipeline complexity.
Its independent full-buffer, full-capacity, public managed/native and complete
ordered package evidence is recorded above. The integration candidate still
requires final default-package/platform/Showcase gates before merge; the old
single-pass FXC fault remains reproducible through explicit selection.

Both product implementations consume `WgpuContext.HitTestExecutionPath`:
`GpuHitTestDeviceIndex` constructs the ordered managed dispatcher, and
`NativeCompositor` passes the existing generated ordered-query flag to the C++
engine. No second C++ geometry implementation is added. Shared surfaces inherit
the actual owner's compiler, adapter diagnostics, request preference and limits.
External devices without compiler identity retain their explicit host policy;
raw C hosts retain the documented engine flag. Missing or inadequate limits
still reject ordered buffers/dispatch, never fall back to the failed pipeline.

Selection is O(1) with no allocation; pipeline laziness, index-owned O(M) scratch,
one-submission dispatch, completion and readback contracts are unchanged. No
numeric CPU/SIMD work, shader bodies or rendering quality changes. Automatic
compiler and adapter preferences are unchanged. There is no claim that a
specific path is fastest on all devices; this decision addresses the observed
FXC correctness failure while retaining other defaults.

CI keeps the explicit ordered lane and all default package consumers. An added
Windows x64/ARM64 step runs the same independent 210-query full-capacity reference
with automatic selection, requiring actual D3D12/FXC, an Automatic request and
the resolved ordered path. Forced OrderedStages or DXC cannot satisfy that step.
Existing device-limit, overflow, counter, duplicate/tie and untouched-tail checks
remain. Pure policy/interop tests currently pass 141 cases; workflow lint passes.

Local validation uses Windows 11 ARM64 in the existing Parallels VM with system
WARP, stock WebGPU and no policy/configuration changes. Windows PowerShell 5
initially rejects scripts; the existing task-local PowerShell 7 runner reports
RemoteSigned and executes them without an execution-policy override. Native
ARM64 and emulated x64 automatic selection each pass all 210 raw buffers, 182
managed queries and 210 C++ queries, including 23 full 256-entry lists. Artifacts are under
`artifacts/query-automatic.3kyONl`. The native DLLs are from CI `34805390052`
(`8c208aea`), alongside current managed policy outputs: this is an explicitly
staged diagnostic graph, not unchanged final NuGet qualification. Metal's
automatic native owner-query probe also still passes its single-pass route.

The formerly failing `--native-owner-query-probe` now exits zero under automatic
selection on both VM architectures. It covers point/bounds/ellipse participation,
16 repeated waits, region-first lazy pipeline creation and native owner/generation
isolation with unchanged exact assertions. It reports 11 buffers/82,512 logical
bytes and no textures for the simple native index. The system compiler/WARP/D3D12
modules are loaded from Windows System32; no compiler or WARP payload is injected.
Native DLL SHA-256: ARM64
`6764086bb8b9baa7fd206e135223c2f669d61c1e2733949165eae4f9d71f7368`,
x64 `fc43eacf54b897d660d0e5a1a24b4b9c012d060042b63bfb6de1234ba9a2b99d`.
Both use managed backend
`5e3a44758d96256643a4ad26efa0927d292c6771b359c948cdff4732ba844d2b`
and the independent reference
`ba0a01e4d409f5570fbc62f5b8e22334a5facf4a289c0e0af2fb0925ef0071c3`.
This closes the local default-owner-query failure through the selected GPU
algorithm, not the explicit single-pass FXC defect or final package/application
qualification. Current-head package CI is still required.

The cross-engine architecture sources below were rechecked together with
[HLSL shader profiles](https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/dx-graphics-hlsl-models)
and the [WGSL program-order contract](https://www.w3.org/TR/WGSL/#program-order).
Keep device-qualified lazy GPU stages and retained exact results; neither a
barrier guess nor a CPU geometry rewrite follows from these failures. No foreign
implementation is copied. Complete source/package/hardware qualification,
native memory endpoints and ordered merges remain independent requirements.

## Research and design decisions

The cross-engine research matrix in
[native input ownership](native-mil-hit-test-ownership.md#design-references-and-decisions)
was rechecked for this work: [WebRender](https://firefox-source-docs.mozilla.org/gfx/RenderingOverview.html),
[Skia paths](https://api.skia.org/classSkPath.html),
[SkParagraph](https://github.com/google/skia/blob/main/modules/skparagraph/include/Paragraph.h),
[Direct2D](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/nn-d2d1-id2d1geometry),
[DirectWrite](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritetextlayout-hittestpoint),
[Win2D](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasGeometry.htm),
[Vello](https://docs.rs/vello/latest/vello/struct.Scene.html),
[Parley](https://docs.rs/parley/latest/parley/struct.Layout.html), and
[HarfBuzz](https://harfbuzz.github.io/harfbuzz-hb-shape-plan.html).
Retained layout/scene reuse, broad-phase culling, lazy pipelines and batched GPU
work are retained; no font, DPI, hinting, atlas-generation or owner policy changes.
Worker preparation and device-qualified caches remain the existing product owners.
The [WebGPU usage model](https://gpuweb.github.io/gpuweb/#programming-model-resource-usages)
informs the separate indirect buffer and dispatch dependencies. The pinned
[wgpu DX12 compiler configuration](https://github.com/gfx-rs/wgpu/blob/87576b72b37c6b78b41104eb25fc31893af94092/wgpu-hal/src/dx12/shader_compilation.rs)
was inspected only to understand compiler options; upstream code was not modified
or imported into this implementation.

## Automatic D3D12/DXC selection

The Windows modal sample's explicit DXC compiler selection exposed the remaining
single-pass policy gap. LibreWPF diagnostic `38994007641` retains the unchanged
qualified producer `5a4c4d310c17df9cd7da0d33b072b90e951de0cf`, original Microsoft
seven-scenario reference, no portable input and the original 60-second process
limit. Native traces record the first retained point `hit_query` after the initial
frame submission. The same native thread first raises access violation
`0xC0000005` while generated code executes `str w11, [x28]` with `x28=0x2174`,
then terminates at system `d3d10warp.dll+0x971f8` with fail-fast parameter 73.
The preceding generated address construction uses the zero register; neighboring
stores use the stack pointer. This is consistent with the earlier independently
captured WARP spill-address fault, not a managed input callback failure.

Both original dumps are validated against the debugger's actual PID, exception
thread, code, instruction and access parameters. Artifact `12086826218` has
SHA-256 `070f267c78527f52e920af6e1ff57c6b634e0d064038e3f60c6ed8300ef1934b`.
The first-chance dump SHA-256 is
`fa29fcefab0a5ecf56fd7033a913a16000735e52a4f5a7cc1fa3101c25a636f9`.
No system DLL, original shader, input or timeout was replaced to obtain this evidence.

Automatic policy now selects the existing ordered GPU implementation for actual
owned D3D12/DXC as well as D3D12/FXC. Explicit SinglePass/OrderedStages choices and
unknown/borrowed compiler identities remain unchanged. The shared O(1) policy
feeds both managed and native dispatchers; shader predicates, traversal order,
candidate/indirect ownership, limits, full results and single-submission behavior
are unchanged. This introduces no CPU geometry or result fallback and does not
change compiler or adapter defaults.

The previous policy fails the actual D3D12/DXC selection regression; the repaired
policy passes all 18 existing/expanded policy and limit cases without skips.
Windows x64 and ARM64 CI retain the independent original Metal reference and both
existing FXC lanes, then execute the complete 210-record/full-capacity managed and
C++ comparison from a separate output using the same Build's verified DXC payload.
`--require-automatic-dxc` requires the real initialized D3D12/DXC identity, an
Automatic request and OrderedStages result. Forced ordered selection cannot pass
that gate. Whole producer CI, unchanged default/DXC package consumers and final
ordinary modal/Gallery application acceptance remain required before qualification.
