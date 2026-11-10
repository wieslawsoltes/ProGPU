//! Original captured-device 26.6 policies. A real retained source is required;
//! this module does not construct hinted outlines or manufacture provider proof.
use super::*;
use crate::font::layout::{GposArithmetic, GposDeviceFrame, GposDeviceSource};
mod marks;
mod stretch_device;
pub use marks::apply_native_device_fallback_marks;
pub use stretch_device::{NativeDeviceStretchScratch, apply_native_device_arabic_stretch};

/// Original captured unpositioned mark extent in signed device 26.6 units.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NativeDeviceMarkBounds {
    /// Original left bearing.
    pub x_bearing: i32,
    /// Original upper bearing.
    pub y_bearing: i32,
    /// Nonnegative captured width.
    pub width: i32,
    /// Original negative-downward extent, never a positive height.
    pub negative_height: i32,
    /// Actual unpositioned horizontal advance from this generation.
    pub horizontal_advance: i32,
}
/// Original special-space capture inventory selected by first available cmap.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeDeviceSpaceKind {
    /// First source digit U+0030 through U+0039 that exists in the font.
    Figure,
    /// Source full stop, otherwise comma.
    Punctuation,
}
/// Synchronously borrowed actual native capture and its exact GPOS generation.
/// Callbacks read retained metrics only: no font execution, GPU work, managed
/// callbacks or application reentry. Distinct occurrences retain descriptors.
pub trait NativeDeviceSource: GposDeviceSource {
    /// Read one actual unpositioned extent using original current occurrence and
    /// the caller's retained descriptor identity; absence stays separate from zero.
    fn mark_bounds(
        &self,
        index: usize,
        descriptor: u32,
        glyph: u32,
    ) -> Result<Option<NativeDeviceMarkBounds>, FontError>;
    /// Read the captured unpositioned horizontal advance of this stretch occurrence.
    fn stretch_advance(&self, index: usize, descriptor: u32, glyph: u32) -> Result<i32, FontError>;
    /// Read actual captured horizontal/positive vertical advances for the selected
    /// source glyph. This callback is invoked only after original cmap admission.
    fn space_advance(&self, kind: NativeDeviceSpaceKind, glyph: u16)
    -> Result<[i32; 2], FontError>;
}
/// One borrowed captured generation; source implementations may aggregate owners
/// but must retain the exact common font/instance/descriptor generation throughout.
#[derive(Clone, Copy)]
pub struct NativeDeviceFrame<'a> {
    /// Actual retained native metrics and contour-point source.
    pub source: &'a dyn NativeDeviceSource,
    /// Original checked arithmetic route, never silently downgraded.
    pub arithmetic: GposArithmetic,
}
impl NativeDeviceFrame<'_> {
    /// Borrow the identical source for the existing native GPOS executor.
    pub fn gpos(&self) -> GposDeviceFrame<'_> {
        GposDeviceFrame {
            source: self.source,
            arithmetic: self.arithmetic,
        }
    }
    fn validate(
        &self,
        font: &SfntFace,
        coordinates: &[i16],
        direction: GposDirection,
    ) -> Result<(), FontError> {
        if direction == GposDirection::Unspecified {
            return Err(FontError::InvalidArgument);
        }
        self.gpos().validate(GposOptions {
            font: Some(font),
            normalized_coordinates: coordinates,
            direction,
            ..Default::default()
        })
    }
}
/// Apply the original device-space policy, retaining integer design rounding
/// before projection and exact captured digit/punctuation advances.
pub fn apply_native_device_space(
    font: &SfntFace,
    coordinates: &[i16],
    direction: GposDirection,
    glyph: &mut ShapingGlyph,
    frame: NativeDeviceFrame<'_>,
) -> Result<(), FontError> {
    frame.validate(font, coordinates, direction)?;
    let kind = spaces::fallback(glyph.code_point);
    if kind == 0 {
        return Ok(());
    }
    let original = font
        .glyph_index(glyph.code_point)
        .ok_or(FontError::InvalidFace)?;
    let space = font.glyph_index(0x20).ok_or(FontError::InvalidFace)?;
    if original != 0 || space == 0 {
        return Ok(());
    }
    let vertical = matches!(
        direction,
        GposDirection::TopToBottom | GposDirection::BottomToTop
    );
    let mut advance = i64::from(if vertical {
        glyph.advance_y
    } else {
        glyph.advance_x
    });
    if matches!(kind, 1..=6 | 16 | 17) {
        let units = i64::from(
            font.header_metrics()
                .ok_or(FontError::InvalidFace)?
                .units_per_em,
        );
        let divisor = i64::from(kind);
        let design = if kind == 17 {
            units * 4 / 18
        } else {
            (units + divisor / 2) / divisor
        };
        let signed = if vertical { -design } else { design } as i32;
        let vector = if vertical { [0, signed] } else { [signed, 0] };
        let projected = frame
            .source
            .project_design([vector, [0; 2]])
            .map_err(|_| FontError::InvalidArgument)?;
        advance = i64::from(projected[0][usize::from(vertical)]);
    } else if matches!(kind, 19 | 20) {
        let mut candidate = 0;
        if kind == 19 {
            for cp in 0x30..=0x39 {
                candidate = font.glyph_index(cp).ok_or(FontError::InvalidFace)?;
                if candidate != 0 {
                    break;
                }
            }
        } else {
            candidate = font.glyph_index(0x2e).ok_or(FontError::InvalidFace)?;
            if candidate == 0 {
                candidate = font.glyph_index(0x2c).ok_or(FontError::InvalidFace)?;
            }
        }
        if candidate != 0 {
            let metrics = frame
                .source
                .space_advance(
                    if kind == 19 {
                        NativeDeviceSpaceKind::Figure
                    } else {
                        NativeDeviceSpaceKind::Punctuation
                    },
                    candidate,
                )
                .map_err(|_| FontError::InvalidArgument)?;
            advance = if vertical {
                -i64::from(metrics[1])
            } else {
                i64::from(metrics[0])
            };
        }
    } else if kind == 21 {
        advance /= 2;
    }
    let advance = i32::try_from(advance).map_err(|_| FontError::InvalidArgument)?;
    if vertical {
        glyph.advance_y = advance;
    } else {
        glyph.advance_x = advance;
    }
    Ok(())
}
