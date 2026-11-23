//! Read-only whole-request admission before any texture source acquisition.
use super::*;
#[derive(Clone, Debug)]
pub(super) struct MaterialCost {
    pub request: MeshTextureRequest,
    pub bytes: u64,
    pub passes: u32,
}
/// Frozen resource admission for a whole scene preflight. Acquisition may select
/// smaller/no source work but cannot exceed these captured physical source costs.
#[derive(Clone, Debug)]
pub struct Mesh3DResourcePlan {
    device: wgpu::Device,
    viewport: Arc<Mesh3DViewport>,
    size: [u32; 2],
    samples: u32,
    pub(super) materials: Vec<MaterialCost>,
    bytes: u64,
    passes: u32,
}
impl Mesh3DResourcePlan {
    pub fn cost(&self) -> (u64, u32) {
        (self.bytes, self.passes)
    }
    pub(super) fn admits(
        &self,
        request: &Mesh3DRequest,
        device: &wgpu::Device,
    ) -> Result<(), Error> {
        if self.device != *device
            || !Arc::ptr_eq(&self.viewport, &request.viewport)
            || self.size != request.size
            || self.samples != request.effective_sample_count()
            || self.materials.len() != request.viewport.meshes.len()
        {
            return Err(Error::ResourceDomain);
        }
        Ok(())
    }
}
impl Mesh3DRequest {
    /// Count actual target storage and every potential original live-material
    /// Gaussian pair without acquiring source leases or mutating GPU resources.
    pub fn resource_cost(&self, device: &wgpu::Device) -> Result<(u64, u32), Error> {
        Ok(self.resource_plan(device)?.cost())
    }
    /// Capture metadata once for whole-scene admission; pass this plan to
    /// `Mesh3DEngine::prepare_with_plan` after the full scene budget succeeds.
    pub fn resource_plan(&self, device: &wgpu::Device) -> Result<Mesh3DResourcePlan, Error> {
        self.validate_base(device)?;
        let samples = self.effective_sample_count();
        let mut bytes = u64::from(self.size[0])
            .checked_mul(u64::from(self.size[1]))
            .and_then(|v| {
                v.checked_mul(
                    4 * (1 + u64::from(samples) + if samples > 1 { u64::from(samples) } else { 0 }),
                )
            })
            .ok_or(Error::Capacity)?;
        let mut passes = 1u32;
        let mut materials = Vec::new();
        materials
            .try_reserve_exact(self.viewport.meshes.len())
            .map_err(|_| Error::Capacity)?;
        for mesh in self.viewport.meshes.iter() {
            let planar = mesh.yuv_conversion.is_some()
                && mesh
                    .texture_source
                    .as_ref()
                    .is_some_and(|source| source.supports_planar());
            let request = if planar {
                MeshTextureRequest::Planar
            } else {
                MeshTextureRequest::Rgb
            };
            let (cost, work) = if let Some(source) = &mesh.texture_source {
                let sigma = mesh.texture_effect.blur_sigma;
                if sigma.is_finite() && sigma > 0.01 && sigma <= 32.0 {
                    let metadata = source
                        .metadata(device, request)
                        .ok_or(Error::UnsupportedExecution)?;
                    blur_cost(
                        device,
                        &metadata,
                        if planar { mesh.yuv_conversion } else { None },
                        sigma,
                    )?
                } else {
                    (0, 0)
                }
            } else {
                (0, 0)
            };
            bytes = bytes.checked_add(cost).ok_or(Error::Capacity)?;
            passes = passes.checked_add(work).ok_or(Error::Capacity)?;
            materials.push(MaterialCost {
                request,
                bytes: cost,
                passes: work,
            });
        }
        Ok(Mesh3DResourcePlan {
            device: device.clone(),
            viewport: self.viewport.clone(),
            size: self.size,
            samples,
            materials,
            bytes,
            passes,
        })
    }
}
pub(super) fn blur_cost(
    device: &wgpu::Device,
    metadata: &MeshTextureMetadata,
    conversion: Option<crate::image_effect::YuvConversion>,
    sigma: f32,
) -> Result<(u64, u32), Error> {
    if metadata.device != *device || !sigma.is_finite() || sigma <= 0.01 || sigma > 32.0 {
        return Ok((0, 0));
    }
    let source = metadata.luma;
    let bpp = if let Some(conversion) = conversion {
        let Some(chroma) = metadata.chroma else {
            return Ok((0, 0));
        };
        let formats = (source.format == wgpu::TextureFormat::R8Unorm
            && chroma.format == wgpu::TextureFormat::Rg8Unorm)
            || (device
                .features()
                .contains(wgpu::Features::TEXTURE_FORMAT_16BIT_NORM)
                && source.format == wgpu::TextureFormat::R16Unorm
                && chroma.format == wgpu::TextureFormat::Rg16Unorm);
        if !formats || chroma.size != source.size.map(|v| v.div_ceil(2)) {
            return Ok((0, 0));
        }
        conversion.validate()?;
        8
    } else {
        match source.format {
            wgpu::TextureFormat::Rgba16Float => 8,
            wgpu::TextureFormat::Rgba8Unorm
            | wgpu::TextureFormat::Rgba8UnormSrgb
            | wgpu::TextureFormat::Bgra8Unorm
            | wgpu::TextureFormat::Bgra8UnormSrgb => 4,
            _ => return Ok((0, 0)),
        }
    };
    if source.size.contains(&0)
        || source
            .size
            .iter()
            .any(|v| *v > device.limits().max_texture_dimension_2d)
    {
        return Err(Error::InvalidInput);
    }
    let one = u64::from(source.size[0])
        .checked_mul(u64::from(source.size[1]))
        .and_then(|v| v.checked_mul(bpp))
        .ok_or(Error::Capacity)?;
    // Existing original Gaussian engine admission applies to each actual target.
    if one > 256 * 1024 * 1024 {
        return Err(Error::Capacity);
    }
    Ok((one.checked_mul(2).ok_or(Error::Capacity)?, 2))
}
