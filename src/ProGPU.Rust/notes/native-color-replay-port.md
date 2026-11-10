# Native color replay adapter

Implementation only; no compiler, test, GPU/browser, benchmark or fixture execution. Root adds `text::native_color` and the private `scene::native_color` module declarations. Production files: `text/native_color.rs`, `native_color/{bitmap,layers,plan}.rs`, `scene/native_color.rs`; narrow native-design source ownership and native-atlas position-policy hooks.

C++ owns COLR/CPAL/SVG/sbix/CBLC/CBDT queries and canonical caller-supplied RGBA resources. No C++ automatic bitmap decoder/color-preference caller was found beyond font-candidate renderability. This is explicitly an adapter of the original owned **managed native-scene compiler** selection/presentation over native query/outline producers, not a claim of an absent C++ codec.

`NativeColorResolver` retains actual font bytes, selected face, normalized coordinates, glyph, frozen palette/overrides and target raster size in bounded cache entries. COLR precedes SVG, then bitmap; a matching empty COLR base suppresses SVG but permits bitmap. Native palette foreground/missing entries preserve white and their source flag. sbix precedes CBDT at lookup; a selected sbix payload failing decoding does not retry CBDT. PNG uses the original owned native decoder by default; JPEG now uses the safe image-crate public decoder boundary with bounded straight-RGBA publication. TIFF remains a selected-strike decode miss under the original default capability; its raw tag never proved a StbImageSharp TIFF codec. The explicit `EncodedRasterDecoder` boundary can supply TIFF or another independently owned provider. See [JPEG integration](native-jpeg-bitmap-port.md). Decoder differences from original StbImageSharp remain unqualified. Borrowed decoder addresses never serve as persistent identities; one preparation deduplicates its actual provider results locally.

`NativeDesignGeometry::new_for_color` captures complete real source slots, fonts and instance coordinates for fonts with no outline tables; `new` retains its original strict monochrome admission. No fake empty outline kind is introduced. `NativeColorReplay::prepare` builds an immutable ordered result: COLR paths, native SVG layer draws, bitmap/monochrome atlas runs, and CFF/varied explicit-vector fallbacks. COLR preserves layer order, foreground flags, no-ink slots, phase-128 placement, actual native outline ownership, one color-layer pass (no synthetic color bold duplication), source italic and existing text raster/contrast policy. SVG consumes the native producer's canonical local geometry/full-affine contract, keeping its independent raw-byte and numeric gates; it does not recreate managed DOM endpoint history from resolved arc records. Consequently managed SVG arc-rewrite/phase equivalence is a separate unqualified boundary, not claimed by this adapter.

Bitmap presentation preserves original sbix-origin versus CBDT-bearing equations, strike-to-raster ratio, optional outline-derived render extents, original clamp/floor/ceiling rules, tight straight RGBA and exact 48-byte retained descriptors. All records/pixels and per-slot placement are admitted before publication. Atlas batching retains painter order and common source generation; selected bitmap positions resolve exactly once through the actual native target frame. Existing already-positioned `NativeColorGlyphBatch::new` stays unchanged; `from_source_positions` explicitly selects the new source adapter. Empty selected atlas leases retain complete source ownership for no-ink color selections without emitting draw instances. Frozen bitmap DPI is checked again by the existing atlas preparation path.

Public entry points: `NativeDesignGlyphSource::new`, `NativeColorResolver::{new,resolve,clear}`, `NativeColorReplay::{prepare,record}`, `Scene::draw_native_color_path`, plus immutable presentation/source/pixel accessors. Native query/outline faults remain typed failures rather than managed-parser substitution. Actual system-provider, full target frame, original pixels, decoder, browser and package qualification remains outstanding; no validation claim follows from authored code.

Original SHA256:

| Source | SHA256 |
|---|---|
| src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.Glyphs.cs | 093976562d04f82a5f16b08731eed3e7587fef5a85cede17717caef410d5121d |
| src/ProGPU.Scene.Native/GpuPictureNativeSceneCompiler.BitmapGlyphs.cs | 992b504dbf48344076a716e751b851973c215d02353d0216ec0d2d4d822a9e40 |
| src/ProGPU.Text/TtfFont.cs | 7db1a7d8a0d4cf40e4beb182fc5ef03d0cdf5953b547191c4aa5a34b9c48f85b |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_color_glyph.cpp | da49fe835f4101613a102950072dcc13b76f721cf90e808ee36cc1dd862a5972 |
| src/ProGPU.Native/src/Scene/progpu_native_semantic_validation.cpp | 3b49e72ee4a6d3d6d413f3e5f6eb3772d3894e730a0ec4f1926e6b0642449c32 |
| src/ProGPU.Native/src/Text/progpu_native_colr.cpp | fceaf8650f7e9029cf827c1cf889a299af874a1da23d455414efbef3fb93a524 |
| src/ProGPU.Native/src/Text/progpu_native_cpal.cpp | b41d91e1276746c4492142bec91b4db6ce95d2d931dbe4b937725df9b8c31518 |
| src/ProGPU.Native/src/Text/progpu_native_sbix.cpp | 2d76621a394ac89b2654d949055c0798b0ae3c63cfd399941c5399e6fedf0759 |
| src/ProGPU.Native/src/Text/progpu_native_cblc.cpp | 89f0034111702e2111049c404dad409132322c52d7130a4101aa1ec7a9d6b2b4 |
| src/ProGPU.Native/src/Text/progpu_native_cbdt.cpp | 1fe2ac388f9a696d0960a6b60ffeca781c7073c000200faac703cbddabd3ffb7 |
