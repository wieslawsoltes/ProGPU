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
The native font adapter must retain immutable selected font bytes and exact face,
size, phase, hint policy and variation identity. It must capture advances and
outlines together before the next FreeType glyph slot overwrites them, preserve
original glyph/source IDs, publish batches atomically and retain caller tails.
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
