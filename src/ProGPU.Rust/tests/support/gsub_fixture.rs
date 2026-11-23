#![allow(dead_code)]
#[path = "digest_fixture.rs"]
mod digest;
pub use digest::{coverage1, extension, put, words};
pub fn table(lookups: &[(u16, u16, Vec<Vec<u8>>)]) -> Vec<u8> {
    let mut b = words(&[1, 0, 10, 12, 14, 0, 0, lookups.len() as u16]);
    b.resize(16 + lookups.len() * 2, 0);
    for (i, (kind, flags, subs)) in lookups.iter().enumerate() {
        let start = b.len();
        put(&mut b, 16 + i * 2, (start - 14) as u16);
        b.extend(words(&[*kind, *flags, subs.len() as u16]));
        b.resize(b.len() + subs.len() * 2, 0);
        if flags & 16 != 0 {
            b.extend(words(&[0]));
        }
        for (j, sub) in subs.iter().enumerate() {
            let rel = (b.len() - start) as u16;
            put(&mut b, start + 6 + j * 2, rel);
            b.extend(sub);
        }
    }
    b
}
pub fn single(delta: u16) -> Vec<u8> {
    let mut b = words(&[1, 6, delta]);
    b.extend(coverage1(&[0, 8, 9, 10, 11, 12, 20, 30, 65535]));
    b
}
pub fn single2() -> Vec<u8> {
    let mut b = words(&[2, 12, 3, 65535, 0, 400]);
    b.extend(coverage1(&[10, 20, 30]));
    b
}
pub fn multiple(values: &[u16]) -> Vec<u8> {
    let mut b = words(&[1, 0, 1, 8, values.len() as u16]);
    b.extend(words(values));
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[10]));
    b
}
pub fn ligature(components: &[u16], replacement: u16) -> Vec<u8> {
    let mut b = words(&[1, 0, 1, 8, 1, 4, replacement, components.len() as u16 + 1]);
    b.extend(words(components));
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[10]));
    b
}
pub fn context(chain: bool, format: u16, records: &[(u16, u16)]) -> Vec<u8> {
    let back: &[u16] = if chain { &[9, 8] } else { &[] };
    let input = [10, 20];
    let look: &[u16] = if chain { &[30] } else { &[] };
    let mut offsets = vec![];
    if format == 3 {
        let mut b = words(&[3]);
        if chain {
            for values in [back, &input, look] {
                b.extend(words(&[values.len() as u16]));
                for &g in values {
                    offsets.push((b.len(), g));
                    b.extend([0, 0]);
                }
            }
            b.extend(words(&[records.len() as u16]));
        } else {
            b.extend(words(&[input.len() as u16, records.len() as u16]));
            for g in input {
                offsets.push((b.len(), g));
                b.extend([0, 0]);
            }
        }
        for &(a, c) in records {
            b.extend(words(&[a, c]));
        }
        for (at, g) in offsets {
            let n = b.len() as u16;
            put(&mut b, at, n);
            b.extend(coverage1(&[g]));
        }
        return b;
    }
    let header = if format == 1 {
        6
    } else if chain {
        12
    } else {
        8
    };
    let sets = if format == 1 { 1 } else { 2 };
    let mut b = vec![0; header + sets * 2];
    put(&mut b, 0, format);
    put(&mut b, header - 2, sets as u16);
    let set = b.len();
    put(&mut b, header + (sets - 1) * 2, set as u16);
    b.extend(words(&[1, 4]));
    let value = |g: u16| if format == 2 { g / 10 } else { g };
    if chain {
        b.extend(words(&[back.len() as u16]));
        b.extend(words(&back.iter().map(|&g| value(g)).collect::<Vec<_>>()));
        b.extend(words(&[2, value(20), look.len() as u16]));
        b.extend(words(&look.iter().map(|&g| value(g)).collect::<Vec<_>>()));
        b.extend(words(&[records.len() as u16]));
    } else {
        b.extend(words(&[2, records.len() as u16, value(20)]));
    }
    for &(a, c) in records {
        b.extend(words(&[a, c]));
    }
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[10]));
    if format == 2 {
        let at = b.len() as u16;
        for p in if chain { &[4, 6, 8][..] } else { &[4][..] } {
            put(&mut b, *p, at);
        }
        b.extend(words(&[1, 0, 81]));
        b.extend(words(&(0..=80).map(|g| g / 10).collect::<Vec<_>>()));
    }
    b
}
pub fn reverse() -> Vec<u8> {
    let mut b = words(&[1, 18, 2, 24, 30, 1, 36, 1, 77]);
    for g in [10, 9, 8, 20] {
        b.extend(coverage1(&[g]));
    }
    b
}
pub fn gdef() -> Vec<u8> {
    let mut b = words(&[1, 2, 14, 0, 0, 32, 46]);
    b.extend(words(&[1, 10, 6, 1, 2, 3, 4, 5, 0]));
    b.extend(words(&[1, 10, 4, 0, 0, 2, 7]));
    b.extend(words(&[1, 1, 0, 8]));
    b.extend(coverage1(&[12]));
    b
}
pub fn seeds() -> Vec<Vec<u8>> {
    let mut out = vec![];
    for (kind, sub) in [
        (1, single(100)),
        (1, single(65530)),
        (1, single2()),
        (2, multiple(&[])),
        (2, multiple(&[10])),
        (2, multiple(&[10, 20, 30])),
        (3, multiple(&[100, 101, 102])),
        (3, multiple(&[])),
        (4, ligature(&[20], 88)),
        (4, ligature(&[20, 30], 89)),
        (4, ligature(&[], 90)),
        (8, reverse()),
    ] {
        for flags in [0, 1, 2, 4, 8, 16, 0x200, 0x210, 0xffff] {
            out.push(table(&[(kind, flags, vec![sub.clone()])]));
            out.push(table(&[(7, flags, vec![extension(kind, &sub)])]));
        }
    }
    for chain in [false, true] {
        for format in [1, 2, 3] {
            for records in [
                vec![],
                vec![(0, 1)],
                vec![(0, 1), (1, 1)],
                vec![(0, 2)],
                vec![(0, 3)],
                vec![(0, 4)],
                vec![(0, 5)],
                vec![(0, 6)],
                vec![(0, 1), (2, 1)],
                vec![(0, 1), (1, 65535)],
                vec![(0, 3), (1, 1)],
                vec![(0, 2), (1, 1)],
                vec![(0, 0)],
                vec![(0, 7)],
            ] {
                for flags in [0, 8, 0x210] {
                    let sub = context(chain, format, &records);
                    let kind = if chain { 6 } else { 5 };
                    for ext in [false, true] {
                        out.push(table(&[
                            (
                                if ext { 7 } else { kind },
                                flags,
                                vec![if ext {
                                    extension(kind, &sub)
                                } else {
                                    sub.clone()
                                }],
                            ),
                            (1, 0, vec![single(100)]),
                            (2, 0, vec![multiple(&[10, 20, 30])]),
                            (2, 0, vec![multiple(&[])]),
                            (4, 8, vec![ligature(&[20], 99)]),
                            (3, 0, vec![multiple(&[100, 200, 300])]),
                            (1, 0, vec![words(&[65535, 1])]),
                            (5, 0, vec![context(false, 3, &[(0, 0)])]),
                        ]));
                    }
                }
            }
        }
    }
    for kind in [0, 9, 65535] {
        out.push(table(&[(kind, 0, vec![single(10)])]));
    }
    out.push(table(&[(1, 0, vec![])]));
    out.push(table(&[(7, 0, vec![extension(7, &single(10))])]));
    out.push(table(&[(
        2,
        0,
        vec![multiple(&(0..300).collect::<Vec<_>>())],
    )]));
    // A context can delete its input then return no-match on a later record.
    // An extension lookup then visits its next subtable at the original slot.
    let one = words(&[3, 1, 2, 16, 0, 1, 0, 1, 1, 1, 10]);
    out.push(table(&[
        (
            7,
            0,
            vec![extension(5, &one), extension(2, &multiple(&[100, 101]))],
        ),
        (2, 0, vec![multiple(&[])]),
    ]));
    out
}
