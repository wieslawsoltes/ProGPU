# Original-source EDIT word boundaries

The internal native profile resolves a complete original UTF-16 input once into
an owned boundary snapshot. It currently reproduces the inventories for 22 of
the 24 independent Microsoft EDIT requests; the BMP-symbol and emoji/joiner
requests remain explicitly unadmitted. This is **not** a complete portable word
classifier, ordinary Forms provider admission or application qualification.

The independent receipt is from LibreWinForms Build 36802156343,
Windows job 110178653752, source head 639fe6938027c6e2565ae84968e1010b8ed665d8.
Its SHA-256 is
`647da7cdd14bfad8c5b4567b553bcbfa5ceacfde3c3823524abc0271ad430a0b`.
It contains all original 20 and four additional Thai/Lao/Khmer cases, 398
requested gestures, 388 observed gestures and ten explicitly unavailable
coordinates. Assertions here are independent literal UTF-16 inventories, not
results generated from ICU or this implementation. CRCRLF interior positions
were unavailable; its atomic group follows the documented
[EditWordBreakProc contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nc-winuser-editwordbreakproca),
not a newly inferred observable hit at index3.

## Explicit profile, not raw UAX or `fWordStop`

The existing native Unicode17/UAX14 worker and property tables are unchanged.
The explicit EDIT profile assembles original line opportunities, whitespace
seams and CR-group starts; it never feeds its results back into shaping, bidi,
graphemes, carets or line fitting. Whitespace is not `char.IsWhiteSpace`:
the receipt excludes TAB/CR/LF, NBSP and ideographic space from `fWhiteSpace`,
while NNBSP supplies a whitespace seam. Space-separator generalization outside
the reference inventory is an unqualified profile hypothesis, not a Windows
Unicode-table equivalence claim.

