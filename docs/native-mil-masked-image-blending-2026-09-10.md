# Direct and retained masked-image blending

Acceptance path: masked source images in the native renderer used by LibreWPF
ShowcaseApp, with the existing `--masked-images` native/managed differential as
the bounded component gate. The blocking path is the direct native image-frame
pipeline selection, not a new source-WPF renderer or a relaxed image comparison.

## Contract and correction

Direct `NativeCompositor` image frames accept only straight-alpha source textures.
Retained semantic image commands also admit premultiplied images. The retained
mask pipeline correctly uses `fs_retained_image` to normalize samples once and
fixed-function `One` blending. Sharing it with direct image frames changed where
their RGB/alpha multiplication occurs, exposing different D3D12 UNORM rounding
from the managed direct-image path.

Keep the retained pipeline unchanged. An engine-owned straight-alpha mask
pipeline uses the existing `fs_main` shader and `SrcAlpha` color blending for
direct image frames. It shares the existing shader, layouts, bindings and upload
path; both native providers consume the same implementation. Color-matrix and
retained MIL selection remain unchanged. Engine destruction releases the extra
cached pipeline. This adds one cached pipeline, not per-frame allocation,
readback, synchronization, CPU processing or a framework-local fallback.

The implementation reuses ProGPU's own image-frame contract and shader entry
points. The managed direct-image pipeline already owns straight-alpha blending
and needs no corresponding change. No shader copy, public ABI change, pixel
quantization workaround, tolerance increase or performance claim is introduced.

## Evidence

- Hosted Windows x64 run `34487437908`, job `102905231297`, at `cbbb2aed`:
  maximum channel difference 1, zero pixels over per-pixel tolerance, but mean
  channel difference 0.092973 exceeds the unchanged 0.05 mean limit across
  518,400 pixels. Native/managed hashes were `26B664198F86F3CA` and
  `9A77A7C25D3EF9C9`.
- Before correction, the Windows ARM64 Parallels adapter at `03acd40c` passes:
  maximum 1, zero over-tolerance pixels, mean 0.03784963348765432. This adapter
  does not reproduce the hosted WARP failure and is not a substitute for it.
  Guest logs: `artifacts/masked-image-before-build.log` and
  `artifacts/masked-image-before-test.log`.
- Before and after correction, local Apple M3 Pro / Metal passes with identical
  native output: maximum 1, zero over-tolerance pixels, mean
  0.03810763888888889, native hash `F2CC379B7484336F`, managed hash
  `E6BE6F3DFA337817`. The after run loads the rebuilt native library from the
  explicit build-directory search path. Logs under `artifacts/release-hour`:
  `masked-images-metal-runtime.log` and `masked-images-metal-after.log`.
- Both local native providers rebuild; all 19 native suites pass, including
  retained image/MIL coverage (`masked-image-path-build.log` and
  `masked-image-path-native-tests.log`). A focused managed source-contract test
  guards direct/retained pipeline separation, straight-alpha admission and
  pipeline release; it complements, not replaces, the runtime differential.
  The new test passes and the complete `NativeRendererInteropTests` class passes
  121/121 (`masked-image-routing-test.log` and
  `masked-image-native-interop-tests.log`). Documentation verification passes.

Fresh hosted Windows WARP confirmation and complete exact-head CI, payload,
package and platform application qualification remain required before merge.
The 0.05 mean limit and all existing pixel assertions are unchanged.
