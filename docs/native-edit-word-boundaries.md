# Original-source EDIT word boundaries

The internal native profile resolves a complete original UTF-16 input once into
an owned boundary snapshot. The focused controls retain all 24 independent
Microsoft EDIT inventories; the two formerly unadmitted BMP-symbol/emoji
inventories now pass with a pinned measured property profile. The two Thai
inventories require the exact owned ICU dependency. This is **not** a complete
portable word classifier, ordinary Forms provider admission or application
qualification: unmeasured/nonmatching symbol and real script-item policies remain
explicitly unsupported.

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

The existing native Unicode17/UAX14 default behavior and property tables are unchanged.
An internal shared-worker profile suppresses the modern after-ZWJ LB8a
prohibition and resolves only the precisely measured BMP symbol domain below
for EDIT selection; the ordinary worker continues using its original rules.
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
line-breaking; the justified measured property domain is recorded below.
Its emoji request retains a word boundary at original UTF-16 index 7 inside the
modern woman-ZWJ-laptop grapheme. Replacing it with UAX29 or deleting the boundary
to accommodate shaping would silently change source selection. The independently
observed joiner profile and measured BMP symbols are now implemented below.
Unobserved/nonmatching BMP So/ID members fail with an unqualified-policy error
and leave the previous snapshot completely untouched. Unknown complex-script policies and invalid
UTF-16 also fail before publication. No complete provider is claimed for the
admitted easy-script inputs.

Resolve the remaining item rules with bounded independent source/reference evidence before
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

### Hosted result and isolated joiner policy

