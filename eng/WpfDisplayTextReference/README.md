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

The optional arguments `midpoint-rtl-positioned-v3 <pinned-rtl-font-directory>`
produce a separate schema-3 receipt, not an extension/replacement of the
original schema-1 192 cases. The
four-argument invocation keeps its original inventory and output fields. Neither
new producer hashes nor schema-3 receipts are admitted by the paired native
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

Plain `Hello` and mark-positioning `x\u0301 m\u0302 A\u0308 ` retain exactly
their prior Inter inputs. The new `RtlPositioned` kind uses original strong
Hebrew and niqqud text `\u05E9\u05B8\u05C1\u05DC\u05D5\u05B9\u05DD ` with
a separate exact Noto Sans Hebrew Regular physical face. Each input identifies
its font key; each original run still records the actual physical FontUri,
nominal design advances, source characters and all original double metrics.
There is no source rewriting, implicit fallback or offset conversion. Every
positioned case must actually report nonzero offsets, and the Hebrew case must
observe nonzero offsets **in an actual odd-direction run**, not in a separate
even run. Coverage failure prevents receipt publication, without prescribing
any expected offset or advance value. Paragraph direction alone is not evidence
of run direction; both original paragraph directions remain in the inventory.

The former `midpoint-positioned-v2` RLO/PDF hypothesis is unqualified. Both
architectures of original workflow 36996659823 rejected its first RLO case:
U+202E selected Segoe UI with zero advance/empty ink, while visible Inter
`x\u0301 ` reported BidiLevel 0 and a nonzero offset. The strict font guard
correctly rejected this; neither fallback nor odd-run coverage is relaxed.
`CreateLegacy` and its paired no-control tests retain those exact failed inputs.
v3 explicitly changes the RTL source and physical font, not its labels or
expected output. The four old argument invocation and all 192 original cases
remain independent and unchanged; every midpoint axis and ordinal is retained.

### Pinned test-only RTL font

`rtl-font.json` is the common workflow/embedded producer manifest. CI downloads
the unmodified 26,900-byte static Noto Sans Hebrew Regular 3.000 from
[notofonts/noto-fonts at ffebf8c1ee449e544955a7e813c54f9b73848eac](https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/hinted/ttf/NotoSansHebrew/NotoSansHebrew-Regular.ttf).
Its SHA-256 is `a7fa16fffb27bedb060a0866267c29e9859aeb9c21cc33f5b3aaf6eb062eca85`.
The original [SIL Open Font License 1.1 and notice](https://github.com/notofonts/noto-fonts/blob/ffebf8c1ee449e544955a7e813c54f9b73848eac/LICENSE)
is retained beside it (`OFL.txt`, 4,377 bytes, SHA-256
`0dab92d0544f7b233403f14b84a663bdbfa746982eda629e7f4f9ffe1b036feb`).
The font also carries its own original copyright in its name table. Both files
are hash/length checked before use and after capture, accompany the evidence
artifact, and have separate receipt identities. No font is installed, modified,
added to product packages or accepted from an ambient Windows fallback. The
producer's existing exact-path check applies independently to every run of the
chosen face, including any zero-ink runs. Download failure is a hard failure,
with each request bounded to 30 seconds; the original 192-case process runs first.

Read-only original table inspection establishes glyph coverage for every chosen
Hebrew letter, mark and space, and presence of GPOS; that is not a WPF shaping
observation. Actual odd-run/positioning coverage remains a hosted Windows gate.

`Original` retains every existing line, glyph, physical font, nominal design
advance, source cluster, offset, caret distance, selection and ink field using
the original public double values. No float narrowing, rounding, guessed
26.6-to-source offset conversion or expected-output baseline is applied. The
same exact selected physical-face check, Microsoft strong-name/module provenance,
32-line/caret traversal bounds, 60-second observation and 90-second process
bounds apply. CI captures each family in a fresh process, within the unchanged
eight-minute job limit, with distinct CreateNew receipt/log paths.

`WpfDisplayTextReference.Tests` links only the pure input/coverage contract. Its
synthetic JSON tests prove inventory, exact-double preservation and fail-closed
coverage; they neither execute WPF nor generate qualified oracle receipts. This
change affects the reference only: managed/native renderers, shaping policies,
source Display admission and runtime packages are unchanged.

Before the RTL fixture correction, the Windows reference project compiled
against cached reference assemblies with zero warnings/errors; all 13 device-free
input/coverage tests passed with zero skips. Workflow YAML and all three PowerShell
blocks parsed without executing those blocks. The complete original capture and
source helper methods remain byte-identical, and the original 192-case loop is
unchanged apart from indentation. Those 13 controls and the original 192 cases
also passed in both hosted Windows jobs; their failed v2 expansion is described
above. The v3 RTL corpus and added tests are authored, not yet executed at this
commit. No native text execution, GPU run or runtime staging is part of this
reference change. All 288 v3 observations remain pending both hosted captures.
