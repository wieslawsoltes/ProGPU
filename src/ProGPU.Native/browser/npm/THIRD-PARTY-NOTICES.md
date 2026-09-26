# Runtime notices

ProGPU's original renderer and JavaScript adapter are MIT licensed; see LICENSE.
The generated JavaScript and WebAssembly also contain the Emscripten runtime,
C/C++ standard-library runtime, and Emdawnwebgpu browser bindings.

The package build copies original notices from the actual pinned Emscripten
4.0.18 toolchain and its hash-verified Emdawnwebgpu port into `licenses/`.
`build-info.json` records their file hashes and the exact ProGPU source commit.
Notices are retained in full; some toolchain notices cover optional runtime
components which this package does not use.

No font files, third-party renderer implementations, Chromium, Playwright,
native operating-system libraries, .NET runtime, or NuGet packages are included.

Toolchain references:

- https://github.com/emscripten-core/emscripten/tree/4.0.18
- https://github.com/emscripten-core/emscripten/blob/4.0.18/tools/ports/emdawnwebgpu.py

The existing pinned Emdawnwebgpu port supplies browser WebGPU ABI bindings.
It is a declared build dependency, not source copied into ProGPU implementation.
