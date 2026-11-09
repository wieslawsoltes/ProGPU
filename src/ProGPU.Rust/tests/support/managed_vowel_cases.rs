//! Public invalid-cluster specification supplies inputs; actual C# owns outputs.
use super::{Case, fixture, raw, source};
fn inventory() -> Vec<(String, Vec<u32>)> {
    include_str!("vowel_inventory.txt")
        .lines()
        .filter(|s| !s.starts_with('#'))
        .map(|line| {
            let mut fields = line.split_whitespace();
            let script = fields.next().unwrap().to_owned();
            let points = fields
                .map(|v| u32::from_str_radix(v, 16).unwrap())
                .filter(|&v| v != 0)
                .collect();
            (script, points)
        })
        .collect()
}
pub fn corpus(mut emit: impl FnMut(Case, bool)) {
    let rows = inventory();
    let mut scripts: Vec<_> = rows.iter().map(|r| r.0.clone()).collect();
    scripts.sort();
    scripts.dedup();
    for (row_index, (script, points)) in rows.iter().enumerate() {
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            for level in 0..4 {
                for flags in [0, 1, 16, 17, 64, 128, 255] {
                    for actions in [vec![68], vec![68, 68], vec![68, 20, 68]] {
                        let mut c = raw(font, script, level, points, actions.clone());
                        c.source.flags = flags;
                        for (i, g) in c.raw.as_mut().unwrap().iter_mut().enumerate() {
                            g.cluster = [i32::MIN, -1, i32::MAX][i % 3];
                            g.grapheme_cluster = 77 + i as i32;
                        }
                        emit(
                            c,
                            level == 1
                                && flags == 16
                                && actions.len() == 1
                                && (font == 0 || font == 34 && row_index % 13 == 0),
                        );
                    }
                }
            }
        }
        // Exact script spelling and raw malformed/nonmatching component values.
        for name in scripts.iter().cloned().chain([
            script.to_uppercase(),
            format!("{script} "),
            script[..3].into(),
            "dev2".into(),
            "DFLT".into(),
            "".into(),
            "😀".into(),
        ]) {
            for component in 0..points.len() {
                for value in [
                    0,
                    points[component] - 1,
                    points[component],
                    points[component] + 1,
                    0x25cc,
                    0xd800,
                    0xdfff,
                    0x110000,
                    u32::MAX,
                ] {
                    let mut changed = points.clone();
                    changed[component] = value;
                    emit(
                        raw(0, &name, (row_index % 4) as u8, &changed, vec![68]),
                        name == script.to_uppercase()
                            && component == 0
                            && value == points[0]
                            && row_index % 17 == 0,
                    );
                }
            }
        }
        for flags in 0..=255 {
            let mut c = raw(0, script, flags % 4, points, vec![68]);
            c.source.flags = flags;
            c.source.pre = vec![0x301, 0xd800];
            c.source.post = vec![0x25cc, 0x644];
            emit(c, false);
        }
        for font in [0, 1, 34, fixture::FALLBACK_FONT] {
            emit(
                Case {
                    source: source(
                        font,
                        script,
                        (row_index % 4) as u8,
                        fixture::preparation::units(points),
                    ),
                    actions: vec![68],
                    raw: None,
                },
                font == 0 && row_index % 11 == 0,
            );
            // A truncated triple, leading/trailing gaps and repeated matches.
            for pattern in [
                points[..points.len() - 1].to_vec(),
                [vec![65], points.clone(), vec![65]].concat(),
                points.repeat(4),
            ] {
                emit(
                    raw(font, script, 1, &pattern, vec![68]),
                    row_index % 17 == 0 && font == 0,
                );
            }
        }
        // Every adjacent rule pair retains the first rule's actual skip count.
        for (other_script, other) in &rows {
            for font in [0, 1, fixture::FALLBACK_FONT] {
                let input = [points.clone(), other.clone()].concat();
                emit(
                    raw(font, script, 1, &input, vec![68]),
                    font == 0 && script == other_script && points == other && row_index % 9 == 0,
                );
            }
        }
    }
    // All short lengths and exact raw spelling stay no-ops without a full match.
    for script in &scripts {
        for points in [
            vec![],
            vec![0x905],
            vec![0x930, 0x94d],
            vec![0xd800, u32::MAX],
        ] {
            emit(raw(0, script, 1, &points, vec![68]), true);
        }
    }
    let pool: Vec<_> = rows
        .iter()
        .flat_map(|r| r.1.iter().copied())
        .chain([0, 0x25cc, 0x200d, 0x301, 0xd800, 0x110000, u32::MAX])
        .collect();
    let mut seed = 0x71a2_c35fu32;
    let mut next = || {
        seed ^= seed << 13;
        seed ^= seed >> 17;
        seed ^= seed << 5;
        seed
    };
    for _ in 0..12000 {
        let (script, row) = &rows[next() as usize % rows.len()];
        let n = next() as usize % 24;
        let mut points: Vec<_> = (0..n).map(|_| pool[next() as usize % pool.len()]).collect();
        points.extend(row);
        let mut c = raw(
            [0, 1, 34, fixture::FALLBACK_FONT][next() as usize % 4],
            script,
            (next() % 4) as u8,
            &points,
            vec![68, 20, 68],
        );
        c.source.flags = next() as u8;
        for g in c.raw.as_mut().unwrap() {
            let cp = g.code_point;
            let words = std::array::from_fn::<_, 31, _>(|_| next());
            *g = super::replay::preparation::records::from_words(&words);
            g.code_point = cp;
        }
        emit(c, false);
    }
    for (script, points) in &rows {
        emit(raw(0, script, 1, &points.repeat(1024), vec![68]), false);
    }
}
