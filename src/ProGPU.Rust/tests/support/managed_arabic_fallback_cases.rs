//! Authored complete-record controls for actual original fallback observations.
use super::{Case, fixture, raw, source};
use fixture::FALLBACK_FONT as BASE;
fn identity(font: usize, points: &[u32], actions: Vec<u32>) -> Case {
    let mut c = raw(font, "arab", 1, points, actions);
    for glyph in c.raw.as_mut().unwrap() {
        glyph.glyph_index = glyph.code_point as u16;
        glyph.arabic_action = 0;
    }
    c
}
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    let rows = fixture::fallback_inventory();
    let forms: Vec<_> = rows.iter().filter(|r| r.0 == "form").collect();
    let rules: Vec<_> = rows.iter().filter(|r| r.0 != "form").collect();
    // All explicit feature masks and raw action values, with nominal/target holes.
    for font in [0, 1, BASE, BASE + 1, BASE + 2] {
        for code in (22..=28).chain(32..=63) {
            for action in (0..=9).chain([255]) {
                let points: Vec<_> = forms.iter().map(|r| u32::from(r.1[0])).collect();
                let mut c = identity(font, &points, vec![code]);
                for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                    g.arabic_action = action;
                    if font == 0 {
                        g.glyph_index = 7;
                    }
                    if font == BASE + 2 {
                        g.glyph_index = 0;
                    }
                    g.cluster = [i32::MAX, -1, i32::MIN, i as i32][i % 4];
                }
                if font == BASE {
                    // Already substituted nonzero and zero IDs must not be
                    // re-formed merely because their source code point has a form.
                    for missing in [false, true] {
                        let mut changed = c.clone();
                        for g in changed.raw.as_mut().unwrap() {
                            g.glyph_index = if missing { 0 } else { g.glyph_index + 1 };
                        }
                        emit(changed, action == 6 && code == 63);
                    }
                }
                emit(
                    c,
                    font == BASE && action == 6 && matches!(code, 22..=28 | 32 | 33 | 63),
                );
            }
        }
    }
    // Boundaries and unsupported scalars do not query a presentation glyph.
    for code in [22, 23, 25, 47, 63] {
        for action in [0, 1, 4, 6, 255] {
            for block in (0..=0x8ff).collect::<Vec<_>>().chunks(128) {
                let mut c = identity(BASE, block, vec![code]);
                for g in c.raw.as_mut().unwrap() {
                    g.arabic_action = action;
                }
                emit(c, false);
            }
            emit(
                identity(
                    BASE,
                    &[
                        0xd800,
                        0xdfff,
                        0xffff,
                        0x10000,
                        0x10ffff,
                        0x110000,
                        u32::MAX,
                    ],
                    vec![code],
                ),
                code == 63 && action == 0,
            );
        }
    }
    let mut hole_font = BASE + 8;
    for (rule_index, row) in rules.iter().enumerate() {
        let points: Vec<_> = row.1[..row.1.len() - 1]
            .iter()
            .map(|&v| u32::from(v))
            .collect();
        for code in [22, 23, 24, 25, 26, 27, 28, 32, 47, 48, 63] {
            emit(identity(BASE, &points, vec![code]), code == 48);
        }
        for font in (BASE..BASE + 8).chain(hole_font..hole_font + row.1.len()) {
            for level in 0..4 {
                for insert in [
                    None,
                    Some(0x301),
                    Some(0x651),
                    Some(0x200d),
                    Some(0x200c),
                    Some(0x34f),
                    Some(0x180b),
                    Some(0xfe0f),
                    Some(0x61c),
                    Some(0xd800),
                    Some(u32::MAX),
                ] {
                    let mut input = points.clone();
                    if let Some(cp) = insert {
                        input.insert(1, cp);
                    }
                    for mode in 0..5 {
                        let mut c = identity(
                            font,
                            &input,
                            match mode {
                                1 => vec![64, 48],
                                2 => vec![66, 48],
                                3 => vec![66, 67, 48],
                                _ => vec![48],
                            },
                        );
                        c.source.level = level;
                        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                            g.use_syllable = if mode == 2 { (i % 2) as u8 } else { 13 };
                            g.cluster = [i32::MAX, -1, i32::MIN, 7][i % 4];
                            if mode == 4 && i == 1 {
                                g.glyph_index = points[1] as u16;
                            }
                        }
                        emit(
                            c,
                            font == BASE
                                && level == 1
                                && mode == 1
                                && rule_index % 11 == 0
                                && matches!(insert, Some(0x301) | Some(0x200d) | Some(0xd800)),
                        );
                    }
                }
            }
        }
        // Original successful replacement skips its new position in this pass.
        let twice: Vec<_> = points.iter().chain(&points).copied().collect();
        emit(identity(BASE, &twice, vec![48, 48]), rule_index % 7 == 0);
        hole_font += row.1.len();
    }
    // Each priority witness removes only the preceding output, retaining aliases.
    for kind in ["triple", "pair", "mark"] {
        for row in rules.iter().filter(|r| r.0 == kind) {
            let points: Vec<_> = row.1[..row.1.len() - 1]
                .iter()
                .map(|&v| u32::from(v))
                .collect();
            let mut c = identity(hole_font, &points, vec![48]);
            for g in c.raw.as_mut().unwrap() {
                g.glyph_index = 1;
            }
            emit(c, true);
            hole_font += 1;
        }
    }
    // Actual owned preparation -> joining -> fallback -> final stage-state actions.
    for script in ["arab", "syrc", "latn", "ARAB", ""] {
        for level in 0..4 {
            for flags in [0, 1, 2, 3, 64, 128, 192, 255] {
                for points in [
                    vec![0x644, 0x627],
                    vec![0x644, 0x644, 0x647],
                    vec![0x628, 0x301, 0x644, 0x627],
                    vec![0x651, 0x64b],
                    vec![0x628, 0x200d, 0x628],
                ] {
                    let mut c = Case {
                        source: source(BASE, script, level, fixture::preparation::units(&points)),
                        actions: vec![21, 63, 17, 18, 19, 20],
                        raw: None,
                    };
                    c.source.flags = flags;
                    emit(c, script == "arab" && level == 1 && flags == 192);
                }
            }
        }
    }
    // Deterministic complete-record mutations include faults after prior replacements.
    let mut seed = 0xa7ab_1c55u32;
    let mut next = || {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        seed
    };
    let pool = [
        0x628,
        0x644,
        0x647,
        0xfedf,
        0xfee0,
        0xfeea,
        0xfee4,
        0xfea0,
        0xfea4,
        0x651,
        0x64b,
        0x64c,
        0x301,
        0x200d,
        0x200c,
        0x34f,
        0x180b,
        0xd800,
        0x110000,
        u32::MAX,
    ];
    for _ in 0..12000 {
        let n = 1 + next() as usize % 20;
        let points: Vec<_> = (0..n).map(|_| pool[next() as usize % pool.len()]).collect();
        let mut c = identity(BASE + next() as usize % 8, &points, vec![63, 20, 48]);
        c.source.level = (next() % 4) as u8;
        c.source.flags = next() as u8;
        for g in c.raw.as_mut().unwrap() {
            let cp = g.code_point;
            let id = g.glyph_index;
            let words = std::array::from_fn::<_, 31, _>(|_| next());
            *g = super::replay::preparation::records::from_words(&words);
            g.code_point = cp;
            g.glyph_index = id;
            g.arabic_action = [0, 1, 4, 6][next() as usize % 4];
        }
        emit(c, false);
    }
}
