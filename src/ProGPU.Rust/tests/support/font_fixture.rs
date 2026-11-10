// Original ProGPU-native text fixture data, adapted into independent Rust inputs.
// Source: progpu_native_text_tests.cpp make_font/make_cmap/make_cmap14.
pub fn u16_at(data: &mut [u8], offset: usize, value: u16) {
    data[offset..offset + 2].copy_from_slice(&value.to_be_bytes());
}
pub fn u32_at(data: &mut [u8], offset: usize, value: u32) {
    data[offset..offset + 4].copy_from_slice(&value.to_be_bytes());
}
pub fn format4(symbol: bool, range: bool) -> Vec<u8> {
    let mut data = vec![0; if range { 34 } else { 32 }];
    let length = data.len() as u16;
    u16_at(&mut data, 0, 4);
    u16_at(&mut data, 2, length);
    u16_at(&mut data, 6, 4);
    let code = if symbol { 0xf041 } else { 0x41 };
    for (offset, value) in [
        (14, code),
        (16, 0xffff),
        (20, code),
        (22, 0xffff),
        (24, if range { 2 } else { 3u16.wrapping_sub(code) }),
        (26, 1),
    ] {
        u16_at(&mut data, offset, value);
    }
    if range {
        u16_at(&mut data, 28, 4);
        u16_at(&mut data, 32, 3);
    }
    data
}
pub fn groups(format: u16, start: u32, end: u32, glyph: u32) -> Vec<u8> {
    let mut data = vec![0; 28];
    u16_at(&mut data, 0, format);
    for (offset, value) in [(4, 28), (12, 1), (16, start), (20, end), (24, glyph)] {
        u32_at(&mut data, offset, value);
    }
    data
}
pub fn format14() -> Vec<u8> {
    let mut data = vec![0; 38];
    u16_at(&mut data, 0, 14);
    for (offset, value) in [(2, 38), (6, 1), (13, 21), (17, 29), (21, 1), (29, 1)] {
        u32_at(&mut data, offset, value);
    }
    data[10..13].copy_from_slice(&[0, 0xfe, 0x0f]);
    data[25..28].copy_from_slice(&[0, 0, 0x41]);
    data[33..36].copy_from_slice(&[0, 0, 0x42]);
    u16_at(&mut data, 36, 7);
    data
}
pub fn cmap(records: &[(u16, u16, Vec<u8>)]) -> Vec<u8> {
    let mut data = vec![0; 4 + records.len() * 8];
    u16_at(&mut data, 2, records.len() as u16);
    for (i, (platform, encoding, bytes)) in records.iter().enumerate() {
        let offset = data.len() as u32;
        u16_at(&mut data, 4 + i * 8, *platform);
        u16_at(&mut data, 6 + i * 8, *encoding);
        u32_at(&mut data, 8 + i * 8, offset);
        data.extend_from_slice(bytes);
    }
    data
}
pub fn tables(cmap: Vec<u8>) -> Vec<([u8; 4], Vec<u8>)> {
    let mut head = vec![0; 54];
    for (offset, value) in [
        (18, 1000),
        (36, -20i16 as u16),
        (38, -200i16 as u16),
        (40, 900),
        (42, 800),
        (50, 1),
    ] {
        u16_at(&mut head, offset, value);
    }
    let mut hhea = vec![0; 36];
    for (offset, value) in [(4, 800), (6, -200i16 as u16), (8, 40), (10, 1200), (34, 2)] {
        u16_at(&mut hhea, offset, value);
    }
    let mut hmtx = vec![0; 20];
    for (offset, value) in [(0, 500), (2, 10), (4, 600), (6, 20), (10, 30)] {
        u16_at(&mut hmtx, offset, value);
    }
    let mut maxp = vec![0; 6];
    u16_at(&mut maxp, 4, 8);
    let mut loca = vec![0; 36];
    for offset in [20, 24, 28, 32] {
        u32_at(&mut loca, offset, 22);
    }
    let mut glyf = vec![0; 22];
    for (offset, value) in [(0, 1), (2, 10), (6, 30), (8, 40), (10, 2)] {
        u16_at(&mut glyf, offset, value);
    }
    glyf[14..].copy_from_slice(&[0x33, 0x37, 0x26, 10, 20, 5, 30, 10]);
    vec![
        (*b"head", head),
        (*b"hhea", hhea),
        (*b"hmtx", hmtx),
        (*b"maxp", maxp),
        (*b"loca", loca),
        (*b"glyf", glyf),
        (*b"cmap", cmap),
    ]
}
pub fn sfnt(tables: &[([u8; 4], Vec<u8>)], face_offset: usize) -> Vec<u8> {
    let mut data = vec![0; face_offset + 12 + tables.len() * 16];
    u32_at(&mut data, face_offset, 0x10000);
    u16_at(&mut data, face_offset + 4, tables.len() as u16);
    for (index, (tag, bytes)) in tables.iter().enumerate() {
        let record = face_offset + 12 + index * 16;
        let offset = data.len() as u32;
        data[record..record + 4].copy_from_slice(tag);
        u32_at(&mut data, record + 4, 0x1000 + index as u32);
        u32_at(&mut data, record + 8, offset);
        u32_at(&mut data, record + 12, bytes.len() as u32);
        data.extend_from_slice(bytes);
    }
    data
}
pub fn base() -> Vec<u8> {
    sfnt(
        &tables(cmap(&[
            (3, 1, format4(false, false)),
            (3, 10, groups(12, 0x1f600, 0x1f600, 7)),
            (0, 5, format14()),
        ])),
        0,
    )
}
pub fn corpus() -> Vec<(u32, Vec<u8>)> {
    let mut result = vec![(0, base())];
    for format in [12, 13] {
        for (start, end, glyph) in [
            (0x41, 0x43, 5),
            (0x41, 0x43, 0),
            (0x41, 0x43, u32::MAX),
            (0, u32::MAX, 1),
        ] {
            for bmp in [false, true] {
                let mut records = vec![(3, 10, groups(format, start, end, glyph))];
                if bmp {
                    records.push((3, 1, format4(false, false)));
                }
                result.push((0, sfnt(&tables(cmap(&records)), 0)));
            }
        }
    }
    for range in [false, true] {
        for symbol in [false, true] {
            let records = [
                (3, 1, format4(false, range)),
                (3, 10, groups(12, 0x41, 0x41, 99)),
                (3, u16::from(!symbol), format4(symbol, range)),
                (0, 5, format14()),
            ];
            result.push((0, sfnt(&tables(cmap(&records)), 0)));
        }
    }
    for mode in 0..6 {
        let mut ts = tables(cmap(&[(3, 1, format4(false, false))]));
        match mode {
            0 => ts[4].1 = vec![0; 2], // truncated loca
            1 => {
                u16_at(&mut ts[0].1, 50, 0);
                ts[4].1 = vec![0; 18];
                for o in [10, 12, 14, 16] {
                    u16_at(&mut ts[4].1, o, 11);
                }
            }
            2 => ts[2].1.truncate(6), // absent bearings retain zero
            3 => ts.push((*b"head", vec![0; 54])), // last valid duplicate
            4 => ts.push((*b"CFF2", vec![2, 0, 5])),
            5 => ts[6].1.clear(),
            _ => unreachable!(),
        }
        result.push((0, sfnt(&ts, 0)));
    }
    let mut duplicate = tables(vec![]);
    duplicate.push((*b"head", vec![0; 54]));
    let mut data = sfnt(&duplicate, 0);
    u32_at(&mut data, 12 + 7 * 16 + 8, u32::MAX);
    result.push((0, data));
    let mut collection = sfnt(&tables(cmap(&[(3, 1, format4(false, false))])), 20);
    collection[..4].copy_from_slice(b"ttcf");
    u32_at(&mut collection, 4, 0x10000);
    u32_at(&mut collection, 8, 2);
    u32_at(&mut collection, 12, 20);
    u32_at(&mut collection, 16, 20);
    for index in 0..=2 {
        result.push((index, collection.clone()));
    }
    for signature in [*b"wOFF", *b"wOF2", *b"OTTO", *b"true", *b"abcd"] {
        let mut data = base();
        data[..4].copy_from_slice(&signature);
        result.push((0, data));
    }
    for length in 0..base().len() {
        result.push((0, base()[..length].to_vec()));
    }
    // Deterministic malformed directory/cmap/loca corpus, no external fuzz engine.
    let seeds: Vec<_> = result[..27].to_vec();
    let mut random = 0x5a1783d1u32;
    for iteration in 0..2048 {
        let (face, mut data) = seeds[iteration % seeds.len()].clone();
        for _ in 0..1 + iteration % 4 {
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            let index = random as usize % data.len();
            random = random.wrapping_mul(1664525).wrapping_add(1013904223);
            data[index] = (random >> 24) as u8;
        }
        result.push((face, data));
    }
    result
}

