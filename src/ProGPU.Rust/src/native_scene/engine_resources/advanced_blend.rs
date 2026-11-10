//! Original destination-reading layer resolve. Axis-clip ancestry is supplied by
//! the ordered scope producer, never inferred from a painted bounding envelope.
use super::*;
use crate::native_scene::execution::NativeScissor;
#[cfg(feature = "gpu")]
mod gpu;
#[cfg(feature = "gpu")]
mod root_copy;
#[cfg(feature = "gpu")]
pub use gpu::{
    NativeAdvancedBlendEngine, NativeAdvancedBlendMetrics, NativeAdvancedBlendRequest,
    NativeAdvancedScratchRequest, PreparedNativeAdvancedBlends,
};
#[cfg(feature = "gpu")]
pub use root_copy::PreparedNativeRootCopy;
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeAdvancedBlendMode {
    Normal,
    ReplaceAxisBackground { linear_byte: bool },
    AxisClipComposite,
}
impl NativeAdvancedBlendMode {
    pub fn coverage(self) -> bool {
        self != Self::Normal
    }
    fn operation(self) -> u32 {
        match self {
            Self::Normal => 0,
            Self::ReplaceAxisBackground { linear_byte: false } => 2,
            Self::ReplaceAxisBackground { linear_byte: true } => 3,
            Self::AxisClipComposite => 4,
        }
    }
}
/// Exact pixel bounds obtained from consecutive original source axis clips.
/// The ordered producer stops this chain at the first ordinary group.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeAxisClipBounds {
    pub parent: [f32; 4],
    pub child: [f32; 4],
}
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, bytemuck::Pod, bytemuck::Zeroable)]
pub struct NativeAdvancedBlendUniforms {
    pub source_origin: [f32; 2],
    pub source_extent: [f32; 2],
    pub blend_mode: u32,
    pub operation_kind: u32,
    pub raster_operation_code: u32,
    pub pattern_kind: u32,
    pub pattern_color: [f32; 4],
    pub pattern_background_color: [f32; 4],
    pub pattern_origin: [f32; 2],
    pub pattern_mask_low: u32,
    pub pattern_mask_high: u32,
    pub pattern_flags: u32,
    pub padding: u32,
    pub pattern_texture_extent: [f32; 2],
}
const _: () = {
    assert!(size_of::<NativeAdvancedBlendUniforms>() == 96);
    assert!(std::mem::offset_of!(NativeAdvancedBlendUniforms, operation_kind) == 20);
    assert!(std::mem::offset_of!(NativeAdvancedBlendUniforms, pattern_texture_extent) == 88);
};
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NativeAdvancedBlendSampling {
    source: NativeScissor,
    target: NativeScissor,
    mode: NativeAdvancedBlendMode,
    uniforms: NativeAdvancedBlendUniforms,
}
impl NativeAdvancedBlendSampling {
    pub fn new(
        source: NativeScissor,
        target: NativeScissor,
        blend_mode: u32,
        mode: NativeAdvancedBlendMode,
        axis_bounds: Option<NativeAxisClipBounds>,
    ) -> Result<Self, Error> {
        if blend_mode > BLEND_MODULATE
            || (!mode.coverage()
                && (axis_bounds.is_some()
                    || !((BLEND_MULTIPLY..=BLEND_EXCLUSION).contains(&blend_mode)
                        || (BLEND_OVERLAY..=BLEND_LUMINOSITY).contains(&blend_mode))))
        {
            return Err(Error::InvalidInput);
        }
        let mut uniforms = NativeAdvancedBlendUniforms {
            source_origin: [
                source.x as f32 - target.x as f32,
                source.y as f32 - target.y as f32,
            ],
            source_extent: [source.width as f32, source.height as f32],
            blend_mode,
            operation_kind: mode.operation(),
            ..Default::default()
        };
        if let Some(bounds) = axis_bounds {
            if !bounds
                .parent
                .iter()
                .chain(bounds.child.iter())
                .all(|v| v.is_finite())
            {
                return Err(Error::InvalidInput);
            }
            uniforms.pattern_kind = 1;
            uniforms.pattern_color = bounds.parent;
            uniforms.pattern_background_color = [
                source_max(bounds.child[0], bounds.parent[0]),
                source_max(bounds.child[1], bounds.parent[1]),
                source_min(bounds.child[2], bounds.parent[2]),
                source_min(bounds.child[3], bounds.parent[3]),
            ];
        }
        Ok(Self {
            source,
            target,
            mode,
            uniforms,
        })
    }
    pub fn source(&self) -> NativeScissor {
        self.source
    }
    pub fn target(&self) -> NativeScissor {
        self.target
    }
    pub fn mode(&self) -> NativeAdvancedBlendMode {
        self.mode
    }
    pub fn uniforms(&self) -> &NativeAdvancedBlendUniforms {
        &self.uniforms
    }
    /// Empty semantic extents retain zero sampling while the physical resolve
    /// uses the original one-pixel minimum required for a WebGPU scissor.
    pub fn source_size(&self) -> [u32; 2] {
        [self.source.width.max(1), self.source.height.max(1)]
    }
}

// C++ std::min/max retain the first operand on equality, including signed zero.
fn source_min(a: f32, b: f32) -> f32 {
    if b < a { b } else { a }
}
fn source_max(a: f32, b: f32) -> f32 {
    if a < b { b } else { a }
}
