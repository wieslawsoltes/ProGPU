//! Original managed GDI ternary raster-operation recording metadata.
use crate::{Error, image::ImageSource};

/// Original pattern operand. Colors are straight normalized values; texture
/// tiles retain actual source owners and repeat in physical device pixels.
#[derive(Clone, Debug)]
pub enum RasterPattern {
    /// One normalized straight RGBA color.
    Solid([f32; 4]),
    /// Immutable original 8-by-8 mask, least-significant bit at the upper left.
    Tile {
        /// Original row-major 64-bit mask.
        bits: u64,
        /// Straight normalized foreground RGBA.
        foreground: [f32; 4],
        /// Straight normalized background; zero alpha preserves destination.
        background: [f32; 4],
        /// Original logical brush origin, scaled once at the physical target.
        origin: [f32; 2],
    },
    /// Actual immutable pixel owner or same-device texture tile. GPU pixels stay
    /// live through replay; no extraction or reinterpretation of alpha occurs.
    Texture {
        /// Actual source tile, read by integer textureLoad in the original shader.
        image: ImageSource,
        /// Original logical brush origin, including negative phase.
        origin: [f32; 2],
    },
}
/// Original eight-bit ternary truth table in pattern/source/destination order.
/// Enabled state is represented by the presence of this typed command, keeping
/// code zero (BLACKNESS) distinct from an absent operation.
#[derive(Clone, Debug)]
pub struct RasterOperation {
    /// Every byte is a valid original ROP3 truth table.
    pub code: u8,
    /// Original independently owned pattern input.
    pub pattern: RasterPattern,
}
impl RasterOperation {
    /// Retain a normalized solid pattern after original metadata admission.
    pub fn solid(code: u8, color: [f32; 4]) -> Result<Self, Error> {
        let value = Self {
            code,
            pattern: RasterPattern::Solid(color),
        };
        value.validate()?;
        Ok(value)
    }
    /// Validate complete recording metadata without GPU allocation or callbacks.
    pub fn validate(&self) -> Result<(), Error> {
        let color = |c: [f32; 4]| c.iter().all(|x| x.is_finite() && (0.0..=1.0).contains(x));
        let valid = match &self.pattern {
            RasterPattern::Solid(c) => color(*c),
            RasterPattern::Tile {
                foreground,
                background,
                origin,
                ..
            } => color(*foreground) && color(*background) && origin.iter().all(|x| x.is_finite()),
            RasterPattern::Texture { image, origin } => {
                !image.size().contains(&0) && origin.iter().all(|x| x.is_finite())
            }
        };
        if valid {
            Ok(())
        } else {
            Err(Error::InvalidInput)
        }
    }
}
