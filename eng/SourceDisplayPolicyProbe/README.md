# Original source Display comparison

This separate probe calls `LayoutHintedSourceParagraph`, never the old float
`LayoutHintedParagraph`. The old raw-slot probe remains independent. Source
em/DPI, width and genuine pre-formatting Typeface baseline/spacing enter the
original double options/styles. Only units-per-em comes from `head`; `hhea` and
expected output line geometry cannot supply source request metrics.

The new original-Windows `--source-inputs` capture is required. Only exact
reviewed schema-2/4 hashes from the successful capture below are admitted:
a declared source SHA or a caller-provided hash cannot authorize input.
Existing source-input-era 192 receipts and the exact reviewed 288 v3 receipts
are accepted for device-free validation only; they explicitly lack the required
Display request metrics. Their original bytes, case order, counts (386/450 and
426/546 lines/runs), glyph occurrences and source payloads stay intact.

The retained WPF `b23e1203fa95824d4a3332e599c5805c60b3030b` factory is the source
request reference: complete original paragraph, source ascent=observed baseline,
descent=observed spacing-minus-baseline, original default line height, explicit
capture/interpreter/numeric policies, original source features and no local
metric correction. These receipts use one default physical style, no typography
overrides, no tabs/objects, zero indent and emergency wrapping. The midpoint
source doubles sometimes exceed the WPF factory's current exact-float raster-em
admission; that fact is recorded separately, never promoted to source admission.

Every Display input executes both TT35 and TT40. Em, advance and offset policy
names are required command arguments; no defaults are selected. Physical capture
arguments implement only those explicit input choices and are independently
validated by native. Unsupported native domains remain failed observations with
the complete original case, never trimmed, reordered or retried through Ideal.
Other native failures prevent receipt publication. No engine/device is created;
nominal resource preparation explicitly selects intrinsic SIMD.

Strict original UTF-16, font, full native level, glyph/run/cluster/line ownership
alignment reuses `HintedDisplayPolicyProbe.SourceComparison`. Only exact aligned
occurrences enter native `CopySourceMetrics`, the same batched source-run seam
used by the WPF adapter. Those original double advances/nominal-source offsets
are compared unchanged with original GlyphRun arrays; no index search, prefix
shape, float promotion, offset sign repair or tolerance is used. Double line
full width, height, baseline offset and origin are compared separately. Both
raw native shaping and effective source generation/raster shadows are retained.
An exact comparison is limited to these fields: caret/selection data is retained
but not qualified, and source/glyph pixel/application parity remains separate.

## Invocation after qualification

First verify the whole exact native Build has succeeded, then stage that exact
package by the existing provenance procedure. The executable records supplied
source/Build metadata but does not independently qualify it. An individual green
native job, failed/canceled Build or older same-name library is not sufficient.

```sh
dotnet run --project eng/SourceDisplayPolicyProbe -c Release -- \
  source-inputs.json Inter-Regular.ttf unused-for-192 \
  /absolute/qualified/libprogpu_native.dylib /absolute/CreateNew.json \
  EXACT_NATIVE_COMMIT https://github.com/wieslawsoltes/ProGPU/actions/runs/SUCCESSFUL_RUN \
  FloatCaptureNearestHalfUp SourceIdealUnits SourceIdealUnits
```

For source-midpoint inputs provide the pinned original Hebrew font path. Package
mode uses `-p:ProGpuRuntimePackageVersion=EXACT_VERSION` and an isolated verified
feed. The 60-second CPU budget and CreateNew-only output remain explicit; all
resources are retired while the exact module stays loaded through process exit.
No runtime has been staged or new native producer executed for this preparation.

Device-free controls require one immutable original receipt:

```sh
PROGPU_SOURCE_DISPLAY_REFERENCE=/absolute/original/reference.json \
PROGPU_SOURCE_DISPLAY_INPUT_RECEIPT=/absolute/reviewed/source-inputs.json \
  dotnet test eng/SourceDisplayPolicyProbe.Tests -c Release -m:1
```

