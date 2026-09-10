# Native excluded segment origin

Core acceptance is LibreWPF's unchanged document application: an explicit hard
line break beside Figure/Floater must start the next segment at the previous
segment's native bottom against the same paragraph-local exclusions.

`try_layout_excluded_logical_shaped_text_at` adds a finite, nonnegative double
origin to the existing shared fitter. The original entry point delegates with
zero, preserving its signature and behavior. Explicit placements, glyph baselines
and result height stay in the paragraph frame; result height is the absolute
bottom, not the segment's standalone height. Empty input retains the supplied
bottom but still emits no row; this does not implement empty source text lines.

The implementation is an in-repository refactoring of ProGPU e333972a,
`src/ProGPU.Native/src/Text/progpu_native_text_layout.cpp`, specifically the existing
excluded logical fitter. No foreign implementation is incorporated. The established
[text research and ownership comparison](native-mil-text-source-integration.md)
continues to apply: retain shaped ranges and CPU layout, reuse native interaction,
and keep renderer resource/frame scheduling separate. This adds no shaping engine,
GPU work, atlas policy, startup work or device-recovery state.

Cost is the existing bounded fit cost plus constant origin validation. All caller
scratch and native SIMD metric/exclusion helpers are retained. The vertical prefix
is dependency-bound and remains double precision. Nonfinite/negative origins and
float bands unable to represent the next positive height fail explicitly. Invalid
input has no valid output prefix, matching the existing fitter contract.

Both wgpu-native and Dawn libraries compile from this shared implementation.
The native text CTest passed (1/1, 0.61 seconds), covering fractional origins,
half-open exclusions ending at the new origin, absolute bottoms, invalid origins
and the existing zero-origin regressions. The module export and import fixture
were updated, but this local build has modules disabled; import-based qualification
remains required. No performance improvement is claimed.

C ABI, generated managed records, retained snapshot transport, neutral capability
and WPF hard-segment consumption remain unfinished. Existing C callers still use
zero origin. WPF must retain the source rejection until this whole path connects;
it must also accumulate all segment extents, not only the first. Full application,
package/platform and exact-head CI qualification remain open.

## C boundary connection

The additive `get_excluded_flow_paragraph_requirements_at` and
`layout_excluded_flow_paragraph_at` C entry points now carry a double origin
through the same validation, scratch arena and native fitter. Existing exported
functions and 16-byte exclusion options are unchanged; no reserved field is
repurposed and no new wire record requires generation. Empty native input keeps
the supplied content bottom while emitting no rows, not a fabricated empty line.

Both providers compile and pass their exported-symbol allowlists. The C interop
CTest passed (1/1, 0.78 seconds), including fractional origin 30.25, native baseline
65.25, absolute content bottom 72.25 and failure-cleared counts with untouched
caller glyph/placement buffers on negative origin. Legacy origin-zero calls and
their existing insufficient-scratch/invalid-option checks remain covered.

Managed imports, snapshots, neutral capability and WPF consumption remain next.
This C boundary is not source hard-line admission or package qualification.

## Managed retained transport

`GetExcludedFlowParagraphRequirementsAt`, `LayoutExcludedFlowParagraphAt` and
`NativeTextParagraphSnapshot.CreateWithExclusionsAt` now preserve the explicit
double origin through the leased context and pinned span calls. Legacy methods
still select their old exports. The snapshot owns native placements and builds
interaction from those frames without coordinate repair or reformatting.
Nonfinite/negative/out-of-range origins are rejected before snapshot construction;
empty excluded snapshots retain their explicit unsupported contract.

The Release consumer build passed with zero warnings/errors. The MIL-only consumer
passed both renderer paths and the shared text binding's shifted snapshot check:
origin 30.25, inline/caret Y 50.25, final fragment top 92.25 and content bottom
112.25, with preserved source clusters and intrinsic width. The text binding calls
`progpu_native`; this is not a separate Dawn text invocation or packaged artifact
qualification. Both native libraries were compiled/export-checked in the prior
boundary checkpoint. Neutral provider and source hard-segment consumption remain
required before anchor application admission.

The optional `IPortableSegmentedTextFormatting` neutral capability now exposes
`FormatExcludedAt` with a double origin, preserving existing excluded providers
and request records. Callers must require the capability rather than ignore a
nonzero origin. The interop Release build passes with zero warnings/errors.
Source provider and hard-segment consumption remain required.
