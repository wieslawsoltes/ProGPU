//! Original native context and chaining-context formats 1, 2 and 3.
use super::*;
use std::ops::Range;
#[derive(Clone, Copy)]
enum Matcher<'a> {
    Glyph,
    Class(ClassDefinition<'a>),
    Coverage(usize),
}
#[derive(Clone, Copy)]
enum Walk {
    Back,
    Forward,
    IncludeFirst,
}
impl Executor<'_, '_, '_, '_> {
    fn context_array(
        &self,
        cursor: &mut usize,
        omit_first: bool,
    ) -> Result<Range<usize>, FontError> {
        self.check(*cursor, 2)?;
        let mut n = usize::from(self.word(*cursor));
        *cursor += 2;
        if omit_first {
            require(n != 0)?;
            n -= 1;
        }
        self.check(*cursor, n * 2)?;
        let start = *cursor;
        *cursor += n * 2;
        Ok(start..*cursor)
    }
    fn context_match(
        &self,
        frame: Frame,
        mut p: usize,
        values: Range<usize>,
        matcher: Matcher<'_>,
        walk: Walk,
    ) -> Result<Option<usize>, FontError> {
        for (i, at) in values.step_by(2).enumerate() {
            p = match walk {
                Walk::Back => self.previous(p, frame, false),
                Walk::IncludeFirst if i == 0 => p,
                _ => self.next(p + 1, frame),
            };
            if p >= self.glyphs.len() {
                return Ok(None);
            }
            let expected = self.word(at);
            let glyph = self.glyphs[p].glyph_id;
            let matches = match matcher {
                Matcher::Glyph => glyph as u16 == expected,
                Matcher::Class(c) => c.get(glyph as u16) == expected,
                Matcher::Coverage(parent) => {
                    require(glyph <= 65535)?;
                    let coverage = Coverage::parse(
                        self.table.bytes(),
                        relative(parent, u32::from(expected))?,
                    )?;
                    coverage.find(glyph as u16).is_some()
                }
            };
            if !matches {
                return Ok(None);
            }
        }
        Ok(Some(p))
    }
    fn position_records(
        &mut self,
        at: usize,
        count: usize,
        input_count: usize,
        frame: Frame,
    ) -> Applied {
        self.check(at, count * 4)?;
        let mut changed = false;
        for index in 0..count {
            let record = at + index * 4;
            let sequence = usize::from(self.word(record));
            let lookup = self.word(record + 2);
            require(sequence < input_count)?;
            let mut p = frame.position;
            for _ in 0..sequence {
                p = self.next(p + 1, frame);
                if p >= self.glyphs.len() {
                    break;
                }
            }
            // Preserve native no-match even when earlier nested records wrote.
            if p >= self.glyphs.len() {
                return Ok(false);
            }
            changed |= self.lookup(lookup, p, frame.depth + 1)?;
        }
        Ok(changed || count == 0)
    }
    pub(super) fn context(&mut self, s: usize, chain: bool, frame: Frame) -> Applied {
        self.check(s, 2)?;
        let format = self.word(s);
        match format {
            1 | 2 => self.context_rules(s, chain, format, frame),
            3 => self.context_coverages(s, chain, frame),
            _ => Err(FontError::InvalidFace),
        }
    }
    fn context_coverages(&mut self, s: usize, chain: bool, frame: Frame) -> Applied {
        let (back, input, look, records, count) = if chain {
            self.check(s, 4)?;
            let mut cursor = s + 2;
            let back = self.context_array(&mut cursor, false)?;
            let input = self.context_array(&mut cursor, false)?;
            require(!input.is_empty())?;
            let look = self.context_array(&mut cursor, false)?;
            self.check(cursor, 2)?;
            let count = usize::from(self.word(cursor));
            self.check(cursor + 2, count * 4)?;
            (back, input, look, cursor + 2, count)
        } else {
            self.check(s, 6)?;
            let n = usize::from(self.word(s + 2));
            let count = usize::from(self.word(s + 4));
            require(n != 0)?;
            self.check(s + 6, n * 2 + count * 4)?;
            (0..0, s + 6..s + 6 + n * 2, 0..0, s + 6 + n * 2, count)
        };
        let matcher = Matcher::Coverage(s);
        let p = frame.position;
        let Some(start) = self.context_match(frame, p, back, matcher, Walk::Back)? else {
            return Ok(false);
        };
        let input_count = input.len() / 2;
        let Some(end) = self.context_match(frame, p, input, matcher, Walk::IncludeFirst)? else {
            return Ok(false);
        };
        let Some(last) = self.context_match(frame, end, look, matcher, Walk::Forward)? else {
            return Ok(false);
        };
        self.dependency(start, last);
        self.position_records(records, count, input_count, frame)
    }
    fn context_rules(&mut self, s: usize, chain: bool, format: u16, frame: Frame) -> Applied {
        self.check(
            s,
            if format == 1 {
                6
            } else if chain {
                12
            } else {
                8
            },
        )?;
        let data = self.table.bytes();
        let coverage = Coverage::parse(data, relative(s, u32::from(self.word(s + 2)))?)?;
        let Some(coverage_index) = coverage.find(self.glyphs[frame.position].glyph_id as u16)
        else {
            return Ok(false);
        };
        let (mut back_match, mut input_match, mut look_match) =
            (Matcher::Glyph, Matcher::Glyph, Matcher::Glyph);
        let (mut set_index, mut count, mut offsets) = (
            coverage_index as usize,
            usize::from(self.word(s + 4)),
            s + 6,
        );
        if format == 2 {
            let input_at = if chain { s + 6 } else { s + 4 };
            let classes =
                ClassDefinition::parse(data, relative(s, u32::from(self.word(input_at)))?)?;
            input_match = Matcher::Class(classes);
            set_index = usize::from(classes.get(self.glyphs[frame.position].glyph_id as u16));
            if chain {
                // Original complete class inventories are admitted before set selection.
                back_match = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 4)))?,
                )?);
                look_match = Matcher::Class(ClassDefinition::parse(
                    data,
                    relative(s, u32::from(self.word(s + 8)))?,
                )?);
                count = usize::from(self.word(s + 10));
                offsets = s + 12;
            } else {
                count = usize::from(self.word(s + 6));
                offsets = s + 8;
            }
        }
        self.check(offsets, count * 2)?;
        if set_index >= count {
            return Ok(false);
        }
        let relative_set = self.word(offsets + set_index * 2);
        if relative_set == 0 {
            return Ok(false);
        }
        let set = relative(s, u32::from(relative_set))?;
        self.check(set, 2)?;
        let rules = usize::from(self.word(set));
        self.check(set + 2, rules * 2)?;
        for i in 0..rules {
            let rule = relative(set, u32::from(self.word(set + 2 + i * 2)))?;
            let (back, input, look, records, record_count) = if chain {
                self.check(rule, 2)?;
                let mut cursor = rule;
                let back = self.context_array(&mut cursor, false)?;
                let input = self.context_array(&mut cursor, true)?;
                let look = self.context_array(&mut cursor, false)?;
                self.check(cursor, 2)?;
                let count = usize::from(self.word(cursor));
                self.check(cursor + 2, count * 4)?;
                (back, input, look, cursor + 2, count)
            } else {
                self.check(rule, 4)?;
                let n = usize::from(self.word(rule));
                let count = usize::from(self.word(rule + 2));
                require(n != 0)?;
                self.check(rule + 4, (n - 1) * 2 + count * 4)?;
                (
                    0..0,
                    rule + 4..rule + 4 + (n - 1) * 2,
                    0..0,
                    rule + 4 + (n - 1) * 2,
                    count,
                )
            };
            let p = frame.position;
            let Some(first) = self.context_match(frame, p, back, back_match, Walk::Back)? else {
                continue;
            };
            let input_count = input.len() / 2 + 1;
            let Some(end) = self.context_match(frame, p, input, input_match, Walk::Forward)? else {
                continue;
            };
            let Some(last) = self.context_match(frame, end, look, look_match, Walk::Forward)?
            else {
                continue;
            };
            self.dependency(first, last);
            return self.position_records(records, record_count, input_count, frame);
        }
        Ok(false)
    }
}
