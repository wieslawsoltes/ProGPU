//! Original native fixed-point style-axis resolution with whole-output preflight.
use super::NativeFontProviderSlant;
use crate::font::{FontError, SfntFace, Tag, VariationAxis};

/// Original catalog/variation request; independent of provider's raw u16/u8 style.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub struct NativeFontStyleRequest {
    pub weight: i32,
    pub width: i32,
    pub slant: NativeFontProviderSlant,
    /// Signed 16.16 optical size; nonpositive keeps the face's original default.
    pub optical_size_fixed: i32,
}
impl Default for NativeFontStyleRequest {
    fn default() -> Self {
        Self {
            weight: 400,
            width: 5,
            slant: NativeFontProviderSlant::Normal,
            optical_size_fixed: 0,
        }
    }
}
impl NativeFontStyleRequest {
    /// Source unspecified-base behavior and independent original clamps.
    pub fn normalized(self) -> Self {
        let unspecified =
            self.weight == 0 && self.width == 0 && self.slant == NativeFontProviderSlant::Normal;
        Self {
            weight: if unspecified {
                400
            } else {
                self.weight.clamp(1, 1000)
            },
            width: if unspecified {
                5
            } else {
                self.width.clamp(1, 9)
            },
            optical_size_fixed: self.optical_size_fixed.max(0),
            ..self
        }
    }
}
/// Original setting; raw user coordinate stays unclamped even though normalization
/// uses the axis range. Recognized duplicate tags remain separate source slots.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeFontStyleVariation {
    pub tag: Tag,
    pub user_fixed: i32,
    pub normalized: i16,
    pub axis_index: u16,
}
fn selected(axis: VariationAxis, request: NativeFontStyleRequest) -> Option<i32> {
    Some(match axis.tag.bytes() {
        [b'w', b'g', b'h', b't'] => request.weight << 16,
        [b'w', b'd', b't', b'h'] => match request.width {
            1 => 50 << 16,
            2 => (62 << 16) + (1 << 15),
            3 => 75 << 16,
            4 => (87 << 16) + (1 << 15),
            5 => 100 << 16,
            6 => (112 << 16) + (1 << 15),
            7 => 125 << 16,
            8 => 150 << 16,
            _ => 200 << 16,
        },
        [b'i', b't', b'a', b'l'] => {
            if request.slant == NativeFontProviderSlant::Normal {
                axis.minimum_fixed
            } else {
                axis.maximum_fixed
            }
        }
        [b's', b'l', b'n', b't'] => {
            if request.slant == NativeFontProviderSlant::Normal {
                0.clamp(axis.minimum_fixed, axis.maximum_fixed)
            } else if i64::from(axis.minimum_fixed).abs() >= i64::from(axis.maximum_fixed).abs() {
                axis.minimum_fixed
            } else {
                axis.maximum_fixed
            }
        }
        [b'o', b'p', b's', b'z'] if request.optical_size_fixed > 0 => request.optical_size_fixed,
        _ => return None,
    })
}
impl SfntFace {
    /// Inspect all original axes and every selected legacy normalization before
    /// output publication. Unknown axes still receive original endpoint validation.
    pub fn native_style_variation_count(
        &self,
        request: NativeFontStyleRequest,
    ) -> Result<u16, FontError> {
        let request = request.normalized();
        let mut count = 0u16;
        for index in 0..self.variation_axis_count()? {
            let axis = self.variation_axis(index)?;
            if let Some(value) = selected(axis, request) {
                self.normalize_variation_coordinate(index, value)?;
                count = count.checked_add(1).ok_or(FontError::InvalidFace)?;
            }
        }
        Ok(count)
    }
    /// Copy exact source-order settings after whole-table and output-capacity
    /// admission. Missing fvar writes zero settings; unused output slots survive.
    pub fn native_style_variations_into(
        &self,
        request: NativeFontStyleRequest,
        output: &mut [NativeFontStyleVariation],
    ) -> Result<u16, FontError> {
        let count = self.native_style_variation_count(request)?;
        if output.len() < usize::from(count) {
            return Err(FontError::Capacity);
        }
        let request = request.normalized();
        let mut written = 0usize;
        for index in 0..self.variation_axis_count()? {
            let axis = self.variation_axis(index)?;
            if let Some(user_fixed) = selected(axis, request) {
                output[written] = NativeFontStyleVariation {
                    tag: axis.tag,
                    user_fixed,
                    normalized: self.normalize_variation_coordinate(index, user_fixed)?,
                    axis_index: index,
                };
                written += 1;
            }
        }
        Ok(count)
    }
}
