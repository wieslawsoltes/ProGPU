#![allow(dead_code)]
#[path = "managed_variations_fixture.rs"]
pub mod variations;
pub use variations::font;
#[path = "names_fixture.rs"]
mod names;
use names::Record;
pub fn tables(variant: usize) -> Vec<([u8; 4], Vec<u8>)> {
    let mut tables = font::tables(vec![]);
    let mut os2 = vec![0; 90];
    for (at, v) in [
        (0, 2),
        (4, 500),
        (6, 7),
        (26, 42),
        (28, 300),
        (62, if variant & 1 == 0 { 0 } else { 0x200 }),
        (86, 480),
        (88, 700),
    ] {
        font::u16_at(&mut os2, at, v);
    }
    if variant == 5 {
        os2.truncate(8);
    }
    tables.push((*b"OS/2", os2));
    let mut post = vec![0; 16];
    font::u16_at(&mut post, 8, -90i16 as u16);
    font::u16_at(&mut post, 10, 48);
    font::u32_at(&mut post, 12, variant as u32);
    if variant == 6 {
        post.truncate(11);
    }
    tables.push((*b"post", post));
    let mut vhea = vec![0; 36];
    font::u16_at(&mut vhea, 34, 2);
    tables.push((*b"vhea", vhea));
    tables.push((*b"vmtx", variations::words(&[1100, 20, 1200, 30])));
    tables.push((
        *b"name",
        names::table(&[
            Record::utf16(3, 0x409, 1, &[70, 97, 109]),
            Record::utf16(3, 0x409, 16, &[80, 114, 101, 102]),
            Record::utf16(0, 0, 2, &[82, 101, 103]),
            Record::utf16(0, 0, 6, &[80, 83]),
            Record::utf16(3, 0x409, 256, &[87, 101, 105, 103, 104, 116]),
        ]),
    ));
    if variant != 7 {
        tables.push((
            *b"fvar",
            variations::fvar(&[
                (*b"wght", 100 * 65536, 400 * 65536, 900 * 65536, 0, 256),
                (*b"ital", 0, 0, 65536, 1, 257),
                (*b"slnt", -20 * 65536, 0, 0, 0, 258),
            ]),
        ));
        let mut avar = variations::words(&[1, 0, 0, 3]);
        for _ in 0..3 {
            avar.extend(variations::words(&[
                3,
                (-16384i16) as u16,
                (-16384i16) as u16,
                0,
                0,
                16384,
                16384,
            ]));
        }
        if variant == 4 {
            font::u16_at(&mut avar, 16, 1234);
        }
        tables.push((*b"avar", avar));
        let mut store = variations::multi_store(3);
        for region in 0..7 {
            for axis in 1..3 {
                let at = 28 + (region * 3 + axis) * 6;
                store[at..at + 6].fill(0);
            }
        }
        tables.push((*b"GDEF", variations::gdef(&store)));
        tables.push((*b"gvar", variations::gvar(3, variant % 8)));
        if !variant.is_multiple_of(3) {
            tables.push((*b"HVAR", variations::hvar(&store, variant as u8)));
        }
        let mut mvar = variations::words(&[1, 0, 0, 8, 9, 84]);
        for (i, tag) in [
            *b"hasc", *b"hdsc", *b"hlgp", *b"xhgt", *b"cpht", *b"undo", *b"unds", *b"stro",
            *b"strs",
        ]
        .into_iter()
        .enumerate()
        {
            mvar.extend(tag);
            mvar.extend(variations::words(&[
                if i == 8 { 3 } else { (i % 2) as u16 },
                (i % 8) as u16,
            ]));
        }
        mvar.extend(store);
        tables.push((*b"MVAR", mvar));
    }
    let mut cpal = variations::words(&[
        if variant == 6 { 2 } else { 0 },
        3,
        2,
        if variant == 3 { 3 } else { 5 },
        0,
        16,
        0,
        2,
    ]);
    for i in 0..5u8 {
        cpal.extend([i * 30, 255 - i * 20, i * 40, 100 + i * 25]);
    }
    tables.push((*b"CPAL", cpal));
    tables.push((
        *b"COLR",
        variations::words(&[if variant == 5 { 2 } else { 0 }, 0, 0, 14, 0, 14, 0]),
    ));
    if variant & 1 != 0 {
        tables.push((*b"SVG ", vec![]));
        tables.push((*b"CFF ", vec![]));
        tables.push((*b"sbix", vec![]));
    }
    tables
}
