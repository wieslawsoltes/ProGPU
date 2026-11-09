//! Authored full-record action controls, never a foreign shaping implementation.
#![allow(dead_code)]
#[path = "managed_preparation_fixture.rs"]
pub mod preparation;
use preparation::mapping::font;
pub fn fonts() -> Vec<Vec<u8>> {
    let mut fonts = preparation::fonts();
    // Selected coverage holes force original font-dependent decomposition routes.
    for holes in [
        vec![(0xac00, 0xd7a3)],
        vec![(0x1161, 0x1175)],
        vec![(0x11a8, 0x11c2)],
        vec![(0xfb00, 0xfb4f)],
        vec![(0x25cc, 0x25cc)],
        vec![(0x28, 0x28)],
        vec![(0xfe10, 0xfe4f)],
    ] {
        let mut groups = Vec::new();
        let mut first = 0;
        for (start, end) in holes {
            if first < start {
                groups.push((first, start - 1, 7));
            }
            first = end + 1;
        }
        groups.push((first, 0x10ffff, 7));
        let mut cmap = vec![0; 16 + 12 * groups.len()];
        font::u16_at(&mut cmap, 0, 13);
        let len = cmap.len() as u32;
        font::u32_at(&mut cmap, 4, len);
        font::u32_at(&mut cmap, 12, groups.len() as u32);
        for (i, (a, b, c)) in groups.into_iter().enumerate() {
            for (j, v) in [a, b, c].into_iter().enumerate() {
                font::u32_at(&mut cmap, 16 + 12 * i + 4 * j, v);
            }
        }
        fonts.push(font::sfnt(&font::tables(font::cmap(&[(0, 4, cmap)])), 0));
    }
    fonts.extend(fallback_fonts());
    fonts
}

// Independently captured input/output facts are fixture inputs, never Rust outputs.
pub fn fallback_inventory() -> Vec<(String, Vec<u16>)> {
    include_str!("arabic_fallback_inventory.txt")
        .lines()
        .filter(|line| !line.starts_with('#') && !line.is_empty())
        .map(|line| {
            let mut fields = line.split_whitespace();
            (
                fields.next().unwrap().to_owned(),
                fields
                    .map(|v| u16::from_str_radix(v, 16).unwrap())
                    .collect(),
            )
        })
        .collect()
}
pub const FALLBACK_FONT: usize = 37;
pub fn fallback_fonts() -> Vec<Vec<u8>> {
    let rows = fallback_inventory();
    let forms: Vec<_> = rows.iter().filter(|r| r.0 == "form").collect();
    let ligatures: Vec<_> = rows.iter().filter(|r| r.0 != "form").collect();
    let bases: Vec<_> = forms.iter().map(|r| r.1[0]).collect();
    let presentations: Vec<_> = forms
        .iter()
        .flat_map(|r| r.1[1..].iter().copied())
        .collect();
    let results: Vec<_> = ligatures.iter().map(|r| *r.1.last().unwrap()).collect();
    let mut fonts = vec![
        fallback_font(&[], &[], None),
        fallback_font(&presentations, &[], None),
        fallback_font(&bases, &[], None),
        fallback_font(&results, &[], None),
        fallback_font(&[], &[], Some(1)),
        fallback_font(&[], &[], Some(3)),
        fallback_font(&[], &[], Some(259)),
        fallback_font(&[], &[], Some(256)),
    ];
    // Every individual missing component and target is independently authored.
    for row in &ligatures {
        for &cp in &row.1 {
            fonts.push(fallback_font(&[cp], &[], None));
        }
    }
    // Ambiguous input aliases expose the complete captured row priority.
    for kind in ["triple", "pair", "mark"] {
        let selected: Vec<_> = ligatures.iter().filter(|r| r.0 == kind).collect();
        let aliases: Vec<_> = selected
            .iter()
            .flat_map(|r| r.1[..r.1.len() - 1].iter().copied())
            .collect();
        let mut disabled = Vec::new();
        for row in selected {
            fonts.push(fallback_font(&disabled, &aliases, Some(1)));
            disabled.push(*row.1.last().unwrap());
        }
    }
    fonts
}
fn fallback_font(holes: &[u16], aliases: &[u16], class: Option<u16>) -> Vec<u8> {
    let mut groups: Vec<(u32, u32, u32)> = Vec::new();
    for cp in 1..=u16::MAX {
        let glyph = if holes.contains(&cp) {
            0
        } else if aliases.contains(&cp) {
            1
        } else {
            cp as u32
        };
        if let Some(last) = groups.last_mut()
            && glyph != 0
            && last.1 + 1 == cp as u32
            && last.2 + cp as u32 - last.0 == glyph
        {
            last.1 = cp as u32;
            continue;
        }
        if glyph != 0 {
            groups.push((cp as u32, cp as u32, glyph));
        }
    }
    let mut cmap = vec![0; 16 + 12 * groups.len()];
    font::u16_at(&mut cmap, 0, 12);
    let len = cmap.len() as u32;
    font::u32_at(&mut cmap, 4, len);
    font::u32_at(&mut cmap, 12, groups.len() as u32);
    for (i, &(a, b, c)) in groups.iter().enumerate() {
        for (j, v) in [a, b, c].into_iter().enumerate() {
            font::u32_at(&mut cmap, 16 + 12 * i + 4 * j, v);
        }
    }
    let mut tables = font::tables(font::cmap(&[(0, 4, cmap)]));
    if let Some(class) = class {
        let mut gdef = vec![0; 22];
        for (at, value) in [
            (0, 1),
            (4, 12),
            (12, 2),
            (14, 1),
            (16, 0),
            (18, u16::MAX),
            (20, class),
        ] {
            font::u16_at(&mut gdef, at, value);
        }
        tables.push((*b"GDEF", gdef));
    }
    font::sfnt(&tables, 0)
}
