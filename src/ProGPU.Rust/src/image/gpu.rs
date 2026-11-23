//! Actual device-bound texture ownership; no explicit destruction on Drop.
use super::*;
/// Shared actual texture handle. External pixel mutation stays with its producer;
/// this wrapper retains lifetime and alpha interpretation, not a mutable snapshot.
#[derive(Clone, Debug)]
pub struct GpuImage {
    owner: Arc<()>,
    device: wgpu::Device,
    texture: wgpu::Texture,
    alpha: ImageAlpha,
    producer: Option<Arc<dyn GpuImageOwner>>,
}
#[cfg(not(target_arch = "wasm32"))]
pub(crate) trait GpuImageOwner: std::fmt::Debug + Send + Sync {}
#[cfg(not(target_arch = "wasm32"))]
impl<T: std::fmt::Debug + Send + Sync> GpuImageOwner for T {}
#[cfg(target_arch = "wasm32")]
pub(crate) trait GpuImageOwner: std::fmt::Debug {}
#[cfg(target_arch = "wasm32")]
impl<T: std::fmt::Debug> GpuImageOwner for T {}
impl From<GpuImage> for ImageSource {
    fn from(value: GpuImage) -> Self {
        Self::Gpu(value)
    }
}
impl GpuImage {
    /// Retain a same-device sampled 2D texture without taking explicit destruction
    /// ownership. Caller supplies its actual creating device; wgpu independently
    /// validates device identity when resources are bound. Never wrap a texture
    /// whose external owner will explicitly destroy it during retained use.
    pub fn from_texture(
        device: &wgpu::Device,
        texture: &wgpu::Texture,
        alpha: ImageAlpha,
    ) -> Result<Self, Error> {
        if texture.dimension() != wgpu::TextureDimension::D2
            || texture.depth_or_array_layers() != 1
            || texture.sample_count() != 1
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::TEXTURE_BINDING)
            || !matches!(
                texture.format(),
                wgpu::TextureFormat::R8Unorm
                    | wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
                    | wgpu::TextureFormat::Rgba16Float
            )
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            owner: Arc::new(()),
            device: device.clone(),
            texture: texture.clone(),
            alpha,
            producer: None,
        })
    }
    /// Actual physical texture extent.
    pub fn size(&self) -> [u32; 2] {
        [self.texture.width(), self.texture.height()]
    }
    /// Original alpha representation.
    pub fn alpha(&self) -> ImageAlpha {
        self.alpha
    }
    /// Actual sampled resource, kept alive by this owner and prepared bindings.
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }
    /// Actual mip inventory, independent of requested draw filter.
    pub fn mip_level_count(&self) -> u32 {
        self.texture.mip_level_count()
    }
    pub(crate) fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub(crate) fn identity(&self) -> usize {
        Arc::as_ptr(&self.owner) as usize
    }
    pub(crate) fn with_producer(mut self, owner: Arc<dyn GpuImageOwner>) -> Self {
        self.producer = Some(owner);
        self
    }
    /// Retain the actual external producer through all previously submitted work.
    /// Call immediately after submitting commands that use this image. Prepared
    /// drawing automatically performs this step at its submission boundary.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        if let Some(owner) = self.producer.clone() {
            queue.on_submitted_work_done(move || drop(owner));
        }
    }
}
