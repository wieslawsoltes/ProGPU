# Retained excluded paragraph snapshots

Acceptance dependency: ShowcaseApp's Figure/Floater-containing document needs
source text to retain native exclusion layout for drawing and interaction.
`NativeTextParagraphSnapshot.CreateWithExclusions` now connects the span transport
to that shared retained paragraph pipeline. It does not place source anchors or
admit WPF Figure/Floater yet.

The snapshot owns the positioned glyphs, fragment lines, explicit native
`Fragments` and `FragmentLayout` metrics. Lines in this mode are fragments, not
independent vertically stacked rows. Native tops, row indices, interval offsets,
extents and baselines stay unchanged. Source cluster ends, bidi levels, style/font
identity and inline-object positions reuse the ordinary snapshot's mapping.
Input exclusion arrays are borrowed only during synchronous construction and
are not retained. Source array mutation cannot move an already built snapshot.

Interaction uses the C fragment builder over the same positioned arrays.
Its documented capacity bound is G boxes and 2G caret stops; the ordinary
requirements call is not used because it validates an ordinary line-height
prefix. Native fragment validation still runs before snapshot publication.
Inline-object Y remains its actual native baseline minus source ascent, so
cleared vertical gaps are preserved without managed offset repair.

Empty excluded paragraphs currently fail explicitly: the existing synthetic
empty ordinary row has no native exclusion placement contract. Measured collapse
continues to reject missing sign metrics. Fragment caret navigation transport and
source consumers must be connected before application admission; do not call
ordinary row-index navigation for same-row fragments.

## Local verification

Backend and project-reference consumer builds have zero warnings/errors. The
MIL-only consumer passes against the staged native libraries on macOS arm64.
Tests cover 20-DIP clearance, tops 20/40/82, height 102, retained caret/box/object
Y, original clusters and intrinsic widths. LTR and RTL paragraphs with a real
word boundary produce two fragments on one 20-DIP row, preserve interval order,
and hit the actual fragment after caller exclusion-array mutation.

The first same-row fixture used the indivisible word `AA`; native layout correctly
cleared the exclusion and retained one row at top 20. That behavior is now tested
separately. The split fixture uses `A A` and intervals wide enough for real source
word advances; no layout tolerance or production fitting policy was weakened.
Empty excluded input is tested for explicit rejection.

Logs: `artifacts/excluded-snapshot-build.log`,
`artifacts/excluded-snapshot-consumer-build.log`, and
`artifacts/excluded-snapshot-consumer.log`. These are local wgpu text evidence,
not Dawn text, freshly packaged payloads, source application closure or final
Windows/Linux/CI qualification. The wider goal remains open.
