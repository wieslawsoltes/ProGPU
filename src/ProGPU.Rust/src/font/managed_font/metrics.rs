use super::*;
use metadata::word;
impl ManagedFont {
    fn item_count(&self, glyph: u16) -> Result<usize, FontError> {
        {
            let mut cache = self.root.cache.lock().unwrap_or_else(|e| e.into_inner());
            if cache.item_counts.is_empty() {
                cache.item_counts = vec![0; usize::from(self.root.base.metadata.glyph_count)];
            }
            let &cached = cache
                .item_counts
                .get(usize::from(glyph))
                .ok_or(FontError::InvalidFace)?;
            if cached != 0 {
                return Ok(cached);
            }
        }
        let computed = self.compute_item_count(glyph)?;
        let mut cache = self.root.cache.lock().unwrap_or_else(|e| e.into_inner());
        let slot = cache
            .item_counts
            .get_mut(usize::from(glyph))
            .ok_or(FontError::InvalidFace)?;
        if *slot == 0 {
            *slot = computed;
        }
        Ok(*slot)
    }
    fn compute_item_count(&self, glyph: u16) -> Result<usize, FontError> {
        let base = &self.root.base.metadata;
        let raw = &self.root.raw;
        let source = self.source();
        if !base.has_true_type_outlines || glyph >= base.glyph_count {
            return Ok(4);
        }
        let g = u32::from(glyph);
        let (start, end) = if raw.loca_format == 0 {
            (
                u32::from(word(source, raw.loca.wrapping_add(g * 2))?) * 2,
                u32::from(word(source, raw.loca.wrapping_add((g + 1) * 2))?) * 2,
            )
        } else {
            (
                metadata::long(source, raw.loca.wrapping_add(g * 4))?,
                metadata::long(source, raw.loca.wrapping_add((g + 1) * 4))?,
            )
        };
        if start == end {
            return Ok(4);
        }
        let at = raw.glyf.wrapping_add(start);
        let len = source.data().len() as u32;
        if at > len.wrapping_sub(10) {
            return Ok(4);
        }
        let contours = word(source, at)? as i16;
        if contours >= 0 {
            if contours == 0 {
                return Ok(4);
            }
            let last = at.wrapping_add(10).wrapping_add((contours as u32 - 1) * 2);
            return if last <= len.wrapping_sub(2) {
                Ok(usize::from(word(source, last)?) + 5)
            } else {
                Ok(4)
            };
        }
        let mut cursor = at.wrapping_add(10);
        let mut count = 0;
        loop {
            if cursor > len.wrapping_sub(4) {
                return Ok(4);
            }
            let flags = word(source, cursor)?;
            cursor = cursor.wrapping_add(4 + if flags & 1 != 0 { 4 } else { 2 });
            cursor = cursor.wrapping_add(if flags & 8 != 0 {
                2
            } else if flags & 0x40 != 0 {
                4
            } else if flags & 0x80 != 0 {
                8
            } else {
                0
            });
            count += 1;
            if flags & 0x20 == 0 || cursor > len {
                break;
            }
        }
        Ok(count + 4)
    }
    /// Original horizontal advance including selected HVAR/phantom delta and
    /// original float scale order. Missing metric storage returns half em.
    pub fn advance_width(&self, glyph: u16, em: f32) -> Result<f32, FontError> {
        let raw = &self.root.raw;
        if raw.horizontal == 0 || raw.horizontal_count == 0 {
            return Ok(em * 0.5);
        }
        let index = glyph.min(raw.horizontal_count - 1);
        let base = f32::from(word(
            self.source(),
            raw.horizontal.wrapping_add(u32::from(index) * 4),
        )?);
        let delta = if let Some(i) = &self.state.instance {
            if i.data().uses_glyph_phantom_advance() {
                i.phantom_advance_delta(glyph, self.item_count(glyph)?)?
            } else {
                i.advance_delta(glyph)?
            }
        } else {
            0.0
        };
        Ok((base + delta) * (em / f32::from(self.metadata().units_per_em)))
    }
    /// Original current managed vertical advance. Its fallback uses selected
    /// ascent/descent; the original method does not apply native VVAR policy.
    pub fn advance_height(&self, glyph: u16, em: f32) -> Result<f32, FontError> {
        let raw = &self.root.raw;
        let advance = if raw.vertical == 0 || raw.vertical_count == 0 {
            (i32::from(self.metadata().ascender) - i32::from(self.metadata().descender)) as f32
        } else {
            f32::from(word(
                self.source(),
                raw.vertical
                    .wrapping_add(u32::from(glyph.min(raw.vertical_count - 1)) * 4),
            )?)
        };
        Ok(advance * (em / f32::from(self.metadata().units_per_em)))
    }
}

