# Original source Display comparison

This separate probe calls `LayoutHintedSourceParagraph`, never the old float
`LayoutHintedParagraph`. The old raw-slot probe remains independent. Source
em/DPI, width and genuine pre-formatting Typeface baseline/spacing enter the
original double options/styles. Only units-per-em comes from `head`; `hhea` and
expected output line geometry cannot supply source request metrics.

The new original-Windows `--source-inputs` capture is required. Its schema-2/4
receipts must acquire exact reviewed hashes after both Windows jobs succeed.
The source-input hash allowlist is intentionally empty until those observations
exist: a declared source SHA or a caller-provided hash cannot authorize input.
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
original Windows jobs, followed by exact receipt-hash review; the new allowlist
remains empty and ordinary Display stays unadvertised.
