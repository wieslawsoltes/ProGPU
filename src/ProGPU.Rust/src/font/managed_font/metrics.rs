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
