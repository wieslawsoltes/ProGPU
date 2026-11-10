//! Authored mapping/script inventories and source-text workloads.
#![allow(dead_code)]
#[path = "managed_mapping_fixture.rs"]
pub mod mapping;
use mapping::font;
pub fn units(points: &[u32]) -> Vec<u16> {
    let mut out = Vec::new();
    for &point in points {
        if (0xd800..=0xdfff).contains(&point) {
            out.push(point as u16);
        } else {
            out.extend_from_slice(char::from_u32(point).unwrap().encode_utf16(&mut [0; 2]));
        }
    }
    out
}
pub fn script_table(tags: &[[u8; 4]]) -> Vec<u8> {
    let mut out = vec![0; 8 + tags.len() * 6];
    font::u16_at(&mut out, 4, 6);
    font::u16_at(&mut out, 6, tags.len() as u16);
    for (i, tag) in tags.iter().enumerate() {
        out[8 + i * 6..12 + i * 6].copy_from_slice(tag);
    }
    out
}
fn groups(ranges: &[(u32, u32, u32)]) -> Vec<u8> {
    let mut out = vec![0; 16 + ranges.len() * 12];
    font::u16_at(&mut out, 0, 13);
    let len = out.len() as u32;
    font::u32_at(&mut out, 4, len);
    font::u32_at(&mut out, 12, ranges.len() as u32);
    for (i, &(start, end, glyph)) in ranges.iter().enumerate() {
        for (j, v) in [start, end, glyph].into_iter().enumerate() {
            font::u32_at(&mut out, 16 + i * 12 + j * 4, v);
        }
    }
    out
}
pub fn fonts() -> Vec<Vec<u8>> {
    let full = groups(&[(0, 0x10ffff, 7)]);
    let missing = groups(&[]);
    let space = groups(&[(0x20, 0x20, 3)]);
    let holes = [0xc0, 0x344, 0x9cc, 0x2011, 0xfffd, 0x1f469];
    let mut ranges = Vec::new();
    let mut start = 0;
    for hole in holes {
        ranges.push((start, hole - 1, 7));
        start = hole + 1;
    }
    ranges.push((start, 0x10ffff, 7));
    let selector = mapping::selectors(&[
        (0xfe0f, 65, 0),
        (0xfe0e, 65, 5),
        (0xe0100, 0x1f469, 6),
        (0xfe0f, 0xfffd, 1),
    ]);
    let mut result = Vec::new();
    for nominal in [full.clone(), missing, space, groups(&ranges)] {
        result.push(font::sfnt(
            &font::tables(font::cmap(&[(0, 4, nominal), (0, 5, selector.clone())])),
            0,
        ));
    }
    for tags in [
        vec![*b"dev2", *b"mym2"],
        vec![*b"dev2", *b"dev3", *b"bng3", *b"mym2"],
        vec![
            *b"bng3", *b"dev3", *b"gjr3", *b"gur3", *b"knd3", *b"mlm3", *b"ory3", *b"tml3",
            *b"tel3",
        ],
        vec![
            *b"bng2", *b"dev2", *b"gjr2", *b"gur2", *b"knd2", *b"mlm2", *b"ory2", *b"tml2",
            *b"tel2",
        ],
    ] {
        let mut tables = font::tables(font::cmap(&[(0, 4, full.clone())]));
        tables.push((*b"GSUB", script_table(&tags)));
        result.push(font::sfnt(&tables, 0));
    }
    for len in 0..20 {
        let mut raw = script_table(&[*b"dev3", *b"dev2"]);
        raw.truncate(len);
        let mut tables = font::tables(font::cmap(&[(0, 4, full.clone())]));
        tables.push((*b"GSUB", raw));
        result.push(font::sfnt(&tables, 0));
    }
    let mut bad = full;
    font::u32_at(&mut bad, 12, u32::MAX);
    result.push(font::sfnt(&font::tables(font::cmap(&[(0, 4, bad)])), 0));
    result.push(vec![]);
    result
}
pub const SCRIPTS: &[&str] = &[
    "latn",
    "deva",
    "beng",
    "gujr",
    "guru",
    "knda",
    "mlym",
    "orya",
    "taml",
    "telu",
    "mymr",
    "khmr",
    "hang",
    "tibt",
    "mong",
    "sinh",
    "java",
    "marc",
    "limb",
    "tale",
    "bugi",
    "khar",
    "sylo",
    "tfng",
    "bali",
    "nkoo",
    "phag",
    "cham",
    "kali",
    "lepc",
    "rjng",
    "saur",
    "sund",
    "egyp",
    "kthi",
    "mtei",
    "lana",
    "tavt",
    "batk",
    "brah",
    "mand",
    "cakm",
    "plrd",
    "shrd",
    "takr",
    "dupl",
    "gran",
    "khoj",
    "sind",
    "mahj",
    "mani",
    "modi",
    "hmng",
    "phlp",
    "sidd",
    "tirh",
    "ahom",
    "mult",
    "adlm",
    "bhks",
    "newa",
    "gonm",
    "soyo",
    "zanb",
    "dogr",
    "gong",
    "rohg",
    "maka",
    "medf",
    "sogo",
    "sogd",
    "elym",
    "nand",
    "hmnp",
    "wcho",
    "chrs",
    "diak",
    "kits",
    "yezi",
    "cpmn",
    "ougr",
    "tnsa",
    "toto",
    "vith",
    "kawi",
    "nagm",
    "DFLT",
    "",
    "deva-extra",
    "DEVA",
    "\u{1f600}",
];
