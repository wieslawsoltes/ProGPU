# Uncached visual opacity-mask input

The external LibreWPF SDK application, with NativeMilWgpu and native input
enabled, rejected its first frame at an unannotated isolation layer (command
6130, mask resource 3625). The layer begins before source owner 3695 and includes
other owned descendants. Skipping an unowned layer would therefore lose real
input and is not a valid fix.

The uncached visual composite now publishes the existing `source_opacity_mask`
policy when its source visual has a spatial opacity mask. Its rendered mask and
opacity are unchanged. Own content and descendant geometry retain their source
owners and actual clip state; no mask bounds or sampled alpha become input.
The already separate cache/effect composite paths are not newly admitted.

This reuses ProGPU's original typed builder policy, introduced for drawing
opacity masks, in the shared MIL producer. It adds no shader, CPU geometry pass,
foreign implementation, or provider-specific path. Both native providers use it.

Native MIL regression scene 9842 covers a fully transparent gradient mask over
own content and a child, an actual rectangular clip, an unclipped sibling, and
mask removal on the next generation. All three source owners retain their correct
geometry in both generations; the raster mask exists only in the masked generation.
The native MIL test executable passes. Both provider libraries compile on macOS
ARM64. The coverage ledger was regenerated (decoder digest only), and the full
native contract verifier passes, including generated interfaces and Unicode data.

The diagnostic external application now completes native scene/index compilation
and enters rendering. Rendering next rejects static multi-guideline deformation
for a draw family not yet supported by that executor. That is the next concrete
application blocker. Locally replaced diagnostic binaries are not exact-package
qualification; final application, platform, image/input and PR CI gates remain.

The next renderer rejection is localized to command 222, kind 23
(`DRAW_STROKE_BATCH`), resource 132, inheriting its saved state. The semantic
preflight currently admits per-point guidelines only for `DRAW_PATH`; stroke
capacity and compilation otherwise share the existing stroke lowerer. The next
implementation must preserve real stroke caps/joins, width, brush mapping,
target localization/DPI, and unsnapped source input, not merely remove this
preflight guard or snap a control hull. The failure-only renderer probe was
removed after recording this evidence.
