# Original native Verify and captured-device policies

This increment connects the complete original safe-boundary fragment verifier
to `NativeShaper` and ports the independent original 26.6 space, fallback-mark
and stretch policies against a typed actual-capture boundary.

| Family | Implementation |
| --- | --- |
| Automatic Verify | Monotone-run requirement/capacity, separate fragment glyph inventory, original safe cuts, replay and whole-run failure publication |
| Standalone Verify | Original empty/nonmonotone handling, cluster direction/order, source interval and scalar-boundary admission, five-scalar context slicing, BOT/EOT/Verify flags and exact seven-field comparison |
| Device fragment replay | Same original verifier delegates each fragment to an independent actual device producer; it retains full device output capacity and never substitutes design-unit output |
| Device special spaces | Exact first-available cmap admission, integer design-unit rounding before projection, captured figure/punctuation metrics and signed-32 publication |
| Device fallback marks | Shared source template walk, captured per-occurrence extents, exact component/class arithmetic, projected integer gap, signed-64 scalar/SIMD accumulation and atomic signed-32 per-mark publication |
| Device stretch | Original shared run discovery, frozen pre-publication widths, complete offset preflight, ordered dependency writes and descriptor-preserving backward expansion |

## Public API

Existing `NativeBufferFlags::VERIFY` now enables automatic verification through
`NativeShaper::{shape, shape_into}`. `NativeShapeRequirements` includes
`verification_glyph_capacity`; `NativeShapeScratch` owns its independent output
inventory. Native error value 10 is exposed as `FontError::VerificationFailed`.
Capacity errors remain distinct. Automatic verification failure restores the
original owned glyph storage and clears the published live count to zero.

`NativeShaper::verify(input, expected, scratch)` performs original design-unit
fragment replay independently. Each fragment receives the original declared
whole-run output capacity even if the caller owns more storage; the unused tail
remains untouched. The algorithm reuses existing scratch and frozen font/feature
owners, without cloning the shaper or allocating new context/output vectors per
fragment. Existing full-outline cache misses retain their separately documented
temporary path allocation behavior.

`NativeShaper::verify_device` uses `NativeDeviceFragmentSource::shape_fragment` and
borrowed `NativeFragmentContext` (actual flags and bounded decoded pre/post
context). The producer retains an independent fragment generation and cannot
overwrite the expected run's source descriptors or captures. Unlike design-unit
verification, this branch retains the complete supplied fragment output capacity.
The shaper exposes its frozen `language()` in addition to existing font, route,
feature and coordinate accessors. A trait declaration or successful callback is
not qualification of an original hinted producer.

Captured-device policy exports under `text::native_pipeline` are:

* `NativeDeviceSource`, extending the original `GposDeviceSource` generation;
* `NativeDeviceFrame`, with an explicit `GposArithmetic` selection and `gpos()`
  access to the identical source;
* `NativeDeviceMarkBounds`, `NativeDeviceSpaceKind`, `NativeDeviceStretchScratch`;
* `apply_native_device_space`, `apply_native_device_fallback_marks`, and
  `apply_native_device_arabic_stretch`.

The actual source owns retained unpositioned metrics. Mark/stretch queries receive
both the current source occurrence and its retained descriptor identity; equal
glyph IDs cannot collapse distinct occurrences. Font-object and normalized-instance
identity are checked at the device policy entry point. Source implementations may
aggregate real producer owners, but must retain their common generation throughout
the synchronous call and may not run fonts, GPU work, managed callbacks or
application reentry while supplying captured metrics.

The shared fallback walker preserves original prior-positioned skipping, no-bound
behavior, component defaults, class reset and dependency order. Device geometry
uses independent signed-64 lanes to preserve cancellation before signed-32 range
admission. Explicit scalar and supported SIMD routes keep the original overflow
expression and publish both offsets, zero advances and updated class bounds only
after all checks for that mark. Earlier marks/dependencies remain published after
a later failure, as in native. The integer UPM gap is projected exactly once.

Stretch freezes only actual stretch widths before reversal or output mutation.
It retains the original 1,048,576-glyph bound, 256-glyph repeat cap, negative-width
behavior, copy/overlap arithmetic and all signed-32 offset checks. Only after the
complete preflight does it write dependencies, reverse the source where required,
expand backwards and copy the exact descriptor identity into each repeated slot.
No metric callback runs after output publication begins. Storage never grows.

## Exact original provenance

All paths are relative to `src/ProGPU.Native/src/Text/Shaping/`.

| Original source | SHA-256 |
| --- | --- |
| `progpu_native_open_type_shaper.cpp` | `1590eacb54721eb1c9ae5bd71b824c91ccc0893c8e5a2969625d5bc40131ddce` |
| `progpu_native_fallback_marks.cpp` | `dba3622518f53e434f8ea5dd69790041b67787a3d9e8d48f284e446e4bacb466` |
| `progpu_native_fallback_marks_internal.hpp` | `f99807b3c55a481649ac0436c3a2c0fdaf81fd19ea11d5fdc280860992d82222` |
| `progpu_native_arabic_stretch.cpp` | `0c35e61d9f6314dc2bdcc5b6a03f1c4a7c0d4b89616e848bf5cea3f252a58f2c` |
| `progpu_native_arabic_stretch_internal.hpp` | `5348f1636a916c66e81a487961ea1760f28f72b92349e31e624f621b4ee09173` |
| `progpu_native_space_fallback.cpp` | `6d3dd9d3bc113f9292becdd12f128ec0c8ae67f5d5689a1431f0542ae2e49e07` |
| `progpu_native_space_fallback_internal.hpp` | `a272b864adfbc81b3f878445742a75eb34ec760cea105e26e5b518fea0284a69` |
| `progpu_native_open_type_device_shaper_internal.hpp` | `8eb3fa3bb6ad789267154a1b06efb11b94a5a7d5a2b2a41ae833cf2941fc2b6f` |

The native shaper's original context writers, verification core and fragment
comparison at lines 1329–1617 and final Verify admission/publication are the
algorithm authority. Device policy functions come from their complete original
source branches; the shared fallback/stretch walks remain common to design and
device execution. Only ProGPU-owned implementation is ported.

## Remaining independent boundaries and evidence

The subsequent [native device run port](native-device-run-port.md) connects the
complete final-GSUB descriptor preparation, capture-positioning/capture-boundary
owners, device run services and independent fragment captures to an explicit
actual-provider trait. Actual hinted outline/metric production and source replay
writers remain independent work. These interfaces consume actual retained
captures; they do not execute the interpreter or infer source GDI/GDI+/DirectWrite
raster policy. Native
paragraph fallback/bidi itemization and source/browser/provider/package integration
also remain independent. The previously documented native Hangul producer/consumer
flag mismatch remains unchanged.

The CPU ownership/device-resource separation follows the prior cross-engine
architecture research in `DESIGN.md`; external engines provide no implementation.
No compiler, tests, lint, browser, benchmarks or new validation fixtures were run
or authored, following the user's mandatory port-first order. Only touched-source
formatting was performed. No build/trace/benchmark artifacts were generated.
Original malformed-font/error-order comparisons, cancellation/overflow behavior,
device producers, browser runs and cold/warm performance remain deferred evidence.
