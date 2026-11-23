use super::*;
#[derive(Clone, Copy)]
enum Matcher<'a> {
    Glyph,
    Class(ClassDefinition<'a>),
    Coverage(usize),
}
#[derive(Clone, Copy)]
enum Direction {
    Back,
    Forward,
    IncludeFirst,
}
impl Executor<'_, '_, '_> {
    // Original raw contextual search deliberately differs from strict Coverage:
    // exclusive high endpoint, no sorting or range/index canonicalization.
    fn raw_coverage(
        &self,
        parent: usize,
        relative_offset: u16,
        glyph: u32,
    ) -> Result<bool, FontError> {
        require(glyph <= 65535)?;
        let offset = relative(parent, u32::from(relative_offset))?;
        self.check(offset, 4)?;
        let format = self.word(offset);
        let count = usize::from(self.word(offset + 2));
        let stride = match format {
            1 => 2,
            2 => 6,
            _ => return Err(FontError::InvalidFace),
        };
        self.check(offset + 4, count * stride)?;
        let mut low = 0;
        let mut high = count;
        while low < high {
            let mid = low + (high - low) / 2;
            let record = offset + 4 + mid * stride;
            let start = u32::from(self.word(record));
            let end = if format == 1 {
                start
            } else {
                u32::from(self.word(record + 2))
            };
            if glyph < start {
                high = mid;
            } else if glyph > end {
                low = mid + 1;
            } else {
                return Ok(true);
            }
        }
        Ok(false)
    }
    fn array(&self, cursor: &mut usize, omit_first: bool) -> Result<Range<usize>, FontError> {
        self.check(*cursor, 2)?;
        let mut count = usize::from(self.word(*cursor));
        *cursor += 2;
        if omit_first {
            require(count != 0)?;
            count -= 1;
        }
        self.check(*cursor, count * 2)?;
        let start = *cursor;
        *cursor += count * 2;
        Ok(start..*cursor)
    }
    fn match_sequence(
        &self,
        frame: Frame,
        mut position: usize,
        values: Range<usize>,
        matcher: Matcher<'_>,
        direction: Direction,
    ) -> Result<Option<usize>, FontError> {
        for (i, offset) in values.step_by(2).enumerate() {
            position = match direction {
                Direction::Back => self.previous(position, frame),
                Direction::IncludeFirst if i == 0 => position,
                _ => self.next(position + 1, frame),
            };
            if position >= self.count() && !(matches!(direction, Direction::IncludeFirst) && i == 0)
            {
                return Ok(None);
            }
            let glyph = self.storage[position].glyph_id;
            let expected = self.word(offset);
            let matches = match matcher {
                Matcher::Glyph => glyph as u16 == expected,
                Matcher::Class(classes) => classes.get(glyph as u16) == expected,
                Matcher::Coverage(parent) => self.raw_coverage(parent, expected, glyph)?,
            };
            if !matches {
                return Ok(None);
            }
        }
        Ok(Some(position))
    }
    fn records(&mut self, records: usize, count: u16, input_count: usize, frame: Frame) -> Applied {
        self.check(records, usize::from(count) * 4)?;
        let mut changed = false;
        for i in 0..usize::from(count) {
            let offset = records + i * 4;
            let sequence = usize::from(self.word(offset));
            let lookup = self.word(offset + 2);
            require(sequence < input_count)?;
            let mut target = frame.position;
            for _ in 0..sequence {
                target = self.next(target + 1, frame);
                if target >= self.count() {
                    break;
                }
            }
            // Original source reports no match here even after previous records
            // changed the buffer. Do not replace this with `changed`.
            if target >= self.count() {
                return Ok(false);
            }
            changed |= self.lookup(lookup, target, frame.syllable, frame.depth + 1)?;
        }
        Ok(changed)
    }
    pub(super) fn context_coverages(&mut self, s: usize, chain: bool, frame: Frame) -> Applied {
        let p = frame.position;
        let matcher = Matcher::Coverage(s);
        let (back, input, look, records, record_count) = if chain {
            self.check(s, 4)?;
            let mut cursor = s + 2;
            let back = self.array(&mut cursor, false)?;
            let input = self.array(&mut cursor, false)?;
            require(!input.is_empty())?;
            let look = self.array(&mut cursor, false)?;
            self.check(cursor, 2)?;
            (back, input, look, cursor + 2, self.word(cursor))
        } else {
            self.check(s, 6)?;
            let input_count = usize::from(self.word(s + 2));
            require(input_count != 0)?;
            self.check(s + 6, input_count * 2)?;
            (
                s..s,
                s + 6..s + 6 + input_count * 2,
                s..s,
                s + 6 + input_count * 2,
                self.word(s + 4),
            )
        };
        let input_count = input.len() / 2;
        let Some(start) = self.match_sequence(frame, p, back, matcher, Direction::Back)? else {
            return Ok(false);
        };
        let Some(input_end) =
            self.match_sequence(frame, p, input, matcher, Direction::IncludeFirst)?
        else {
            return Ok(false);
        };
        let Some(end) = self.match_sequence(frame, input_end, look, matcher, Direction::Forward)?
        else {
            return Ok(false);
        };
        self.dependency(start, end);
        self.record_end(input_end + 1);
        self.records(records, record_count, input_count, frame)
    }
    pub(super) fn context_rules(
        &mut self,
        s: usize,
        chain: bool,
        format: u16,
        frame: Frame,
    ) -> Applied {
        let header = if format == 1 {
            6
        } else if chain {
            12
        } else {
            8
        };
        self.check(s, header)?;
        let Some(coverage_index) = self
            .strict_coverage(s)?
            .find(self.storage[frame.position].glyph_id as u16)
        else {
            return Ok(false);
        };
        let mut back_matcher = Matcher::Glyph;
        let mut input_matcher = Matcher::Glyph;
        let mut look_matcher = Matcher::Glyph;
        let mut set_index = usize::from(coverage_index);
        if format == 2 {
            let data = self.table.bytes();
            if chain {
                back_matcher = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 4)))?,
                )?);
                input_matcher = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 6)))?,
                )?);
                look_matcher = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 8)))?,
                )?);
            } else {
                input_matcher = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 4)))?,
                )?);
            }
            if let Matcher::Class(classes) = input_matcher {
                set_index = usize::from(classes.get(self.storage[frame.position].glyph_id as u16));
            }
        }
        let count = usize::from(self.word(s + header - 2));
        let offsets = s + header;
        self.check(offsets, count * 2)?;
        if set_index >= count {
            return Ok(false);
        }
        let rel = self.word(offsets + set_index * 2);
        if rel == 0 {
            return Ok(false);
        }
        let set = relative(s, u32::from(rel))?;
        self.check(set, 2)?;
        let count = usize::from(self.word(set));
        self.check(set + 2, count * 2)?;
        for i in 0..count {
            let rule = relative(set, u32::from(self.word(set + 2 + i * 2)))?;
            let (back, input, look, records, record_count) = if chain {
                let mut cursor = rule;
                let back = self.array(&mut cursor, false)?;
                let input = self.array(&mut cursor, true)?;
                let look = self.array(&mut cursor, false)?;
                self.check(cursor, 2)?;
                (back, input, look, cursor + 2, self.word(cursor))
            } else {
                self.check(rule, 4)?;
                let count = usize::from(self.word(rule));
                require(count != 0)?;
                self.check(rule + 4, (count - 1) * 2)?;
                (
                    rule..rule,
                    rule + 4..rule + 4 + (count - 1) * 2,
                    rule..rule,
                    rule + 4 + (count - 1) * 2,
                    self.word(rule + 2),
                )
            };
            let input_count = input.len() / 2 + 1;
            let Some(start) =
                self.match_sequence(frame, frame.position, back, back_matcher, Direction::Back)?
            else {
                continue;
            };
            let Some(input_end) = self.match_sequence(
                frame,
                frame.position,
                input,
                input_matcher,
                Direction::Forward,
            )?
            else {
                continue;
            };
            let Some(end) =
                self.match_sequence(frame, input_end, look, look_matcher, Direction::Forward)?
            else {
                continue;
            };
            self.dependency(start, end);
            self.record_end(input_end + 1);
            return self.records(records, record_count, input_count, frame);
        }
        Ok(false)
    }
    pub(super) fn reverse(&mut self, s: usize, frame: Frame) -> Applied {
        self.check(s, 6)?;
        require(self.word(s) == 1)?;
        let Some(index) = self
            .strict_coverage(s)?
            .find(self.storage[frame.position].glyph_id as u16)
        else {
            return Ok(false);
        };
        let mut cursor = s + 4;
        let back = self.array(&mut cursor, false)?;
        let look = self.array(&mut cursor, false)?;
        self.check(cursor, 2)?;
        let count = self.word(cursor);
        cursor += 2;
        require(index < count)?;
        self.check(cursor, usize::from(count) * 2)?;
        let matcher = Matcher::Coverage(s);
        let Some(start) =
            self.match_sequence(frame, frame.position, back, matcher, Direction::Back)?
        else {
            return Ok(false);
        };
        let Some(end) =
            self.match_sequence(frame, frame.position, look, matcher, Direction::Forward)?
        else {
            return Ok(false);
        };
        self.replace(frame.position, self.word(cursor + usize::from(index) * 2));
        self.dependency(start, end);
        Ok(true)
    }
}
