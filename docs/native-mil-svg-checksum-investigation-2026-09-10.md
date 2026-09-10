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
