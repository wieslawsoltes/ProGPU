//! Original declared-bound opacity-mask allocation and direct R8 source drawing.
use super::*;
use crate::{
    mask::{Mask, MaskCompositionMetrics, MaskUniforms},
    scene::{CompiledDrawState, LayerBlend},
    vector::Matrix4,
};
mod pipeline;
pub(super) use pipeline::BoundedMaskPipelines;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(super) struct Bounds {
    pub origin: [u32; 2],
    pub size: [u32; 2],
}
impl Bounds {
    fn full(frame: RenderFrame) -> Self {
        Self {
            origin: frame.origin,
            size: [frame.width, frame.height],
        }
    }
    fn empty(frame: RenderFrame) -> Self {
        Self {
            origin: frame.origin,
            size: [1; 2],
        }
    }
    fn intersection(self, other: Self, frame: RenderFrame) -> Self {
        let x = self.origin[0].max(other.origin[0]);
        let y = self.origin[1].max(other.origin[1]);
        let right = (self.origin[0] + self.size[0]).min(other.origin[0] + other.size[0]);
        let bottom = (self.origin[1] + self.size[1]).min(other.origin[1] + other.size[1]);
        if right <= x || bottom <= y {
            Self::empty(frame)
        } else {
            Self {
                origin: [x, y],
                size: [right - x, bottom - y],
            }
        }
    }
    fn quantized(self, frame: RenderFrame) -> Self {
        let size = [
            self.size[0].max(1).div_ceil(16) * 16,
            self.size[1].max(1).div_ceil(16) * 16,
        ];
        let size = [size[0].min(frame.width), size[1].min(frame.height)];
        Self {
            origin: [
                self.origin[0].min(frame.origin[0] + frame.width - size[0]),
                self.origin[1].min(frame.origin[1] + frame.height - size[1]),
            ],
            size,
        }
    }
    pub(super) fn frame(self, frame: RenderFrame) -> RenderFrame {
        RenderFrame {
            origin: self.origin,
            frame: Frame {
                width: self.size[0],
                height: self.size[1],
                dpi_scale: frame.dpi_scale,
            },
            ..frame
        }
    }
}
// Original Vector3.Transform corner order followed by pairwise min/max, then
// width subtraction/re-addition. SIMD lanes retain independent original corners.
pub(in crate::renderer) fn source_bounds(
    bounds: [f32; 4],
    transform: Matrix4,
    frame: RenderFrame,
) -> Bounds {
    let [x, y, w, h] = bounds;
    let m = transform.0;
    let xs = wide::f32x4::from([x, x + w, x, x + w]);
    let ys = wide::f32x4::from([y, y, y + h, y + h]);
    let px = (xs * wide::f32x4::splat(m[0][0])
        + ys * wide::f32x4::splat(m[1][0])
        + wide::f32x4::ZERO
        + wide::f32x4::splat(m[3][0]))
    .to_array();
    let py = (xs * wide::f32x4::splat(m[0][1])
        + ys * wide::f32x4::splat(m[1][1])
        + wide::f32x4::ZERO
        + wide::f32x4::splat(m[3][1]))
    .to_array();
    if px.iter().chain(&py).any(|v| !v.is_finite()) {
        return Bounds::full(frame);
    }
    let l = px[0].min(px[1]).min(px[2].min(px[3]));
    let r = px[0].max(px[1]).max(px[2].max(px[3]));
    let t = py[0].min(py[1]).min(py[2].min(py[3]));
    let b = py[0].max(py[1]).max(py[2].max(py[3]));
    let edges = [
        l * frame.dpi_scale - 2.0,
        t * frame.dpi_scale - 2.0,
        (l + (r - l)) * frame.dpi_scale + 2.0,
        (t + (b - t)) * frame.dpi_scale + 2.0,
    ];
    if edges.iter().any(|v| !v.is_finite()) {
        return Bounds::full(frame);
    }
    let x = (edges[0].floor() as i32).clamp(
        frame.origin[0] as i32,
        (frame.origin[0] + frame.width) as i32,
    ) as u32;
    let y = (edges[1].floor() as i32).clamp(
        frame.origin[1] as i32,
        (frame.origin[1] + frame.height) as i32,
    ) as u32;
    let r = (edges[2].ceil() as i32).clamp(
        frame.origin[0] as i32,
        (frame.origin[0] + frame.width) as i32,
    ) as u32;
    let b = (edges[3].ceil() as i32).clamp(
        frame.origin[1] as i32,
        (frame.origin[1] + frame.height) as i32,
    ) as u32;
    if r <= x || b <= y {
        Bounds::empty(frame)
    } else {
        Bounds {
            origin: [x, y],
            size: [r - x, b - y],
        }
    }
}
/// Actual bounded source target after each preceding declared mask. Ordinary
/// clips do not shrink this allocation, matching original mask-stack semantics.
pub(super) fn storage_bounds(state: &CompiledDrawState, frame: RenderFrame) -> Bounds {
    if !state
        .components
        .iter()
        .any(|c| matches!(&c.mask, Mask::Source(mask) if mask.is_bounded_source()))
    {
        return Bounds::full(frame);
    }
    let mut previous: Option<Bounds> = None;
    for component in state.components.iter() {
        if component.operation != crate::mask::MaskOperation::Intersect {
            continue;
        }
        match &component.mask {
            Mask::Source(mask) => {
                if mask.is_bounded_source() {
                    let next = mask.bounds().map_or_else(
                        || Bounds::full(frame),
                        |bounds| source_bounds(bounds, mask.transform(), frame),
                    );
                    let next = previous.map_or(next, |p| next.intersection(p, frame));
                    previous = Some(
                        if previous.is_none()
                            && frame.mask_depth <= 1
                            && mask.solid_alpha().is_some()
                            && super::visual::physical_rows(mask.transform(), frame.dpi_scale)
                                .is_ok_and(|rows| rows.is_some())
                        {
                            next
                        } else {
                            next.quantized(frame)
                        },
                    );
                } else {
                    previous = Some(Bounds::full(frame));
                }
            }
            Mask::Analytic(mask) => {
                let next = source_bounds(mask.bounds, Matrix4::from_affine(mask.transform), frame);
                previous = Some(previous.map_or(next, |p| next.intersection(p, frame)));
            }
            Mask::RenderedTexture(mask) => {
                let next = Bounds {
                    origin: mask.storage_origin,
                    size: mask.texture.source.size(),
                };
                previous = Some(previous.map_or(next, |p| next.intersection(p, frame)));
            }
            Mask::FrameAnalytic(mask) => {
                if let Some([x, y, w, h]) = mask.storage_bounds {
                    let next = Bounds {
                        origin: [x, y],
                        size: [w, h],
                    };
                    previous = Some(previous.map_or(next, |p| next.intersection(p, frame)));
                }
            }
            _ => {}
        }
    }
    previous.unwrap_or_else(|| Bounds::full(frame))
}

