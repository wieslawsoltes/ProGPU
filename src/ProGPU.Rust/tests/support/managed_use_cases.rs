//! Whole original USE stages, including filtered positions and retained records.
use super::{Case, fixture, raw, source};
const POINTS: [u32; 44] = [
    0, 0x30, 0x11052, 0x2d, 0x34f, 0xf8d, 0x94d, 0x1107f, 0x200c, 0xad, 0xd4e, 0x93f, 0x1c34,
    0x17cc, 0x1939, 0x1930, 0x1a5a, 0xa75, 0x103b, 0x103c, 0xa71, 0x859, 0x93a, 0x941, 0x93b,
    0x7eb, 0x952, 0x903, 0x1b6b, 0xcf1, 0x1039, 0x9fe, 0xf35, 0xb2, 0x1a60, 0x13000, 0x13430, 0x5b,
    0x5d, 0xdca, 0x13447, 0x13440, 0x1bf2, 0x110000,
];
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    for length in 0..=3 {
        for mut ordinal in 0..44usize.pow(length) {
            let original = ordinal;
            let points: Vec<_> = (0..length)
                .map(|_| {
                    let point = POINTS[ordinal % 44];
                    ordinal /= 44;
                    point
                })
                .collect();
            let mut c = raw(
                0,
                "tibt",
                (original % 4) as u8,
                &points,
                vec![74, 75, 76, 78],
            );
            c.source.flags = original as u8;
            for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                g.cluster = [i32::MAX, -1, i32::MIN][i % 3];
            }
            emit(c, length <= 1 || length == 2 && original % 47 == 0);
        }
    }
    for a in 0..=255u8 {
        for b in 0..=255u8 {
            let mut c = raw(0, "latn", 1, &[65, 66, 67, 68, 69], vec![78]);
            c.source.flags = 16;
            for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                g.use_category = [18, a, b, 22, 23][i];
                g.use_syllable = 0x12;
                g.ligated = if a & 1 == 0 { 0 } else { b };
                g.multiple_substitution_component = if b & 1 == 0 { 0 } else { a };
                g.cluster = [9, -5, -5, 7, 8][i];
            }
            emit(c, a % 51 == 0 && b % 51 == 0);
        }
    }
    let patterns = [
        vec![],
        vec![0x34f],
        vec![0x34f, 0x200c, 0x34f, 0x93f, 0x34f],
        vec![0x200c, 0x34f, 0x30, 0x200c, 0x34f, 0x93f],
        vec![0x30, 0x200c, 0x34f, 0x110000, 0x93f],
        vec![0x30, 0x200c, 0xd800],
        vec![0x200c, u32::MAX],
        vec![0xd4e, 0x30, 0x94d, 0x30, 0x93f, 0x1c34],
        vec![0xd4e, 0xd4e, 0x93f, 0x93f, 0x1c34],
        vec![0x30, 0x1039, 0x30, 0x1a60, 0x30, 0x93f],
        vec![0x30, 0x93f, 0x1a60, 0x30, 0x93f],
        vec![0x11052, 0x1107f, 0x11052, 0x1107f],
        vec![0, 0x1b6b, 0x1b6b, 0x200c, 0xad, 0],
        vec![0x5b, 0x5b, 0x13000, 0x13440, 0x13447, 0x5d, 0x13430, 0x5b],
        vec![0x5b, 0x5b, 0x5b, 0x5b],
    ];
    for (n, points) in patterns.iter().enumerate() {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in [0, 1, 16, 17, 64, 128, 255] {
                    for actions in [
                        vec![73, 77],
                        vec![74],
                        vec![74, 78],
                        vec![74, 75, 76, 78],
                        vec![75, 76, 78],
                        vec![76, 75, 78],
                        vec![74, 18, 78],
                        vec![78],
                        vec![74, 20, 78],
                        vec![74, 21, 75, 76, 78],
                        vec![74, 78, 78],
                    ] {
                        let mut c = raw(font, "tibt", level, points, actions.clone());
                        c.source.flags = flags;
                        c.source.pre = vec![0x34f, 0xd800];
                        c.source.post = vec![0x200c, 0x93f];
                        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                            g.cluster = [i32::MIN, 8, 8, -1, i32::MAX][i % 5];
                        }
                        emit(c, level == 1 && flags == 16 && actions == [74, 78]);
                    }
                }
            }
            for script in [
                "tibt", "adlm", "arab", "chrs", "rohg", "mand", "mani", "mong", "nkoo", "ougr",
                "phag", "phlp", "sogd", "syrc", "TIBT", "arab ", "ARAB", "", "latn", "😀",
            ] {
                emit(
                    raw(font, script, 1, points, vec![74, 78]),
                    font == 0 && n % 5 == 0,
                );
            }
            for flags in 0..=255 {
                let mut c = raw(font, "tibt", flags % 4, points, vec![74, 78]);
                c.source.flags = flags;
                emit(c, false);
            }
            for level in 0..4 {
                for flags in [0, 16, 255] {
                    let mut c = Case {
                        source: source(
                            font,
                            "tibt",
                            level,
                            fixture::preparation::units(
                                &points
                                    .iter()
                                    .copied()
                                    .filter(|&p| p <= 0x10ffff)
                                    .collect::<Vec<_>>(),
                            ),
                        ),
                        actions: vec![14, 74, 75, 76, 78],
                        raw: None,
                    };
                    c.source.flags = flags;
                    emit(c, level == 1 && flags == 0);
                }
            }
        }
    }
    for kind in 0..=255u8 {
        for ligated in [0, 1, 255] {
            for actions in [vec![75], vec![76], vec![75, 76], vec![78]] {
                let mut c = raw(0, "tibt", 1, &[65, 66, 67, 68, 69, 70, 71], actions);
                c.source.flags = 16;
                for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                    g.use_syllable = if i == 0 || i == 6 { 0xf8 } else { kind };
                    g.use_category = [0, 18, 1, 12, 22, 23, 0][i];
                    g.ligated = ligated;
                    g.cluster = [9, 9, -7, 3, 4, 8, 8][i];
                    g.multiple_substitution_component = 0;
                    g.use_rphf_eligible = [0, 255, 1, 0, 1, 255, 0][i];
                    g.substituted = [255, 0, 1, 255, 1, 0, 255][i];
                }
                emit(c, kind % 16 <= 2 && ligated == 0);
            }
        }
    }
    let facts = include_bytes!("script_properties.bin");
    let points: Vec<_> = facts[8..]
        .chunks_exact(10)
        .map(|v| u32::from_le_bytes(v[..4].try_into().unwrap()))
        .collect();
    for chunk in points.chunks(48) {
        for font in [0, 34] {
            emit(raw(font, "tibt", 1, chunk, vec![74, 78]), false);
        }
    }
    let mut seed = 0x555345u32;
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
        let actions = match index % 4 {
            0 => vec![74, 75, 76, 78],
            1 => vec![78],
            2 => vec![75, 76],
            _ => vec![76, 75, 78],
        };
        let mut c = raw(
            [0, 1, 34, fixture::FALLBACK_FONT][index % 4],
            "tibt",
            (index % 4) as u8,
            &points,
            actions,
        );
        c.source.flags = next() as u8;
        for g in c.raw.as_mut().unwrap() {
            let cp = g.code_point;
            let mut words = std::array::from_fn::<_, 31, _>(|_| next());
            words[4] = cp;
            *g = super::replay::preparation::records::from_words(&words);
            if index % 4 == 1 {
                g.use_syllable = ((index % 15 + 1) << 4) as u8 | (index % 9) as u8;
            }
        }
        emit(c, index % 997 == 0);
    }
    for n in [15, 16, 31, 64, 256, 2048] {
        for pattern in &patterns[1..] {
            for font in [0, 34] {
                emit(
                    raw(font, "tibt", 1, &pattern.repeat(n), vec![74, 78]),
                    false,
                );
            }
        }
        for part in [
            vec![0x34f],
            vec![0x5b],
            vec![0x93f, 0x1c34],
            vec![0x1a60, 0x30],
        ] {
            let mut points = vec![0x30];
            points.extend(part.repeat(n));
            emit(raw(0, "tibt", 1, &points, vec![74, 78]), false);
        }
    }
}
