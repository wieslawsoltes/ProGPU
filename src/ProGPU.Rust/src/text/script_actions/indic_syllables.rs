//! Independent Indic grammar from public specifications and original API outcomes.
//! No generated syllable tables, state numbering or foreign lexer structure used.

#[derive(Default)]
pub(super) struct Scanner {
    // A failed matra prefix may contain an arbitrarily long joiner run. Retain
    // its actual interval so successive standalone joiners do not rescan it.
    joiners: core::ops::Range<usize>,
}
impl Scanner {
    pub(super) fn next(
        &mut self,
        start: usize,
        length: usize,
        category: impl Fn(usize) -> u8,
    ) -> (usize, u8) {
        let mut c = Cursor {
            length,
            category,
            joiners: &mut self.joiners,
        };
        let mut end = start + 1;
        let mut kind = match c.at(start) {
            Some(3 | 4 | 7..=9 | 12..=14 | 16) => 4,
            _ => 5,
        };
        let mut admit = |candidate: usize, family: u8, equal: bool| {
            if candidate > end || equal && candidate == end {
                end = candidate;
                kind = family;
            }
        };
        let repha = start + usize::from(c.at(start) == Some(14));
        admit(c.tail(c.nukta(repha)), 4, false);
        let ra_halant = c.at(start) == Some(15) && c.at(start + 1) == Some(4);
        if ra_halant {
            admit(c.tail(c.nukta(start + 2)), 4, false);
        }
        let consonant_start = start + usize::from(matches!(c.at(start), Some(14 | 18)));
        let consonant_end = c.consonant(consonant_start);
        if consonant_end > consonant_start {
            admit(c.tail(consonant_end), 0, true);
        }
        let base = if ra_halant { start + 2 } else { repha };
        if c.at(base) == Some(2) {
            let after = c.nukta(base + 1);
            let trailing_joiner = c.optional(after, 6);
            admit(c.tail(after).max(trailing_joiner), 1, true);
        }
        if c.at(base) == Some(11) || !ra_halant && c.at(base) == Some(10) {
            admit(c.tail(c.nukta(base + 1)), 2, true);
        }
        if c.at(start) == Some(18) && c.at(start + 1) == Some(10) {
            admit(c.tail(c.nukta(start + 2)), 2, true);
        }
        if c.at(start) == Some(17) {
            admit(c.signs(c.optional(start + 1, 3)), 3, true);
        }
        (end, kind)
    }
}

struct Cursor<'a, F> {
    length: usize,
    category: F,
    joiners: &'a mut core::ops::Range<usize>,
}
impl<F: Fn(usize) -> u8> Cursor<'_, F> {
    fn at(&self, position: usize) -> Option<u8> {
        (position < self.length).then(|| (self.category)(position))
    }
    fn optional(&self, position: usize, category: u8) -> usize {
        position + usize::from(self.at(position) == Some(category))
    }
    fn joiner(&self, position: usize) -> usize {
        position + usize::from(matches!(self.at(position), Some(5 | 6)))
    }
    fn all_joiners(&mut self, position: usize) -> usize {
        if self.joiners.contains(&position) {
            return self.joiners.end;
        }
        let mut end = position;
        while matches!(self.at(end), Some(5 | 6)) {
            end += 1;
        }
        *self.joiners = position..end;
        end
    }
    fn nukta(&self, mut position: usize) -> usize {
        if self.at(position) == Some(5) && self.at(position + 1) == Some(12) {
            position += 1;
        }
        position = self.optional(position, 12);
        self.optional(self.optional(position, 3), 3)
    }
    fn consonant(&self, position: usize) -> usize {
        if matches!(self.at(position), Some(1 | 15)) {
            self.nukta(self.optional(position + 1, 6))
        } else {
            position
        }
    }
    fn signs(&self, mut position: usize) -> usize {
        let modifier = self.joiner(position);
        if matches!(self.at(modifier), Some(8 | 57)) {
            let second = modifier + 1 + usize::from(matches!(self.at(modifier + 1), Some(8 | 57)));
            position = self.optional(second, 5);
        }
        while self.at(position) == Some(9) {
            position += 1;
        }
        position
    }
    fn tail(&mut self, mut position: usize) -> usize {
        loop {
            let halant = self.joiner(position);
            if self.at(halant) != Some(4) {
                break;
            }
            let mut base = halant + 1;
            if self.at(base) == Some(6) {
                base = self.optional(base + 1, 3);
            }
            let end = self.consonant(base);
            if end == base {
                break;
            }
            position = end;
        }
        // A medial ends the consonant-link section. Halants after this boundary
        // are terminal; a later consonant starts a separate syllable.
        position = self.optional(position, 16);
        let halant = self.joiner(position);
        if self.at(halant) == Some(4) {
            let bare = halant == position;
            position = halant + 1;
            if self.at(position) == Some(6) {
                position = self.optional(position + 1, 3);
            } else if bare {
                position = self.optional(position, 5);
            }
        } else {
            loop {
                let mut matra = self.all_joiners(position);
                if matches!(self.at(matra), Some(8 | 57)) && self.at(matra + 1) == Some(13) {
                    matra += 1;
                }
                if !matches!(self.at(matra), Some(7 | 13)) {
                    break;
                }
                position = self.optional(self.optional(matra + 1, 3), 4);
            }
        }
        self.signs(position)
    }
}

#[cfg(test)]
mod tests {
    use super::Scanner;
    use core::cell::Cell;

    #[test]
    fn long_failed_joiner_prefixes_have_bounded_category_visits() {
        for prefix in [vec![], vec![14], vec![15, 4], vec![1, 4, 1]] {
            for suffix in [vec![], vec![1], vec![7], vec![57, 13], vec![12], vec![3]] {
                let mut categories = prefix.clone();
                categories.extend((0..8192).map(|i| 5 + (i % 2) as u8));
                categories.extend(&suffix);
                let calls = Cell::new(0usize);
                let mut scanner = Scanner::default();
                let mut start = 0;
                while start < categories.len() {
                    let (end, _) = scanner.next(start, categories.len(), |i| {
                        calls.set(calls.get() + 1);
                        categories[i]
                    });
                    assert!(start < end && end <= categories.len());
                    start = end;
                }
                assert!(
                    calls.get() < categories.len() * 64,
                    "{prefix:?} / {suffix:?}"
                );
            }
        }
    }
}
