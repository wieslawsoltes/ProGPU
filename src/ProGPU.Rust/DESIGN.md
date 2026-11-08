# Rust port design and evidence

The authoritative algorithms are the original ProGPU files listed in README.md.
External sources below inform boundaries and tests only; no external engine code
is copied or translated into the implementation.

## Architecture research (2026-11-08)

* [Skia canvas](https://skia.org/docs/user/api/skcanvas_overview/) separates
  matrix/clip state from draw paint. [Skia text overview](https://skia.org/docs/dev/design/text_overview/)
  separates text processing stages. Adopt those boundaries already present in
  ProGPU: immutable source geometry/text and explicit replay state.
* [DirectWrite](https://learn.microsoft.com/en-us/windows/win32/directwrite/direct-write-portal)
  offers reusable layout and lower-level glyph rendering. Preserve ProGPU's
  positioned glyph ownership independently of rendering; no platform shaper
  substitution or implicit equivalence between grayscale and ClearType.
* [Direct2D resource domains](https://learn.microsoft.com/en-us/windows/win32/direct2d/resources-and-resource-domains)
  distinguish device-independent and dependent resources. Keep CPU geometry
  after device loss, invalidate GPU realizations, and reject cross-device replay.
* [Win2D custom hosts](https://learn.microsoft.com/en-us/windows/apps/develop/win2d/using-win2d-without-built-in-controls)
  leave resize/DPI/device-loss policy to lower-level consumers. Adapt this to a
  winit host separate from the renderer, including zero-size/suspended windows.
* [WebRender](https://github.com/servo/webrender/wiki) provides a retained rendering
  reference. Retain ProGPU's compiled generations and ordered batches, with no
  geometry upload on stable replay. Visibility and atlas eviction must preserve
  source identity and painter order when those families are ported.
* [Vello](https://github.com/linebender/vello) is a compute-oriented rendering
  reference; [Parley](https://github.com/linebender/parley) is a separate text layout
  library. Preserve ProGPU's existing GPU rasterization and reusable CPU layout
  boundary; do not substitute either engine for the owned implementation.
* [HarfBuzz shaping concepts](https://harfbuzz.github.io/shaping-concepts.html)
  distinguish script-dependent shaping from simple advances. Full ProGPU Unicode,
  fallback and OpenType behavior remains required; a character-to-glyph loop will
  not stand in for shaping.

Startup must allocate only requested pipeline families. Geometry compilation may
run off-thread without a GPU; realization occurs on demand and batches uploads.
Future glyph cache keys must retain selected face, variations, hinting, DPI,
phase, raster policy and generation. Worker preparation must not mutate published
generations. Resource eviction cannot invalidate an encoded frame. Culling must
operate on original bounds/clip semantics, not approximate shader envelopes.
These are port requirements, not claims that pending families are implemented.

## Initial validation

`cargo test --no-default-features`: two geometry regressions and canonical Naga
validation of all six initial shader compositions pass. The explicit native
oracle test executes the unchanged C++ analytic header: 288 cases and 36,864
output bytes match exactly, covering all three primitive kinds, AA/aliased edges,
zero extents, strokes, affine/reflected frames and invalid dimensions.
`cargo clippy --no-default-features --all-targets -- -D warnings` passes.

The new implementation does not alter managed/native source or shaders; their
behavior and existing qualification gates remain unchanged. Rendering changes
in this port still require matched CPU records and GPU image evidence before
being declared parity-qualified. No performance improvement is claimed.
