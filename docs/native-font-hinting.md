# Native font hinting

Display-mode text requires one selected hinted generation for fitting, placement,
interaction and raster outlines. Exact `hdmx` widths alone do not supply that
generation. The implementation uses a pinned external FreeType library through
its public API, without importing or adapting its implementation into ProGPU.

## Dependency ownership

`eng/native-freetype.json` pins FreeType 2.14.3's official repository, annotated
release object, resolved commit, original author and signing fingerprint.
`eng/progpu-prepare-freetype.py` requires a fresh explicit workspace outside the
repository. It never resets an existing checkout, edits a system library/package
cache or overwrites an existing payload. Its GnuPG keyring is task-owned; no user
keyring is changed. A bad, revoked, expired, missing or unexpected signature fails
before checkout/build. No weak-signature override is supplied.

Only a static Release/PIC library is produced, with optional compression, PNG,
HarfBuzz and Brotli dependencies disabled. Every archive object must independently
match the requested platform and architecture; directory names and compiler
success are insufficient. Fat, bitcode, hybrid ARM64EC and empty archives reject.
Unix/macOS/Windows targets retain the same source and public API.

The installation receipt includes source identity, actual library SHA-256,
verified object count, configure command and original-notice hashes. Original
FTL/dual-license documentation, contributed-driver notices and leading copyright/
license comments are preserved separately from implementation. This software is
based in part on the work of the FreeType Project. Runtime/package integration must
carry these notices and the credit, rather than assuming build-time provenance is
redistribution completeness.

The preparation controls run inside the existing Linux Build job. The six existing
native renderer jobs also execute the real signed-source producer, independently
verify every static object, and link a small original public-API probe to that exact
archive (no ambient `find_package` or system-library substitution). The probe
requires matching 2.14.3 headers/runtime, interpreter 35 and 40 availability, two
independent library policies and nonmutating rejection of an invalid policy. It
does not load a font, execute glyph instructions or render pixels. Artifacts retain
the static library, headers, original notices and complete receipt; they are not
native product/runtime packages. Actual producer evidence still requires a whole
successful exact-head Build, not one green architecture or a canceled producer.
Library version alone is not actual dependency identity; native integration must
also keep FreeType symbols private to the owning renderer and reject interposition.

## Remaining integration

Dependency preparation is not hinted-font execution or application qualification.
The native font adapter now owns immutable original font bytes, exact face index,
26.6 device-em request and fractional phase, interpreter policy and original-order
16.16 variation coordinates. Its native face and library end before their memory
font. Variable TrueType fonts require all axes explicitly within their ranges;
named-instance encodings, CFF/CFF2, color and bitmap-only fonts remain unadmitted,
rather than silently substituting a base instance or bitmap. That is an explicit
remaining compatibility requirement, not full source Display support.

One serialized native batch captures every original glyph's advances, all metrics,
linear advances, bearing deltas, outline flags, points, tags and contour ends from
the same glyph slot before another load overwrites it. Retained immutable batches
keep their exact original font/size/policy identity after adapter disposal. SIMD
validates original unsigned IDs on admitted x64/ARM64 targets; geometry uses exact
native signed-long bulk copies, without float rounding. The private C++ types are
not a wire ABI. No managed per-glyph crossing is added. Any later invalid ID,
native hint fault or allocation failure leaves the candidate unpublished and the
previous batch unchanged; descriptors and input order, including repeated glyphs,
remain intact.

The producer builds this original adapter and executes focused controls against
the host's actual Arial (Windows/macOS) or DejaVu Sans (Linux), without packaging
those fonts. Controls compare every retained metric/point/tag/contour/flag to a
separate public-API FreeType face, require an actual hinted-versus-unhinted
difference, retain caller mutation/disposal independence, exercise every SIMD lane
and bounded tail, invalid configuration publication, phase translation and empty/
repeated batches. The receipt records the actual test-font hash. These controls
also cover concurrent captures from one live owner and a nonuniform fractional
device-em request, without disposing the owner during a borrowed operation.
are adapter/dependency evidence, not an independent Windows Display oracle or
GPU/application qualification. Variable-instance and native hint-failure injection
controls remain required alongside subsequent integration.

The adapter is currently built by the isolated producer, not linked into the
product text context or either renderer. Next steps must connect its selected
immutable generation through the existing context use lease and bounded cache,
retain original shaping identities, add atomic fixed-width C/managed transport
and untouched caller-tail tests, and share its output across actual consumers.
No sampled width, isolated suffix reshape, per-glyph managed crossing, bitmap
substitution or Ideal coercion admits source Display mode.

Managed/native shaping, continuation, caret geometry, both raster providers and
the WPF source must consume the same generation before its existing guard changes.
Native package/NativeAOT and independent Windows source comparisons remain required.
The preparation controls and library-policy probe are authored, not locally
executed. Hosted CI runs them; no local dependency build, glyph/font execution,
GPU run or VM was performed during implementation.

Public references: [official downloads](https://freetype.org/download.html),
[glyph slot lifetime and hinted metrics](https://freetype.org/freetype2/docs/reference/ft2-glyph_retrieval.html),
[driver/interpreter properties](https://freetype.org/freetype2/docs/reference/ft2-properties.html),
and [upstream licensing](https://freetype.org/license.html).
The library-policy probe follows the public
[module property contract](https://freetype.org/freetype2/docs/reference/ft2-module_management.html)
and does not copy upstream implementation or its example code. All existing
shaping/layout, glyph/atlas, upload, GPU execution and source defaults remain
unchanged in this dependency-only step; application performance and independent
Windows Display comparisons are still unqualified.

## Architecture research and decisions

Only public contracts/design notes were used; foreign implementation structure is
not copied. [Skia's shaped-text model](https://docs.skia.org/docs/dev/design/text_shaper/)
and [Win2D retained text layout](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm)
support retaining original font/glyph identity and sharing formatted results across
drawing and interaction. Adopted: immutable batch snapshots; rejected: reshaping
substrings or deriving caret positions from ink geometry.
[Direct2D/DirectWrite integration](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-and-directwrite)
keeps layout and rendering distinct. A FreeType interpreter choice is not inferred
to reproduce Microsoft's measuring/rendering modes; an independent Windows oracle
must qualify the complete source path.
[WebRender font-instance options](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html)
make rendering policy part of font-instance identity; this adapter retains its
own exact policy/phase/variation identity rather than adopting foreign cache keys.
[Vello](https://github.com/linebender/vello) and
[Parley's layout concepts](https://github.com/linebender/parley/blob/main/doc/concept.md)
keep reusable CPU layout separate from GPU vector work. Adopted: CPU-dependent
native TrueType instruction execution and retained outlines; existing ProGPU GPU
coverage/composition stays unchanged. [HarfBuzz's responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html)
do not replace font hint execution; shaping remains the original ProGPU pipeline,
not another engine's text implementation.

Creation is lazy/explicit and owns a font copy; capture is O(H + G + P + C), where
H is dependent instruction execution, G glyphs, P points and C contours, with
O(F + G + P + C) owned storage including F original font bytes. Startup, worker
preparation, scene visibility, demand uploads, cache eviction, atlas generations
and device-loss ownership are unchanged until actual product wiring. The future
bounded context cache must avoid repeated font copies/captures without confusing
font identity, positioned scene revisions or live atlas generations. Cold/warm
application timings, allocation/residency evidence, native package/NativeAOT and
independent image gates remain outstanding; no performance benefit is claimed.
