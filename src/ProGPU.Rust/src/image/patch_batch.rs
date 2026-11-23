//! Immutable complete source patch generation and explicit retained source owner.
use super::*;
/// Independent native and original managed patch input/placement contracts.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ImagePatchPolicy {
    /// Existing native bounded positive source/destination admission.
    #[default]
    Native,
    /// Original managed Canvas layout and two-stage sprite placement. Source UVs
    /// may extend beyond the texture; command bounds are metadata, not clipping.
    ManagedCanvas,
    /// Same source admission with the atlas's explicit Matrix3x2 sprite step.
    ManagedAtlas,
    /// Original ordinary Canvas texture command, retaining source fallback UVs,
    /// signed destination extent and unit homogeneous/patch-opacity metadata.
    ManagedImage,
}
/// Original complete row/sprite ordered patch batch, shared without per-cell draws.
#[derive(Clone, Debug)]
pub struct ImagePatchBatch {
    patches: Arc<[ImagePatch]>,
    bounds: [f32; 4],
    policy: ImagePatchPolicy,
}
impl ImagePatchBatch {
    pub(super) fn new(patches: Vec<ImagePatch>, bounds: [f32; 4]) -> Self {
        Self {
            patches: patches.into(),
            bounds,
            policy: ImagePatchPolicy::ManagedCanvas,
        }
    }
    pub(super) fn new_atlas(patches: Vec<ImagePatch>, bounds: [f32; 4]) -> Self {
        Self {
            patches: patches.into(),
            bounds,
            policy: ImagePatchPolicy::ManagedAtlas,
        }
    }
    /// Actual immutable patch inventory in source painter order.
    pub fn patches(&self) -> &[ImagePatch] {
        &self.patches
    }
    /// Original command left/top/right/bottom metadata, independent of sprite UVs.
    pub fn bounds(&self) -> [f32; 4] {
        self.bounds
    }
    /// Whether the original layout emits no texture command.
    pub fn is_empty(&self) -> bool {
        self.patches.is_empty()
    }
    /// Number of actual emitted quads.
    pub fn len(&self) -> usize {
        self.patches.len()
    }
    /// Whole source inventory admission before source upload or GPU filtering.
    pub fn validate(&self, size: [u32; 2], sampling: RasterSampling) -> Result<(), Error> {
        if size.iter().any(|v| *v == 0 || *v > 16384) || self.bounds.iter().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if self.patches.len() > 65_536 {
            return Err(Error::Capacity);
        }
        if sampling.cubic.is_some() && sampling.max_anisotropy > 1 {
            return Err(Error::InvalidInput);
        }
        for patch in self.patches.iter() {
            if patch
                .source
                .iter()
                .chain(&patch.destination)
                .chain(&patch.transform.0)
                .any(|v| !v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
            match patch.kind {
                ImagePatchKind::FixedColor(color) | ImagePatchKind::AtlasColor { color, .. }
                    if color.iter().any(|v| !v.is_finite()) =>
                {
                    return Err(Error::InvalidInput);
                }
                _ => {}
            }
        }
        Ok(())
    }
    /// Retain the complete batch in one indexed image command.
    pub fn image_draw(
        &self,
        image: impl Into<ImageSource>,
        sampling: RasterSampling,
    ) -> Result<ImageDraw, Error> {
        let image = image.into();
        self.validate(image.size(), sampling)?;
        let b = self.bounds;
        let mut draw = ImageDraw::new(image, [b[0], b[1], b[2] - b[0], b[3] - b[1]]);
        draw.patches = Some(self.patches.clone());
        draw.patch_policy = self.policy;
        apply_sampling(&mut draw, sampling);
        draw.validate()?;
        Ok(draw)
    }
}
/// Original image/bitmap/texture generation retained until explicit preparation.
/// Bitmap recording freezes bytes; it never reads GPU pixels or uploads eagerly.
#[derive(Clone, Debug)]
pub enum ImageBatchSource {
    /// Actual immutable renderer image owner.
    Image(ImageSource),
    /// Exact immutable bitmap snapshot and color-space metadata.
    Raster(RasterSnapshot),
    /// Actual context, subset, color-space and GPU texture owner.
    #[cfg(feature = "gpu")]
    Texture(TextureImage),
}
impl ImageBatchSource {
    /// Actual selected source dimensions, independent of underlying texture subset.
    pub fn size(&self) -> [u32; 2] {
        match self {
            Self::Image(image) => image.size(),
            Self::Raster(image) => [
                image.info().width.max(0) as u32,
                image.info().height.max(0) as u32,
            ],
            #[cfg(feature = "gpu")]
            Self::Texture(image) => [
                image.info().width.max(0) as u32,
                image.info().height.max(0) as u32,
            ],
        }
    }
}
impl From<ImageSource> for ImageBatchSource {
    fn from(value: ImageSource) -> Self {
        Self::Image(value)
    }
}
impl From<&ImageSource> for ImageBatchSource {
    fn from(value: &ImageSource) -> Self {
        value.clone().into()
    }
}
impl From<Image> for ImageBatchSource {
    fn from(value: Image) -> Self {
        Self::Image(value.into())
    }
}
impl From<&Image> for ImageBatchSource {
    fn from(value: &Image) -> Self {
        value.clone().into()
    }
}
impl From<RasterSnapshot> for ImageBatchSource {
    fn from(value: RasterSnapshot) -> Self {
        Self::Raster(value)
    }
}
impl From<&RasterSnapshot> for ImageBatchSource {
    fn from(value: &RasterSnapshot) -> Self {
        value.clone().into()
    }
}
#[cfg(feature = "gpu")]
impl From<GpuImage> for ImageBatchSource {
    fn from(value: GpuImage) -> Self {
        Self::Image(value.into())
    }
}
#[cfg(feature = "gpu")]
impl From<TextureImage> for ImageBatchSource {
    fn from(value: TextureImage) -> Self {
        Self::Texture(value)
    }
}
#[cfg(feature = "gpu")]
impl From<&TextureImage> for ImageBatchSource {
    fn from(value: &TextureImage) -> Self {
        value.clone().into()
    }
}
#[cfg(feature = "gpu")]
impl From<&GpuImage> for ImageBatchSource {
    fn from(value: &GpuImage) -> Self {
        value.clone().into()
    }
}
pub(super) fn color_components(color: [u8; 4]) -> [f32; 4] {
    (wide::f32x4::from(color.map(f32::from)) / wide::f32x4::splat(255.0)).to_array()
}
pub(crate) fn apply_sampling(draw: &mut ImageDraw, sampling: RasterSampling) {
    draw.sampling = if sampling.cubic.is_some() {
        ImageSampling::Cubic
    } else if sampling.max_anisotropy > 0 || sampling.mipmap != RasterMipmapMode::None {
        ImageSampling::LinearMipmap
    } else if sampling.filter == RasterFilterMode::Nearest {
        ImageSampling::Nearest
    } else {
        ImageSampling::Linear
    };
    draw.cubic = sampling
        .cubic
        .filter(|c| c.iter().all(|v| v.is_finite()))
        .unwrap_or([0.0, 0.5]);
    draw.max_anisotropy = if sampling.max_anisotropy > 0 {
        sampling.max_anisotropy.clamp(1, 16) as u8
    } else {
        1
    };
}
