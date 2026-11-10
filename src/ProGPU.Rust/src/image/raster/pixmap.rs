//! Safe borrowed pixel views; subset/view borrows retain the actual source lease.
use super::{
    ImageInfo, PixelAlphaType as A, PixelColorType as C, RasterError as E, RasterExecution as X,
    color,
};
/// Immutable original pixmap over caller-owned storage, with an explicit borrow.
#[derive(Clone, Debug)]
pub struct Pixmap<'a> {
    pub(super) info: ImageInfo,
    pub(super) row_bytes: i32,
    pub(super) pixels: &'a [u8],
}
/// Exclusive original writable pixmap; safe Rust prevents overlapping mutation
/// while a source or subset view is borrowed.
#[derive(Debug)]
pub struct PixmapMut<'a> {
    pub(super) info: ImageInfo,
    pub(super) row_bytes: i32,
    pub(super) pixels: &'a mut [u8],
}
impl<'a> Pixmap<'a> {
    /// Retain an actual complete storage borrow, including final-row policy.
    pub fn new(info: ImageInfo, row_bytes: i32, pixels: &'a [u8]) -> Result<Self, E> {
        let count = info.validate_storage(row_bytes)?;
        if pixels.len() < count {
            return Err(E::InsufficientPixels);
        }
        Ok(Self {
            info,
            row_bytes,
            pixels: &pixels[..count],
        })
    }
    /// Original immutable metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual row stride, independent of tight bytes-size metadata.
    pub fn row_bytes(&self) -> i32 {
        self.row_bytes
    }
    /// Complete view storage, excluding unused trailing padding.
    pub fn pixels(&self) -> &'a [u8] {
        self.pixels
    }
    /// Original byte offset from actual view origin.
    pub fn pixel_offset(&self, x: i32, y: i32) -> Result<usize, E> {
        offset(&self.info, self.row_bytes, x, y)
    }
    /// Original tail span beginning at the selected pixel.
    pub fn pixel_span(&self, x: i32, y: i32) -> Result<&'a [u8], E> {
        if self.info.is_empty() || self.info.bytes_per_pixel() <= 0 {
            return Ok(&[]);
        }
        Ok(&self.pixels[self.pixel_offset(x, y)?..])
    }
    /// Original typed span admission plus Rust's explicit alignment check.
    pub fn typed_span<T: bytemuck::Pod>(&self, x: i32, y: i32) -> Result<&'a [T], E> {
        typed_admission::<T>(&self.info, self.row_bytes)?;
        bytemuck::try_cast_slice(self.pixel_span(x, y)?).map_err(|_| E::InvalidStorage)
    }
    /// Original straight RGBA8 color query; unsupported conversion remains explicit.
    pub fn color(&self, x: i32, y: i32, execution: X) -> Result<[u8; 4], E> {
        self.pixel_offset(x, y)?;
        if self.pixels.is_empty() || !self.info.color_type.supports_color() {
            return Ok([0; 4]);
        }
        color::read(self.pixel_span(x, y)?, &self.info, execution)
    }
    /// Original float query multiplies byte colors by one precomputed 1/255.
    pub fn color_f(&self, x: i32, y: i32, execution: X) -> Result<[f32; 4], E> {
        let c = self.color(x, y, execution)?.map(f32::from);
        Ok(if execution == X::CpuSimd {
            (wide::f32x4::from(c) * wide::f32x4::splat(1.0 / 255.0)).to_array()
        } else {
            c.map(|v| v * (1.0 / 255.0))
        })
    }
    /// Original independent alpha query uses division by 255.
    pub fn alpha(&self, x: i32, y: i32, execution: X) -> Result<f32, E> {
        Ok(f32::from(self.color(x, y, execution)?[3]) / 255.0)
    }
    /// Exact original clamped subset; a nonempty view borrows the same original
    /// rows and does not copy or replace its source with a completed image.
    pub fn subset(&self, rectangle: [i32; 4]) -> Result<Option<Self>, E> {
        let Some([l, t, r, b]) = intersection(&self.info, rectangle) else {
            return Ok(None);
        };
        let offset = self.pixel_offset(l, t)?;
        Ok(Some(Self::new(
            self.info.with_size([r - l, b - t]),
            self.row_bytes,
            &self.pixels[offset..],
        )?))
    }
    /// Reinterpret original metadata while retaining the actual storage borrow.
    pub fn with_info(&self, info: ImageInfo) -> Result<Self, E> {
        Self::new(info, self.row_bytes, self.pixels)
    }
    /// Original independently checked source/destination intersection, leaving
    /// all destination bytes outside the intersection unchanged.
    pub fn read_pixels(
        &self,
        destination: &mut PixmapMut<'_>,
        source_origin: [i32; 2],
        execution: X,
    ) -> Result<bool, E> {
        if self.pixels.is_empty() || destination.pixels.is_empty() || destination.info.is_empty() {
            return Ok(false);
        }
        if !self.info.color_type.supports_color() || !destination.info.color_type.supports_color() {
            return Ok(false);
        }
        let [sx, sy] = source_origin;
        let right = sx.checked_add(destination.info.width).ok_or(E::Capacity)?;
        let bottom = sy.checked_add(destination.info.height).ok_or(E::Capacity)?;
        let Some([l, t, r, b]) = intersection(&self.info, [sx, sy, right, bottom]) else {
            return Ok(false);
        };
        let src_bpp = self.info.bytes_per_pixel() as usize;
        let dst_bpp = destination.info.bytes_per_pixel() as usize;
        for y in t..b {
            let source = self.pixel_offset(l, y)?;
            let target = offset(&destination.info, destination.row_bytes, l - sx, y - sy)?;
            let count = (r - l) as usize;
            for x in 0..count {
                let color =
                    color::read(&self.pixels[source + x * src_bpp..], &self.info, execution)?;
                color::write(
                    &mut destination.pixels[target + x * dst_bpp..],
                    &destination.info,
                    color,
                    execution,
                )?;
            }
        }
        Ok(true)
    }
    /// Original content-opacity query with explicit metadata early exits.
    pub fn compute_is_opaque(&self, execution: X) -> Result<bool, E> {
        if self.pixels.is_empty() || self.info.is_empty() {
            return Ok(false);
        }
        if self.info.alpha_type == A::Opaque
            || matches!(self.info.color_type, C::Rgb565 | C::Rgb888x)
        {
            return Ok(true);
        }
        for y in 0..self.info.height {
            for x in 0..self.info.width {
                if self.color(x, y, execution)?[3] != 255 {
                    return Ok(false);
                }
            }
        }
        Ok(true)
    }
    /// Original CPU bitmap resize, with unchanged source alpha and sampling
    /// policy, followed by original pixmap conversion into the destination.
    pub fn scale_pixels(
        &self,
        destination: &mut PixmapMut<'_>,
        sampling: super::RasterSampling,
        execution: X,
    ) -> Result<bool, E> {
        if self.pixels.is_empty() || destination.pixels.is_empty() {
            return Ok(false);
        }
        let resized = super::resize::resize(self, destination.info.clone(), sampling, execution)?;
        let bytes = resized.pixels()?;
        Pixmap::new(resized.info().clone(), resized.row_bytes(), &bytes)?.read_pixels(
            destination,
            [0, 0],
            execution,
        )
    }
    /// Original encoder staging in straight RGBA, optionally compositing RGB on
    /// black and forcing alpha to 255 for JPEG's explicit alpha policy.
    pub fn straight_rgba(&self, blend_on_black: bool, execution: X) -> Result<Vec<u8>, E> {
        if self.info.is_empty() || self.pixels.is_empty() {
            return Err(E::InvalidStorage);
        }
        if !self.info.color_type.supports_color() {
            return Err(E::UnsupportedFormat);
        }
        let info = self.info.with_color_type(C::Rgba8888);
        let mut result =
            super::zeros(usize::try_from(info.bytes_size()?).map_err(|_| E::Capacity)?)?;
        for y in 0..self.info.height {
            for x in 0..self.info.width {
                let mut c = self.color(x, y, execution)?;
                if blend_on_black {
                    let rgb = color::premultiply([c[0], c[1], c[2]], c[3], execution);
                    c = [rgb[0], rgb[1], rgb[2], 255];
                }
                let at = (y as usize * self.info.width as usize + x as usize) * 4;
                result[at..at + 4].copy_from_slice(&c);
            }
        }
        Ok(result)
    }
}
impl<'a> PixmapMut<'a> {
    /// Borrow exclusive complete storage without taking ownership of its allocation.
    pub fn new(info: ImageInfo, row_bytes: i32, pixels: &'a mut [u8]) -> Result<Self, E> {
        let count = info.validate_storage(row_bytes)?;
        if pixels.len() < count {
            return Err(E::InsufficientPixels);
        }
        Ok(Self {
            info,
            row_bytes,
            pixels: &mut pixels[..count],
        })
    }
    /// Actual immutable metadata.
    pub fn info(&self) -> &ImageInfo {
        &self.info
    }
    /// Actual original row stride.
    pub fn row_bytes(&self) -> i32 {
        self.row_bytes
    }
    /// Borrow an immutable source view for the current exclusive lease.
    pub fn as_pixmap(&self) -> Pixmap<'_> {
        Pixmap {
            info: self.info.clone(),
            row_bytes: self.row_bytes,
            pixels: self.pixels,
        }
    }
    /// Exclusive complete current byte inventory.
    pub fn pixels_mut(&mut self) -> &mut [u8] {
        self.pixels
    }
    /// Original typed mutable span, with complete width/stride/alignment admission.
    pub fn typed_span_mut<T: bytemuck::Pod>(&mut self, x: i32, y: i32) -> Result<&mut [T], E> {
        typed_admission::<T>(&self.info, self.row_bytes)?;
        if self.info.is_empty() || self.info.bytes_per_pixel() <= 0 {
            return Ok(&mut []);
        }
        let offset = offset(&self.info, self.row_bytes, x, y)?;
        bytemuck::try_cast_slice_mut(&mut self.pixels[offset..]).map_err(|_| E::InvalidStorage)
    }
    /// Original per-pixel write with this producer's format/alpha semantics.
    pub fn set_color(&mut self, x: i32, y: i32, color: [u8; 4], execution: X) -> Result<(), E> {
        let at = offset(&self.info, self.row_bytes, x, y)?;
        color::write(&mut self.pixels[at..], &self.info, color, execution)
    }
    /// Original clamped erase. A constant packed pixel is prepared once; row
    /// padding and all pixels outside the original selected rectangle survive.
    pub fn erase(&mut self, color: [u8; 4], rectangle: [i32; 4], execution: X) -> Result<bool, E> {
        let Some([l, t, r, b]) = intersection(&self.info, rectangle) else {
            return Ok(false);
        };
        if self.pixels.is_empty() {
            return Ok(false);
        }
        if !self.info.color_type.supports_color() {
            return Ok(false);
        }
        let mut pixel = [0u8; 16];
        let bpp = self.info.bytes_per_pixel() as usize;
        color::write(&mut pixel, &self.info, color, execution)?;
        for y in t..b {
            let at = offset(&self.info, self.row_bytes, l, y)?;
            let row = &mut self.pixels[at..at + (r - l) as usize * bpp];
            for target in row.chunks_exact_mut(bpp) {
                target.copy_from_slice(&pixel[..bpp]);
            }
        }
        Ok(true)
    }
    /// Original float-color erase uses ties-to-even byte quantization.
    pub fn erase_f(
        &mut self,
        color: [f32; 4],
        rectangle: [i32; 4],
        execution: X,
    ) -> Result<bool, E> {
        self.erase(color.map(color::float_byte), rectangle, execution)
    }
    /// Borrow an exclusive subset retaining the original stride and source lifetime.
    pub fn subset(&mut self, rectangle: [i32; 4]) -> Result<Option<PixmapMut<'_>>, E> {
        let Some([l, t, r, b]) = intersection(&self.info, rectangle) else {
            return Ok(None);
        };
        let at = offset(&self.info, self.row_bytes, l, t)?;
        Ok(Some(PixmapMut::new(
            self.info.with_size([r - l, b - t]),
            self.row_bytes,
            &mut self.pixels[at..],
        )?))
    }
}
pub(super) fn offset(info: &ImageInfo, row_bytes: i32, x: i32, y: i32) -> Result<usize, E> {
    if x < 0 || y < 0 || x >= info.width || y >= info.height {
        return Err(E::Coordinates);
    }
    y.checked_mul(row_bytes)
        .and_then(|v| {
            x.checked_mul(info.bytes_per_pixel())
                .and_then(|x| v.checked_add(x))
        })
        .and_then(|v| usize::try_from(v).ok())
        .ok_or(E::Capacity)
}
pub(super) fn intersection(info: &ImageInfo, [l, t, r, b]: [i32; 4]) -> Option<[i32; 4]> {
    if info.is_empty() {
        return None;
    }
    let [l, r] = [l, r].map(|v| v.clamp(0, info.width));
    let [t, b] = [t, b].map(|v| v.clamp(0, info.height));
    (l < r && t < b).then_some([l, t, r, b])
}
fn typed_admission<T: bytemuck::Pod>(info: &ImageInfo, row_bytes: i32) -> Result<(), E> {
    if std::any::TypeId::of::<T>() == std::any::TypeId::of::<u8>()
        || info.is_empty()
        || info.bytes_per_pixel() <= 0
    {
        return Ok(());
    }
    let size = std::mem::size_of::<T>();
    if size == 0
        || info.bytes_per_pixel() as usize != size
        || (info.height > 1 && row_bytes as usize % size != 0)
    {
        return Err(E::InvalidStorage);
    }
    Ok(())
}
