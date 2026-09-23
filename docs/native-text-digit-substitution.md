# Native text digit substitution

## Contract

Styled paragraphs may select a Unicode decimal digit sequence independently for
each source style. `PortableTextStyle.DigitZero` and
`NativeTextParagraphStyle.DigitZero` contain the first scalar in a contiguous
ten-digit Unicode `Nd` sequence with numeric values zero through nine. Zero disables substitution. The optional
`ContextualDigits` flag uses that sequence only in Unicode Arabic-letter bidi
context; otherwise the original European digits remain active.

The generated C ABI retains its 32-byte style record. Its final
`digit_substitution` word stores the digit-zero scalar in the low 21 bits and
the contextual policy in `PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL`.
Native validation rejects unknown bits, invalid scalars and sequences whose ten
members do not have the exact decimal values zero through nine. The existing
.NET-derived Unicode category generator also emits decimal-zero scalars and
checks every complete sequence. This rejects shifted starts such as U+1D7CF
inside adjacent mathematical digit alphabets, even though all ten scalars are
`Nd`. Forced invalid policies fail before any
paragraph result is published.

## Native pipeline

When any style enables substitution, the C++ paragraph call copies the borrowed
scalar records into its existing caller-owned scratch arena. It applies digit substitution to that copy before
bidi resolution, script itemization, grapheme and line-break analysis, font
selection and OpenType shaping. The copy retains every original `input_index`
and `input_length`; caret, selection, wrapping and source editing therefore
continue to address the original UTF-16 text. No managed rewrite or per-digit
native call is involved.

Retained paragraph metadata resolves through `NativeTextBidiInterop.ResolveStyled`,
which applies that same native substitution to the existing bidi scratch copy.
Resolving metadata from the original European digit scalars would incorrectly
export level zero for forced Arabic digits in an LTR paragraph, despite level-two
rendering. Original source indices remain unchanged; glyph levels, selection
boxes and caret stops now consume the substituted bidi state in ordinary, measured
inline and continued paragraphs. The unstyled resolver and scratch requirements
remain unchanged, with no additional crossing or source-sized allocation.

For contextual substitution the scan starts from the explicit paragraph
direction. An `AL` bidi strong character selects the culture digits. An `L` or
`R` strong character selects European digits. This includes strong directional
controls and `AL` punctuation, and is not restricted to the Unicode letter
category. Other bidi classes retain the preceding context, including ordinary
European digits. The state crosses adjacent style
runs so splitting formatting does not change the nearest preceding strong
character. Mandatory line-break classes `BK`, `CR`, `LF` and `NL` reset to the
initial paragraph direction.

## Source font selection

`NativeTextShapingInterop.ResolveDigitContext(ReadOnlySpan<char>, bool, Span<byte>)`
exposes the same C++ state transition through one synchronous C ABI call.
`IPortableTextDigitContext` is the optional neutral provider capability for source
adapters that must choose physical fonts before formatting. The input remains
unchanged, output is one byte per UTF-16 code unit, and both surrogate units receive
their scalar's context. A value of one means `AL` context after that scalar; zero
means European context. The return value is the final context. Output tails remain
untouched. Invalid UTF-16, short output, invalid flags or overlapping buffers fail
before any output changes. Empty input returns its initial state.

Full paragraphs pass their direction as the initial state. An adapter that carries
the returned state between chunks must split at hard segment boundaries and seed
each new segment from paragraph direction. This prevents a prior segment's strong
character from becoming a later segment's reset direction. The source can then map
the selected digit scalars to its real physical font ranges and submit forced digit
policies for those intervals while preserving original source text and positions.
The provider cannot reconstruct those source font rules from family names.

The four-argument overload additionally returns native UAX #29 grapheme-start
bytes through the same crossing. It reuses the native paragraph's boundary-state
analyzer, including prepend, emoji ZWJ and Indic conjunct rules. A one marks the
first UTF-16 unit of a grapheme; low surrogates and other interior units are zero.
These bytes are independent of the raw digit context: for example U+0903 can
change strong context inside an Arabic-base grapheme. Source adapters must use
the actual digit's context while keeping physical font/style partitions on native
grapheme boundaries. This prevents font selection from splitting a base and mark
or a prepend character from its digit.

## Performance and ownership

The active algorithm is one allocation-free `O(N)` pass over the paragraph scalar
snapshot and uses the paragraph's bounded scratch storage. Paragraphs without an
active digit policy retain their existing scratch size and input path. Substitution is
scalar-dependent because context can cross run boundaries; SIMD is not suitable
for the contextual state transition. The context-only API validates UTF-16 once,
then performs that dependent scan in `O(N)` time and `O(1)` auxiliary storage;
caller output uses `N` bytes. It initializes no font, shaping context or GPU device.
The optional grapheme output adds one dependent pass and `N` caller-owned bytes,
without allocating scalar or cluster arrays. There is no per-grapheme crossing.
All later compute-heavy bidi and shaping
work remains in the existing native batched pipeline.

## Conformance evidence

The native showcase test compares forced and contextual substitution with direct
Arabic-Indic and European input. It covers initial LTR/RTL context, preceding
Latin and Arabic letters, reset after a hard line break, preserved source clusters
and atomic rejection of shifted decimal sequences. Native context tests cover
Latin, Hebrew, Syriac, Arabic, supplementary Arabic scalars, neutral surrogate
pairs, directional controls, hard boundaries, invalid UTF-16 and untouched tails.
Grapheme tests distinguish an interior strong-mark context change, prepend/digit
ownership and a supplementary emoji ZWJ cluster, while retaining atomic failures.
Managed tests verify UTF-16 style mapping, the exact generated wire policy and
rejection of insufficient or aliased spans before crossing into native code.

The managed native-package consumer additionally compares complete retained
glyphs, lines, source cluster ends, bidi levels, boxes and carets with direct
Arabic-digit scalar input. Its ordinary, contextual, hard-break, continued and
inline cases execute in the existing package gate; the explicit
`--text-digit-substitution-only` selector runs that same fixture independently.

The behavior is based on the public WPF
[`NumberSubstitutionMethod`](https://learn.microsoft.com/dotnet/api/system.windows.media.numbersubstitutionmethod)
contract and Unicode bidirectional classes from
[`UAX #9`](https://unicode.org/reports/tr9/). No WPF, DirectWrite or operating
system implementation source is included or translated.
Decimal values follow the [Unicode numeric property contract](https://www.unicode.org/reports/tr44/#Numeric_Type)
through the repository's existing .NET 10 Unicode data generation. This change
retains the shared source/shaping/rendering ownership described in
`native-mil-text-source-integration.md`; it introduces no glyph cache, raster,
worker, DPI or device-recovery policy.

## Remaining source integration

LibreWPF must resolve each source `DigitState` to the actual culture digit
sequence and use the native context capability before selecting actual physical
fonts for rendered digit intervals. Numeric punctuation has a separate source
contract; this API admits digit substitution only. Windows comparison, including
strong controls/punctuation and hard-line behavior, remains an independent
application and package qualification gate. Managed and native MIL renderers
consume the same resulting physical-font glyph runs; this source-text preparation
does not introduce a renderer-specific substitute.
