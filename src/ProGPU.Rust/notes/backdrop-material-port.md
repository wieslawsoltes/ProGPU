# Managed backdrop material port

Implemented `backdrop::{BackdropMaterialBrush, BackdropMaterialParameters,
BackdropMaterialDraw}` and original kind/source enums, Mica defaults, raw value
retention and finite sanitization at the 176-byte uniform boundary. The original
WGSL is embedded unchanged and included in the crate package. `Scene` retains the
actual command, source texture and source transform; compilation/preparation
preserves original QuadClipper geometry/UV work, active opacity and fixed blend.
GPU source upload is not fabricated: supplied textures retain their same-device
owner and producer lease through submission.

`HostBackdrop` is the original compositor's painter-position destination capture,
not an OS-provider callback. Only the renderer binds the actual private pre-draw
root as that source. Host commands share the ordered destination engine with
advanced blends/ROP3 and intervening Clear/AA/ordinary draws. The canonical
shader receives the completed destination, mixes uncovered coverage itself and
uses Src, while explicit texture materials retain their requested fixed blend.
The existing stable-root executor uses copy/material/copy (three actual passes)
per host operation rather than the original alternating-root optimization; these
passes and texture allocations are reported by AdvancedBlendMetrics, separately
from BackdropMetrics. No CPU pixel effect or readback is introduced.

The original fragment consumes a single mask. Ordered chains/axis-area masks use
the existing actual GPU coverage producer. `CompiledDrawState.local_coverage`
records only a completed one-to-one target producer, so backdrop and managed WGSL
localize that record without pretending that a resolved analytic record is a
materialized texture. Root-coordinate direct analytic/sampled masks on cropped
parents remain explicitly rejected until their original source localization route
is represented. Original recorded logical clip edges are retained separately for
QuadClipper; this does not change the original no-op vertex-mesh clamp.

The shared pipeline is immutable per draw and source frame, so later preparation
cannot overwrite an earlier command's uniform, source, mask or painter-position
binding. Exact source validation precedes preparation; unsupported source/device,
blend and frame contracts fail explicitly. Remaining qualification: original
pixels, source-host/package providers, failure/cancellation behavior and actual
cold/warm performance, including removal of the extra stable-root copy. No
compiler, test, browser, benchmark or fixture validation was run; source formatting
only. No build artifacts or trace files were generated.

| Original path | SHA-256 |
|---|---|
| `ProGPU.Vector/BackdropMaterialBrush.cs` | `de6731e8e9fcedc6fefe86c05d42cd6be655d5b0e5e1c677a553fbf1e672ca2a` |
| `ProGPU.Scene/BackdropMaterialParams.cs` | `67b82b4741477a5e9df263e6e602c3961e159a36b0eba2643e4436ec76f472ab` |
| `ProGPU.Scene/Extensions/BackdropMaterialExtensionPipeline.cs` | `390b80a12e7bd82e2fcca3cc41c7998c2ed79e18ebbac56d7bdaf5c6be6d510b` |
| `ProGPU.Scene/Extensions/QuadClipper.cs` | `da3612c8915b465229a6f9208a8187a6430b56bd9d856aee06209a9bf6c2f0d9` |
| `ProGPU.Scene/Shaders/BackdropMaterial.wgsl` | `871f0522d1b10eccaa20f030acc4df421c5ae9c98bd8b4e44b423acb37ac9913` |
| `ProGPU.Scene/Compositor.cs` | `a45a99f135806d248fc3792dcbdb5ebe17c094821c6aed080dcdedf4025237cd` |
