//! Original complete pixel metadata; byte storage capability is independent of
//! the narrower original color conversion and resize implementations.
use super::{ColorSpace, RasterError as E};
/// All original SKColorType numeric identities.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum PixelColorType {
    /// Unknown layout.
    #[default]
    Unknown = 0,
    /// Eight-bit alpha.
    Alpha8 = 1,
    /// Packed 5/6/5 RGB.
    Rgb565 = 2,
    /// Packed four-bit alpha/red/green/blue.
    Argb4444 = 3,
    /// Eight-bit RGBA.
    Rgba8888 = 4,
    /// Eight-bit RGB plus ignored fourth channel.
    Rgb888x = 5,
    /// Eight-bit BGRA.
    Bgra8888 = 6,
    /// Packed 10/10/10/2 RGBA.
    Rgba1010102 = 7,
    /// Packed 10-bit RGB plus ignored bits.
    Rgb101010x = 8,
    /// Eight-bit grayscale.
    Gray8 = 9,
    /// Half-float RGBA.
    RgbaF16 = 10,
    /// Clamped half-float RGBA.
    RgbaF16Clamped = 11,
    /// Float RGBA.
    RgbaF32 = 12,
    /// Eight-bit red/green.
    Rg88 = 13,
    /// Half-float alpha.
    AlphaF16 = 14,
    /// Half-float red/green.
    RgF16 = 15,
    /// Sixteen-bit alpha.
    Alpha16 = 16,
    /// Sixteen-bit red/green.
    Rg1616 = 17,
    /// Sixteen-bit RGBA.
    Rgba16161616 = 18,
    /// Packed 10/10/10/2 BGRA.
    Bgra1010102 = 19,
    /// Packed 10-bit BGR.
    Bgr101010x = 20,
    /// Original extended-range packed BGR.
    Bgr101010xXr = 21,
    /// Original encoded sRGB RGBA bytes.
    Srgba8888 = 22,
    /// Eight-bit red.
    R8Unorm = 23,
    /// Original ten-bit RGBA with six unused bits per lane.
    Rgba10x6 = 24,
    /// Original extended-range ten-bit BGRA.
    Bgra10101010Xr = 25,
    /// Half-float RGB plus ignored lane.
    RgbF16F16F16x = 26,
    /// Sixteen-bit red.
    R16Unorm = 27,
    /// Half-float red.
    RF16 = 28,
}
/// Original alpha metadata, independent of selected pixel-format byte layout.
#[repr(u32)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Hash)]
pub enum PixelAlphaType {
    /// Unspecified interpretation.
    #[default]
    Unknown = 0,
    /// Fully opaque interpretation.
    Opaque = 1,
    /// RGB is stored premultiplied by alpha.
    Premultiplied = 2,
    /// RGB is stored independently of alpha.
    Straight = 3,
}
impl PixelColorType {
    /// Original metadata-only GL sized-format mapping; zero retains an explicitly
    /// unsupported mapping and does not qualify a GL texture producer.
    pub const fn gl_sized_format(self) -> u32 {
        use PixelColorType::*;
        match self {
            Unknown | Rgb101010x | RgbaF32 | Bgra1010102 | Bgr101010x | Bgr101010xXr | Rgba10x6
            | Bgra10101010Xr | RgbF16F16F16x => 0,
            Alpha8 => 0x803c,
            Rgb565 => 0x8d62,
            Argb4444 => 0x8056,
            Rgba8888 => 0x8058,
            Rgb888x => 0x8051,
            Bgra8888 => 0x93a1,
            Rgba1010102 => 0x8059,
            Gray8 => 0x8040,
            RgbaF16 | RgbaF16Clamped => 0x881a,
            Rg88 => 0x822b,
            AlphaF16 | RF16 => 0x822d,
            RgF16 => 0x822f,
            Alpha16 | R16Unorm => 0x822a,
            Rg1616 => 0x822c,
            Rgba16161616 => 0x805b,
            Srgba8888 => 0x8c43,
            R8Unorm => 0x8229,
        }
    }
    /// Exact original byte size, including formats with no CPU color converter.
    pub const fn bytes_per_pixel(self) -> i32 {
        use PixelColorType::*;
        match self {
            Unknown => 0,
            Alpha8 | Gray8 | R8Unorm => 1,
            Rgb565 | Argb4444 | Rg88 | AlphaF16 | Alpha16 | R16Unorm | RF16 => 2,
            Rgba8888 | Rgb888x | Bgra8888 | Rgba1010102 | Rgb101010x | RgF16 | Rg1616
            | Bgra1010102 | Bgr101010x | Bgr101010xXr | Srgba8888 => 4,
            RgbaF16 | RgbaF16Clamped | Rgba16161616 | Rgba10x6 | Bgra10101010Xr | RgbF16F16F16x => {
                8
            }
            RgbaF32 => 16,
        }
    }
    /// Exact original power-of-two byte shift; Unknown selects zero.
    pub const fn bit_shift_per_pixel(self) -> u32 {
        match self.bytes_per_pixel() {
            2 => 1,
            4 => 2,
            8 => 3,
            16 => 4,
            _ => 0,
        }
    }
    /// Original format-specific alpha normalization helper. ImageInfo itself
    /// retains its supplied raw alpha type rather than applying this implicitly.
    pub const fn alpha_type(self, alpha: PixelAlphaType) -> PixelAlphaType {
        use PixelColorType::*;
        match self {
            Unknown => PixelAlphaType::Unknown,
            Rgb565 | Rgb888x | Rgb101010x | Gray8 | Rg88 | RgF16 | Rg1616 | Bgr101010x
            | Bgr101010xXr | R8Unorm | RgbF16F16F16x | R16Unorm | RF16 => PixelAlphaType::Opaque,
            Alpha8 | AlphaF16 | Alpha16 if matches!(alpha, PixelAlphaType::Straight) => {
                PixelAlphaType::Premultiplied
            }
            _ => alpha,
        }
    }
    /// Original pixmap conversion/copy capability, independent of byte storage.
    pub const fn supports_color(self) -> bool {
        matches!(
            self,
            Self::Alpha8
                | Self::Rgb565
                | Self::Argb4444
                | Self::Rgba8888
                | Self::Rgb888x
                | Self::Bgra8888
                | Self::Gray8
                | Self::Rg88
                | Self::Srgba8888
                | Self::R8Unorm
        )
    }
    /// Original four-format bitmap resize producer capability.
    pub const fn supports_resize(self) -> bool {
        matches!(
            self,
            Self::Rgb565 | Self::Rgba8888 | Self::Rgb888x | Self::Bgra8888
        )
    }
}
/// Complete original signed image metadata. Equality preserves actual selected
/// color-space owner identity rather than comparing equivalent transfer values.
#[derive(Clone, Debug, Default)]
pub struct ImageInfo {
    /// Original signed width.
    pub width: i32,
    /// Original signed height.
    pub height: i32,
    /// Original exact pixel layout.
    pub color_type: PixelColorType,
    /// Original exact alpha interpretation.
    pub alpha_type: PixelAlphaType,
    /// Actual selected color-space owner, or absent metadata.
    pub color_space: Option<ColorSpace>,
}
impl PartialEq for ImageInfo {
    fn eq(&self, other: &Self) -> bool {
        self.width == other.width
            && self.height == other.height
            && self.color_type == other.color_type
            && self.alpha_type == other.alpha_type
            && match (&self.color_space, &other.color_space) {
                (None, None) => true,
                (Some(a), Some(b)) => a.same_owner(b),
                _ => false,
            }
    }
}
impl Eq for ImageInfo {}
impl ImageInfo {
    /// Original fixed platform channel shifts in red/green/blue/alpha order.
    pub const PLATFORM_CHANNEL_SHIFTS: [u32; 4] = [0, 8, 16, 24];
    /// Original platform color type, independent of host OS and device format.
    pub const PLATFORM_COLOR_TYPE: PixelColorType = PixelColorType::Rgba8888;
    /// Exact original number of bits per pixel.
    pub fn bits_per_pixel(&self) -> i32 {
        self.bytes_per_pixel() * 8
    }
    /// Exact original shift used for pixel-sized address arithmetic.
    pub fn bit_shift_per_pixel(&self) -> u32 {
        self.color_type.bit_shift_per_pixel()
    }
    /// Original platform RGBA byte order and premultiplied default.
    pub fn new(width: i32, height: i32) -> Self {
        Self {
            width,
            height,
            color_type: PixelColorType::Rgba8888,
            alpha_type: PixelAlphaType::Premultiplied,
            color_space: None,
        }
    }
    /// Retain all metadata explicitly without allocation or implicit coercion.
    pub fn with_format(
        width: i32,
        height: i32,
        color_type: PixelColorType,
        alpha_type: PixelAlphaType,
        color_space: Option<ColorSpace>,
    ) -> Self {
        Self {
            width,
            height,
            color_type,
            alpha_type,
            color_space,
        }
    }
    /// Original nonpositive-dimension empty classification.
    pub fn is_empty(&self) -> bool {
        self.width <= 0 || self.height <= 0
    }
    /// Exact metadata opacity, independent of current pixel contents.
    pub fn is_opaque(&self) -> bool {
        self.alpha_type == PixelAlphaType::Opaque
    }
    /// Exact original bytes per pixel.
    pub fn bytes_per_pixel(&self) -> i32 {
        self.color_type.bytes_per_pixel()
    }
    /// Original declared packed row bytes, checked as a signed 32-bit count.
    pub fn row_bytes(&self) -> Result<i32, E> {
        self.width
            .checked_mul(self.bytes_per_pixel())
            .ok_or(E::Capacity)
    }
    /// Original 64-bit row count.
    pub fn row_bytes64(&self) -> i64 {
        i64::from(self.width) * i64::from(self.bytes_per_pixel())
    }
    /// Original signed tightly packed image byte count.
    pub fn bytes_size(&self) -> Result<i32, E> {
        self.width
            .checked_mul(self.height)
            .and_then(|v| v.checked_mul(self.bytes_per_pixel()))
            .ok_or(E::Capacity)
    }
    /// Original 64-bit tightly packed count with explicit overflow rejection.
    pub fn bytes_size64(&self) -> Result<i64, E> {
        i64::from(self.width)
            .checked_mul(i64::from(self.height))
            .and_then(|v| v.checked_mul(i64::from(self.bytes_per_pixel())))
            .ok_or(E::Capacity)
    }
    /// Original physical size.
    pub fn size(&self) -> [i32; 2] {
        [self.width, self.height]
    }
    /// Original left/top/right/bottom rectangle.
    pub fn rect(&self) -> [i32; 4] {
        [0, 0, self.width, self.height]
    }
    /// Copy metadata with a new original size.
    pub fn with_size(&self, size: [i32; 2]) -> Self {
        let mut v = self.clone();
        v.width = size[0];
        v.height = size[1];
        v
    }
    /// Copy metadata with a new channel layout, leaving alpha unchanged.
    pub fn with_color_type(&self, color_type: PixelColorType) -> Self {
        let mut v = self.clone();
        v.color_type = color_type;
        v
    }
    /// Copy metadata with a different actual selected color owner.
    pub fn with_color_space(&self, color_space: Option<ColorSpace>) -> Self {
        let mut v = self.clone();
        v.color_space = color_space;
        v
    }
    /// Copy metadata with a new original alpha interpretation.
    pub fn with_alpha_type(&self, alpha_type: PixelAlphaType) -> Self {
        let mut v = self.clone();
        v.alpha_type = alpha_type;
        v
    }
    /// Original last-row-excludes-padding storage size.
    pub fn byte_count(&self, row_bytes: i32) -> Result<usize, E> {
        if self.is_empty() || self.bytes_per_pixel() <= 0 || row_bytes <= 0 {
            return Ok(0);
        }
        let bytes = (self.height - 1)
            .checked_mul(row_bytes)
            .and_then(|v| v.checked_add(self.row_bytes().ok()?))
            .ok_or(E::Capacity)?;
        usize::try_from(bytes).map_err(|_| E::InvalidStorage)
    }
    /// Complete original bitmap storage admission, including empty/Unknown
    /// metadata; positive-height rows still require their full declared width.
    pub fn validate_storage(&self, row_bytes: i32) -> Result<usize, E> {
        if self.width < 0
            || self.height < 0
            || row_bytes < 0
            || (self.height > 0 && row_bytes < self.row_bytes()?)
        {
            return Err(E::InvalidStorage);
        }
        self.byte_count(row_bytes)
    }
}
