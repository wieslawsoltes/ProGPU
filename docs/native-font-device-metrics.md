# Native horizontal device-width records

WPF's [Display formatting mode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformattingmode)
requires GDI-compatible font metrics. Its
[DirectWrite metric contract](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nf-dwrite-idwritefontface-getgdicompatibleglyphmetrics)
depends on font size, pixel density and measuring mode. Merely removing the
portable formatter's rejection or rounding an already fitted Ideal paragraph
does not implement that contract.

The shared native font view now reads the optional OpenType
[`hdmx` table](https://learn.microsoft.com/en-us/typography/opentype/spec/hdmx).
This is an original implementation from the published field contract, without
third-party implementation code. It supplies exact cached integer advances for
an explicitly requested device-record size, retaining glyph zero, zero widths
and unsigned 255-pixel widths. Returned spans borrow the font's immutable bytes;
they are not owned buffers, design-unit metrics or ink bounds.

The reader distinguishes an absent table/size from invalid data. A declared
out-of-range or duplicate table is not absence. Validate version, stride,
complete table extent, glyph count, strictly increasing nonzero sizes, actual
maximum width and zero padding before publishing any record. A later malformed
record rejects an earlier matching record. Failure and absence clear the result
and availability flag. Arithmetic checks precede multiplication and slicing.
There is no nearest-size selection, interpolation, rescaling or allocation.
Work is O(T + R * S), with T directory entries, R records and S record bytes;
workspace is O(1). Consumers should retain validated metadata with font lifetime,
not repeat table validation per glyph or during rendering.

The independently constructed SFNT fixtures cover exact record identity, sparse
sizes, zero/unsigned widths, missing tables, empty tables, every truncated table
prefix, malformed later records, stride/count overflow, duplicate directory
entries and invalid font ranges. The test is registered in native CTest with
the existing strict warnings, sanitizer policy and 60-second deadline. The
named text-module consumer also exercises the exported record and method.

Local macOS ARM64 evidence: the 57-case executable passes under Clang C++20
with `-O2 -Wall -Wextra -Werror -pedantic`, and with AddressSanitizer plus
UndefinedBehaviorSanitizer. The actual CMake text-library/test target builds
and its registered CTest passes. The local CMake run uses header compatibility;
named-module and all other platform gates remain required in hosted CI.

This supplies font metadata, not complete GDI-compatible shaping. Hint execution
for uncached sizes, variable-instance eligibility, device-grid policy, batched
C/managed transport, native pre-fitting advance selection and retained
interaction/continuation consistency remain required. LibreWPF #184 stays open;
source Display mode is not enabled by this change.

Managed and native renderers retain their existing text behavior. Both source
renderer modes use the shared native paragraph service, whose Display admission
must be connected together when the full metric contract exists. No managed-only
approximation, renderer fallback, C ABI change or package qualification is added.
