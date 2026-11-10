#![allow(dead_code)]
#[path = "gsub_fixture.rs"]
pub mod common;
pub use common::{context, coverage1, extension, gdef, put, table, words};
pub fn single(format: u16, values: u16) -> Vec<u8> {
    let mut b = words(&[format, 0, values]);
    if format == 2 {
        b.extend(words(&[3]));
    }
    for row in 0..if format == 2 { 3 } else { 1 } {
        for bit in 0..8 {
            if values & (1 << bit) != 0 {
                b.extend(words(&[(row * 100 + bit * 17 - 200i32) as u16]));
            }
        }
    }
    let n = b.len() as u16;
    put(&mut b, 2, n);
    b.extend(coverage1(&[10, 20, 30]));
    b
}
pub fn pair(format: u16, a: u16, b: u16) -> Vec<u8> {
    let mut out = words(&[format, 0, a, b]);
    let values = |r: i32| {
        let mut v = Vec::new();
        for f in [a, b] {
            for bit in 0..8 {
                if f & (1 << bit) != 0 {
                    v.extend(words(&[(r + bit * 37 - 99) as u16]));
                }
            }
        }
        v
    };
    if format == 1 {
        out.extend(words(&[1, 12, 3]));
        for g in [12, 20, 30] {
            out.extend(words(&[g]));
            out.extend(values(i32::from(g)));
        }
    } else {
        out.extend(words(&[0, 0, 3, 4]));
        for r in 0..12 {
            out.extend(values(r));
        }
        let at = out.len() as u16;
        put(&mut out, 8, at);
        put(&mut out, 10, at);
        out.extend(words(&[2, 3, 10, 10, 1, 20, 20, 2, 30, 30, 3]));
    }
    let at = out.len() as u16;
    put(&mut out, 2, at);
    out.extend(coverage1(&[10]));
    out
}
pub fn cursive() -> Vec<u8> {
    let mut b = words(&[1, 0, 3, 0, 0, 0, 0, 0, 0]);
    for i in 0..6 {
        let n = b.len() as u16;
        put(&mut b, 6 + i * 2, n);
        b.extend(words(&[
            (i % 3 + 1) as u16,
            (i * 80) as u16,
            (-300 + i as i32 * 113) as u16,
        ]));
    }
    let n = b.len() as u16;
    put(&mut b, 2, n);
    b.extend(coverage1(&[10, 20, 30]));
    b
}
pub fn mark(kind: u16) -> Vec<u8> {
    let mut b = words(&[
        1,
        0,
        0,
        2,
        12,
        0,
        2,
        0,
        10,
        1,
        16,
        1,
        50,
        (-200i16) as u16,
        3,
        400,
        600,
    ]);
    let targets = b.len();
    put(&mut b, 10, targets as u16);
    if kind == 5 {
        b.extend(words(&[1, 4, 3]));
        for i in 0..6 {
            b.extend(words(&[(14 + i * 6) as u16]));
        }
        for i in 0..6 {
            b.extend(words(&[1, (100 + i * 50) as u16, (200 + i * 50) as u16]));
        }
    } else {
        b.extend(words(&[1, 6, 12, 2, 700, 800, 1, 900, 1000]));
    }
    let n = b.len() as u16;
    put(&mut b, 2, n);
    b.extend(coverage1(&[12, 13]));
    let n = b.len() as u16;
    put(&mut b, 4, n);
    b.extend(coverage1(&[match kind {
        4 => 10,
        5 => 11,
        _ => 12,
    }]));
    b
}
pub fn seeds() -> Vec<Vec<u8>> {
    let mut subs = Vec::new();
    for format in [1, 2] {
        for f in [0, 1, 2, 4, 8, 15, 16, 32, 64, 128, 255, 0xff00, 65535] {
            subs.push((1, single(format, f)));
        }
    }
    for format in [1, 2] {
        for (a, b) in [
            (0, 0),
            (1, 0),
            (0, 15),
            (15, 15),
            (0xf0, 0xff),
            (65535, 65535),
        ] {
            subs.push((2, pair(format, a, b)));
        }
    }
    subs.push((3, cursive()));
    for k in 4..=6 {
        subs.push((k, mark(k)));
    }
    let mut out = Vec::new();
    for (kind, sub) in subs {
        for flags in [0, 1, 2, 4, 8, 16, 0x200, 0x210, 65535] {
            out.push(table(&[(kind, flags, vec![sub.clone()])]));
            out.push(table(&[(9, flags, vec![extension(kind, &sub)])]));
        }
    }
    out
}
