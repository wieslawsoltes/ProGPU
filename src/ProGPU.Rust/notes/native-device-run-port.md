# Native actual-provider device run port

This increment connects the original selected-font native run to an explicit
actual hinted-outline provider. It ports the owned prepare/capture/device run
services and bounded positioning recipes; it does not supply or qualify the
native dependency interpreter. Only original ProGPU implementation is the source.
The earlier `DESIGN.md` architecture references inform the separation between CPU
shaping, immutable font/capture owners and GPU replay, not the implementation.

## Connected public API

All types are exported from `text::native_pipeline` and its `complex` module.

| API | Original connected role |
| --- | --- |
| `NativeShaper::device_requirements` | Complete original device capacity; actual selected stch permits bounded 257-fold growth, with the original 1,048,576 limit |
| `NativeShaper::shape_device` | Owned actual-provider run; publishes only the complete immutable result |
| `NativeShaper::shape_device_with_scratch` | Explicit reusable native scratch; retains the original live/partial prefix on failure |
| `NativeHintedProvider::capture` | Required whole-batch actual producer with exact source, raw request and ordered IDs |
| `NativeHintedBatch::from_capture` | Ownership transfer and structural transport admission of actual provider output |
| `NativeHintedBatch::{project_design_vectors,anchor_point}` | Original fixed-point displacement projection and exact phase-removed contour point access |
| `NativeDeviceRun` | Actual retained batch, decoded source, normalized instance, final glyphs and descriptor occurrence indices |
| `NativeDeviceRun::recompose_positioning` | Original bounded single/pair recipe over complete safe prepared edges |
| `NativeDevicePositionedSlice` | Retained original run, recomposed glyphs and original final occurrence indices |

`NativeHintedRequest` retains independent X/Y requested pixel em in 26.6,
interpreter 35/40, X/Y phase, all raw 16.16 axes and the independent Normal,
Monochrome, HorizontalLcd or explicit Unhinted load target. The ordinary defaults
and source raster-policy gates are unchanged. `NativeHintedMetrics` retains actual
driver ppem/scales and both driver/design line metrics. `NativeHintedGlyph` owns
every original raw metric, linear advance, side-bearing delta, outline flag,
point, tag and contour end. Native-long values use i64 to retain LP64 extremes
until the original signed32 admission. Descriptors remain one per captured ID,
including duplicates, empty outlines and auxiliary spaces.

## Original preparation and positioning order

The existing original normalization, preprocessing, native GSUB and private
script stages share their existing implementation. The device branch begins at
the original post-ignorable-processing preparation boundary. It freezes default
ignorable sentinels before actual metrics overwrite them, then captures the
complete final-GSUB ID prefix and the exact first-available figure/punctuation
space auxiliaries. Capture must preserve the exact owned font byte allocation,
face index, frozen request and whole requested descriptor order/count.

Binding checks actual UPM, actual ppem, exact raw-axis count and original native
fvar/avar normalization against the frozen F2DOT14 coordinates. Original fixed
scales must be positive signed32. Projection proves every rounded signed32
result before any output write, retains symmetric magnitude rounding, and never
adds phase to displacements. Contour access removes only the original actual
phase after proving the subtraction domain. Forced GPU/native-compute projection
remains unsupported because this stage consumes CPU-visible values before GPOS.
Automatic/intrinsic projection retains the native architecture admission;
explicit scalar arithmetic is available independently. The scalar port is not a
claim that the dependency's independent `FT_MulFix` oracle has executed.

Whole-prefix metric preflight checks exact glyph IDs and all original lane
bounds before writing. Horizontal positioning uses the actual captured advance
vector. Vertical positioning uses the original raw vertical advance and the
original `vx-hx` and `-hy-vy` bearing expressions, with signed32 input and result
admission. It never substitutes unhinted design outlines or recomputed envelopes.

The connected writer preserves space fallback, early USE/Myanmar mark zeroing,
default-ignorable zeroing, prepared captures, selected native GPOS lookups and
feature ranges, device legacy kern, late mark zeroing, device attachment
resolution, captured-extent fallback marks, direction reversal, monotone-character
cluster repair, captured-width Arabic stretch and action/complex metadata cleanup.
Original descriptor indices reverse with output and repeat with stretch copies;
no glyph-ID search or duplicate collapse is introduced. Final publication checks
every occurrence against its retained original source descriptor.

Verify uses independent child provider captures for the exact original safe
fragments and bounded contexts. Children have the complete original output
capacity, independent scratch/mapping/metric owners and no optional recipe
capture. A child failure retains its actual capture/projection error domains;
failed verification clears the live count as in the original. Child owners retire
before the parent result is published. The provider is borrowed exclusively for
the synchronous operation; safe slices replace the original raw alias/alignment
and lifetime preflight rather than permitting foreign writable aliases.

## Original prepared boundaries and positioning slices

Optional capture occurs only for original ordinary non-Arabic horizontal
MonotoneGraphemes runs. Positioning recipes additionally exclude positioning
marks. Original logical cluster identity is required. Selected lookup coverage
digests are negative-only exclusion proofs; positive digests never admit an
opcode. Each retained lookup must be single/pair or an extension whose entire
subtable inventory resolves to those same families. Legacy kern selection is
recorded before digest pruning, preserving the original feature decision.

