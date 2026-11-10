//! Original native retained RGBA glyph transport; no bitmap raster substitute.
use super::*;
use bytemuck::{Pod, Zeroable};
/// Original 48-byte scene descriptor, including full row stride and reserved words.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct NativeColorGlyphBitmap {
    pub pixel_offset: u64,
    pub width: u32,
    pub height: u32,
    pub row_bytes: u32,
    pub reserved0: u32,
    pub bearing: [f32; 2],
    pub render_size: [f32; 2],
    pub reserved: [u32; 2],
}
const _: () = {
    assert!(size_of::<NativeColorGlyphBitmap>() == 48);
};
impl NativeColorGlyphBitmap {
    pub fn pixel_range(self, pixel_bytes: usize) -> Result<std::ops::Range<usize>, Error> {
        if self.width == 0
            || self.height == 0
            || self.width > 16384
            || self.height > 16384
            || self.row_bytes < self.width * 4
            || self.reserved0 != 0
            || self.reserved != [0; 2]
            || !self
                .bearing
                .into_iter()
                .chain(self.render_size)
                .all(f32::is_finite)
            || self.render_size.into_iter().any(|v| v < 0.0)
        {
            return Err(Error::InvalidInput);
        }
        let required =
            u64::from(self.row_bytes) * u64::from(self.height - 1) + u64::from(self.width) * 4;
        let end = self
            .pixel_offset
            .checked_add(required)
            .ok_or(Error::Capacity)?;
        if end > pixel_bytes as u64 {
            return Err(Error::InvalidInput);
        }
        Ok(self.pixel_offset as usize..end as usize)
    }
}
/// Explicit original already-positioned bitmap reference and frozen physical
/// atlas-to-logical ratio. This is not inferred from font em or image dimensions.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeColorGlyphPlacement {
    pub bitmap_index: u32,
    pub atlas_to_logical_scale: f32,
}
#[derive(Debug)]
pub struct NativeColorGlyphBatch {
    source: Arc<NativeDesignGeometry>,
    dpi_scale: f32,
    records: Arc<[NativeColorGlyphBitmap]>,
    pixels: Arc<[u8]>,
    placements: Arc<[Option<NativeColorGlyphPlacement>]>,
    resolve_source_positions: bool,
}
impl NativeColorGlyphBatch {
    /// Retain complete actual pixels and descriptors, admitting every later
    /// record and every occurrence before publication. None preserves native
    /// monochrome/no-ink policy for that slot; duplicate references remain valid.
    pub fn new(
        source: Arc<NativeDesignGeometry>,
        dpi_scale: f32,
        records: Arc<[NativeColorGlyphBitmap]>,
        pixels: Arc<[u8]>,
        placements: Arc<[Option<NativeColorGlyphPlacement>]>,
    ) -> Result<Arc<Self>, Error> {
        if !dpi_scale.is_finite()
            || dpi_scale <= 0.0
            || records.len() > 1 << 20
            || pixels.len() > 256 * 1024 * 1024
            || placements.len() != source.slots().len()
        {
            return Err(Error::InvalidInput);
        }
        for &r in records.iter() {
            r.pixel_range(pixels.len())?;
        }
        for (i, p) in placements.iter().enumerate() {
            if let Some(p) = p {
                if p.bitmap_index as usize >= records.len()
                    || !p.atlas_to_logical_scale.is_finite()
                    || p.atlas_to_logical_scale <= 0.0
                    || source.slots()[i].style_index.is_none()
                    || source.glyph_source(i as u32).is_none()
                {
                    return Err(Error::InvalidInput);
                }
            }
        }
        Ok(Arc::new(Self {
            source,
            dpi_scale,
            records,
            pixels,
            placements,
            resolve_source_positions: false,
        }))
    }
    /// Color-first source adapter: positions are the actual unsnapped native
    /// paragraph slots, and resolve once through NativeAtlasFrame at target DPI.
    /// Existing `new` keeps already-positioned retained-record behavior.
    pub fn from_source_positions(
        source: Arc<NativeDesignGeometry>,
        dpi_scale: f32,
        records: Arc<[NativeColorGlyphBitmap]>,
        pixels: Arc<[u8]>,
        placements: Arc<[Option<NativeColorGlyphPlacement>]>,
    ) -> Result<Arc<Self>, Error> {
        let mut result = Self::new(source, dpi_scale, records, pixels, placements)?;
        Arc::get_mut(&mut result)
            .expect("new color batch has one owner")
            .resolve_source_positions = true;
        Ok(result)
    }
    pub fn resolves_source_positions(&self) -> bool {
        self.resolve_source_positions
    }
    pub fn source(&self) -> &Arc<NativeDesignGeometry> {
        &self.source
    }
    pub fn dpi_scale(&self) -> f32 {
        self.dpi_scale
    }
    pub fn records(&self) -> &[NativeColorGlyphBitmap] {
        &self.records
    }
    pub fn pixels(&self) -> &[u8] {
        &self.pixels
    }
    pub fn placements(&self) -> &[Option<NativeColorGlyphPlacement>] {
        &self.placements
    }
    pub fn bitmap_for_slot(&self, slot: u32) -> Option<NativeColorGlyphPlacement> {
        self.placements.get(slot as usize).copied().flatten()
    }
    pub fn copy_into(
        &self,
        records: &mut [NativeColorGlyphBitmap],
        pixels: &mut [u8],
        placements: &mut [Option<NativeColorGlyphPlacement>],
    ) -> Result<(), Error> {
        if records.len() < self.records.len()
            || pixels.len() < self.pixels.len()
            || placements.len() < self.placements.len()
        {
            return Err(Error::Capacity);
        }
        records[..self.records.len()].copy_from_slice(&self.records);
        pixels[..self.pixels.len()].copy_from_slice(&self.pixels);
        placements[..self.placements.len()].copy_from_slice(&self.placements);
        Ok(())
    }
}
