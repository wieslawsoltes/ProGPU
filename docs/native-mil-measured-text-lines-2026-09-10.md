# Measured native paragraph lines

## Implemented producer prerequisite

The unchanged LibreWPF rich-document application needs inline controls and
anchored content. A measured object cannot be represented correctly by advance
width alone: the previous native logical line writer used one fixed line height.
This checkpoint extends that existing writer with caller-resolved per-item
ascent/descent through try_layout_measured_logical_shaped_text.

The new C++ text_item_metrics contains DIP ascent/descent for each logical item.
The shared wrapping, bidi reordering, tab and justification implementation is
unchanged. For each emitted line, paired maxima determine ascent and descent;
height is max(options.line_height, ascent + descent). The baseline is retained
line top plus ascent, and any minimum-height surplus follows the descent.
Only the line containing tall content grows. The following line uses the same
measured prefix; there is no post-layout WPF Y adjustment.

The old justified/tabbed/scaled entry points delegate with empty metrics and
retain their exact baseline-zero fixed-height convention. Both native providers
compile this same source. The C ABI and generated managed wire records are
unchanged: this is not yet inline-object transport or source admission.

Inputs must provide either no metrics or one entry per logical item. All
metrics are validated before writes for finite nonnegative values. A conservative
input-count times maximum-height bound rejects possible float coordinate
overflow before output publication. Nonempty metric input rejects trimming
until synthetic sign metrics have their own contract; it must not guess them
from a text font. Existing unmeasured trimming remains unchanged.

## Provenance and implementation applicability

The source semantics come from original ProGPU
src/ProGPU.Text/StyledTextLayout.cs at cdeac8d230aa0754645239062c476240832e7ab3:
its candidate ascent/descent, per-line maximum and baseline placement. The
managed implementation already has this behavior and is not replaced by
a reduced native path. This prerequisite does not yet port its inline-box
shaping, styled source transport or richer paragraph options.

Public-contract and cross-engine research, decisions and complete downstream
acceptance are recorded in the
[LibreWPF inline/anchor design](https://github.com/wieslawsoltes/LibreWPF/blob/9b9578d16/reports/native-mil-inline-anchor-design-2026-09-10.md).
It covers DirectWrite/Win2D, SkParagraph, Vello/Parley, WebRender, HarfBuzz and
the WPF source model. No third-party implementation was copied.

Validation and per-line reductions use NEON/SSE2 paired intrinsic lanes on
supported desktop architectures, with a scalar reference on other targets.
The line-top prefix is dependency-bound and accumulated in double precision.
Work is O(G), allocation-free, with existing caller-owned scratch/output.
No GPU dispatch, readback, new fallback policy, font cache or frame upload is
introduced. This is not a measured performance improvement claim.

## Verification and remaining integration

The full native text test executable passes. New cases cover three wrapped
lines with unequal metrics, RTL source identity, minimum height, old empty-metric
behavior, mismatched counts, negative/NaN/infinite/overflowing metrics and explicit
trimming rejection without successful output publication. Thirty-two independent
scalar-oracle cases compare hard-broken line maxima and baseline prefixes with
the SIMD implementation. Log: artifacts/release-hour/measured-text-tests.log.

Both native wgpu and Dawn libraries rebuild successfully:
artifacts/release-hour/measured-text-providers-build.log.
The include-based suite and the import-based text module consumer both compile
and execute. This build directory does not enable the CMake module targets,
so the module was compiled with Homebrew LLVM, an explicit installed Xcode
SDK sysroot, and linked against the same text/compression libraries.
Compiler-specific BMI files remain untracked build artifacts.

Still required: inline metric C ABI/generated transport, separate non-ink
object identity, actual source scalar/cluster insertion, object-aware interaction,
source UI measurement/visual lifetime, exact line-height and trimming-sign policy,
Figure/Floater placement/exclusion and the unchanged application's actions.
Windows/Linux runtime, package and final CI qualification remain open.
Do not enable a WPF object consumer from this helper's test results.
