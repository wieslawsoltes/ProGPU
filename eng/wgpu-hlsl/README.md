# Pinned HLSL workgroup initialization

`naga-workgroup-zero-init.patch` is an authored change to the exact external
Naga 0.19.2 revision recorded in `build-inputs.json`. Apply it only in an isolated
source build after verifying the original file, patch and resulting file hashes.
Do not modify installed dependencies, the NuGet cache or retained source evidence.

The pinned writer initialized each used workgroup array with one whole-array cast
assignment on invocation zero. For the real binary glyph shader, FXC with its
original `cs_5_1` / `ENABLE_STRICTNESS` settings exhausted the original 60-second
compiler process limit. An equivalent distributed initializer compiled in
1.845 seconds on the same compiler version and binary hash. Removing either
curve or continuation logic did not resolve that limit. These observations are
compiler attribution, not rendering or package qualification.

The patch initializes each fixed top-level workgroup array element with its
original typed default initializer. The linear invocation index is
`x + width * (y + height * z)` and the stride is the complete XYZ workgroup size.
Each element belongs to exactly one residue class. The checked nonzero extent
and exceptional final-increment guard prevent arithmetic wrap. Names are
allocated by Naga's existing name allocator. Nested element types retain their
original typed initializer; arrays inside a non-array structure keep the old
initialization policy.

Non-array globals still initialize on invocation zero. All initialization stays
at the original entrypoint location before the original single workgroup barrier,
before original local initialization, reads or early returns. Zero initialization
remains enabled, release optimization flags are unchanged, and no resource,
entrypoint, binding or WebGPU ABI changes are introduced.

`verify-generation.py` exercises an actual patched translator and the original
translator. It checks XYZ and uneven-length coverage, scalar/vector/atomic/struct/
nested-array element zero, name collisions, barrier placement, and byte-identical
scalar-only output. Run it with `--translator`, `--original-translator` and
`--output` paths. Translation must use the recorded pinned Naga implementation;
these controls do not create a graphics device.

The actual patched writer with the scalar-root binary glyph shader compiled both
original entrypoints under unchanged release settings in diagnostic run
40678661219: horizontal 4.7644855 seconds, vertical 2.1349807 seconds, both
HRESULT 0. The original 60-second process bounds remained intact. Both runners
used compiler SHA256
`1b37214a4b23f76a88cfedace6236bd2f6f46ae11b71a0f8e5d5332dda79f2b3`.
FXC warnings and raw bytecode remain retained; no warning suppression was added.

Both Windows architectures, both native providers where applicable, exact
packages and original application/pixel acceptance remain independent gates.
