# Native SVG layer producer

Implementation only: no build, test, shader, runtime, browser or benchmark validation was run. New production files are `text/native_font_provider/svg.rs` and `svg/{xml,number,values,path,layers,render}.rs`. Provider root supplies `mod svg; pub use svg::*;`.

The original native implementation owns XML-to-layer execution, not merely gzip extraction. Its exported text-module APIs emit canonical layers, 48-byte path segments, 256-byte brushes and 32-byte gradient stops. Native scene-builder consumption retains layer-local path coordinates, separate affine placement and the emitted brush records. Rust reuses those existing segment/material layouts and records the same inventory through retained Scene paths.

Implemented: bounded 16 MiB raw-byte XML, entity decoding without whole-document UTF-8 admission, first duplicate ID selection, qualified close-name matching, local-name attributes, inherited transforms/fill/opacity, bounded and cycle-checked references, original ignored/unsupported element rules, path/rectangle/circle/ellipse/polygon geometry, auto-closed M/L/H/V/Q/T/C/S/A/Z/F paths, control-point bounds and native arc extrema, original gradient defaults/spread/stable stop ordering, inline and full gradient records, atomic four-buffer caller publication, selected-face/gzip connection and immutable retained rendering. Scalar parsing preserves dependent cursor/reference/append order; canonical record copying uses safe contiguous slice copies. Existing native endpoint-arc implementation is shared.

Public APIs: `native_svg_path_requirements`, `native_decode_svg_path`, `native_svg_glyph_requirements`, `native_decode_svg_glyph`; `NativeSvgGlyph::{decode,requirements,copy_into,layers,segments,brushes,gradient_stops}`; `SfntFace::native_svg_glyph_layers`; `NativeSvgGlyphDraw::{new,scene,record}`. Requirements are populated before short-buffer rejection; every caller array remains untouched on capacity failure. Retained draws keep the original decoded generation and actual selected SfntFace owner alive. Text shaping, baseline/italic placement and coverage remain explicit caller policies.

Unresolved arithmetic boundary: source `svg_number.hpp` collects 19 significant decimal digits and performs `long double` power/multiplication before narrowing. No existing owned Rust extended-precision implementation was found (the CFF numeric helper also uses f64). This port preserves scanner/exponent/overflow ordering using host f64 and exposes `NativeSvgNumberArithmetic::HostF64`; it does **not** assert equivalence to extended native long double or cross-runtime transcendental results. XML byte classification matches the ordinary ASCII/C locale; locale-altered C ctype behavior is not qualified. Full original provider, source, pixels and package/browser qualification remains outstanding. General SVG/CSS/strokes/text/filter elements are outside the original native glyph engine and remain ignored exactly by its traversal; they are not fabricated capabilities.

Original SHA256 (paths relative to `src/ProGPU.Native/src/`):

| Source | SHA256 |
|---|---|
| Text/progpu_native_svg_document_internal.hpp | fb8aa473da2b478e93a6e9748707ab2d7231b3969da54255589ea55acac0c81f |
| Text/progpu_native_svg_xml.cpp | acd86a6827daf37527dcae600ddeb1566c319bf83fa806df46daf0427c45841e |
| Text/progpu_native_svg_number.hpp | 8ea62c1def6bb6164f89b9bd29d546464e3ef7111107c406656e61206276fc4e |
| Text/progpu_native_svg_values.cpp | 67f7f6df6478ac20bd0d302d1737bfe6c31c3e4db98bd3c8d76c689162a036fd |
| Text/progpu_native_svg_path.cpp | b3dc9cfa4c804013d9cfe1b56de21419393c6e02064338462992a72734e0ce3d |
| Text/progpu_native_svg_path_internal.hpp | 625d89d0b5b244bd4221cd26f533eb9e54ba6f11da479c714d8c9190fe96776e |
| Text/progpu_native_svg_glyph_layers.cpp | 6ce7387ad7977d56ce86948d2acb49b9ad27203f1c4577f2454d1186f2c9fea1 |
| Text/progpu_native_svg_glyph_decode.cpp | 0bab9d4cfeccbc9820f630055376c1e0b12949ceeb5d8247ac6f79779aeb99fa |
| Geometry/progpu_native_arc.hpp | 350eede28a9c1966407a8e7c5f5530d3c82d0362ac2de9bab31e7b18895ce619 |
