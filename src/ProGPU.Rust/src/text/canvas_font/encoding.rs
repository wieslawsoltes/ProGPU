//! Original strict encoded-element reader and malformed-input result policy.
use super::*;
/// Original encoding values. UTF-16/32 and glyph IDs use native byte order.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(u8)]
pub enum TextEncoding {
    /// Strict UTF-8.
    #[default]
    Utf8 = 0,
    /// Strict native-endian UTF-16.
    Utf16 = 1,
    /// Valid native-endian Unicode scalars.
    Utf32 = 2,
    /// Native-endian glyph slots; one odd trailing byte is ignored.
    GlyphId = 3,
}
struct Elements<'a> {
    bytes: &'a [u8],
    encoding: TextEncoding,
    offset: usize,
}
impl Elements<'_> {
    fn next(&mut self) -> Option<(u32, usize)> {
        let input = self.bytes.get(self.offset..)?;
        let (value, used) = match self.encoding {
            TextEncoding::GlyphId => (
                u32::from(u16::from_ne_bytes(input.get(..2)?.try_into().ok()?)),
                2,
            ),
            TextEncoding::Utf32 => {
                let value = u32::from_ne_bytes(input.get(..4)?.try_into().ok()?);
                char::from_u32(value)?;
                (value, 4)
            }
            TextEncoding::Utf16 => {
                let first = u16::from_ne_bytes(input.get(..2)?.try_into().ok()?);
                if (0xd800..=0xdbff).contains(&first) {
                    let second = u16::from_ne_bytes(input.get(2..4)?.try_into().ok()?);
                    if !(0xdc00..=0xdfff).contains(&second) {
                        return None;
                    }
                    (
                        0x10000
                            + ((u32::from(first) - 0xd800) << 10)
                            + (u32::from(second) - 0xdc00),
                        4,
                    )
                } else if (0xdc00..=0xdfff).contains(&first) {
                    return None;
                } else {
                    (u32::from(first), 2)
                }
            }
            TextEncoding::Utf8 => {
                let first = *input.first()?;
                let used = if first < 0x80 {
                    1
                } else if first < 0xe0 {
                    2
                } else if first < 0xf0 {
                    3
                } else {
                    4
                };
                let scalar = std::str::from_utf8(input.get(..used)?)
                    .ok()?
                    .chars()
                    .next()?;
                (u32::from(scalar), used)
            }
        };
        self.offset += used;
        Some((value, self.offset))
    }
}
fn count(bytes: &[u8], encoding: TextEncoding) -> usize {
    if encoding == TextEncoding::GlyphId {
        return bytes.len() / 2;
    }
    let mut source = Elements {
        bytes,
        encoding,
        offset: 0,
    };
    let mut count = 0;
    while source.offset < bytes.len() {
        if source.next().is_none() {
            return 0;
        }
        count += 1;
    }
    count
}
impl CanvasFont {
    /// Count the complete original text inventory. Any malformed Unicode returns
    /// zero; GlyphId alone ignores a trailing byte.
    pub fn count_glyphs(&self, bytes: &[u8], encoding: TextEncoding) -> usize {
        count(bytes, encoding)
    }
    fn resolve(&self, value: u32, encoding: TextEncoding) -> Result<u16> {
        if self.typeface.is_none() {
            Ok(0)
        } else if encoding == TextEncoding::GlyphId {
            Ok(value as u16)
        } else {
            self.glyph(value as i32)
        }
    }
    /// Complete nominal mapping with original strict malformed-text empty result.
    pub fn encoded_glyphs(&self, bytes: &[u8], encoding: TextEncoding) -> Result<Vec<u16>> {
        let mut out = allocate(count(bytes, encoding), 0)?;
        self.write_encoded_glyphs(bytes, encoding, &mut out)?;
        Ok(out)
    }
    /// Nominal UTF-8 string mapping; it never invokes the shaping pipeline.
    pub fn glyphs(&self, text: &str) -> Result<Vec<u16>> {
        self.encoded_glyphs(text.as_bytes(), TextEncoding::Utf8)
    }
    /// Used-prefix mapping. Malformed/empty input leaves the complete output alone.
    pub fn write_encoded_glyphs(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        out: &mut [u16],
    ) -> Result<usize> {
        let count = count(bytes, encoding);
        if out.len() < count {
            return Err(Error::Capacity.into());
        }
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        for slot in &mut out[..count] {
            let (value, _) = source.next().ok_or(Error::InvalidInput)?;
            *slot = self.resolve(value, encoding)?;
        }
        Ok(count)
    }
    /// Original integer-codepoint overload compacts valid scalars and fills its
    /// remaining exact-length slots with zero; invalid scalars are not replaced.
    pub fn glyphs_from_codepoints(&self, codepoints: &[i32]) -> Result<Vec<u16>> {
        let mut out = allocate(codepoints.len(), 0)?;
        let mut index = 0;
        for &point in codepoints {
            if u32::try_from(point).ok().and_then(char::from_u32).is_some() {
                out[index] = self.glyph(point)?;
                index += 1;
            }
        }
        Ok(out)
    }
    /// Original complete mapped-glyph containment. Malformed or empty encoded
    /// input returns true, independently of integer-codepoint containment.
    pub fn contains_encoded_glyphs(&self, bytes: &[u8], encoding: TextEncoding) -> Result<bool> {
        let count = count(bytes, encoding);
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        for _ in 0..count {
            let (value, _) = source.next().ok_or(Error::InvalidInput)?;
            if self.resolve(value, encoding)? == 0 {
                return Ok(false);
            }
        }
        Ok(true)
    }
    /// Original integer-codepoint containment, retaining invalid-to-zero mapping.
    pub fn contains_codepoints(&self, points: &[i32]) -> Result<bool> {
        for &point in points {
            if !self.contains_glyph(point)? {
                return Ok(false);
            }
        }
        Ok(true)
    }
    /// Original nominal encoded measurement and independent painted bounds.
    pub fn measure_encoded(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        paint: Option<&Paint>,
    ) -> Result<(f32, [f32; 4])> {
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        let mut advance = 0.0;
        let mut bounds = None;
        for _ in 0..count(bytes, encoding) {
            let (value, _) = source.next().ok_or(Error::InvalidInput)?;
            let glyph = self.resolve(value, encoding)?;
            if let Some(mut b) = self.measured_glyph_bounds(glyph, paint)? {
                b[0] += advance;
                b[2] += advance;
                union(&mut bounds, b);
            }
            advance += self.glyph_advance(glyph)?;
        }
        Ok((advance, bounds.unwrap_or([0.0; 4])))
    }
    /// Original strict greater-than width break; returns source bytes consumed.
    /// NaN/negative widths retain the original float comparison behavior.
    pub fn break_encoded(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        maximum: f32,
    ) -> Result<(usize, f32)> {
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        let mut width = 0.0;
        let mut consumed = 0;
        for _ in 0..count(bytes, encoding) {
            let (value, end) = source.next().ok_or(Error::InvalidInput)?;
            let next = width + self.glyph_advance(self.resolve(value, encoding)?)?;
            if next > maximum {
                break;
            }
            width = next;
            consumed = end;
        }
        Ok((consumed, width))
    }
    /// Original ordered nominal positions from a complete encoded inventory.
    pub fn encoded_positions(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        origin: [f32; 2],
    ) -> Result<Vec<[f32; 2]>> {
        let glyphs = self.encoded_glyphs(bytes, encoding)?;
        self.glyph_positions(&glyphs, origin)
    }
    /// Original source outline accumulation, preserving separate selected-font
    /// advances and exact zero-offset/addition semantics for every glyph slot.
    pub fn text_path(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        origin: [f32; 2],
    ) -> Result<RetainedPath> {
        let mut output = PathBuilder::new();
        let mut x = origin[0];
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        for _ in 0..count(bytes, encoding) {
            let (value, _) = source.next().ok_or(Error::InvalidInput)?;
            let glyph = self.resolve(value, encoding)?;
            if let Some(path) = self.glyph_path(glyph)? {
                output.add_path(&path, [x, origin[1]], PathAddMode::Append)?;
            }
            x += self.glyph_advance(glyph)?;
        }
        Ok(output.detach())
    }
    /// Original positioned outline accumulation. Valid nonempty text requires
    /// exact position count; malformed text returns its original empty result.
    pub fn positioned_text_path(
        &self,
        bytes: &[u8],
        encoding: TextEncoding,
        positions: &[[f32; 2]],
    ) -> Result<RetainedPath> {
        let mut output = PathBuilder::new();
        let count = count(bytes, encoding);
        if count == 0 {
            return Ok(output.detach());
        }
        if positions.len() != count {
            return Err(Error::InvalidInput.into());
        }
        let mut source = Elements {
            bytes,
            encoding,
            offset: 0,
        };
        for &position in positions {
            let (value, _) = source.next().ok_or(Error::InvalidInput)?;
            if let Some(path) = self.glyph_path(self.resolve(value, encoding)?)? {
                output.add_path(&path, position, PathAddMode::Append)?;
            }
        }
        Ok(output.detach())
    }
}
