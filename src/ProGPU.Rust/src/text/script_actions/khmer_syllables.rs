//! Independent ordered parser from public Khmer grammar and original API results.
//! No foreign transition tables, states or generated lexer structure are used.
//! See DESIGN.md for the observed source-specific extensions and corpus.

/// One immutable-category pass. Cache only the end of the last joiner run so a
/// failed optional suffix cannot rescan that run for every non-cluster character.
#[derive(Default)]
pub(super) struct Scanner {
    joiner_end: usize,
}
impl Scanner {
    pub(super) fn next(
        &mut self,
        start: usize,
        length: usize,
        category: impl Fn(usize) -> u8,
    ) -> (usize, u8) {
        let mut c = Cursor {
            position: start,
            length,
            category,
            joiner_end: self.joiner_end,
        };
        let mut kind = 1; // Broken cluster unless an original base is present.
        match c.current() {
            Some(1 | 2 | 15) => {
                kind = 0;
                c.position += 1;
                c.modifier();
            }
            Some(10 | 11) => {
                kind = 0;
                c.position += 1;
            }
            _ => {}
        }
        c.take(25);
        while c.subscript() {
            c.modifier();
        }
        // A trailing unattached coeng is a separate ending; it does not admit
        // the following vowels/signs into this syllable.
        if !c.take(4) {
            c.marks();
            c.take(22);
            c.marks();
            c.take(21);
            c.marks();
            if c.joiner() && c.at(c.position + 1) == Some(20) {
                c.position += 1;
            }
            c.take(20);
            c.marks();
            c.take(23);
            c.marks();
            // Original late subscript has no modifier/vowel continuation.
            c.subscript();
            while c.take(27) {}
        }
        if c.position == start {
            c.position += 1;
            kind = 2; // Every otherwise unclassified category is one token.
        }
        self.joiner_end = c.joiner_end;
        (c.position, kind)
    }
}

struct Cursor<F> {
    position: usize,
    length: usize,
    category: F,
    joiner_end: usize,
}
impl<F: Fn(usize) -> u8> Cursor<F> {
    fn at(&self, index: usize) -> Option<u8> {
        (index < self.length).then(|| (self.category)(index))
    }
    fn current(&self) -> Option<u8> {
        self.at(self.position)
    }
    fn joiner(&self) -> bool {
        matches!(self.current(), Some(5 | 6))
    }
    fn take(&mut self, value: u8) -> bool {
        if self.current() == Some(value) {
            self.position += 1;
            true
        } else {
            false
        }
    }
    fn modifier(&mut self) {
        if self.joiner() && self.at(self.position + 1) == Some(25) {
            self.position += 1;
        }
        self.take(25);
    }
    fn subscript(&mut self) -> bool {
        if self.current() == Some(4) && matches!(self.at(self.position + 1), Some(1 | 2 | 15)) {
            self.position += 2;
            true
        } else {
            false
        }
    }
    fn marks(&mut self) {
        loop {
            let mut end = self.position;
            if self.joiner() {
                end = end.max(self.joiner_end);
                while matches!(self.at(end), Some(5 | 6)) {
                    end += 1;
                }
                self.joiner_end = end;
            }
            if self.at(end) != Some(26) {
                break;
            }
            self.position = end + 1;
        }
    }
}
