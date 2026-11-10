//! Original target-space brush paint and geometry-mask scissor production.
use super::*;
use crate::geometry::{Affine, AnalyticPrimitive, Shape};
pub(super) struct BrushPlan {
    pub vertices: Vec<VectorVertex>,
    pub indices: Vec<u32>,
    pub brush: wire::Brush,
    pub stops: Arc<[wire::GradientStop]>,
    pub scissor: NativeScissor,
    pub view: [f32; 4],
    pub opacity: f32,
}
impl BrushPlan {
    pub fn byte_cost(&self) -> u64 {
        self.vertices.len() as u64 * 56
            + self.indices.len() as u64 * 4
            + 256
            + (self.stops.len() as u64 * 32).max(32)
    }
}
fn advanced(t: NativeMaskTarget) -> bool {
    t.presentation.viewport_x != 0
        || t.presentation.viewport_y != 0
        || t.presentation.dpi_scale_x != t.dpi_scale
        || t.presentation.dpi_scale_y != t.dpi_scale
}
fn view(t: NativeMaskTarget) -> Result<[f32; 4], Error> {
    let p = t.presentation;
    let values = [
        (1.0 / f64::from(p.dpi_scale_x)) as f32,
        (1.0 / f64::from(p.dpi_scale_y)) as f32,
        ((f64::from(t.extent.x) - f64::from(p.viewport_x)) / f64::from(p.dpi_scale_x)) as f32,
        ((f64::from(t.extent.y) - f64::from(p.viewport_y)) / f64::from(p.dpi_scale_y)) as f32,
    ];
    if values.iter().any(|v| !v.is_finite()) {
        return Err(Error::InvalidInput);
    }
    Ok(values)
}
fn material_stops(
    stops: &Arc<[wire::GradientStop]>,
    count: u32,
) -> Result<Arc<[wire::GradientStop]>, Error> {
    let selected = stops.get(..count as usize).ok_or(Error::InvalidInput)?;
    let mut result = allocate(selected.len())?;
    result.extend_from_slice(selected);
    Ok(result.into())
}
pub(super) fn compile_brush(
    source: &BrushMask,
    request: &NativeMaskRequest,
) -> Result<BrushPlan, Error> {
    let t = request.target;
    let b = source.header;
    let target_x = if advanced(t) {
        0.0
    } else {
        t.extent.x as f32 / t.dpi_scale
    };
    let target_y = if advanced(t) {
        0.0
    } else {
        t.extent.y as f32 / t.dpi_scale
    };
    let mut transform = b.transform;
    if !advanced(t) {
        transform.m31 -= target_x;
        transform.m32 -= target_y;
    }
    if let Some(state) = request.composite_state {
        let mut first = wire::Point {
            x: b.bounds.x * transform.m11 + b.bounds.y * transform.m21 + transform.m31 + target_x,
            y: b.bounds.x * transform.m12 + b.bounds.y * transform.m22 + transform.m32 + target_y,
        };
        let mut last = wire::Point {
            x: (b.bounds.x + b.bounds.width) * transform.m11
                + (b.bounds.y + b.bounds.height) * transform.m21
                + transform.m31
                + target_x,
            y: (b.bounds.x + b.bounds.width) * transform.m12
                + (b.bounds.y + b.bounds.height) * transform.m22
                + transform.m32
                + target_y,
        };
        let cursor = request.cursor();
        cursor.snap_composite_point(&state, &mut first)?;
        cursor.snap_composite_point(&state, &mut last)?;
        transform.m11 = (last.x - first.x) / b.bounds.width;
        transform.m12 = 0.0;
        transform.m21 = 0.0;
        transform.m22 = (last.y - first.y) / b.bounds.height;
        transform.m31 = first.x - target_x - b.bounds.x * transform.m11;
        transform.m32 = first.y - target_y - b.bounds.y * transform.m22;
    }
    if advanced(t) {
        transform = localize_transform(transform, t.extent, t.presentation, t.dpi_scale);
    }
    let color = if b.brush.kind == SCENE_BRUSH_SOLID {
        let c = b.brush.colors[0];
        [c.r, c.g, c.b, c.a]
    } else {
        [
            b.bounds.x + b.bounds.width * 0.5,
            b.bounds.y + b.bounds.height * 0.5,
            0.0,
            1.0,
        ]
    };
    let primitive = AnalyticPrimitive {
        shape: Shape::Rectangle,
        bounds: [b.bounds.x, b.bounds.y, b.bounds.width, b.bounds.height],
        color,
        transform: Affine([
            transform.m11,
            transform.m12,
            transform.m21,
            transform.m22,
            transform.m31,
            transform.m32,
        ]),
        corner_radius: 0.0,
        stroke_thickness: 0.0,
        aliased: false,
    };
    let vertices = primitive.vertices(1.5 / primitive.transform.scales()?[1])?;
    let mut owned = allocate(4)?;
    owned.extend_from_slice(&vertices);
    let mut indices = allocate(6)?;
    indices.extend_from_slice(&[0, 1, 2, 0, 2, 3]);
    Ok(BrushPlan {
        vertices: owned,
        indices,
        brush: b.brush,
        stops: material_stops(&source.stops, b.gradient_stop_count)?,
        scissor: NativeScissor::full(t.size()),
        view: view(t)?,
        opacity: b.opacity,
    })
}
pub(super) fn compile_geometry(
    source: &GeometryMask,
    request: &NativeMaskRequest,
) -> Result<BrushPlan, Error> {
    let t = request.target;
    let h = source.header;
    let start = h.primitive_offset as usize;
    let end = start
        .checked_add(h.primitive_count as usize)
        .ok_or(Error::Capacity)?;
    let selected = source
        .primitives
        .get(start..end)
        .ok_or(Error::InvalidInput)?;
    let mut primitives = allocate(selected.len())?;
    for original in selected {
        let mut p = *original;
        if advanced(t) {
            p.transform = localize_transform(p.transform, t.extent, t.presentation, t.dpi_scale);
        } else {
            p.transform.m31 -= t.extent.x as f32 / t.dpi_scale;
            p.transform.m32 -= t.extent.y as f32 / t.dpi_scale;
        }
        if h.brush.kind == SCENE_BRUSH_SOLID {
            p.color = h.brush.colors[0];
        }
        primitives.push(p);
    }
    let (vertices, indices) = super::super::vector::compile_mask_geometry(&primitives)?;
    Ok(BrushPlan {
        vertices,
        indices,
        brush: h.brush,
        stops: material_stops(&source.stops, h.gradient_stop_count)?,
        scissor: scissor(&h, t),
        view: view(t)?,
        opacity: h.opacity,
    })
}
fn scissor(mask: &wire::LayerGeometryMask, target: NativeMaskTarget) -> NativeScissor {
    let t = mask.transform;
    let p = target.presentation;
    let at = |x: f64, y: f64| {
        [
            (x * f64::from(t.m11) + y * f64::from(t.m21) + f64::from(t.m31))
                * f64::from(p.dpi_scale_x)
                + (f64::from(p.viewport_x) - f64::from(target.extent.x)),
            (x * f64::from(t.m12) + y * f64::from(t.m22) + f64::from(t.m32))
                * f64::from(p.dpi_scale_y)
                + (f64::from(p.viewport_y) - f64::from(target.extent.y)),
        ]
    };
    // Far edges are added in float before the source widens to double.
    let right = f64::from(mask.bounds.x + mask.bounds.width);
    let bottom = f64::from(mask.bounds.y + mask.bounds.height);
    let corners = [
        at(f64::from(mask.bounds.x), f64::from(mask.bounds.y)),
        at(right, f64::from(mask.bounds.y)),
        at(f64::from(mask.bounds.x), bottom),
        at(right, bottom),
    ];
    let (mut min, mut max) = (corners[0], corners[0]);
    for c in &corners[1..] {
        for i in 0..2 {
            min[i] = min[i].min(c[i]);
            max[i] = max[i].max(c[i]);
        }
    }
    let limits = target.size().map(f64::from);
    for i in 0..2 {
        min[i] = (min[i] - 2.0).floor().clamp(0.0, limits[i]);
        max[i] = (max[i] + 2.0).ceil().clamp(0.0, limits[i]);
    }
    if max[0] <= min[0] || max[1] <= min[1] {
        return NativeScissor {
            x: 0,
            y: 0,
            width: 1,
            height: 1,
            drawable: false,
        };
    }
    let [x, y] = min.map(|v| v as u32);
    NativeScissor {
        x,
        y,
        width: max[0] as u32 - x,
        height: max[1] as u32 - y,
        drawable: true,
    }
}
