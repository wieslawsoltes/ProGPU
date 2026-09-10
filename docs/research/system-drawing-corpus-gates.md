# System.Drawing rendering corpus gates

## Decision

ProGPU now has a source-built SVG.NET consumer gate in
`eng/SystemDrawing.SvgCorpus`. The runner builds the `Svg` library from the
`wieslawsoltes/SVG` submodule against ProGPU's assembly named
`System.Drawing.Common`; it does not resolve Microsoft's package at runtime.
This exercises the public `System.Drawing`, `System.Drawing.Drawing2D`, text,
image, clipping, compositing, and codec paths through a mature real consumer.

The inputs and expected PNGs come from the same pinned Svg.Skia checkout used
by the existing SkiaSharp-shim gate:

- Svg.Skia commit `03f64b67badfca9fca216dc25896d0c0ee04e7b7`;
- 1,730 resvg SVG/PNG fixture pairs under
  `externals/resvg/crates/resvg/tests/{tests,extra}`; and
- 525 SVG 1.1 W3C SVG/PNG fixture pairs under
  `externals/W3C_SVG_11_TestSuite/W3C_SVG_11_TestSuite/{svg,png}`.

The [SVG.NET project](https://github.com/wieslawsoltes/SVG) is MS-PL and its
project directly consumes `System.Drawing.Common`. The
[resvg fixture source](https://github.com/wieslawsoltes/resvg) is available
under MIT or Apache-2.0. The pinned W3C fork links back to the
[W3C SVG test-suite overview](https://www.w3.org/Graphics/SVG/Test/Overview.html)
but does not carry a root license file; continue consuming it by pinned CI
checkout as the existing Svg.Skia gate does, and confirm W3C attribution terms
before redistributing the corpus in packages or release artifacts.

## Gate behavior

The quality command performs the following for every fixture:

1. Decode the reference PNG with StbImageSharp, independently of ProGPU's
   image decoder.
2. Parse the SVG with SVG.NET and render it at the exact reference dimensions
   using ProGPU `System.Drawing.Common`.
3. Encode the result through ProGPU's PNG path and decode it independently.
4. Compare premultiplied RGBA using the same normalized root-mean-square error
   shape used by the Svg.Skia suite. W3C rows backed by Chrome references are
   composited on white with Svg.Skia's established normalization; resvg and
   legacy transparent references remain raw RGBA comparisons.
5. Retain only failing PNGs, and write per-fixture time, allocation, error, and
   exception data to `quality-results.json`.
6. Load the pinned resvg font database directly through ProGPU `TtfFont`
   before SVG.NET creates any `System.Drawing.Font`. W3C's additional TrueType
   resources are loaded for that suite as well. The JSON evidence records the
   source-file count, loaded face count, family names, skipped files, and a
   SHA-256 of the ordered font inventory; rendering therefore does not depend
   on fonts installed on the GitHub runner.
7. Compare the exact failure keys with
   `eng/system-drawing-svg-known-differences.txt`. A new failure or an
   unreviewed improvement fails the job, so changes cannot silently weaken or
   stale the inventory.

Expected third-party exceptions are tracked separately in
`eng/system-drawing-svg-known-exceptions.txt`, including their exact managed
exception type and a reviewed reason. A new exception, a type change, or a
resolved entry fails the gate. This keeps SVG.NET parser limitations and
recursive malformed inputs visible without treating them as ProGPU pixel
baselines or weakening `System.Drawing` argument validation.

The runner also validates the exact corpus sizes. Missing submodules, silently
dropped files, or unexpected corpus updates therefore fail before producing a
misleading parity result. The workflow splits resvg and W3C into independent
jobs and uploads JSON, the candidate inventory, and failing images. Each
quality fixture executes in an isolated child process, so malformed recursive
SVG input or a native backend termination is reported against that exact row
instead of discarding the rest of the corpus. Independent workers run
concurrently, capped at four by default; `--max-parallelism 1` retains a serial
diagnostic mode. Results and generated inventories remain sorted by corpus
identity rather than completion order.

`eng/system-drawing-svg-threshold-overrides.txt` contains narrowly reviewed
per-fixture thresholds for cross-architecture reference variation. The first
entry raises only W3C `shapes-intro-01-t` from `0.100` to `0.102`: its embedded
SVG font has a small remaining rasterization variance between local ARM64 and
hosted x64 and was observed at `0.099077` and `0.100143`. Host font selection is
now eliminated separately by loading and hashing the corpus-owned fonts. The
rest of both corpora retains the suite threshold. Overrides are parsed as typed
finite values, validated against the pinned fixture catalog, and carry an inline
reason; unknown fixture keys fail the gate.

The performance command uses ten representative W3C fixtures spanning basic
shapes, paths, gradients, patterns, and text. It warms the complete pipeline,
then records seven isolated Release samples with elapsed time, total managed
allocation, and a pixel-derived checksum. The automatic gate requires the
complete fixture set, at least seven samples, the expected semantic checksum,
the expected x64 process architecture, and both median and nearest-rank p95
time/allocation budgets from
`eng/system-drawing-svg-performance-budget.json`. The JSON evidence also
records the OS, process architecture, runtime, processor count, and GC mode.
Keep the complete corpus as the correctness gate and use this representative
set for bounded regression detection rather than as a universal throughput
claim.

The hosted x64 budget was calibrated from three successful seven-sample runs.
Their medians were `4,597.710`, `4,637.553`, and `4,593.515` ms and
`175,522,168`, `175,528,952`, and `175,521,832` allocated bytes. The gate
allows `5,750` ms median / `6,500` ms p95 and `185,000,000` B median /
`190,000,000` B p95. These intentionally broad limits detect material
regressions while allowing hosted-runner noise. Recalibration requires a
reviewed reference commit and several equivalent Release runs; an improvement
does not justify silently relaxing a correctness inventory or changing the
fixture checksum.

The 2026-09-07 pinned-font local ARM64/.NET 10.0.400 software-WebGPU run records
a `5,655.852` ms median and `176,001,760` median allocated bytes for the complete
ten-fixture pipeline. All seven samples produced checksum `1eff2c6cbe8504b8`;
elapsed samples ranged from `5,055.619` to `5,761.553` ms and allocation from
`175,998,448` to `177,056,960` bytes. This is a reproducible command-shape and
allocation baseline, not a cross-machine throughput claim. The roughly 176 MB
per iteration is also a concrete optimization target after correctness and
hosted cross-platform behavior are stable.

## First corpus-driven repair

The initial reviewed inventory exposed a renderer error rather than an SVG.NET
parser limitation: ProGPU sampled `PathGradientBrush.InterpolationColors` in
the opposite direction. The public GDI+ contract defines preset position zero
at the path boundary and position one at the center; the retained path-gradient
shader now follows that direction. A direct GPU regression checks blue at the
boundary and red at the center, while the focused System.Drawing suite checks
the exact production-shader path. The resvg
`radialGradient/gradientTransform` error consequently falls from `0.568567` to
`0.059166`, below the `0.12` gate, and the ordinary radial-gradient cases fall
to roughly `0.003` error. SVG focal-radius behavior remains separately visible
because this SVG.NET implementation does not apply its `fr` value when it
constructs the System.Drawing path gradient.

Across the full pinned corpora, the repair resolves 51 resvg differences and
two W3C differences. The reviewed baseline moves from 1,110 to 1,160 passing
resvg fixtures (`492` pixel differences and `78` exceptions) and from 67 to 69
passing W3C fixtures (`447` pixel differences and `9` exceptions). One new
resvg difference is intentional attribution rather than a renderer regression:
SVG.NET constructs a path gradient for the invalid negative-radius SVG while
the reference ignores that invalid paint server. The earlier reversed sampling
had only hidden that upstream input-validation difference.

The first hosted x64 run also exposed 16 text fixtures that passed on the
developer ARM64 machine only because that machine happened to have Noto Sans
installed. The reference SVGs explicitly request the Noto and related faces
shipped in resvg's own test font database. Loading those exact pinned files in
every isolated worker makes all 16 pass under their unchanged suite threshold;
no text-specific tolerance or platform-difference waiver is used.

The direction rule is documented by Microsoft in
[`PathGradientBrush::SetInterpolationColors`](https://learn.microsoft.com/windows/win32/api/gdipluspath/nf-gdipluspath-pathgradientbrush-setinterpolationcolors).

## Development commands

With a recursive pinned Svg.Skia checkout in `external/Svg.Skia`:

The preparation script also installs an SVG.NET-local
`Directory.Packages.props`, so SVG.NET keeps its explicit package versions
without changing ProGPU's central package policy.

```bash
./eng/progpu-prepare-svg-system-drawing.sh \
  "$PWD/external/Svg.Skia/externals/SVG"

dotnet run \
  --project eng/SystemDrawing.SvgCorpus/SystemDrawing.SvgCorpus.csproj \
  --configuration Release \
  -p:SvgSourceRoot="$PWD/external/Svg.Skia/externals/SVG" \
  -p:UseProGpuSystemDrawing=true \
  -p:ProGpuSourceRoot="$PWD" \
  -- quality \
  --corpus-root "$PWD/external/Svg.Skia" \
  --artifacts "$PWD/artifacts/svg-system-drawing/all" \
  --known-differences "$PWD/eng/system-drawing-svg-known-differences.txt" \
  --known-exceptions "$PWD/eng/system-drawing-svg-known-exceptions.txt" \
  --threshold-overrides "$PWD/eng/system-drawing-svg-threshold-overrides.txt" \
  --suite resvg \
  --threshold 0.12
```

Repeat the command with `--suite w3c --threshold 0.10`. The thresholds are
suite contracts and must not be collapsed into one looser combined run.

The official `ColorMatrix(float[][])` contract requires five rows and five
columns and throws for undersized input. SVG.NET currently forwards empty or
short `feColorMatrix type="matrix"` values to that constructor. The two exact
resvg rows are therefore retained as consumer-level differences until SVG.NET
validates the required 20 SVG coefficients and ignores the malformed filter
primitive before constructing a `ColorMatrix`; relaxing the public
`System.Drawing` constructor is not an acceptable fix. Consequently the current
strict resvg inventory is 1,158 passes, 494 pixel differences, and 78 expected
exceptions; the W3C inventory remains 69 passes, 447 differences, and 9
expected exceptions.

To reproduce one row through the same isolated worker and inventory checks,
add an exact key such as
`--fixture 'resvg|tests/painting/stroke/pattern'`. Corpus-size validation still
runs before selection, so a partial or stale checkout cannot masquerade as a
successful focused test.

For representative performance evidence, replace the arguments after `--`
with:

```text
performance
--corpus-root <Svg.Skia checkout>
--artifacts <artifact directory>
--benchmark-fixtures eng/system-drawing-svg-benchmark-fixtures.txt
--performance-budget eng/system-drawing-svg-performance-budget.json
--iterations 7
```

## Official .NET System.Drawing behavior gate

The active [dotnet/winforms `System.Drawing.Common` tests](https://github.com/dotnet/winforms/tree/main/src/System.Drawing.Common/tests)
are now source-built against ProGPU as a second independent consumer gate. They
cover precise managed API behavior and
rendering operations such as
[`Graphics.DrawLine`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Graphics_DrawLineTests.cs),
[`Graphics.DrawBezier`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Graphics_DrawBezierTests.cs),
bitmap and codec behavior, matrices, paths, regions, fonts, and metafiles. The
repository is MIT licensed and already includes a `mono` compatibility slice.

The gate pins dotnet/winforms commit
[`b8acee9d29af0ed4c9049cea5f05f80570ecf3b0`](https://github.com/dotnet/winforms/commit/b8acee9d29af0ed4c9049cea5f05f80570ecf3b0).
The test assets are pinned independently to dotnet/runtime-assets commit
[`13b701d371826c168b26d581351422c031089faa`](https://github.com/dotnet/runtime-assets/commit/13b701d371826c168b26d581351422c031089faa).
Both repositories are fetched sparsely under `artifacts`; upstream sources and
assets are not copied into the product or its packages.

The pinned tree has 89 C# source files. The source-build enables 82 unchanged
upstream files, discovers 1,753 test methods, and expands them to 4,453 cases.
Seven files are excluded in
`eng/system-drawing-upstream-excluded-files.txt` because they require Win32 HDC,
HWND, enhanced-metafile, HICON, RemoteExecutor, or internal friend-assembly
contracts. Two individual native/host-font cases are excluded in
`eng/system-drawing-upstream-excluded-methods.txt` because xUnit's upstream
dynamic-skip metadata evaluates them before their platform condition. Every
exclusion carries a reviewed reason. The small
`PortableTestInfrastructure.cs` adapter exists only in the source-built test
project and supplies typed test utilities and platform facts; it is not shipped
or reachable from product code.

On Linux ARM64, the current reviewed strict baseline is 3,263 passed, 1,105
known failures, and 85 skips. The gate records counts by
fully qualified method and exception type, plus an exact skip inventory and
summary. Any new failure, changed exception, resolved failure, changed skip, or
case-count change fails until reviewed, so the known-failure ledger cannot hide
a regression or silently retain an improvement. Separate platform baselines
make OS/architecture differences explicit rather than broad-skipping portable
behavior.

The first hosted Linux x64 capture was reviewed from the failed gate artifact
and promoted without changing any test selection. After applying the same
architecture-independent Matrix repairs, its exact manifest records 3,279
passes, 1,140 known failures, and 34 skips across the same 4,453 expanded
cases. The architecture difference remains an explicit inventory, not an
ARM64 baseline copied onto x64.

The corpus immediately exposed two real implementation issues. All 141 named
`Brushes` mutation cases failed because the standard brush cache had incorrectly
shared `SystemBrushes` immutability; standard brushes are now mutable while the
separate system-brush cache remains immutable. Another 26 cases restored
`PrinterSettings` page, enum, filename, null, default-printer, and diagnostic
managed semantics while retaining explicit platform failures for native printer
handles. A second managed-contract pass corrected GDI+ gamma direction,
`ImageAttributes` validation/disposal/remap semantics, `ColorMatrix` malformed
input behavior, profile-path validation, and solid-brush clone identity. Across
these first implementation passes, 295 expanded cases moved from known failure
to pass. The next source-built tranche makes all 220 official `MatrixTests`
cases pass and repairs dependent gradient/texture transform behavior: GDI+
validation and disposal exception shapes, approximate identity, non-finite
invertibility, self-multiplication, identity hashing, empty point batches,
finite-overflow saturation, and disposed-matrix no-op behavior. In total, 367
expanded ARM64 cases have moved from known failure to pass
(2,896/1,472 to 3,263/1,105).

Run the strict gate with:

```bash
./eng/progpu-verify-upstream-system-drawing-tests.sh
```

`--update-baseline` is a review operation, not a normal development escape
hatch. Inspect candidate failures, skips, summary JSON, XML, and runner logs
under `artifacts/system-drawing-upstream/results` before accepting a change.

## Additional corpus research

### Adopt selectively: Mono managed System.Drawing tests

The archived [Mono System.Drawing test tree](https://github.com/mono/mono/tree/main/mcs/class/System.Drawing/Test)
contains long-lived contracts for drawing primitives, regions, paths, imaging,
fonts, and printing. Representative examples include
[`RegionDataTest`](https://github.com/mono/mono/blob/main/mcs/class/System.Drawing/Test/System.Drawing/RegionDataTest.cs),
[`PathDataTest`](https://github.com/mono/mono/blob/main/mcs/class/System.Drawing/Test/System.Drawing.Drawing2D/PathDataTest.cs),
and [`MetaHeaderTest`](https://github.com/mono/mono/blob/main/mcs/class/System.Drawing/Test/System.Drawing.Imaging/MetaHeaderTest.cs).
Mono's class libraries are generally MIT licensed, but each imported asset and
test should retain its provenance notice.

Port only tests that add behavior not already present in the current .NET
suite. Prefer test-data reuse and small adapters over copying the obsolete
NUnit/security-permission infrastructure.

### Differential oracle: Wine GDI+ tests

Wine's [`dlls/gdiplus/tests`](https://github.com/wine-mirror/wine/tree/master/dlls/gdiplus/tests)
is unusually valuable for native GDI+ edge semantics. Its focused suites cover
graphics, images/codecs, brushes, pens, matrices, paths, path iterators,
regions, fonts, string formatting, and especially
[`metafile.c`](https://github.com/wine-mirror/wine/blob/master/dlls/gdiplus/tests/metafile.c).
The code is LGPL-2.1-or-later.

Do not translate or copy Wine test implementation into ProGPU. Instead, use it
as an external black-box oracle on Windows/Wine: generate compact inputs with
independently written ProGPU tests, capture native GDI+ status/geometry/pixels,
and compare serialized observations. This preserves clean-room boundaries while
still exposing many underspecified edge cases.

### Later SVG expansion: Web Platform Tests

The SVG working group identifies
[Web Platform Tests](https://github.com/web-platform-tests/wpt/tree/master/svg)
as the SVG 2 test suite. WPT includes SVG reftests where a test is compared with
an independently expressed reference. Add only a static, resource-closed
subset that SVG.NET can parse without browser DOM, JavaScript, networking, or
HTML layout. Pin both the WPT commit and generated subset manifest, and require
each enabled row to have a deterministic viewport, fonts, and resource closure.

This is complementary to the SVG 1.1 PNG corpus: WPT expands newer SVG/CSS
features, while the pinned PNG suites give a stable raster oracle today.

### Backend diagnostics, not System.Drawing conformance

The [Cairo regression suite](https://cgit.freedesktop.org/cairo/tree/test/README)
and Skia's GM/DM/Gold infrastructure contain excellent raster stress cases.
They should be used to isolate compositor problems such as antialiasing,
clipping, operators, gradients, and sampling, but not counted as
`System.Drawing` API parity because their public contracts and raster rules are
different. Reproduce only small specification-derived cases unless licensing
and provenance review explicitly approves fixture reuse.

## Recommended order

1. Continue closing the official dotnet/winforms pure managed/headless failure
   inventory in coherent API groups.
2. Add hosted x64 and then macOS/Windows platform baselines without weakening
   the existing exact inventory checks.
3. Add only unique Mono cases with provenance recorded per imported file.
4. Build a serialized native-Windows/Wine differential oracle for GDI+ and
   metafile edge cases.
5. Add a deterministic static WPT SVG 2 subset.
6. Use Cairo/Skia cases only as backend diagnostics tied to a failing public
   System.Drawing or SVG consumer scenario.
