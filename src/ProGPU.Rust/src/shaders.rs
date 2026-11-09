//! Canonical shader compositions, matching `ProGPU.Backend/Shaders.cs`.
//! Composition happens at compile time, with no runtime decoding or allocation.

macro_rules! shader {
    ($file:literal) => {
        include_str!(concat!("../../ProGPU.Backend/Shaders/", $file, ".wgsl"))
    };
}

/// Vector primitives and materials, including specialized solid entry points.
pub const VECTOR: &str = concat!(
    shader!("PathAtlasSampling"),
    "\n",
    shader!("RegisteredMaterialCommon"),
    "\n",
    shader!("GlyphCoverageTransfer"),
    "\n",
    shader!("Vector")
);
/// Analytic path rasterization and signed/Boolean coverage.
pub const PATH: &str = concat!(
    shader!("PathRasterizerCommon"),
    "\n",
    shader!("PathRasterizer")
);
/// Grayscale glyph rasterization.
pub const GLYPH: &str = shader!("GlyphRasterizer");
/// Binary glyph rasterization with unchanged dropout and continuation visits.
pub const BINARY_GLYPH: &str = shader!("BinaryGlyphRasterizer");
/// Positioned text rendering.
pub const TEXT: &str = concat!(
    shader!("TextGlyphGeometryCommon"),
    "\n",
    shader!("TextMaskCommon"),
    "\n",
    shader!("TextGlyphCoverageCommon"),
    "\n",
    shader!("GlyphCoverageTransfer"),
    "\n",
    shader!("Text")
);
/// Image rendering and reconstruction.
pub const TEXTURE: &str = concat!(
    shader!("SampledMaskCommon"),
    "\n",
    shader!("TextureImageSamplingCommon"),
    "\n",
    shader!("Texture")
);
/// Original pairwise segment splitting/classification for Boolean reconstruction.
pub const PATH_OPERATION: &str = shader!("PathOpGeometry");
/// Original separate ordered output-record finalizer.
pub const PATH_OPERATION_FINALIZER: &str = shader!("PathOpRecordFinalizer");
/// Named modules available for validation. Inclusion does not qualify host replay.
pub const MODULES: &[(&str, &str)] = &[
    ("vector", VECTOR),
    ("path", PATH),
    ("glyph", GLYPH),
    ("binary_glyph", BINARY_GLYPH),
    ("text", TEXT),
    ("texture", TEXTURE),
    ("path_operation", PATH_OPERATION),
    ("path_operation_finalizer", PATH_OPERATION_FINALIZER),
];
