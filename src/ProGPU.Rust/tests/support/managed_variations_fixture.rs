#![allow(dead_code)]
#[path = "gpos_dispatch_fixture.rs"]
mod position;
pub use position::{font, gdef, multi_store, put, store, words};
pub type AxisFixture = ([u8; 4], i32, i32, i32, u16, u16);
pub fn fvar(axes: &[AxisFixture]) -> Vec<u8> {
    let mut b = words(&[1, 0, 16, 2, axes.len() as u16, 20, 0, 8]);
    for (tag, min, default, max, flags, name) in axes {
        b.extend(tag);
        b.extend(min.to_be_bytes());
        b.extend(default.to_be_bytes());
        b.extend(max.to_be_bytes());
        b.extend(words(&[*flags, *name]));
    }
    b
}
pub fn avar(axes: usize, variant: usize) -> Vec<u8> {
    let mut b = words(&[1, 0, 0, axes as u16]);
    for i in 0..axes {
        let pairs = match (variant + i) % 5 {
            0 => vec![(-16384, -16384), (0, 0), (16384, 16384)],
            1 => vec![
                (-20000, 32767),
                (-8192, -1234),
                (8192, 3333),
                (20000, -32768),
            ],
            2 => vec![(8192, -20000), (0, 32767), (-8192, 1)],
            3 => vec![(0, 0), (0, 32767), (16384, 16384)],
            _ => vec![(0, 1234)],
        };
        b.extend(words(&[pairs.len() as u16]));
        for (a, c) in pairs {
            b.extend(words(&[a as u16, c as u16]));
        }
    }
    b
}
pub fn hvar(store: &[u8], map: u8) -> Vec<u8> {
    let mut b = vec![0; 12];
    font::u32_at(&mut b, 4, 12);
    b.extend(store);
    if map != 0 {
        let at = b.len() as u32;
        font::u32_at(&mut b, 8, at);
        b.extend([if map & 1 == 0 { 0 } else { 1 }, 0x31]);
        if map & 1 == 0 {
            b.extend(words(&[5]))
        } else {
            b.extend(5u32.to_be_bytes())
        }
        for v in [0u32, 4, 14, 0xffff, 9] {
            b.extend(v.to_be_bytes());
        }
    }
    b
}
pub fn mvar(store: &[u8]) -> Vec<u8> {
    let mut b = words(&[1, 0, 0, 8, 5, 52]);
    for (tag, a, c) in [
        (*b"hasc", 0, 0),
        (*b"hdsc", 1, 3),
        (*b"hasc", 1, 4),
        (*b"test", 3, 7),
        ([255, 128, 129, b'x'], 1, 1),
    ] {
        b.extend(tag);
        b.extend(words(&[a, c]));
    }
    b.extend(store);
    b
}
pub fn gvar(axes: usize, variant: usize) -> Vec<u8> {
    let points = if variant & 1 == 0 {
        None
    } else {
        Some(vec![0u8, 2, 1, 3])
    };
    let shared_points = variant >= 6;
    let embedded = variant < 4;
    let mut headers = Vec::new();
    let mut payload = Vec::new();
    if shared_points {
        if let Some(p) = &points {
            payload.push(p.len() as u8);
            payload.push(p.len() as u8 - 1);
            payload.extend(p);
        } else {
            payload.push(0);
        }
    }
    for n in 0..3 {
        let mut data = Vec::new();
        let flags = (if points.is_some() && !shared_points {
            0x2000
        } else {
            0
        }) | (if embedded { 0x8000 } else { 0 })
            | (if n == 1 { 0x4000 } else { 0 });
        if !shared_points && let Some(p) = &points {
            data.push(p.len() as u8);
            data.push(p.len() as u8 - 1);
            data.extend(p);
        }
        let count = points.as_ref().map_or(8, |p| p.len());
        for axis in 0..2 {
            data.push(0x40 | (count as u8 - 1));
            for i in 0..count {
                data.extend(((i as i16 * 27 + n * 31 + axis * 11) - 70).to_be_bytes());
            }
        }
        headers.extend(words(&[data.len() as u16, flags]));
        if embedded {
            for a in 0..axes {
                headers.extend(words(&[if a == 0 { 8192 } else { 0 }]));
            }
        }
        if n == 1 {
            headers.extend(words(&vec![0; axes]));
            headers.extend(words(&vec![16384; axes]));
        }
        payload.extend(data);
    }
    let mut glyph = words(&[
        3 | (if shared_points { 0x8000 } else { 0 }),
        (4 + headers.len()) as u16,
    ]);
    glyph.extend(headers);
    glyph.extend(payload);
    let mut b = vec![0; 56];
    put(&mut b, 0, 1);
    put(&mut b, 4, axes as u16);
    put(&mut b, 12, 8);
    put(&mut b, 14, 1);
    font::u32_at(
        &mut b,
        16,
        (56 + if embedded { 0 } else { axes * 2 }) as u32,
    );
    if !embedded {
        put(&mut b, 6, 1);
        font::u32_at(&mut b, 8, 56);
        for a in 0..axes {
            b.extend(words(&[if a == 0 { 8192 } else { 0 }]));
        }
    }
    for i in 1..=8 {
        font::u32_at(&mut b, 20 + i * 4, glyph.len() as u32);
    }
    b.extend(glyph);
    b
}
pub fn tables(axes: usize, variant: usize) -> Vec<([u8; 4], Vec<u8>)> {
    let mut tables = font::tables(vec![]);
    let axis: Vec<_> = (0..axes)
        .map(|i| {
            (
                [*b"wght", *b"wdth", *b"opsz"][i],
                -65536,
                0,
                65536,
                (i * 3) as u16,
                (i + 1) as u16,
            )
        })
        .collect();
    tables.push((*b"fvar", fvar(&axis)));
    tables.push((*b"avar", avar(axes, variant)));
    let s = if axes == 1 {
        store()
    } else {
        multi_store(axes as u16)
    };
    tables.push((*b"GDEF", gdef(&s)));
    tables.push((*b"gvar", gvar(axes, variant)));
    if !variant.is_multiple_of(3) {
        tables.push((*b"HVAR", hvar(&s, variant as u8)));
    }
    if !variant.is_multiple_of(4) {
        tables.push((*b"MVAR", mvar(&s)));
    }
    tables
}
