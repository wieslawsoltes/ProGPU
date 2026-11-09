#![allow(dead_code)]
#[path = "font_fixture.rs"]
mod font;
#[path = "gsub_fixture.rs"]
pub mod gsub;
use gsub::{put, words};
#[path = "layout_selection_fixture.rs"]
mod selection;
pub use selection::table;
pub fn font(data: &[u8], gdef: &[u8]) -> Vec<u8> {
    let mut tables = font::tables(vec![]);
    tables.push((*b"GSUB", data.into()));
    if !gdef.is_empty() {
        tables.push((*b"GDEF", gdef.into()));
    }
    font::sfnt(&tables, 0)
}
