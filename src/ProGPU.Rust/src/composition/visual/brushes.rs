//! Original WinUI surface and mask brush scopes with frozen source owners.
use super::*;
use crate::{
    geometry::AnalyticPrimitive,
    image::{ImageBrush, ImageSampling},
    mask::{Mask, SourceMask},
    scene::MaterialPaint,
    vector::Brush,
};

/// Frozen real scene brush. Source masks keep their original paint programs;
/// source texture generations are retained independently of mutable resources.
#[derive(Clone, Debug)]
pub enum CompositionBrushSnapshot {
    /// Original color/gradient scene material.
    Material(Arc<Brush>),
    /// Actual texture and original source/destination/custom mapping.
    Surface(ImageBrush),
    /// Original BeginSceneBrush opacity scope, with independent producer owners.
    Mask { source: Box<Self>, mask: Box<Self> },
    /// An explicitly null or unavailable surface/source, not missing metadata.
    Empty,
}
impl CompositionBrushSnapshot {
    /// Direct material when the original brush uses material shading.
    pub fn material(&self) -> Option<&Arc<Brush>> {
        if let Self::Material(v) = self {
            Some(v)
        } else {
            None
        }
    }
    /// Draw the original brush over its receiving bounds, including mask scopes.
    pub fn record(&self, scene: &mut Scene, bounds: [f32; 4]) -> Result<(), Error> {
        self.with_source(scene, bounds, |scene, source| match source {
            Self::Material(brush) => scene.draw_material_analytic(
                AnalyticPrimitive::rectangle(bounds, [1.0; 4]),
                MaterialPaint::new(brush.clone()),
                [bounds[0] + bounds[2] * 0.5, bounds[1] + bounds[3] * 0.5],
            ),
            Self::Surface(brush) => scene.draw_image(brush.for_fill(bounds)?),
            Self::Empty => Ok(()),
            Self::Mask { .. } => unreachable!(),
        })
    }
    pub(super) fn with_source(
        &self,
        scene: &mut Scene,
        bounds: [f32; 4],
        record: impl FnOnce(&mut Scene, &Self) -> Result<(), Error>,
    ) -> Result<(), Error> {
        match self {
            Self::Empty => Ok(()),
            Self::Mask { source, mask } => {
                if matches!(source.as_ref(), Self::Empty) || matches!(mask.as_ref(), Self::Empty) {
                    return Ok(());
                }
                let mut mask_scene = Scene::default();
                mask.record(&mut mask_scene, bounds)?;
                // Candidate ownership and balanced scope publication are atomic.
                let mut candidate = Scene::default();
                candidate.push_mask(Mask::Source(SourceMask::new(
                    Arc::new(mask_scene),
                    Matrix4::IDENTITY,
                )?))?;
                source.with_source(&mut candidate, bounds, record)?;
                candidate.pop_scope()?;
                scene.append(&candidate)
            }
            _ => record(scene, self),
        }
    }
    #[cfg(feature = "gpu")]
    pub(crate) fn preflight_device(&self, device: &wgpu::Device) -> Result<(), Error> {
        match self {
            Self::Surface(v) => {
                if let crate::image::ImageSource::Gpu(image) = &v.draw.image
                    && image.device() != device
                {
                    return Err(Error::ResourceDomain);
                }
                Ok(())
            }
            Self::Mask { source, mask } => {
                source.preflight_device(device)?;
                mask.preflight_device(device)
            }
            _ => Ok(()),
        }
    }
}

/// Original CompositionBitmapInterpolationMode values, distinct from image enum.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum CompositionBitmapInterpolationMode {
    NearestNeighbor = 0,
    #[default]
    Linear = 1,
    MagLinearMinLinearMipLinear = 2,
    MagLinearMinLinearMipNearest = 3,
    MagLinearMinNearestMipLinear = 4,
    MagLinearMinNearestMipNearest = 5,
    MagNearestMinLinearMipLinear = 6,
    MagNearestMinLinearMipNearest = 7,
    MagNearestMinNearestMipLinear = 8,
    MagNearestMinNearestMipNearest = 9,
}
impl CompositionBitmapInterpolationMode {
    fn sampling(self) -> ImageSampling {
        use CompositionBitmapInterpolationMode::*;
        match self {
            NearestNeighbor | MagNearestMinNearestMipNearest => ImageSampling::Nearest,
            Linear => ImageSampling::Linear,
            MagLinearMinLinearMipLinear => ImageSampling::LinearMipmap,
            MagLinearMinLinearMipNearest => ImageSampling::MagLinearMinLinearMipNearest,
            MagLinearMinNearestMipLinear => ImageSampling::MagLinearMinNearestMipLinear,
            MagLinearMinNearestMipNearest => ImageSampling::MagLinearMinNearestMipNearest,
            MagNearestMinLinearMipLinear => ImageSampling::MagNearestMinLinearMipLinear,
            MagNearestMinLinearMipNearest => ImageSampling::MagNearestMinLinearMipNearest,
            MagNearestMinNearestMipLinear => ImageSampling::MagNearestMinNearestMipLinear,
        }
    }
}

