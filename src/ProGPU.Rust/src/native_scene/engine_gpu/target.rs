//! Actual target texture/view ownership used by ordered native execution.
use super::*;
use crate::native_scene::{
    engine_resources::{NativeImageTextureOwner, layers::NativeLayerTexture},
    execution::NativeTargetFormat,
};
#[derive(Debug)]
struct Attachment {
    device: wgpu::Device,
    texture: wgpu::Texture,
    view: wgpu::TextureView,
    _producer: Option<Arc<dyn NativeImageTextureOwner>>,
}
/// A retained base-level color attachment. A surface lease or external producer
/// may be retained alongside its actual wgpu texture. No destructor destroys,
/// polls, submits or releases a borrowed producer's texture.
#[derive(Clone, Debug)]
pub struct NativeRenderAttachment(Arc<Attachment>);
impl NativeRenderAttachment {
    pub fn from_texture(
        device: &wgpu::Device,
        texture: &wgpu::Texture,
        producer: Option<Arc<dyn NativeImageTextureOwner>>,
    ) -> Result<Self, Error> {
        admit(texture)?;
        let view = texture.create_view(&wgpu::TextureViewDescriptor {
            label: Some("Native actual base-level render attachment"),
            mip_level_count: Some(1),
            array_layer_count: Some(1),
            ..Default::default()
        });
        Ok(Self(Arc::new(Attachment {
            device: device.clone(),
            texture: texture.clone(),
            view,
            _producer: producer,
        })))
    }
    pub fn from_layer(layer: &NativeLayerTexture) -> Result<Self, Error> {
        admit(layer.texture())?;
        Ok(Self(Arc::new(Attachment {
            device: layer.device().clone(),
            texture: layer.texture().clone(),
            view: layer.view().clone(),
            _producer: Some(Arc::new(layer.clone())),
        })))
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
        [self.0.texture.width(), self.0.texture.height()]
    }
    pub fn format(&self) -> NativeTargetFormat {
        match self.0.texture.format() {
            wgpu::TextureFormat::Rgba8Unorm => NativeTargetFormat::Rgba8Unorm,
            wgpu::TextureFormat::Bgra8Unorm => NativeTargetFormat::Bgra8Unorm,
            wgpu::TextureFormat::Rgba8UnormSrgb => NativeTargetFormat::Rgba8UnormSrgb,
            wgpu::TextureFormat::Bgra8UnormSrgb => NativeTargetFormat::Bgra8UnormSrgb,
            wgpu::TextureFormat::R8Unorm => NativeTargetFormat::R8Unorm,
            _ => unreachable!("admitted native target format"),
        }
    }
    pub fn retain_submitted(&self, queue: &wgpu::Queue) {
        let owner = self.clone();
        queue.on_submitted_work_done(move || drop(owner));
    }
}
fn admit(texture: &wgpu::Texture) -> Result<(), Error> {
    if texture.width() == 0
        || texture.height() == 0
        || texture.dimension() != wgpu::TextureDimension::D2
        || texture.depth_or_array_layers() != 1
        || texture.sample_count() != 1
        || !texture
            .usage()
            .contains(wgpu::TextureUsages::RENDER_ATTACHMENT)
        || !matches!(
            texture.format(),
            wgpu::TextureFormat::Rgba8Unorm
                | wgpu::TextureFormat::Bgra8Unorm
                | wgpu::TextureFormat::Rgba8UnormSrgb
                | wgpu::TextureFormat::Bgra8UnormSrgb
                | wgpu::TextureFormat::R8Unorm
        )
    {
        return Err(Error::InvalidInput);
    }
    Ok(())
}