pub(super) fn resolve_solid(
    source: &CompiledDrawState,
    frame: RenderFrame,
    maximum: u32,
) -> Result<Option<CompiledDrawState>, Error> {
    let [component] = source.components.as_ref() else {
        return Ok(None);
    };
    let Mask::Source(mask) = &component.mask else {
        return Ok(None);
    };
    if frame.mask_depth > 1 || component.operation != crate::mask::MaskOperation::Intersect {
        return Ok(None);
    }
    let (Some(bounds), Some(alpha)) = (mask.bounds(), mask.solid_alpha()) else {
        return Ok(None);
    };
    let Some(rows) = super::visual::physical_rows(mask.transform(), frame.dpi_scale)? else {
        return Ok(None);
    };
    let uniforms = MaskUniforms {
        coordinate0: rows[0],
        coordinate1: rows[1],
        bounds: [
            bounds[0],
            bounds[1],
            bounds[0] + bounds[2],
            bounds[1] + bounds[3],
        ],
        options: [2.0, alpha, 0.0, 0.0],
        ..bytemuck::Zeroable::zeroed()
    };
    frame.uniforms(maximum)?;
    let mut result = source.clone();
    result.masks = [
        uniforms,
        MaskUniforms::zeroed(),
        MaskUniforms::zeroed(),
        MaskUniforms::zeroed(),
    ];
    result.count = 1;
    result.coverage = None;
    result.materialized = false;
    result.local_coverage = false;
    Ok(Some(result))
}
