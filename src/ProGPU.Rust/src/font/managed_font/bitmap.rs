//! Original embedded sbix/CBLC/CBDT selection and presentation metrics.
use super::*;
use crate::font::bytes;
use std::{io::Cursor, ops::Range};
mod cbdt;
mod sbix;

/// Original selected embedded image and metrics. Encoded bytes are a zero-copy
/// immutable slice of the actual selected font, retained beyond font disposal.
#[derive(Clone, Debug)]
pub struct ManagedBitmapGlyph {
    source: SfntFace,
    storage: crate::font::FontData,
    range: Range<usize>,
    record: BitmapRecord,
}
impl ManagedBitmapGlyph {
    /// Original PNG graphic type (including its trailing space).
    pub const PNG: u32 = 0x706e6720;
    /// Original JPEG graphic type (including its trailing space).
    pub const JPEG: u32 = 0x6a706720;
    /// Original TIFF graphic type. Selection does not prove codec support.
    pub const TIFF: u32 = 0x74696666;
    /// Actual encoded image bytes, without a copied font table or payload.
    pub fn data(&self) -> &[u8] {
        &self.storage[self.range.clone()]
    }
    /// Actual immutable bitmap payload owner, including demand-loaded sbix bytes.
    pub fn storage(&self) -> &crate::font::FontData {
        &self.storage
    }
    /// Original selected immutable font face, independent from external sbix bytes.
    pub fn source(&self) -> &SfntFace {
        &self.source
    }
    /// Original strike pixels per em.
    pub fn pixels_per_em(&self) -> u16 {
        self.record.ppem
    }
    /// Original strike pixels per inch (72 for CBDT).
    pub fn pixels_per_inch(&self) -> u16 {
        self.record.ppi
    }
    /// Original outermost sbix duplicate-record origin; zero for CBDT.
    pub fn origin_offset(&self) -> [i16; 2] {
        self.record.origin
    }
    /// Original graphic-type tag, not a guarantee about the encoded payload.
    pub fn graphic_type(&self) -> u32 {
        self.record.kind
    }
    /// Whether the source explicitly supplied CBDT horizontal metrics.
    pub fn uses_horizontal_metrics(&self) -> bool {
        self.record.horizontal
    }
    /// Original signed CBDT horizontal bearings, zero for sbix.
    pub fn bearing(&self) -> [i16; 2] {
        self.record.bearing
    }
}
/// One decoded straight-alpha RGBA8 image with original atlas presentation
/// metrics. Own/retain this result at a scene revision, not on every warm frame.
#[derive(Debug)]
pub struct DecodedBitmapGlyph {
    pixels: Vec<u8>,
    /// Actual decoded width/height, independent of font-table metric dimensions.
    pub size: [u32; 2],
    /// Original resolved baseline offset, preserving sbix/CBDT policy.
    pub bearing: [f32; 2],
    /// Original outline-framed render extent, or zero when not supplied.
    pub render_size: [f32; 2],
    /// Original strike-to-target scale, or one for outline-framed sbix images.
    pub raster_scale: f32,
}
impl DecodedBitmapGlyph {
    /// Tightly packed straight-alpha RGBA8, without ICC or EXIF transformation.
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }
    /// Transfer the actual decoded allocation without copying it.
    pub fn into_pixels(self) -> Vec<u8> {
        self.pixels
    }
}
#[derive(Clone, Debug)]
struct BitmapRecord {
    range: Range<usize>,
    ppem: u16,
    ppi: u16,
    origin: [i16; 2],
    kind: u32,
    horizontal: bool,
    bearing: [i16; 2],
}
impl ManagedFont {
    /// Convenience nominal bitmap query. Use `try_bitmap_glyph` when resident
    /// demand-loading capacity errors must remain distinct from a missing image.
    pub fn bitmap_glyph(&self, glyph: u16, target_ppem: f32) -> Option<ManagedBitmapGlyph> {
        self.try_bitmap_glyph(glyph, target_ppem).ok().flatten()
    }
    /// Original sbix-first, external resident-source, then CBLC/CBDT selection.
    /// Each source selects its nearest valid strike, favoring larger ppem on ties.
    /// Returned bytes retain their actual owner after source/cache retirement.
    pub fn try_bitmap_glyph(
        &self,
        glyph: u16,
        target_ppem: f32,
    ) -> Result<Option<ManagedBitmapGlyph>, FontError> {
        if glyph >= self.metadata().glyph_count {
            return Ok(None);
        }
        if self
            .root
            .resident
            .as_ref()
            .is_none_or(|source| source.glyph == glyph)
        {
            if let Some((_, span)) = self.source().table_span(Tag::new(*b"sbix")) {
                let data = &self.source().data()[span.offset..span.offset + span.length];
                if let Some(record) =
                    sbix::select(data, self.metadata().glyph_count, glyph, target_ppem)
                {
                    return Ok(Some(self.bitmap_record(
                        self.source().storage().clone(),
                        span.offset,
                        record,
                    )));
                }
            }
        }
        if let Some(source) = &self.root.resident {
            if let Some(storage) = source.source.glyph_data(glyph)? {
                if let Some(record) =
                    sbix::select(&storage, self.metadata().glyph_count, glyph, target_ppem)
                {
                    return Ok(Some(self.bitmap_record(storage, 0, record)));
                }
            }
        }
        if let Some((_, span)) = self.source().table_span(Tag::new(*b"CBDT")) {
            if let Some(cblc) = self.source().table(Tag::new(*b"CBLC")) {
                let data = &self.source().data()[span.offset..span.offset + span.length];
                if let Some(record) = cbdt::select(cblc.bytes, data, glyph, target_ppem) {
                    return Ok(Some(self.bitmap_record(
                        self.source().storage().clone(),
                        span.offset,
                        record,
                    )));
                }
            }
        }
        Ok(None)
    }
    fn bitmap_record(
        &self,
        storage: crate::font::FontData,
        offset: usize,
        record: BitmapRecord,
    ) -> ManagedBitmapGlyph {
        let range = offset + record.range.start..offset + record.range.end;
        ManagedBitmapGlyph {
            source: self.source().clone(),
            storage,
            range,
            record,
        }
    }
    /// Decode the original selected image and resolve original sbix/CBDT metrics.
    /// Failed decoding returns None rather than trying another strike/table.
    /// Resource-capacity failures remain errors. The Rust codec dependency has
    /// independent byte/error/performance qualification against the managed codec.
    pub fn decode_bitmap_glyph(
        &self,
        glyph: u16,
        target_ppem: f32,
    ) -> Result<Option<DecodedBitmapGlyph>, FontError> {
        if !target_ppem.is_finite() || target_ppem <= 0.0 {
            return Ok(None);
        }
        let Some(bitmap) = self.try_bitmap_glyph(glyph, target_ppem)? else {
            return Ok(None);
        };
        let mut reader =
            match image::ImageReader::new(Cursor::new(bitmap.data())).with_guessed_format() {
                Ok(reader) => reader,
                Err(_) => return Ok(None),
            };
        // Preserve original admission: no unrelated default 512 MiB codec cap.
        // Address-space/allocation failures still surface as Capacity below.
        reader.no_limits();
        // The original codec tries headerless TGA last when no recognized
        // signature matched. Keep this distinct from trusting the font tag.
        if reader.format().is_none() {
            reader.set_format(image::ImageFormat::Tga);
        }
        let decoded = match reader.decode() {
            Ok(image) => image,
            Err(image::ImageError::Limits(_)) => return Err(FontError::Capacity),
            Err(_) => return Ok(None),
        };
        let size = [decoded.width(), decoded.height()];
        if size.contains(&0) || size.iter().any(|&v| v > i32::MAX as u32) {
            return Ok(None);
        }
        let length = u64::from(size[0]) * u64::from(size[1]) * 4;
        if length > i32::MAX as u64 {
            return Err(FontError::Capacity);
        }
        let pixels = decoded.into_rgba8().into_raw();
        if pixels.len() as u64 != length {
            return Ok(None);
        }
        let scale = if bitmap.record.ppem > 0 {
            target_ppem / f32::from(bitmap.record.ppem)
        } else {
            1.0
        };
        let origin = bitmap.record.origin;
        let mut bearing = if bitmap.record.horizontal {
            [
                f32::from(bitmap.record.bearing[0]),
                -f32::from(bitmap.record.bearing[1]),
            ]
        } else {
            [
                -f32::from(origin[0]),
                i32::from(origin[1]).wrapping_sub(size[1] as i32) as f32,
            ]
        };
        let mut render_size = [0.0; 2];
        let mut raster_scale = scale;
        if !bitmap.record.horizontal
            && self.metadata().units_per_em > 0
            && let Some(bounds) = self.glyph_bounds(glyph)?
        {
            let outline_scale = target_ppem / f32::from(self.metadata().units_per_em);
            bearing = [
                f32::from(bounds[0]) * outline_scale - f32::from(origin[0]) * scale,
                -f32::from(bounds[3]) * outline_scale + f32::from(origin[1]) * scale,
            ];
            render_size = [
                (i32::from(bounds[2]) - i32::from(bounds[0])) as f32 * outline_scale,
                (i32::from(bounds[3]) - i32::from(bounds[1])) as f32 * outline_scale,
            ];
            raster_scale = 1.0;
        }
        Ok(Some(DecodedBitmapGlyph {
            pixels,
            size,
            bearing,
            render_size,
            raster_scale,
        }))
    }
}
fn pick(best: &mut Option<BitmapRecord>, distance: &mut f32, candidate: BitmapRecord, target: f32) {
    let next = (f32::from(candidate.ppem) - target).abs();
    if best
        .as_ref()
        .is_none_or(|old| next < *distance || (next == *distance && candidate.ppem > old.ppem))
    {
        *best = Some(candidate);
        *distance = next;
    }
}
