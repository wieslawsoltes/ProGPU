use super::*;
fn value_size(format: u16) -> usize {
    (format as u8).count_ones() as usize * 2
}
impl Executor<'_, '_> {
    pub(super) fn primitive(&mut self, kind: u16, at: i64, position: usize, flags: u16) -> Applied {
        match kind {
            1 => self.single(at, position),
            2 => self.pair(at, position, flags),
            3 => self.cursive(at, position, flags),
            4..=6 => self.mark(kind, at, position, flags),
            _ => Ok(false),
        }
    }
    fn value(&mut self, at: i64, format: u16, i: usize) -> Applied {
        if !self.fits(at, value_size(format)) {
            return Ok(false);
        }
        let mut cursor = at;
        let mut values = [0i16; 4];
        for bit in 0..8 {
            if format & (1 << bit) != 0 {
                let value = self.read(cursor)? as i16;
                cursor += 2;
                if let Some(slot) = values.get_mut(bit) {
                    *slot = value;
                }
            }
        }
        self.buffer.values(i, values);
        Ok(true)
    }
    fn single(&mut self, at: i64, i: usize) -> Applied {
        if !self.fits(at, 6) {
            return Ok(false);
        }
        let Some(index) = self.coverage(at, at + 2, i)? else {
            return Ok(false);
        };
        let format = self.read(at + 4)?;
        let value = match self.read(at)? {
            1 => at + 6,
            2 if self.fits(at + 6, 2) && index < u32::from(self.read(at + 6)?) => {
                at + 8 + i64::from(index) * value_size(format) as i64
            }
            _ => return Ok(false),
        };
        self.value(value, format, i)
    }
    fn pair(&mut self, at: i64, i: usize, flags: u16) -> Applied {
        if !self.fits(at, 10) {
            return Ok(false);
        }
        let Some(index) = self.coverage(at, at + 2, i)? else {
            return Ok(false);
        };
        let Some(second) = self.buffer.next(i + 1, flags)? else {
            return Ok(false);
        };
        let f1 = self.read(at + 4)?;
        let f2 = self.read(at + 6)?;
        let n1 = value_size(f1);
        let n2 = value_size(f2);
        let record = if self.read(at)? == 1 {
            let count = self.read(at + 8)?;
            let pointer = at + 10 + i64::from(index) * 2;
            if index >= u32::from(count) || !self.fits(pointer, 2) {
                return Ok(false);
            }
            let set = at + i64::from(self.read(pointer)?);
            if !self.fits(set, 2) {
                return Ok(false);
            }
            let count = i32::from(self.read(set)?);
            let size = 2 + n1 + n2;
            if !self.fits(set + 2, count as usize * size) {
                return Ok(false);
            }
            let (mut low, mut high) = (0, count - 1);
            let mut found = None;
            while low <= high {
                let mid = (low + high) >> 1;
                let p = set + 2 + i64::from(mid) * size as i64;
                let candidate = self.read(p)?;
                if candidate < self.buffer.glyph(second) {
                    low = mid + 1;
                } else if candidate > self.buffer.glyph(second) {
                    high = mid - 1;
                } else {
                    found = Some(p + 2);
                    break;
                }
            }
            let Some(found) = found else {
                return Ok(false);
            };
            found
        } else {
            if self.read(at)? != 2 || !self.fits(at, 16) {
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
            // Original unchecked Int32 product/offset, including malformed large
            // class inventories. Preserve its wrapped read location explicitly.
            let relative = (i32::from(c1)
                .wrapping_mul(i32::from(count2))
                .wrapping_add(i32::from(c2)))
            .wrapping_mul((n1 + n2) as i32);
            let record = (at as i32).wrapping_add(16).wrapping_add(relative) as i64;
            if !self.fits(record, n1 + n2) {
                return Ok(false);
            }
            record
        };
        if f1 != 0 {
            self.value(record, f1, i)?;
        }
        if f2 != 0 {
            self.value(record + n1 as i64, f2, second)?;
        }
        self.buffer.dependency(i, second + 1);
        Ok(true)
    }
    fn anchor(&self, at: i64) -> Result<Option<[i32; 2]>, FontError> {
        if !self.fits(at, 6) || !(1..=3).contains(&self.read(at)?) {
            return Ok(None);
        }
        Ok(Some([
            i32::from(self.read(at + 2)? as i16),
            i32::from(self.read(at + 4)? as i16),
        ]))
    }
    fn cursive(&mut self, at: i64, i: usize, flags: u16) -> Applied {
        if !self.fits(at, 6) || self.read(at)? != 1 {
            return Ok(false);
        }
        let coverage = at + i64::from(self.read(at + 2)?);
        let index = coverage_at(self.data, coverage, self.buffer.glyph(i))?;
        let count = u32::from(self.read(at + 4)?);
        let Some(index) = index.filter(|&v| v < count) else {
            return Ok(false);
        };
        let record = at + 6 + i64::from(index) * 4;
        if !self.fits(record, 4) {
            return Ok(false);
        }
        let entry = self.read(record)?;
        if entry == 0 {
            return Ok(false);
        }
        let Some(previous) = self
            .buffer
            .previous_covered(i, flags, self.data, coverage, false)?
        else {
            return Ok(false);
        };
        let previous_index = coverage_at(self.data, coverage, self.buffer.glyph(previous))?;
        let Some(previous_index) = previous_index.filter(|&v| v < count) else {
            return Ok(false);
        };
        let record = at + 6 + i64::from(previous_index) * 4;
        if !self.fits(record, 4) {
            return Ok(false);
        }
        let exit = self.read(record + 2)?;
        if exit == 0 {
            return Ok(false);
        }
        let Some(entry) = self.anchor(at + i64::from(entry))? else {
            return Ok(false);
        };
        let Some(exit) = self.anchor(at + i64::from(exit))? else {
            return Ok(false);
        };
        self.buffer.cursive(previous, i, entry, exit, flags)?;
        Ok(true)
    }
    fn mark(&mut self, kind: u16, at: i64, i: usize, flags: u16) -> Applied {
        if !self.fits(at, 12) || self.read(at)? != 1 {
            return Ok(false);
        }
        let Some(mark_index) = self.coverage(at, at + 2, i)? else {
            return Ok(false);
        };
        let coverage = at + i64::from(self.read(at + 4)?);
        let classes = i64::from(self.read(at + 6)?);
        let marks = at + i64::from(self.read(at + 8)?);
        let targets = at + i64::from(self.read(at + 10)?);
        if classes == 0 {
            return Ok(false);
        }
        let Some(target) =
            self.buffer
                .previous_covered(i, flags, self.data, coverage, kind != 6)?
        else {
            return Ok(false);
        };
        let class = self.buffer.glyph_class(target)?;
        if match kind {
            4 => class != 0 && class != 1,
            5 => class != 2,
            _ => class != 3 || !self.buffer.can_attach(i, target),
        } {
            return Ok(false);
        }
        let target_index = coverage_at(self.data, coverage, self.buffer.glyph(target))?.unwrap();
        let (anchor_base, record_base, index) = if kind == 5 {
            let pointer = targets + 2 + i64::from(target_index) * 2;
            if !self.fits(pointer, 2) {
                return Ok(false);
            }
            let attach = targets + i64::from(self.read(pointer)?);
            if !self.fits(attach, 2) {
                return Ok(false);
            }
            let count = usize::from(self.read(attach)?);
            if count == 0 {
                return Ok(false);
            }
            let component = self.buffer.component(i, target).min(count - 1);
            (attach, attach + 2 + component as i64 * classes * 2, 0)
        } else {
            (targets, targets + 2, i64::from(target_index))
        };
        let record = marks + 2 + i64::from(mark_index) * 4;
        if !self.fits(record, 4) {
            return Ok(false);
        }
        let class = i64::from(self.read(record)?);
        if class >= classes {
            return Ok(false);
        }
        let mark_anchor = marks + i64::from(self.read(record + 2)?);
        let target_record = (record_base as i32).wrapping_add(
            (index as i32)
                .wrapping_mul(classes as i32)
                .wrapping_add(class as i32)
                .wrapping_mul(2),
        ) as i64;
        if !self.fits(target_record, 2) {
            return Ok(false);
        }
        let rel = self.read(target_record)?;
        if rel == 0 {
            return Ok(false);
        }
        let Some(mark) = self.anchor(mark_anchor)? else {
            return Ok(false);
        };
        let Some(target_anchor) = self.anchor(anchor_base + i64::from(rel))? else {
            return Ok(false);
        };
        self.buffer.attach(
            i,
            target,
            target_anchor[0] - mark[0],
            target_anchor[1] - mark[1],
        );
        Ok(true)
    }
}
