# Original ShaderEffect padding in integral captures

The MIL ShaderEffect resource retains all four original local padding doubles in
packet order (top, bottom, left, right). The packet/scene ABI and shader-resource
versions are unchanged. Finite nonnegative values must also narrow to finite
floats; validation precedes resource publication. The additive managed
`NativeMilShaderPadding` overload writes those exact doubles, while the existing
overload emits exactly its original zero-padding bytes.

For nonzero padding, source bounds become float local edges, then each edge is
inflated by its corresponding float padding before the existing positive-axis
transform. The retained doubles are not overwritten by this projection. Zero
padding calls the original double-transform path unchanged. Overflow/collapse
fails; there is no epsilon, ceil-to-fit repair or origin adjustment.

The expanded rectangle flows through the existing isolated layer and shader
binding. Implicit source geometry stays where it was, leaving transparent padded
pixels. A shader can produce ink in that border. An owned ImageBrush sampler
still realizes over the complete zero-origin physical input extent, now expanded.
UV normalization and derivative registers use that same complete capture rather
than unpadded content or spare allocation. Original final clipping, inner opacity
and source resource revisions/retirement remain authoritative.

This does **not** admit general fractional captures: the existing render preflight
still requires exact integral physical origin/extents and a complete uncropped
input. A fractional padding value can succeed only when original float inflation
and the admitted mapping yield that exact existing frame. Fractional physical
edges, out-of-target captures, rotated/reflected mappings, backdrop/cache-content
captures and custom effect input mapping remain rejected. No source hit-test
identity, renderer fallback or capability advertisement is added.

## Independent source contract

Original WPF source was inspected through immutable LibreWPF `381194e` objects:

- `WpfGfx/core/resources/ShaderEffect.cpp:172–179` narrows each padding double
  and inflates local float edges.
- `WpfGfx/core/uce/drawingcontext.cpp:4912–4923` obtains the original inner
  bounds and inflates them before effect isolation.
- `WpfGfx/core/resources/Effect.cpp:37–50` retains the whole effect input,
  independent of the final clip.
- `drawingcontext.cpp:3695–3733` separates scale from the rest of the transform;
  `:3193` outward-integralizes allocation. The new bounded native admission does
  not attempt to generalize that fractional allocation mapping.

No foreign implementation is copied. The original public source exporter already
retains all four values; its actual-source test sets `PaddingTop=2`. The frozen
WPF source compiler still rejects nonzero padding pending a separately qualified
producer and paired source change. This native checkpoint does not select that
source route.

## Authored controls and dependency gates

The shared fixture builds twelve original MIL scenes from one real source
channel, verifies effect-generation advancement, then destroys that channel
before either provider replays its immutable scenes. Cases retain zero-padding
baseline/reset, asymmetric borders, source input, constant ink, UV output,
derivative output at DPI 1/2, full-frame ImageBrush sampling, changed origin at
equal extent, original-double-to-float padding projection, and two still-rejected
fractional/cropped frames. All 64x64 RGBA bytes, cold/warm/independent images,
actual submission/command counts, effect-cache counts and uniform bytes are
checked. No original sampler or derivative control is removed.

Raw MIL checks assert local bounds, unchanged final clip, complete physical
sampler extent and whole-batch rollback for invalid later padding on every axis.
Managed transport checks assert original double bits, byte-identical zero-padding
overloads, invalid axes and late-invalid constants without partial append.
Independent original Windows reference authoring follows this major implementation
checkpoint in a separate reference-only branch; no Windows pixel result is yet
claimed.

This branch is a source-only union of PR271 `225fb995c2b7e879e2bb85f1328c7ed1fbae5820`
and PR267 `7085fdbae78d6e8cb682c746b082e0baf5a409cb`. Neither dependency is qualified
by this merge. No native/GPU/full-graph build, runtime staging or VM is part of this
checkpoint. Actual native/Windows/provider/package execution and the complete
source dependency union remain required before WPF integration or parity claims.
