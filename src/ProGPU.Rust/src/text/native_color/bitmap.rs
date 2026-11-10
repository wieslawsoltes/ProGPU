//! Owned bitmap decoder boundary and original TtfFont presentation equations.
use super::*;
use crate::image::{EncodedRasterDecoder, Image, ImageAlpha};
/// Actual decoder route. Native PNG retains its original owned algorithm;
/// JPEG uses the safe dependency boundary. TIFF remains an explicit provider.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeBitmapDecodeExecution {
    NativePng,
    /// Safe owned JPEG adapter; this is not an original C++ JPEG codec.
    JpegCodec,
    ExternalRaster,
}
#[derive(Clone, Debug)]
pub struct NativeDecodedColorBitmap {
    image: Image,
    source: Arc<NativeDesignGlyphSource>,
    pub graphic_type: crate::font::Tag,
    pub pixels_per_em: u16,
    pub pixels_per_inch: u16,
    pub bearing: [f32; 2],
    pub render_size: [f32; 2],
    pub raster_scale: f32,
    pub execution: NativeBitmapDecodeExecution,
}
impl NativeDecodedColorBitmap {
    pub fn image(&self) -> &Image {
        &self.image
    }
    pub fn source(&self) -> &Arc<NativeDesignGlyphSource> {
        &self.source
    }
}
pub(super) fn decode(
    source: Arc<NativeDesignGlyphSource>,
    target: f32,
    decoder: Option<&dyn EncodedRasterDecoder>,
) -> Result<Option<NativeDecodedColorBitmap>> {
    let font = source.font();
    let glyph = source.glyph();
    // A selected sbix payload owns decoder success/failure. Failure to decode it
    // never retries CBDT, matching the original TryGetBitmapGlyph boundary.
    let data = match font
        .native_sbix_glyph(glyph, target)
        .or_else(|_| font.native_cbdt_glyph(glyph, target))
    {
        Ok(data) => data,
        Err(_) => return Ok(None),
    };
    let (image, execution) = if let Some(decoder) = decoder {
        let decoded = match decoder.decode_rgba(data.bytes) {
            Ok(v) => v,
            Err(crate::image::EncodedImageError::Capacity)
            | Err(crate::image::EncodedImageError::Image(Error::Capacity)) => {
                return Err(Error::Capacity.into());
            }
            Err(_) => return Ok(None),
        };
        let length = u64::from(decoded.size[0])
            .checked_mul(u64::from(decoded.size[1]))
            .and_then(|n| n.checked_mul(4))
            .ok_or(Error::Capacity)?;
        if decoded.size.contains(&0) || decoded.rgba.len() as u64 != length {
            return Ok(None);
        }
        (
            Image::rgba8(decoded.size, ImageAlpha::Straight, decoded.rgba)?,
            NativeBitmapDecodeExecution::ExternalRaster,
        )
    } else if data.graphic_type == crate::font::Tag::new(*b"png ") {
        let image = match Image::decode_png(data.bytes) {
            Ok(v) => v,
            Err(crate::image::PngImageError::Allocation)
            | Err(crate::image::PngImageError::Image(Error::Capacity)) => {
                return Err(Error::Capacity.into());
            }
            Err(_) => return Ok(None),
        };
        (image, NativeBitmapDecodeExecution::NativePng)
    } else if data.graphic_type == crate::font::Tag::new(*b"jpg ") {
        let image = match Image::decode_jpeg(data.bytes) {
            Ok(image) => image,
            Err(crate::image::JpegDecodeError::Capacity)
            | Err(crate::image::JpegDecodeError::Image(Error::Capacity)) => {
                return Err(Error::Capacity.into());
            }
            Err(_) => return Ok(None),
        };
        (image, NativeBitmapDecodeExecution::JpegCodec)
    } else {
        // A TIFF tag selects a source payload; the original default raster
        // provider cannot decode TIFF. Keep the selected-source failure (and no
        // CBDT retry), while the explicit decoder above may supply that capability.
        return Ok(None);
    };
    let [width, height] = image.size();
    if width == 0
        || height == 0
        || width > 16_384
        || height > 16_384
        || image.pixels().len()
            != (width as usize)
                .checked_mul(height as usize)
                .and_then(|v| v.checked_mul(4))
                .ok_or(Error::Capacity)?
    {
        return Err(Error::InvalidInput.into());
    }
    let bitmap_scale = if data.pixels_per_em > 0 {
        target / f32::from(data.pixels_per_em)
    } else {
        1.0
    };
    let mut bearing = if data.uses_horizontal_metrics {
        [f32::from(data.bearing_x), -f32::from(data.bearing_y)]
    } else {
        [
            -f32::from(data.origin_offset_x),
            f32::from(data.origin_offset_y) - height as f32,
        ]
    };
    let mut render_size = [0.0; 2];
    let mut raster_scale = bitmap_scale;
    let units = font.header_metrics().map_or(0, |v| v.units_per_em);
    if !data.uses_horizontal_metrics && units > 0 {
        if let Some([xmin, ymin, xmax, ymax]) = outline_bounds(&source)? {
            let scale = target / f32::from(units);
            bearing = [
                f32::from(xmin) * scale - f32::from(data.origin_offset_x) * bitmap_scale,
                -f32::from(ymax) * scale + f32::from(data.origin_offset_y) * bitmap_scale,
            ];
            render_size = [
                (i32::from(xmax) - i32::from(xmin)) as f32 * scale,
                (i32::from(ymax) - i32::from(ymin)) as f32 * scale,
            ];
            raster_scale = 1.0;
        }
    }
    let graphic_type = data.graphic_type;
    let pixels_per_em = data.pixels_per_em;
    let pixels_per_inch = data.pixels_per_inch;
    if !bearing
        .into_iter()
        .chain(render_size)
        .chain([raster_scale])
        .all(f32::is_finite)
        || raster_scale <= 0.0
    {
        return Err(Error::InvalidInput.into());
    }
    Ok(Some(NativeDecodedColorBitmap {
        image,
        source,
        graphic_type,
        pixels_per_em,
        pixels_per_inch,
        bearing,
        render_size,
        raster_scale,
        execution,
    }))
}
fn outline_bounds(source: &NativeDesignGlyphSource) -> Result<Option<[i16; 4]>> {
    let font = source.font();
    let true_type = font.table(crate::font::Tag::new(*b"glyf")).is_some()
        && font.table(crate::font::Tag::new(*b"loca")).is_some();
    if true_type && source.normalized_coordinates().iter().all(|&v| v == 0) {
        return Ok(font.glyph_bounds(source.glyph()));
    }
    let outline = match NativeDesignOutline::new(
        font.clone(),
        source.glyph(),
        source.normalized_coordinates(),
    ) {
        Ok(v) => v,
        Err(NativeDesignGeometryError::Font(FontError::UnsupportedOutline)) => return Ok(None),
        Err(e) => return Err(e.into()),
    };
    if !outline.has_ink() {
        return Ok(None);
    }
    let mut bounds = [f32::MAX, f32::MAX, f32::MIN, f32::MIN];
    for segment in outline.segments() {
        for p in &segment.points[..segment.kind as usize + 2] {
            if p[0] < bounds[0] {
                bounds[0] = p[0];
            }
            if p[1] < bounds[1] {
                bounds[1] = p[1];
            }
            if bounds[2] < p[0] {
                bounds[2] = p[0];
            }
            if bounds[3] < p[1] {
                bounds[3] = p[1];
            }
        }
    }
    let result = [
        bounds[0].floor() as i16,
        bounds[1].floor() as i16,
        bounds[2].ceil() as i16,
        bounds[3].ceil() as i16,
    ];
    Ok((result[2] > result[0] && result[3] > result[1]).then_some(result))
}
