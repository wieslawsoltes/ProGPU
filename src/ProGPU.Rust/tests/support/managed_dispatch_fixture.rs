#![allow(dead_code)]
#[path = "font_fixture.rs"]
mod font;
#[path = "gsub_fixture.rs"]
pub mod gsub;
pub fn table(mut data: Vec<u8>, features: &[([u8; 4], Vec<u16>)], required: u16) -> Vec<u8> {
    use gsub::{put, words};
    let script = data.len();
    put(&mut data, 4, script as u16);
    data.extend(words(&[
        1,
        0x4446,
        0x4c54,
        8,
        4,
        0,
        0,
        required,
        features.len() as u16,
    ]));
    data.extend(words(&(0..features.len() as u16).collect::<Vec<_>>()));
    let list = data.len();
    put(&mut data, 6, list as u16);
    data.extend(words(&[features.len() as u16]));
    for (tag, _) in features {
        data.extend(tag);
        data.extend([0, 0]);
    }
    for (i, (_, lookups)) in features.iter().enumerate() {
        let relative = (data.len() - list) as u16;
        put(&mut data, list + 6 + i * 6, relative);
        data.extend(words(&[0, lookups.len() as u16]));
        data.extend(words(lookups));
    }
    data
}
pub fn font(data: &[u8], gdef: &[u8]) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", data.into()));
    if !gdef.is_empty() {
        tables.push((*b"GDEF", gdef.into()));
    }
    font::sfnt(&tables, 0)
}