These parser/comparison controls are not original Windows observations, native
execution or ordinary Display activation. Managed/native renderer behavior is
unchanged; both consumers use the same future qualified native text producer.

## Preparation checks

After the substantive capture/probe commits, all 31 device-free controls passed
against each unchanged original x64 and ARM64 receipt from both the 192-case
source-input-era producer and the 288-case midpoint/Hebrew producer (124 test
executions, zero skipped). The unchanged raw probe's 64 controls and midpoint
inventory's 17 controls also passed. All six changed/new executable C# files
parsed, and the workflow YAML and all four PowerShell blocks parsed without
executing those blocks. `git diff --check` passed.

The original Windows-targeted reference project compiled against cached reference
assemblies with zero warnings/errors after adding the missing explicit System.IO
import. This was a source-only compile, not WPF execution on the local host.
The new native-linked probe has not been type-built against the pending producer
package or executed. No native graph/renderer build, GPU/VM execution or runtime
staging occurred. Genuine independent input capture still requires the hosted
original Windows jobs, followed by exact receipt-hash review. That independent
input qualification is now recorded below; ordinary Display stays unadvertised.

## Reviewed independent source input capture

[Original Windows workflow 37011550318](https://github.com/wieslawsoltes/ProGPU/actions/runs/37011550318)
succeeded on both architectures at exact source
`92f4778b0db2d802d84b50281bc4c4e97349b4db`. Every original case payload is
exactly equal to the prior immutable 192/v3 receipts; no drive normalization,
glyph reorder, field removal or tolerance was needed. Each independent capture,
after excluding only its added metric-input field, also equals its same-run
primary case. Counts remain192/386/450 and288/426/546 cases/lines/runs.

Every observed metric input has the exact original font/URI, em/DPI bits, mode,
toReal=1, fixed method signatures and PresentationCore identity/hash. All metric
values agree across x64/ARM64. The actual captured source inputs, not output line
baselines, now authorize the new native factory arguments. Retained artifacts
are under `/private/tmp/progpu-display-independent-inputs.swgc8V`.

| Architecture | Independent receipt | SHA-256 |
| --- | --- | --- |
| ARM64 | source-inputs.json | `1542bcbf3fe0837a85565c8e716213c8d24aad2fd2cee69dae87d7888633934e` |
| ARM64 | source-midpoint-inputs.json | `66eb33702bfa83a208b7af91e7f6a8d332f28ed9d03631b43dfb9b3b84039a56` |
| x64 | source-inputs.json | `4f0d43ec902a767e01c46e635196a5d1f960489d93cde863a9e8010a8cec669a` |
| x64 | source-midpoint-inputs.json | `781ceaf0e530463543fc6ab27857b8dc34d26cd7ed88b77281f7f80414395b90` |

The old schemas still reject native execution for absent independent metrics.
Receipt immutability/finite tamper/occurrence-reorder controls are additive.
After the allowlist/control commit, all 37 controls passed against each paired
old/new x64/ARM64 schema1/2 and schema3/4 receipt (148 executions, zero skipped).
This is original Windows input evidence, not a successful native producer Build,
native comparison, interpreter selection, complete Display or application result.

## Hosted source type compilation

The existing Linux Build job now compiles this probe immediately after `Build
tests`, with the same Release configuration, runtime and CI setting. The earlier
package-consumer compile builds `Backend.Native` but not `Text`; the two test
project builds provide both real library outputs under their existing
`bin/Release/net10.0` paths (library project references omit the executable RID).
The probe step requires those files, permits its normal restore and sets
`BuildProjectReferences=false`; missing references or compiler errors fail the
job, never skip it. It does not rebuild the dependency graph, run the probe,
stage a native artifact, create a device or change existing tests/deadlines.
This closes the probe's direct type-compilation gap. A passing compile is not
native execution or source comparison: the whole exact producer Build must
still succeed before the separate bounded native observations above.
