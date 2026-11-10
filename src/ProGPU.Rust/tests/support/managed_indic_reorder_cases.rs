//! Complete original record controls for font-dependent Indic reordering.
use super::{Case, fixture, raw, source};
use progpu::text::{ManagedShapingGlyph, unicode::ManagedIndicProperties};
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    let first_font = fixture::FALLBACK_FONT + fixture::fallback_fonts().len();
    let mut fonts = vec![0, 1, 2, 34, fixture::FALLBACK_FONT];
    fonts.extend(first_font..first_font + fixture::indic_reorder::COUNT);
    let scripts = [
        "deva", "beng", "guru", "gujr", "orya", "taml", "telu", "knda", "mlym", "sinh", "latn", "",
        "DEVA", "dev2", "deva ",
    ];
    let patterns = [
        vec![],
        vec![0x93f],
        vec![0x930, 0x94d, 0x915, 0x93f, 0x900],
        vec![0x930, 0x94d, 0x200d, 0x915, 0x93c, 0x94d, 0x916, 0x93f],
        vec![0xd4e, 0x200d, 0x915, 0x94d, 0x916, 0x900, 0x951],
        vec![0x93f, 0x93c, 0x915, 0x94d, 0x200d, 0x916, 0x93f, 0x900],
        vec![0x915, 0x94d, 0x916, 0x93f, 0x94d, 0x917, 0x900],
        vec![0x25cc, 0x93f, 0x200c, 0x915, 0x94d, 0x916],
        vec![0xd4e, 0x93f, 0x93c, 0x93f, 0x915, 0x900],
        vec![0x20, 0x93f, 0x915, 0x94d, 0x916, 0x900],
    ];
    for &font in &fonts {
        for (s, script) in scripts.iter().enumerate() {
            for (p, points) in patterns.iter().enumerate() {
                for code in 81..=92 {
                    for level in 0..4 {
                        let mut c = case(font, script, level, points, vec![code]);
                        c.source.flags = [0, 16, 255][(p + s) % 3];
                        emit(
                            c,
                            level == 1 && s % 3 == 0 && p == code as usize % patterns.len(),
                        );
                    }
                }
                for actions in [
                    vec![80, 82, 81],
                    vec![80, 83, 81, 81],
                    vec![83, 20, 83, 81],
                    vec![64, 66, 83, 81, 67],
                    vec![80, 84, 81],
                    vec![80, 87, 81],
                ] {
                    emit(case(font, script, 1, points, actions), p == 2 && s == 0);
                }
                emit(
                    Case {
                        source: source(font, script, 1, fixture::preparation::units(points)),
                        actions: vec![80, 82, 81],
                        raw: None,
                    },
                    font == first_font && p % 3 == 0,
                );
            }
        }
    }
    // Every raw category, position, syllable and substitution byte independently.
    for byte in 0..=255u8 {
        for field in 0..9 {
            for code in [81, 83, 84] {
                for script in ["deva", "telu", "knda", "mlym"] {
                    let mut c = case(first_font + 5, script, 1, &patterns[3], vec![code]);
                    for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                        if i % 2 != 0 {
                            continue;
                        }
                        match field {
                            0 => g.indic_category = byte,
                            1 => g.indic_position = byte,
                            2 => g.use_syllable = byte,
                            3 => g.ligated = byte,
                            4 => g.multiplied = byte,
                            5 => g.substituted = byte,
                            6 => g.script_feature_mask = byte,
                            7 => g.indic_original_order = byte,
                            _ => g.script_shaper = byte,
                        }
                    }
                    emit(c, byte % 51 == 0 && field % 3 == 0 && script == "mlym");
                }
            }
        }
    }
    // Full byte-level mutations, including invalid code points after prior work.
    let mut seed = 0x494e4452u32;
    for n in 0..24000 {
        let mut next = || {
            seed ^= seed << 13;
            seed ^= seed >> 17;
            seed ^= seed << 5;
            seed
        };
        let len = (next() % 65) as usize;
        let points = vec![0x915; len];
        let code = 81 + next() % 12;
        let mut c = case(
            fonts[next() as usize % fonts.len()],
            scripts[next() as usize % scripts.len()],
            (next() % 4) as u8,
            &points,
            vec![code],
        );
        c.source.flags = next() as u8;
        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
            let words = std::array::from_fn::<_, 31, _>(|_| next());
            *g = super::replay::preparation::records::from_words(&words);
            g.glyph_index = [10, 20, 30, 0, 7, 0xffff][next() as usize % 6];
            g.indic_category = (next() % 20) as u8;
            g.indic_position = (next() % 16) as u8;
            g.use_syllable = if n % 3 == 0 {
                0x10
            } else {
                ((i / 4) as u8).wrapping_mul(16)
            };
            if n % 2 == 0 {
                g.ligated = (next() % 2) as u8;
                g.multiplied = (next() % 2) as u8;
                g.substituted = (next() % 2) as u8;
            }
            if n % 4 != 0 {
                g.code_point = [0x61, 0x20, 0x200d, 0x93f, 0x915, 0x1c89][next() as usize % 6];
            }
        }
        emit(c, n % 997 == 0);
    }
    for cp in [
        0,
        0x20,
        0x41,
        0x61,
        0x300,
        0x903,
        0x488,
        0x31,
        0x2160,
        0xb2,
        0x2028,
        0x2029,
        0x200d,
        0xe000,
        0x5f,
        0x2d,
        0x28,
        0x29,
        0x2018,
        0x2019,
        0x21,
        0x2b,
        0x24,
        0x5e,
        0x2600,
        0x378,
        0xd800,
        0xdfff,
        0x10ffff,
        0x110000,
        u32::MAX,
    ] {
        let mut c = case(0, "deva", 1, &[cp, 0x93f], vec![81]);
        let g = c.raw.as_mut().unwrap();
        g[0].use_syllable = 0x10;
        g[1].use_syllable = 0x20;
        emit(c, true);
    }
    for n in [15, 16, 127, 128, 255, 256, 1024, 4096] {
        for (script, code) in [("deva", 83), ("knda", 84), ("mlym", 81), ("taml", 81)] {
            let points = patterns[5]
                .iter()
                .copied()
                .cycle()
                .take(n)
                .collect::<Vec<_>>();
            emit(case(first_font + 11, script, 1, &points, vec![code]), false);
        }
    }
}
fn case(font: usize, script: &str, level: u8, points: &[u32], actions: Vec<u32>) -> Case {
    let mut c = raw(font, script, level, points, actions);
    for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
        let facts = ManagedIndicProperties::for_raw_code_point(g.code_point);
        let cp = g.code_point;
        *g = ManagedShapingGlyph::new([10, 20, 30][i % 3], [i32::MAX, -1, 8, i32::MIN][i % 4], cp);
        g.indic_category = facts.category();
        g.indic_position = facts.position();
        g.use_syllable = 0x10;
    }
    c
}
