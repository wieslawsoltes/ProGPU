use super::fixture;
pub(super) fn font(glyphs: &[Vec<u8>]) -> Vec<u8> {
    let mut tables = fixture::tables(vec![]);
    let mut loca = vec![0; (glyphs.len() + 1) * 4];
    let mut glyf = Vec::new();
    for (i, glyph) in glyphs.iter().enumerate() {
        glyf.extend_from_slice(glyph);
        fixture::u32_at(&mut loca, (i + 1) * 4, glyf.len() as u32);
    }
    for (tag, data) in &mut tables {
        match &*tag {
            b"head" => fixture::u16_at(data, 50, 1),
            b"maxp" => fixture::u16_at(data, 4, glyphs.len() as u16),
            b"loca" => *data = loca.clone(),
            b"glyf" => *data = glyf.clone(),
            _ => {}
        }
    }
    fixture::sfnt(&tables, 0)
}
pub(super) fn simple() -> Vec<u8> {
    let mut data = vec![0; 10];
    fixture::u16_at(&mut data, 0, 1);
    data.extend_from_slice(&[0, 2, 0, 0, 0, 1, 0]); // three off/on/off points
    for delta in [3i16, 11, -7, -5, 13, -3] {
        data.extend_from_slice(&delta.to_be_bytes());
    }
    data
}
pub(super) fn record(flags: u16, index: u16, args: [u16; 2], matrix: [i16; 4]) -> Vec<u8> {
    let mut data = Vec::new();
    data.extend_from_slice(&flags.to_be_bytes());
    data.extend_from_slice(&index.to_be_bytes());
    for arg in args {
        if flags & 1 != 0 {
            data.extend_from_slice(&arg.to_be_bytes());
        } else {
            data.push(arg as u8);
        }
    }
    let values: &[i16] = if flags & 8 != 0 {
        &matrix[..1]
    } else if flags & 0x40 != 0 {
        &matrix[..2]
    } else if flags & 0x80 != 0 {
        &matrix
    } else {
        &[]
    };
    for value in values {
        data.extend_from_slice(&value.to_be_bytes());
    }
    data
}
pub(super) fn composite(records: &[Vec<u8>], instructions: bool) -> Vec<u8> {
    let mut data = vec![0; 10];
    fixture::u16_at(&mut data, 0, u16::MAX);
    for record in records {
        data.extend_from_slice(record);
    }
    if instructions {
        data.extend_from_slice(&[0, 3, 0xb0, 1, 0x2f]);
    }
    data
}
pub(super) fn corpus() -> Vec<(u32, Vec<u8>, u16)> {
    let mut cases = Vec::new();
    // Every raw flag combination: unknown bits and transform precedence survive.
    for flags in 0..=u16::MAX {
        let mut records = vec![record(
            flags,
            0,
            [0x80ff, 0x7fff],
            [-8192, 12288, -16384, 32767],
        )];
        if flags & 0x20 != 0 {
            records.push(record(2, 0, [7, 9], [0; 4]));
        }
        let g = composite(&records, flags & 0x100 != 0 && flags & 0x20 == 0);
        cases.push((0, font(&[simple(), vec![], g]), 2));
    }
    // Valid and invalid point attachment, scaled offsets, half ties and nested frames.
    for parent in [0, 1, 2, 3, 255, 65535] {
        for child in [0, 1, 2, 3, 255, 65535] {
            for flags in [1, 5, 0x81, 0x85, 0x1081, 0x1885] {
                let records = [
                    record(0x23, 0, [3, 5], [0; 4]),
                    record(flags, 0, [parent, child], [8192, -8192, 24576, 16384]),
                ];
                let inner = composite(&records, false);
                let outer = composite(
                    &[record(0x883, 1, [13, 65531], [-16384, 8192, 4096, 16384])],
                    false,
                );
                cases.push((0, font(&[simple(), inner, outer]), 2));
            }
        }
    }
    for args in [[1, 3], [65535, 65533], [0x8000, 0x7fff]] {
        for scale in [8192, -8192, 24576, -24576] {
            cases.push((
                0,
                font(&[
                    simple(),
                    composite(&[record(0x80f, 0, args, [scale, 0, 0, 0])], false),
                ]),
                1,
            ));
        }
    }
    // Original depth boundary and cycles, including a valid sibling after omission.
    for depth in 0..=36 {
        for flags in [2, 0x83, 0x887] {
            let mut glyphs = vec![simple()];
            for i in 0..depth {
                glyphs.push(composite(
                    &[record(
                        flags,
                        i as u16,
                        [1, 65533],
                        [16383, 12345, -23456, 8191],
                    )],
                    false,
                ));
            }
            cases.push((0, font(&glyphs), depth as u16));
        }
    }
    for child in [0, 1, 2, 65535] {
        let g = composite(
            &[
                record(0x22, child, [1, 2], [0; 4]),
                record(2, 1, [3, 4], [0; 4]),
            ],
            false,
        );
        cases.push((0, font(&[g, simple()]), 0));
    }
    let valid = composite(
        &[
            record(0x1a3, 0, [7, 9], [-8192, 16384, -32768, 32767]),
            record(0x103, 0, [0xffff, 13], [0; 4]),
        ],
        true,
    );
    for len in 0..valid.len() {
        cases.push((0, font(&[simple(), valid[..len].to_vec()]), 1));
    }
    for i in 0..valid.len() {
        for value in [0, 1, 127, 255] {
            let mut g = valid.clone();
            g[i] = value;
            cases.push((0, font(&[simple(), g]), 1));
        }
    }
    // A malformed child is still measured before an invalid attachment is omitted.
    let bad = vec![0, 1, 0];
    cases.push((
        0,
        font(&[bad, composite(&[record(0, 0, [255, 255], [0; 4])], false)]),
        1,
    ));
    for (index, data) in fixture::corpus() {
        cases.push((index, data, 0));
    }
    cases
}
