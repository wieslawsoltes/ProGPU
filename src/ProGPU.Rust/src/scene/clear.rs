//! Exact original straight-color target-storage commands, independent of paint.
use super::*;

/// Actual target alpha contract; never inferred from a clear color or ancestor.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum TargetAlpha {
    /// Retain the source clear alpha and premultiply RGB once.
    #[default]
    Preserve,
    /// The actual target ignores alpha: write alpha one and original RGB.
    Ignore,
}

/// Validated original straight RGBA. Finite HDR/negative RGB and signed zero
/// remain unchanged; source alpha must be in 0..=1.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ClearColor([f32; 4]);
impl ClearColor {
    /// Validate before recording, retaining every original component bit.
    pub fn new(color: [f32; 4]) -> Result<Self, Error> {
        if !color.iter().all(|v| v.is_finite()) || !(0.0..=1.0).contains(&color[3]) {
            return Err(Error::InvalidInput);
        }
        Ok(Self(color))
    }
    /// Original straight components, including signed zero and unclamped RGB.
    pub fn rgba(self) -> [f32; 4] {
        self.0
    }
    /// Original independent RGB multiplication under an explicit actual target
    /// policy. There is no quantization, color clamp or inherited draw opacity.
    pub fn premultiplied(self, target: TargetAlpha) -> [f32; 4] {
        let [r, g, b, a] = self.0;
        let alpha = if target == TargetAlpha::Ignore {
            1.0
        } else {
            a
        };
        let mut result = (wide::f32x4::from([r, g, b, 1.0]) * wide::f32x4::splat(alpha)).to_array();
        result[3] = alpha;
        result
    }
}
impl Scene {
    /// Replace the actual current target inside its binary clip in painter
    /// order. Transparent clear erases storage. No geometry, brush, transform,
    /// source hit primitive or per-draw opacity participates.
    ///
    /// Direct mask scopes reject during complete compilation, including when
    /// their clip is empty. An ordinary isolated layer owns its clear target;
    /// final layer opacity/masking remains independent. Source AA background
    /// initialization is a separate compositor policy, not inferred here.
    pub fn clear_target(&mut self, color: [f32; 4]) -> Result<(), Error> {
        let color = ClearColor::new(color)?;
        self.commands.try_reserve(1).map_err(|_| Error::Capacity)?;
        self.commands.push(Command::Clear(color));
        Ok(())
    }
    /// Original direct clear commands in source order, without traversing child
    /// targets or manufacturing geometry/input records.
    pub fn target_clears(&self) -> impl Iterator<Item = ClearColor> + '_ {
        self.commands.iter().filter_map(|command| match command {
            Command::Clear(color) => Some(*color),
            _ => None,
        })
    }
}
impl CompiledScene {
    /// Original compiled storage colors, independent of geometry and material
    /// records. Actual target clipping is resolved by GPU preparation.
    pub fn target_clears(&self) -> impl Iterator<Item = ClearColor> + '_ {
        self.draws.iter().filter_map(|draw| match draw {
            DrawRange::Clear(color) => Some(*color),
            _ => None,
        })
    }
}
