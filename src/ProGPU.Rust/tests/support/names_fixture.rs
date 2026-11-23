#![allow(dead_code)]
use super::font;

#[derive(Clone)]
pub struct Record {
    pub platform: u16,
    pub encoding: u16,
    pub language: u16,
    pub id: u16,
    pub bytes: Vec<u8>,
}
impl Record {
    pub fn new(platform: u16, encoding: u16, language: u16, id: u16, bytes: Vec<u8>) -> Self {
        Self {
            platform,
            encoding,
            language,
            id,
            bytes,
        }
    }
    pub fn utf16(platform: u16, language: u16, id: u16, text: &[u16]) -> Self {
        Self::new(
            platform,
            1,
            language,
            id,
            text.iter().flat_map(|v| v.to_be_bytes()).collect(),
        )
    }
}
pub fn table(records: &[Record]) -> Vec<u8> {
    let mut out = vec![0; 6 + records.len() * 12];
    let offset = out.len();
    font::u16_at(&mut out, 2, records.len() as u16);
    font::u16_at(&mut out, 4, offset as u16);
    for (i, r) in records.iter().enumerate() {
        let at = 6 + i * 12;
        let values = [
            r.platform,
            r.encoding,
            r.language,
            r.id,
            r.bytes.len() as u16,
            (out.len() - offset) as u16,
        ];
        for (j, v) in values.into_iter().enumerate() {
            font::u16_at(&mut out, at + j * 2, v);
        }
        out.extend(&r.bytes);
    }
    out
}
pub fn sfnt(records: &[Record]) -> Vec<u8> {
    font::sfnt(&[(*b"name", table(records))], 0)
}
