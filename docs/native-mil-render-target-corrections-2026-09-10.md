# Native MIL render-target corrections

## Acceptance and ownership

The acceptance application is the native MIL ShowcaseApp. The actions are viewing
a cached stroked visual and clipping a Viewport3D inside an offscreen visual.
The bounded paths are ProGPU's shared mask shaders/managed pipeline selection and
the native semantic 3D camera upload. These fixes do not open SDK admission.

R8 mask shaders now output coverage in both red and source alpha. The compositor
selects premultiplied blending for those outputs, preserving earlier coverage
under subsequent transparent primitive padding. This implements the existing
[source-over equation](https://www.w3.org/TR/compositing-1/#porterduffcompositingoperators_srcover),
not a new stroke tessellator. Native semantic brush-mask draws use the same shader
without blending: their red output remains unchanged. No allocation, additional
pass or CPU work is introduced. Existing cross-engine reuse decisions in
`native-mil-hit-test-ownership.md` remain applicable; text shaping, worker pools,
font caches and upload scheduling are unaffected.

Native 3D render-target cropping belongs in camera viewport pixel placement, not
the model transform. Retain the world semantic state and the full unclamped camera
viewport relative to the offscreen target. Clamping the viewport would rescale the
projection. The shared fixture now selects the actual unlit mode (2); mode 0 is
lit and a zero-light fixture correctly renders black. The source native MIL path
consumes this C++ engine; ordinary managed 3D is not newly qualified by this fix.
The correction is constant-work camera setup and does not change the native ABI.

## Evidence and remaining work

- `CachedStrokeMaskPreservesEarlierRoundJoinEdgeCoverage` passes, including a
  repeated-frame texture identity/pixel check. Before the fix the checked red
  channel was 20 instead of the ordinary stroke's 137; tolerance remains 2.
- The full managed run passes 4,569 tests, skips seven platform cases, and fails
  the same four full-image cached-stroke comparisons. The new edge test was run
  separately afterward and passes. Overlapping ordinary stroke pieces and cached
  union opacity/AA still differ; do not widen the image tolerance or skip them.
- Native Direct2D WebGPU passes, including the clipped/cached/scaled/nested
  Viewport3D fixtures, siblings and depth ordering. The latest full native run is
  17/19 suites; Direct2D compatibility and MIL serialization fixtures still fail.
- This is macOS arm64 local evidence, not exact-head Windows/Linux packages,
  native Windows comparison, application startup or complete parity proof.

Logs are in the prepared worktree's `artifacts/release-hour`: `mask-edge-regression.log`,
`managed-mask-full.log`, and `native-final-corrections-tests.log`. All final PR CI,
package and application qualification gates remain required before merging.
