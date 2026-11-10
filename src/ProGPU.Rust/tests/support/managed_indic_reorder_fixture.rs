//! Authored font lookups that expose original Indic probe priority and mutation.
#![allow(dead_code)]
use super::preparation::mapping::font;
#[path = "gsub_fixture.rs"]
mod gsub;
use gsub::{put, words};
#[path = "layout_selection_fixture.rs"]
mod selection;
pub const COUNT: usize = 16;
pub fn fonts() -> Vec<Vec<u8>> {
    let mut result = Vec::new();
    for variant in 0..COUNT {
        let tag = [*b"rphf", *b"blwf", *b"vatu", *b"pstf", *b"pref"][variant % 5];
        let mut lookups = vec![(4, 0, vec![gsub::ligature(&[20], 50)])];
        let mut features = vec![(tag, vec![0])];
        let required = if variant == 10 { 0 } else { u16::MAX };
        match variant {
            5 => {
                lookups = vec![
                    (5, 0, vec![gsub::context(false, 3, &[(1, 2)])]),
                    (4, 0, vec![gsub::ligature(&[21], 51)]),
                    (1, 0, vec![gsub::single(1)]),
                ];
                features = vec![(*b"pref", vec![0, 1])];
            }
            6 => {
                lookups = vec![
                    (1, 0, vec![gsub::single(0)]),
                    (4, 0, vec![gsub::ligature(&[20], 52)]),
                ];
                features = vec![(*b"blwf", vec![0]), (*b"pstf", vec![1])];
            }
            7 => {
                lookups = vec![(4, 0, vec![gsub::ligature(&[20, 30], 53)])];
                features = vec![(*b"rphf", vec![0])];
            }
            8 => {
                lookups = vec![(2, 0, vec![gsub::multiple(&[])])];
                features = vec![(*b"vatu", vec![0])];
            }
            9 => {
                lookups = vec![(1, 0, vec![words(&[2, 6, 0, 1, 1, 10])])];
                features = vec![(*b"blwf", vec![0])];
            }
            11 => {
                lookups = vec![
                    (1, 0, vec![gsub::single(0)]),
                    (4, 0, vec![gsub::ligature(&[20], 54)]),
                ];
                features = vec![
                    (*b"rphf", vec![0, 1]),
                    (*b"pref", vec![1]),
                    (*b"blwf", vec![1]),
                    (*b"pstf", vec![1]),
                ];
            }
            12 => {
                lookups[0].1 = 2;
            }
            13 => {
                lookups[0].1 = 8;
            }
            14 => {
                lookups = vec![(3, 0, vec![gsub::multiple(&[10, 55])])];
                features = vec![(*b"pref", vec![0])];
            }
            15 => {
                lookups = vec![(7, 0, vec![gsub::extension(4, &gsub::ligature(&[20], 56))])];
                features = vec![(*b"rphf", vec![0])];
            }
            _ => {}
        }
        let data = selection::table(gsub::table(&lookups), &features, required);
        let mut tables = font::tables(font::cmap(&[(0, 4, font::groups(13, 0, 0x10ffff, 10))]));
        tables.push((*b"GSUB", data));
        if matches!(variant, 12 | 13) {
            let mut gdef = vec![0; 12];
            font::u16_at(&mut gdef, 0, 1);
            font::u16_at(&mut gdef, 4, 12);
            gdef.extend(words(&[1, 10, 1, if variant == 12 { 1 } else { 3 }]));
            tables.push((*b"GDEF", gdef));
        }
        result.push(font::sfnt(&tables, 0));
    }
    result
}
