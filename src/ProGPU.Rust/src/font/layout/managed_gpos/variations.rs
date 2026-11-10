use super::*;
use crate::font::layout::{ManagedEnabledLookup, ManagedFeatureOptions, ManagedLayoutVariations};
fn size(format: u16) -> i64 {
    i64::from((format as u8).count_ones()) * 2
}
impl Executor<'_, '_> {
    fn variation_subtable(
        &self,
        lookup: i64,
        kind: u16,
        pointer: i64,
    ) -> Result<Option<(u16, i64)>, FontError> {
        let mut at = lookup + i64::from(self.read(pointer)?);
        let mut kind = kind;
        if kind == 9 {
            if !self.fits(at, 8) || self.read(at)? != 1 {
                return Ok(None);
            }
            kind = self.read(at + 2)?;
            let relative = u32_at(self.data, at as usize + 4);
            if relative > i32::MAX as u32 {
                return Ok(None);
            }
            at += i64::from(relative);
        }
        Ok(Some((kind, at)))
    }
    pub(super) fn variations(
        &mut self,
        enabled: &ManagedEnabledLookup,
        options: &ManagedFeatureOptions,
        vars: &ManagedLayoutVariations,
    ) -> Result<(), FontError> {
        let Some((lookup, kind, count)) = raw_lookup(self.data, enabled.index) else {
            return Ok(());
        };
        let lookup = lookup as i64;
        let mut pair = kind == 2;
        if kind == 9 {
            for i in 0..count {
                let pointer = lookup + 6 + i64::from(i) * 2;
                if !self.fits(pointer, 2) {
                    break;
                }
                let extension = lookup + i64::from(self.read(pointer)?);
                if self.fits(extension, 8)
                    && self.read(extension)? == 1
                    && self.read(extension + 2)? == 2
                {
                    pair = true;
                    break;
                }
            }
        }
        if pair {
            return self.pair_variations(lookup, kind, count, enabled, options, vars);
        }
        for i in 0..count {
            let pointer = lookup + 6 + i64::from(i) * 2;
            if !self.fits(pointer, 2) {
                break;
            }
            let Some((kind, at)) = self.variation_subtable(lookup, kind, pointer)? else {
                continue;
            };
            match kind {
                1 => self.single_variations(at, vars)?,
                4 | 6 => self.mark_variations(at, kind == 4, vars)?,
                _ => {}
            }
        }
        Ok(())
    }
    fn variation_index(&self, at: i64, vars: &ManagedLayoutVariations) -> Result<f32, FontError> {
        if !self.fits(at, 6) || self.read(at + 4)? != 0x8000 {
            return Ok(0.0);
        }
        vars.delta(self.read(at)?, self.read(at + 2)?)
    }
    fn device_variation(
        &self,
        base: i64,
        cursor: &mut i64,
        vars: &ManagedLayoutVariations,
    ) -> Result<f32, FontError> {
        if !self.fits(*cursor, 2) {
            return Ok(0.0);
        }
        let relative = self.read(*cursor)?;
        *cursor += 2;
        if relative == 0 {
            Ok(0.0)
        } else {
            self.variation_index(base + i64::from(relative), vars)
        }
    }
    fn value_variation(
        &mut self,
        i: usize,
        record: i64,
        format: u16,
        base: i64,
        vars: &ManagedLayoutVariations,
    ) -> Result<(), FontError> {
        let mut cursor = record + i64::from((format & 15).count_ones()) * 2;
        let x = if format & 16 != 0 {
            self.device_variation(base, &mut cursor, vars)?
        } else {
            0.0
        };
        let y = if format & 32 != 0 {
            self.device_variation(base, &mut cursor, vars)?
        } else {
            0.0
        };
        let advance = if format & 64 != 0 {
            self.device_variation(base, &mut cursor, vars)?
        } else {
            0.0
        };
        if format & 128 != 0 {
            let _ = self.device_variation(base, &mut cursor, vars)?;
        }
        self.buffer.variation(i, x, y, advance);
        Ok(())
    }
    fn single_variations(
        &mut self,
        at: i64,
        vars: &ManagedLayoutVariations,
    ) -> Result<(), FontError> {
        if !self.fits(at, 6) {
            return Ok(());
        }
        let format = self.read(at)?;
        let coverage = at + i64::from(self.read(at + 2)?);
        let value = self.read(at + 4)?;
        for i in 0..self.buffer.glyphs.len() {
            let Some(index) = coverage_at(self.data, coverage, self.buffer.glyph(i))? else {
                continue;
            };
            let record = if format == 1 {
                at + 6
            } else if format == 2 && self.fits(at + 6, 2) && index < u32::from(self.read(at + 6)?) {
                at + 8 + i64::from(index) * size(value)
            } else {
                continue;
            };
            self.value_variation(i, record, value, at, vars)?;
        }
        Ok(())
    }
    fn pair_variations(
        &mut self,
        lookup: i64,
        kind: u16,
        count: u16,
        enabled: &ManagedEnabledLookup,
        options: &ManagedFeatureOptions,
        vars: &ManagedLayoutVariations,
    ) -> Result<(), FontError> {
        let flags = self.read(lookup + 2)?;
        let pointer = lookup + 6 + i64::from(count) * 2;
        let set = if flags & 16 != 0 && self.fits(pointer, 2) {
            self.read(pointer)?
        } else {
            u16::MAX
        };
        self.buffer.select_mark_set(set);
        for i in 0..self.buffer.glyphs.len() {
            if enabled
                .digest
                .is_some_and(|d| !d.may_have(self.buffer.glyph(i)))
                || !enabled.required
                    && options.value_at(enabled.tag.map(u16::from), self.buffer.glyphs[i].cluster)
                        == 0
                || !self.buffer.eligible(i, flags)?
            {
                continue;
            }
            let Some(second) = self.buffer.next(i + 1, flags)? else {
                continue;
            };
            for sub in 0..count {
                let pointer = lookup + 6 + i64::from(sub) * 2;
                if !self.fits(pointer, 2) {
                    break;
                }
                let Some((effective, at)) = self.variation_subtable(lookup, kind, pointer)? else {
                    continue;
                };
                if effective == 2 && self.pair_variation_at(at, i, second, vars)? {
                    break;
                }
            }
        }
        Ok(())
    }
    fn pair_variation_at(
        &mut self,
        at: i64,
        i: usize,
        second: usize,
        vars: &ManagedLayoutVariations,
    ) -> Applied {
        if !self.fits(at, 10) {
            return Ok(false);
        }
        let format = self.read(at)?;
        let coverage = at + i64::from(self.read(at + 2)?);
        let f1 = self.read(at + 4)?;
        let f2 = self.read(at + 6)?;
        let n1 = size(f1);
        let n2 = size(f2);
        let Some(index) = coverage_at(self.data, coverage, self.buffer.glyph(i))? else {
            return Ok(false);
        };
        if format == 1 {
            let count = self.read(at + 8)?;
            let pointer = at + 10 + i64::from(index) * 2;
            if index >= u32::from(count) || !self.fits(pointer, 2) {
                return Ok(false);
            }
            let set = at + i64::from(self.read(pointer)?);
            if !self.fits(set, 2) {
                return Ok(false);
            }
            let count = self.read(set)?;
            let n = 2 + n1 + n2;
            let mut record = set + 2;
            for _ in 0..count {
                if !self.fits(record, n as usize) {
                    return Ok(false);
                }
                if self.read(record)? == self.buffer.glyph(second) {
                    self.value_variation(i, record + 2, f1, set, vars)?;
                    self.value_variation(second, record + 2 + n1, f2, set, vars)?;
                    return Ok(true);
                }
                record += n;
            }
            return Ok(false);
        }
        if format != 2 || !self.fits(at, 16) {
            return Ok(false);
        }
        let c1 = class_at(
            self.data,
            at + i64::from(self.read(at + 8)?),
            self.buffer.glyph(i),
        );
        let c2 = class_at(
            self.data,
            at + i64::from(self.read(at + 10)?),
            self.buffer.glyph(second),
        );
        let count1 = self.read(at + 12)?;
        let count2 = self.read(at + 14)?;
        if c1 >= count1 || c2 >= count2 {
            return Ok(false);
        }
        let relative = (i32::from(c1)
            .wrapping_mul(i32::from(count2))
            .wrapping_add(i32::from(c2)))
        .wrapping_mul((n1 + n2) as i32);
        let record = (at as i32).wrapping_add(16).wrapping_add(relative) as i64;
        if !self.fits(record, (n1 + n2) as usize) {
            return Ok(false);
        }
        self.value_variation(i, record, f1, at, vars)?;
        self.value_variation(second, record + n1, f2, at, vars)?;
        Ok(true)
    }
    fn anchor_variation(
        &self,
        at: i64,
        vars: &ManagedLayoutVariations,
    ) -> Result<[f32; 2], FontError> {
        if !self.fits(at, 10) || self.read(at)? != 3 {
            return Ok([0.0; 2]);
        }
        let x = self.read(at + 6)?;
        let y = self.read(at + 8)?;
        Ok([
            if x == 0 {
                0.0
            } else {
                self.variation_index(at + i64::from(x), vars)?
            },
            if y == 0 {
                0.0
            } else {
                self.variation_index(at + i64::from(y), vars)?
            },
        ])
    }
    fn mark_variations(
        &mut self,
        at: i64,
        base: bool,
        vars: &ManagedLayoutVariations,
    ) -> Result<(), FontError> {
        if !self.fits(at, 12) || self.read(at)? != 1 {
            return Ok(());
        }
        let marks = at + i64::from(self.read(at + 2)?);
        let targets = at + i64::from(self.read(at + 4)?);
        let classes = i32::from(self.read(at + 6)?);
        let mark_array = at + i64::from(self.read(at + 8)?);
        let target_array = at + i64::from(self.read(at + 10)?);
        for i in 1..self.buffer.glyphs.len() {
            let Some(index) = coverage_at(self.data, marks, self.buffer.glyph(i))? else {
                continue;
            };
            let mut target = Some(i - 1);
            if base {
                while let Some(index) = target {
                    if self.buffer.glyph_class(index)? != 3 {
                        break;
                    }
                    target = index.checked_sub(1);
                }
            }
            let Some(target) = target else {
                continue;
            };
            let Some(target_index) = coverage_at(self.data, targets, self.buffer.glyph(target))?
            else {
                continue;
            };
            let record = mark_array + 2 + i64::from(index) * 4;
            if !self.fits(record, 4) {
                continue;
            }
            let class = i32::from(self.read(record)?);
            if class >= classes {
                continue;
            }
            let mark_anchor = mark_array + i64::from(self.read(record + 2)?);
            let target_record = (target_array as i32).wrapping_add(2).wrapping_add(
                (target_index as i32)
                    .wrapping_mul(classes)
                    .wrapping_add(class)
                    .wrapping_mul(2),
            ) as i64;
            if !self.fits(target_record, 2) {
                continue;
            }
            let relative = self.read(target_record)?;
            if relative == 0 {
                continue;
            }
            let mark = self.anchor_variation(mark_anchor, vars)?;
            let target = self.anchor_variation(target_array + i64::from(relative), vars)?;
            self.buffer
                .variation(i, target[0] - mark[0], target[1] - mark[1], 0.0);
        }
        Ok(())
    }
}
