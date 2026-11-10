//! Original Indic preparation, preserving every unrelated record/state field.
use super::{Case, fixture, raw, source};

const POINTS: [u32; 27] = [
    0,
    0x915,
    0x904,
    0x93c,
    0x94d,
    0x200c,
    0x200d,
    0x93a,
    0x900,
    0x951,
    0x2d,
    0x25cc,
    0xa40,
    0xd4e,
    0x930,
    0xa75,
    0x93d,
    0xcf1,
    0xb2,
    0x17c9,
    0x102d,
    0xfe00,
    0x34f,
    0xd800,
    0x10ffff,
    0x110000,
    u32::MAX,
];

pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    for length in 0..=3 {
        for mut ordinal in 0..POINTS.len().pow(length) {
            let original = ordinal;
            let points: Vec<_> = (0..length)
                .map(|_| {
                    let point = POINTS[ordinal % POINTS.len()];
                    ordinal /= POINTS.len();
                    point
                })
                .collect();
            for actions in [vec![79], vec![80], vec![80, 80]] {
                let mut c = raw(0, "deva", (original % 4) as u8, &points, actions);
                c.source.flags = original as u8;
                for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                    g.cluster = [i32::MAX, -1, i32::MIN][i % 3];
                }
                emit(c, length <= 1 || length == 2 && original % 43 == 0);
            }
        }
    }
    let patterns = [
        vec![],
        vec![0x93f],
        vec![0x930, 0x94d, 0x915, 0x93c, 0x93c, 0x93f, 0x900, 0x951],
        vec![0x930, 0x94d, 0x904, 0x200d, 0x94d, 0x915],
        vec![0xd4e, 0x25cc, 0x94d, 0x200d, 0x93c, 0x915, 0x900],
        vec![0xcf1, 0x2d, 0x93c, 0xa75, 0x94d, 0x915],
        vec![0x93d, 0x93c, 0x900, 0xb2, 0x200c, 0x951, 0x951],
        vec![0x200c, 0x200d, 0xb2, 0xa40, 0x93c, 0x94d, 0x93a],
        vec![0x93c, 0x93c, 0x93c, 0x900, 0x900, 0xb2],
        vec![0x915, 0xa75, 0x94d, 0x915, 0x93f],
        vec![0xd800, 0x110000, 0x200c, 0xffffffff, 0x34f, 0x10ffff],
    ];
    for (n, points) in patterns.iter().enumerate() {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in 0..=255 {
                    let mut c = raw(font, "deva", level, points, vec![80]);
                    c.source.flags = flags;
                    c.source.pre = vec![0x93f, 0xd800];
                    c.source.post = vec![0x94d, 0x915];
                    for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                        g.cluster = [i32::MIN, 8, 8, -1, i32::MAX][i % 5];
                    }
                    emit(c, level == 1 && flags == 16);
                }
                for actions in [
                    vec![79, 80, 79],
                    vec![80, 20, 80],
                    vec![80, 18, 80],
                    vec![68, 80],
                    vec![80, 71, 80],
                    vec![80, 74, 80],
                ] {
                    emit(raw(font, "deva", level, points, actions), level == 1);
                }
                for flags in [0, 16, 255] {
                    let mut c = Case {
                        source: source(
                            font,
                            "deva",
                            level,
                            fixture::preparation::units(
                                &points
                                    .iter()
                                    .copied()
                                    .filter(|&cp| cp <= 0x10ffff)
                                    .collect::<Vec<_>>(),
                            ),
                        ),
                        actions: vec![80],
                        raw: None,
                    };
                    c.source.flags = flags;
                    emit(c, level == 1 && flags == 0);
                }
            }
            for script in [
                "deva", "dev2", "beng", "bng2", "taml", "mlym", "sinh", "mymr", "latn", "", "DEVA",
                "deva ", "😀",
            ] {
                emit(
                    raw(font, script, 1, points, vec![80]),
                    font == 0 && n % 3 == 0,
                );
            }
        }
    }
    let facts = include_bytes!("script_properties.bin");
    let points: Vec<_> = facts[8..]
        .chunks_exact(10)
        .map(|v| u32::from_le_bytes(v[..4].try_into().unwrap()))
        .collect();
    for chunk in points.chunks(48) {
        emit(raw(0, "deva", 1, chunk, vec![80]), false);
    }
    let mut seed = 0x494e4449u32;
    for index in 0..16000 {
        let mut next = || {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed
        };
        let length = (next() % 129) as usize;
        let points: Vec<_> = (0..length)
            .map(|_| POINTS[next() as usize % POINTS.len()])
            .collect();
        let mut c = raw(
            [0, 1, 34, fixture::FALLBACK_FONT][index % 4],
            "deva",
            (index % 4) as u8,
            &points,
            if index % 5 == 0 { vec![79] } else { vec![80] },
        );
        c.source.flags = next() as u8;
        for g in c.raw.as_mut().unwrap() {
            let cp = g.code_point;
            let mut words = std::array::from_fn::<_, 31, _>(|_| next());
            words[4] = cp;
            *g = super::replay::preparation::records::from_words(&words);
        }
        emit(c, index % 997 == 0);
    }
    for n in [15, 16, 31, 64, 256, 2048] {
        for pattern in &patterns[1..] {
            emit(raw(0, "deva", 1, &pattern.repeat(n), vec![80]), false);
        }
        for part in [vec![0x200c], vec![0x200d], vec![0x200c, 0x200d]] {
            for suffix in [vec![], vec![0x93f], vec![0xb2, 0xa40]] {
                let mut points = part.repeat(n);
                points.extend(suffix);
                emit(raw(0, "deva", 1, &points, vec![80]), false);
            }
        }
    }
}
