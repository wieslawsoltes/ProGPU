# SVG checksum investigation and main integration

Integrated main `cde81083be9533761e2fe5573adf5d27015e5324` (PR #160)
with a conflict-free merge, preserving published branch history. The new SVG
performance budget, timing/allocation limits and expected checksum are unchanged.
The branch's existing retention of passing quality frames is also preserved.
This is source integration, not completed CI or package qualification.

## Authoritative comparison

Downloaded the W3C quality artifacts from main run
[34516549448](https://github.com/wieslawsoltes/ProGPU/actions/runs/34516549448)
and branch run
[34516759230](https://github.com/wieslawsoltes/ProGPU/actions/runs/34516759230).
The latter tests source `26a6030f` combined with main in merge commit
`6b63af65f3259d33739ff8c2a8c50677451f8c33`.

Recomputing the benchmark's width/height/centre-ARGB hash over the ten retained
branch quality PNGs gives `1eff2c56a78504b8`, exactly the failed performance
result. For `w3c|text-text-01-b`, both images are 480 by 360:

| Measurement | Main | Branch |
| --- | --- | --- |
| Centre ARGB at (240, 180) | `ff000000` | `80000000` |
| Quality report error | 0.20453165044134528 | 0.17491342615273825 |

Replacing just that final centre sample in the branch's checksum calculation
with main's sample gives the expected `1eff2c6cbe8504b8`. The text images differ
at 23,209 pixels, with difference bounds (5, 8)–(408, 352). This is not merely
a changed timing measurement. Both complete frames were visually inspected;
the lower aggregate error does not establish that every changed pixel is correct.
Main deletes passing quality images, so this does not independently prove that
all other benchmark images are identical to main.

There is no `src/System.Drawing.Common` source difference against current main.
Shared text, scene and backend rendering code does differ. The intended source
text coverage and the responsible shared rendering change still require a
matched reproduction. Do not restore opaque coverage, update the checksum, or
weaken the gate solely on these observations.

Local downloaded evidence is under `artifacts/svg-checksum.QSsnec`, separately
under `main` and `branch`; GitHub retains the authoritative run artifacts. No
images or third-party implementation were copied into product source. This
investigation changes no managed/native rendering behavior or public contract.

## Matched local reproduction

Built the corpus runner separately against main `cde81083` and branch
`c5623470` on macOS arm64 with SDK 10.0.201. Both used the CI-pinned Svg.Skia
`03f64b67badfca9fca216dc25896d0c0ee04e7b7`, SVG.NET submodule
`fd33bed4ff14c803b800214ddec977ca0a2e0f8e`, and the existing CI project
overlay (applied locally with patch tooling because the preparation script uses
GNU sed). Dependency preparation is uncommitted and not product source.

Both builds succeeded with zero errors and 115 upstream warnings. The focused
quality command retained its existing known difference: main error 0.206989,
branch 0.179278. Both registered the same 22 pinned font faces with inventory
digest `7d2494f409eca08073006614ff6a8ccfb6cf7746e39102c432a6d8b945e6e2f3`.
The centre samples reproduce the CI difference exactly: main alpha 255, branch
128. Therefore this difference is not exclusive to Linux's software adapter.
The checked Chrome reference has white at that pixel; the legacy W3C PNG is
transparent there. Neither tested output is established as correct by that
sample. The SVG source places a black text label there, not a shape guide.

## Per-fixture performance evidence

The runner now records each existing warmup centre sample, with fixture key and
dimensions, in `performance-fixture-samples.json`. It writes this file after the
timed iterations. The measured loop, combined checksum, budget evaluator and
limits are unchanged. Warmup evidence does not replace the timed gate and does
not claim image parity; the retained quality PNGs remain necessary.

A local diagnostic run with one iteration produced ten unique ordered samples;
recomputing their combined hash reproduced the timed `1eff2c56a78504b8` result.
The final sample identifies `w3c|text-text-01-b` with ARGB `80000000`.
This one-iteration ARM64 run intentionally did not apply the seven-iteration
X64 CI budget and is not performance qualification. Its logs and JSON are under
`artifacts/svg-checksum.QSsnec/diagnostic-performance*`. The renderer cause and
the original failing CI checksum remain unresolved; no baseline was changed.

## Isolated probes

At `bca8474b`, created a separate detached diagnostic worktree under
`artifacts/svg-checksum.QSsnec/probe-source`; no experimental renderer change
was applied to the delivery branch.

* Restoring main's two text-mask fragment alpha outputs and matching text-mask
  pipeline source-alpha selection produced a frame byte-identical to the local
  branch frame. That paired change does not explain this fixture difference.
* Running the unmodified branch with `PROGPU_COMPUTE_EXECUTION` set separately
  to `compute`, `raster` and `simd` produced byte-identical frames, each with
  centre alpha 128. These are fixture-output comparisons, not proof that every
  glyph path was exercised or that the fallback implementations are fully qualified.
* After restoring the probe's text-mask changes, substituting main's complete
  ordinary path-raster shader (only its entry-point name adapted) changed other
  pixels but retained centre alpha 128. Its aggregate error was 0.175933.
  Thus that shader substitution alone does not restore the expected checksum.

All probe builds succeeded. Retained evidence uses `mask-probe*`, `glyph-*` and
`path-probe*` under the same artifact directory. The final diagnostic worktree
still contains the isolated path-shader experiment, not a proposed product fix.
The production ordinary path shader, shared text-mask fix and configurable GPU/
SIMD execution paths remain unchanged. Further source/command or composition
isolation is needed; these results do not authorize a baseline change.

## Paired path probe correction and control-hull isolation

Further isolation corrected an invalid inference in the earlier shader-only
probe. Main's path shader interprets fill-rule value 0 as even-odd; the branch's
shared shader interprets value 1 as even-odd and its encoder deliberately maps
the managed enum. Substituting only main's shader therefore mixed two incompatible
encodings. Its unchanged centre pixel did not exclude path rasterization.

In the separate diagnostic worktree, pairing that main shader with main's raw
managed fill-rule encoding reproduced the local main frame byte-for-byte,
including centre alpha 255 and aggregate error 0.206989. This is diagnostic
compatibility isolation, not a proposed rollback of the shared wire encoding.

Adding only the branch's quadratic and cubic control-hull Y rejection checks to
that matched main shader changed the centre alpha to 128 and aggregate error to
0.182320. The whole image is not identical to the branch, so this isolates the
checksum-changing sample, not every rendering difference. The checks originate
in 5b4f925e. The next investigation must verify actual curve crossings and the
endpoint/root solver around those rejected sample rows; reverting valid culling
or accepting a new checksum without that evidence would be premature.

Separately, a private copy of the branch runner with main's ProGPU.Text assembly
produced an image byte-identical to the branch. Restoring branch text and replacing
ProGPU.Scene with main's assembly retained the same reported aggregate error.
These mixed-binary probes are not release or cross-platform qualification. The
delivery worktree's renderer and checksum were not changed.

Evidence: artifacts/svg-checksum.QSsnec/text-assembly-probe.H9vGyQ (isolated binaries
and output), fill-rule-probe.log/build log and output directory, and
control-hull-probe.log/build log and output directory. The first diagnostic build
omitted the absolute ProGpuSourceRoot required by the SVG overlay and failed;
the corrected explicit-root build succeeded. Probe-source still contains the
main shader experiment, diagnostic raw encoding and the two control-hull checks;
none is staged as product code. No new third-party implementation was copied.

## Captured curve and impossible crossing

The probe now emits the fixture's original compiled segments and raster grids.
It captured 22 path batches (three-times scale). An independent float32 numerical
probe of the old normalized cubic solver found an accepted root for this actual
label segment:

| Point | X | Y |
| --- | --- | --- |
| Start | 90.447998046875 | 37.676002502441406 |
| Control 1 | 90.85600280761719 | 37.676002502441406 |
| Control 2 | 91.12999725341797 | 37.465999603271484 |
| End | 91.2699966430664 | 37.04600143432617 |

At sample Y 38.02083206176758, the scalar float32 Cardano evaluation returns
t=0.005859375 with a negative derivative, accepted by the old interior-root
policy. Yet every control Y is below that sample: a polynomial Bézier's
nonnegative Bernstein weights cannot produce that Y for t in [0,1]. Double
roots of the rounded coefficients are approximately 82576.5 and a complex pair,
not a segment crossing. This proves the candidate crossing is impossible; the
scalar calculation is not a claim of bit-exact GPU evaluation or whole-image parity.

The authored GPU regression uses this curve plus a separate rectangle so that
the empty sample is inside the combined record bounds but outside both contours.
It checks empty sample pixels against the clear frame and checks visible rectangle
ink separately. The curve control-hull checks must not be reverted merely to
recover main's checksum. Final sample accounting, matched native coverage and
full representative-image review remain necessary before a baseline decision.

Diagnostics are curve-dump.log, curve-root-probe.py and curve-dump-build.log under
the same isolated artifact directory. NumPy from the bundled workspace runtime
was used for numerical analysis; system Python lacked that dependency. The
diagnostic worktree also logs its borrowed segment data and forwards worker stderr;
these temporary logging changes are not part of product rendering.

The focused GPU test passes on macOS arm64, and all eight tests in
`GpuCameraCoverageTests` pass with no skips. Its initial reference frame had no
attached scene and returned uninitialized transparent pixels; the fixture now
attaches an empty retained picture before rendering the reference. Coverage
assertions are unchanged. This validates the current renderer's empty samples,
not an old-renderer failure or matched native-backend coverage. Logs are
`control-hull-regression.log` and `camera-coverage-regression.log` beside the
diagnostic evidence.

## Current merge gate observations

At ProGPU head `8047a26d`, the browser WebGPU CI job completed successfully.
The representative performance job still fails only its checksum assertion:
all seven iterations report `1eff2c56a78504b8`; timing and allocation budgets
report no violations. Other pending jobs must finish before claiming green CI.
LibreWinForms PR #29 has passing checks at its existing pin. LibreWPF head
`124fe3396` successfully queries its exact pinned producer, then rejects failed
ProGPU run 34522229666 for `e574a911`; SDK consumers consequently remain skipped.
Update downstream pins only with the coherent producer revision, retaining the
exact-revision requirement. No checksum, performance budget, SDK gate or core
application acceptance requirement is waived by this investigation.

## Paired old-shader and native MIL regression

Copied the current headless test output into a private artifact directory and
substituted only the diagnostic backend assembly. With main's ordinary shader
and no control-hull checks, the new test fails: an expected clear pixel
`[20,20,31,255]` becomes white `[255,255,255,255]`. Adding only the quadratic
and cubic control-hull checks back makes that same test pass. Both diagnostic
backend builds report zero warnings/errors. This isolated single-crossing fixture
does not depend on the old/new fill-rule encoding distinction (nonzero and
evenodd both admit one false crossing). Product assemblies were not overwritten.
Logs: `old-curve-test.log`, `culled-curve-test.log`, and their backend build logs
under the existing diagnostic artifact directory.

The existing native package consumer now renders the same curve, independent
rectangle and three-times transform through NativeMilChannel and the C++ retained
renderer. Seven empty sample pixels must remain exactly opaque black, and the
rectangle's interior red channel must be 255. This check runs in its ordinary
rendering lane, including `--render-only`; `--mil-only` still does not claim GPU
coverage. The project-reference build and rendering run pass locally on macOS
arm64 with the staged native library. Logs: `native-curve-build.log` and
`native-curve-test.log`. This is native wgpu rendering evidence, not Dawn rendering,
fresh package provenance, Windows/Linux qualification or full SVG image parity.

Together these tests establish the correctness reason for retaining the
control-hull rejection. The representative checksum remains unchanged pending
the full-image review; no sampled checksum substitutes for that review.
