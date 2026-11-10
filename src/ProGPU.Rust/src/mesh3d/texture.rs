//! Actual source texture leases, independent of geometry and source wire data.
use super::*;
use crate::{
    image::{GpuImage, ImageAlpha},
    image_effect::ImageEffectTextureGeneration,
};

/// Keep actual external producer state alive through queue completion. Its Drop
/// is the source's once-only release point; no polling, destruction or callback
/// is performed by scene encoding. Browser owners need not be Send/Sync.
#[cfg(not(target_arch = "wasm32"))]
pub trait MeshTextureLeaseOwner: std::fmt::Debug + Send + Sync {}
#[cfg(not(target_arch = "wasm32"))]
impl<T: std::fmt::Debug + Send + Sync> MeshTextureLeaseOwner for T {}
#[cfg(target_arch = "wasm32")]
pub trait MeshTextureLeaseOwner: std::fmt::Debug {}
#[cfg(target_arch = "wasm32")]
impl<T: std::fmt::Debug> MeshTextureLeaseOwner for T {}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum MeshTextureRequest {
    Rgb,
    Planar,
}
/// Read-only physical source facts used before texture acquisition. A producer
/// may return unavailable metadata without pretending its source is empty.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct MeshTextureMetadata {
    pub device: wgpu::Device,
    pub luma: MeshTexturePlaneMetadata,
    pub chroma: Option<MeshTexturePlaneMetadata>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct MeshTexturePlaneMetadata {
    pub size: [u32; 2],
    pub format: wgpu::TextureFormat,
}
impl MeshTextureMetadata {
    pub(crate) fn from_lease(lease: &MeshTextureLease, planar: bool) -> Self {
        let plane = |texture: &wgpu::Texture| MeshTexturePlaneMetadata {
            size: [texture.width(), texture.height()],
            format: texture.format(),
        };
        Self {
            device: lease.device().clone(),
            luma: plane(lease.luma()),
            chroma: if planar {
                lease.chroma().map(plane)
            } else {
                None
            },
        }
    }
}
/// Original context-aware/planar acquisition seam. `None` is the original false
/// capability result and selects the canonical white material fallback.
/// Implementations bridge their actual context/generic/planar source contract;
/// no lease acquisition occurs during encoding.
pub trait MeshTextureSource: MeshTextureLeaseOwner {
    fn supports_planar(&self) -> bool {
        false
    }
    /// Read-only admission facts; this must not acquire or mutate source textures.
    /// Unknown metadata is allowed for ordinary sampling. A potential live blur
    /// needs actual dimensions/formats before the renderer admits its allocations.
    fn metadata(
        &self,
        _device: &wgpu::Device,
        _request: MeshTextureRequest,
    ) -> Option<MeshTextureMetadata> {
        None
    }
    fn acquire(
        &self,
        device: &wgpu::Device,
        request: MeshTextureRequest,
    ) -> Result<Option<MeshTextureLease>, Error>;
}
#[derive(Clone, Debug)]
struct Lease {
    device: wgpu::Device,
    luma: wgpu::Texture,
    chroma: Option<wgpu::Texture>,
    alpha: ImageAlpha,
    owner: Option<Arc<dyn MeshTextureLeaseOwner>>,
    generations: Option<(
        ImageEffectTextureGeneration,
        Option<ImageEffectTextureGeneration>,
    )>,
}
/// Immutable selected physical texture handles and their genuine producer owner.
#[derive(Clone, Debug)]
pub struct MeshTextureLease(Arc<Lease>);
impl MeshTextureLease {
    /// Retain an actual image and all its nested producer/release ownership.
    pub fn from_image(image: GpuImage) -> Self {
        Self(Arc::new(Lease {
            device: image.device().clone(),
            luma: image.texture().clone(),
            chroma: None,
            alpha: image.alpha(),
            owner: Some(Arc::new(image)),
            generations: None,
        }))
    }
    /// Retain the original raw texture plane(s) together with a genuine producer
    /// lease. The supplied device must be their actual creating device; wgpu also
    /// independently enforces this when the selected handles are bound.
    pub fn owned(
        device: &wgpu::Device,
        luma: &wgpu::Texture,
        chroma: Option<&wgpu::Texture>,
        alpha: ImageAlpha,
        owner: Arc<dyn MeshTextureLeaseOwner>,
    ) -> Result<Self, Error> {
        Self::create(device, luma, chroma, alpha, Some(owner))
    }
    /// Explicit borrowed source: native handles are retained, but callers retain
    /// their external source and must not explicitly destroy textures in flight.
    pub fn borrowed(
        device: &wgpu::Device,
        luma: &wgpu::Texture,
        chroma: Option<&wgpu::Texture>,
        alpha: ImageAlpha,
    ) -> Result<Self, Error> {
        Self::create(device, luma, chroma, alpha, None)
    }
    fn create(
        device: &wgpu::Device,
        luma: &wgpu::Texture,
        chroma: Option<&wgpu::Texture>,
        alpha: ImageAlpha,
        owner: Option<Arc<dyn MeshTextureLeaseOwner>>,
    ) -> Result<Self, Error> {
        let sampled = |texture: &wgpu::Texture| {
            texture.dimension() == wgpu::TextureDimension::D2
                && texture.depth_or_array_layers() == 1
                && texture.sample_count() == 1
                && texture
                    .usage()
                    .contains(wgpu::TextureUsages::TEXTURE_BINDING)
        };
        if !sampled(luma) || chroma.is_some_and(|texture| !sampled(texture)) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(Arc::new(Lease {
            device: device.clone(),
            luma: luma.clone(),
            chroma: chroma.cloned(),
            alpha,
            owner,
            generations: None,
        })))
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.0.device
    }
    pub fn luma(&self) -> &wgpu::Texture {
        &self.0.luma
    }
    pub fn chroma(&self) -> Option<&wgpu::Texture> {
        self.0.chroma.as_ref()
    }
    pub fn alpha(&self) -> ImageAlpha {
        self.0.alpha
    }
    pub fn size(&self) -> [u32; 2] {
        [self.0.luma.width(), self.0.luma.height()]
    }
    pub fn generations(
        &self,
    ) -> Option<(
        ImageEffectTextureGeneration,
        Option<ImageEffectTextureGeneration>,
    )> {
        self.0.generations
    }
    /// Retain actual content/view revisions. Missing metadata disables reuse; it
    /// never invents a stable source generation for mutable producer pixels.
    pub fn with_generations(
        &self,
        luma: ImageEffectTextureGeneration,
        chroma: Option<ImageEffectTextureGeneration>,
    ) -> Result<Self, Error> {
        if self.0.chroma.is_some() != chroma.is_some() {
            return Err(Error::InvalidInput);
        }
        let mut value = (*self.0).clone();
        value.generations = Some((luma, chroma));
        Ok(Self(Arc::new(value)))
    }
    /// Called immediately after submitting all commands that sample this lease.
    /// Actual callback owners survive even if the prepared scene is then dropped.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        if let Some(owner) = self.0.owner.clone() {
            queue.on_submitted_work_done(move || drop(owner));
        }
    }
}
impl MeshTextureSource for MeshTextureLease {
    fn supports_planar(&self) -> bool {
        self.0.chroma.is_some()
    }
    fn metadata(
        &self,
        _device: &wgpu::Device,
        request: MeshTextureRequest,
    ) -> Option<MeshTextureMetadata> {
        Some(MeshTextureMetadata::from_lease(
            self,
            request == MeshTextureRequest::Planar,
        ))
    }
    fn acquire(
        &self,
        _device: &wgpu::Device,
        request: MeshTextureRequest,
    ) -> Result<Option<MeshTextureLease>, Error> {
        if request == MeshTextureRequest::Planar {
            return Ok(self.0.chroma.is_some().then(|| self.clone()));
        }
        if self.0.chroma.is_none() {
            return Ok(Some(self.clone()));
        }
        let mut value = (*self.0).clone();
        value.chroma = None;
        if let Some((luma, _)) = value.generations {
            value.generations = Some((luma, None));
        }
        Ok(Some(Self(Arc::new(value))))
    }
}
