use super::*;
use std::task::{Poll, Waker};
/// Original optional immutable readback caching policy.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum ImageCachingHint {
    /// Retain one immutable CPU generation for future reads.
    #[default]
    Allow,
    /// Map fresh actual texture bytes without retaining that result.
    Disallow,
}
#[derive(Debug, Default)]
pub(super) struct ReadbackPool {
    buffer: Option<wgpu::Buffer>,
    cached: Option<Arc<[u8]>>,
    spare: Option<Arc<[u8]>>,
}
#[derive(Default)]
struct Signal {
    result: Option<Result<(), wgpu::BufferAsyncError>>,
    waker: Option<Waker>,
}
/// Immutable bytes retaining their original image source. Borrowed pixmaps never
/// outlive this owner; base texture row stride remains independent of view width.
#[derive(Clone, Debug)]
pub struct TextureReadback {
    image: TextureImage,
    bytes: Arc<[u8]>,
    info: ImageInfo,
    stride: i32,
    offset: usize,
}
impl TextureReadback {
    /// Original source info with actual native byte order and alpha encoding.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual retained row stride; subsets retain the complete source row width.
    pub fn row_bytes(&self) -> i32 {
        self.stride
    }
    /// Safe borrowed view retaining original row layout and selected source extent.
    pub fn pixmap(&self) -> Result<Pixmap<'_>, RasterError> {
        Pixmap::new(self.info.clone(), self.stride, &self.bytes[self.offset..])
    }
    /// Original image source owner, kept live independently of user wrappers.
    pub fn image(&self) -> &TextureImage {
        &self.image
    }
    /// Readback pixels in the native source layout, including retained row gaps.
    pub fn pixels(&self) -> Result<&[u8], RasterError> {
        let size = self.info.byte_count(self.stride)?;
        Ok(&self.bytes[self.offset..self.offset + size])
    }
    pub(crate) fn whole_bytes(&self) -> &Arc<[u8]> {
        &self.bytes
    }
}
struct Pending {
    image: TextureImage,
    buffer: Option<wgpu::Buffer>,
    pitch: u32,
    submission: wgpu::SubmissionIndex,
    signal: Arc<Mutex<Signal>>,
    cache: bool,
}
/// Pending same-device texture copy/map. Dropping this object never polls, waits
/// or explicitly unmaps; abandoned handles retire through wgpu's actual owner.
pub struct TextureReadbackWork {
    ready: Option<TextureReadback>,
    pending: Option<Pending>,
}
impl TextureReadbackWork {
    /// Browser-safe asynchronous mapping. Native applications must continue their
    /// device polling while awaiting, or use complete_blocking().
    pub async fn complete(mut self) -> Result<TextureReadback, ImageResourceError> {
        if let Some(ready) = self.ready.take() {
            return Ok(ready);
        }
        let pending = self.pending.take().ok_or(ImageResourceError::Abandoned)?;
        std::future::poll_fn(|cx| {
            let mut state = pending.signal.lock().unwrap_or_else(|e| e.into_inner());
            if let Some(result) = state.result.take() {
                Poll::Ready(result)
            } else {
                if state
                    .waker
                    .as_ref()
                    .is_none_or(|w| !w.will_wake(cx.waker()))
                {
                    state.waker = Some(cx.waker().clone());
                }
                Poll::Pending
            }
        })
        .await
        .map_err(|e| ImageResourceError::Readback(e.to_string()))?;
        pending.finish()
    }
    /// Original native thirty-second map bound. This API is absent in browsers.
    #[cfg(not(target_arch = "wasm32"))]
    pub fn complete_blocking(mut self) -> Result<TextureReadback, ImageResourceError> {
        if let Some(ready) = self.ready.take() {
            return Ok(ready);
        }
        let pending = self.pending.take().ok_or(ImageResourceError::Abandoned)?;
        pending
            .image
            .context()
            .device()
            .poll(wgpu::PollType::Wait {
                submission_index: Some(pending.submission.clone()),
                timeout: Some(std::time::Duration::from_secs(30)),
            })
            .map_err(|e| ImageResourceError::Readback(e.to_string()))?;
        pending
            .signal
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .result
            .take()
            .ok_or_else(|| {
                ImageResourceError::Readback("mapping exceeded the original native bound".into())
            })?
            .map_err(|e| ImageResourceError::Readback(e.to_string()))?;
        pending.finish()
    }
    pub(crate) async fn complete_host(self) -> Result<TextureReadback, ImageResourceError> {
        #[cfg(not(target_arch = "wasm32"))]
        {
            self.complete_blocking()
        }
        #[cfg(target_arch = "wasm32")]
        {
            self.complete().await
        }
    }
}
impl Pending {
    fn finish(mut self) -> Result<TextureReadback, ImageResourceError> {
        let buffer = self.buffer.take().ok_or(ImageResourceError::Abandoned)?;
        let size = self.image.storage.image.size();
        let count = byte_count(size)?;
        let spare = self
            .image
            .storage
            .readback
            .lock()
            .unwrap_or_else(|e| e.into_inner())
            .spare
            .take();
        let mut bytes =
            if let Some(bytes) = spare.filter(|v| v.len() == count && Arc::strong_count(v) == 1) {
                bytes
            } else {
                Arc::<[u8]>::from(super::super::raster::zeros(count)?)
            };
        let writable = Arc::get_mut(&mut bytes).ok_or(Error::ResourceDomain)?;
        {
            let mapped = buffer.slice(..).get_mapped_range();
            let width = size[0] as usize * 4;
            for row in 0..size[1] as usize {
                writable[row * width..(row + 1) * width].copy_from_slice(
                    &mapped[row * self.pitch as usize..row * self.pitch as usize + width],
                );
            }
        }
        buffer.unmap();
        {
            let mut pool = self
                .image
                .storage
                .readback
                .lock()
                .unwrap_or_else(|e| e.into_inner());
            pool.cached = self.cache.then(|| bytes.clone());
            pool.spare = Some(bytes.clone());
            if pool
                .buffer
                .as_ref()
                .is_none_or(|old| old.size() < buffer.size())
            {
                pool.buffer = Some(buffer);
            }
        }
        ready(&self.image, bytes, false)
    }
}
fn ready(
    image: &TextureImage,
    bytes: Arc<[u8]>,
    portable: bool,
) -> Result<TextureReadback, ImageResourceError> {
    let mut info = native_info(&image.storage.image, image.info());
    if portable {
        info.color_type = PixelColorType::Rgba8888;
    }
    let width = image.storage.image.size()[0];
    let stride =
        i32::try_from(width.checked_mul(4).ok_or(Error::Capacity)?).map_err(|_| Error::Capacity)?;
    let offset = (image.region[1] as usize * width as usize + image.region[0] as usize) * 4;
    Ok(TextureReadback {
        image: image.clone(),
        bytes,
        info,
        stride,
        offset,
    })
}
pub(super) fn begin(
    image: &TextureImage,
    caching: ImageCachingHint,
) -> Result<TextureReadbackWork, ImageResourceError> {
    image.context().admit()?;
    if let Some(pixels) = &image.storage.portable {
        return Ok(TextureReadbackWork {
            ready: Some(ready(image, pixels.clone(), true)?),
            pending: None,
        });
    }
    let mut pool = image
        .storage
        .readback
        .lock()
        .unwrap_or_else(|e| e.into_inner());
    if caching == ImageCachingHint::Allow
        && let Some(bytes) = &pool.cached
    {
        return Ok(TextureReadbackWork {
            ready: Some(ready(image, bytes.clone(), false)?),
            pending: None,
        });
    }
    pool.cached = None;
    let texture = image.storage.image.texture();
    if !texture.usage().contains(wgpu::TextureUsages::COPY_SRC) {
        return Err(ImageResourceError::TextureContract);
    }
    let pitch = texture
        .width()
        .checked_mul(4)
        .and_then(|v| v.checked_add(255))
        .ok_or(Error::Capacity)?
        & !255;
    let size = u64::from(pitch) * u64::from(texture.height());
    if size > image.context().device().limits().max_buffer_size || size > u64::from(u32::MAX) {
        return Err(Error::Capacity.into());
    }
    let buffer = pool
        .buffer
        .take()
        .filter(|b| b.size() >= size)
        .unwrap_or_else(|| {
            image
                .context()
                .device()
                .create_buffer(&wgpu::BufferDescriptor {
                    label: Some("ProGPU reusable image readback"),
                    size,
                    usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
                    mapped_at_creation: false,
                })
        });
    drop(pool);
    let mut encoder =
        image
            .context()
            .device()
            .create_command_encoder(&wgpu::CommandEncoderDescriptor {
                label: Some("ProGPU image texture readback"),
            });
    encoder.copy_texture_to_buffer(
        wgpu::TexelCopyTextureInfo {
            texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyBufferInfo {
            buffer: &buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(pitch),
                rows_per_image: Some(texture.height()),
            },
        },
        texture.size(),
    );
    let submission = image.context().queue().submit([encoder.finish()]);
    image.gpu_image().retain_submitted(image.context().queue());
    let signal = Arc::new(Mutex::new(Signal::default()));
    let callback = signal.clone();
    buffer
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |result| {
            let wake = {
                let mut s = callback.lock().unwrap_or_else(|e| e.into_inner());
                s.result = Some(result);
                s.waker.take()
            };
            if let Some(wake) = wake {
                wake.wake();
            }
        });
    Ok(TextureReadbackWork {
        ready: None,
        pending: Some(Pending {
            image: image.clone(),
            buffer: Some(buffer),
            pitch,
            submission,
            signal,
            cache: caching == ImageCachingHint::Allow,
        }),
    })
}
