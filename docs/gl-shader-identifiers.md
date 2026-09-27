# Shared shader identifiers on the GL backend

LibreWPF #186 reports Mesa rejecting the glyph compute shader with `PACKED_TOK`.
The shared WGSL used a local named `packed`, which the pinned native translation
path can emit unchanged into GLSL, where it is reserved.

The glyph, ordinary/staged path and signed-winding coverage shaders now name
their output word `coverageWord`. The vertical shadow reader uses
`packedCoverage`, and native texture hit-query readback uses `packedResult`.
These are identifier-only substitutions: coverage arithmetic, byte/lane order,
buffer/texture layouts, bindings, workgroups and entrypoints are unchanged.
Managed resources and native CMake embedding still consume the same canonical
files. There is no copied shader variant or upstream compiler patch.

Five source regressions fail on the original shaders and pass after the change;
four pattern controls also pass. Local logs are retained in
`artifacts/shader-identifiers/`. These tests detect the reported identifier
collision; they do not claim actual Mesa compilation, rendered pixels, native
readback or application qualification. Those runtime gates remain required.

This change does not add an environment-variable backend contract or alter
backend/adapter/compiler defaults. In particular, no renderer fallback is used
to hide a shader failure. Full exact-head CI and the final explicit Linux GL
application run remain separate requirements before release qualification.
