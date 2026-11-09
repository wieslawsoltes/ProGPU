//! Canonical backend compositions and original ProGPU.Compute effect resources.
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
/// Original destination-aware advanced blend, raster operation and AA composition shader.
pub const ADVANCED_BLEND: &str = shader!("AdvancedBlend");
/// Original actual-target storage replacement, independent of paint/masks.
pub const TARGET_CLEAR: &str = shader!("TargetClear");
/// Original linear previous-level mip generation, without CPU resampling.
pub const MIP_GENERATOR: &str = shader!("MipGenerator");
/// Original horizontal Gaussian/box group-effect kernel.
pub const BLUR_HORIZONTAL: &str =
    include_str!("../../ProGPU.Compute/Shaders/GaussianBlurHorizontal.wgsl");
/// Original vertical Gaussian/box group-effect kernel.
pub const BLUR_VERTICAL: &str =
    include_str!("../../ProGPU.Compute/Shaders/GaussianBlurVertical.wgsl");
/// Original premultiplied source-over-shadow composition kernel.
pub const DROP_SHADOW: &str =
    include_str!("../../ProGPU.Compute/Shaders/GroupDropShadowCompose.wgsl");
/// Original pairwise segment splitting/classification for Boolean reconstruction.
pub const PATH_OPERATION: &str = shader!("PathOpGeometry");
/// Original separate ordered output-record finalizer.
pub const PATH_OPERATION_FINALIZER: &str = shader!("PathOpRecordFinalizer");
/// Original fused RGB/YUV, color matrix, spherical image effect.
pub const IMAGE_EFFECT: &str = include_str!("../../ProGPU.Scene/Shaders/ImageEffect.wgsl");
/// Original paired-tap filterable image Gaussian kernel.
pub const IMAGE_BLUR: &str = shader!("TextureGaussianBlur");
/// Original integer-load unfilterable planar Gaussian kernel.
pub const IMAGE_BLUR_UNFILTERABLE: &str = shader!("TextureGaussianBlurUnfilterable");
/// Original path-atlas transfer and raw/sampled retained-mask composition.
pub const CLIP_COMPOSE: &str = concat!(
    shader!("PathAtlasSampling"),
    "\n",
    shader!("SampledMaskCommon"),
    "\n",
    shader!("ClipCompose")
);
/// Original staged signed-winding leaf supersamples.
pub const PATH_SIGNED_LEAF: &str = concat!(
    shader!("PathRasterizerCommon"),
    "\n",
    shader!("PathSignedWindingLeaf")
);
/// Original staged signed-row postfix evaluation.
pub const PATH_SIGNED_ROWS: &str = shader!("PathSignedWindingEvaluate");
/// Original staged signed coverage packing.
pub const PATH_SIGNED_PACK: &str = shader!("PathSignedWindingCoverage");
/// Original uniform-backdrop group blending.
pub const GROUP_BLEND: &str = shader!("GroupBlend");
/// Original legacy bytecode-effect wrapper; validated body is inserted at preparation.
pub const WPF_BYTECODE_EFFECT: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfBytecodeEffect.wgsl");
/// Original final-device sample wrapper, with an independent complete input lattice.
pub const WPF_BYTECODE_SAMPLE_EFFECT: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfBytecodeSampleEffect.wgsl");
/// Original affine final-device sample wrapper, retaining both cross terms.
pub const WPF_BYTECODE_AFFINE_EFFECT: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfBytecodeAffineEffect.wgsl");
/// Canonical managed WGSL effect vertex/header template.
pub const WPF_EFFECT_HEADER: &str = include_str!("../../ProGPU.Scene/Shaders/WpfEffectHeader.wgsl");
/// Canonical managed optional mask declarations.
pub const WPF_EFFECT_MASK_DECLARATIONS: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectMaskDeclarations.wgsl");
/// Canonical managed sampled and analytic mask helper.
pub const WPF_EFFECT_MASK_HELPER: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectMaskHelper.wgsl");
/// Canonical managed no-mask helper.
pub const WPF_EFFECT_NO_MASK_HELPER: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectNoMaskHelper.wgsl");
/// Canonical original pass-through WGSL effect body.
pub const WPF_EFFECT_PASS_THROUGH: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectPassThrough.wgsl");
/// Canonical managed straight-alpha fragment wrapper.
pub const WPF_EFFECT_STRAIGHT_WRAPPER: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectStraightWrapper.wgsl");
/// Canonical managed premultiplied-alpha fragment wrapper.
pub const WPF_EFFECT_PREMULTIPLIED_WRAPPER: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectPremultipliedWrapper.wgsl");
/// Canonical managed straight-to-premultiplied fragment wrapper.
pub const WPF_EFFECT_STRAIGHT_TO_PREMULTIPLIED_WRAPPER: &str =
    include_str!("../../ProGPU.Scene/Shaders/WpfEffectStraightToPremultipliedWrapper.wgsl");
/// Named modules available for validation. Inclusion does not qualify host replay.
pub const MODULES: &[(&str, &str)] = &[
    ("group_blend", GROUP_BLEND),
    ("path_signed_leaf", PATH_SIGNED_LEAF),
    ("path_signed_rows", PATH_SIGNED_ROWS),
    ("path_signed_pack", PATH_SIGNED_PACK),
    ("clip_compose", CLIP_COMPOSE),
    ("image_effect", IMAGE_EFFECT),
    ("image_blur", IMAGE_BLUR),
    ("image_blur_unfilterable", IMAGE_BLUR_UNFILTERABLE),
    ("vector", VECTOR),
    ("path", PATH),
    ("glyph", GLYPH),
    ("binary_glyph", BINARY_GLYPH),
    ("text", TEXT),
    ("texture", TEXTURE),
    ("mip_generator", MIP_GENERATOR),
    ("advanced_blend", ADVANCED_BLEND),
    ("target_clear", TARGET_CLEAR),
    ("blur_horizontal", BLUR_HORIZONTAL),
    ("blur_vertical", BLUR_VERTICAL),
    ("drop_shadow", DROP_SHADOW),
    ("path_operation", PATH_OPERATION),
    ("path_operation_finalizer", PATH_OPERATION_FINALIZER),
];
