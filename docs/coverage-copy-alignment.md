# Portable coverage-copy placement

Managed path and glyph coverage buffers use 256-byte row pitch but require
512-byte texture-copy base offsets, matching the existing C++ backend policy.
An odd number of 256-byte rows must not place the next image at a merely
256-aligned offset. The shared checked `GpuCoverageUpload.AlignCopyOffset`
retains already aligned values and rejects overflow without wrapping.

Path batches align each output slice before capacity accounting. Glyph rings
include inter-slice padding in their capacity check before flushing and use
the same aligned offset for shader output and texture copies. Ring capacities
that are only 256-aligned remain valid. Immediate and oversized glyph copies
remain at zero; glyph bounds, row pitch, coverage bytes and sampling do not
change. `RecordCopy` rejects misaligned nonempty copies before native encoding.
No backend-name inference, device/adapter/compiler default, fallback, completion
rule, deadline or pixel tolerance changes.

## Reproducer and checks

The original LibreWinForms DataGridView sample, from successful Forms Build
36384119276 at 95d54691, failed its unchanged 20-second Windows ARM64 startup
gate. A separate bounded, owned-process D3D12 debug replay retained all original
package bytes and the default Parallels DX12 adapter. The debug layer reported
COPYTEXTUREREGION_INVALIDSRCOFFSET at offsets 11008, 33024 and 55040, followed by
an unclosed command-list error and device removal with DXGI_ERROR_INVALID_CALL.
The prior buffer-admission fix correctly rejected the later failed allocation;
it did not fix the invalid texture-copy submission.

Twelve CPU cases cover distinct row/offset alignment, all three observed offsets,
the highest representable aligned offset and checked overflow. Five GPU cases
compare complete odd-height path/glyph atlas pixels against independent immediate
rasters, exercise small/odd-aligned/large rings and reject a misaligned copy before
encoding. All 17 new cases and the existing 65 buffer/source-guard cases pass on
the local Metal run. Full hosted CI and the original Windows package/UI test
remain separate requirements; instrumented debug runs never qualify startup.

The native contract is documented by Microsoft under
[D3D12 texture placement](https://learn.microsoft.com/en-us/windows/win32/api/d3d12/ns-d3d12-d3d12_placed_subresource_footprint).
