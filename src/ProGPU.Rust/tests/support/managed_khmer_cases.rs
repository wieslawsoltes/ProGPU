//! Complete original stage controls, independent of the private parser corpus.
use super::{Case, fixture, raw, source};
const POINTS: [u32; 16] = [
    0x41, 0x1780, 0x17a3, 0x17d2, 0x200c, 0x200d, 0x2d, 0x25cc, 0x179a, 0x17b7, 0x17bb, 0x17c1,
    0x17b6, 0x17c9, 0x17c6, 0x17c7,
];
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    // Every category combination through four source records, with original
    // arbitrary metadata, signed clusters, buffer flags and real font ownership.
    for length in 0..=4 {
        for mut ordinal in 0..16usize.pow(length) {
            let original = ordinal;
            let points: Vec<_> = (0..length)
                .map(|_| {
                    let point = POINTS[ordinal % 16];
                    ordinal /= 16;
                    point
                })
                .collect();
            let mut c = raw(0, "khmr", (original % 4) as u8, &points, vec![69]);
            c.source.flags = (original % 256) as u8;
            for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                g.cluster = [i32::MAX, -1, i32::MIN, 7][i % 4];
                g.grapheme_cluster = 90 + i as i32;
            }
            emit(c, length <= 2 && original % 3 == 0);
        }
    }
    let patterns = [
        vec![],
        vec![0x1780],
        vec![0x17c1],
        vec![0x17c6],
        vec![0x1780, 0x17d2, 0x179a, 0x17c1, 0x17b7, 0x17c6],
        vec![0x17c1, 0x17d2, 0x179a, 0x17c7],
        vec![0x1780, 0x17c9, 0x17cc, 0x17d2, 0x1780, 0x200d, 0x17c9],
        vec![
            0x17d2, 0x179a, 0x17d2, 0x179a, 0x17d2, 0x179a, 0x17d2, 0x179a,
        ],
        vec![
            0x1780, 0x200c, 0x200d, 0x17c6, 0x17c1, 0x17bb, 0x200d, 0x17b7, 0x17b6,
        ],
        vec![0x17c1, 0x17c1, 0x17c7, 0x17c6, 0x17d2],
        vec![
            0x25cc,
            0x17c1,
            0x17c6,
            0x200d,
            0x17d2,
            0xd800,
            0x110000,
            u32::MAX,
        ],
    ];
    for (n, points) in patterns.iter().enumerate() {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in [0, 1, 16, 17, 64, 128, 255] {
                    for actions in [
                        vec![69],
                        vec![69, 69],
                        vec![69, 20, 69],
                        vec![68, 69, 17, 18, 69],
                    ] {
                        let mut c = raw(font, "khmr", level, points, actions.clone());
                        c.source.flags = flags;
                        c.source.pre = vec![0x301, 0xd800];
                        c.source.post = vec![0x179a, 0x17d2];
                        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                            g.cluster = [i32::MIN, 8, 8, -1, i32::MAX][i % 5];
                        }
                        emit(c, level == 1 && flags == 16 && actions.len() <= 2);
                    }
                }
            }
            for script in ["khmr", "KHMR", "khmr ", "khm", "", "latn", "😀"] {
                emit(
                    raw(font, script, 1, points, vec![69]),
                    font == 0 && n % 3 == 0,
                );
            }
            for flags in 0..=255 {
                let mut c = raw(font, "khmr", flags % 4, points, vec![69]);
                c.source.flags = flags;
                emit(c, false);
            }
        }
    }
    for points in patterns
        .iter()
        .chain([vec![0x1780, 0x17be, 0x17bf, 0x17c4, 0x17c5]].iter())
    {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in [0, 16, 255] {
                    let mut c = Case {
                        source: source(
                            font,
                            "khmr",
                            level,
                            fixture::preparation::units(
                                &points
                                    .iter()
                                    .copied()
                                    .filter(|&p| p <= 0x10ffff)
                                    .collect::<Vec<_>>(),
                            ),
                        ),
                        actions: vec![68, 69],
                        raw: None,
                    };
                    c.source.flags = flags;
                    emit(c, level == 1 && flags == 0);
                }
            }
        }
    }
    // All original property range boundaries/interiors and high aliases through
    // the whole stage, in batches that cross syllable and serial boundaries.
    let facts = include_bytes!("script_properties.bin");
    let points: Vec<_> = facts[8..]
        .chunks_exact(10)
        .map(|v| u32::from_le_bytes(v[..4].try_into().unwrap()))
        .collect();
    for chunk in points.chunks(48) {
        for font in [0, 34] {
            emit(raw(font, "khmr", 1, chunk, vec![69]), false);
        }
    }
    let mut seed = 0x4b484d52u32;
    for index in 0..12000 {
        let mut next = || {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed
        };
        let length = (next() % 49) as usize;
        let points: Vec<_> = (0..length)
            .map(|_| POINTS[next() as usize % POINTS.len()])
            .collect();
        let mut c = raw(
            [0, 1, 34, fixture::FALLBACK_FONT][index % 4],
            "khmr",
            (index % 4) as u8,
            &points,
            vec![69],
        );
        c.source.flags = next() as u8;
        for g in c.raw.as_mut().unwrap() {
            g.cluster = next() as i32;
            g.grapheme_cluster = next() as i32;
            g.flags = next();
            g.script_feature_mask = next() as u8;
            g.indic_category = next() as u8;
            g.indic_position = next() as u8;
            g.use_syllable = next() as u8;
            g.script_shaper = next() as u8;
        }
        emit(c, index % 997 == 0);
    }
    for n in [15, 16, 31, 64, 256, 2048] {
        for pattern in &patterns[2..] {
            for font in [0, 34] {
                emit(raw(font, "khmr", 1, &pattern.repeat(n), vec![69]), false);
            }
        }
        emit(raw(0, "khmr", 1, &vec![0x200d; n], vec![69]), false);
    }
}
