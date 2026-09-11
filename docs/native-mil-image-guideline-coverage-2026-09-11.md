# Native MIL image guideline coverage

Acceptance: the external LibreWPF SDK application opens and presents through
NativeMilWgpu with native input enabled. The preceding checkpoint rejected image
command 5179 under per-point guideline state. Bitmap sampling must remain in its
original affine frame while guidelines deform destination coverage.

## Implementation and provenance

The source contract is documented in LibreWPF's
`reports/native-mil-image-guideline-contract-2026-09-11.md`: `DrawBitmap` retains
the bitmap brush transform independently of the path modified by
`FillPathWithBrush`. No source implementation is copied. This change composes
ProGPU's existing typed path, picture-mask, extended-source image sampler and
source rectangle input scope; it adds no shader, wire ABI or CPU pixel renderer.

Guideline-bearing bitmap, D3DImage and media draws now retain their original
destination as a four-edge path in an owned GPU coverage scene. The typed
guideline resource is copied, including resolved offsets. Canonical path
execution owns deformation and coverage, including collapsed shapes; the mask
is not a guessed destination envelope or four stretched texture corners.

The sampled image and coverage have separate scopes. Image source/destination
rectangles expand by the same original affine ratio, allowing the existing
extended-source sampler to cover displaced edges without rescaling the bitmap.
The original complete transform is applied once. Storage reserves a physical
pixel for the largest explicit guideline offset and another for edge coverage;
that conservative rectangle is only allocation/sampling support. Inherited
source clips/masks remain on the image paint state. Noninvertible/nonfinite
mapping still fails explicitly. Ordinary images retain their previous fast path.

The enclosing typed image rectangle owns point/region input before raster
isolation. Its original owner, transform and source clipping are unchanged;
coverage internals are excluded through the existing full rectangle scope.
The geometric coverage layer is not mislabeled as source opacity-mask input.
DrawingImage keeps its separate vector lowering and destination annotation.

Per-image construction is fixed topology plus copying the existing guideline
resource. There are no compute-heavy CPU pixel loops or scalar raster fallbacks;
native path/raster intrinsic and GPU paths remain authoritative. Picture masks
currently retain the existing parent-frame raster allocation policy. Final
performance qualification must measure its cost, not assume this uncommon
guideline path matches ordinary-image throughput.

## Regression and runtime evidence

Both providers compile on macOS ARM64. Native MIL tests pass for owned and
external bitmap sources at DPI 1, 1.5 and 2, translated source ownership,
unchanged affine sampling, separate per-point coverage and original input bounds.
The full native contract verifier passes after regenerating the decoder digest.
These are structural/execution-policy checks, not Windows pixel comparisons.

The diagnostic app passes the image-family rejection and then exposed picture
mask preparation for a zero-sized, non-drawable parent target. The replay path
already skips that composite; preparation now omits its unused mask binding
under the same `composite_drawable` decision. Resource validation, layer state
and source input are retained. This is not a successful no-op for visible masks.
Temporary renderer probes were removed.

After that correction the next observed application failure is MIL scene
compilation for target 4901, returning UnsupportedCommand (5). Its precise source
path remains to be isolated. The diagnostic app uses substituted local binaries,
not exact final packages. Visible mask pixel comparisons, transformed/clip/edit
runtime coverage, platform/VM qualification, final-head CI and ordered merges
remain required. No full application or rendering-parity claim follows here.