[SCRIPT_LOGATTR](https://learn.microsoft.com/en-us/windows/win32/api/usp10/ns-usp10-script_logattr)
distinguishes soft line opportunities from keyboard word stops. In particular,
Khmer's many `fWordStop` bits do not become mouse-word boundaries. The profile
admits an original Khmer-to-whitespace seam, but suppresses Lao/Khmer dictionary
opportunities; broader mixed-script tailoring remains unqualified.

Only interior original Thai-to-Thai seams may admit dictionary results. One
private ICU line iterator sees the **complete unchanged source**, not isolated
words, reshaped prefixes or normalized text. Its other script opportunities are
discarded. There is no fixture-word dictionary, recorded-index lookup, font,
target or source-local repair. The four reference results are:

| Original request | Original UTF-16 boundaries |
| --- | --- |
| Thai adjacent | 0, 4, 7, 11, 15 |
| Thai spaced | 0, 4, 8, 12, 16 |
| Lao adjacent | 0, 15 |
| Khmer adjacent | 0, 18, 19 |

Raw ICU LINE matched only 14 of the 24 inventories in an authorized installed
ICU 78.1 probe; raw WORD also split punctuation and trailing spaces incorrectly.
Neither raw engine is accepted as EDIT. The actual pinned 78.3 implementation
passes the explicit profile controls, including both Thai requests.

## Owned optional ICU dependency

[ICU 78.3](https://unicode-org.github.io/icu/download/78.html) uses Unicode 17.
[eng/native-edit-word-icu.json](../eng/native-edit-word-icu.json) pins its official
release tag/commit, source-archive SHA-256, complete original license SHA-256 and
original little-endian data SHA-256. Configuration requires that exact existing
archive; it never searches for or loads ambient ICU, downloads a binary or
substitutes OS data. The archive was independently downloaded from the official
release and its actual source/license/data hashes checked before authoring.

The original common-library compilation inventory is compiled into a private
static/PIC library. Versioned symbols have an additional `_progpu_edit` suffix,
hidden linkage and matching private C++ namespaces. Before using dictionaries,
every selected public ICU function must belong to the executing image, and its
version must be exactly 78.3. A serialized one-time initialization registers the
owned original data and disables all filesystem data access. Each request owns
its iterator and exact-typed original UTF-16 copy; no global mutable text or
borrowed cached output is published. Other original Unicode workers never
call this private ICU.

The full original data is 33,107,232 bytes; no size/startup/performance improvement
is claimed. The full Unicode-3.0 and original bundled notices remain intact in
the verified source archive. This source-only target installs no new SDK/NuGet
payload. Product admission still requires coherent six-RID static architecture,
owner, original-notice, packaging and independent consumer controls. The private
build is not a prepared runtime dependency receipt.

## Focused device-free checks

The standalone harness contains only the classifier and existing Unicode
workers; it does not configure Dawn, fonts, GPU devices or the complete product.

```sh
cmake -S eng/edit-word-boundary-contracts -B artifacts/edit-word-boundary-contracts/no-icu -G Ninja -DCMAKE_BUILD_TYPE=Release
cmake --build artifacts/edit-word-boundary-contracts/no-icu -j 2
ctest --test-dir artifacts/edit-word-boundary-contracts/no-icu --output-on-failure

cmake -S eng/edit-word-boundary-contracts -B artifacts/edit-word-boundary-contracts/owned-icu -G Ninja -DCMAKE_BUILD_TYPE=Release -DPROGPU_NATIVE_EDIT_WORD_ICU_SOURCE_ARCHIVE=/absolute/path/to/icu4c-78.3-sources.tgz
cmake --build artifacts/edit-word-boundary-contracts/owned-icu -j 2
ctest --test-dir artifacts/edit-word-boundary-contracts/owned-icu --output-on-failure
```

Both configurations passed locally on macOS ARM64 with strict warnings on the
ProGPU-owned source. They exercise original inventories, precise absent-engine
failures, UTF-16 preservation, independent/concurrent output ownership, empty
input, leading-content identity and unchanged default UAX14 behavior. These are
CPU semantic controls, not hosted Windows/provider/package or mouse-routing
qualification. Original Forms source-seam and password controls are unchanged.

## Remaining contracts before source admission

The reference joins BMP snowman/snowman-VS to adjacent Latin, unlike modern
line-breaking; the justified general property range is not yet established.
Its emoji request retains a word boundary at original UTF-16 index 7 inside the
modern woman-ZWJ-laptop grapheme. Replacing it with UAX29, deleting the boundary
to accommodate shaping, or treating every ZWJ as an ordinary combining mark
would silently change source selection. BMP ID-class symbols and ZWJ inputs
therefore fail with distinct unqualified-policy errors and leave the previous
snapshot completely untouched. Unknown complex-script policies and invalid
UTF-16 also fail before publication. No complete provider is claimed for the
admitted easy-script inputs.

Resolve those rules with bounded independent source/reference evidence before
expanding transport or publishing Forms capability markers. The later source
adapter must own one snapshot alongside the exact original retained layout,
text and interaction generation. Password source still bypasses the provider
before any source text reaches this worker. Every original source-handler,
selection-generation, signed-anchor and drag-reversal guard remains required.

## Bounded hosted Windows policy reference

The purpose-named `Native EDIT word policy reference` workflow runs only an
isolated original Microsoft WindowsDesktop oracle on
[Windows Server 2025 X64](https://github.com/actions/runner-images/blob/main/README.md#available-images),
using the exact source reference SDK `11.0.100-preview.5.26302.115`. It never
builds ProGPU products, packages or renderers and does not use a VM. The original
24 inputs, indices and policy flags are retained byte-for-byte from the pinned
LibreWinForms reference; their source inventory hash is checked before building.
Its original 30-second internal and 60-second outer process limits are unchanged.

Separate fresh processes record the original24 controls, 72 unknown contextual
EDIT observations, and a bounded BMP symbol-property attribute sweep. Contexts
distinguish BMP/supplementary symbols, variation selectors, emoji/nonemoji joiners,
Latin, Arabic, Indic and CJK surrounding source. There are no guessed expected
word endpoints. The sweep alone records no EDIT gestures and cannot qualify a
mouse-selection rule. Each original script item also records direct `ScriptBreak`
input units/raw bytes independently of the complete-source `ScriptStringAnalyse`
result; disagreement is evidence, not permission to substitute either API.

CreateNew artifacts preserve partial failures. Receipts and execution metadata
retain the exact checked-out head, source/binary hashes, PIDs, SDK information,
OS build, actual runtime/architecture, current/UI locale and loaded native-library
paths, versions and hashes. Every raw UTF-16 unit, script/property bit and
unavailable native coordinate is preserved. Server2025 outputs must be compared
with the earlier Windows11 receipt using this provenance; they are not silently
the same source platform. Neither these observations nor schema rejection controls
admit the still-unresolved classifier policies or ordinary Forms capability.
