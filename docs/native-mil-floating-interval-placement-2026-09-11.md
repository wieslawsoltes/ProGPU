# Native floating-interval placement

The unchanged LibreWPF acceptance document contains sibling Figure/Floater
children. A native Microsoft.WindowsDesktop.App 10.0.12 reference on Windows ARM64
shows that bottomless floats pack in available intervals, not at a fixed column X.
The source probe and 13-case JSON are tracked in LibreWPF under
eng/NativeAnchorReference and reports/native-mil-anchor-reference-2026-09-11.json.

At width 320, two width-100 left floats occupy X=0/100; right floats X=220/120;
center floats X=110/5, centered inside the first fitting remaining interval. The
child band follows its source row. Full-width floats clear subsequent rows, and
an anchor-only paragraph retains a parent row. These are measured reference cases,
not full layout parity or proof of source event/empty-paragraph integration.

try_place_text_floater shares the existing fixed-anchor placement core and native
interval resolver. Left/center traverse free intervals from the left; right
traverses from the right. Center aligns in the selected interval. Only when no
interval fits may explicit delay advance to an actual obstacle bottom. Failed
fits preserve the previous output. Existing try_place_text_anchor retains fixed
horizontal reference behavior unchanged.

Both providers share this C++ implementation. The native header and named module
export the distinct function. There is no allocation or per-anchor managed/native
crossing. Independent coordinate validation retains the existing NEON/SSE2 lanes;
interval choice, source order and next-Y retries are dependent scalar operations.
No new benchmark or fastest-path claim is made. Original same-repository placement
and interval code supplies the implementation; no foreign code was copied. The
research provenance in native-mil-anchor-intervals-2026-09-10.md still applies.

Both full native providers rebuild and the native text suite passes, including
left/right/center sibling packing, exhausted bands, finite retries, exact edge
contact, invalid coordinates and unchanged fixed-anchor behavior.
The named-module consumer also compiles and runs with Homebrew LLVM, using the
active Xcode SDK sysroot and the rebuilt text/compression archives. Apple clang's
module parser and Homebrew's stale default sysroot required selecting the actual
module-capable compiler and SDK; no warning was suppressed.
Source automatic
admission remains closed. Batched transport, native source-row events, source
continuations/empty parent rows and final application/package/platform qualification
are required next; this primitive alone must not be called document completion.
