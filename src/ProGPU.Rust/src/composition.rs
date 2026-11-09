//! Original physical source-axis clip planning, independent of draw geometry.
use crate::{Error, mask::MaskUniforms};
use bytemuck::Zeroable;

mod raster_operation;
pub use raster_operation::{RasterOperation, RasterPattern};

/// Actual source presentation and current parent storage domain.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AxisClipFrame {
    /// Original root physical frame dimensions, used by native target-domain
    /// clipping before intersection with bounded parent storage.
    pub root_size: [u32; 2],
    /// Physical parent x/y/width/height in the original root coordinates.
    pub parent: [u32; 4],
    /// Independent positive source DPI axes.
    pub dpi: [f32; 2],
    /// Original renderer scalar DPI for texture-quad projection. This stays
    /// separate from independent source presentation axes.
    pub semantic_dpi: f32,
    /// Original physical viewport x/y/width/height, independent of parent bounds.
    pub viewport: [u32; 4],
}

/// Immutable source AA clip. All original ancestor edges remain available;
/// allocation bounds never substitute for geometric area or binary scissors.
#[derive(Clone, Debug)]
pub struct AxisClipPlan {
    frame: AxisClipFrame,
    bounds: [f32; 4],
    projected: [f32; 4],
    extent: [u32; 4],
    background: bool,
    // Immediate parent first, matching the native reverse materialized walk.
    ancestors: [[f32; 4]; 15],
    ancestor_count: u8,
}
impl AxisClipPlan {
    #[cfg(feature = "gpu")]
    pub(crate) fn with_background(mut self, enabled: bool) -> Self {
        self.background = enabled;
        self
    }
    /// Plan one explicit unit-opacity identity rectangle after an ordinary
    /// storage boundary. No ordinary layer opacity/mask/background is inferred.
    pub fn new(
        bounds: [f32; 4],
        frame: AxisClipFrame,
        initialize_from_background: bool,
    ) -> Result<Self, Error> {
        if frame.root_size.contains(&0) {
            return Err(Error::InvalidInput);
        }
        if frame
            .dpi
            .iter()
            .chain(std::iter::once(&frame.semantic_dpi))
            .any(|v| !v.is_finite() || *v <= 0.0)
        {
            return Err(Error::InvalidInput);
        }
        for [x, y, w, h] in [frame.parent, frame.viewport] {
            x.checked_add(w).ok_or(Error::Capacity)?;
            y.checked_add(h).ok_or(Error::Capacity)?;
        }
        let [x, y, w, h] = bounds;
        if !bounds.iter().all(|v| v.is_finite()) || w < 0.0 || h < 0.0 {
            return Err(Error::InvalidInput);
        }
        let [dx, dy] = frame.dpi;
        let [vx, vy, _, _] = frame.viewport;
        let projected = (wide::f32x4::from([x, y, x + w, y + h])
            * wide::f32x4::from([dx, dy, dx, dy])
            + wide::f32x4::from([vx as f32, vy as f32, vx as f32, vy as f32]))
        .to_array();
        if !projected.iter().all(|v| v.is_finite()) {
            return Err(Error::InvalidInput);
        }
        let [px, py, pw, ph] = frame.parent;
        // Original outward allocation policy, independent of exact area edges.
        let domain = [
            frame.root_size[0].max(px + pw),
            frame.root_size[1].max(py + ph),
        ];
        let limits = [domain[0], domain[1], domain[0], domain[1]];
        let snapped = std::array::from_fn::<_, 4, _>(|i| {
            let value = projected[i].clamp(0.0, limits[i] as f32);
            let rounded = value.round();
            if (value - rounded).abs() < 0.0001 {
                rounded
            } else {
                value
            }
        });
        let raw = [
            snapped[0].floor() as u32,
            snapped[1].floor() as u32,
            snapped[2].ceil() as u32,
            snapped[3].ceil() as u32,
        ];
        let [vx, vy, vw, vh] = frame.viewport;
        let left = raw[0].max(px).max(vx).min(px + pw);
        let top = raw[1].max(py).max(vy).min(py + ph);
        let right = raw[2].min(px + pw).min(vx + vw);
        let bottom = raw[3].min(py + ph).min(vy + vh);
        let empty = snapped[2] <= snapped[0] || snapped[3] <= snapped[1];
        Ok(Self {
            frame,
            bounds,
            projected,
            extent: [
                left,
                top,
                if empty { 0 } else { right.saturating_sub(left) },
                if empty { 0 } else { bottom.saturating_sub(top) },
            ],
            background: initialize_from_background,
            ancestors: [[0.0; 4]; 15],
            ancestor_count: 0,
        })
    }
    /// Plan an immediately nested source AA clip, retaining every original
    /// ancestor. Use `new` after an ordinary layer to stop the ratio chain.
    pub fn child(&self, bounds: [f32; 4], initialize_from_background: bool) -> Result<Self, Error> {
        if self.ancestor_count == 15 {
            return Err(Error::Capacity);
        }
        let mut child = Self::new(
            bounds,
            AxisClipFrame {
                parent: self.extent,
                ..self.frame
            },
            initialize_from_background,
        )?;
        child.ancestors[0] = self.projected;
        let count = usize::from(self.ancestor_count);
        child.ancestors[1..count + 1].copy_from_slice(&self.ancestors[..count]);
        child.ancestor_count = self.ancestor_count + 1;
        Ok(child)
    }
    /// Exact original logical x/y/width/height.
    pub fn bounds(&self) -> [f32; 4] {
        self.bounds
    }
    /// Original independent source frame, including the actual parent domain.
    pub fn frame(&self) -> AxisClipFrame {
        self.frame
    }
    /// Original projected near/far edges, before allocation rounding.
    pub fn projected_edges(&self) -> [f32; 4] {
        self.projected
    }
    /// Outward physical child x/y/width/height intersected with parent/viewport.
    pub fn extent(&self) -> [u32; 4] {
        self.extent
    }
    /// Whether the original selected flag requires copying actual parent pixels.
    pub fn initializes_background(&self) -> bool {
        self.background
    }
    /// Complete consecutive AA target depth, bounded by the original sixteen.
    pub fn depth(&self) -> u8 {
        self.ancestor_count + 1
    }
    /// Empty source intersections retain metadata without texture or draw work.
    pub fn is_empty(&self) -> bool {
        self.extent[2] == 0 || self.extent[3] == 0
    }
    /// Original area mask localized to actual child storage. Zero radii,
    /// identity mapping and unit opacity remain independent of source alpha.
    pub fn coverage_uniforms(&self) -> MaskUniforms {
        let mut uniforms = MaskUniforms::zeroed();
        uniforms.coordinate0[0] = 1.0;
        uniforms.coordinate1[1] = 1.0;
        uniforms.bounds = localize(self.projected, self.extent);
        uniforms.options = [5.0, 1.0, 0.0, 0.0];
        uniforms
    }
    /// Original parent area and child-intersection edges in actual parent
    /// pixels. The shader divides those areas once for consecutive AA clips.
    pub fn coverage_ratio_bounds(&self) -> Option<[[f32; 4]; 2]> {
        let count = usize::from(self.ancestor_count);
        if count == 0 {
            return None;
        }
        let mut parent = localize(self.ancestors[0], self.frame.parent);
        for ancestor in &self.ancestors[1..count] {
            parent = intersect(parent, localize(*ancestor, self.frame.parent));
        }
        Some([
            parent,
            intersect(localize(self.projected, self.frame.parent), parent),
        ])
    }
}
fn localize(edges: [f32; 4], target: [u32; 4]) -> [f32; 4] {
    std::array::from_fn(|i| (f64::from(edges[i]) - f64::from(target[i % 2])) as f32)
}
fn intersect(a: [f32; 4], b: [f32; 4]) -> [f32; 4] {
    // std::max/min keep the first equal operand, including signed-zero bits.
    std::array::from_fn(|i| {
        if (i < 2 && a[i] < b[i]) || (i >= 2 && b[i] < a[i]) {
            b[i]
        } else {
            a[i]
        }
    })
}
