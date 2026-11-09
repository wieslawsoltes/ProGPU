use super::*;
#[derive(Clone, Copy)]
enum Matcher {
    Glyph,
    Class(i64),
    Coverage(i64),
}
#[derive(Clone, Copy)]
enum Direction {
    Back,
    Input,
    Context,
    FirstInput,
}
struct Records {
    offset: i64,
    count: u16,
    input_end: usize,
    start: usize,
    end: usize,
}
impl Executor<'_, '_> {
    fn matches(&self, matcher: Matcher, at: i64, position: usize) -> Result<bool, FontError> {
        let expected = self.read(at)?;
        let glyph = self.buffer.glyph_at(position)?;
        Ok(match matcher {
            Matcher::Glyph => glyph == expected,
            Matcher::Class(classes) => class_at(self.data, classes, glyph) == expected,
            Matcher::Coverage(parent) => {
                coverage_at(self.data, parent + i64::from(expected), glyph)?.is_some()
            }
        })
    }
    fn sequence(
        &self,
        values: i64,
        count: u16,
        mut position: usize,
        flags: u16,
        matcher: Matcher,
        direction: Direction,
    ) -> Result<Option<usize>, FontError> {
        for i in 0..count {
            let next = match direction {
                Direction::Back => self.buffer.previous(position, flags)?,
                Direction::FirstInput if i == 0 => Some(position),
                Direction::Input | Direction::FirstInput => {
                    self.buffer.next(position + 1, flags, false)?
                }
                Direction::Context => self.buffer.next(position + 1, flags, true)?,
            };
            let Some(next) = next else {
                return Ok(None);
            };
            position = next;
            if !self.matches(matcher, values + i64::from(i) * 2, position)? {
                return Ok(None);
            }
        }
        Ok(Some(position))
    }
    fn context_records(&mut self, records: Records, position: usize, flags: u16) -> Applied {
        self.buffer.dependency(position, records.input_end);
        let mut changed = false;
        let mut input_end = records.input_end as i64;
        for i in 0..records.count {
            let at = records.offset + i64::from(i) * 4;
            let sequence = self.read(at)?;
            let lookup = self.read(at + 2)?;
            let mut target = Some(position);
            for _ in 0..sequence {
                target = self.buffer.next(target.unwrap() + 1, flags, false)?;
                if target.is_none() {
                    break;
                }
            }
            if let Some(target) = target {
                let before = self.buffer.glyphs.len();
                let record_changed = self.lookup(lookup, target)?;
                changed |= record_changed;
                if record_changed && (target as i64) < input_end {
                    input_end += self.buffer.glyphs.len() as i64 - before as i64;
                }
            }
        }
        if changed {
            self.buffer.set_context_end(input_end);
        }
        Ok(changed)
    }
    fn chain_rule(
        &self,
        mut cursor: i64,
        position: usize,
        flags: u16,
        matchers: [Matcher; 3],
        include_first: bool,
    ) -> Result<Option<Records>, FontError> {
        if !self.fits(cursor, 2) {
            return Ok(None);
        }
        let back = self.read(cursor)?;
        cursor += 2;
        if !self.fits(cursor, usize::from(back) * 2) {
            return Ok(None);
        }
        let Some(start) =
            self.sequence(cursor, back, position, flags, matchers[0], Direction::Back)?
        else {
            return Ok(None);
        };
        cursor += i64::from(back) * 2;
        if !self.fits(cursor, 2) {
            return Ok(None);
        }
        let input = self.read(cursor)?;
        cursor += 2;
        let trailing = if include_first {
            input
        } else {
            input.saturating_sub(1)
        };
        if include_first && input == 0 || !self.fits(cursor, usize::from(trailing) * 2) {
            return Ok(None);
        }
        let Some(input_last) = self.sequence(
            cursor,
            trailing,
            position,
            flags,
            matchers[1],
            if include_first {
                Direction::FirstInput
            } else {
                Direction::Input
            },
        )?
        else {
            return Ok(None);
        };
        cursor += i64::from(trailing) * 2;
        if !self.fits(cursor, 2) {
            return Ok(None);
        }
        let look = self.read(cursor)?;
        cursor += 2;
        if !self.fits(cursor, usize::from(look) * 2) {
            return Ok(None);
        }
        let Some(last) = self.sequence(
            cursor,
            look,
            input_last,
            flags,
            matchers[2],
            Direction::Context,
        )?
        else {
            return Ok(None);
        };
        cursor += i64::from(look) * 2;
        if !self.fits(cursor, 2) {
            return Ok(None);
        }
        let count = self.read(cursor)?;
        cursor += 2;
        if !self.fits(cursor, usize::from(count) * 4) {
            return Ok(None);
        }
        Ok(Some(Records {
            offset: cursor,
            count,
            input_end: input_last + 1,
            start,
            end: last + 1,
        }))
    }
    pub(super) fn context(
        &mut self,
        offset: i64,
        position: usize,
        flags: u16,
        chain: bool,
        format: u16,
    ) -> Applied {
        if format == 3 {
            let record = if chain {
                let Some(r) = self.chain_rule(
                    offset + 2,
                    position,
                    flags,
                    [Matcher::Coverage(offset); 3],
                    true,
                )?
                else {
                    return Ok(false);
                };
                self.buffer.dependency(r.start, r.end);
                r
            } else {
                let count = self.read(offset + 2)?;
                let record_count = self.read(offset + 4)?;
                let values = offset + 6;
                if count == 0
                    || !self.fits(
                        values,
                        (usize::from(count) + usize::from(record_count) * 2) * 2,
                    )
                {
                    return Ok(false);
                }
                let Some(last) = self.sequence(
                    values,
                    count,
                    position,
                    flags,
                    Matcher::Coverage(offset),
                    Direction::FirstInput,
                )?
                else {
                    return Ok(false);
                };
                Records {
                    offset: values + i64::from(count) * 2,
                    count: record_count,
                    input_end: last + 1,
                    start: position,
                    end: last + 1,
                }
            };
            return self.context_records(record, position, flags);
        }
        if format != 1 && format != 2 {
            return Ok(false);
        }
        let header = if format == 1 {
            6
        } else if chain {
            12
        } else {
            8
        };
        if !self.fits(offset, header) {
            return Ok(false);
        }
        let Some(index) = self.coverage(offset, offset + 2, position)? else {
            return Ok(false);
        };
        let mut matchers = [Matcher::Glyph; 3];
        let mut set_index = i64::from(index);
        if format == 2 {
            if chain {
                for (i, matcher) in matchers.iter_mut().enumerate() {
                    *matcher =
                        Matcher::Class(offset + i64::from(self.read(offset + 4 + i as i64 * 2)?));
                }
            } else {
                matchers[1] = Matcher::Class(offset + i64::from(self.read(offset + 4)?));
            }
            if let Matcher::Class(classes) = matchers[1] {
                set_index = i64::from(class_at(
                    self.data,
                    classes,
                    self.buffer.glyph_at(position)?,
                ));
            }
        }
        let count = self.read(offset + header as i64 - 2)?;
        let p = offset + header as i64 + set_index * 2;
        if set_index >= i64::from(count) || !self.fits(p, 2) {
            return Ok(false);
        }
        let relative = self.read(p)?;
        if relative == 0 {
            return Ok(false);
        }
        let set = offset + i64::from(relative);
        if !self.fits(set, 2) {
            return Ok(false);
        }
        let count = self.read(set)?;
        for i in 0..count {
            let p = set + 2 + i64::from(i) * 2;
            if !self.fits(p, 2) {
                break;
            }
            let rule = set + i64::from(self.read(p)?);
            let records = if chain {
                let Some(r) = self.chain_rule(rule, position, flags, matchers, false)? else {
                    continue;
                };
                self.buffer.dependency(r.start, r.end);
                r
            } else {
                if !self.fits(rule, 4) {
                    continue;
                }
                let count = self.read(rule)?;
                let record_count = self.read(rule + 2)?;
                if count == 0
                    || !self.fits(
                        rule + 4,
                        (usize::from(count) - 1 + usize::from(record_count) * 2) * 2,
                    )
                {
                    continue;
                }
                let Some(last) = self.sequence(
                    rule + 4,
                    count - 1,
                    position,
                    flags,
                    matchers[1],
                    Direction::Input,
                )?
                else {
                    continue;
                };
                Records {
                    offset: rule + 4 + i64::from(count - 1) * 2,
                    count: record_count,
                    input_end: last + 1,
                    start: position,
                    end: last + 1,
                }
            };
            return self.context_records(records, position, flags);
        }
        Ok(false)
    }
}
