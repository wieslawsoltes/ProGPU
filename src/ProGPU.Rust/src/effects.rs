//! Original bounded native Gaussian, box and drop-shadow group-effect chains.
//! Descriptors are immutable and CPU planning never initializes WebGPU. Effects
//! consume premultiplied captures; mask, opacity and final blending are separate.
use crate::Error;
use bytemuck::{Pod, Zeroable};
use std::sync::Arc;
use wide::f32x4;

pub use crate::path::{ExecutionPath, ExecutionPreference};
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
pub use gpu::{EffectEngine, EffectMetrics, EffectOutput, PreparedEffects};

/// Original native maximum number of ordered effects.
pub const MAX_EFFECTS: usize = 8;

/// Logical original group-effect parameters. Drop-shadow colors are straight
/// linear RGBA; input/output images remain premultiplied throughout execution.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Effect {
    /// Independent Gaussian standard deviations in logical pixels.
    GaussianBlur {
        /// Original logical X/Y standard deviations.
        sigma: [f32; 2],
    },
    /// Independent logical box radii, floored only at the physical target.
    BoxBlur {
        /// Original logical X/Y box radii.
        radius: [f32; 2],
    },
    /// Blur source alpha, offset/tint it, then place the original source over it.
    DropShadow {
        /// Original logical X/Y standard deviations, independently allowing zero.
        sigma: [f32; 2],
        /// Original logical X/Y shadow translation.
        offset: [f32; 2],
        /// Original straight linear RGBA in 0..=1.
        color: [f32; 4],
    },
}
impl Effect {
    fn values(self) -> ([f32; 2], [f32; 2]) {
        match self {
            Self::GaussianBlur { sigma } => (sigma, [0.0; 2]),
            Self::BoxBlur { radius } => (radius, [0.0; 2]),
            Self::DropShadow { sigma, offset, .. } => (sigma, offset),
        }
    }
    /// Native logical admission. DPI-dependent radius limits are checked when
    /// planning; zero shadow sigma is permitted independently on either axis.
    pub fn validate(self) -> Result<(), Error> {
        let (values, offset) = self.values();
        let shadow = matches!(self, Self::DropShadow { .. });
        if values
            .iter()
            .any(|v| !v.is_finite() || *v < 0.0 || (!shadow && *v <= 0.01))
            || offset.iter().any(|v| !v.is_finite())
        {
            return Err(Error::InvalidInput);
        }
        if let Self::DropShadow { color, .. } = self
            && color
                .iter()
                .any(|v| !v.is_finite() || !(0.0..=1.0).contains(v))
        {
            return Err(Error::InvalidInput);
        }
        Ok(())
    }
}

/// Owned original effect order; construction validates the complete inventory.
#[derive(Clone, Debug)]
pub struct EffectChain(Arc<[Effect]>);
impl EffectChain {
    /// Retain one through eight complete original effect descriptors.
    pub fn new(effects: impl Into<Arc<[Effect]>>) -> Result<Self, Error> {
        let effects = effects.into();
        if effects.is_empty() || effects.len() > MAX_EFFECTS {
            return Err(Error::InvalidInput);
        }
        for &effect in effects.iter() {
            effect.validate()?;
        }
        Ok(Self(effects))
    }
    /// Original logical descriptors in source order.
    pub fn effects(&self) -> &[Effect] {
        &self.0
    }

    /// Resolve independent physical X/Y DPI and the original three-texture
    /// ping-pong schedule. Planning is bounded O(E), with at most eight nodes.
    pub fn plan(&self, dpi: [f32; 2]) -> Result<EffectPlan, Error> {
        if dpi.iter().any(|v| !v.is_finite() || *v <= 0.0) {
            return Err(Error::InvalidInput);
        }
        let mut steps = Vec::new();
        steps
            .try_reserve_exact(self.0.len())
            .map_err(|_| Error::Capacity)?;
        let mut source = EffectSource::Input;
        for &effect in self.0.iter() {
            let (value, offset) = effect.values();
            // Original semantic presentation multiply/add lanes; retain the
            // independently resolved axes, including signed-zero behavior.
            let mapped = (f32x4::from([value[0], value[1], offset[0], offset[1]])
                * f32x4::from([dpi[0], dpi[1], dpi[0], dpi[1]])
                + f32x4::ZERO)
                .to_array();
            let is_box = matches!(effect, Effect::BoxBlur { .. });
            let maximum = if is_box { 128.0 } else { 128.0 / 3.0 };
            if mapped.iter().any(|v| !v.is_finite()) || mapped[0] > maximum || mapped[1] > maximum {
                return Err(Error::InvalidInput);
            }
            let blur = |value: f32| {
                if is_box {
                    BlurUniforms {
                        radius: value.floor().clamp(0.0, 128.0) as u32,
                        kernel_type: 1,
                        ..Zeroable::zeroed()
                    }
                } else {
                    BlurUniforms {
                        sigma: value,
                        radius: (value * 3.0).ceil().clamp(0.0, 128.0) as u32,
                        ..Zeroable::zeroed()
                    }
                }
            };
            let horizontal = (0..3)
                .find(|&slot| source != EffectSource::Intermediate(slot))
                .ok_or(Error::InvalidInput)?;
            let shadow = if let Effect::DropShadow { color, .. } = effect {
                Some(ShadowUniforms {
                    offset: [mapped[2], mapped[3]],
                    padding: [0.0; 2],
                    color,
                })
            } else {
                None
            };
            let vertical = if shadow.is_some() {
                (0..3)
                    .find(|&slot| slot != horizontal && source != EffectSource::Intermediate(slot))
                    .ok_or(Error::InvalidInput)?
            } else {
                match source {
                    EffectSource::Intermediate(slot) => slot,
                    EffectSource::Input => u8::from(horizontal == 0),
                }
            };
            let output = if shadow.is_some() {
                horizontal
            } else {
                vertical
            };
            steps.push(EffectStep {
                source,
                horizontal,
                vertical,
                output,
                blur: [blur(mapped[0]), blur(mapped[1])],
                shadow,
            });
            source = EffectSource::Intermediate(output);
        }
        Ok(EffectPlan { steps, dpi })
    }
}

