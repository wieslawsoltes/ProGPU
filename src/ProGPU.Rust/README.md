# ProGPU Rust

Native Rust port in progress. This is not yet a parity-qualified release.
The Cargo manifest is at the repository root so packaged crates can embed the
same canonical shader files as C# and C++ without copied shader forks.

Run `cargo test --locked` and `cargo clippy --all-targets -- -D warnings`.
The CPU geometry API also builds with `--no-default-features`.

## Port ledger

| Domain | Implementation | Remaining qualification |
| --- | --- | --- |
| Shader composition | Compile-time canonical vector, path, glyph, binary glyph, text, image modules | Device pipeline and pixel tests for each host path |
| Analytic geometry | Native vertex layout, rectangle/ellipse/rounded rectangle expansion | Matched native differential fixtures |
| wgpu resources and rendering | Pending | Lifetime, replay, device-loss and pixel controls |
| winit sample | Pending | Resize, DPI, suspend/resume and present controls |
| Paths, strokes and Boolean geometry | Pending | Complete native algorithms and GPU dispatch |
| Fonts, Unicode, shaping, layout and text | Pending | Complete owned text stack and original fixtures |
| Images, brushes, clips, layers and effects | Pending | Complete retained resource and compositing contracts |
| Scene compiler, interaction and archives | Pending | Generation, replay and transport compatibility |
| Performance and distribution | Pending | Matched Release CPU/GPU counters, platform gates and package consumer |

No C/C++ FFI renderer, third-party text/raster engine, or CPU raster substitute is
used. Existing managed/native provider, source application and package gates
remain independent and mandatory.

## Provenance

Initial source baseline: `cbfd65c6a69c49e10a5dbe2fe35967311efd80cc`.
Geometry ports `src/ProGPU.Native/src/Backend/progpu_native_geometry_base.hpp`
and `progpu_native_geometry_analytic.hpp`; GPU records and projection follow
`progpu_native_gpu_records.hpp` and `progpu_native_pipeline.cpp`.
Shader compositions follow `src/ProGPU.Backend/Shaders.cs` and native CMake.
Only original ProGPU implementation is ported; external crates remain normal
Cargo dependencies with a committed lockfile.
