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
retained source command transport, source scope/clip integration and managed
renderer wiring remain required. Existing legacy `INITIALIZE_FOR_CLEARTYPE`
rejection must not be removed on the strength of this private renderer alone.
Independent original Windows captures and both-provider full-byte comparisons
are the final numeric authority. Authored source guards are **unexecuted**;
all build, shader, GPU, image, performance and package validation is deferred to
the final integrated stack tip. No compatibility or performance result is claimed.