Prepared boundary records include marks where the original whole-run boundary
capture admits them. Their final occurrence permutation must be unique and
complete, preserving glyph ID, code point, cluster and the subset relation of
original dependency flags. A failed boundary proof clears only the optional
boundary inventory, retaining the otherwise valid final run.

Recomposition owns the original feature plan, normalized coordinates, lookup
order and projection selection. It requires nonempty complete safe cluster edges,
reads original raw GDEF independently of the ordinary shaper blocklist, reapplies
native single/pair GPOS and device kern, and retains direction and original
occurrence identity. Full-run recomposition compares all eight original glyph
fields, including flags, before publication; smaller slices can differ only in
placement while retaining the untouched original generation.

## Exact original provenance

Paths below are relative to `src/ProGPU.Native/src/Text/`.

| Source | SHA-256 |
| --- | --- |
| `Font/progpu_native_hinted_font.hpp` | `04b6289fab9d53e42e7930f142ef2acd67fd285888eecb2ce61cef54159a33f6` |
| `Font/progpu_native_hinted_font.cpp` | `e9f740af00aa3d32a66f37cf4fbf5b130dff9e9e2bb38dc47f8f1d03ae0cab36` |
| `Font/progpu_native_hinted_gpos.cpp` | `d21f414eb1df24d130b69f85f522a989e62e06c1e9071210efb63d38ce979b9c` |
| `Font/progpu_native_hinted_run_metrics.cpp` | `d18208b285224e60fc8f1e3bbf6fd6030e00c38f6df3656776dd33f11102245b` |
| `Font/progpu_native_hinted_shaper.hpp` | `2766030190ceb75dd72e6a8dcdea3183201a807a3fbb62795c572364a3eba49a` |
| `Font/progpu_native_hinted_shaper.cpp` | `ca8a88c759db54c61ba8dc009e7b4d8c50b9b32f7ab3c24b07964ba57675df2c` |
| `Font/progpu_native_hinted_positioning.hpp` | `5b00f482676ea6ed1dc491763a1b26624eb7bfb5f231319f039ffed067fa3633` |
| `Font/progpu_native_hinted_positioning.cpp` | `9b5a80f479ad2dacc0ef5c23500e9c4e3e9b6a240f26e652c99f8aedb351f0bf` |
| `Shaping/progpu_native_open_type_shaper.cpp` | `1590eacb54721eb1c9ae5bd71b824c91ccc0893c8e5a2969625d5bc40131ddce` |
| `Shaping/progpu_native_open_type_device_shaper_internal.hpp` | `8eb3fa3bb6ad789267154a1b06efb11b94a5a7d5a2b2a41ae833cf2941fc2b6f` |

The complete hinted shaper, run-metrics initializer, GPOS binder and positioning
recipe source are the orchestration authorities. From `hinted_font.cpp`, only the
owned transport contract and original projection/anchor arithmetic are included;
the dependency loader, interpreter creation/selection and actual capture producer
remain outside this increment. Existing original native GPOS, kern, device
fallback-space/mark/stretch and Verify ports supply their existing algorithms.

## Ownership, allocation and remaining work

The safe provider trait is an explicit producer boundary, not evidence of a
working hinted implementation. The subsequent [producer lifecycle/cache/writer port](native-hinted-producer-port.md)
connects original owned face selection, exclusive cache, sequential raw slot
capture, fixed-width copies and owned physical geometry to an explicit safe
backend. The actual FreeType binding, prepared receipt verification and loaded
image observation still require the real dependency backend implementation. The independent dependency scalar oracle,
paired unfitted replay/scan transfer, retained shaped-source replay,
source GDI/GDI+/DirectWrite raster-policy adapters, paragraph fallback/itemization,
package/browser integration and original pixels remain independent work. The
previously documented original native Hangul flag mismatch remains unchanged.

All descriptor, point, source, feature and scratch Vec allocations use existing
fallible capacity helpers or explicit try-reserve at execution boundaries. Batch
construction takes already owned producer vectors. Standard-library Arc control
blocks and existing cloned immutable font/table owners follow the crate's current
allocation contract; this does not claim recoverable allocator failure everywhere.
Owned publication copies the final scratch prefix. Independent fragment calls
currently allocate their child scratch/descriptor inventories; no allocation-free
or throughput parity claim follows. Native driver resource exhaustion remains an
explicit capture error; Rust allocation/domain capacity errors retain FontError::Capacity.
Safe `wide` integer vector arithmetic preserves source expressions, with hardware
lowering/performance qualification deferred.

Per the mandatory port-first order, no compiler, lint, tests, browser, benchmarks,
new fixtures or differential execution were performed. Only touched-source
formatting was performed. No build artifacts, traces or benchmark output were
generated. Original error-order, malformed input, numeric boundaries, independent
fragment captures, cold/warm allocations and native/browser/performance evidence
remain for the validation phase after the code port.