#[cfg(feature = "gpu")]
#[derive(Clone, Debug, Default)]
pub struct CompositionSurfaceValue {
    /// Actual completed texture generation; None is the original null surface.
    pub image: Option<crate::image::TextureImage>,
}
#[cfg(feature = "gpu")]
impl PartialEq for CompositionSurfaceValue {
    fn eq(&self, other: &Self) -> bool {
        match (&self.image, &other.image) {
            (None, None) => true,
            (Some(a), Some(b)) => a.same_storage(b) && a.texture_region() == b.texture_region(),
            _ => false,
        }
    }
}
#[cfg(feature = "gpu")]
pub type CompositionSurface = CompositionResource<CompositionSurfaceValue>;
#[cfg(feature = "gpu")]
impl CompositionSurface {
    /// Publish one complete immutable source generation and notify weak owners.
    pub fn set(&self, value: CompositionSurfaceValue) -> Result<(), Error> {
        validate_surface(&value)?;
        self.replace(value);
        Ok(())
    }
}
#[cfg(feature = "gpu")]
pub(super) fn validate_surface(value: &CompositionSurfaceValue) -> Result<(), Error> {
    if let Some(image) = &value.image {
        let size = image.gpu_image().size();
        if image.texture_region() != [0, 0, size[0], size[1]] {
            return Err(Error::UnsupportedExecution);
        }
        if !image.is_valid(image.context()) {
            return Err(Error::ResourceDomain);
        }
    }
    Ok(())
}

/// Original surface mapping defaults and independent custom transform fields.
#[cfg(feature = "gpu")]
#[derive(Clone, Debug, PartialEq)]
pub struct CompositionSurfaceBrush {
    pub surface: Option<CompositionSurface>,
    pub interpolation: CompositionBitmapInterpolationMode,
    pub stretch: CompositionStretch,
    pub snap_to_pixels: bool,
    pub anchor: [f32; 2],
    pub transform: CompositionTransform,
    pub alignment: [f32; 2],
}
#[cfg(feature = "gpu")]
impl Default for CompositionSurfaceBrush {
    fn default() -> Self {
        Self {
            surface: None,
            interpolation: Default::default(),
            stretch: Default::default(),
            snap_to_pixels: false,
            anchor: [0.0; 2],
            transform: Default::default(),
            alignment: [0.5; 2],
        }
    }
}
#[cfg(feature = "gpu")]
impl CompositionSurfaceBrush {
    pub(super) fn normalize(&mut self) -> Result<(), Error> {
        self.transform.validate()?;
        if self
            .anchor
            .iter()
            .chain(&self.alignment)
            .any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        self.alignment = self.alignment.map(|v| v.clamp(0.0, 1.0));
        Ok(())
    }
    pub(super) fn snapshot(&self, bounds: [f32; 4]) -> Result<CompositionBrushSnapshot, Error> {
        let Some(surface) = &self.surface else {
            return Ok(CompositionBrushSnapshot::Empty);
        };
        let value = surface.value();
        validate_surface(&value)?;
        let Some(image) = value.image else {
            return Ok(CompositionBrushSnapshot::Empty);
        };
        let size = image.gpu_image().size().map(|v| v as f32);
        let ratio = [bounds[2] / size[0], bounds[3] / size[1]];
        let scale = match self.stretch {
            CompositionStretch::None => [1.0; 2],
            CompositionStretch::Fill => ratio,
            CompositionStretch::Uniform => [ratio[0].min(ratio[1]); 2],
            CompositionStretch::UniformToFill => [ratio[0].max(ratio[1]); 2],
        };
        let painted = [size[0] * scale[0], size[1] * scale[1]];
        if painted[0] <= 0.0 || painted[1] <= 0.0 {
            return Ok(CompositionBrushSnapshot::Empty);
        }
        let destination = [
            bounds[0] + (bounds[2] - painted[0]) * self.alignment[0],
            bounds[1] + (bounds[3] - painted[1]) * self.alignment[1],
            painted[0],
            painted[1],
        ];
        let t = self.transform;
        let mut matrix = crate::geometry::Affine([
            1.0,
            0.0,
            0.0,
            1.0,
            -self.anchor[0] * bounds[2],
            -self.anchor[1] * bounds[3],
        ]);
        // Keep each original Matrix3x2 multiplication in its original order.
        for next in [
            crate::geometry::Affine([1.0, 0.0, 0.0, 1.0, -t.center[0], -t.center[1]]),
            crate::geometry::Affine([t.scale[0], 0.0, 0.0, t.scale[1], 0.0, 0.0]),
            crate::geometry::Affine([
                t.rotation.cos(),
                t.rotation.sin(),
                -t.rotation.sin(),
                t.rotation.cos(),
                0.0,
                0.0,
            ]),
            crate::geometry::Affine([1.0, 0.0, 0.0, 1.0, t.center[0], t.center[1]]),
            t.matrix,
            crate::geometry::Affine([1.0, 0.0, 0.0, 1.0, t.offset[0], t.offset[1]]),
        ] {
            matrix = properties::multiply(matrix, next);
        }
        let mut draw = image.draw(destination);
        draw.transform = matrix;
        draw.sampling = self.interpolation.sampling();
        draw.snap_to_pixels = self.snap_to_pixels;
        draw.validate()?;
        Ok(CompositionBrushSnapshot::Surface(ImageBrush {
            draw,
            extend_to_fill_bounds: false,
        }))
    }
}
