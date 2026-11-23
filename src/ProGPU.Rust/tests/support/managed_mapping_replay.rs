//! Transport of authored requests and complete actual managed mapping results.
use progpu::font::{FontError, ManagedFont};
#[derive(Clone)]
pub struct Case {
    pub font: Vec<u8>,
    pub queries: Vec<[u32; 3]>,
}
pub fn emit(out: &mut Vec<u8>, value: u32) {
    out.extend(value.to_le_bytes());
}
impl Case {
    #[allow(dead_code)]
    pub fn write(&self, out: &mut Vec<u8>) {
        emit(out, self.font.len() as u32);
        out.extend(&self.font);
        emit(out, self.queries.len() as u32);
        for q in &self.queries {
            for &v in q {
                emit(out, v);
            }
        }
    }
    #[allow(dead_code)] // Baseline uses the same execute path on a retained font.
    pub fn expected(&self) -> Vec<u8> {
        let mut out = Vec::new();
        let font = ManagedFont::new(self.font.clone(), 0);
        emit(&mut out, font.as_ref().err().map_or(0, |e| *e as u32));
        let Ok(font) = font else {
            return out;
        };
        let mut results = vec![[0; 3]; self.queries.len()];
        execute(&font, &self.queries, &mut results);
        for r in results {
            for v in r {
                emit(&mut out, v);
            }
        }
        out
    }
}
pub fn execute(font: &ManagedFont, queries: &[[u32; 3]], results: &mut [[u32; 3]]) {
    assert_eq!(queries.len(), results.len());
    for (&[kind, code, selector], out) in queries.iter().zip(results) {
        let result: Result<(bool, u16), FontError> = match kind {
            0 => font
                .character_map()
                .glyph_index(code)
                .map(|g| (g.is_some(), g.unwrap_or(0))),
            1 => font.glyph_index(code).map(|g| (true, g)),
            2 => font
                .variation_glyph(code, selector)
                .map(|g| (g.is_some(), g.unwrap_or(0))),
            3 => font.has_glyph(code).map(|b| (b, 0)),
            4 => Ok((font.character_map().uses_symbol_character_map(), 0)),
            _ => Err(FontError::InvalidArgument),
        };
        *out = match result {
            Ok((found, glyph)) => [0, u32::from(found), u32::from(glyph)],
            Err(e) => [e as u32, 0, 0],
        };
    }
}
