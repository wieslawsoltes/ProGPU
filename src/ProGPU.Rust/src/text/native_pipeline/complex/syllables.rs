//! Native capacity/index/serial wrapper over the existing independent category
//! grammars. No managed glyph or font object enters this boundary.
use super::*;
#[path = "../../script_actions/indic_syllables.rs"]
mod indic;
#[path = "../../script_actions/khmer_syllables.rs"]
mod khmer;
#[path = "../../script_actions/myanmar_syllables.rs"]
mod myanmar;
#[path = "../../script_actions/use_syllables.rs"]
mod use_machine;
pub(super) fn assign(
    kind: NativeComplexKind,
    categories: &[u8],
    indices: &[u32],
    output: &mut [u8],
) -> Result<(), FontError> {
    if output.len() < categories.len() {
        return Err(FontError::Capacity);
    }
    let count = if indices.is_empty() {
        categories.len()
    } else {
        if indices.len() < 2
            || indices.last().copied() != Some(categories.len() as u32)
            || indices.windows(2).any(|v| v[0] >= v[1])
        {
            return Err(FontError::InvalidArgument);
        }
        indices.len() - 1
    };
    let mapped = |index: usize| {
        if indices.is_empty() {
            index
        } else {
            indices[index] as usize
        }
    };
    let at = |index| categories[mapped(index)];
    output[..categories.len()].fill(0);
    let mut indic = indic::Scanner::default();
    let mut use_machine = use_machine::Scanner::default();
    let mut khmer = khmer::Scanner::default();
    let mut start = 0;
    let mut serial = 1;
    while start < count {
        let (end, family) = match kind {
            NativeComplexKind::Indic => indic.next(start, count, at),
            NativeComplexKind::Use => use_machine.next(start, count, at),
            NativeComplexKind::Myanmar => myanmar::next(start, count, at),
            NativeComplexKind::Khmer => khmer.next(start, count, at),
            NativeComplexKind::None => return Err(FontError::InvalidArgument),
        };
        output[mapped(start)..mapped(end)].fill((serial << 4) | family);
        serial = if serial == 15 { 1 } else { serial + 1 };
        start = end;
    }
    Ok(())
}
