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
    fonts
}
