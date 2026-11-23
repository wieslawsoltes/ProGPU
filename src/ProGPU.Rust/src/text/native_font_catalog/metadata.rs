//! Fixed-work native OS/2/head metadata, distinct from managed defaults.
use super::*;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct NativeFaceStyle {
    pub weight: u16,
    pub width: u16,
    pub italic: bool,
}
impl Default for NativeFaceStyle {
    fn default() -> Self {
        Self {
            weight: 400,
            width: 5,
            italic: false,
        }
    }
}
impl SfntFace {
    /// Original native always-available style defaults and OS/2/head precedence.
    pub fn native_face_style(&self) -> NativeFaceStyle {
        let mut result = NativeFaceStyle::default();
        if let Some(table) = self
            .table(Tag::new(*b"OS/2"))
            .filter(|t| t.bytes.len() >= 64)
        {
            result.weight = word(table.bytes, 4).clamp(1, 1000);
            result.width = word(table.bytes, 6).clamp(1, 9);
            result.italic = word(table.bytes, 62) & 1 != 0;
        }
        if let Some(table) = self
            .table(Tag::new(*b"head"))
            .filter(|t| t.bytes.len() >= 46)
        {
            result.italic |= word(table.bytes, 44) & 2 != 0;
        }
        result
    }
    /// Raw original fsType bits; absence stays distinct from explicit zero.
    pub fn native_embedding_rights(&self) -> Option<u16> {
        self.table(Tag::new(*b"OS/2"))
            .filter(|t| t.bytes.len() >= 10)
            .map(|t| word(t.bytes, 8))
    }
}
