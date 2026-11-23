# Native scene gallery integration

Implementation only; no build, test, shader/compiler check, browser launch, fixture,
benchmark or pixel comparison was run for this increment.

`examples/support/native_scene_gallery.rs` owns a real `NativeSceneBuilder` stream,
semantic `NativeScene`, `NativeSceneRenderer`, native attachment and external-image
inventory. Both existing hosts select it with `--native-scene` or
`?scene=native-scene`. Rendering submits the native engine before the existing host
presents its completed texture. Browser preparation is awaited through the existing
serialized WASM queue; final capture also awaits native work before presentation.

The authored scene includes analytic gradients, triangle geometry, a closed cubic
path, embedded RGBA pixels, repeated actual nested native picture captures, an
isolated rounded opacity mask with an independent draw clip, drop shadow and Gaussian
blur, and an advanced screen-blend layer. Optional `--text FONT_PATH` / browser font
upload supplies actual owned contours to native glyph records and atlas execution.
This nominal Latin adapter preserves source advances and explicit contour closure;
it supplies no invented shaping, hinted-source policy, system font or fallback.

The native root uses a real 960×640 logical attachment at host DPI with ordinary
native presentation. The original mapped-presentation gate for masked, effected
and advanced layers remains intact. Only the subsequent ordinary sampled host draw
fits that completed image to the window. The explicit native format is RGBA8Unorm;
all child captures receive that actual engine format. Attachment size/bytes and
cold/latest native counters remain separately exposed in browser metrics; the
native host prints cold and first retained work. These are runtime diagnostic APIs,
not measurements collected during this port or qualification claims.

Production contracts reused (SHA-256, original owned source):

| Source | SHA-256 |
| --- | --- |
| `src/ProGPU.Native/include/progpu_native.h` | `bf31eab3ad25f079e36391c4d3072bbd15eeab8a94f8cbd04bb9e2d15145e441` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder.cpp` | `c3d052bd2d3e9d979f8ac3f46dabe84225433697b2e24a509dcfbb19adf6ef34` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_layer.cpp` | `7436958d7065afd34b6b602cf480a211e9705184db6db62fed6df28653df3077` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_image.cpp` | `c65b7f68453c9fedd93e0dd9be55211338f2b2e75d246b3a575ba40086ec95be` |
| `src/ProGPU.Native/src/Scene/Builder/progpu_native_scene_builder_glyph.cpp` | `e0a682de64ee1be87fb72a01aa3b00cc956ef4c069b568cd7fa2392ffd8226b3` |
| `src/ProGPU.Native/src/Scene/progpu_native_semantic_render_execution.cpp` | `c0325d3d72f30d79f386cfc0619f84213090b67a3a203e9e69ac4e4add4a9dae` |

Remaining qualification is the original complete native/browser/provider/pixel and
performance work after implementation. This gallery does not cover every native
family or count as executed coverage. No dependency, package or engine admission
policy was changed and no generated artifacts were created.
