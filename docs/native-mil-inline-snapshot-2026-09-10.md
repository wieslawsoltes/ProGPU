# Retained native inline paragraph snapshots

## Core acceptance dependency

ShowcaseApp's inline Button requires one retained source paragraph for drawing,
selection and object placement. `NativeTextParagraphSnapshot.CreateWithInlineObjects`
now connects the existing inline C++ paragraph and measured interaction APIs.
It does not yet connect WPF's TextEmbeddedObject or admit Figure/Floater.

The input uses explicit `NativeTextParagraphStyle` ranges, matching source-owned
`NativeTextStyleMetrics`, and `NativeTextParagraphInlineObject` values. Object
positions are UTF-16 offsets, not native scalar indices. Mapping validates every
actual U+FFFC against the ordered object list and rejects missing, duplicate,
unordered, non-object or excess entries. Existing SIMD UTF decoding and native
metric validation remain shared.

The native composer owns shaping, wrapping, tabs, bidi, intrinsic widths,
baselines and line heights. The snapshot preserves positioned glyphs and original
cluster successors, then invokes measured interaction directly over those arrays.
It does not rewrite glyph Y coordinates or recompose lines in managed code.
`HasMeasuredLines` distinguishes this coordinate contract from the old factory.

`InlineObjects` is owned, source-ordered placement storage. Each value retains the
source position, positioned glyph index and line index, with X/width from the
native glyph and the object rectangle projected from its actual line baseline
and source ascent/descent. Array order preserves caller object identity even
when glyphs are in RTL visual order. Duplicate, missing or changed native object
identities fail publication. The object glyph/font sentinels are non-ink and must
never reach font lookup or glyph atlas rendering.

Existing ordinary snapshots retain their old behavior. Measured snapshots reject
collapse explicitly until the sign has a metric contract; they cannot be
reformatted through the old unmeasured collapse path. Empty measured snapshots
retain an empty line, reject object/style entries and invalid line heights.

## Ownership, cost and applicability

Input arrays are borrowed only during synchronous construction. Placements,
cluster metadata, native positioned arrays and interaction output are retained
snapshot-owned storage, not per-frame materialization. Source font/context leases
remain the responsibility of the existing paragraph provider lifetime.

Object mapping is O(S + O) for S scalars and O objects. Placement export walks
the positioned output and uses source-position binary lookup for objects:
O(G + L + O log O), with O(O) retained placement storage and no GPU work or
per-glyph native crossing. Cluster reconstruction keeps the existing sorted-key
algorithm. No managed/native shaping algorithm was duplicated or replaced.
This metadata adapter changes neither renderer's rasterization; both native
providers retain the same shared producer. Original ProGPU provenance and
cross-engine decisions remain in the linked documents below.

## Verification

The native consumer built with zero warnings/errors and passed using project
references and locally built native libraries. Added cases cover exact tall
object placement and hit geometry, intrinsic widths, source-ordered RTL objects,
surrogate pairs, tabs, zero-width objects, LF/CRLF ownership, caller-array
mutation, and explicit measured-collapse rejection. The object remains one
UTF-16 unit while the line retains its following hard break.

The focused managed snapshot suite passes 10 tests, including new mapping and
rejection cases. Logs: `artifacts/release-hour/inline-snapshot-consumer-build.log`,
`inline-snapshot-consumer.log` and `inline-snapshot-tests.log`.
This is local source/binding verification, not packaged, Windows/Linux, complete
fallback, performance or full application qualification.

## Next integration

Expose measured styles and object placements through the neutral text provider,
then adapt real WPF embedded-object measurement, source indices, visual lifetime,
drawing and interaction to that retained snapshot. Figure/Floater exclusion,
source application closure, exact-head packages and all final gates remain open.
The separate SVG checksum issue remains a merge blocker.

See [inline producer](native-mil-inline-paragraph-2026-09-10.md),
[measured interaction](native-mil-measured-text-interaction-2026-09-10.md) and
[measured line design/provenance](native-mil-measured-text-lines-2026-09-10.md).