[Run 36862800693](https://github.com/wieslawsoltes/ProGPU/actions/runs/36862800693)
succeeded at exact source `6e7286564e1e44d1fce4e398769aa9805ecac9fb`.
All original24 controls and contextual72 observations completed. The contextual
receipt contains 624 observed gestures and 148 unavailable coordinates; every
available initial double-down range agrees with its independent complete-source
soft-break inventory and the previously recorded strict-previous-boundary rule.
The raw symbol sweep completed 3,854 symbols in three contexts (11,562 cases),
but records no EDIT gestures. Receipt SHA-256 identities are:

| Receipt | SHA-256 |
| --- | --- |
| original24.json | `f1ecce8f26af8a862bcf76d7a700246f2fef3f27f5050e946bc2c359772e4c08` |
| contexts.json | `9c94f64741b11cc131dce0c04f86417d2d411f64f3d42bf957dc6b735f0c66f8` |
| symbol-attributes.json | `643db9eef41889455161d460bbfa7cf14449edf6e65de2ca5e5a0e6a2caa9bb1` |
| execution.json | `ef4091230d7c75054d922141dec73b0fdd886574cd9ca8ff08e98912bc09ba40` |

The actual runner reported OS build `10.0.26100.0`, X64, `en-US` current/UI
locale and runtime `11.0.0-preview.5.26302.115`. The receipt retains the loaded
GDI32/gdi32full/usp10/TextShaping versions and hashes. These Server2025 results
are not automatically Windows11, other-locale or other-build qualification.

ZWJ and ZWNJ have the same observed selection attachment in the supplied Latin,
Indic, supplementary-letter, supplementary-CJK and supplementary-emoji contexts.
They attach to the preceding line class but do not prohibit a later boundary
merely because the preceding source scalar is ZWJ. For example,
`x\U0001F469\u200D\U0001F4BBy ` has original UTF-16 boundaries
`0,1,4,6,8`; index4 stays inside the modern emoji grapheme. Leading, trailing,
space-adjacent and repeated ZWJ observations agree. The internal shared-worker
profile changes only LB8a after ZWJ; it does not rewrite code points, UTF-16
indices, raw properties, graphemes, shaping, bidi or ordinary line fitting.
Nineteen literal contextual CPU controls pass in the focused no-ICU harness,
including an explicit unchanged-default-LB8a control. The original24 inventories
and absent-dictionary/atomic rejection controls remain intact.

The contextual symbols disprove a blanket BMP `So`/`ID` to `AL` rule: snowman
joins adjacent Latin while watch and smile split, including their VS16 variants.
Emoji_Presentation also does not distinguish them. The symbol script properties,
CTYPE1 and single-symbol direct ScriptBreak first byte do not identify this
distinction. Comparing the official Unicode LineBreak data from versions
3.2, 5.2, 6.0–9.0 and 13–17 did not yield an exact historical property profile;
no historical table is substituted. The measured domain below is generated
mechanically from exact original-API observations, not guessed property ranges.

A separate contextual gap is explicit: `x\u0628\u062Ay ` and its ZWJ/ZWNJ
variants have a native boundary at index1 that the line/whitespace profile does
not emit. The unchanged canonical script itemizer identifies an interior Arabic
run whose start has no existing boundary; this precise domain now fails atomically
with `unqualified_script_item_transition_policy`, including all three contextual
rejection controls. This interim guard is not an implemented item-boundary policy
or original-case parity. Direct ScriptBreak for the actual Arabic item starts with raw byte13,
unlike the neighboring Latin item. That is evidence requiring a real script-item
policy, not permission to make every script/bidi transition a word boundary.
Unmeasured BMP and mixed-script contracts therefore still block a complete classifier
or provider. No reference-generated word/index lookup is used in production.

The next observer revision retains the original24 and contextual72 inventories
and adds a **separate** 128-input item-context phase: repeated symbols/VS16 within
actual native items, varied Arabic entries, and several distinct script entries
in both paragraph directions. Direct ScriptBreak still receives only the whole
actual ScriptItemize item and its original SCRIPT_ANALYSIS; it never combines
items or constructs a substitute script identity. Raw
[CTYPE2/3](https://learn.microsoft.com/en-us/windows/win32/api/stringapiset/nf-stringapiset-getstringtypew)
records are preserved per original WCHAR, including surrogate halves, alongside
CTYPE1. They are not assumed equivalent to modern scalar properties or to a
soft-break class. Original 30/60-second process limits, callback limits, CreateNew
evidence and the 16/128MiB receipt bounds remain unchanged. All new results are
unknown observations without generated expected word endpoints.

### Pinned measured symbol property domain

[Run 36867054042](https://github.com/wieslawsoltes/ProGPU/actions/runs/36867054042),
job `110385050238`, succeeded at exact source
`4aab8ead3eafac60a57b6a646debad132184ef13`. It retained original24/context72
and completed the separate 128 item contexts and all 11,562 symbol observations.
The actual runner remains Server2025 build `10.0.26100.0`, X64, en-US,
`.NET 11.0.0-preview.5.26302.115`; this is not automatic Windows11 equivalence.
The original loaded-module paths/versions/hashes and native API inputs remain
in the bounded receipts. Their exact identities are:

| Receipt | SHA-256 |
| --- | --- |
| symbol-attributes.json | `ef40688691684bba820071ac712699884439da99dd456daee86b4e37878ac7c3` |
| item-contexts.json | `a095a0cc680cca4aea8248b16cd13e4bc9afe2d3bb8bf811a7795f279d7fa706` |
| execution.json | `4cac80ec27456687cb553ec50090c4fd6afd92c2869e048a2986bb87b4db668a` |

[eng/native-edit-word-symbol-profile.json](../eng/native-edit-word-symbol-profile.json)
pins the observed domain, counts, source identity and exact original ProGPU
Unicode17 property-file hash. The generated data considers **only** observed BMP
runtime `OtherSymbol` scalars whose original raw line class is `ID`. All three
unchanged-source contexts must match one complete soft-break signature:

| Resolved property | `a SYMBOL b SP` | `a SYMBOL VS16 b SP` | `CJK SYMBOL CJK SP` | Members |
| --- | --- | --- | --- | --- |
| AL | none | none | 1, 2 | 15 |
| ID | 1, 2 | 1, 3 | 1, 2 | 902 |
| unqualified | any other combination | any other combination | any other combination | 82 |

This is a bounded measured property profile, not a UnicodeScript-to-engine
classifier, word dictionary, fixture-word/index lookup or vendor implementation.
Consecutive equal **observed** records alone are compacted; no holes, unobserved
characters or nonmatching signatures are filled. Enclosed-Hangul and Balinese
members with different signatures remain atomic unsupported controls. Original
Unicode17 properties, shaping, source units and modern default line breaking are
unchanged. The original symbol and emoji requests now retain their independently
observed UTF-16 inventories `0,5,7,9` and `0,4,7,9,11`, including the interior
modern-grapheme boundary. General per-item engine assembly remains required.

The generator validates exact receipt/property hashes, completion/platform/API
identity, full UTF-16 inputs, all-three-context coverage and the pinned decision
counts. Its output was reproduced byte-for-byte from the exact receipt. Twenty
Python schema/reproducibility/rejection tests pass; their synthetic records are
not native evidence. The strict no-ICU native harness passes the 22 available
original inventories (the two Thai requests fail precisely for absent ICU),
44 independent repeated-symbol/VS16 controls from the **separate held-out**
item receipt, and three mixed-heart/joiner controls. The generator never reads
the held-out receipt. The prior owned-ICU Thai controls are unchanged; that
larger configuration was not rerun for this property-only slice.

```sh
python3 eng/progpu-generate-edit-word-symbol-profile.py --receipt /absolute/path/to/symbol-attributes.json --check src/ProGPU.Native/src/Text/progpu_native_edit_symbol_data.generated.hpp
python3 -m unittest discover -s eng/tests -p 'test_edit_word*.py'
```

The complete-source Arabic/complex-script entry policy is still not implemented.
CTYPE2/3, general category and UnicodeScript alone do not identify the original
Windows engine or its contextual item-first behavior. The existing atomic
transition guard is interim safety, not issue completion or Forms admission.
