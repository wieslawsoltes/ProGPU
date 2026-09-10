# Native paragraph word-space justification

## Acceptance dependency

LibreWPF's source-built Application.Run harness executes rich-editor AlignJustify.
Registering its actual native media providers exposed the source formatter's
explicit rejection of justification. The enum already reached the native adapter,
but the underlying shaped-only algorithm treated it as non-expanding alignment.
This change implements real word-space expansion in the shared C++ paragraph;
it does not introduce a WPF-local composer or restore the empty-text fallback.

## Implementation

The existing context paragraph classifies complete Unicode source clusters into
content, whitespace and U+0020 word spaces, using caller-owned byte-sized scratch.
Mixed content/space clusters and combining marks are not split. Soft-wrapped,
unclipped lines distribute their remaining visible width over interior word-space
clusters. Leading/trailing whitespace, final and mandatory-break lines, collapsed
views, nonbreaking/fixed-width spaces and words without opportunities retain their
advances. Tab prefixes retain the existing grid; only opportunities after the last
tab may expand. RTL trailing whitespace remains outside the visible left edge.

Positioning retains glyph IDs, font indices, source clusters, bidi order and
per-style floating-point scales. Expanded advances feed the existing native
interaction builder, so carets, selection and hit testing consume the same geometry.
Intrinsic widths remain natural, unexpanded measurements. C ABI structures and
entry points are unchanged; scratch requirements account for classification.
Legacy shaped-only C++ entry points keep their original signatures and behavior.

Classification and advance distribution are allocation-free O(S + G) source/cluster
and prefix scans. Those dependencies require ordered traversal; independent
four-component metric conversion continues using NEON/SSE2. No GPU readback,
per-glyph interop, integer font rescaling or new CPU raster fallback is introduced.

## Validation and limits

The native text showcase regression exercises uniform/styled LTR/RTL paragraphs,
actual wrapped width, retained font/cluster identity, final/hard lines, nonbreaking
spaces, unbreakable words, tabs and malformed/combining-cluster classification.
The complete local native suite passed 20/20 after the implementation; subsequent
classification regression additions are included in the final rerun log.

The source-built native host gate also passes with a new typed-provider regression
for styled/RTL wrapping, expanded-space selection, caret distances, point hits and
indivisible combining clusters. The broader Application.Run harness now proceeds
past justification and stops at its separate rich-editor decoration-scope rejection.
It is not qualified as a passing application.

Local evidence: artifacts/release-hour/native-justification-final-build.log and
native-justification-final-tests.log; LibreWPF validation artifacts include
native-host-justification.log and application-run-justification-tests.log.

Microsoft's [DirectWrite justification documentation](https://learn.microsoft.com/en-us/windows/win32/DirectWrite/justification--kerning--and-spacing)
also describes script-specific justification character insertion. That behavior,
inter-character policies and full Windows-oracle typography comparison remain
unimplemented/unqualified here. This word-space implementation is not a claim of
complete DirectWrite, rich-document, package-application or cross-platform parity.
All exact-head CI, package and application gates remain required.
