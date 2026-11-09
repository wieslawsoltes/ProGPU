//! Independent USE grammar from the public specification and original API
//! outcomes. No foreign transition tables, lexer topology or states are used.

#[derive(Default)]
pub(super) struct Scanner {
    // A failed hieroglyph prefix can span many segment-begin records. Cache its
    // endpoint so successive standalone symbols do not rescan the same run.
    segment_begin_end: usize,
}
impl Scanner {
    pub(super) fn next(
        &mut self,
        start: usize,
        length: usize,
        category: impl Fn(usize) -> u8,
    ) -> (usize, u8) {
        let c = Cursor { length, category };
        if matches!(c.at(start), Some(49 | 51)) {
            let end = c.hieroglyph(start, &mut self.segment_begin_end);
            if end > start {
                // A present hieroglyph after the optional segment begins is
                // disjoint from every other family's continuation. Avoid
                // parsing impossible Indic/number/symbol tails on this path.
                return (end, 6);
            }
        }
        let mut end = start + 1;
        let mut kind = match c.at(start) {
            Some(11..=14 | 18 | 22..=35 | 37..=39 | 41 | 42 | 44..=46 | 48 | 53 | 56) => 7,
            _ => 8,
        };
        let mut admit = |candidate: usize, candidate_kind: u8, equal: bool| {
            if candidate > end || equal && candidate == end {
                end = candidate;
                kind = candidate_kind;
            }
        };
        let broken = start + usize::from(c.at(start) == Some(18));
        admit(
            c.tail(broken)
                .0
                .max(c.number(broken).0)
                .max(c.symbol(broken)),
            7,
            false,
        );
        let base = start + usize::from(matches!(c.at(start), Some(18 | 43)));
        if matches!(c.at(base), Some(1 | 5)) {
            let (end, kind) = c.tail(base + 1);
            admit(end, kind, true);
        }
        if c.at(start) == Some(4) {
            let (end, kind) = c.number(start + 1);
            admit(end, kind, true);
        }
        if matches!(c.at(start), Some(0 | 5 | 51)) {
            admit(
                c.tail(start + 1).0.max(c.symbol(start + 1)),
                5,
                c.at(start) != Some(5),
            );
        }
        (end, kind)
    }
}
struct Cursor<F> {
    length: usize,
    category: F,
}
impl<F: Fn(usize) -> u8> Cursor<F> {
    fn at(&self, position: usize) -> Option<u8> {
        (position < self.length).then(|| (self.category)(position))
    }
    fn optional(&self, position: usize, category: u8) -> usize {
        position + usize::from(self.at(position) == Some(category))
    }
    fn many(&self, mut position: usize, category: u8) -> usize {
        while self.at(position) == Some(category) {
            position += 1;
        }
        position
    }
    fn ordered(&self, mut position: usize, categories: &[u8]) -> usize {
        for &category in categories {
            position = self.many(position, category);
        }
        position
    }
    fn consonants(&self, mut position: usize) -> usize {
        position = self.ordered(position, &[31, 32]);
        loop {
            if matches!(self.at(position), Some(12 | 44 | 48 | 53))
                && self.at(position + 1) == Some(1)
            {
                position += 2;
            } else if self.at(position) == Some(11) {
                position += 1;
            } else {
                return position;
            }
            position = self.ordered(position, &[31, 32]);
        }
    }
    fn tail(&self, mut position: usize) -> (usize, u8) {
        position = self.consonants(position);
        if matches!(self.at(position), Some(44 | 56)) {
            return (self.optional(position + 1, 14), 0);
        }
        for category in [30, 27, 28, 29] {
            position = self.optional(position, category);
        }
        position = if self.at(position) == Some(12) {
            position + 1
        } else {
            self.ordered(position, &[22, 33, 34, 35])
        };
        position = self.optional(position, 53);
        position = self.ordered(position, &[23, 37, 38, 39]);
        // Late sakot/base groups do not reopen the earlier consonant/medial/
        // vowel sections. Early sakot/base pairs belong to consonants above.
        while self.at(position) == Some(48) && self.at(position + 1) == Some(1) {
            position += 2;
        }
        if self.at(position) == Some(48) {
            return (self.optional(position + 1, 14), 1);
        }
        position = self.ordered(position, &[24, 25, 26]);
        position = if self.at(position) == Some(47) {
            position + 1
        } else {
            self.ordered(position, &[45, 46])
        };
        (self.optional(position, 14), 2)
    }
    fn number(&self, mut position: usize) -> (usize, u8) {
        while self.at(position) == Some(13) && self.at(position + 1) == Some(4) {
            position += 2;
        }
        if self.at(position) == Some(13) {
            (self.optional(position + 1, 14), 3)
        } else {
            (self.optional(position, 14), 4)
        }
    }
    fn symbol(&self, position: usize) -> usize {
        self.optional(self.ordered(position, &[41, 42]), 14)
    }
    fn segment_begin(&self, position: usize, cached: &mut usize) -> usize {
        if self.at(position) != Some(51) {
            return position;
        }
        *cached = self.many(position.max(*cached), 51);
        *cached
    }
    fn hieroglyph_modifiers(&self, position: usize) -> usize {
        self.many(self.optional(self.optional(position, 55), 54), 52)
    }
    fn hieroglyph(&self, start: usize, cached: &mut usize) -> usize {
        let mut position = self.segment_begin(start, cached);
        if self.at(position) != Some(49) {
            return start;
        }
        position = self.hieroglyph_modifiers(position + 1);
        while self.at(position) == Some(50) {
            position = self.segment_begin(position + 1, cached);
            if self.at(position) == Some(49) {
                position = self.hieroglyph_modifiers(position + 1);
            }
        }
        self.optional(position, 14)
    }
}
