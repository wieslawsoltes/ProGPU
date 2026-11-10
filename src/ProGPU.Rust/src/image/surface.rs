//! Original SKSurface target/flush/snapshot lifecycle on retained Rust scenes.
use super::texture::{
    self, ImageContext, ImageResourceError, TextureImage, TextureOwnership, TextureRelease,
};
use super::{
    GpuImage, ImageAlpha, ImageInfo, PixelAlphaType, PixelColorType, Pixmap, PixmapMut,
    RasterExecution,
};
use crate::{
    Error, Renderer, Scene, composition::SourceRenderFrame, geometry::Affine, vector::Matrix4,
};
mod pixels;
/// Original independent surface origin, applied to drawing rather than readback.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum SurfaceOrigin {
    /// Top-left source coordinates.
    #[default]
    TopLeft,
    /// Original scale(1,-1) followed by translation(0,height).
    BottomLeft,
}
/// Original source pixel geometry metadata. This does not select a font rasterizer.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u32)]
pub enum SurfacePixelGeometry {
    /// No channel geometry.
    Unknown = 0,
    /// Horizontal RGB.
    #[default]
    RgbHorizontal = 1,
    /// Horizontal BGR.
    BgrHorizontal = 2,
    /// Vertical RGB.
    RgbVertical = 3,
    /// Vertical BGR.
    BgrVertical = 4,
}
/// Immutable original raw surface flags and independent pixel geometry.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SurfaceProperties {
    /// Original unnormalized flags; bit zero requests device-independent fonts.
    pub flags: u32,
    /// Original independent device pixel order.
    pub pixel_geometry: SurfacePixelGeometry,
}
impl Default for SurfaceProperties {
    fn default() -> Self {
        Self {
            flags: 0,
            pixel_geometry: SurfacePixelGeometry::RgbHorizontal,
        }
    }
}
impl SurfaceProperties {
    /// Original flags query; metadata does not qualify a source font policy.
    pub fn uses_device_independent_fonts(self) -> bool {
        self.flags & 1 != 0
    }
}
/// Actual submitted flush outcome. Empty/null recordings do not submit GPU work.
#[derive(Debug, Default)]
pub struct SurfaceFlush {
    /// Actual submission, if any commands reached the GPU.
    pub submission: Option<wgpu::SubmissionIndex>,
    /// Whether CPU-backed bytes were updated.
    pub copied_to_cpu: bool,
}
/// Surface CPU storage remains a real exclusive borrow or actual owned bytes.
/// No invented native pointer aliases are exposed by this safe implementation.
enum CpuStorage<'a> {
    Borrowed(&'a mut [u8]),
    Owned(Vec<u8>),
}
impl CpuStorage<'_> {
    fn bytes(&self) -> &[u8] {
        match self {
            Self::Borrowed(v) => v,
            Self::Owned(v) => v,
        }
    }
    fn bytes_mut(&mut self) -> &mut [u8] {
        match self {
            Self::Borrowed(v) => v,
            Self::Owned(v) => v,
        }
    }
}
/// Mutable retained drawing target. Dropping an abandoned surface only retires
/// actual owned handles and the once-only CPU callback: it never draws, flushes,
/// polls or unmaps. Explicit close() performs the original flush-before-release.
pub struct RenderSurface<'pixels> {
    context: Option<ImageContext>,
    info: ImageInfo,
    texture: Option<wgpu::Texture>,
    texture_producer: Option<GpuImage>,
    owns_texture: bool,
    alpha: ImageAlpha,
    has_contents: bool,
    origin: SurfaceOrigin,
    properties: SurfaceProperties,
    scene: Vec<Scene>,
    renderer: Option<Renderer>,
    generation: Option<TextureImage>,
    cpu: Option<CpuStorage<'pixels>>,
    row_bytes: i32,
    readback: Option<texture::TextureReadback>,
    readback_source: Option<TextureImage>,
    release: Option<TextureRelease>,
    readback_regions: Option<Vec<[f32; 4]>>,
}
impl<'pixels> RenderSurface<'pixels> {
    /// Create the original RGBA8 premultiplied offscreen target. Source color and
    /// alpha metadata remain independent of its actual GPU storage encoding.
    pub fn new(context: &ImageContext, info: ImageInfo) -> Result<Self, ImageResourceError> {
        Self::new_with_options(
            context,
            info,
            SurfaceOrigin::TopLeft,
            SurfaceProperties::default(),
            1,
            false,
        )
    }
    /// Complete original sample-count/origin/mipmap/properties allocation policy.
    /// Sample counts zero and one both select the original single-sample target.
    pub fn new_with_options(
        context: &ImageContext,
        info: ImageInfo,
        origin: SurfaceOrigin,
        properties: SurfaceProperties,
        samples: u32,
        mipmapped: bool,
    ) -> Result<Self, ImageResourceError> {
        if !matches!(samples, 0 | 1) || info.is_empty() {
            return Err(ImageResourceError::TextureContract);
        }
        let size = [info.width as u32, info.height as u32];
        let texture = texture::allocate_texture(
            context,
            size,
            wgpu::TextureFormat::Rgba8Unorm,
            if mipmapped {
                texture::mip_levels(size)
            } else {
                1
            },
            true,
        )?;
        Ok(Self::base(
            Some(context.clone()),
            info,
            Some(texture),
            true,
            origin,
            properties,
        ))
    }
    /// Borrow an actual renderer target, including render-only textures. Device
    /// ownership is supplied explicitly; wgpu validates the actual handle domain.
    pub fn from_texture(
        context: &ImageContext,
        target: &wgpu::Texture,
        info: ImageInfo,
        origin: SurfaceOrigin,
        properties: SurfaceProperties,
    ) -> Result<Self, ImageResourceError> {
        context.admit()?;
        if info.is_empty()
            || target.dimension() != wgpu::TextureDimension::D2
            || target.depth_or_array_layers() != 1
            || target.sample_count() != 1
            || [target.width(), target.height()] != [info.width as u32, info.height as u32]
            || !target
                .usage()
                .contains(wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC)
            || !matches!(
                target.format(),
                wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
            )
        {
            return Err(ImageResourceError::TextureContract);
        }
        Ok(Self::base(
            Some(context.clone()),
            info,
            Some(target.clone()),
            false,
            origin,
            properties,
        ))
    }
    /// Borrow a typed sampled source and keep its actual external release lease.
    pub fn from_gpu_image(
        context: &ImageContext,
        image: &GpuImage,
        info: ImageInfo,
        origin: SurfaceOrigin,
        properties: SurfaceProperties,
    ) -> Result<Self, ImageResourceError> {
        if image.device() != context.device() {
            return Err(Error::ResourceDomain.into());
        }
        let mut surface = Self::from_texture(context, image.texture(), info, origin, properties)?;
        surface.alpha = image.alpha();
        surface.texture_producer = Some(image.clone());
        Ok(surface)
    }
    /// Original CPU-backed source, with exclusive borrowed storage and optional
    /// once-only callback. Initial bytes upload using original surface conversion.
    pub fn from_pixels(
        context: &ImageContext,
        info: ImageInfo,
        row_bytes: i32,
        pixels: &'pixels mut [u8],
        release: Option<TextureRelease>,
        execution: RasterExecution,
    ) -> Result<Self, ImageResourceError> {
        let stride = if row_bytes > 0 {
            row_bytes
        } else {
            info.row_bytes()?
        };
        Pixmap::new(info.clone(), stride, pixels)?;
        let bytes = pixels::upload_pixels(&info, stride, pixels, execution)?;
        let mut surface = Self::new(context, info)?;
        let texture = surface
            .texture
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        context.queue().write_texture(
            wgpu::TexelCopyTextureInfo {
                texture,
                mip_level: 0,
                origin: wgpu::Origin3d::ZERO,
                aspect: wgpu::TextureAspect::All,
            },
            &bytes,
            wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(texture.width() * 4),
                rows_per_image: Some(texture.height()),
            },
            texture.size(),
        );
        surface.cpu = Some(CpuStorage::Borrowed(pixels));
        surface.row_bytes = stride;
        surface.release = release;
        surface.has_contents = true;
        Ok(surface)
    }
    /// Original null drawing surface: retains and clears commands without GPU work.
    pub fn null(size: [u32; 2]) -> Result<Self, ImageResourceError> {
        if size.contains(&0) || size.iter().any(|&v| v > i32::MAX as u32) {
            return Err(ImageResourceError::Bounds);
        }
        Ok(Self::base(
            None,
            ImageInfo::new(size[0] as i32, size[1] as i32),
            None,
            false,
            SurfaceOrigin::TopLeft,
            SurfaceProperties::default(),
        ))
    }
    fn base(
        context: Option<ImageContext>,
        info: ImageInfo,
        texture: Option<wgpu::Texture>,
        owns_texture: bool,
        origin: SurfaceOrigin,
        properties: SurfaceProperties,
    ) -> Self {
        let has_contents = texture.is_some() && !owns_texture;
        Self {
            context,
            info,
            texture,
            texture_producer: None,
            owns_texture,
            alpha: ImageAlpha::Premultiplied,
            has_contents,
            origin,
            properties,
            scene: Vec::new(),
            renderer: None,
            generation: None,
            cpu: None,
            row_bytes: 0,
            readback: None,
            readback_source: None,
            release: None,
            readback_regions: None,
        }
    }
    /// Original declared surface metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual source origin, independent of CPU row order.
    pub fn origin(&self) -> SurfaceOrigin {
        self.origin
    }
    /// Original immutable source properties.
    pub fn properties(&self) -> SurfaceProperties {
        self.properties
    }
    /// Whether commands await a flush.
    pub fn has_pending_commands(&self) -> bool {
        !self.scene.is_empty()
    }
    /// Actual backing texture, borrowed; mutation must use the surface API while
    /// snapshots exist. External explicit destruction is never permitted here.
    pub fn texture(&self) -> Option<&wgpu::Texture> {
        self.texture.as_ref()
    }
    /// An existing backing generation is available as a layer backdrop only after
    /// actual initialization or a completed submission of original drawing.
    pub fn backdrop_texture(&self) -> Option<&wgpu::Texture> {
        if self.has_contents {
            self.texture.as_ref()
        } else {
            None
        }
    }
    /// Append a balanced frozen recording. The first actual command reclaims a
    /// sole snapshot generation or copies its original complete base-level pixels.
    pub fn record(&mut self, scene: &Scene) -> Result<(), ImageResourceError> {
        if scene.is_empty() {
            return Ok(());
        }
        let mut candidate = Scene::default();
        candidate.append(scene)?;
        self.scene.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.prepare_write()?;
        self.scene.push(candidate);
        Ok(())
    }
    /// Original CPU readback region tracking: None copies the whole surface;
    /// Some(empty) copies no bytes. Each edge rounds independently at flush.
    pub fn set_readback_regions(
        &mut self,
        regions: Option<Vec<[f32; 4]>>,
    ) -> Result<(), ImageResourceError> {
        if regions
            .as_ref()
            .is_some_and(|r| r.iter().flatten().any(|v| !v.is_finite()))
        {
            return Err(Error::InvalidInput.into());
        }
        self.readback_regions = regions;
        Ok(())
    }
    fn prepare_write(&mut self) -> Result<(), ImageResourceError> {
        if let Some(generation) = &self.generation {
            if self.owns_texture && !generation.can_reclaim() {
                let context = self
                    .context
                    .as_ref()
                    .ok_or(ImageResourceError::TextureContract)?;
                let old = self
                    .texture
                    .as_ref()
                    .ok_or(ImageResourceError::TextureContract)?;
                let next = texture::allocate_texture(
                    context,
                    [old.width(), old.height()],
                    old.format(),
                    1,
                    true,
                )?;
                let mut encoder =
                    context
                        .device()
                        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                            label: Some("ProGPU surface snapshot copy-on-write"),
                        });
                copy_base(&mut encoder, old, &next);
                context.queue().submit([encoder.finish()]);
                generation.gpu_image().retain_submitted(context.queue());
                self.texture = Some(next);
                self.readback_source = None;
            }
            self.generation = None;
        }
        self.readback = None;
        Ok(())
    }
    /// Flush using original initialized-target load/clear policy. Commands and
    /// command-retained owners are retired even if preparation or encoding fails.
    pub async fn flush(&mut self) -> Result<SurfaceFlush, ImageResourceError> {
        self.flush_core(true).await
    }
    /// Presentation path flushes drawing without copying back into CPU storage.
    pub async fn flush_for_presentation(&mut self) -> Result<SurfaceFlush, ImageResourceError> {
        self.flush_core(false).await
    }
    async fn flush_core(&mut self, copy_to_cpu: bool) -> Result<SurfaceFlush, ImageResourceError> {
        let pending = std::mem::take(&mut self.scene);
        if self.texture.is_none() || pending.is_empty() {
            return Ok(SurfaceFlush::default());
        }
        let regions = self.readback_regions.take();
        let mut scene = Scene::default();
        for source in pending {
            scene.append(&source)?;
        }
        let context = self
            .context
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?
            .clone();
        context.admit()?;
        let texture = self
            .texture
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?
            .clone();
        if self.renderer.is_none() {
            self.renderer =
                Some(Renderer::new(context.device(), context.queue(), texture.format()).await?);
        }
        let matrix = if self.origin == SurfaceOrigin::BottomLeft {
            Matrix4::from_affine(Affine([1.0, 0.0, 0.0, -1.0, 0.0, self.info.height as f32]))
        } else {
            Matrix4::IDENTITY
        };
        let frame = SourceRenderFrame::image_filter([texture.width(), texture.height()], matrix)?;
        let renderer = self
            .renderer
            .as_mut()
            .ok_or(ImageResourceError::TextureContract)?;
        let prepared = renderer.prepare_source_scene(&scene, frame).await?;
        let mut encoder =
            context
                .device()
                .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                    label: Some("ProGPU explicit surface flush"),
                });
        if self.has_contents {
            renderer.encode_loaded(&mut encoder, &prepared, &texture)?;
        } else {
            let view = texture.create_view(&wgpu::TextureViewDescriptor {
                mip_level_count: Some(1),
                ..Default::default()
            });
            renderer.encode(&mut encoder, &prepared, &view, wgpu::Color::TRANSPARENT)?;
        }
        let submission = renderer.submit_encoded_scene(&prepared, encoder)?;
        if let Some(producer) = &self.texture_producer {
            producer.retain_submitted(context.queue());
        }
        self.has_contents = true;
        let mut result = SurfaceFlush {
            submission: Some(submission),
            copied_to_cpu: false,
        };
        if copy_to_cpu && self.cpu.is_some() {
            self.update_cpu(regions).await?;
            result.copied_to_cpu = true;
        }
        Ok(result)
    }
    /// Full immutable snapshot sharing one owned backing generation.
    pub async fn snapshot(&mut self) -> Result<TextureImage, ImageResourceError> {
        self.snapshot_subset([0, 0, self.info.width as u32, self.info.height as u32])
            .await
    }
    /// Original snapshot allocation policy: owned targets share one generation;
    /// borrowed targets make a new immutable complete copy for every request.
    pub async fn snapshot_subset(
        &mut self,
        bounds: [u32; 4],
    ) -> Result<TextureImage, ImageResourceError> {
        let [l, t, r, b] = bounds;
        if r <= l || b <= t || r > self.info.width as u32 || b > self.info.height as u32 {
            return Err(ImageResourceError::Bounds);
        }
        self.flush().await?;
        let context = self
            .context
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        let target = self
            .texture
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        if self.generation.is_none() || !self.owns_texture {
            let actual = if self.owns_texture {
                target.clone()
            } else {
                let copy = texture::allocate_texture(
                    context,
                    [target.width(), target.height()],
                    target.format(),
                    1,
                    false,
                )?;
                let mut encoder =
                    context
                        .device()
                        .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                            label: Some("ProGPU borrowed surface snapshot"),
                        });
                copy_base(&mut encoder, target, &copy);
                context.queue().submit([encoder.finish()]);
                if let Some(owner) = &self.texture_producer {
                    owner.retain_submitted(context.queue());
                }
                copy
            };
            let image = GpuImage::from_texture(context.device(), &actual, self.alpha)?;
            let portable = self
                .readback
                .as_ref()
                .filter(|_| self.cpu.is_some())
                .map(|r| r.whole_bytes().clone());
            let generation = TextureImage::create_surface_generation(
                context.clone(),
                image,
                self.info.clone(),
                portable,
            );
            if !self.owns_texture {
                return generation.subset(bounds);
            }
            self.generation = Some(generation);
            self.readback = None;
        }
        self.generation
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?
            .subset(bounds)
    }
    async fn native_readback(&mut self) -> Result<texture::TextureReadback, ImageResourceError> {
        if let Some(source) = &self.readback_source {
            return source
                .readback(texture::ImageCachingHint::Disallow)?
                .complete_host()
                .await;
        }
        let context = self
            .context
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        let target = self
            .texture
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        let reusable = target
            .usage()
            .contains(wgpu::TextureUsages::TEXTURE_BINDING);
        // Render-only targets cannot be wrapped as sampled images. Copy only in
        // that explicit backend case; ordinary targets read their actual bytes.
        let image = if target
            .usage()
            .contains(wgpu::TextureUsages::TEXTURE_BINDING)
        {
            GpuImage::from_texture(context.device(), target, self.alpha)?
        } else {
            let copy = texture::allocate_texture(
                context,
                [target.width(), target.height()],
                target.format(),
                1,
                false,
            )?;
            let mut encoder = context.device().create_command_encoder(&Default::default());
            copy_base(&mut encoder, target, &copy);
            context.queue().submit([encoder.finish()]);
            if let Some(owner) = &self.texture_producer {
                owner.retain_submitted(context.queue());
            }
            GpuImage::from_texture(context.device(), &copy, self.alpha)?
        };
        let source = TextureImage::from_texture(
            context,
            image,
            self.info.clone(),
            TextureOwnership::Borrowed,
            None,
        )?;
        let readback = source
            .readback(texture::ImageCachingHint::Disallow)?
            .complete_host()
            .await?;
        if reusable {
            self.readback_source = Some(source);
        }
        Ok(readback)
    }
    async fn update_cpu(
        &mut self,
        regions: Option<Vec<[f32; 4]>>,
    ) -> Result<(), ImageResourceError> {
        let readback = self.native_readback().await?;
        if let Some(cpu) = &mut self.cpu {
            pixels::copy_readback(
                &self.info,
                self.row_bytes,
                cpu.bytes_mut(),
                &readback,
                regions.as_deref(),
                RasterExecution::default(),
            )?;
        }
        self.readback = Some(readback);
        Ok(())
    }
    /// Expose a safe CPU view with the original flush and materialization policy.
    /// The exclusive surface borrow keeps storage and source owners alive.
    pub async fn peek_pixels(&mut self) -> Result<Option<Pixmap<'_>>, ImageResourceError> {
        if self.texture.is_none() {
            return Ok(None);
        }
        self.flush().await?;
        if self.cpu.is_none() {
            self.row_bytes = self.info.row_bytes()?;
            let mut bytes = super::raster::zeros(self.info.byte_count(self.row_bytes)?)?;
            let readback = self.native_readback().await?;
            if !readback.pixmap()?.read_pixels(
                &mut PixmapMut::new(self.info.clone(), self.row_bytes, &mut bytes)?,
                [0, 0],
                RasterExecution::default(),
            )? {
                return Err(super::RasterError::UnsupportedFormat.into());
            }
            self.cpu = Some(CpuStorage::Owned(bytes));
            self.readback = Some(readback);
        }
        let cpu = self
            .cpu
            .as_ref()
            .ok_or(ImageResourceError::TextureContract)?;
        Ok(Some(Pixmap::new(
            self.info.clone(),
            self.row_bytes,
            cpu.bytes(),
        )?))
    }
    /// Original CPU-backed source precedence over direct texture readback.
    pub async fn read_pixels(
        &mut self,
        destination: &mut PixmapMut<'_>,
        origin: [i32; 2],
        execution: RasterExecution,
    ) -> Result<bool, ImageResourceError> {
        if self.texture.is_none() || destination.info().is_empty() {
            return Ok(false);
        }
        self.flush().await?;
        if let Some(cpu) = &self.cpu {
            return Ok(
                Pixmap::new(self.info.clone(), self.row_bytes, cpu.bytes())?.read_pixels(
                    destination,
                    origin,
                    execution,
                )?,
            );
        }
        Ok(self
            .native_readback()
            .await?
            .pixmap()?
            .read_pixels(destination, origin, execution)?)
    }
    /// Explicit teardown flushes before releasing counted generations, then polls
    /// once without waiting. Live snapshots retain their actual texture storage.
    pub async fn close(mut self) -> Result<(), ImageResourceError> {
        let flushed = self.flush().await;
        self.scene = Vec::new();
        self.renderer = None;
        self.texture = None;
        self.texture_producer = None;
        self.generation = None;
        self.readback = None;
        self.readback_source = None;
        #[cfg(not(target_arch = "wasm32"))]
        if let Some(context) = &self.context
            && !context.is_abandoned()
        {
            let _ = context.device().poll(wgpu::PollType::Poll);
        }
        let released = self.release.take().map(|release| release()).transpose();
        self.cpu = None;
        flushed?;
        released?;
        Ok(())
    }
}
impl Drop for RenderSurface<'_> {
    fn drop(&mut self) {
        self.scene = Vec::new();
        self.renderer = None;
        self.texture = None;
        self.texture_producer = None;
        self.generation = None;
        self.readback = None;
        self.readback_source = None;
        if let Some(release) = self.release.take() {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(release));
        }
        self.cpu = None;
    }
}
fn copy_base(
    encoder: &mut wgpu::CommandEncoder,
    source: &wgpu::Texture,
    destination: &wgpu::Texture,
) {
    encoder.copy_texture_to_texture(
        wgpu::TexelCopyTextureInfo {
            texture: source,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyTextureInfo {
            texture: destination,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::Extent3d {
            width: source.width(),
            height: source.height(),
            depth_or_array_layers: 1,
        },
    );
}