pub fn format4_inventory() -> Vec<Vec<u8>> {
    let mut result = Vec::new();
    for count in [1usize, 2, 3, 15, 16, 17, 187] {
        for ordering in 0..3 {
            for indirect in [false, true] {
                let mut table = vec![0u8; 16 + count * 8 + usize::from(indirect) * count * 2];
                let length = table.len() as u16;
                u16_at(&mut table, 0, 4);
                u16_at(&mut table, 2, length);
                u16_at(&mut table, 6, count as u16 * 2);
                for i in 0..count {
                    let end = match ordering {
                        0 => ((i + 1) * 65535 / count) as u16,
                        1 => ((i / 2 + 1) * 65535 / count.div_ceil(2)) as u16,
                        _ => ((count - i) * 65535 / count) as u16,
                    };
                    let start = if i % 4 == 0 {
                        end.saturating_add(1)
                    } else {
                        end.saturating_sub(100)
                    };
                    u16_at(&mut table, 14 + i * 2, end);
                    u16_at(&mut table, 16 + count * 2 + i * 2, start);
                    u16_at(
                        &mut table,
                        16 + count * 4 + i * 2,
                        (i as u16).wrapping_sub(400),
                    );
                    if indirect {
                        u16_at(&mut table, 16 + count * 6 + i * 2, count as u16 * 2);
                        u16_at(
                            &mut table,
                            16 + count * 8 + i * 2,
                            if i % 3 == 0 { 0 } else { i as u16 + 3 },
                        );
                    }
                }
                result.push(sfnt(&tables(cmap(&[(3, 1, table)])), 0));
            }
        }
    }
    result
}
