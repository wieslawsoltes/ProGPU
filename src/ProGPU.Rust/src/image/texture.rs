//! Original retained SKImage texture storage, context caches and immutable views.
use super::{
    GpuImage, ImageAlpha, ImageDraw, ImageInfo, PixelAlphaType, PixelColorType, Pixmap, PixmapMut,
    RasterError, RasterExecution,
};
use crate::Error;
use std::{
    fmt,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
};
mod compatibility;
mod picture;
pub use picture::PictureCaptureSession;
mod readback;
pub use compatibility::TextureRasterView;
mod upload;
pub use readback::{ImageCachingHint, TextureReadback, TextureReadbackWork};

/// Resource ownership does not permit invalidating independently retained wgpu
/// handles. Adopted resources retire through their actual final handle owner.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TextureOwnership {
    /// External producer owns mutation/destruction; the handle and optional
    /// release lease remain retained until their last actual use.
    Borrowed,
    /// Ownership of the supplied texture generation was transferred here.
    Adopted,
    /// This implementation allocated the texture generation.
    Owned,
}
/// GPU image lifecycle, source-domain, readback or explicit callback failure.
#[derive(Debug, thiserror::Error)]
pub enum ImageResourceError {
    /// A context was explicitly abandoned or an owner was already closed.
    #[error("the image context has been abandoned")]
    Abandoned,
    /// A GPU-only image has no original portable CPU generation.
    #[error("cross-context materialization requires immutable portable pixels")]
    NotPortable,
    /// The original GPU resource lacks the requested capability.
    #[error("the texture does not support this image operation")]
    TextureContract,
    /// The original view bounds are not contained by the source image.
    #[error("image view bounds must be non-empty and contained by the source")]
    Bounds,
    /// Native mapping failed or exceeded the original thirty-second bound.
    #[error("texture readback failed: {0}")]
    Readback(String),
    /// The client release procedure returned an explicit-disposal error.
    #[error("image release callback failed: {0}")]
    Release(String),
    /// Original safe raster conversion rejected an operation.
    #[error(transparent)]
    Raster(#[from] RasterError),
    /// Existing renderer/device admission failed.
    #[error(transparent)]
    Render(#[from] Error),
}
/// The original once-only texture release procedure. A captured closure owns its
/// typed source context; no raw address or inferred global context is retained.
pub type TextureRelease = Box<dyn FnOnce() -> Result<(), ImageResourceError> + Send + Sync>;

#[derive(Debug)]
struct ContextState {
    device: wgpu::Device,
    queue: wgpu::Queue,
    abandoned: AtomicBool,
    mip_pipelines: Mutex<Vec<(wgpu::TextureFormat, wgpu::RenderPipeline)>>,
}
/// Explicit actual WebGPU device/queue owner. Distinct contexts on the same device
/// remain distinct portable-cache identities, matching the original source API.
#[derive(Clone, Debug)]
pub struct ImageContext(Arc<ContextState>);
impl ImageContext {
    /// The queue must be the supplied device's actual queue. Safe wgpu performs
    /// independent backend validation when these handles are used together.
    pub fn new(device: &wgpu::Device, queue: &wgpu::Queue) -> Self {
        Self(Arc::new(ContextState {
            device: device.clone(),
            queue: queue.clone(),
            abandoned: AtomicBool::new(false),
            mip_pipelines: Mutex::new(Vec::new()),
        }))
    }
    /// Actual device, retained independently of the caller's handle.
    pub fn device(&self) -> &wgpu::Device {
        &self.0.device
    }
    /// Actual submission queue.
    pub fn queue(&self) -> &wgpu::Queue {
        &self.0.queue
    }
    /// Exact original context identity, not merely adapter/device equivalence.
    pub fn same_context(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.0, &other.0)
    }
    /// Stop new work without destroying borrowed devices or live snapshots.
    pub fn abandon(&self) {
        self.0.abandoned.store(true, Ordering::Release);
    }
    /// Whether this actual source context still admits new work.
    pub fn is_abandoned(&self) -> bool {
        self.0.abandoned.load(Ordering::Acquire)
    }
    pub(crate) fn admit(&self) -> Result<(), ImageResourceError> {
        if self.is_abandoned() {
            Err(ImageResourceError::Abandoned)
        } else {
            Ok(())
        }
    }
}

pub(super) struct TextureStorage {
    context: ImageContext,
    image: GpuImage,
    info: ImageInfo,
    ownership: TextureOwnership,
    portable: Option<Arc<[u8]>>,
    texture_backed: bool,
    readback: Mutex<readback::ReadbackPool>,
    release: ReleaseAction,
}
impl fmt::Debug for TextureStorage {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("TextureStorage")
            .field("image", &self.image)
            .field("info", &self.info)
            .field("ownership", &self.ownership)
            .field("portable", &self.portable.is_some())
            .field("texture_backed", &self.texture_backed)
            .finish_non_exhaustive()
    }
}
struct ReleaseAction(Option<TextureRelease>);
impl ReleaseAction {
    fn take(&mut self) -> Option<TextureRelease> {
        self.0.take()
    }
}
impl Drop for ReleaseAction {
    fn drop(&mut self) {
        // This is the final storage field: actual texture/readback owners have
        // already retired. Abandoned callback failures alone are contained.
        if let Some(release) = self.0.take() {
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(release));
        }
    }
}
#[derive(Debug, Default)]
struct PortableCache {
    entries: Vec<(ImageContext, GpuImage)>,
    raster: Option<super::RasterSnapshot>,
    encoded: Option<Arc<[u8]>>,
}
/// Immutable retained image view. Subsets share the original primary storage;
/// context-local textures belong to the view, independently of primary leases.
#[derive(Clone, Debug)]
pub struct TextureImage {
    storage: Arc<TextureStorage>,
    region: [u32; 4],
    cache: Arc<Mutex<PortableCache>>,
}
impl TextureImage {
    /// Wrap a genuine device-bound texture with original complete backend image
    /// gates. Borrowed producers must not mutate/destroy immutable image pixels.
    pub fn from_texture(
        context: &ImageContext,
        image: GpuImage,
        info: ImageInfo,
        ownership: TextureOwnership,
        release: Option<TextureRelease>,
    ) -> Result<Self, ImageResourceError> {
        context.admit()?;
        if image.device() != context.device()
            || image.size() != [info.width as u32, info.height as u32]
            || info.is_empty()
            || !image
                .texture()
                .usage()
                .contains(wgpu::TextureUsages::COPY_SRC)
            || !matches!(
                image.texture().format(),
                wgpu::TextureFormat::Rgba8Unorm
                    | wgpu::TextureFormat::Bgra8Unorm
                    | wgpu::TextureFormat::Rgba8UnormSrgb
                    | wgpu::TextureFormat::Bgra8UnormSrgb
                    | wgpu::TextureFormat::Rgba16Float
            )
        {
            return Err(ImageResourceError::TextureContract);
        }
        Ok(Self::create(
            context.clone(),
            image,
            info,
            ownership,
            None,
            true,
            release,
        ))
    }
    pub(super) fn create(
        context: ImageContext,
        image: GpuImage,
        info: ImageInfo,
        ownership: TextureOwnership,
        portable: Option<Arc<[u8]>>,
        texture_backed: bool,
        release: Option<TextureRelease>,
    ) -> Self {
        let [w, h] = image.size();
        Self {
            storage: Arc::new(TextureStorage {
                context,
                image,
                info,
                ownership,
                portable,
                texture_backed,
                readback: Mutex::new(Default::default()),
                release: ReleaseAction(release),
            }),
            region: [0, 0, w, h],
            cache: Arc::new(Mutex::new(Default::default())),
        }
    }
    /// Original metadata, with the actual subset extent and retained color space.
    pub fn info(&self) -> ImageInfo {
        self.storage
            .info
            .with_size([self.region[2] as i32, self.region[3] as i32])
    }
    /// Actual primary texture context; portable views may materialize elsewhere.
    pub fn context(&self) -> &ImageContext {
        &self.storage.context
    }
    /// Original primary-storage ownership, independent of wrapper count.
    pub fn ownership(&self) -> TextureOwnership {
        self.storage.ownership
    }
    /// Original alpha-only metadata query.
    pub fn is_alpha_only(&self) -> bool {
        self.storage.info.color_type == PixelColorType::Alpha8
    }
    /// Original explicitly materialized raster/texture classification.
    pub fn is_texture_backed(&self) -> bool {
        self.storage.texture_backed
    }
    /// Original immutable images are not lazily generated.
    pub fn is_lazy_generated(&self) -> bool {
        false
    }
    /// Source subrectangle in actual primary texture pixels: X,Y,width,height.
    pub fn texture_region(&self) -> [u32; 4] {
        self.region
    }
    /// Exact storage identity shared by retained views.
    pub fn same_storage(&self, other: &Self) -> bool {
        Arc::ptr_eq(&self.storage, &other.storage)
    }
    /// Actual primary sampled texture with a counted producer/release lease.
    pub fn gpu_image(&self) -> GpuImage {
        self.storage
            .image
            .clone()
            .with_producer(self.storage.clone())
    }
    /// True only for the exact live context of the primary texture.
    pub fn is_valid(&self, context: &ImageContext) -> bool {
        !context.is_abandoned() && self.context().same_context(context)
    }
    /// Share a nonempty contained view; no texture copy or CPU readback occurs.
    pub fn subset(&self, bounds: [u32; 4]) -> Result<Self, ImageResourceError> {
        let [l, t, r, b] = bounds;
        if r <= l || b <= t || r > self.region[2] || b > self.region[3] {
            return Err(ImageResourceError::Bounds);
        }
        Ok(Self {
            storage: self.storage.clone(),
            region: [self.region[0] + l, self.region[1] + t, r - l, b - t],
            cache: Arc::new(Mutex::new(Default::default())),
        })
    }
    /// Prepare the original source rectangle and opaque-alpha policy for drawing.
    pub fn draw(&self, destination: [f32; 4]) -> ImageDraw {
        let mut draw = ImageDraw::new(self.gpu_image(), destination);
        draw.source = self.region.map(|v| v as f32);
        draw.ignore_source_alpha = self.storage.info.alpha_type == PixelAlphaType::Opaque;
        draw
    }
    /// A shared surface generation can reclaim its texture only when no view,
    /// sampling lease or pending readback retains that exact generation.
    pub(crate) fn can_reclaim(&self) -> bool {
        self.region
            == [
                0,
                0,
                self.storage.image.size()[0],
                self.storage.image.size()[1],
            ]
            && Arc::strong_count(&self.storage) == 1
    }
    /// Explicit close propagates the final source callback error. Other actual
    /// storage/view/submission owners keep the callback pending until their end.
    pub fn close(self) -> Result<(), ImageResourceError> {
        let Self { storage, cache, .. } = self;
        drop(cache);
        if let Ok(mut storage) = Arc::try_unwrap(storage) {
            let release = storage.release.take();
            drop(storage);
            if let Some(release) = release {
                release()?;
            }
        }
        Ok(())
    }
    /// Copy immutable portable pixels for this exact subset, without GPU work.
    pub fn portable_pixels(&self) -> Result<Option<Arc<[u8]>>, ImageResourceError> {
        let Some(pixels) = &self.storage.portable else {
            return Ok(None);
        };
        let [x, y, w, h] = self.region;
        if [x, y, w, h]
            == [
                0,
                0,
                self.storage.image.size()[0],
                self.storage.image.size()[1],
            ]
        {
            return Ok(Some(pixels.clone()));
        }
        let mut out = super::raster::zeros(byte_count([w, h])?)?;
        let stride = self.storage.image.size()[0] as usize * 4;
        for row in 0..h as usize {
            let start = (y as usize + row) * stride + x as usize * 4;
            out[row * w as usize * 4..(row + 1) * w as usize * 4]
                .copy_from_slice(&pixels[start..start + w as usize * 4]);
        }
        Ok(Some(out.into()))
    }
    /// Reuse an actual context-local generation only for original CPU-portable
    /// images. A GPU-only image never silently crosses devices by readback.
    pub fn for_context(
        &self,
        context: &ImageContext,
    ) -> Result<(GpuImage, [u32; 4]), ImageResourceError> {
        context.admit()?;
        if self.context().same_context(context) {
            return Ok((self.gpu_image(), self.region));
        }
        let pixels = self
            .portable_pixels()?
            .ok_or(ImageResourceError::NotPortable)?;
        let mut cache = self.cache.lock().unwrap_or_else(|e| e.into_inner());
        if let Some((_, image)) = cache.entries.iter().find(|(c, _)| c.same_context(context)) {
            return Ok((
                image.clone().with_producer(self.cache.clone()),
                [0, 0, self.region[2], self.region[3]],
            ));
        }
        let image = upload::upload(context, &self.info(), &pixels, 1, false)?;
        cache.entries.push((context.clone(), image.clone()));
        Ok((
            image.with_producer(self.cache.clone()),
            [0, 0, self.region[2], self.region[3]],
        ))
    }
    /// Materialize a CPU-compatible view using exact native readback layout.
    pub fn readback(
        &self,
        caching: ImageCachingHint,
    ) -> Result<TextureReadbackWork, ImageResourceError> {
        readback::begin(self, caching)
    }
    /// Convert only the selected intersection, preserving destination row padding.
    pub async fn read_pixels(
        &self,
        destination: &mut PixmapMut<'_>,
        origin: [i32; 2],
        caching: ImageCachingHint,
        execution: RasterExecution,
    ) -> Result<bool, ImageResourceError> {
        self.readback(caching)?
            .complete()
            .await?
            .pixmap()?
            .read_pixels(destination, origin, execution)
            .map_err(Into::into)
    }
}
pub(super) fn byte_count(size: [u32; 2]) -> Result<usize, ImageResourceError> {
    usize::try_from(u64::from(size[0]) * u64::from(size[1]) * 4).map_err(|_| Error::Capacity.into())
}
pub(super) fn native_info(image: &GpuImage, mut info: ImageInfo) -> ImageInfo {
    info.color_type = match image.texture().format() {
        wgpu::TextureFormat::Bgra8Unorm | wgpu::TextureFormat::Bgra8UnormSrgb => {
            PixelColorType::Bgra8888
        }
        wgpu::TextureFormat::Rgba16Float => PixelColorType::RgbaF16,
        _ => PixelColorType::Rgba8888,
    };
    info.alpha_type = if image.alpha() == ImageAlpha::Straight {
        PixelAlphaType::Straight
    } else {
        PixelAlphaType::Premultiplied
    };
    info
}

pub(crate) use upload::{allocate as allocate_texture, mip_count as mip_levels};
pub(crate) fn surface_byte_count(size: [u32; 2]) -> Result<usize, ImageResourceError> {
    byte_count(size)
}
impl TextureImage {
    pub(crate) fn create_surface_generation(
        context: ImageContext,
        image: GpuImage,
        info: ImageInfo,
        portable: Option<Arc<[u8]>>,
    ) -> Self {
        Self::create(
            context,
            image,
            info,
            TextureOwnership::Owned,
            portable,
            true,
            None,
        )
    }
}
