//! Authored font bytes; original ProGPU APIs are the independent behavior oracle.
#![allow(dead_code)] // Shared fixtures serve independent tests and baseline modes.
#[path = "font_fixture.rs"]
pub mod font;
use font::{u16_at, u32_at};
pub fn source(cmap: Vec<u8>, version: u16, page: u16) -> Vec<u8> {
    let mut tables = font::tables(cmap);
    let mut os = vec![0; 64];
    u16_at(&mut os, 0, version);
    u16_at(&mut os, 62, page);
    tables.push((*b"OS/2", os));
    font::sfnt(&tables, 0)
}
pub fn identity_symbol() -> Vec<u8> {
    let mut f = font::format4(true, false);
    u16_at(&mut f, 14, 0xf2ff);
    u16_at(&mut f, 20, 0xf000);
    u16_at(&mut f, 24, 0);
    f
}
pub fn directory_source(count: usize) -> Vec<u8> {
    let mut tables = font::tables(seeds()[0].clone());
    for n in 0..count {
        tables.push(((0xa0000000u32 + n as u32).to_be_bytes(), vec![]));
    }
    font::sfnt(&tables, 0)
}
pub fn symbol_hole(code: u16) -> Vec<u8> {
    let mut segments = Vec::new();
    if code > 0xf000 {
        segments.push((0xf000, code - 1, 0));
    }
    if code < 0xf2ff {
        segments.push((code + 1, 0xf2ff, 0));
    }
    segments.push((0xffff, 0xffff, 1));
    let count = segments.len();
    let mut t = vec![0; 16 + count * 8];
    u16_at(&mut t, 0, 4);
    u16_at(&mut t, 2, (16 + count * 8) as u16);
    u16_at(&mut t, 6, count as u16 * 2);
    for (n, (start, end, delta)) in segments.into_iter().enumerate() {
        u16_at(&mut t, 14 + n * 2, end);
        u16_at(&mut t, 16 + count * 2 + n * 2, start);
        u16_at(&mut t, 16 + count * 4 + n * 2, delta);
    }
    t
}
pub fn basic() -> Vec<[u32; 3]> {
    let mut q = vec![[4, 0, 0]];
    for c in [
        0,
        0x20,
        0x41,
        0x42,
        0x43,
        127,
        128,
        255,
        256,
        0x621,
        0x64b,
        0x200d,
        0xd800,
        0xf041,
        0xf141,
        0xfe81,
        0xffff,
        0x10000,
        0x1f600,
        0x10ffff,
        0x110000,
        0x80000000,
        u32::MAX,
    ] {
        q.extend([[0, c, 0], [1, c, 0], [1, c, 0], [3, c, 0]]);
        for selector in [0, 0xfe0f, 0xfe0e, 0xe0100, u32::MAX] {
            q.push([2, c, selector]);
        }
    }
    q
}
pub fn selectors(records: &[(u32, u32, u16)]) -> Vec<u8> {
    let mut t = vec![0; 10 + records.len() * 11];
    u16_at(&mut t, 0, 14);
    u32_at(&mut t, 6, records.len() as u32);
    for (n, &(selector, code, glyph)) in records.iter().enumerate() {
        let at = 10 + n * 11;
        t[at..at + 3].copy_from_slice(&selector.to_be_bytes()[1..]);
        let offset = t.len() as u32;
        u32_at(&mut t, at + 7, offset);
        t.extend(1u32.to_be_bytes());
        t.extend(&code.to_be_bytes()[1..]);
        t.extend(glyph.to_be_bytes());
    }
    let len = t.len() as u32;
    u32_at(&mut t, 2, len);
    t
}
pub fn seeds() -> Vec<Vec<u8>> {
    let base = font::format4(false, false);
    let group = font::groups(12, 0x41, 0x43, 5);
    vec![
        font::cmap(&[(3, 1, base.clone()), (0, 5, font::format14())]),
        font::cmap(&[
            (3, 0, identity_symbol()),
            (3, 10, group.clone()),
            (0, 5, font::format14()),
        ]),
        font::cmap(&[
            (3, 10, group.clone()),
            (0, 4, font::groups(13, 0x41, 0x43, 9)),
            (3, 1, base.clone()),
        ]),
        font::cmap(&[(3, 10, group)]),
        font::cmap(&[
            (3, 1, base.clone()),
            (9, 99, selectors(&[(0xfe0f, 0x42, 0), (0xfe0f, 0x42, 11)])),
        ]),
        font::cmap(&[
            (3, 1, base.clone()),
            (0, 5, selectors(&[(0xfe0f, 0x42, 11), (0xfe0e, 0x42, 12)])),
        ]),
        font::cmap(&[
            (0, 5, selectors(&[(0xfe0f, 0x42, 7)])),
            (0, 5, selectors(&[(0xfe0f, 0x42, 9)])),
            (3, 1, base),
        ]),
    ]
}

#[allow(dead_code)] // Shared baseline input authoring, outside measured execution.
pub fn benchmark_source(variant: usize) -> Vec<u8> {
    if variant == 6 {
        return directory_source(4096);
    }
    let cmap = match variant {
        0 => font::cmap(&[
            (3, 1, font::format4(false, false)),
            (0, 5, font::format14()),
        ]),
        1 => font::cmap(&[
            (3, 1, font::format4_inventory()[36].clone()),
            (0, 5, font::format14()),
        ]),
        2 => {
            let mut g = font::groups(12, 0, 127, 1);
            u32_at(&mut g, 12, 2);
            g.extend(0x1f600u32.to_be_bytes());
            g.extend(0x1f63fu32.to_be_bytes());
            g.extend(200u32.to_be_bytes());
            let len = g.len() as u32;
            u32_at(&mut g, 4, len);
            font::cmap(&[(3, 10, g), (0, 5, font::format14())])
        }
        3 | 4 => font::cmap(&[(3, 0, identity_symbol()), (0, 5, font::format14())]),
        5 => font::cmap(&[
            (3, 1, font::format4(false, false)),
            (
                0,
                5,
                selectors(
                    &(0..64)
                        .map(|n| (0xfe00 + n, 0x42, n as u16))
                        .collect::<Vec<_>>(),
                ),
            ),
        ]),
        _ => panic!("unknown mapping baseline"),
    };
    source(
        cmap,
        0,
        match variant {
            3 => 0xb200,
            4 => 0xb300,
            _ => 0,
        },
    )
}
