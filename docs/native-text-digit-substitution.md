# Native text digit substitution

## Contract

Styled paragraphs may select a Unicode decimal digit sequence independently for
each source style. `PortableTextStyle.DigitZero` and
`NativeTextParagraphStyle.DigitZero` contain the first scalar in a contiguous
ten-digit Unicode `Nd` sequence. Zero disables substitution. The optional
`ContextualDigits` flag uses that sequence only in Unicode Arabic-letter bidi
context; otherwise the original European digits remain active.

The generated C ABI retains its 32-byte style record. Its final
`digit_substitution` word stores the digit-zero scalar in the low 21 bits and
the contextual policy in `PROGPU_NATIVE_TEXT_DIGIT_SUBSTITUTION_CONTEXTUAL`.
Native validation rejects unknown bits, invalid scalars and sequences whose ten
members are not Unicode decimal digits. Forced invalid policies fail before any
paragraph result is published.

## Native pipeline

When any style enables substitution, the C++ paragraph call copies the borrowed
scalar records into its existing caller-owned scratch arena. It applies digit substitution to that copy before
bidi resolution, script itemization, grapheme and line-break analysis, font
selection and OpenType shaping. The copy retains every original `input_index`
and `input_length`; caret, selection, wrapping and source editing therefore
continue to address the original UTF-16 text. No managed rewrite or per-digit
native call is involved.

For contextual substitution the scan starts from the explicit paragraph
direction. An Arabic-letter bidi strong character selects the culture digits. A Latin,
Hebrew or other non-Arabic strong character selects European digits. Neutrals,
marks and digits retain the preceding context. The state crosses adjacent style
runs so splitting formatting does not change the nearest preceding letter.

## Performance and ownership

The active algorithm is one allocation-free `O(N)` pass over the paragraph scalar
snapshot and uses the paragraph's bounded scratch storage. Paragraphs without an
active digit policy retain their existing scratch size and input path. Substitution is
scalar-dependent because context can cross run boundaries; SIMD is not suitable
for the contextual state transition. All later compute-heavy bidi and shaping
work remains in the existing native batched pipeline.

## Conformance evidence

The native showcase test compares forced and contextual substitution with direct
Arabic-Indic and European input. It covers initial LTR/RTL context, preceding
Latin and Arabic letters, preserved source clusters and atomic rejection of an
invalid digit sequence. Managed tests verify UTF-16 style mapping and the exact
generated wire policy.

The behavior is based on the public WPF
[`NumberSubstitutionMethod`](https://learn.microsoft.com/dotnet/api/system.windows.media.numbersubstitutionmethod)
contract and Unicode bidirectional classes from
[`UAX #9`](https://unicode.org/reports/tr9/). No WPF, DirectWrite or operating
system implementation source is included or translated.

## Remaining source integration

LibreWPF must resolve each source `DigitState` to the actual culture digit
sequence, pass the digit-zero scalar and contextual flag on its retained physical
font style, and reject non-contiguous custom `NativeDigits` rather than changing
source indices. Windows comparison still remains an independent application and
package qualification gate.
