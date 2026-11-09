//! Connected original ShapeCore positioning and safe-boundary verification.
use super::*;
use crate::{
    font::layout::{ManagedPositionBuffer, ManagedPositionDirection},
    text::ManagedShapingGlyph,
};

/// Complete candidate failure; partial text/position generations are not exposed.
#[derive(Clone, Copy, Debug, PartialEq, Eq, thiserror::Error)]
#[non_exhaustive]
pub enum ManagedShapingError {
    /// Original source preparation, script or substitution failure.
    #[error(transparent)]
    Preparation(#[from] PreparationError),
    /// Original metric, outline or positioning failure.
    #[error(transparent)]
    Font(#[from] FontError),
    /// Original optional safe-boundary/monotone verification failed.
    #[error("shaping verification failed at glyph {glyph}")]
    Verification {
        /// First affected output glyph slot.
        glyph: usize,
    },
}
/// Fully positioned original managed design-unit records. Positive Y remains
/// the original font-space direction; UI output can negate Y at its boundary.
/// This is reusable CPU text, independent of GPU atlases and paragraph layout.
#[derive(Clone, Debug)]
pub struct ManagedShapedRun {
    source: Arc<ManagedPreparationSource>,
    direction: ManagedShapingDirection,
    glyphs: Vec<ManagedShapingGlyph>,
}
impl ManagedShapedRun {
    /// Original text, selected font, script, profiles and neighboring context.
    pub fn source(&self) -> &ManagedPreparationSource {
        &self.source
    }
    /// Actual resolved physical shaping direction.
    pub fn direction(&self) -> ManagedShapingDirection {
        self.direction
    }
    /// Complete positioned records in final visual order and original font units.
    pub fn glyphs(&self) -> &[ManagedShapingGlyph] {
        &self.glyphs
    }
}
impl ManagedShapingPlan {
    /// Execute original preparation, GSUB, metric construction, GPOS, kerning,
    /// fallback marks, visual ordering, Arabic stretch and optional verification.
    /// This first complete route explicitly requires managed TrueType outlines;
    /// it does not substitute native CFF geometry for the pending managed adapter.
    pub fn shape_true_type(
        &self,
        text: impl Into<Arc<[u16]>>,
        preparation: ManagedPreparationOptions,
    ) -> Result<ManagedShapedRun, ManagedShapingError> {
        use ManagedShapingDirection::*;
        let text = text.into();
        let substitutions = self.substitute(text.clone(), preparation.clone())?;
        let source = substitutions
            .preparation
            .as_ref()
            .ok_or(PreparationError::MissingPreparation)?
            .clone();
        let direction = match self.direction {
            LeftToRight => ManagedPositionDirection::LeftToRight,
            RightToLeft => ManagedPositionDirection::RightToLeft,
            TopToBottom => ManagedPositionDirection::TopToBottom,
            BottomToTop => ManagedPositionDirection::BottomToTop,
        };
        let mut positions = ManagedPositionBuffer::from_true_type_substitutions(
            &substitutions,
            direction,
            self.zero_mark_advances_early(),
        )?;
        let has_gpos = self.positioning.is_some();
        let kern = if let Some(plan) = &self.positioning {
            plan.apply(&mut positions)?
        } else {
            false
        };
        let kern_enabled = self
            .features
            .features()
            .iter()
            .rev()
            .find(|f| f.tag() == b"kern".map(u16::from))
            .is_some_and(|f| f.value() != 0);
        if !kern
            && kern_enabled
            && !self.indic
            && matches!(self.direction, LeftToRight | RightToLeft)
        {
            positions.apply_legacy_kern()?;
        }
        let fallback = self.uses_fallback_mark_positioning();
        if fallback || matches!(self.unicode_script.as_ref(), "thai" | "lao ") {
            positions.zero_mark_advances(
                !has_gpos && matches!(self.direction, LeftToRight | TopToBottom),
            )?;
        }
        positions.resolve_attachments()?;
        if fallback {
            positions.apply_true_type_fallback_marks(&self.font)?;
        }
        if matches!(self.direction, RightToLeft | BottomToTop) {
            positions.reverse();
            if preparation.cluster_level == ManagedClusterLevel::MonotoneCharacters {
                positions.reverse_combining_clusters();
            }
        }
        if self.arabic {
            positions.apply_arabic_stretch(&self.font, self.direction == RightToLeft)?;
        }
        if preparation
            .buffer_flags
            .contains(ManagedBufferFlags::VERIFY)
        {
            self.verify(&text, &preparation, positions.glyphs())?;
        }
        Ok(ManagedShapedRun {
            source,
            direction: self.direction,
            glyphs: positions.into_glyphs(),
        })
    }
    fn verify(
        &self,
        text: &[u16],
        preparation: &ManagedPreparationOptions,
        expected: &[ManagedShapingGlyph],
    ) -> Result<(), ManagedShapingError> {
        use ManagedBufferFlags as F;
        if !matches!(
            preparation.cluster_level,
            ManagedClusterLevel::MonotoneGraphemes | ManagedClusterLevel::MonotoneCharacters
        ) || expected.is_empty()
        {
            return Ok(());
        }
        let forward = matches!(
            self.direction,
            ManagedShapingDirection::LeftToRight | ManagedShapingDirection::TopToBottom
        );
        for i in 1..expected.len() {
            let (a, b) = (expected[i - 1].cluster, expected[i].cluster);
            if a != b && (a < b) != forward {
                return Err(ManagedShapingError::Verification { glyph: i });
            }
        }
        let mut output_start = 0;
        let mut logical_edge = if forward { 0 } else { text.len() as i32 };
        for output_end in 1..=expected.len() {
            let last = output_end == expected.len();
            if !last {
                let left = expected[output_end - 1];
                let right = expected[output_end];
                if left.cluster == right.cluster
                    || (if forward { right.flags } else { left.flags }) & 1 != 0
                {
                    continue;
                }
            }
            let (start, end) = if forward {
                let end = if last {
                    text.len() as i32
                } else {
                    expected[output_end].cluster
                };
                let start = logical_edge;
                logical_edge = end;
                (start, end)
            } else {
                let start = if last {
                    0
                } else {
                    expected[output_end - 1].cluster
                };
                let end = logical_edge;
                logical_edge = start;
                (start, end)
            };
            if start < 0 || end > text.len() as i32 || start >= end {
                return Err(ManagedShapingError::Verification {
                    glyph: output_start,
                });
            }
            let (start, end) = (start as usize, end as usize);
            let mut flags = preparation.buffer_flags.bits() & !F::VERIFY.bits();
            if start != 0 {
                flags &= !F::BEGINNING_OF_TEXT.bits();
            }
            if end != text.len() {
                flags &= !F::END_OF_TEXT.bits();
            }
            let mut fragment_preparation = preparation.clone();
            fragment_preparation.buffer_flags = F::from_bits(flags);
            fragment_preparation.pre_context = joined(&preparation.pre_context, &text[..start])?;
            fragment_preparation.post_context = joined(&text[end..], &preparation.post_context)?;
            let mut options = self.requested.clone();
            options.script = Some(self.unicode_script.clone());
            options.direction = Some(self.direction);
            options.features = options.features.for_fragment(start as u32..end as u32)?;
            let plan = Self::new(self.font.clone(), &text[start..end], options)?;
            let actual = plan.shape_true_type(&text[start..end], fragment_preparation)?;
            if actual.glyphs.len() != output_end - output_start {
                return Err(ManagedShapingError::Verification {
                    glyph: output_start,
                });
            }
            for (i, (a, b)) in expected[output_start..output_end]
                .iter()
                .zip(&actual.glyphs)
                .enumerate()
            {
                if a.glyph_index != b.glyph_index
                    || a.cluster != b.cluster.wrapping_add(start as i32)
                    || a.code_point != b.code_point
                    || a.advance_x != b.advance_x
                    || a.advance_y != b.advance_y
                    || a.offset_x != b.offset_x
                    || a.offset_y != b.offset_y
                {
                    return Err(ManagedShapingError::Verification {
                        glyph: output_start + i,
                    });
                }
            }
            output_start = output_end;
        }
        Ok(())
    }
}
fn joined(a: &[u16], b: &[u16]) -> Result<Arc<[u16]>, FontError> {
    let len = a.len().checked_add(b.len()).ok_or(FontError::Capacity)?;
    let mut out = Vec::new();
    out.try_reserve_exact(len)
        .map_err(|_| FontError::Capacity)?;
    out.extend_from_slice(a);
    out.extend_from_slice(b);
    Ok(out.into())
}
