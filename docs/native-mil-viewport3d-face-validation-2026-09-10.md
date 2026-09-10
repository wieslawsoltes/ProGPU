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
