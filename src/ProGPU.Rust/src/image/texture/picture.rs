//! Explicit exclusive original picture-compositor cache, keyed by actual target
//! format within one retained context. It owns no process-global or UI source.
use super::*;
use std::collections::HashMap;
/// Reusable source-capture session for one actual image context. Distinct target
/// formats own distinct renderer/pipeline/cache identities. Completed image
/// generations remain immutable and independent of subsequent session captures.
pub struct PictureCaptureSession {
    context: ImageContext,
    renderers: HashMap<wgpu::TextureFormat, crate::Renderer>,
}
impl PictureCaptureSession {
    /// Retain the original context. GPU pipeline creation remains lazy.
    pub fn new(context: &ImageContext) -> Self {
        Self {
            context: context.clone(),
            renderers: HashMap::new(),
        }
    }
    /// Actual context owner used by every cached compositor.
    pub fn context(&self) -> &ImageContext {
        &self.context
    }
    /// Capture the complete actual source recording to transparent storage. The
    /// original RGBA8/F16 distinction is preserved before drawing begins.
    pub async fn capture(
        &mut self,
        picture: impl Into<Arc<crate::Scene>>,
        size: [u32; 2],
        matrix: crate::vector::Matrix4,
        format: wgpu::TextureFormat,
    ) -> Result<TextureImage, ImageResourceError> {
        self.context.admit()?;
        if !matches!(
            format,
            wgpu::TextureFormat::Rgba8Unorm | wgpu::TextureFormat::Rgba16Float
        ) {
            return Err(ImageResourceError::TextureContract);
        }
        let frame = crate::composition::SourceRenderFrame::image_filter(size, matrix)?;
        self.renderers.try_reserve(1).map_err(|_| Error::Capacity)?;
        // Detached exclusive candidate: cancellation retires only this active
        // renderer; it cannot publish a partially prepared cached generation.
        let mut renderer = match self.renderers.remove(&format) {
            Some(renderer) => renderer,
            None => {
                crate::Renderer::new(self.context.device(), self.context.queue(), format).await?
            }
        };
        let result = renderer.capture_source_scene(picture, frame).await;
        self.renderers.insert(format, renderer);
        let capture = result?;
        let mut info = ImageInfo::new(size[0] as i32, size[1] as i32);
        if format == wgpu::TextureFormat::Rgba16Float {
            info.color_type = PixelColorType::RgbaF16;
        }
        TextureImage::from_texture(
            &self.context,
            capture.image().clone(),
            info,
            TextureOwnership::Owned,
            None,
        )
    }
}
