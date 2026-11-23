//! Original prohibited-vowel insertion; dependent visits retain source order.
//! Matching is O(log R) for R fixed specification rows. Repeated insertions keep
//! original O(N²) worst-case movement with O(1) scratch beyond the owned buffer.
use super::{ManagedGlyphBuffer, ManagedShapingGlyph, insert};
use crate::{font::ManagedFont, text::preparation::PreparationError};
#[path = "vowel_data.rs"]
mod data;

fn match_length(script: &str, first: u32, second: u32, third: u32) -> usize {
    let Ok(tag) = <[u8; 4]>::try_from(script.as_bytes()) else {
        return 0;
    };
    let tag = u32::from_be_bytes(tag);
    // Original pairs shadow triples sharing their prefix. The complete public
    // inventory and raw third-scalar observations independently prove this rule.
    if data::CONSTRAINTS
        .binary_search(&(tag, first, second, 0))
        .is_ok()
    {
        2
    } else if third != 0
        && data::CONSTRAINTS
            .binary_search(&(tag, first, second, third))
            .is_ok()
    {
        3
    } else {
        0
    }
}
pub(super) fn apply(
    buffer: &mut ManagedGlyphBuffer,
    font: &ManagedFont,
    script: &str,
) -> Result<(), PreparationError> {
    let mut index = 0;
    while index + 1 < buffer.glyphs.len() {
        let third = buffer.glyphs.get(index + 2).map_or(0, |g| g.code_point);
        let count = match_length(
            script,
            buffer.glyphs[index].code_point,
            buffer.glyphs[index + 1].code_point,
            third,
        );
        if count == 0 {
            index += 1;
            continue;
        }
        let final_index = index + count - 1;
        let glyph = ManagedShapingGlyph::new(
            font.glyph_index(0x25cc)?,
            buffer.glyphs[final_index].cluster,
            0x25cc,
        );
        insert(&mut buffer.glyphs, final_index, glyph)?;
        index += count + 1;
    }
    Ok(())
}
