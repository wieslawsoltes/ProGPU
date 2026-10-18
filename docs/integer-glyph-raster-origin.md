# Explicit integer glyph raster origins

`TextHintingMode.IntegerPixel` separates an explicit raster-origin choice from
the retained logical glyph positions. It is intended for source adapters whose
original observations require whole physical pixel origins. It does not execute
TrueType instructions, change advances or claim Microsoft text raster parity.
`Auto`, `Fixed` and `Animated` retain their existing placement policies.

## Contract

The command contains positioned ordinary outline glyphs, `Aliased` rendering and
`PreferGlyphAtlas`. Its complete render frame must be finite, positive diagonal
2D, with no rotation, shear, perspective, synthetic bold/italic or font transform.
CFF, color/bitmap fonts, logical-resolution atlases, explicit vector commands and
the specialized vector-only retained glyph builder are not admitted by this
policy. Ordinary retained command replay remains supported. Raw `DrawText`
cannot select the mode; the adapter must retain its actual prepared glyphs.

At compilation, transform each original position plus command offset, multiply
by the actual physical raster scale, round each axis to its nearest integer with
half ties toward positive infinity, and divide by that same scale for projection.
The managed raster scale includes the existing static compilation zoom. The
native picture compiler uses its explicit target DPI. Atlas X phase is zero.
The rounding helper compares the original fractional part; adding `0.5` first
would incorrectly change sufficiently large, already integral floats.

Recording checks the descriptor and borrows the original arrays in O(1) time and
space. Compilation validates the whole selected range before atlas/style/list
mutation in O(G) time and O(1) extra space for G selected glyphs. Invalid physical
coordinates cannot partly publish a native scene. The original arrays, shaping,
range, font ownership and default font policies remain unchanged.

Atlas exhaustion preserves existing UVs and generations. Its ordinary vector
fallback uses the already rounded origin and original basis, with an origin-relative
outline; it does not revert to the original fractional origin or inverse-transform
and recompose that origin. This preserves placement, not equivalence between the
atlas and path coverage algorithms. Existing coverage, dropout and contrast work
remains separate.

Snapshots and retained text records keep the explicit mode. Picture archive v9
also stores the original `PreferGlyphAtlas` and `UseLogicalGlyphAtlasResolution`
flags. Earlier archive writers reject this new mode, and v1–v8 readers cannot
admit it under an old header. Existing native positioned-glyph transport already
carries the resolved origin and phase; the MIL/WPF hinting enum is unchanged.
Consumers need the matching rebuilt producer before selecting the new API.

## Original observations and remaining qualification

### Prepared physical outlines

`DrawHintedGlyphsWithRasterOrigin` and its explicit-range counterpart carry
`IntegerPixel` through the original retained command. Existing overloads keep
`Auto` and their unsnapped placement. Prepared outlines retain their exact DPI,
identity basis, generation, original physical phase and source ink/brush domain;
the new option admits only aliased coverage. It does not rerun font instructions
or change the existing coverage/dropout algorithm.

Both managed atlas replay and native picture compilation reuse the original
`IntegerGlyphRasterOrigin.TryResolve` implementation in this repository. The
complete occurrence position, draw origin and effective translation are composed
before rounding. Full selected-range preflight includes no-ink occurrences and
rejects physical overflow before resource/style/atlas publication. Private raster
bounds use that same resolved target origin, including nested picture translation;
they never subtract a translation and add it back after rounding. Source hit and
brush bounds remain unrounded. An unavailable atlas retains its existing failure
policy rather than silently changing this prepared-outline contract.

Recording and compilation add O(G) work with O(1) temporary storage for G selected
occurrences to the existing whole-range validation. Placement uses `Vector2`
arithmetic and the existing independent-axis rounding helper. No font callbacks,
GPU readback, coverage reupload, per-glyph submission or new cache namespace is
introduced. Physical outline generation remains the raster-cache identity.
Snapshots retain the existing command field. Hinted-geometry archive support is
unchanged; this does not claim portable serialization of its resource owner.

The native compiler emits the already-supported resolved `NativePositionedGlyph`
position with the unchanged unit basis and original physical outlines. Both native
providers therefore consume the same existing ABI and shader. No C++ or WGSL
policy fork is needed. Managed full-frame cold/warm controls and independent native
wire/bounds controls cover the new placement. Final producer/provider, package,
original grid-fit phase and application qualification remain required before
source release admission; integer-origin support is not Microsoft raster parity.

