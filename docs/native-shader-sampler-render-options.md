# Shader sampler visual render options

A native MIL visual update with the BitmapScalingMode flag and value
`Unspecified` now preserves its inherited image sampling mode. A root with no
specified ancestor still uses the existing Linear default. Explicit Linear,
Nearest and Fant retain their original behavior. This is a two-condition source
state correction, not a shader filter change or an effect-state reordering.

The distinction matters when an already-published visual clears its own mode:
the original packet includes flagged Unspecified, but that value does not replace
the parent's mode with Linear. The original owned bitmap, ImageBrush revision,
capture picture, shader sampling mode, engine leases and submission lifetime are
unchanged. Unknown render-option bits and invalid values retain existing atomic
rejection.

## Independent source evidence

The original WPF source was inspected read-only at LibreWPF
`e75b3307d64a2d90d09c82c55a9151b5cf14e574`:

- `PresentationCore/System/Windows/Media/RenderOptions.cs` registers the attached
  BitmapScalingMode property without a changed callback.
- `PresentationCore/System/Windows/UIElement.cs` supplies metadata that propagates
  the attached value into `VisualBitmapScalingMode`; DrawingGroup separately
  supplies its own metadata.
- `PresentationCore/System/Windows/Media/Visual.cs` serializes the actual visual
  field, not the attached property. Bare DrawingVisual, ContainerVisual and Brush
  do not gain UIElement's propagation by having that attached value set.
- `WpfGfx/core/uce/drawingcontext.cpp` applies own visual render options before
  effects, ignores Unspecified during that application, and restores options
  after effects. `resources/ShaderEffect.cpp` realizes secondary samplers using
  the current render state. This does not establish an incoming-only rule.

These observable contracts informed an independent correction in ProGPU's
existing render-state compiler; no foreign implementation was copied.

Original Windows workflow `37010911430`, commit
`69c08f54a9f5a2f5f0926518b0c7c7aaa79d8e32`, retained all sixteen inputs and
three replays per input before its assertions failed. Its x64 failure receipt
SHA-256 is `17f013af6e4936e2f36bbc753635712a5bbc088e41d9cfcf8ed43bdc98d6a356`.
Both the own-attached-Nearest and parent-attached-Nearest cases yielded identical
linear ramps. The distinction above explains why those attached properties were
not equivalent to the native fixture's real Nearest MIL options; it is not a
reason to change native sampling defaults. That SoftwareOnly reference also
does not qualify original hardware filtering. The ARM64 all-black external
sampler result is not a positive comparison.

## Authored paired controls

The shared original-packet fixture now has thirteen cases, each retaining
cold/warm/independent-engine replay and strict RGBA, command, submission and
effect-retention checks in both provider suites:

- Original four Nearest source cases are unchanged.
- A root Unspecified case checks the independent pixel-center two-texel Linear
  ramp, with exact opacity and UNORM8 arithmetic.
- Two parent-Nearest/own-Unspecified cases exercise None and Tile addressing.
- Two same-owner pairs capture actual own Nearest, then reset it to Unspecified
  under the same Nearest parent. Their pixels must remain identical.
- Absolute and relative non-tiled viewboxes map the same full 400x200 image at
  independent 192/384 DPI. Stretch.None maps its 200x50 DIP content to
  `(-50,30,200,50)` inside the 100x100 viewport. Output contains fifty rows, not a
  cropped twenty-row viewbox. Native mapping already satisfied this contract;
  this adds coverage, not a bounds repair.

One live channel retains the same source owner and advances actual brush/bitmap
revisions across every case. All immutable scenes outlive that channel before
GPU replay. Original 64x64 targets remain unchanged; only the full-source pair
uses 128x128 targets through the same readback/timeout and BGRA-to-RGBA paths.
Raw MIL controls independently inspect the compiled capture's actual image
sampling and full-source mapping before device execution.

## Remaining qualification

These controls are authored, not locally GPU-executed. Hosted execution of both
providers and equivalent actual-visual-field original Windows controls is still
required. Repeated Linear bitmap sampling has a separate source-addressing
question: rasterizing a clamped enlarged page and repeating it is not generally
equivalent to filtering a repeated original bitmap. This change neither selects
that policy nor rewrites the original four pixel expectations. Full ShaderEffect,
source application and platform parity remain unqualified by this change.

## Post-commit checks

Implementation checkpoint `9f10c4c10865aea75649514d17801acc2587083a` passed
strict AppleClang C++20 `-Wall -Wextra -Wpedantic -Werror -fsyntax-only` for
the complete MIL compiler, the complete MIL test translation unit and an
instantiated thirteen-case shared GPU fixture. Each compiler process had an
explicit 45-second or shorter limit. The full native contract verifier passed:
143 command definitions, 141 packet layouts, unchanged 109/25/7 dispatch ledger,
93 owned memory fields, three inline-array generator controls, all generated
wire contracts and Unicode tables. Its first invocation stopped at an omitted
sparse-checkout Unicode source; after adding that unchanged tracked directory,
the complete verifier passed. `git diff --check` also passed.

Original reference workflow `37013264907` subsequently completed successfully at
`5e13e5863053f9e5836fda323fdd52be6fe8a70b`. The x64 job `110857864291` adds
actual protected-visual-field own Nearest, inherited Nearest and already-rendered
own-Nearest-to-Unspecified reset controls, retaining the original sixteen
inputs: 24 cases and 72 replays. ARM64 job `110857864140` remains explicitly an
unsupported-software negative control. This independently supports the corrected
inheritance contract; it is not execution of ProGPU's new fixture.

No native renderer link/build, GPU or VM execution, runtime staging or full
managed source build was performed locally. Provider pixel execution remains a
required hosted gate, not a result inferred from syntax or original references.
