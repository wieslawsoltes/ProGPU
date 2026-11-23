//! Original ordered attachment resolution with separate design/device overflow policy.
use super::*;

/// Resolve caller-owned native attachments after all GPOS lookups. Design-unit
/// execution clamps each ordered addition; device execution checks every addition
/// before publishing that glyph. Scratch bytes beyond the live glyph count remain
/// unchanged. Earlier resolved glyphs survive a later cycle/depth/overflow failure.
pub fn resolve_gpos_attachments(
    glyphs: &mut [ShapingGlyph],
    attachments: &[GposAttachment],
    direction: GposDirection,
    scratch: &mut [u8],
    device: bool,
) -> Result<(), FontError> {
    if attachments.len() != glyphs.len() || device && direction == GposDirection::Unspecified {
        return Err(FontError::InvalidArgument);
    }
    if scratch.len() < glyphs.len() {
        return Err(FontError::Capacity);
    }
    scratch[..glyphs.len()].fill(0);
    for index in 0..glyphs.len() {
        resolve(index, glyphs, attachments, direction, scratch, 0, device)?;
    }
    Ok(())
}
fn resolve(
    index: usize,
    glyphs: &mut [ShapingGlyph],
    attachments: &[GposAttachment],
    direction: GposDirection,
    states: &mut [u8],
    depth: u32,
    strict: bool,
) -> Result<(), FontError> {
    if states[index] == 2 {
        return Ok(());
    }
    require(states[index] != 1 && depth < 64)?;
    states[index] = 1;
    let attachment = attachments[index];
    if attachment.target >= 0 && (attachment.target as usize) < glyphs.len() {
        let target = attachment.target as usize;
        resolve(
            target,
            glyphs,
            attachments,
            direction,
            states,
            depth + 1,
            strict,
        )?;
        let mut candidate = glyphs[index];
        let parent = glyphs[target];
        let add = |value: &mut i32, delta: i64| -> Result<(), FontError> {
            let sum = i64::from(*value) + delta;
            *value = if strict {
                checked(sum)?
            } else {
                sum.clamp(i64::from(i32::MIN), i64::from(i32::MAX)) as i32
            };
            Ok(())
        };
        match attachment.kind {
            GposAttachmentKind::Mark => {
                add(&mut candidate.offset_x, i64::from(parent.offset_x))?;
                add(&mut candidate.offset_y, i64::from(parent.offset_y))?;
                let forward = matches!(
                    direction,
                    GposDirection::LeftToRight | GposDirection::TopToBottom
                );
                let (first, last, sign) = if target < index {
                    (target, index, if forward { -1 } else { 1 })
                } else {
                    (index, target, if forward { 1 } else { -1 })
                };
                let shift = usize::from(!forward);
                for glyph in &glyphs[first + shift..last + shift] {
                    add(&mut candidate.offset_x, sign * i64::from(glyph.advance_x))?;
                    add(&mut candidate.offset_y, sign * i64::from(glyph.advance_y))?;
                }
            }
            GposAttachmentKind::CursiveVertical => {
                add(&mut candidate.offset_x, i64::from(parent.offset_x))?
            }
            GposAttachmentKind::CursiveHorizontal => {
                add(&mut candidate.offset_y, i64::from(parent.offset_y))?
            }
            GposAttachmentKind::None => {}
        }
        glyphs[index] = candidate;
    }
    states[index] = 2;
    Ok(())
}