/// Original source selector in the bounded effect schedule.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EffectSource {
    /// The actual isolated source capture, never overwritten by effects.
    Input,
    /// One of three original intermediates, indexed 0..3.
    Intermediate(u8),
}
/// Exact original 16-byte blur record.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct BlurUniforms {
    /// Physical Gaussian sigma; zero for box kernels.
    pub sigma: f32,
    /// Original clamped physical radius (Gaussian ceil(3*sigma), box floor).
    pub radius: u32,
    /// Zero for Gaussian, one for box.
    pub kernel_type: u32,
    /// Original zero padding.
    pub padding: u32,
}
/// Exact original 32-byte shadow record.
#[repr(C)]
#[derive(Clone, Copy, Debug, Pod, Zeroable)]
pub struct ShadowUniforms {
    /// Independent physical shadow offsets.
    pub offset: [f32; 2],
    /// Original zero padding.
    pub padding: [f32; 2],
    /// Original straight linear RGBA color.
    pub color: [f32; 4],
}
const _: () = {
    assert!(size_of::<BlurUniforms>() == 16);
    assert!(size_of::<ShadowUniforms>() == 32);
};

/// One immutable ordered effect node. Both blur passes always remain present,
/// including radius-zero copies and asymmetric zero-sigma shadows.
#[derive(Clone, Debug)]
pub struct EffectStep {
    source: EffectSource,
    horizontal: u8,
    vertical: u8,
    output: u8,
    blur: [BlurUniforms; 2],
    shadow: Option<ShadowUniforms>,
}
impl EffectStep {
    /// Original input texture selector.
    pub fn source(&self) -> EffectSource {
        self.source
    }
    /// Horizontal, vertical and final output intermediate indices.
    pub fn texture_slots(&self) -> [u8; 3] {
        [self.horizontal, self.vertical, self.output]
    }
    /// Original horizontal and vertical parameter records.
    pub fn blur_uniforms(&self) -> &[BlurUniforms; 2] {
        &self.blur
    }
    /// Optional original shadow composition parameters.
    pub fn shadow_uniforms(&self) -> Option<&ShadowUniforms> {
        self.shadow.as_ref()
    }
}
/// Frozen physical schedule, independently inspectable in CPU-only builds.
#[derive(Clone, Debug)]
pub struct EffectPlan {
    steps: Vec<EffectStep>,
    dpi: [f32; 2],
}
impl EffectPlan {
    /// Complete original node order and texture dependencies.
    pub fn steps(&self) -> &[EffectStep] {
        &self.steps
    }
    /// Actual independent presentation DPI used for every node.
    pub fn dpi(&self) -> [f32; 2] {
        self.dpi
    }
    /// Original separate 16x16 compute passes; no node fusion or pass deletion.
    pub fn pass_count(&self) -> u32 {
        self.steps
            .iter()
            .map(|s| 2 + u32::from(s.shadow.is_some()))
            .sum()
    }
    /// Actual number of referenced intermediate textures, at most three.
    pub fn texture_count(&self) -> usize {
        self.steps
            .iter()
            .flat_map(|s| s.texture_slots())
            .max()
            .map_or(0, |v| usize::from(v) + 1)
    }
    /// Exact RGBA8 intermediate allocation budget for a positive physical
    /// extent. Device dimension limits remain a separate execution check.
    pub fn required_texture_bytes(&self, size: [u32; 2]) -> Result<u64, Error> {
        if size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        u64::from(size[0])
            .checked_mul(u64::from(size[1]))
            .and_then(|bytes| bytes.checked_mul(4 * self.texture_count() as u64))
            .ok_or(Error::Capacity)
    }
}
