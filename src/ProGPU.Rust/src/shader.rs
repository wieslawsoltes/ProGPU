//! Original immutable SKShader factories and independent brush/capture policies.
//! Shader wrappers retain their actual source graph; no source language compiler
//! or implicit graphics context is invented by these value factories.
use crate::{
    Error,
    canvas::CanvasMatrix,
    image::{ColorFilter, ColorSpace, ImageSource},
    image_filter::{
        FilterBlender, FilterImageSource, FilterPictureSource, FilterRect, FilterSampling,
        FilterShaderSource, FilterTileMode, FilterTransform,
    },
    scene::LayerBlend,
    vector::{Brush, BrushKind, Matrix4},
};
use std::sync::{Arc, OnceLock};
mod brush;
#[cfg(feature = "gpu")]
mod gpu;
mod gradient;
pub use gradient::{ShaderGradientOptions, ShaderStops};
/// Original independent source tile policies.
pub type ShaderTileMode = FilterTileMode;
/// Original reconstruction record, including cubic coefficients and anisotropy.
pub type ShaderSampling = FilterSampling;
#[derive(Debug)]
struct ShaderOwner {
    source: Arc<FilterShaderSource>,
    material: OnceLock<Option<Arc<Brush>>>,
}
/// One immutable original shader generation. Clone retains actual image/picture,
/// runtime uniforms, child shaders and filter owners without executing callbacks.
#[derive(Clone, Debug)]
pub struct Shader(Arc<ShaderOwner>);
impl Shader {
    /// Retain a complete typed source graph after bounded ownership admission.
    /// A runtime source remains a real unsupported execution category.
    pub fn from_source(source: Arc<FilterShaderSource>) -> Result<Self, Error> {
        source.runtime_support()?;
        Ok(Self(Arc::new(ShaderOwner {
            source,
            material: OnceLock::new(),
        })))
    }
    fn material(brush: Brush) -> Self {
        let brush = Arc::new(brush);
        let material = OnceLock::new();
        let _ = material.set(Some(brush.clone()));
        Self(Arc::new(ShaderOwner {
            source: Arc::new(FilterShaderSource::Brush(brush)),
            material,
        }))
    }
    /// Original empty shader is a transparent solid, not a missing source owner.
    pub fn empty() -> Self {
        Self::material(Brush::default())
    }
    /// Original straight byte RGBA divisions.
    pub fn color(color: [u8; 4]) -> Self {
        Self::material(Brush::new(BrushKind::Solid(
            color.map(|v| f32::from(v) / 255.0),
        )))
    }
    /// Original floating color clamps components; the supplied selected color
    /// space is admission metadata and is not applied as a gamut conversion.
    pub fn color_f(color: [f32; 4], _space: &ColorSpace) -> Self {
        Self::material(Brush::new(BrushKind::Solid(
            color.map(|v| v.clamp(0.0, 1.0)),
        )))
    }
    /// Original byte color-space overload does not alter its source values.
    pub fn color_in_space(color: [u8; 4], _space: &ColorSpace) -> Self {
        Self::color(color)
    }
    /// Original raw Perlin/fractal descriptor. Finite/range admission belongs to
    /// material preparation, retaining the signed octave count and original seed.
    pub fn perlin_noise(
        turbulence: bool,
        frequency: [f32; 2],
        octaves: i32,
        seed: f32,
        tile_size: [i32; 2],
    ) -> Self {
        Self::material(Brush::new(BrushKind::PerlinNoise(
            crate::vector::PerlinNoise::new(
                turbulence,
                frequency,
                octaves,
                seed,
                tile_size.map(|v| v as f32),
            ),
        )))
    }
    /// Ordered original destination/source shader composition.
    pub fn compose(background: &Self, foreground: &Self, blend: LayerBlend) -> Result<Self, Error> {
        Self::blend(&FilterBlender::Mode(blend), background, foreground)
    }
    /// Fixed and arithmetic composition retain distinct policies. Original
    /// runtime blenders reject atomically rather than selecting SrcOver.
    pub fn blend(
        blender: &FilterBlender,
        background: &Self,
        foreground: &Self,
    ) -> Result<Self, Error> {
        Self::from_source(Arc::new(FilterShaderSource::blend(
            blender,
            background.source(),
            foreground.source(),
        )?))
    }
    /// Original wrapper retains the child's identity and complete local matrix.
    /// It is not flattened into gradient stops, image source or picture metadata.
    pub fn with_local_matrix(&self, matrix: CanvasMatrix) -> Result<Self, Error> {
        Self::from_source(Arc::new(FilterShaderSource::LocalMatrix {
            source: self.source(),
            matrix: matrix.matrix4(),
        }))
    }
    /// Original shader color-filter wrapper. Canvas executes this as a separate
    /// image filter; public to_brush keeps its independent stop-filter policy.
    pub fn with_color_filter(&self, filter: ColorFilter) -> Result<Self, Error> {
        Self::from_source(Arc::new(FilterShaderSource::ColorFilter {
            source: self.source(),
            filter,
        }))
    }
    /// Explicit already-retained immutable image producer. Public texture-image
    /// factories separately make the original owned base-level copy.
    pub fn retained_image(
        source: FilterImageSource,
        tile: [ShaderTileMode; 2],
        sampling: ShaderSampling,
        matrix: CanvasMatrix,
        is_raw: bool,
    ) -> Result<Self, Error> {
        Self::from_source(Arc::new(FilterShaderSource::Image {
            source,
            tile,
            sampling,
            matrix: matrix.matrix4(),
            is_raw,
        }))
    }
    /// Explicit immutable CPU pixel source with deferred canonical GPU upload.
    /// The optional selected transfer remains distinct from raw-image bypass.
    pub fn pixels(
        image: crate::image::Image,
        color_space: Option<&ColorSpace>,
        tile: [ShaderTileMode; 2],
        sampling: ShaderSampling,
        matrix: CanvasMatrix,
        is_raw: bool,
    ) -> Result<Self, Error> {
        Self::retained_image(
            FilterImageSource {
                image: ImageSource::Pixels(image),
                color_space: color_space.map(ColorSpace::to_filter_color_space),
            },
            tile,
            sampling,
            matrix,
            is_raw,
        )
    }
    /// Original retained actual picture commands, cull/color inventory, tile
    /// rectangle and independent nearest/linear reconstruction.
    pub fn picture(
        source: FilterPictureSource,
        tile_modes: [ShaderTileMode; 2],
        sampling: crate::image::ImageSampling,
        matrix: CanvasMatrix,
        tile: Option<FilterRect>,
    ) -> Result<Self, Error> {
        if !matches!(
            sampling,
            crate::image::ImageSampling::Nearest | crate::image::ImageSampling::Linear
        ) {
            return Err(Error::InvalidInput);
        }
        let tile = tile.unwrap_or(source.target);
        Self::from_source(Arc::new(FilterShaderSource::Picture {
            source,
            matrix: matrix.matrix4(),
            tile,
            tile_modes,
            sampling,
        }))
    }
    /// Actual reflected runtime instance and immutable child/uniform generation.
    /// Existing runtime source-language execution gates remain unchanged.
    pub fn runtime(instance: crate::runtime_effect::RuntimeEffectInstance) -> Result<Self, Error> {
        if instance.program().kind() != crate::runtime_effect::RuntimeEffectKind::Shader {
            return Err(Error::InvalidInput);
        }
        Self::from_source(Arc::new(FilterShaderSource::Runtime(instance)))
    }
    /// O(1) exact graph owner for Paint and canonical image-filter execution.
    pub fn source(&self) -> Arc<FilterShaderSource> {
        self.0.source.clone()
    }
    /// Inspect exact typed source metadata without materialization.
    pub fn kind(&self) -> &FilterShaderSource {
        &self.0.source
    }
    /// Original owner identity, independent of structural/color equality.
    pub fn same_owner(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    /// Independent original public vector-brush conversion. Returned mutable
    /// gradient arrays are copies; shader/image/picture ownership stays frozen.
    pub fn to_brush(&self) -> Result<Brush, Error> {
        let material = self
            .0
            .material
            .get_or_init(|| brush::to_brush(&self.0.source, 0).ok().map(Arc::new));
        material
            .as_ref()
            .map(|v| v.as_ref().clone())
            .ok_or(Error::UnsupportedExecution)
    }
    /// Original Canvas distinction: ColorFilter, image, picture and composition
    /// are special source captures; local wrappers recurse without changing it.
    pub fn requires_capture(&self) -> Result<bool, Error> {
        brush::special(&self.0.source, 0)
    }
}
pub(crate) fn canvas_brush(source: &FilterShaderSource) -> Result<Brush, Error> {
    if brush::special(source, 0)? {
        return Err(Error::UnsupportedExecution);
    }
    brush::to_brush(source, 0)
}
pub(crate) use brush::filter_brush;
