//! Original 128-byte typed input geometry, separate from broad-phase bounds.
use super::*;
use bytemuck::{Pod, Zeroable};
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u32)]
pub enum HitPrimitiveKind {
    Bounds = 0,
    RectangleFill = 1,
    RectangleStroke = 2,
    EllipseFill = 3,
    EllipseStroke = 4,
    LineStroke = 5,
    PathFill = 6,
    PathStroke = 7,
}
/// Exact original primitive wire. Geometry data and clip segments remain
/// independent of the clipped broad-phase envelope.
#[repr(C)]
#[derive(Clone, Copy, Debug, Default, PartialEq, Pod, Zeroable)]
pub struct HitPrimitive {
    pub bounds_min: [f32; 2],
    pub bounds_max: [f32; 2],
    pub data0: [f32; 4],
    pub data1: [f32; 4],
    pub data2: [f32; 4],
    pub inverse_transform0: [f32; 4],
    pub inverse_transform1: [f32; 4],
    pub kind: u32,
    pub flags: u32,
    pub id: i32,
    pub z_index: f32,
    pub clip_start_segment: u32,
    pub clip_segment_count: u32,
    pub clip_fill_rule: u32,
    pub clip_flags: u32,
}
impl HitPrimitive {
    pub const VISIBLE: u32 = 1;
    pub const HIT_TEST_VISIBLE: u32 = 2;
    pub const POINT_ONLY: u32 = 4;
    pub const REGION_ONLY: u32 = 8;
    pub fn with_flags(mut self, flags: u32) -> Result<Self> {
        if flags & !15 != 0 || flags & 12 == 12 {
            return Err(ArchiveError::InvalidData);
        }
        self.flags = flags;
        Ok(self)
    }
    pub fn with_world_bounds(mut self, min: [f32; 2], max: [f32; 2]) -> Self {
        self.bounds_min = min;
        self.bounds_max = max;
        self
    }
    pub fn with_clip(mut self, start: u32, count: u32, rule: FillRule) -> Self {
        self.clip_start_segment = start;
        self.clip_segment_count = count;
        self.clip_fill_rule = rule as u32;
        self.clip_flags = u32::from(count > 0);
        self
    }
    fn create(
        kind: HitPrimitiveKind,
        id: i32,
        bounds: [f32; 4],
        data0: [f32; 4],
        data1: [f32; 4],
        data2: [f32; 4],
        transform: Matrix4,
        z: f32,
    ) -> Self {
        let bounds = transform_bounds(bounds, transform);
        let inverse = inverse(transform).0;
        Self {
            bounds_min: [bounds[0], bounds[1]],
            bounds_max: [bounds[2], bounds[3]],
            data0,
            data1,
            data2,
            inverse_transform0: [inverse[0][0], inverse[1][0], inverse[3][0], 0.0],
            inverse_transform1: [inverse[0][1], inverse[1][1], inverse[3][1], 0.0],
            kind: kind as u32,
            flags: 3,
            id,
            z_index: z,
            clip_fill_rule: FillRule::Nonzero as u32,
            ..Default::default()
        }
    }
    pub fn bounds(id: i32, b: [f32; 4], transform: Matrix4, z: f32) -> Self {
        Self::create(
            HitPrimitiveKind::Bounds,
            id,
            b,
            b,
            [0.0; 4],
            [0.0; 4],
            transform,
            z,
        )
    }
    pub fn rectangle_fill(
        id: i32,
        b: [f32; 4],
        radius: [f32; 2],
        transform: Matrix4,
        z: f32,
    ) -> Self {
        Self::create(
            HitPrimitiveKind::RectangleFill,
            id,
            b,
            b,
            [radius[0], radius[1], 0.0, 0.0],
            [0.0; 4],
            transform,
            z,
        )
    }
    pub fn rectangle_stroke(
        id: i32,
        b: [f32; 4],
        radius: [f32; 2],
        width: f32,
        tolerance: f32,
        transform: Matrix4,
        z: f32,
    ) -> Self {
        Self::create(
            HitPrimitiveKind::RectangleStroke,
            id,
            padded(b, padding(width, tolerance)),
            b,
            [radius[0], radius[1], width, tolerance],
            [0.0; 4],
            transform,
            z,
        )
    }
    pub fn ellipse_fill(id: i32, b: [f32; 4], transform: Matrix4, z: f32) -> Self {
        Self::create(
            HitPrimitiveKind::EllipseFill,
            id,
            b,
            b,
            [0.0; 4],
            ellipse_data(b),
            transform,
            z,
        )
    }
    pub fn ellipse_stroke(
        id: i32,
        b: [f32; 4],
        width: f32,
        tolerance: f32,
        transform: Matrix4,
        z: f32,
    ) -> Self {
        Self::create(
            HitPrimitiveKind::EllipseStroke,
            id,
            padded(b, padding(width, tolerance)),
            b,
            [width, tolerance, 0.0, 0.0],
            ellipse_data(b),
            transform,
            z,
        )
    }
    pub fn line_stroke(
        id: i32,
        a: [f32; 2],
        b: [f32; 2],
        width: f32,
        caps: [PenLineCap; 2],
        tolerance: f32,
        transform: Matrix4,
        z: f32,
    ) -> Self {
        let mut pad = padding(width, tolerance);
        if caps.contains(&PenLineCap::Square) {
            pad *= 2.0f32.sqrt();
        }
        let delta = sub(b, a);
        let length = length(delta);
        let direction = if length.is_finite() && length > 0.0001 {
            [delta[0] / length, delta[1] / length]
        } else {
            [0.0; 2]
        };
        Self::create(
            HitPrimitiveKind::LineStroke,
            id,
            padded(
                [
                    a[0].min(b[0]),
                    a[1].min(b[1]),
                    a[0].max(b[0]),
                    a[1].max(b[1]),
                ],
                pad,
            ),
            [a[0], a[1], b[0], b[1]],
            [
                width,
                tolerance,
                caps[0] as u32 as f32,
                caps[1] as u32 as f32,
            ],
            [
                direction[0],
                direction[1],
                if length.is_finite() { length } else { 0.0 },
                0.0,
            ],
            transform,
            z,
        )
    }
    pub fn path_fill(
        id: i32,
        b: [f32; 4],
        start: u32,
        count: u32,
        rule: FillRule,
        transform: Matrix4,
        z: f32,
    ) -> Self {
        Self::create(
            HitPrimitiveKind::PathFill,
            id,
            b,
            b,
            [start as f32, count as f32, rule as u32 as f32, 0.0],
            [0.0; 4],
            transform,
            z,
        )
    }
    pub fn path_stroke(
        id: i32,
        b: [f32; 4],
        start: u32,
        count: u32,
        width: f32,
        tolerance: f32,
        caps: [PenLineCap; 2],
        transform: Matrix4,
        z: f32,
    ) -> Self {
        Self::create(
            HitPrimitiveKind::PathStroke,
            id,
            padded(b, padding(width, tolerance)),
            b,
            [start as f32, count as f32, width, tolerance],
            [caps[0] as u32 as f32, caps[1] as u32 as f32, 0.0, 0.0],
            transform,
            z,
        )
    }
}
fn padding(width: f32, tolerance: f32) -> f32 {
    (width.abs() * 0.5 + tolerance.max(0.0)).max(0.0)
}
fn padded(b: [f32; 4], p: f32) -> [f32; 4] {
    [b[0] - p, b[1] - p, b[2] + p, b[3] + p]
}
fn ellipse_data(b: [f32; 4]) -> [f32; 4] {
    let radii = [(b[2] - b[0]) * 0.5, (b[3] - b[1]) * 0.5];
    let inverse = radii.map(|r| {
        if r.is_finite() && r.abs() > 0.0001 {
            1.0 / r
        } else {
            0.0
        }
    });
    [
        (b[0] + b[2]) * 0.5,
        (b[1] + b[3]) * 0.5,
        inverse[0],
        inverse[1],
    ]
}
const _: () = assert!(size_of::<HitPrimitive>() == 128);

