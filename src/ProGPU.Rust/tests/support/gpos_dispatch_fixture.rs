#![allow(dead_code)]
#[path = "font_fixture.rs"]
pub mod font;
#[path = "gpos_fixture.rs"]
pub mod gpos;
#[path = "layout_selection_fixture.rs"]
mod selection;
pub use gpos::{coverage1, put, words};
pub fn context(chain: bool, format: u16, records: &[(u16, u16)]) -> Vec<u8> {
    gpos::context(chain, format, records)
}
pub fn cursive() -> Vec<u8> {
    gpos::cursive()
}
pub fn table(lookups: &[(u16, u16, Vec<Vec<u8>>)]) -> Vec<u8> {
    gpos::table(lookups)
}
pub fn extension(kind: u16, sub: &[u8]) -> Vec<u8> {
    gpos::extension(kind, sub)
}
fn dword(b: &mut [u8], p: usize, n: u32) {
    b[p..p + 4].copy_from_slice(&n.to_be_bytes());
}
pub fn selected(raw: Vec<u8>, features: &[([u8; 4], Vec<u16>)], required: u16) -> Vec<u8> {
    selection::table(raw, features, required)
}
pub fn store() -> Vec<u8> {
    let mut b = words(&[1, 0, 24, 4, 0, 0, 0, 0, 0, 0, 0, 0, 1, 7]);
    for tuple in [
        (0, 8192, 16384),
        (-16384, -8192, 0),
        (0, 0, 0),
        (-100, -50, 100),
        (100, 80, 50),
        (-16384, 0, 16384),
        (0, 16384, 16384),
    ] {
        b.extend(words(&[tuple.0 as u16, tuple.1 as u16, tuple.2 as u16]));
    }
    for outer in [0, 1, 3] {
        let at = b.len();
        dword(&mut b, 8 + outer * 4, at as u32);
        let long = outer == 1;
        b.extend(words(&[8, if long { 0x8003 } else { 3 }, 7]));
        b.extend(words(&[
            0,
            1,
            2,
            3,
            4,
            5,
            if outer == 3 { 65535 } else { 6 },
        ]));
        for row in 0..8 {
            for i in 0..7 {
                let n = match row {
                    0 => [1, 3, -5, 7, -11, 13, 17][i],
                    1 => [-1, 0, 0, 0, 0, 0, 0][i],
                    2 => [1, 0, 0, 0, 0, 0, 0][i],
                    3 => [32767, -32768, 32766, 127, -128, 3, -7][i],
                    4 => [i32::MAX, i32::MIN, 0x1234567, -32768, 32767, 0, 0][i],
                    _ => row * 13 + i as i32 * 27 - 100,
                };
                if i < 3 && long {
                    b.extend(n.to_be_bytes());
                } else if i < 3 || long {
                    b.extend((n as i16).to_be_bytes());
                } else {
                    b.push(n as i8 as u8);
                }
            }
        }
    }
    b
}
pub fn gdef(store: &[u8]) -> Vec<u8> {
    let mut b = gpos::gdef();
    b.splice(14..14, [0; 4]);
    put(&mut b, 2, 3);
    for p in [4, 10, 12] {
        let v = u16::from_be_bytes([b[p], b[p + 1]]);
        put(&mut b, p, v + 4);
    }
    put(&mut b, 30, 3);
    let n = b.len() as u32;
    dword(&mut b, 14, n);
    b.extend(store);
    b
}
pub fn font(gpos: &[u8], gdef: &[u8]) -> Vec<u8> {
    font_with_axes(gpos, gdef, 1)
}
pub fn font_with_axes(gpos: &[u8], gdef: &[u8], axes: u16) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    if !gpos.is_empty() {
        tables.push((*b"GPOS", gpos.into()));
    }
    if !gdef.is_empty() {
        tables.push((*b"GDEF", gdef.into()));
    }
    let mut fvar = words(&[1, 0, 16, 2, axes, 20, 0, 8]);
    for tag in [*b"wght", *b"wdth", *b"opsz"]
        .into_iter()
        .take(axes as usize)
    {
        fvar.extend(tag);
        fvar.extend((-65536i32).to_be_bytes());
        fvar.extend(0i32.to_be_bytes());
        fvar.extend(65536i32.to_be_bytes());
        fvar.extend(words(&[0, 0]));
    }
    tables.push((*b"fvar", fvar));
    font::sfnt(&tables, 0)
}
fn value(
    b: &mut Vec<u8>,
    format: u16,
    base: usize,
    outer: u16,
    inner: u16,
) -> Vec<(usize, usize, u16, u16)> {
    let mut pointers = Vec::new();
    for bit in 0..8 {
        if format & (1 << bit) != 0 {
            if bit < 4 {
                b.extend(words(&[(-13 + bit * 11) as u16]));
            } else {
                pointers.push((b.len(), base, outer, (inner + bit as u16 - 4) % 8));
                b.extend([0, 0]);
            }
        }
    }
    pointers
}
fn pointers(b: &mut Vec<u8>, items: Vec<(usize, usize, u16, u16)>) {
    for (p, base, outer, inner) in items {
        let at = (b.len() - base) as u16;
        put(b, p, at);
        b.extend(words(&[outer, inner, 0x8000]));
    }
}
pub fn single(format: u16, values: u16, outer: u16, inner: u16) -> Vec<u8> {
    let mut b = words(&[format, 0, values]);
    if format == 2 {
        b.extend(words(&[3]));
    }
    let mut items = Vec::new();
    for _ in 0..if format == 2 { 3 } else { 1 } {
        items.extend(value(&mut b, values, 0, outer, inner));
    }
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[10, 20, 30]));
    pointers(&mut b, items);
    b
}
pub fn pair(format: u16, a: u16, c: u16, outer: u16, inner: u16) -> Vec<u8> {
    let mut b = words(&[format, 0, a, c]);
    let mut items = Vec::new();
    if format == 1 {
        b.extend(words(&[1, 12, 3]));
        for g in [12, 20, 30] {
            b.extend(words(&[g]));
            items.extend(value(&mut b, a, 12, outer, inner));
            items.extend(value(&mut b, c, 12, outer, inner));
        }
    } else {
        b.extend(words(&[0, 0, 3, 4]));
        for _ in 0..12 {
            items.extend(value(&mut b, a, 0, outer, inner));
            items.extend(value(&mut b, c, 0, outer, inner));
        }
        let at = b.len() as u16;
        put(&mut b, 8, at);
        put(&mut b, 10, at);
        b.extend(words(&[2, 3, 10, 10, 1, 20, 20, 2, 30, 30, 3]));
    }
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[10]));
    pointers(&mut b, items);
    b
}
pub fn mark(kind: u16, outer: u16, inner: u16) -> Vec<u8> {
    let mut b = words(&[1, 0, 0, 2, 12, 0, 2, 0, 0, 1, 0]);
    for i in 0..2 {
        let at = b.len();
        put(&mut b, 16 + i * 4, (at - 12) as u16);
        b.extend(words(&[
            3,
            (50 + i * 200) as u16,
            200,
            10,
            16,
            outer,
            inner,
            0x8000,
            outer,
            (inner + 1) % 8,
            0x8000,
        ]));
    }
    let targets = b.len();
    put(&mut b, 10, targets as u16);
    b.extend(words(&[1, 6, 28]));
    for i in 0..2 {
        b.extend(words(&[
            3,
            (700 + i * 200) as u16,
            800,
            10,
            16,
            outer,
            (inner + 2) % 8,
            0x8000,
            outer,
            (inner + 3) % 8,
            0x8000,
        ]));
    }
    let at = b.len() as u16;
    put(&mut b, 2, at);
    b.extend(coverage1(&[12, 13]));
    let at = b.len() as u16;
    put(&mut b, 4, at);
    b.extend(coverage1(&[if kind == 4 { 10 } else { 12 }]));
    b
}

pub fn multi_store(axes: u16) -> Vec<u8> {
    let old = store();
    let mut b = old[..28].to_vec();
    put(&mut b, 24, axes);
    for region in 0..7 {
        for axis in 0..axes {
            let selected = (region + usize::from(axis) * 3) % 7;
            b.extend(&old[28 + selected * 6..34 + selected * 6]);
        }
    }
    b.extend(&old[70..]);
    for outer in [0, 1, 3] {
        let old_at = u32::from_be_bytes(old[8 + outer * 4..12 + outer * 4].try_into().unwrap());
        dword(&mut b, 8 + outer * 4, old_at + u32::from(axes - 1) * 42);
    }
    b
}
