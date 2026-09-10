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
