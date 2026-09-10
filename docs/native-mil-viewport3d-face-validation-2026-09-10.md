# Retained Viewport3D face validation

The release acceptance path is ShowcaseApp's retained Viewport3D display through
source MIL and the native compositor. Its existing `--semantic-viewport3d` gate
checks placement, clipping, opacity, front/back culling, lighting and materials.

## Corrected oriented-surface fixture

At `160cb12b`, the Linux ARM64 native job passed all 23 C++ suites but failed the
later exact front/back image comparison. The same gate failed locally on Metal.
The fixture reversed triangle indices while retaining +Z source normals, so it
compared different visible lighting: canonical `Native3D.wgsl` reverses a
back-facing fragment's normal. The reversed test surface now also supplies -Z
normals. Both surfaces therefore present the same normal toward the observer
while exercising opposite culling modes.

No production shader, face-selection policy, image tolerance or assertion was
changed. This is a fixture-only correction; managed/native renderer algorithms
are unaffected. The implementation provenance is the existing ProGPU-owned
`RetainedViewport3DQualification.cs` fixture and canonical `Native3D.wgsl`.

## Evidence and remaining gates

- Before correction: the local Metal entry failed its exact front/back assertion.
- Release benchmark project build after correction: zero warnings/errors.
- Same local entry after correction: passed exact front/back pixels, typed
  viewport/clip bounds, opacity, orthographic camera, point/spot lighting and
  diffuse/specular gradient checks on Apple M3 Pro / Metal.
- Logs: `artifacts/release-hour/viewport3d-face-fixture-build.log` and
  `viewport3d-face-fixture-test.log`; prior failure is in
  `viewport3d-managed-entry-160.log`.

The separate cached/clipped sibling Viewport3D failure on hosted D3D12 and
Vulkan remains unresolved. Windows VM compilation, other platform checks,
package/application qualification and ordered PR merges remain open. Passing
this fixture does not qualify those gates or establish performance parity.

The native sibling test now emits per-channel nonzero pixel counts/extents and
cache/content-pass counters only when its solid center check fails. This bounded
64-by-64 test-only scan distinguishes missing coverage from shifted coverage; it
does not change rendering, submissions, normal success-path work or assertions.
The rebuilt local Direct2D WebGPU suite still passes; see
`viewport-diagnostics-build.log` and `viewport-diagnostics-test.log`.

## Focused native reproduction

`progpu_native_direct2d_webgpu_tests --mil-viewport3d-only` runs the same ten
Viewport3D cases and every existing exact/tolerant pixel, depth, clip, mixed-2D
and warm-cache assertion through one shared test function. The normal no-argument
CI suite still calls that function after all its earlier phases. This explicit
diagnostic entry is not a replacement for the complete CI/release suite.
Both entries pass locally after extraction (`viewport-focused-*.log`).

The full Windows ARM64 build-only lane completed all 313 steps at `160cb12b`.
Its full graphics test on the Parallels Display Adapter / D3D12 completed earlier
phases in about 318 seconds, then passed Viewport3D cases 0–2 and lost both colored
centers in case 3 (scale-two cache). This differs from the hosted MSVC identity
cache and GCC nested-cache failures; the shared cached replay remains unqualified.
Guest evidence: `artifacts/viewport-160cb12b-win-arm64.log` in the prepared checkout.

## Cached depth slot repair

The native replay allocated `layer_depth_initialized` for only
`PROGPU_NATIVE_SCENE_MAX_MATERIALIZED_LAYERS`, but retained cache slots start at
that exact index and extend to `semantic::layer_slot_count`. Both the cold-pass
reset and the depth-load decision must use the complete attachment-slot domain.
The old reset skipped cache slots and the load decision read out of bounds;
stack contents could select Load instead of the required initial Clear. This
explains why platform/compiler runs failed in different cache variants while
local Metal could pass.

The array now uses `semantic::layer_slot_count`, matching the engine's attachment
array and cached-replay state. Cold content clears its own depth; warm cached
content still skips rendering, and same-target continuations retain depth.
No draw, shader, upload, synchronization or public ABI change is introduced.
The additional state is bounded per-frame stack storage, not a CPU fallback.

Applicability: both native providers share this replay implementation. The
managed `Mesh3DExtensionPipeline` directly clears its owned depth attachment when
rendering an offscreen payload and has no corresponding transient/cache slot
array; it needs no analogous correction. All implementation provenance is within
ProGPU's existing renderer. This is memory-safety repair, not a new architecture
or a performance claim.

Local Release rebuild after repair: all 19 native suites pass, as does the focused
ten-case Viewport3D entry (`viewport-depth-slots-*.log`). Windows reproduction and
fresh hosted CI are required before claiming cross-platform resolution.
