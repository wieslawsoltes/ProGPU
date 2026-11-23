# Original CustomGrid extension

ProGPU source SHA-256:

- `src/ProGPU.Scene/Extensions/CustomGridExtensionPipeline.cs`: `ee81b6c47ac008a1861b7fc0fb1bda8620bf5129ba82f1eea406d5672828bb82`
- `src/ProGPU.Scene/Shaders/CustomGrid.wgsl`: `ad702ddd23673dd5b96087737e70a03fc8e59cd1c722c788256d24bea003551c`
- `src/ProGPU.Backend/RenderPipelineCache.cs`: `c03d6d1648fbeb643150b660b685052e7bc6b2d944032b0a386a939482cc0ea4`

`Scene::draw_custom_grid` retains an actual original extension7 command. Its four
NDC vertices, white color, UV order, dummy material zero, shape7 and six indices
are appended to the shared original 56-byte vector stream. Source transform and
opacity are not applied: the original producer and binding-free shader do not
consume them. A separate ordered draw range selects unchanged `CustomGrid.wgsl`
with fixed straight SrcOver and the actual render-pass scissor. Ordinary logical
and device dot-grid commands retain their independent vector routes.

The canonical shader retains original major/minor grid, derivative coverage,
intersection glow and center glow arithmetic. No host uniforms, alpha masks,
generic blend mode or camera substitution is fabricated. The pipeline is lazy
per actual Renderer target format; prepared draws retain both pipeline and
original command owners. Shared source capture preserves its actual target's
fullscreen semantics. Archive capture rejects unresolved side-buffer references
instead of inventing their missing owners. Late-camera metadata remains gated
for this producer because it changes shared target state beyond its own shader.

The shared series sample now records this original extension as its background.
No build, test, browser execution, shader validation or benchmark has run.
The current single-sample target matches the original offscreen route; primary
MSAA, original pixels, native/browser lifecycle and full package/source gates
remain independent. Architecture ownership follows the primary references in
`DESIGN.md`; all executable code is translated from the original ProGPU sources.
