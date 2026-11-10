//! Native raw-face hdmx/gasp admission; complete table inventories precede output.
use super::*;
fn word(data: &[u8], offset: usize) -> Result<u16, FontError> {
    Ok(u16::from_be_bytes(
        data.get(offset..offset.checked_add(2).ok_or(FontError::InvalidFace)?)
            .ok_or(FontError::InvalidFace)?
            .try_into()
            .map_err(|_| FontError::InvalidFace)?,
    ))
}
fn dword(data: &[u8], offset: usize) -> Result<u32, FontError> {
    Ok(u32::from_be_bytes(
        data.get(offset..offset.checked_add(4).ok_or(FontError::InvalidFace)?)
            .ok_or(FontError::InvalidFace)?
            .try_into()
            .map_err(|_| FontError::InvalidFace)?,
    ))
}
// General SfntFace::table intentionally skips invalid extents. These native
// optional queries retain a declared malformed/duplicate entry as an error.
fn raw_table(font: &SfntFace, tag: Tag) -> Result<(Option<&[u8]>, bool), FontError> {
    let data = font.data();
    let start = (font.face_offset() as usize)
        .checked_add(12)
        .ok_or(FontError::InvalidFace)?;
    let mut found = None;
    let mut variable = false;
    for index in 0..usize::from(font.table_count()) {
        let at = start
            .checked_add(index * 16)
            .ok_or(FontError::InvalidFace)?;
        let current = dword(data, at)?;
        variable |= current == Tag::new(*b"fvar").0;
        if current != tag.0 {
            continue;
        }
        let offset = dword(data, at + 8)? as usize;
        let length = dword(data, at + 12)? as usize;
        if found.is_some() {
            return Err(FontError::InvalidFace);
        }
        found = Some(
            data.get(offset..offset.checked_add(length).ok_or(FontError::InvalidFace)?)
                .ok_or(FontError::InvalidFace)?,
        );
    }
    Ok((found, variable))
}
/// Exact original hdmx row borrowing its immutable selected font generation.
/// These byte widths are font-authored device metadata, not executed hints or
/// inferred source GDI/GDI+/DirectWrite measurements.
#[derive(Clone, Copy, Debug)]
pub struct NativeHorizontalDeviceMetrics<'a> {
    font: &'a SfntFace,
    widths: &'a [u8],
    pixels_per_em: u8,
    maximum_width: u8,
}
impl NativeHorizontalDeviceMetrics<'_> {
    /// Exact immutable selected source owner.
    pub fn font(&self) -> &SfntFace {
        self.font
    }
    /// Original exact positive pixel-em size, without nearest-size selection.
    pub fn pixels_per_em(&self) -> u8 {
        self.pixels_per_em
    }
    /// Original maximum, checked against every original glyph width.
    pub fn maximum_width(&self) -> u8 {
        self.maximum_width
    }
    /// Complete original maxp-sized width inventory, unchanged and borrowed.
    pub fn glyph_widths(&self) -> &[u8] {
        self.widths
    }
    /// Exact selected glyph width, with out-of-inventory identity staying absent.
    pub fn glyph_width(&self, glyph: u16) -> Option<u8> {
        self.widths.get(usize::from(glyph)).copied()
    }
}
/// Original native hdmx query. Validates the raw selected-face directory, version,
/// full stride/length, strictly ordered sizes, every row maximum and all padding
/// before returning the exact selected size. Missing table/size stays separate.
pub fn native_horizontal_device_metrics(
    font: &SfntFace,
    pixels_per_em: u16,
) -> Result<Option<NativeHorizontalDeviceMetrics<'_>>, FontError> {
    if pixels_per_em == 0 {
        return Err(FontError::InvalidArgument);
    }
    let glyphs = usize::from(
        font.glyph_count()
            .filter(|&count| count != 0)
            .ok_or(FontError::InvalidFace)?,
    );
    let (table, _) = raw_table(font, Tag::new(*b"hdmx"))?;
    let Some(table) = table else {
        return Ok(None);
    };
    if table.len() < 8 || word(table, 0)? != 0 {
        return Err(FontError::InvalidFace);
    }
    let count = usize::from(word(table, 2)?);
    let stride = dword(table, 4)? as usize;
    let payload = glyphs + 2;
    if stride < payload
        || stride % 4 != 0
        || count > (table.len() - 8) / stride
        || table.len() - 8 != count * stride
    {
        return Err(FontError::InvalidFace);
    }
    let mut previous = 0;
    let mut selected = None;
    for index in 0..count {
        let record = &table[8 + index * stride..8 + (index + 1) * stride];
        let size = record[0];
        let maximum = record[1];
        if size <= previous {
            return Err(FontError::InvalidFace);
        }
        previous = size;
        let widths = &record[2..payload];
        if widths.iter().copied().max().unwrap_or(0) != maximum
            || record[payload..].iter().any(|&value| value != 0)
        {
            return Err(FontError::InvalidFace);
        }
        if u16::from(size) == pixels_per_em {
            selected = Some(NativeHorizontalDeviceMetrics {
                font,
                widths,
                pixels_per_em: size,
                maximum_width: maximum,
            });
        }
    }
    Ok(selected)
}
/// Original native static gasp query, using native glyf/loca presence admission
/// independently of ManagedFont metadata. Raw fvar presence remains excluded even
/// if a general accessor would ignore its malformed extent. The shared original
/// gasp parser validates all later records before any earlier match is published.
pub fn native_static_rasterization_recommendations(
    font: &SfntFace,
    pixels_per_em: u16,
) -> Result<Option<FontRasterizationRange>, FontError> {
    if pixels_per_em == 0 {
        return Err(FontError::InvalidArgument);
    }
    if font.data().is_empty() {
        return Err(FontError::InvalidFace);
    }
    let (table, variable) = raw_table(font, Tag::new(*b"gasp"))?;
    let Some(table) = table else {
        return Ok(None);
    };
    let selected = read_rasterization_recommendations(table, pixels_per_em)?;
    if variable
        || selected.is_none()
        || font.table(Tag::new(*b"glyf")).is_none()
        || font.table(Tag::new(*b"loca")).is_none()
    {
        return Ok(None);
    }
    Ok(selected)
}
