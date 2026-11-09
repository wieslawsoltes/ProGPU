//! Original complete-record stage controls, separate from grammar-only checks.
use super::{Case, fixture, raw, source};
const POINTS: [u32; 26] = [
    0x41, 0x1000, 0x1021, 0x1037, 0x1039, 0x200c, 0x200d, 0x1038, 0x1036, 0x1040, 0x25cc, 0x1004,
    0x1a60, 0x102d, 0x102f, 0x1031, 0x102b, 0x103a, 0x103e, 0x103c, 0x103d, 0x103b, 0x1063, 0xfe00,
    0x1060, 0xb2,
];
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    for length in 0..=3 {
        for mut ordinal in 0..26usize.pow(length) {
            let original = ordinal;
            let points: Vec<_> = (0..length)
                .map(|_| {
                    let point = POINTS[ordinal % 26];
                    ordinal /= 26;
                    point
                })
                .collect();
            for actions in [vec![70], vec![71], vec![71, 72]] {
                let mut c = raw(0, "mymr", (original % 4) as u8, &points, actions);
                c.source.flags = (original % 256) as u8;
                for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                    g.cluster = [i32::MAX, -1, i32::MIN][i % 3];
                }
                emit(c, length <= 1 || length == 2 && original % 41 == 0);
            }
        }
    }
    // The explicit reorder stage consumes already substituted metadata. Every
    // raw category pair, retained syllable kind, ligation and position bytes.
    for a in 0..=255u8 {
        for b in 0..=255u8 {
            let mut c = raw(0, "latn", 1, &[65, 66, 67, 68], vec![72]);
            c.source.flags = 16;
            for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                g.indic_category = [a, b, 40, 22][i];
                g.use_syllable = 0x10;
                g.ligated = if b & 1 == 0 { 0 } else { a };
                g.cluster = [9, -5, -5, 9][i];
                g.indic_position = [a, b, 255, 0][i];
            }
            emit(c, a % 51 == 0 && b % 51 == 0);
        }
    }
    let patterns = [
        vec![],
        vec![0x1031],
        vec![0x1000],
        vec![0x1004, 0x103a, 0x1039],
        vec![
            0x1004, 0x103a, 0x1039, 0x1000, 0x103c, 0x1031, 0xfe00, 0x102f, 0x1036,
        ],
        vec![0x1031, 0xfe00, 0x1031, 0xfe00, 0xfe00, 0x1031],
        vec![
            0x1000, 0x1039, 0x1000, 0xfe00, 0x103b, 0x103a, 0x103c, 0x103d, 0x103e, 0x1060, 0x103a,
        ],
        vec![
            0x1000, 0x102f, 0x1036, 0x1031, 0xfe00, 0x102f, 0x1036, 0x102b, 0x1038,
        ],
        vec![0x1063, 0x1036, 0x1036, 0x1037, 0x103a, 0xb2, 0x1038, 0x200d],
        vec![0x103e, 0x103a, 0x1060, 0x103d, 0x103a, 0x103e],
        vec![0x1000, 0x1039, 0x200d, 0x1031, 0x200c, 0x200d],
        vec![0x1031, 0xd800, 0x110000, u32::MAX, 0x1004, 0x103a, 0x1039],
    ];
    for (n, points) in patterns.iter().enumerate() {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in [0, 1, 16, 17, 64, 128, 255] {
                    for actions in [
                        vec![71],
                        vec![71, 72],
                        vec![71, 72, 72],
                        vec![71, 20, 72],
                        vec![71, 18, 72],
                        vec![70, 72],
                        vec![72],
                        vec![68, 71, 72],
                    ] {
                        let mut c = raw(font, "mymr", level, points, actions.clone());
                        c.source.flags = flags;
                        c.source.pre = vec![0x1031, 0xd800];
                        c.source.post = vec![0x1039, 0x1000];
                        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                            g.cluster = [i32::MIN, 8, 8, -1, i32::MAX][i % 5];
                        }
                        emit(c, level == 1 && flags == 16 && actions == [71, 72]);
                    }
                }
            }
            for script in ["mymr", "mym2", "MYMR", "mymr ", "", "latn", "😀"] {
                emit(
                    raw(font, script, 1, points, vec![71, 72]),
                    font == 0 && n % 3 == 0,
                );
            }
            for flags in 0..=255 {
                let mut c = raw(font, "mymr", flags % 4, points, vec![71, 72]);
                c.source.flags = flags;
                emit(c, false);
            }
            for level in 0..4 {
                for flags in [0, 16, 255] {
                    let mut c = Case {
                        source: source(
                            font,
                            "mymr",
                            level,
                            fixture::preparation::units(
                                &points
                                    .iter()
                                    .copied()
                                    .filter(|&p| p <= 0x10ffff)
                                    .collect::<Vec<_>>(),
                            ),
                        ),
                        actions: vec![71, 72],
                        raw: None,
                    };
                    c.source.flags = flags;
                    emit(c, level == 1 && flags == 0);
                }
            }
        }
    }
    // Neighbor-cluster extension must retain records outside the sorted syllable.
    for kind in 0..=255u8 {
        for ligated in [0, 1, 255] {
            let mut c = raw(0, "mymr", 1, &[65, 66, 67, 68, 69, 70, 71], vec![72]);
            c.source.flags = 16;
            for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                g.use_syllable = if i == 0 || i == 6 { 0xf2 } else { kind };
                g.indic_category = [0, 15, 32, 4, 1, 22, 0][i];
                g.ligated = ligated;
                g.cluster = [9, 9, -7, 3, 4, 8, 8][i];
            }
            emit(c, kind % 16 <= 2 && ligated == 0);
        }
    }
    let facts = include_bytes!("script_properties.bin");
    let points: Vec<_> = facts[8..]
        .chunks_exact(10)
        .map(|v| u32::from_le_bytes(v[..4].try_into().unwrap()))
        .collect();
    for chunk in points.chunks(48) {
        for font in [0, 34] {
            emit(raw(font, "mymr", 1, chunk, vec![71, 72]), false);
        }
    }
    let mut seed = 0x4d594d52u32;
    for index in 0..16000 {
        let mut next = || {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed
        };
        let length = (next() % 65) as usize;
        let points: Vec<_> = (0..length)
            .map(|_| POINTS[next() as usize % POINTS.len()])
            .collect();
        let actions = if index % 2 == 0 {
            vec![71, 72]
        } else {
            vec![72]
        };
        let mut c = raw(
            [0, 1, 34, fixture::FALLBACK_FONT][index % 4],
            "mymr",
            (index % 4) as u8,
            &points,
            actions,
        );
        c.source.flags = next() as u8;
        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
            let cp = g.code_point;
            let mut words = std::array::from_fn::<_, 31, _>(|_| next());
            words[4] = cp;
            *g = super::replay::preparation::records::from_words(&words);
            // Include long reordered runs as well as arbitrary already-split runs.
            if index % 4 == 1 {
                g.use_syllable = ((index % 15 + 1) << 4) as u8 | (index % 3) as u8;
                g.ligated = if i % 7 == 0 { 0 } else { g.ligated };
            }
        }
        emit(c, index % 997 == 0);
    }
    for n in [15, 16, 31, 64, 256, 2048] {
        for pattern in &patterns[1..] {
            for font in [0, 34] {
                emit(
                    raw(font, "mymr", 1, &pattern.repeat(n), vec![71, 72]),
                    false,
                );
            }
        }
        for part in [
            vec![0x1031, 0xfe00],
            vec![0x102f, 0x1036],
            vec![0x1039, 0x1000],
        ] {
            let mut points = vec![0x1000];
            points.extend(part.repeat(n));
            emit(raw(0, "mymr", 1, &points, vec![71, 72]), false);
        }
    }
}
