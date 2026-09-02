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
  `externals/resvg/resvg-test-suite/{tests,extra}`; and
- 533 SVG 1.1 W3C SVG/PNG fixture pairs under
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
   shape used by the Svg.Skia suite.
5. Retain only failing PNGs, and write per-fixture time, allocation, error, and
   exception data to `quality-results.json`.
6. Compare the exact failure keys with
   `eng/system-drawing-svg-known-differences.txt`. A new failure or an
   unreviewed improvement fails the job, so changes cannot silently weaken or
   stale the inventory.

The runner also validates the exact corpus sizes. Missing submodules, silently
dropped files, or unexpected corpus updates therefore fail before producing a
misleading parity result. The workflow splits resvg and W3C into independent
jobs and uploads JSON, the candidate inventory, and failing images.

The performance command uses ten representative W3C fixtures spanning basic
shapes, paths, gradients, patterns, and text. It warms the complete pipeline,
then records seven isolated Release samples with elapsed time, total managed
allocation, and a pixel-derived checksum. Results are raw evidence rather than
a single-run performance claim. Establish regression budgets only after
several alternating runs on a pinned runner image; keep the complete corpus as
a correctness gate and use the representative set for iteration speed.

## Development commands

With a recursive pinned Svg.Skia checkout in `external/Svg.Skia`:

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
  --suite all \
  --threshold 0.12
```

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
