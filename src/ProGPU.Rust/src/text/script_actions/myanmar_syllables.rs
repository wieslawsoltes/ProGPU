//! Independent parser from public Myanmar grammar and original API outcomes.
//! No foreign machine tables, state numbering or generated lexer are used.
//! Every unbounded lookahead belongs to a candidate that is consumed; at most
//! three candidates visit a record. O(N) work and O(1) scratch for a whole run.

pub(super) fn next(start: usize, length: usize, category: impl Fn(usize) -> u8) -> (usize, u8) {
    let at = |index| (index < length).then(|| category(index));
    let kinzi = at(start) == Some(15) && at(start + 1) == Some(32) && at(start + 2) == Some(4);
    let mut end = start + 1;
    let mut kind = match category(start) {
        3 | 4 | 8 | 9 | 20..=23 | 32 | 35..=41 => 1,
        _ => 2,
    };
    let broken_end = suffix(start + if kinzi { 3 } else { 0 }, length, &category);
    if broken_end > end {
        end = broken_end;
        kind = 1;
    }
    // A bare base, a kinzi-prefixed base and an explicit stacker-prefixed base
    // compete by their full extent. An incomplete kinzi can remain broken.
    let prefix_end = if kinzi {
        start + 3
    } else if category(start) == 18 {
        start + 1
    } else {
        start
    };
    for base in [start, prefix_end] {
        if matches!(at(base), Some(1 | 2 | 10 | 11 | 15)) {
            let candidate = suffix(base + 1, length, &category);
            if candidate >= end {
                end = candidate;
                kind = 0;
            }
        }
        if prefix_end == start {
            break;
        }
    }
    (end, kind)
}

fn suffix(start: usize, length: usize, category: &impl Fn(usize) -> u8) -> usize {
    let mut c = Cursor {
        position: start,
        length,
        category,
    };
    c.take(40);
    while c.at(c.position) == Some(4) && matches!(c.at(c.position + 1), Some(1 | 2 | 15)) {
        c.position += 2;
        c.take(40);
    }
    if c.take(4) {
        return c.position;
    }
    c.many(32);
    if c.take(38) {
        c.take(32);
    }
    c.take(36);
    let medial_start = c.position;
    c.take(37);
    c.take(35);
    c.take(41);
    if c.position > medial_start {
        c.take(32);
    }
    while c.take(22) {
        c.take(40);
    }
    c.many(20);
    c.many(21);
    c.many(9);
    if c.take(3) {
        c.take(32);
    }
    while c.take(23) {
        c.take(35);
        c.take(41);
        c.many(32);
        c.many(20);
        c.many(9);
        if c.take(3) {
            c.take(32);
        }
    }
    loop {
        if c.take(8) || c.take(57) {
            continue;
        }
        if !c.take(39) {
            break;
        }
        c.many(9);
        c.take(3);
        c.take(32);
    }
    if matches!(c.at(c.position), Some(5 | 6)) {
        c.position += 1;
    }
    c.position
}

struct Cursor<'a, F> {
    position: usize,
    length: usize,
    category: &'a F,
}
impl<F: Fn(usize) -> u8> Cursor<'_, F> {
    fn at(&self, index: usize) -> Option<u8> {
        (index < self.length).then(|| (self.category)(index))
    }
    fn take(&mut self, value: u8) -> bool {
        if self.at(self.position) == Some(value) {
            self.position += 1;
            true
        } else {
            false
        }
    }
    fn many(&mut self, value: u8) {
        while self.take(value) {}
    }
}
