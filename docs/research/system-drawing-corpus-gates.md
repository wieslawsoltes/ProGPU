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
6. Compare the exact failure keys with
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

The performance command uses ten representative W3C fixtures spanning basic
shapes, paths, gradients, patterns, and text. It warms the complete pipeline,
then records seven isolated Release samples with elapsed time, total managed
allocation, and a pixel-derived checksum. Results are raw evidence rather than
a single-run performance claim. Establish regression budgets only after
several alternating runs on a pinned runner image; keep the complete corpus as
a correctness gate and use the representative set for iteration speed.

The first local ARM64/.NET 10.0.400 software-WebGPU run records a `5,223.853`
ms median and `175,923,832` median allocated bytes for the complete ten-fixture
pipeline. All seven samples produced checksum `1eff2c6cbe8504b8`; elapsed
samples ranged from `5,101.123` to `7,425.449` ms and allocation from
`175,916,152` to `176,968,104` bytes. This is a reproducible command-shape and
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
  --suite all \
  --threshold 0.12
```

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
--iterations 7
```

## Additional corpus research

### Adopt next: official .NET System.Drawing tests

The active [dotnet/winforms `System.Drawing.Common` tests](https://github.com/dotnet/winforms/tree/main/src/System.Drawing.Common/tests)
are the highest-value next source. They cover precise managed API behavior and
rendering operations such as
[`Graphics.DrawLine`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Graphics_DrawLineTests.cs),
[`Graphics.DrawBezier`](https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/tests/System/Drawing/Graphics_DrawBezierTests.cs),
bitmap and codec behavior, matrices, paths, regions, fonts, and metafiles. The
repository is MIT licensed and already includes a `mono` compatibility slice.

The 2026-09-02 audit pins dotnet/winforms commit
[`b8acee9d29af0ed4c9049cea5f05f80570ecf3b0`](https://github.com/dotnet/winforms/commit/b8acee9d29af0ed4c9049cea5f05f80570ecf3b0).
Its `System/Drawing` test subtree contains 72 test files and 1,822 `[Fact]` or
`[Theory]` declarations, compared with 549 declarations in ProGPU's focused
suite. The gap is behavioral depth rather than public API presence: ApiCompat
already reports zero missing types and members. For example, the official
linear-gradient tests exposed that coincident endpoint construction must surface
GDI+'s `ExternalException`, not a generic argument exception; that contract now
has a focused ProGPU regression.

Proposed integration:

- source-build the tests against ProGPU with the same assembly identity;
- classify Windows-HDC, printer, installed-font, and native-handle cases rather
  than weakening them with broad platform skips;
- run pure managed and headless raster tests on Linux and macOS;
- run the native Windows differential lane on Windows; and
- keep exact skip and failure inventories per operating system.

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

1. Establish and review the SVG.NET resvg/W3C baseline, then make the workflow
   required for `System.Drawing.Common` and renderer changes.
2. Source-build the official dotnet/winforms System.Drawing tests and close its
   pure managed/headless set.
3. Add only unique Mono cases with provenance recorded per imported file.
4. Build a serialized native-Windows/Wine differential oracle for GDI+ and
   metafile edge cases.
5. Add a deterministic static WPT SVG 2 subset.
6. Use Cairo/Skia cases only as backend diagnostics tied to a failing public
   System.Drawing or SVG consumer scenario.
