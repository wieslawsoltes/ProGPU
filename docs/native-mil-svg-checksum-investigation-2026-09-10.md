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
