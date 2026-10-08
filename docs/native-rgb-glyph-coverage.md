# Owned RGB glyph coverage

This is an internal rendering checkpoint, **not ordinary Direct2D ClearType
admission**. The source descriptor retains the actual caller's rendering
parameters; absent parameters do not become guessed monitor or OS settings.

`encode_linear_rgb_glyphs` consumes an original outline batch and explicitly
selected `full_pixel_box_8x8` coverage model. Its three channel integrals reuse
ProGPU's original `GlyphRasterizer.wgsl` half-open winding walker and 8×8 sample
arithmetic, with horizontal centers displaced by −1/3, 0 and +1/3 physical pixel.
BGR reverses the outside channels. Flat geometry evaluates one integral. No
already-quantized grayscale image supplies the other channels. The historical
[Microsoft SID 2000 paper, sections 3 and 5](https://www.microsoft.com/en-us/research/wp-content/uploads/2016/02/sid2000.pdf)
motivates this explicit box model; it does **not** specify modern DirectWrite
mode-dependent grid fitting, filtering or ClearType-level behavior.

The compute and compatible fragment entries execute the same canonical function.
The first lane admits only gamma 1, contrast 0, ClearType level 1 and normalized
straight foreground RGBA, on an original proven opaque single-sample UNORM target.
It rejects forced CPU preferences, sRGB targets and unimplemented parameter
policies. Three instanced draws write R, G and B separately, blending each
channel's own coverage times foreground opacity against that destination channel.
They preserve source order independently per channel and never modify target
alpha. No max-channel alpha, destination copy, per-glyph submission, CPU pixels,
readback or nonlinear correction substitute is used.

Both native providers compile the same execution translation unit and shader
resources. Original managed scalar text remains unchanged; lazy shared managed
shader accessors expose the identical canonical programs but do not register an
RGB path in the managed compositor. Its actual retained command/resource adapter
and matched GPU fixtures are still required before that consumer is admitted.

Pipeline objects are lazy and engine-owned. Batch buffers and RGBA coverage
textures enter the existing real-submission raster-resource retirement lease and
GPU memory inventory. Trace events identify the owned RGB compute pipeline but
do not report completion. All input validation and buffer/atlas bounds precede
encoding. Failure after encoding requires the caller to discard its current
semantic encoder under the existing frame failure contract. This private call
borrows only the engine's current target under its lock: it is not a foreign
texture-view ABI, and a source adapter must preserve actual clips, transform,
opacity and target ownership before calling it.

For G glyphs, S supplied segments and P raster pixels, bounded CPU packing is
O(G + S) time and O(G) scratch; GPU winding work is O(P·S) with three fixed 8×8
integrals and fixed private sample lanes. The current private batch admits at
most 65,536 tiles, 1,048,576 segments and a 4096² atlas; a failed bounded shelf is
an error, not a fallback. Buffer bindings stay inside the original device limit
and portable storage-binding limit. Raster work is batched in one compute or
fragment pass followed by one three-draw composition pass. Retained atlas reuse
and final cold/warm performance qualification remain follow-on work.

## Research and remaining source contract

The design preserves the existing ProGPU ownership separation rather than
importing an external implementation:

- [Win2D retained text layouts](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Text_CanvasTextLayout.htm),
  [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  and [Parley's stack](https://github.com/linebender/parley/blob/main/README.md)
  distinguish original glyph/layout/font identity from raster work. This path
  does not reshape text, redo fallback or drop variation/face identity.
- [Skia's canvas overview](https://skia.org/docs/user/api/skcanvas_overview/)
  motivates keeping captured state separate from immutable draw inputs;
  [WebRender](https://github.com/servo/webrender/blob/main/README.md) and
  [Vello](https://github.com/linebender/vello/blob/main/README.md) describe GPU
  rendering architectures, not a license to infer Windows text pixels.
- Original ProGPU scalar shaders and native resource leases are the implementation
  provenance. No third-party helper, coefficient table, shader source or code
  structure was copied, ported or transcribed. Microsoft renderer implementation
  research is not a normative formula for all Direct2D versions or parameters.

General rendering-parameter correction, actual modern hint/filter policy,
original Direct2D source producer integration and managed renderer wiring remain
required. Existing legacy `INITIALIZE_FOR_CLEARTYPE`
rejection must not be removed on the strength of this private renderer alone.
Independent original Windows captures and both-provider full-byte comparisons
are the final numeric authority. Authored source guards are **unexecuted**;
all build, shader, GPU, image, performance and package validation is deferred to
the final integrated stack tip. No compatibility or performance result is claimed.

## Retained physical command transport

`DRAW_RGB_GLYPH_RUN` is a separate required command over an existing original
outline/segment resource. It carries a 40-byte explicit policy and one 56-byte
physical tile per source occurrence, including repeated indices, original sample
origin/scale/phase, target position and straight foreground. The descriptor's
filter identifier is not a DirectWrite enum. It retains the positive original
DPI generation; it cannot declare its destination opaque or silently reproject
physical tiles at another DPI. No ordinary glyph record/layout changes.

Native and managed builders preflight bounded counts, indices, actual sample
arithmetic, normalized colors, known policy and reserved fields before copying
the packet. The managed writer rejects overlap with its output arena. Native
wire validation checks owned outline/segment ranges and every occurrence,
without retaining caller pointers. The glyph-family identity includes exact
policy/position/paint bytes, active scopes and original resource ownership.

The shared native replay now retains an owned packet and emits the private RGB
pass between surrounding source-order bundles. Preflight requires the actual
innermost materialized target's `IGNORE_ALPHA` contract: neither a white root
clear nor an opaque ancestor proves the current target opaque. Replay rechecks
the live target slot, dimensions and opaque state before encoding. Target-local
scissors preserve source rectangle clips; scope opacity multiplies each original
foreground alpha. Existing isolated-layer masks/opacity are still applied once
by their ordinary composite, not silently dropped from the source stack.

The original DPI must equal both current presentation axes. Unit source bases
may translate by exact integral physical pixels; viewport and actual layer
origin are accounted for without a divide/multiply round trip. Sampling origin,
scale and phase stay unchanged. Nonunit/fractional mappings,
unproven root opacity, sRGB targets and CPU preferences fail before publication.
Fully clipped draws retain validation but publish no RGB operation. Bounded
packing accounts for the actual shelf rectangle and aligned staging, not ink
area alone. No test export or borrowed opaque-engine reinterpretation is added.

Integrated stock Metal execution exposed an omitted `IGNORE_ALPHA` flag in
the mapped transient-layer gate. Mapped SRC_OVER targets now preserve that
existing opaque-layer contract without admitting cache, backdrop, effect or
layer-mask metadata. All 24 scene cases pass both GPU routes with cold/warm exact
pixels or atomic rejection, including the nonzero viewport. The per-point
guideline control separately checks direct builder rejection and valid inherited
SAVE-state runtime rejection; failed direct draws publish no command. Original
Windows, the second provider and complete package qualification remain required.

CPU originals survive bundle reuse; GPU batches still use the original real
submission retirement lease. Cold and warm replay both report their actual
buffer uploads and compute/fragment draw counts. This is not retained coverage
or performance qualification. Independent full-pixel controls and original
source producer integration remain required before ordinary ClearType admission.
The native and managed ownership/atomicity/layout controls are authored only;
no generation verifier, build, test, GPU or CI execution has run for this change.

## Original per-draw masks

RGB replay now uses the same retained source mask builders and exact
`TextMaskCommon.wgsl` functions as ordinary text. Rounded masks, analytic chains,
coverage bitmaps, vector clips, brush/geometry masks, picture masks and composites
keep their existing physical coordinate transforms, opacity, sample policy and
source ownership. The RGB frame carries a zero physical render origin because
its tile positions and mask bindings are already local to the actual target.
There is no second DPI transform, envelope approximation or shifted grayscale.

Each channel's original coverage times foreground/scope opacity is multiplied
once by the primary mask and, when present, the existing analytic continuation
chain. Masking never changes target alpha or discards source draw occurrences.
Sampled and chain pipelines remain separate, lazy engine-owned families; ordinary
unmasked draws do not allocate these layouts or bindings. Native and managed
shader assembly prepend the identical shared mask source, without changing its
ordinary text bindings or arithmetic.

The replay span owns the original mask buffers/textures and picture lease through
bundle replacement. Failed preparation releases unpublished mask resources, and
encoder failure still follows the existing submission retirement contract. Mask
allocations remain in the original aggregate budget and native memory inventory.
The independent provider controls are being authored in a stacked child. All
GPU, original-reference, package and application evidence remains pending at the
final integrated tip; this is not automatic DirectWrite policy admission.

## Authored original Windows receipts

The existing actual Direct2D glyph test now authors 54 independent observations:
nine explicit parameter policies, three foreground/opacity inputs and two source
phases. Policies cover linear RGB/BGR/flat geometry, gamma 1.8/2.2, contrast 0.5,
ClearType levels 0/0.5/1 and Natural versus NaturalSymmetric. The original retained
font face and glyph run draw on an opaque 32×24 target at exactly 96 DPI.
Integrated execution disproved the earlier unexecuted assumption that direct
ClearType and `DrawImage` command-list replay produce identical pixels. An
independent original command-stream observer retained text antialias mode 1 and
the exact caller parameters, but all 54 replay frames matched separately drawn
original grayscale controls. Restoring caller state and recording the opaque
background inside the list did not restore direct ClearType output.

The source receipt therefore preserves direct ClearType, original command-list
and independently drawn grayscale frames separately. Every command-list frame
must match the complete grayscale control bytes and its own cold/warm replay.
These comparisons use only original Microsoft rendering; no ProGPU numeric
oracle supplies any pixels. Direct ClearType equality is recorded as observed,
and `directClearTypeCommandListParityQualified` remains false. This bounded
source observation does not admit original ClearType command-list replay in
ProGPU. Nonlinear policies remain unadmitted in the private encoder.

Each invocation creates a fresh `direct2d-rgb-reference-PID-SEQUENCE` directory
in the test working directory; every file uses `CREATE_NEW`. It records the
original font-file bytes, collection face/type/simulation identity, loaded
DirectWrite/Direct2D module paths and SHA256 hashes, every exact binary32 input,
full tightly packed BGRA outputs and their SHA256 hashes. Schema 2
`reference.json` is written last with the complete 54-case observation inventory
and explicit unqualified ClearType replay status. A partial directory without
this completed receipt is not evidence. Final hosted execution must retain these
directories alongside its original process/build provenance. These local
observations do not qualify a whole producer Build or cover every rendering
mode, font, transform, clip or original module version.

## Integrated Windows compiler checks

The pinned stock Windows backend crashed inside `D3DCompiler_47` while compiling
the unnamed RGB programs. Explicit shader and pipeline names remove that fault
without changing WGSL, input records, bindings, blending or pixel expectations.
The original 24 retained-scene cases and 40 mask/phase cases then pass their
compute/fragment cold/warm checks on Windows ARM64 D3D12 WARP. The complete stock
Metal GPU corpus also passes with the same change.

`progpu_native_direct2d_webgpu_tests --rgb-glyph-software` runs those same complete
RGB families on a software adapter; `--rgb-glyph-only` uses the ordinary adapter
selection. Both call the same helper as the unchanged default full corpus.
These diagnostic entries shorten crash reproduction; they do not replace full
provider, package or original-source qualification. The separate original
Microsoft observations above explicitly retain the direct-versus-command-list
ClearType difference; passing the GPU box model cannot remove that restriction.

The stock fixture also reports an independent raw R8 normalization/blending
probe after a mask assertion fails. `--unorm-mask-precision` runs this observation
directly. All 256 authored byte values are sampled at exact texel centers and
loaded into an RGBA32Float attachment; separate UNORM passes observe filtered,
loaded and arithmetic coverage under the original foreground/scope alpha.
Selected values are logged with a scalar comparison. No product mask texture,
shader or pixel contributes to this probe, and no assertion is waived from its
observations. This distinguishes texture precision from blend precision when
investigating a provider difference; it does not qualify a replacement sampler.
