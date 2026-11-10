//! Retained full-image filter input, independent of final consumer placement.
use super::{FilterPlanRequest, FilterTransform, ImageFilter, ImageFilterPlan};
use crate::{
    Error,
    image::{ImageAlpha, ImageDraw, ImageFormat, ImageSource},
};
/// Immutable original filter recipe over an actual image generation. The request
/// freezes the filter target independently of the final consumer draw transform.
#[derive(Clone, Debug)]
pub struct ImageFilteredImageDraw {
    /// Actual source owner and independent final placement/sampling/opacity.
    pub draw: ImageDraw,
    /// Complete original filter generation.
    pub filter: ImageFilter,
    /// Actual filter source/canvas frame and bounded resource admission.
    pub request: FilterPlanRequest,
}
impl ImageFilteredImageDraw {
    /// Use actual source extents, identity filter frame and source-sized canvas.
    pub fn new(draw: ImageDraw, filter: ImageFilter) -> Self {
        let mut request = FilterPlanRequest::new(
            draw.image.size(),
            draw.image.size(),
            FilterTransform::IDENTITY,
        );
        request.source_mip_levels = Self::prepared_source_mips(&draw.image);
        Self {
            draw,
            filter,
            request,
        }
    }
    /// Complete CPU descriptor admission before recording or GPU work.
    pub fn validate(&self) -> Result<(), Error> {
        self.draw.validate()?;
        if self.request.source_size != self.draw.image.size()
            || self.request.source_mip_levels != Self::prepared_source_mips(&self.draw.image)
        {
            return Err(Error::ResourceDomain);
        }
        self.plan().map(|_| ())
    }
    /// Original source-rebound graph; source metadata cannot silently drift.
    pub fn plan(&self) -> Result<ImageFilterPlan, Error> {
        if self.request.source_size != self.draw.image.size()
            || self.request.source_mip_levels != Self::prepared_source_mips(&self.draw.image)
        {
            return Err(Error::ResourceDomain);
        }
        ImageFilterPlan::new(&self.filter, self.request)
    }
    pub(crate) fn prepared_source_mips(source: &ImageSource) -> u32 {
        if source.alpha() != ImageAlpha::Premultiplied {
            return 1;
        }
        match source {
            ImageSource::Pixels(_) => 1,
            #[cfg(feature = "gpu")]
            ImageSource::Gpu(image) => {
                if image.texture().format() == wgpu::TextureFormat::R8Unorm {
                    1
                } else {
                    image.texture().mip_level_count()
                }
            }
        }
    }
    pub(crate) fn needs_normalization(source: &ImageSource) -> bool {
        source.alpha() != ImageAlpha::Premultiplied
            || match source {
                ImageSource::Pixels(image) => image.format() == ImageFormat::R8,
                #[cfg(feature = "gpu")]
                ImageSource::Gpu(image) => image.texture().format() == wgpu::TextureFormat::R8Unorm,
            }
    }
}
#[cfg(feature = "gpu")]
impl ImageFilteredImageDraw {
    /// Check every actual retained source image and nested picture generation,
    /// including branches that an eventual identity/empty result does not paint.
    pub fn preflight_source_devices(&self, device: &wgpu::Device) -> Result<(), Error> {
        fn image(source: &ImageSource, device: &wgpu::Device) -> Result<(), Error> {
            if let ImageSource::Gpu(image) = source {
                if image.device() != device {
                    return Err(Error::ResourceDomain);
                }
            }
            Ok(())
        }
        image(&self.draw.image, device)?;
        let mut visited = std::collections::HashSet::new();
        let mut pending = vec![&self.filter];
        while let Some(filter) = pending.pop() {
            if !visited.insert(filter.identity()) {
                continue;
            }
            if visited.len() > self.request.maximum_steps {
                return Err(Error::Capacity);
            }
            if let Some(input) = filter.input() {
                pending.push(input);
            }
            use super::ImageFilterKind as K;
            match filter.kind() {
                K::Image(data) => image(&data.source.image, device)?,
                K::Picture(source) => source.scene.preflight_source_capture_devices(device)?,
                K::Shader {
                    source: Some(source),
                    ..
                } => source.preflight_source_devices(device)?,
                K::ColorFilter(color_filter) => color_filter.preflight_source_devices(device)?,
                K::Compose { outer, inner } => {
                    pending.push(outer);
                    pending.push(inner);
                }
                K::Arithmetic {
                    background,
                    foreground,
                    ..
                } => {
                    pending.extend(background);
                    pending.extend(foreground);
                }
                K::BlendMode {
                    blender,
                    background,
                    foreground,
                } => {
                    blender.preflight_source_devices(device)?;
                    pending.extend(background);
                    pending.extend(foreground);
                }
                K::DisplacementMap { displacement, .. } => pending.push(displacement),
                K::Merge(children) => {
                    if children.len() > self.request.maximum_steps.saturating_sub(pending.len()) {
                        return Err(Error::Capacity);
                    }
                    pending.extend(children.iter().flatten());
                }
                _ => {}
            }
        }
        Ok(())
    }
}
