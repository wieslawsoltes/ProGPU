use super::*;
#[derive(Clone, Copy)]
pub(super) struct Crossing {
    pub x: f32,
    pub direction: i32,
}
#[derive(Clone, Copy, Default)]
pub(super) struct CurveMetadata {
    minimum: f32,
    maximum: f32,
    cubic: f32,
    quadratic: f32,
    linear: f32,
}
impl CurveMetadata {
    pub fn from_segment(segment: &wire::PathSegment) -> Self {
        let (a, b, c, d) = (segment.p0, segment.p1, segment.p2, segment.p3);
        if segment.kind == PATH_SEGMENT_QUADRATIC {
            Self {
                minimum: minimum(a.y, minimum(b.y, c.y)),
                maximum: maximum(a.y, maximum(b.y, c.y)),
                quadratic: a.y - 2.0 * b.y + c.y,
                linear: 2.0 * (b.y - a.y),
                cubic: 0.0,
            }
        } else if segment.kind == PATH_SEGMENT_CUBIC {
            Self {
                minimum: minimum(minimum(a.y, b.y), minimum(c.y, d.y)),
                maximum: maximum(maximum(a.y, b.y), maximum(c.y, d.y)),
                cubic: -a.y + 3.0 * b.y - 3.0 * c.y + d.y,
                quadratic: 3.0 * a.y - 6.0 * b.y + 3.0 * c.y,
                linear: -3.0 * a.y + 3.0 * b.y,
            }
        } else {
            Self::default()
        }
    }
}
pub(super) fn visit<const METADATA: bool>(
    sample_y: f32,
    record: &GpuGlyphRecord,
    segments: &[wire::PathSegment],
    metadata: &[CurveMetadata],
    mut visitor: impl FnMut(f32, i32),
) {
    let end = record.start_segment + record.segment_count;
    for index in record.start_segment as usize..end as usize {
        let segment = &segments[index];
        let (a, b, c, d) = (segment.p0, segment.p1, segment.p2, segment.p3);
        if segment.kind == PATH_SEGMENT_LINE {
            if a.y == b.y {
                continue;
            }
            if a.y <= sample_y && b.y > sample_y {
                let t = (sample_y - a.y) / (b.y - a.y);
                visitor(a.x + t * (b.x - a.x), 1);
            } else if a.y > sample_y && b.y <= sample_y {
                let t = (sample_y - a.y) / (b.y - a.y);
                visitor(a.x + t * (b.x - a.x), -1);
            }
            continue;
        }
        if segment.kind == PATH_SEGMENT_QUADRATIC {
            let minimum_y = if METADATA {
                metadata[index].minimum
            } else {
                minimum(a.y, minimum(b.y, c.y))
            };
            let maximum_y = if METADATA {
                metadata[index].maximum
            } else {
                maximum(a.y, maximum(b.y, c.y))
            };
            if sample_y < minimum_y || sample_y > maximum_y {
                continue;
            }
            let qa = if METADATA {
                metadata[index].quadratic
            } else {
                a.y - 2.0 * b.y + c.y
            };
            let qb = if METADATA {
                metadata[index].linear
            } else {
                2.0 * (b.y - a.y)
            };
            let qc = a.y - sample_y;
            let roots = roots::quadratic(qa, qb, qc);
            for &t in &roots.values[..roots.count] {
                if t < -0.01 || t > 1.01 {
                    continue;
                }
                let evaluated_t = t.clamp(0.00001, 0.99999);
                let evaluated_one_minus_t = 1.0 - evaluated_t;
                let derivative_y =
                    2.0 * evaluated_one_minus_t * (b.y - a.y) + 2.0 * evaluated_t * (c.y - b.y);
                if !roots::valid(t, derivative_y, sample_y, a.y, c.y) {
                    continue;
                }
                let clamped_t = t.clamp(0.0, 1.0);
                let one_minus_t = 1.0 - clamped_t;
                let crossing_x = one_minus_t * one_minus_t * a.x
                    + 2.0 * one_minus_t * clamped_t * b.x
                    + clamped_t * clamped_t * c.x;
                let direction = if derivative_y > 0.0 {
                    1
                } else if derivative_y < 0.0 {
                    -1
                } else {
                    0
                };
                if direction != 0 {
                    visitor(crossing_x, direction);
                }
            }
            continue;
        }
        let minimum_y = if METADATA {
            metadata[index].minimum
        } else {
            minimum(minimum(a.y, b.y), minimum(c.y, d.y))
        };
        let maximum_y = if METADATA {
            metadata[index].maximum
        } else {
            maximum(maximum(a.y, b.y), maximum(c.y, d.y))
        };
        if sample_y < minimum_y || sample_y > maximum_y {
            continue;
        }
        let ca = if METADATA {
            metadata[index].cubic
        } else {
            -a.y + 3.0 * b.y - 3.0 * c.y + d.y
        };
        let cb = if METADATA {
            metadata[index].quadratic
        } else {
            3.0 * a.y - 6.0 * b.y + 3.0 * c.y
        };
        let cc = if METADATA {
            metadata[index].linear
        } else {
            -3.0 * a.y + 3.0 * b.y
        };
        let cd = a.y - sample_y;
        let roots = roots::cubic(ca, cb, cc, cd);
        for &t in &roots.values[..roots.count] {
            if t < -0.01 || t > 1.01 {
                continue;
            }
            let evaluated_t = t.clamp(0.00001, 0.99999);
            let derivative_y = 3.0 * ca * evaluated_t * evaluated_t + 2.0 * cb * evaluated_t + cc;
            if !roots::valid(t, derivative_y, sample_y, a.y, d.y) {
                continue;
            }
            let clamped_t = t.clamp(0.0, 1.0);
            let one_minus_t = 1.0 - clamped_t;
            let crossing_x = one_minus_t * one_minus_t * one_minus_t * a.x
                + 3.0 * one_minus_t * one_minus_t * clamped_t * b.x
                + 3.0 * one_minus_t * clamped_t * clamped_t * c.x
                + clamped_t * clamped_t * clamped_t * d.x;
            let direction = if derivative_y > 0.0 {
                1
            } else if derivative_y < 0.0 {
                -1
            } else {
                0
            };
            if direction != 0 {
                visitor(crossing_x, direction);
            }
        }
    }
}
pub(super) fn winding(
    x: f32,
    y: f32,
    record: &GpuGlyphRecord,
    segments: &[wire::PathSegment],
) -> i32 {
    let mut winding = 0i32;
    visit::<false>(y, record, segments, &[], |crossing, direction| {
        winding += if x < crossing { direction } else { 0 };
    });
    winding
}
