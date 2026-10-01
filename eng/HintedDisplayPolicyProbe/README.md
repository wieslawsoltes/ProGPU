# Hinted Display policy observations

This CPU-only diagnostic pairs the original Microsoft WPF reference receipts from
`eng/WpfDisplayTextReference` (PR 245) with **explicit** native TrueType 35 and 40.
It selects neither interpreter and does not enable source Display. A mismatch is
an observation, not permission to round source output, substitute Ideal or change
defaults.

The unchanged original 192-case / 386-line / 450-run receipt and its font hash are
validated before any native load. Only the four exact x64/ARM64 receipt SHA-256
values from whole-successful reference runs 36864998524 (3195c6e96) and
36866824045 (432f5d5d5) are accepted. Self-declared commit/token/inventory alone is
not original provenance: a finite changed advance, baseline, glyph ID or even
reserialized receipt rejects. Hashing and parsing consume one privately copied
snapshot, whose original hash is published without re-reading the path.
Every Display case is retained in full for each
policy. Each original Microsoft glyph-ID batch is captured without reshaping or
reordering; signed slot metrics, advances, side-bearing deltas and all other raw
26.6/16.16 fields remain separate from Microsoft output advances/offsets. The
complete original paragraph is also shaped and positioned once per policy with
its actual direction, preserving original native logical/positioned owners,
clusters, bidi, glyphs, boxes and carets. No source run or line suffix is reshaped.

Paragraph style metrics are genuine default `head`/`hhea` values from the existing
SFNT parser, explicitly labeled as diagnostic inputs. They are **not** Microsoft
output baseline/height values and are not asserted to equal WPF's source line
policy. Neither fitting differences nor raw-slot advance agreement alone prove
positioning parity. The retained original source records permit later alignment
only where original source, cluster, bidi and glyph identities actually agree.

The later reference's independent `SourceFont` inputs, physical `FontMetrics` and
per-original-occurrence `NominalDesignAdvances` are separately validated and
recorded. They do not replace the probe's explicit head/hhea diagnostic inputs.
The earlier producer explicitly records absence, not synthetic values. The newer
receipts match between architectures after only FontUri drive normalization;
their original output arrays also match the earlier receipts. This is source
reference evidence, not native Display metric qualification.

Logical shaped values remain signed 26.6, independently of actual float-positioned
output. Diagnostic double-DIP values divide by 64 then by the original double DPI;
they do not reuse the production float reciprocal. This exposes precision loss
separately from interpreter, shaping/GPOS, run/context and line-policy differences.
The probe does not repair product outputs or compare ink pixels.

## Complete-source fitting and advance comparison

`SourceContextComparison` now connects each of the existing 192 explicit-policy
observations to its original Microsoft Display case. It retains every source
line/run, original occurrence count and unmatched native positioned index.
Different fitting ranges, glyph counts/sequences, physical font owners,
per-run bidi levels or original UTF-16 cluster coverage produce named unmatched
results, never a skipped or passing case. Native original logical indices own
the comparison order; glyph-ID search cannot make a different sequence match.
The original full Microsoft receipt still retains all 192 Ideal/Display inputs.

The Microsoft final line may include exactly one virtual `TextEndOfParagraph`
unit beyond the original UTF-16 text. Raw length and virtual length are recorded
separately; only the final row's one extra unit is qualified. Other overshoot
rejects. This does not manufacture a source scalar, glyph or native line. The
existing wrapped/unwrapped source ranges are compared to the actual native
writer's ranges. Both source widths (with/without trailing whitespace) remain
separate from its retained native width, without selecting a width policy.

Only an exact occurrence alignment permits advance comparisons. Original signed
logical 26.6 values divide by 64 and the source's original double DPI, separately
from actual float-positioned advances. Both exact equality and signed deltas are
recorded without epsilon, rounding or substitution. Thus a matching logical
9.6-DIP advance and a different promoted float projection remain distinguishable.
Original `GlyphRun` offsets and native logical offsets/positions are retained,
but their different coordinate frames are explicitly **not** equated. Baseline,
height, source offset, caret, pixel and full Display policy qualification remain
open; the diagnostic head/hhea inputs are unchanged and are not Microsoft's
output line metrics.

Device-free comparison controls cover source/glyph/font/bidi/cluster rejection,
original logical/positioned ownership, signed and float-projection differences,
fitting mismatch and strict final-EOP handling. Existing immutable x64/ARM64
receipts exercise all 96 original Display cases and every original run as explicit
unmatched results when no native data is supplied. These are comparison/input
controls, **not** native Display observations or 96 passing parity cases. Actual
native source-context fitting/advance findings require the qualified runtime
gate below; no failed/incomplete native Build is staged for these tests.

Applicability: this diagnostic consumes the same retained native text generation
used by both native providers and managed native-text consumers. It changes no
renderer, formatter, interpreter, source metrics, native ABI or default policy.

## Bounded invocation

First verify a **whole successful exact-head Build**, then stage its exact package
using the existing verified staging procedure. Never use a green individual job,
failed/canceled/incomplete Build, system FreeType or a same-name ambient library.
The supplied Build URL/source commit are recorded but are not independently
qualified by this executable. An explicit library path is loaded into the process
and held until exit; font, native module, managed producer and reference hashes
are recorded. No library is copied, replaced or published.

```sh
dotnet run --project eng/HintedDisplayPolicyProbe -c Release -- \
  reference.json src/ProGPU.Fonts.Inter/Fonts/Inter-Regular.ttf \
  /absolute/qualified/package/libprogpu_native.dylib \
  /absolute/fresh-observations.json EXACT_NATIVE_COMMIT \
  https://github.com/wieslawsoltes/ProGPU/actions/runs/SUCCESSFUL_RUN
```

The default build references current managed sources. To compile against the exact
staged managed packages, pass `-p:ProGpuRuntimePackageVersion=EXACT_VERSION` with
the existing isolated NuGet configuration. Both forms use the same public native
context/batch/paragraph APIs. They never create an engine or GPU device. Native
errors fail the probe; no successful observation receipt is published on failure.

Without a qualified native runtime, validate original input without native calls:

```sh
dotnet run --project eng/HintedDisplayPolicyProbe -c Release -- \
  --validate-reference reference.json src/ProGPU.Fonts.Inter/Fonts/Inter-Regular.ttf
PROGPU_WPF_DISPLAY_REFERENCE=/absolute/reference.json \
  dotnet test eng/HintedDisplayPolicyProbe.Tests -c Release -m:1
```

On 2026-10-01 the managed probe compiled with zero warnings/errors, and both
unchanged original x64/ARM64 receipts passed input validation without native loads.
The original 19 focused receipt/identity/signed-conversion tests passed against
each original receipt, zero skipped. Eight follow-up controls cover exact-byte
provenance, finite-value tampering, immutable snapshot hash reuse and required
independent source metadata. These include an exact control distinguishing 9.6 source
double from promotion of the native float projection, not a native observation.
The complete 27-test suite passes against all four accepted original receipts,
zero skipped; the extended probe compiles with zero warnings/errors. New source
receipts and immutable provenance notes are retained internally under
`/private/tmp/progpu-display-source-inputs.8m8manYP`; no native runtime was staged.
Local execution is blocked by the absence of a verified internal native runtime;
the reviewed signed FreeType preparation also requires unavailable GnuPG. No
signature bypass, tool installation, failed-Build staging or native metric result
is claimed. The exact runtime/whole-Build gate remains required before observations.