impl ManagedFont {
    /// Original VORG/TrueType vertical-origin policy in the selected em scale.
    /// The outline fallback currently admits the managed TrueType adapter only.
    pub fn true_type_vertical_origin(&self, glyph: u16, em: f32) -> Result<f32, FontError> {
        let source = self.source();
        if let Some(t) = source.table(Tag::new(*b"VORG")) {
            let d = t.bytes;
            if d.len() >= 8 {
                let count = u16::from_be_bytes([d[6], d[7]]) as usize;
                if count <= (d.len() - 8) / 4 {
                    let mut origin = i16::from_be_bytes([d[4], d[5]]);
                    let (mut low, mut high) = (0i32, count as i32 - 1);
                    while low <= high {
                        let mid = low + ((high - low) / 2);
                        let at = 8 + mid as usize * 4;
                        let candidate = u16::from_be_bytes([d[at], d[at + 1]]);
                        if candidate == glyph {
                            origin = i16::from_be_bytes([d[at + 2], d[at + 3]]);
                            break;
                        }
                        if glyph < candidate {
                            high = mid - 1;
                        } else {
                            low = mid + 1;
                        }
                    }
                    return Ok(f32::from(origin) * (em / f32::from(self.metadata().units_per_em)));
                }
            }
        }
        let raw = &self.root.raw;
        if raw.vertical != 0
            && raw.vertical_count != 0
            && let Some(y_max) = source_vertical_bound(source, glyph)
        {
            let offset = if glyph < raw.vertical_count {
                raw.vertical.wrapping_add(u32::from(glyph) * 4 + 2)
            } else {
                raw.vertical.wrapping_add(
                    u32::from(raw.vertical_count) * 4 + u32::from(glyph - raw.vertical_count) * 2,
                )
            };
            if let Some((start, len)) = metadata::span(source, *b"vmtx")
                && offset.wrapping_add(2) <= start.wrapping_add(len)
            {
                let origin = i32::from(y_max) + i32::from(word(source, offset)? as i16);
                return Ok(origin as f32 * (em / f32::from(self.metadata().units_per_em)));
            }
        }
        let advance = i32::from(self.metadata().ascender) - i32::from(self.metadata().descender);
        let origin = if let Some(b) = self.true_type_bounds(glyph)? {
            i32::from(b[3]) + ((advance - (i32::from(b[3]) - i32::from(b[1]))) >> 1)
        } else if glyph < self.metadata().glyph_count {
            advance >> 1
        } else {
            i32::from(self.metadata().ascender)
        };
        Ok(origin as f32 * (em / f32::from(self.metadata().units_per_em)))
    }
}
// SfntFontFace.TryGetGlyphBounds: empty slots succeed, and header reads are
// bounded by the whole glyf table, not by the declared glyph end.
fn source_vertical_bound(source: &SfntFace, glyph: u16) -> Option<i16> {
    use crate::font::bytes;
    let head = source.table(Tag::new(*b"head"))?.bytes;
    let loca = source.table(Tag::new(*b"loca"))?.bytes;
    let glyf = source.table(Tag::new(*b"glyf"))?.bytes;
    let g = usize::from(glyph);
    let (start, end) = match bytes::i16(head, 50)? {
        0 => (
            u32::from(bytes::u16(loca, g * 2)?) * 2,
            u32::from(bytes::u16(loca, g * 2 + 2)?) * 2,
        ),
        1 => (bytes::u32(loca, g * 4)?, bytes::u32(loca, g * 4 + 4)?),
        _ => return None,
    };
    if start == end {
        return Some(0);
    }
    if start > end || end as usize > glyf.len() {
        return None;
    }
    bytes::bytes(glyf, start as usize, 10)?;
    bytes::i16(glyf, start as usize + 8)
}
