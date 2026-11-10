//! Shared original bitmap subsets and immutable copy-on-write pixel generations.
use super::{
    Image, ImageAlpha, ImageFormat, ImageInfo, PixelAlphaType as A, PixelColorType as C, Pixmap,
    PixmapMut, RasterError as E, RasterExecution as X, color, copied, zeros,
};
use std::{
    ops::{Deref, DerefMut},
    sync::{Arc, RwLock, RwLockReadGuard, RwLockWriteGuard},
};
type Release = Box<dyn FnOnce() -> Result<(), E> + Send + Sync>;
struct Storage {
    bytes: RwLock<Arc<[u8]>>,
    release: Option<Release>,
}
impl std::fmt::Debug for Storage {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("BitmapStorage").finish_non_exhaustive()
    }
}
impl Drop for Storage {
    fn drop(&mut self) {
        if let Some(release) = self.release.take() {
            // Only abandoned-owner retirement contains callback failures.
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(release));
        }
    }
}
/// Original bitmap metadata and actual shared storage owner. Explicit subsets
/// share live mutations. Clone is intentionally absent; use copy or subset to
/// select the desired original ownership contract.
#[derive(Debug, Default)]
pub struct Bitmap {
    info: ImageInfo,
    row_bytes: i32,
    storage: Option<Arc<Storage>>,
    offset: usize,
    immutable: bool,
}
/// Read lease retaining the actual storage lock and selected view extent.
pub struct BitmapPixels<'a> {
    guard: RwLockReadGuard<'a, Arc<[u8]>>,
    range: std::ops::Range<usize>,
}
impl Deref for BitmapPixels<'_> {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.guard[self.range.clone()]
    }
}
/// Exclusive write lease over the current shared generation. Frozen images keep
/// the previous bytes; live bitmap subsets observe this newly selected storage.
pub struct BitmapPixelsMut<'a> {
    guard: RwLockWriteGuard<'a, Arc<[u8]>>,
    range: std::ops::Range<usize>,
}
impl Deref for BitmapPixelsMut<'_> {
    type Target = [u8];
    fn deref(&self) -> &[u8] {
        &self.guard[self.range.clone()]
    }
}
impl DerefMut for BitmapPixelsMut<'_> {
    fn deref_mut(&mut self) -> &mut [u8] {
        &mut Arc::make_mut(&mut self.guard)[self.range.clone()]
    }
}
/// Immutable actual byte generation with original metadata and source owner.
#[derive(Clone, Debug)]
pub struct RasterSnapshot {
    info: ImageInfo,
    row_bytes: i32,
    bytes: Arc<[u8]>,
    offset: usize,
    owner: Arc<Storage>,
}
impl RasterSnapshot {
    /// Actual selected source metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Original selected row stride.
    pub fn row_bytes(&self) -> i32 {
        self.row_bytes
    }
    /// Borrow the frozen source generation independently of later bitmap writes.
    pub fn pixmap(&self) -> Result<Pixmap<'_>, E> {
        Pixmap::new(
            self.info.clone(),
            self.row_bytes,
            &self.bytes[self.offset..],
        )
    }
    /// Original renderer image publication: common formats preserve stored
    /// premultiplication; BGRA is represented directly, opaque alpha is forced.
    pub fn to_image(&self, execution: X) -> Result<Image, E> {
        if self.info.is_empty() {
            return Err(E::InvalidStorage);
        }
        let (format, alpha) = match self.info.color_type {
            C::Rgba8888 | C::Srgba8888 => (
                ImageFormat::Rgba8,
                if self.info.alpha_type == A::Premultiplied {
                    ImageAlpha::Premultiplied
                } else {
                    ImageAlpha::Straight
                },
            ),
            C::Bgra8888 => (
                ImageFormat::Bgra8,
                if self.info.alpha_type == A::Premultiplied {
                    ImageAlpha::Premultiplied
                } else {
                    ImageAlpha::Straight
                },
            ),
            _ => {
                let pixels = self.pixmap()?.straight_rgba(false, execution)?;
                return Ok(Image::rgba8(
                    [self.info.width as u32, self.info.height as u32],
                    ImageAlpha::Straight,
                    pixels,
                )?
                .with_pixel_owner(self.owner.clone()));
            }
        };
        let mut bytes = if self.offset == 0 {
            self.bytes.clone()
        } else {
            Arc::from(&self.bytes[self.offset..self.offset + self.info.byte_count(self.row_bytes)?])
        };
        if self.info.alpha_type == A::Opaque {
            let mut owned = copied(&bytes)?;
            for y in 0..self.info.height as usize {
                for p in owned[y * self.row_bytes as usize..][..self.info.width as usize * 4]
                    .chunks_exact_mut(4)
                {
                    p[3] = 255;
                }
            }
            bytes = owned.into();
        }
        Ok(Image::new(
            [self.info.width as u32, self.info.height as u32],
            self.row_bytes as u32,
            format,
            alpha,
            bytes,
        )?
        .with_pixel_owner(self.owner.clone()))
    }
    /// Actual selected transfer travels with immutable filter input ownership.
    pub fn to_filter_source(
        &self,
        execution: X,
    ) -> Result<crate::image_filter::FilterImageSource, E> {
        Ok(crate::image_filter::FilterImageSource {
            image: self.to_image(execution)?.into(),
            color_space: self
                .info
                .color_space
                .as_ref()
                .map(super::ColorSpace::to_filter_color_space),
        })
    }
}
impl Bitmap {
    /// Replace storage only after complete original layout/allocation admission.
    /// Independently retained old generations continue owning their sources.
    pub fn allocate(&mut self, info: ImageInfo, row_bytes: i32) -> Result<(), E> {
        let candidate = Self::new(info, row_bytes)?;
        self.replace(candidate)
    }
    /// Install a copy of borrowed input with explicit ownership. For a borrowed
    /// zero-copy view use Pixmap/PixmapMut; no raw pointer lifetime is inferred.
    pub fn from_pixel_copy(source: &Pixmap<'_>) -> Result<Self, E> {
        Self::from_pixels(
            source.info.clone(),
            source.row_bytes,
            copied(source.pixels)?,
        )
    }
    /// Complete original source-byte stream read before bitmap publication.
    pub fn from_reader(
        info: ImageInfo,
        row_bytes: i32,
        reader: &mut impl std::io::Read,
    ) -> Result<Self, E> {
        let mut bytes = zeros(info.validate_storage(row_bytes)?)?;
        reader
            .read_exact(&mut bytes)
            .map_err(|e| E::Transport(e.to_string()))?;
        Self::from_pixels(info, row_bytes, bytes)
    }
    /// Original independent byte copy of the selected current view.
    pub fn bytes(&self) -> Result<Vec<u8>, E> {
        copied(&self.pixels()?)
    }
    /// Original destination replacement following complete candidate conversion.
    pub fn copy_to(&self, destination: &mut Self, color_type: C, execution: X) -> Result<(), E> {
        destination.replace(self.copy(color_type, execution)?)
    }
    /// Original independent capability query for destination color conversion.
    pub fn can_copy_to(&self, color_type: C) -> bool {
        color_type.supports_color()
    }
    /// Original subset publication retains the actual live source storage.
    pub fn extract_subset(&self, destination: &mut Self, rectangle: [i32; 4]) -> Result<bool, E> {
        let Some(candidate) = self.subset(rectangle)? else {
            return Ok(false);
        };
        destination.replace(candidate)?;
        Ok(true)
    }
    /// Original pixmap source conversion into caller-owned destination storage.
    pub fn read_pixels(
        &self,
        destination: &mut PixmapMut<'_>,
        origin: [i32; 2],
        execution: X,
    ) -> Result<bool, E> {
        let source = self.snapshot()?;
        source.pixmap()?.read_pixels(destination, origin, execution)
    }
    /// Original bitmap scaling into a separately retained bitmap destination.
    pub fn scale_pixels(
        &self,
        destination: &mut Self,
        sampling: super::RasterSampling,
        execution: X,
    ) -> Result<bool, E> {
        let source = self.snapshot()?;
        let info = destination.info.clone();
        let stride = destination.row_bytes;
        let mut bytes = destination.pixels_mut()?;
        source.pixmap()?.scale_pixels(
            &mut PixmapMut::new(info, stride, &mut bytes)?,
            sampling,
            execution,
        )
    }
    /// Original NotifyPixelsChanged carries no separate producer work. Existing
    /// frozen generations already detach before a writable byte lease is exposed.
    pub fn notify_pixels_changed(&mut self) {}
    /// Swap whole original storage/metadata/immutable owners without copying.
    pub fn swap(&mut self, other: &mut Self) {
        std::mem::swap(self, other);
    }
    fn replace(&mut self, mut candidate: Self) -> Result<(), E> {
        std::mem::swap(self, &mut candidate);
        candidate.reset()
    }
    /// Allocate and zero exactly the original storage layout.
    pub fn new(info: ImageInfo, row_bytes: i32) -> Result<Self, E> {
        let count = info.validate_storage(row_bytes)?;
        let storage = if count == 0 {
            None
        } else {
            Some(Arc::new(Storage {
                bytes: RwLock::new(zeros(count)?.into()),
                release: None,
            }))
        };
        Ok(Self {
            info,
            row_bytes,
            storage,
            offset: 0,
            immutable: false,
        })
    }
    /// Original tight-row allocation convenience.
    pub fn tight(info: ImageInfo) -> Result<Self, E> {
        let stride = info.row_bytes()?;
        Self::new(info, stride)
    }
    /// Adopt actual Rust-owned bytes, without fabricating an external pointer lease.
    pub fn from_pixels(info: ImageInfo, row_bytes: i32, pixels: Vec<u8>) -> Result<Self, E> {
        Self::from_pixels_inner(info, row_bytes, pixels, None)
    }
    /// Adopt actual owned bytes and their once-only original source notification.
    /// Explicit last-owner close propagates its error; abandoned Drop contains it.
    pub fn from_pixels_with_release(
        info: ImageInfo,
        row_bytes: i32,
        pixels: Vec<u8>,
        release: impl FnOnce() -> Result<(), E> + Send + Sync + 'static,
    ) -> Result<Self, E> {
        Self::from_pixels_inner(info, row_bytes, pixels, Some(Box::new(release)))
    }
    fn from_pixels_inner(
        info: ImageInfo,
        row_bytes: i32,
        pixels: Vec<u8>,
        release: Option<Release>,
    ) -> Result<Self, E> {
        let count = info.validate_storage(row_bytes)?;
        if pixels.len() < count {
            return Err(E::InsufficientPixels);
        }
        Ok(Self {
            info,
            row_bytes,
            storage: Some(Arc::new(Storage {
                bytes: RwLock::new(pixels.into()),
                release,
            })),
            offset: 0,
            immutable: false,
        })
    }
    /// Actual source metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual row stride.
    pub fn row_bytes(&self) -> i32 {
        self.row_bytes
    }
    /// Original complete selected byte count.
    pub fn byte_count(&self) -> Result<usize, E> {
        self.info.byte_count(self.row_bytes)
    }
    /// Original storage absence independently of empty dimensions.
    pub fn is_null(&self) -> bool {
        self.storage.is_none()
    }
    /// Original nonpositive-dimension classification.
    pub fn is_empty(&self) -> bool {
        self.info.is_empty()
    }
    /// Original draw admission: nonempty metadata and an actual storage owner.
    pub fn ready_to_draw(&self) -> bool {
        !self.is_null() && !self.is_empty()
    }
    /// Original advisory immutable flag; it does not rewrite or freeze bytes.
    pub fn is_immutable(&self) -> bool {
        self.immutable
    }
    /// Retain the original advisory flag. Immutable image publication still owns
    /// an independent frozen byte generation even if callers later mutate.
    pub fn set_immutable(&mut self) {
        self.immutable = true;
    }
    /// Read the actual selected span while preventing concurrent mutation.
    pub fn pixels(&self) -> Result<BitmapPixels<'_>, E> {
        let storage = self.storage.as_ref().ok_or(E::InvalidStorage)?;
        let guard = storage.bytes.read().map_err(|_| E::Poisoned)?;
        let end = self
            .offset
            .checked_add(self.byte_count()?)
            .filter(|&v| v <= guard.len())
            .ok_or(E::InsufficientPixels)?;
        Ok(BitmapPixels {
            guard,
            range: self.offset..end,
        })
    }
    /// Mutate the shared live subset owner, detaching only immutable snapshots.
    pub fn pixels_mut(&mut self) -> Result<BitmapPixelsMut<'_>, E> {
        let count = self.byte_count()?;
        let storage = self.storage.as_ref().ok_or(E::InvalidStorage)?;
        let mut guard = storage.bytes.write().map_err(|_| E::Poisoned)?;
        let end = self
            .offset
            .checked_add(count)
            .filter(|&v| v <= guard.len())
            .ok_or(E::InsufficientPixels)?;
        if Arc::get_mut(&mut guard).is_none() {
            *guard = copied(&guard)?.into();
        }
        Ok(BitmapPixelsMut {
            guard,
            range: self.offset..end,
        })
    }
    /// O(1) immutable publication of the exact current pixel generation.
    pub fn snapshot(&self) -> Result<RasterSnapshot, E> {
        let owner = self.storage.as_ref().ok_or(E::InvalidStorage)?.clone();
        let bytes = owner.bytes.read().map_err(|_| E::Poisoned)?.clone();
        Ok(RasterSnapshot {
            info: self.info.clone(),
            row_bytes: self.row_bytes,
            bytes,
            offset: self.offset,
            owner,
        })
    }
    /// Original clipped subset retaining the same actual mutable storage owner.
    pub fn subset(&self, rectangle: [i32; 4]) -> Result<Option<Self>, E> {
        if !self.ready_to_draw() {
            return Ok(None);
        }
        let Some([l, t, r, b]) = super::pixmap::intersection(&self.info, rectangle) else {
            return Ok(None);
        };
        let offset = self
            .offset
            .checked_add(super::pixmap::offset(&self.info, self.row_bytes, l, t)?)
            .ok_or(E::Capacity)?;
        Ok(Some(Self {
            info: self.info.with_size([r - l, b - t]),
            row_bytes: self.row_bytes,
            storage: self.storage.clone(),
            offset,
            immutable: false,
        }))
    }
    /// Original legacy bitmap color query, distinct from the broader pixmap
    /// converter. Unsupported sub-four-byte layouts reject unsafe source reads.
    pub fn get_pixel(&self, x: i32, y: i32, execution: X) -> Result<[u8; 4], E> {
        let at = super::pixmap::offset(&self.info, self.row_bytes, x, y)?;
        color::legacy_read(&self.pixels()?[at..], &self.info, execution)
    }
    /// Original bitmap write with independent opaque/premultiplied policy.
    pub fn set_pixel(&mut self, x: i32, y: i32, color: [u8; 4], execution: X) -> Result<(), E> {
        let info = self.info.clone();
        let at = super::pixmap::offset(&info, self.row_bytes, x, y)?;
        color::legacy_write(
            &mut self.pixels_mut()?[at..],
            &info,
            color,
            execution,
            false,
        )
    }
    /// Original complete color-array read in row-major order.
    pub fn colors(&self, execution: X) -> Result<Vec<[u8; 4]>, E> {
        let pixels = self.pixels()?;
        let count = self
            .info
            .width
            .checked_mul(self.info.height)
            .and_then(|v| usize::try_from(v).ok())
            .ok_or(E::Capacity)?;
        let mut result = Vec::new();
        result.try_reserve_exact(count).map_err(|_| E::Capacity)?;
        for y in 0..self.info.height {
            for x in 0..self.info.width {
                let at = super::pixmap::offset(&self.info, self.row_bytes, x, y)?;
                result.push(color::legacy_read(&pixels[at..], &self.info, execution)?);
            }
        }
        Ok(result)
    }
    /// Original exact-width-times-height admission before any pixel mutation.
    pub fn set_colors(&mut self, colors: &[[u8; 4]], execution: X) -> Result<(), E> {
        let info = self.info.clone();
        let stride = self.row_bytes;
        let count = info
            .width
            .checked_mul(info.height)
            .and_then(|v| usize::try_from(v).ok())
            .ok_or(E::Capacity)?;
        if colors.len() != count {
            return Err(E::InvalidStorage);
        }
        if info.color_type != C::Rgb565 && info.bytes_per_pixel() < 4 {
            return Err(E::UnsupportedFormat);
        }
        let mut pixels = self.pixels_mut()?;
        for y in 0..info.height {
            for x in 0..info.width {
                let at = super::pixmap::offset(&info, stride, x, y)?;
                color::legacy_write(
                    &mut pixels[at..],
                    &info,
                    colors[y as usize * info.width as usize + x as usize],
                    execution,
                    false,
                )?;
            }
        }
        Ok(())
    }
    /// Original whole-bitmap erase preserves supplied alpha even for opaque
    /// metadata; rectangle erase uses the separate pixmap producer below.
    pub fn erase(&mut self, color: [u8; 4], execution: X) -> Result<(), E> {
        if !self.ready_to_draw() {
            return Ok(());
        }
        let info = self.info.clone();
        let stride = self.row_bytes as usize;
        if info.color_type != C::Rgb565 && info.bytes_per_pixel() != 4 {
            return Err(E::UnsupportedFormat);
        }
        let bpp = info.bytes_per_pixel() as usize;
        let mut packed = [0u8; 4];
        color::legacy_write(&mut packed, &info, color, execution, true)?;
        let mut pixels = self.pixels_mut()?;
        for y in 0..info.height as usize {
            for p in pixels[y * stride..][..info.width as usize * bpp].chunks_exact_mut(bpp) {
                p.copy_from_slice(&packed[..bpp]);
            }
        }
        Ok(())
    }
    /// Original rectangle erase through pixmap conversion.
    pub fn erase_rect(
        &mut self,
        color: [u8; 4],
        rectangle: [i32; 4],
        execution: X,
    ) -> Result<bool, E> {
        if !self.ready_to_draw() {
            return Ok(false);
        }
        let info = self.info.clone();
        let stride = self.row_bytes;
        let mut bytes = self.pixels_mut()?;
        PixmapMut::new(info, stride, &mut bytes)?.erase(color, rectangle, execution)
    }
    /// Original conversion-copy into fresh storage; publication follows complete
    /// conversion and keeps the original source color-space/alpha metadata.
    pub fn copy(&self, color_type: C, execution: X) -> Result<Self, E> {
        if !color_type.supports_color() {
            return Err(E::UnsupportedFormat);
        }
        let info = self.info.with_color_type(color_type);
        let mut result = Self::tight(info.clone())?;
        let source = self.pixels()?;
        let stride = result.row_bytes;
        let mut target = result.pixels_mut()?;
        let converted = Pixmap::new(self.info.clone(), self.row_bytes, &source)?.read_pixels(
            &mut PixmapMut::new(info, stride, &mut target)?,
            [0, 0],
            execution,
        )?;
        if !converted {
            return Err(E::UnsupportedFormat);
        }
        drop(target);
        Ok(result)
    }
    /// Original alpha extraction uses a four-byte-aligned Alpha8 row stride and
    /// zero output offset; source paint metadata is intentionally independent.
    pub fn extract_alpha(&self, execution: X) -> Result<(Self, [i32; 2]), E> {
        let info = ImageInfo::with_format(
            self.info.width,
            self.info.height,
            C::Alpha8,
            A::Straight,
            None,
        );
        let stride = info
            .row_bytes()?
            .checked_add(3)
            .map(|v| v & !3)
            .ok_or(E::Capacity)?;
        let mut result = Self::new(info, stride)?;
        let bytes = self.pixels()?;
        let source = Pixmap::new(self.info.clone(), self.row_bytes, &bytes)?;
        let mut target = result.pixels_mut()?;
        for y in 0..self.info.height {
            for x in 0..self.info.width {
                target[y as usize * stride as usize + x as usize] =
                    (source.alpha(x, y, execution)? * 255.0)
                        .round_ties_even()
                        .clamp(0.0, 255.0) as u8;
            }
        }
        drop(target);
        Ok((result, [0, 0]))
    }
    /// Original CPU resize with explicit resolved arithmetic policy.
    pub fn resize(
        &self,
        info: ImageInfo,
        sampling: super::RasterSampling,
        execution: X,
    ) -> Result<Self, E> {
        let bytes = self.pixels()?;
        super::resize::resize(
            &Pixmap::new(self.info.clone(), self.row_bytes, &bytes)?,
            info,
            sampling,
            execution,
        )
    }
    /// Decode original selected pixels and apply the original default premul
    /// bitmap publication. No image resampling is implicit in this overload.
    pub fn decode(source: &[u8], execution: X) -> Result<Self, E> {
        let codec = super::super::ImageCodec::create(source)?;
        let decoded = codec.decoded();
        let [w, h] = decoded.size();
        let info = ImageInfo::with_format(
            i32::try_from(w).map_err(|_| E::Capacity)?,
            i32::try_from(h).map_err(|_| E::Capacity)?,
            C::Rgba8888,
            A::Premultiplied,
            decoded.color_space().cloned(),
        );
        Self::from_decoded(decoded, info, execution)
    }
    /// Original decode-to-requested-info nearest mapping uses integer x*source/
    /// destination coordinates, independently of center-based Resize semantics.
    pub fn from_decoded(
        decoded: &super::super::DecodedImage,
        info: ImageInfo,
        execution: X,
    ) -> Result<Self, E> {
        if info.is_empty() || !info.color_type.supports_resize() {
            return Err(E::UnsupportedFormat);
        }
        let mut result = Self::tight(info.clone())?;
        let stride = result.row_bytes as usize;
        let [sw, sh] = decoded.size();
        let mut bytes = result.pixels_mut()?;
        let bpp = info.bytes_per_pixel() as usize;
        for y in 0..info.height as usize {
            let sy = (y as u64 * u64::from(sh) / info.height as u64) as usize;
            for x in 0..info.width as usize {
                let sx = (x as u64 * u64::from(sw) / info.width as u64) as usize;
                let at = (sy * sw as usize + sx) * 4;
                let c: &[u8] = &decoded.pixels()[at..at + 4];
                let alpha = if info.alpha_type == A::Opaque || info.color_type == C::Rgb888x {
                    255
                } else {
                    c[3]
                };
                color::legacy_write(
                    &mut bytes[y * stride + x * bpp..],
                    &info,
                    [c[0], c[1], c[2], alpha],
                    execution,
                    false,
                )?;
            }
        }
        drop(bytes);
        Ok(result)
    }
    /// Retire only this actual source lease; independently retained subsets,
    /// snapshots and images continue owning the original release notification.
    pub fn reset(&mut self) -> Result<(), E> {
        let storage = self.storage.take();
        self.info = ImageInfo::default();
        self.row_bytes = 0;
        self.offset = 0;
        self.immutable = false;
        if let Some(storage) = storage {
            if let Ok(mut storage) = Arc::try_unwrap(storage) {
                if let Some(release) = storage.release.take() {
                    release()?;
                }
            }
        }
        Ok(())
    }
}
