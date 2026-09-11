# SVG.NET W3C threshold review — 2026-09-10

## Decision and evidence

Remove exactly 21 resolved W3C entries from the known-difference inventory after
reviewing every candidate PNG against both current-main output and the pinned
Chrome reference. The W3C threshold remains 0.10; the corpus still runs all 525
fixtures. Expected threshold differences decrease from 267 to 246, passing
classifications increase from 249 to 270, and nine expected exceptions remain.
No resvg entry, threshold override, exception or fixture selection changes.

This is threshold-baseline maintenance, **not pixel-identical SVG compatibility**.
The reviewed images retain established limitations described below. A future
threshold regression in any removed entry now fails the normal added-difference
gate. No candidate failure has been accepted as a new expected failure.

Evidence sources:

- Main `8842f828a47442dd0d3e583f85c57432e3c448e5`:
  [run 34473524292](https://github.com/wieslawsoltes/ProGPU/actions/runs/34473524292),
  artifact `10150706877`.
- Candidate head `cbbb2aedc807453a8c903d7495dec898cf679e09`, GitHub merge ref
  `1be62712eb4663b0d84d2ded41c8953f8443e0ef`:
  [run 34487437882](https://github.com/wieslawsoltes/ProGPU/actions/runs/34487437882),
  artifact `10156544488`. Reports 270 passes, 246 differences, nine exceptions,
  zero added differences and zero unexpected exceptions before maintenance.
- Svg.Skia reference commit `03f64b67badfca9fca216dc25896d0c0ee04e7b7`,
  `tests/Svg.Skia.UnitTests/ChromeReference/W3C`.
- Identical font inventory in both reports: 22 loaded faces,
  SHA-256 `7d2494f409eca08073006614ff6a8ccfb6cf7746e39102c432a6d8b945e6e2f3`.

The original PNGs were inspected without editing. Native Preview supplied a
visible background for transparent pixels; its window screenshots were compared
with the original reference PNGs. This matters because black ink is invisible
when a transparent candidate is displayed on black. Reviewed screenshot captures
are in the local temporary `codex-shot-2026-09-10_16-24-07.png` and
`16-27-58` through `16-32-43` series. Retained original artifacts, not screenshots,
remain the numerical test inputs.

## Per-fixture measured error

All rows improve relative to main and are within the existing threshold.
Values below are rounded for display; the retained JSON contains full precision.

| W3C fixture | Main error | Candidate error |
| --- | ---: | ---: |
| animate-elem-05-t | 0.105827621 | 0.099210317 |
| animate-elem-07-t | 0.106824451 | 0.099274518 |
| animate-elem-37-t | 0.100661069 | 0.098489432 |
| animate-elem-41-t | 0.105647588 | 0.094770471 |
| animate-elem-81-t | 0.102522761 | 0.091713999 |
| animate-struct-dom-01-b | 0.100000005 | 0.093829703 |
| coords-trans-05-t | 0.105097690 | 0.091497419 |
| coords-trans-06-t | 0.110866222 | 0.097582564 |
| coords-units-03-b | 0.102553329 | 0.092625554 |
| filters-light-02-f | 0.104327874 | 0.098848983 |
| filters-offset-01-b | 0.100208281 | 0.098267910 |
| linking-uri-01-b | 0.109201703 | 0.096504333 |
| linking-uri-02-b | 0.109859268 | 0.098133704 |
| linking-uri-03-t | 0.104358374 | 0.094787635 |
| pservers-grad-01-b | 0.106250499 | 0.099196626 |
| struct-dom-11-f | 0.102411184 | 0.097907863 |
| text-align-01-b | 0.105459267 | 0.099970003 |
| text-align-04-b | 0.104290467 | 0.094725466 |
| text-align-05-b | 0.102496591 | 0.095509040 |
| text-dom-02-f | 0.104745025 | 0.097848337 |
| text-text-09-t | 0.103436367 | 0.095297222 |

## Visual observations and remaining contracts

- Motion/shape animation fixtures retain the same static SVG.NET state as main:
  missing moving triangle, initial rather than animated rotations/properties,
  and retained inner transform outlines. Candidate text overdraw is reduced;
  this does not implement animation.
- Coordinate fixtures retain the red/blue axes, endpoints, CSS-unit rules and
  source placement. Text striping is present in both main and candidate, with
  reduced overdraw in the candidate. This remains a text-rendering limitation.
- Distant-light output retains an incorrect dark half-disc, but the previous
  additional vertical smear is reduced. Offset output still lacks the green
  offset discs; its cross markers and black disc remain. Neither filter family
  is declared complete by the threshold classification.
- Linking fixtures preserve the four colored shapes, bounds, central link block
  and outlined triangle. Link execution is not exercised by these static images.
  Linear-gradient strips remain continuous and aligned with their references.
- DOM fixtures retain the same unexecuted black/red indicator states as main
  instead of the reference script results. This is not DOM scripting support.
- Horizontal text anchors retain their positions and colors. Tspan/text-path and
  vertical writing remain visibly incomplete, including horizontal overlap where
  vertical layout is expected. Rotated text retains its layout but still shows
  horizontal streaks. Main shows the same limitations with greater overdraw.

These existing broad SVG.NET compatibility gaps remain deferred under the
native MIL application's finish-first scope. This review must not be used to
qualify WPF text, native MIL application startup, Direct2D/Win2D breadth, or SDK
packages. The next required step is fresh CI against the updated inventory.

