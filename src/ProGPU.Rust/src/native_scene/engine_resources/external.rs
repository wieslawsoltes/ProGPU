//! Actual device-bound image views and exact ordered native sideband identity.
use super::*;
#[cfg(not(target_arch = "wasm32"))]
pub trait NativeImageTextureOwner: std::fmt::Debug + Send + Sync {}
#[cfg(not(target_arch = "wasm32"))]
impl<T: std::fmt::Debug + Send + Sync> NativeImageTextureOwner for T {}
#[cfg(target_arch = "wasm32")]
pub trait NativeImageTextureOwner: std::fmt::Debug {}
#[cfg(target_arch = "wasm32")]
impl<T: std::fmt::Debug> NativeImageTextureOwner for T {}
#[derive(Debug)]
struct TextureStorage {
    device: wgpu::Device,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    size: [u32; 2],
    _producer: Option<Arc<dyn NativeImageTextureOwner>>,
}
/// Retains the actual selected view, including nondefault mip views and planar
/// formats. Declared view extent is source metadata; no view is reconstructed.
#[derive(Clone, Debug)]
pub struct NativeImageTexture(Arc<TextureStorage>);
impl NativeImageTexture {
    pub fn from_view(
        device: &wgpu::Device,
        texture: &wgpu::Texture,
        view: &wgpu::TextureView,
        size: [u32; 2],
        producer: Option<Arc<dyn NativeImageTextureOwner>>,
    ) -> Result<Self, Error> {
        if size.into_iter().any(|n| !(1..=16384).contains(&n))
            || texture.dimension() != wgpu::TextureDimension::D2
            || texture.sample_count() != 1
            || !texture
                .usage()
                .contains(wgpu::TextureUsages::TEXTURE_BINDING)
        {
            return Err(Error::InvalidInput);
        }
        Ok(Self(Arc::new(TextureStorage {
            device: device.clone(),
            texture: texture.clone(),
            view: view.clone(),
            size,
            _producer: producer,
        })))
    }
    pub fn from_image(image: &crate::image::GpuImage) -> Result<Self, Error> {
        Self::from_view(
            image.device(),
            image.texture(),
            &image.texture().create_view(&Default::default()),
            image.size(),
            Some(Arc::new(image.clone())),
        )
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.0.device
    }
    pub fn texture(&self) -> &wgpu::Texture {
        &self.0.texture
    }
    pub fn view(&self) -> &wgpu::TextureView {
        &self.0.view
    }
    pub fn size(&self) -> [u32; 2] {
        self.0.size
    }
    pub fn same_view(&self, other: &Self) -> bool {
        self.0.view == other.0.view
    }
    /// Retain source release owners and actual handles through completion of all
    /// work submitted before this call, independently of prepared-page lifetime.
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.0.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
#[derive(Clone, Debug)]
pub struct NativeExternalImageBinding {
    pub info: NativeExternalImageInfo,
    pub image: NativeImageTexture,
}
#[derive(Clone, Debug)]
pub struct NativeExternalImages {
    device: wgpu::Device,
    bindings: Arc<[NativeExternalImageBinding]>,
    info: Arc<[NativeExternalImageInfo]>,
}
impl NativeExternalImages {
    pub fn new(
        device: &wgpu::Device,
        bindings: Vec<NativeExternalImageBinding>,
    ) -> Result<Self, Error> {
        let mut info = allocate(bindings.len())?;
        for b in &bindings {
            if b.image.device() != device || b.info.size != b.image.size() {
                return Err(Error::ResourceDomain);
            }
            info.push(b.info);
        }
        if !external_info_valid(&info) {
            return Err(Error::InvalidInput);
        }
        Ok(Self {
            device: device.clone(),
            bindings: bindings.into(),
            info: info.into(),
        })
    }
    pub fn device(&self) -> &wgpu::Device {
        &self.device
    }
    pub fn bindings(&self) -> &[NativeExternalImageBinding] {
        &self.bindings
    }
    pub fn metadata(&self) -> &[NativeExternalImageInfo] {
        &self.info
    }
    pub fn find(
        &self,
        id: u64,
        generation: u64,
        role: NativeImageRole,
    ) -> Option<&NativeExternalImageBinding> {
        self.bindings
            .get(external_slot(&self.info, id, generation, role)?)
    }
    /// Original binding update identity includes every actual view and extent.
    /// A changed table invalidates image, glyph-paint and 3D execution caches.
    pub fn same_bindings(&self, other: &Self) -> bool {
        self.device == other.device
            && self.info == other.info
            && self
                .bindings
                .iter()
                .zip(other.bindings.iter())
                .all(|(a, b)| a.image.same_view(&b.image))
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