Local validation of this extension: 121 focused controls pass, including six
cold/warm full-frame GPU comparisons at 1, 1.5 and 2 DPI. The complete renderer
suite passes 6,352 tests with 12 platform skips; all 230 headless tests pass.
The 19 added tests pass. Every outcome in the retained earlier renderer TRX is
unchanged; the additional Linux Close control already exists in the parent and
is skipped on macOS. The prepared Drawing consumer and its test sources compile
with the explicit producer override (zero errors); they have not executed against
an unqualified native payload. The initial focused compile caught an ambiguous
test-only color constructor, corrected before any test executed.

The design sources below were rechecked for this extension. Skia and HarfBuzz
support retaining the original prepared layout; DirectWrite and Win2D keep
raster-origin, contrast and grid-fit choices independent. WebRender informs exact
generation/device identity. Vello/Parley reinforce leaving reusable layout on the
CPU while existing GPU coverage stays batched. Startup/lazy initialization,
visibility, eviction, demand uploads, worker preparation, variable/fallback font
selection and device-loss invalidation keep their existing ownership and policies.
No performance improvement is claimed and no external implementation was copied.

The original Windows raster-phase run
[39123876508](https://github.com/wieslawsoltes/LibreWinForms/actions/runs/39123876508)
retains 328 original PNG frames: 324 explicit-position cases and four whole-string
controls. All four controls match their original baseline exactly. For Microsoft
Sans Serif at 13 pixels and Segoe UI at 12 pixels, positive 96-DPI SingleBitPerPixel
positions below phase 0.5 share a raster; phases at or above 0.5 move one pixel.
Both axes were independently varied. AntiAlias produces a different phase
inventory and is not admitted by this change.

The independent original placement run
[39122980315](https://github.com/wieslawsoltes/LibreWinForms/actions/runs/39122980315)
reproduces all four unhinted whole-string frames from original advances and
baselines. This supports preserving logical placement while repairing raster
placement. It does not justify substituting measured character boxes for pens.
Negative coordinates and other DPIs have mathematical contract tests here, not
new original Windows qualification.

The full local renderer suite passes 6,318 cases with the same 10 established
platform skips (6,328 total). The initial worktree-only run retained five
missing-framework-source failures; preparing the exact manifest revisions and
rebuilding fixes those setup failures without changing any test.

Local tests cover complete cold/warm managed GPU frames at 1, 1.5 and 2 DPI,
independent expected native positions, signed ties and large integral floats,
whole-range failure, tiny nonzero shear rejection, retained/archive round trips,
and actual atlas exhaustion with visible ink from every glyph. These are producer
controls. Matching rebuilt packages, both native providers, original full-frame
source comparisons, browser AOT and application acceptance remain required.
LibreWinForms #174 stays open: grid fitting, dropout, antialias contrast and
ClearType are not solved by integer origins.

## Design research

Primary sources consulted before implementation:

- [Skia text model](https://skia.org/docs/dev/design/text_overview/): adapt the
  separation between reusable positioned glyphs and device rendering; do not
  shape or replace source advances during paint.
- [DirectWrite rendering parameters](https://learn.microsoft.com/en-us/windows/win32/api/dwrite/nn-dwrite-idwriterenderingparams)
  and [Win2D text parameters](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextRenderingParameters.htm):
  preserve independent rendering, contrast and grid-fit policies. An origin
  policy must not silently change any of them.
- [WebRender font instance options](https://doc.servo.org/webrender_api/font/struct.FontInstanceOptions.html):
  keep raster policy explicit at the rendering boundary. Reuse existing exact
  font/glyph/physical-size/phase cache identity when its content is identical;
  phase zero does not need a duplicate atlas merely because the caller chose a
  different logical placement policy.
- [Vello architecture](https://github.com/linebender/vello/blob/main/ARCHITECTURE.md)
  and [Parley concepts](https://github.com/linebender/parley/blob/main/doc/concept.md):
  retain the separation of CPU preparation/layout and GPU rendering. Vello's
  sparse-strip CPU/GPU routes do not justify replacing ProGPU's owned rasterizer
  or introducing a CPU bitmap fallback for this correction.
- [HarfBuzz responsibilities](https://harfbuzz.github.io/what-does-harfbuzz-do.html):
  retain reusable shaping output and the actual selected font instance. This
  correction adds neither fallback-font discovery nor variation resolution.

Lazy initialization, visibility culling, demand uploads, worker preparation,
batching, atlas eviction and device-loss invalidation retain their existing
implementation. The extra selected-range preflight is linear with constant
storage; it creates no per-glyph objects. There is no performance improvement
claim. Cold-start/first-interaction timing, sustained scroll percentiles,
allocation/cache residency and original image quality must still be assessed
with the final integrated application binaries before integration qualification.
