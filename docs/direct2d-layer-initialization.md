# Direct2D layer initialization

The portable optional `scene_layer_options_native::PushLayer1` preserves the
Windows `D2D1_LAYER_OPTIONS1` identity without changing ID2D1RenderTarget's vtable.
Legacy `layer_options::initialize_for_cleartype` is not an alias for background
initialization and still rejects while real RGB coverage is missing.

`INITIALIZE_FROM_BACKGROUND` uses an owned transient GPU texture copied from the
actual current parent before any child draw. Final composition keeps geometric
mask/opacity coverage separate from child alpha: it interpolates the untouched
parent and completed child RGBA. Ordinary SRC_OVER of a copied translucent parent
would composite that parent twice. The existing semantic BACKDROP/effect contract
is unchanged. New initialization excludes effects, caches, mapped-picture frames
and composite-state relocation until those separate ownership contracts connect.

The common Texture/AdvancedBlend shaders implement the coverage resolve and
destination-aware replacement for both native providers. No CPU pixel evaluation,
readback or extra submission supplies layer contents. Pipeline caches belong to
the engine and retire with it; immutable replay spans retain initialization.

Authored controls cover eight opaque/translucent parent, full/half opacity and
original aliased geometric-mask combinations, every pixel cold/warm on both
providers, actual Microsoft device-context pixels, original command-list
translation, and atomic invalid-option/flag rejection. Legacy ClearType rejection
is retained. These controls are **not executed yet**: validation is deferred to
the final integrated stack tip.

Remaining implementation: IGNORE_ALPHA intermediate storage and nested replacement
alpha policy; Clear inside materialized source layers; real ClearType RGB glyph
coverage and corresponding original Windows controls. None is silently admitted
by the background flag or inferred from scalar glyph outlines.

Original contracts: [OPTIONS1](https://learn.microsoft.com/en-us/windows/win32/api/d2d1_1/ne-d2d1_1-d2d1_layer_options1),
[legacy OPTIONS](https://learn.microsoft.com/en-us/windows/win32/api/d2d1/ne-d2d1-d2d1_layer_options),
[layers overview](https://learn.microsoft.com/en-us/windows/win32/direct2d/direct2d-layers-overview).
