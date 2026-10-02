# Original Microsoft WPF Display text reference

This Windows-only reference observes the original `TextFormatter` and source
`GlyphRun` API using the repository's exact Inter-Regular font. It does not load
LibreWPF or ProGPU rendering libraries. Original PresentationCore strong-name
identity, loaded DirectWrite/native module hashes, runtime/OS/architecture,
font and producer hashes accompany every CreateNew receipt.

The fixed 192-case inventory covers Ideal/Display, both paragraph directions,
13/17 DIP em sizes, four DPI scales, three original texts (including kerning,
ligature and combining opportunities), and wrapped/unwrapped widths. Each line
records original source ranges, glyph IDs, advances, offsets, cluster/caret
metadata, baseline, selection and ink bounds. Continuations use independently
cloned original TextLineBreak objects after the preceding line is disposed.
Source font-family baseline/line-spacing, rendering/hinting em and culture are
recorded separately from output line metrics. Each original physical face also
reports normalized font metrics and its public nominal design advances; these
are distinct from positioned Display advances. Paired consumers must never feed
the expected output line baseline/height back as native request metrics and
then count that circular agreement as a formatting result.
No expected metrics are inferred from glyph ink, a font's nominal advances, or
the portable implementation. Null source offset/caret collections remain null.

The path-filtered CI jobs keep Windows Server x64 and Windows 11 ARM64 receipts
separate. The reference has a 60-second observation budget and a 90-second outer
process bound. A successful receipt establishes Microsoft API observations only;
it does not qualify a FreeType interpreter choice, portable Display selection,
caret/pixel parity, or a product/package release. A paired portable comparison
and original application gates remain required.

Public contract references: [TextFormatter.FormatLine](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.textformatting.textformatter.formatline?view=windowsdesktop-10.0)
and [GlyphRun](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.glyphrun?view=windowsdesktop-10.0).
Only public APIs and their observed results are used; no foreign text-engine
implementation is incorporated.

## Independent midpoint and positioned-run receipt

The optional fifth argument `midpoint-positioned-v2` produces a separate schema-2
receipt, not an extension/replacement of the original schema-1 192 cases. The
four-argument invocation keeps its original inventory and output fields. Neither
new producer hashes nor schema-2 receipts are admitted by the paired native
consumer's existing strict hash whitelist. Qualification requires the complete
successful exact producer run on each original Windows architecture; these
authored cases are not observations until that run succeeds.

The additional fixed 288 cases cover physical em 18.5 and 20.5, their immediate
double predecessors/successors, exact DPI 1/2, Ideal/Display, both paragraph
directions, and original wrapped/unwrapped widths. Unlike 19.5/25.5, the new
midpoints distinguish even from half-up rounding. Binary-exact DPI scales keep
both adjacent sides representable without inventing a tolerance. Input metadata
records source em, DPI and physical-em double bits, alongside the actual source
values. WPF may quantize those inputs internally: the probe reports its output
unchanged and never requires neighbouring inputs to produce different output.

Plain `Hello`, mark-positioning `x\u0301 m\u0302 A\u0308 `, and an otherwise
identical Unicode RLO/PDF-wrapped text are independent case kinds. The original
UTF-16 remains intact. A Latin run in an RTL paragraph is not assumed to be an
RTL GlyphRun. Every positioned case must actually report a nonzero original
offset, and every override case an odd-direction original run, or the capture
fails explicitly before receipt publication. This checks coverage, not any
expected offset or advance value. The no-control counterpart remains present.

`Original` retains every existing line, glyph, physical font, nominal design
advance, source cluster, offset, caret distance, selection and ink field using
the original public double values. No float narrowing, rounding, guessed
26.6-to-source offset conversion or expected-output baseline is applied. The
same exact Inter physical-face check, Microsoft strong-name/module provenance,
32-line/caret traversal bounds, 60-second observation and 90-second process
bounds apply. CI captures each family in a fresh process, within the unchanged
eight-minute job limit, with distinct CreateNew receipt/log paths.

`WpfDisplayTextReference.Tests` links only the pure input/coverage contract. Its
synthetic JSON tests prove inventory, exact-double preservation and fail-closed
coverage; they neither execute WPF nor generate qualified oracle receipts. This
change affects the reference only: managed/native renderers, shaping policies,
source Display admission and runtime packages are unchanged.

After the substantive source commit, the Windows reference project compiled
against cached reference assemblies with zero warnings/errors; all 13 device-free
input/coverage tests passed with zero skips. Workflow YAML and all three PowerShell
blocks parsed without executing those blocks. The complete original capture and
source helper methods remain byte-identical, and the original 192-case loop is
unchanged apart from indentation. No new original Windows observation, native
text execution, GPU run or runtime staging was performed. Actual Inter mark/RTL
coverage and midpoint observations remain pending both hosted Windows captures.
