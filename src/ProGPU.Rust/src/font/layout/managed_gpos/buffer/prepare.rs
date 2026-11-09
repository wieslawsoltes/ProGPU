//! Original GlyphPositionBuffer metric/default-ignorable construction.
use super::*;
use crate::{
    font::{ManagedFont, layout::ManagedGlyphBuffer},
    text::preparation::{ManagedBufferFlags as F, ManagedClusterLevel, PreparationError},
};
impl ManagedPositionBuffer {
    /// Initialize original selected-font metrics and default-ignorable policy
    /// from a live prepared/substituted generation. This consumer currently uses
    /// managed TrueType outlines for visibility, bounds and vertical fallback.
    pub fn from_true_type_substitutions(
        substitutions: &ManagedGlyphBuffer,
        direction: ManagedPositionDirection,
        zero_marks_early: bool,
    ) -> Result<Self, PreparationError> {
        let source = substitutions
            .preparation
            .as_ref()
            .ok_or(PreparationError::MissingPreparation)?;
        let font = source.font();
        let options = source.options();
        let flags = options.buffer_flags;
        if !font.metadata().has_true_type_outlines {
            return Err(FontError::InvalidFace.into());
        }
        let preserve = flags.contains(F::PRESERVE_DEFAULT_IGNORABLES);
        let remove = flags.contains(F::REMOVE_DEFAULT_IGNORABLES);
        let invisible = if preserve || remove {
            0
        } else {
            font.glyph_index(32)?
        };
        let visible = if preserve {
            let mut selected = 0;
            for cp in [0x25a1, 0x25cc, 0xfffd, 0x3f] {
                let glyph = font.glyph_index(cp)?;
                if glyph != 0 && visible_outline(font, glyph)? {
                    selected = glyph;
                    break;
                }
            }
            selected
        } else {
            0
        };
        let input = substitutions.glyphs();
        let mut glyphs = Vec::new();
        glyphs
            .try_reserve_exact(input.len())
            .map_err(|_| FontError::Capacity)?;
        let mut out =
            Self::with_mark_profile(font.source().clone(), glyphs, direction, options.marks)?;
        let vertical = matches!(
            direction,
            ManagedPositionDirection::TopToBottom | ManagedPositionDirection::BottomToTop
        );
        let backward = matches!(
            direction,
            ManagedPositionDirection::RightToLeft | ManagedPositionDirection::BottomToTop
        );
        let mut forward_source = i32::MIN;
        let mut forward_merged = i32::MAX;
        let em = f32::from(font.metadata().units_per_em);
        for (index, &original) in input.iter().enumerate() {
            let mut g = original;
            let ignorable = default_ignorable(g.code_point);
            if ignorable && g.substituted == 0 && invisible == 0 && !preserve {
                let cluster = g.cluster;
                if input.get(index + 1).is_some_and(|n| cluster == n.cluster) {
                    continue;
                }
                if backward && index + 1 < input.len() {
                    forward_source = input[index + 1].cluster;
                    forward_merged = cluster.min(forward_source);
                } else if let Some(last) = out.glyphs.last() {
                    let old = last.cluster;
                    if cluster < old {
                        for prev in out.glyphs.iter_mut().rev().take_while(|p| p.cluster == old) {
                            prev.cluster = cluster;
                        }
                    }
                } else if index + 1 < input.len() {
                    forward_source = input[index + 1].cluster;
                    forward_merged = cluster.min(forward_source);
                }
                continue;
            }
            if g.cluster == forward_source {
                g.cluster = forward_merged;
            } else if forward_source != i32::MIN {
                forward_source = i32::MIN;
            }
            if ignorable && g.substituted == 0 && !preserve {
                g.glyph_index = invisible;
            } else if ignorable
                && g.substituted == 0
                && preserve
                && !visible_outline(font, g.glyph_index)?
            {
                g.glyph_index = visible;
            }
            if vertical {
                g.advance_y =
                    checked_short(-font.advance_height(g.glyph_index, em)?.round_ties_even())?;
                g.offset_x = clamp(
                    (font.advance_width(g.glyph_index, em)?.round_ties_even() as i32 / 2)
                        .wrapping_neg(),
                );
                g.offset_y = checked_short(
                    -font
                        .true_type_vertical_origin(g.glyph_index, em)?
                        .round_ties_even(),
                )?;
            } else {
                g.advance_x = checked_short(font.advance_width(g.glyph_index, em)?.round())?;
            }
            space_fallback(font, vertical, &mut g)?;
            out.glyphs.push(g);
            let i = out.glyphs.len() - 1;
            if zero_marks_early && out.glyph_class(i)? == 3 {
                if font.source().table(Tag::new(*b"GPOS")).is_none()
                    && matches!(
                        direction,
                        ManagedPositionDirection::LeftToRight
                            | ManagedPositionDirection::TopToBottom
                    )
                {
                    g.offset_x = add(g.offset_x, -i32::from(g.advance_x));
                    g.offset_y = add(g.offset_y, -i32::from(g.advance_y));
                }
                g.advance_x = 0;
                g.advance_y = 0;
            }
            if ignorable && g.substituted == 0 && !preserve {
                g.advance_x = 0;
                g.advance_y = 0;
            }
            out.glyphs[i] = g;
        }
        if options.cluster_level == ManagedClusterLevel::MonotoneCharacters {
            let mut changed = false;
            for g in &mut out.glyphs {
                if g.has_character_cluster != 0 {
                    g.cluster = g.character_cluster;
                    changed = true;
                }
            }
            if changed {
                for i in 1..out.glyphs.len() {
                    let c = out.glyphs[i].cluster;
                    for g in out.glyphs[..i]
                        .iter_mut()
                        .rev()
                        .take_while(|g| g.cluster > c)
                    {
                        g.cluster = c;
                    }
                }
            }
        }
        Ok(out)
    }
}
fn visible_outline(font: &ManagedFont, glyph: u16) -> Result<bool, FontError> {
    Ok(font.true_type_bounds(glyph)?.is_some() && font.true_type_outline(glyph)?.is_some())
}
fn checked_short(value: f32) -> Result<i16, FontError> {
    if value.is_nan() {
        return Err(FontError::InvalidGlyph);
    }
    Ok(value.clamp(f32::from(i16::MIN), f32::from(i16::MAX)) as i16)
}
fn space_fallback(
    font: &ManagedFont,
    vertical: bool,
    g: &mut ManagedShapingGlyph,
) -> Result<(), FontError> {
    let sign = if vertical { -1 } else { 1 };
    let units = i32::from(font.metadata().units_per_em);
    let em = units as f32;
    let mut advance = i32::from(if vertical { g.advance_y } else { g.advance_x });
    let metric = |glyph| -> Result<i32, FontError> {
        Ok(if vertical {
            (font.advance_height(glyph, em)?.round_ties_even() as i32).wrapping_neg()
        } else {
            font.advance_width(glyph, em)?.round_ties_even() as i32
        })
    };
    match g.space_fallback {
        divisor @ (1..=6 | 16) => {
            let divisor = i32::from(divisor);
            advance = sign * ((units + divisor / 2) / divisor);
        }
        17 => advance = (i64::from(sign) * (i64::from(units) * 4 / 18)) as i32,
        19 => {
            for cp in 0x30..=0x39 {
                let glyph = font.glyph_index(cp)?;
                if glyph != 0 {
                    advance = metric(glyph)?;
                    break;
                }
            }
        }
        20 => {
            let mut glyph = font.glyph_index(0x2e)?;
            if glyph == 0 {
                glyph = font.glyph_index(0x2c)?;
            }
            if glyph != 0 {
                advance = metric(glyph)?;
            }
        }
        21 => advance /= 2,
        _ => {}
    }
    if vertical {
        g.advance_y = clamp(advance);
    } else {
        g.advance_x = clamp(advance);
    }
    Ok(())
}