// Complete row-vector inverse, preserving the original singular-to-Identity
// fallback. Unlike filter admission this primitive constructor does not turn a
// nonfinite determinant into a valid identity-frame input witness.
fn inverse(matrix: Matrix4) -> Matrix4 {
    let m = matrix.0;
    let mut cofactors = [[0.0f32; 4]; 4];
    for (row, out) in cofactors.iter_mut().enumerate() {
        for (column, cofactor) in out.iter_mut().enumerate() {
            let mut v = [0.0; 9];
            let mut index = 0;
            for r in 0..4 {
                if r == row {
                    continue;
                }
                for c in 0..4 {
                    if c != column {
                        v[index] = m[r][c];
                        index += 1;
                    }
                }
            }
            let value = v[0] * (v[4] * v[8] - v[5] * v[7]) - v[1] * (v[3] * v[8] - v[5] * v[6])
                + v[2] * (v[3] * v[7] - v[4] * v[6]);
            *cofactor = if (row + column) % 2 == 0 {
                value
            } else {
                -value
            };
        }
    }
    let determinant = m[0][0] * cofactors[0][0]
        + m[0][1] * cofactors[0][1]
        + m[0][2] * cofactors[0][2]
        + m[0][3] * cofactors[0][3];
    if determinant.abs() < f32::from_bits(1) {
        return Matrix4::IDENTITY;
    }
    let reciprocal = 1.0 / determinant;
    Matrix4(std::array::from_fn(|r| {
        std::array::from_fn(|c| cofactors[c][r] * reciprocal)
    }))
}
