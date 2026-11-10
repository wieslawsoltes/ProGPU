//! Native COLR v0-prefix lookup and CPAL v0/v1 palettes.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeColorRgba8 {
    pub red: u8,
    pub green: u8,
    pub blue: u8,
    pub alpha: u8,
}
impl Default for NativeColorRgba8 {
    fn default() -> Self {
        Self {
            red: 255,
            green: 255,
            blue: 255,
            alpha: 255,
        }
    }
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeColorPaletteOverride {
    pub palette_entry_index: u16,
    pub argb: u32,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeColorGlyphLayer {
    pub glyph_index: u16,
    pub palette_entry_index: u16,
    pub color: NativeColorRgba8,
    pub uses_foreground_color: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativePaletteColor {
    pub color: NativeColorRgba8,
    pub uses_foreground_color: bool,
}
/// Validated complete palette-index inventory. Payloads remain borrowed.
#[derive(Clone, Copy, Debug)]
pub struct NativeColorPalette<'a> {
    bytes: &'a [u8],
    records_offset: usize,
    entries: u16,
    palettes: u16,
}
impl<'a> NativeColorPalette<'a> {
    pub fn parse(bytes: &'a [u8]) -> Result<Self> {
        if bytes.len() < 12 || u16be(bytes, 0).is_none_or(|v| v > 1) {
            return Err(FontError::InvalidFace);
        }
        let entries = u16be(bytes, 2).unwrap();
        let palettes = u16be(bytes, 4).unwrap();
        let colors = u16be(bytes, 6).unwrap();
        let records_offset = u32be(bytes, 8).unwrap() as usize;
        if entries == 0
            || palettes == 0
            || usize::from(palettes) > (bytes.len() - 12) / 2
            || records_offset > bytes.len()
            || usize::from(colors) > (bytes.len() - records_offset) / 4
        {
            return Err(FontError::InvalidFace);
        }
        for index in 0..usize::from(palettes) {
            let first = u16be(bytes, 12 + index * 2).unwrap();
            if first > colors || entries > colors - first {
                return Err(FontError::InvalidFace);
            }
        }
        Ok(Self {
            bytes,
            records_offset,
            entries,
            palettes,
        })
    }
    pub fn bytes(self) -> &'a [u8] {
        self.bytes
    }
    pub fn entries_per_palette(self) -> u16 {
        self.entries
    }
    pub fn palette_count(self) -> u16 {
        self.palettes
    }
    /// Out-of-range palette selects zero. Missing/foreground entries keep the
    /// original opaque-white default; the foreground flag is independent.
    pub fn color(self, palette_index: u16, entry_index: u16) -> NativePaletteColor {
        let foreground = entry_index == u16::MAX;
        if foreground || entry_index >= self.entries {
            return NativePaletteColor {
                uses_foreground_color: foreground,
                ..Default::default()
            };
        }
        let selected = if palette_index < self.palettes {
            palette_index
        } else {
            0
        };
        let first = u16be(self.bytes, 12 + usize::from(selected) * 2).unwrap();
        let offset = self.records_offset + (usize::from(first) + usize::from(entry_index)) * 4;
        NativePaletteColor {
            color: NativeColorRgba8 {
                red: self.bytes[offset + 2],
                green: self.bytes[offset + 1],
                blue: self.bytes[offset],
                alpha: self.bytes[offset + 3],
            },
            uses_foreground_color: false,
        }
    }
}
/// Exact selected COLR layer range. Version one retains its v0-prefix policy.
#[derive(Clone, Copy, Debug)]
pub struct NativeColorGlyphLayers<'a> {
    bytes: &'a [u8],
    offset: usize,
    count: u16,
}
impl<'a> NativeColorGlyphLayers<'a> {
    pub fn len(self) -> u16 {
        self.count
    }
    pub fn is_empty(self) -> bool {
        self.count == 0
    }
    pub fn bytes(self) -> &'a [u8] {
        &self.bytes[self.offset..self.offset + usize::from(self.count) * 4]
    }
    pub fn layer(self, index: u16) -> Option<(u16, u16)> {
        if index >= self.count {
            return None;
        }
        let offset = self.offset + usize::from(index) * 4;
        Some((u16be(self.bytes, offset)?, u16be(self.bytes, offset + 2)?))
    }
}
impl SfntFace {
    pub fn native_color_palette(&self) -> Result<NativeColorPalette<'_>> {
        NativeColorPalette::parse(
            self.table(Tag::new(*b"CPAL"))
                .ok_or(FontError::InvalidGlyph)?
                .bytes,
        )
    }
    pub fn native_colr_layers(&self, glyph: u16) -> Result<NativeColorGlyphLayers<'_>> {
        glyph_count(self, glyph)?;
        let bytes = self
            .table(Tag::new(*b"COLR"))
            .ok_or(FontError::InvalidGlyph)?
            .bytes;
        if bytes.len() < 14 || u16be(bytes, 0).is_none_or(|v| v > 1) {
            return Err(FontError::InvalidFace);
        }
        let count = usize::from(u16be(bytes, 2).unwrap());
        let bases = u32be(bytes, 4).unwrap() as usize;
        let layers = u32be(bytes, 8).unwrap() as usize;
        let layer_count = u16be(bytes, 12).unwrap();
        if bases > bytes.len()
            || count > (bytes.len() - bases) / 6
            || layers > bytes.len()
            || usize::from(layer_count) > (bytes.len() - layers) / 4
        {
            return Err(FontError::InvalidFace);
        }
        let (mut low, mut high) = (0, count);
        while low < high {
            let middle = low + (high - low) / 2;
            let offset = bases + middle * 6;
            match glyph.cmp(&u16be(bytes, offset).unwrap()) {
                std::cmp::Ordering::Less => high = middle,
                std::cmp::Ordering::Greater => low = middle + 1,
                std::cmp::Ordering::Equal => {
                    let first = u16be(bytes, offset + 2).unwrap();
                    let count = u16be(bytes, offset + 4).unwrap();
                    if first > layer_count || count > layer_count - first {
                        return Err(FontError::InvalidFace);
                    }
                    if count == 0 {
                        return Err(FontError::InvalidGlyph);
                    }
                    return Ok(NativeColorGlyphLayers {
                        bytes,
                        offset: layers + usize::from(first) * 4,
                        count,
                    });
                }
            }
        }
        Err(FontError::InvalidGlyph)
    }
    pub fn native_colr_layer_count(&self, glyph: u16) -> Result<u16> {
        Ok(self.native_colr_layers(glyph)?.len())
    }
    /// Complete capacity/table admission precedes every output write. Duplicate
    /// overrides select the last matching entry, only for a real palette entry.
    pub fn native_decode_colr_layers(
        &self,
        glyph: u16,
        palette_index: u16,
        overrides: &[NativeColorPaletteOverride],
        output: &mut [NativeColorGlyphLayer],
    ) -> Result<u16> {
        let layers = self.native_colr_layers(glyph)?;
        if output.len() < usize::from(layers.len()) {
            return Err(FontError::Capacity);
        }
        let palette = self
            .table(Tag::new(*b"CPAL"))
            .map(|table| NativeColorPalette::parse(table.bytes))
            .transpose()?;
        for (index, out) in output[..usize::from(layers.len())].iter_mut().enumerate() {
            let (glyph_index, entry) = layers.layer(index as u16).unwrap();
            let mut color = NativeColorRgba8::default();
            let mut foreground = entry == u16::MAX;
            if let Some(palette) = palette {
                let selected = palette.color(palette_index, entry);
                color = selected.color;
                foreground = selected.uses_foreground_color;
                if !foreground
                    && entry < palette.entries_per_palette()
                    && let Some(value) = overrides
                        .iter()
                        .rev()
                        .find(|v| v.palette_entry_index == entry)
                {
                    color = NativeColorRgba8 {
                        red: (value.argb >> 16) as u8,
                        green: (value.argb >> 8) as u8,
                        blue: value.argb as u8,
                        alpha: (value.argb >> 24) as u8,
                    };
                }
            }
            *out = NativeColorGlyphLayer {
                glyph_index,
                palette_entry_index: entry,
                color,
                uses_foreground_color: foreground,
            };
        }
        Ok(layers.len())
    }
}
