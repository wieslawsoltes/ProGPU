//! Original compositor stroke vertex payloads. Indices embedded in descriptors
//! refer to the actual destination mesh, including earlier painter commands.
use super::*;
use std::f32::consts::PI;

impl Compiler<'_> {
    fn descriptor(&self, kind: f32) -> VectorVertex {
        VectorVertex {
            position: [0.0; 2],
            color: self.color,
            texture_coordinate: [0.0; 2],
            brush_index: self.brush_index,
            shape_size: [0.0; 2],
            corner_radius: 0.0,
            stroke_thickness: 0.0,
            shape_type: kind + if self.draw.aliased { 1000.0 } else { 0.0 },
        }
    }
    fn ordinal(&self) -> Result<f32, Error> {
        Ok(u32::try_from(self.mesh.vertices().len()).map_err(|_| Error::Capacity)? as f32)
    }
    pub(super) fn line(&mut self, start: [f32; 2], end: [f32; 2]) -> Result<(), Error> {
        let transform = self.draw.frame.transform;
        if self.stroke.use_affine {
            let delta = sub(end, start);
            let n = length(delta);
            if !n.is_finite() || n <= EPS {
                return Ok(());
            }
            let d = [delta[0] / n, delta[1] / n];
            let normal = mul([-d[1], d[0]], self.stroke.geometry * 0.5);
            self.quadrilateral(
                [
                    transform.transform_point(add(start, normal)),
                    transform.transform_point(add(end, normal)),
                    transform.transform_point(sub(end, normal)),
                    transform.transform_point(sub(start, normal)),
                ],
                14.0,
            )
        } else {
            let start = transform.transform_point(start);
            let end = transform.transform_point(end);
            let template = VectorVertex {
                texture_coordinate: start,
                shape_size: end,
                stroke_thickness: self.stroke.encoded,
                ..self.descriptor(3.0)
            };
            let vertices = [(start, 1.0), (start, -1.0), (end, 1.0), (end, -1.0)].map(
                |(position, corner_radius)| VectorVertex {
                    position,
                    corner_radius,
                    ..template
                },
            );
            self.mesh.push_indexed(&vertices, &[0, 1, 2, 1, 3, 2])
        }
    }
    fn quadrilateral(&mut self, points: [[f32; 2]; 4], kind: f32) -> Result<(), Error> {
        let [a, b, c, d] = points;
        let area = cross(sub(b, a), sub(c, a));
        if !area.is_finite() || area.abs() <= EPS {
            return Ok(());
        }
        let min = sub(min_point(min_point(a, b), min_point(c, d)), [1.5; 2]);
        let max = add(max_point(max_point(a, b), max_point(c, d)), [1.5; 2]);
        if !finite(min) || !finite(max) {
            return Ok(());
        }
        let template = VectorVertex {
            color: [a[0], a[1], b[0], b[1]],
            shape_size: c,
            corner_radius: d[0],
            stroke_thickness: d[1],
            ..self.descriptor(kind)
        };
        self.mesh
            .push_quad(corners(min, max).map(|position| VectorVertex {
                position,
                texture_coordinate: position,
                ..template
            }))
    }
    fn triangle(&mut self, points: [[f32; 2]; 3], masks: [u32; 2]) -> Result<(), Error> {
        let [a, b, c] = points;
        let area = cross(sub(b, a), sub(c, a));
        if !area.is_finite() || area.abs() <= EPS {
            return Ok(());
        }
        let min = sub(min_point(a, min_point(b, c)), [1.5; 2]);
        let max = add(max_point(a, max_point(b, c)), [1.5; 2]);
        if !finite(min) || !finite(max) {
            return Ok(());
        }
        let template = VectorVertex {
            color: [a[0], a[1], b[0], b[1]],
            shape_size: c,
            corner_radius: masks[0] as f32,
            stroke_thickness: masks[1] as f32,
            ..self.descriptor(13.0)
        };
        self.mesh
            .push_quad(corners(min, max).map(|position| VectorVertex {
                position,
                texture_coordinate: position,
                ..template
            }))
    }
    pub(super) fn bezier(&mut self, points: &[[f32; 2]]) -> Result<(), Error> {
        let transform = self.draw.frame.transform;
        if self.stroke.use_affine {
            let count = affine_bezier_count(points, transform);
            let mut previous = points[0];
            let mut previous_tangent = bezier_tangent(points, 0.0);
            for i in 1..=count {
                let t = i as f32 / count as f32;
                let point = match *points {
                    [a, b, c] => evaluate_quadratic([a, b, c], t),
                    [a, b, c, d] => evaluate_cubic([a, b, c, d], t),
                    _ => return Err(Error::InvalidInput),
                };
                let tangent = bezier_tangent(points, t);
                self.curve_section(
                    [previous, point],
                    [previous_tangent, tangent],
                    [i == 1, i == count],
                )?;
                previous = point;
                previous_tangent = tangent;
            }
            return Ok(());
        }
        let base = self.ordinal()?;
        let color = if points.len() == 4 {
            let end = transform.transform_point(points[3]);
            [end[0], end[1], 0.0, 0.0]
        } else {
            [0.0; 4]
        };
        let template = VectorVertex {
            position: transform.transform_point(points[0]),
            color,
            texture_coordinate: transform.transform_point(points[1]),
            shape_size: transform.transform_point(points[2]),
            corner_radius: base,
            stroke_thickness: self.stroke.encoded,
            ..self.descriptor(if points.len() == 3 { 5.0 } else { 6.0 })
        };
        let vertices = [template; 50];
        let mut indices = [0; 144];
        for (i, output) in indices.chunks_exact_mut(6).enumerate() {
            let left = (2 * i) as u32;
            output.copy_from_slice(&[left, left + 1, left + 2, left + 1, left + 3, left + 2]);
        }
        self.mesh.push_indexed(&vertices, &indices)
    }
    fn curve_section(
        &mut self,
        points: [[f32; 2]; 2],
        tangents: [[f32; 2]; 2],
        ends: [bool; 2],
    ) -> Result<(), Error> {
        let chord = sub(points[1], points[0]);
        let Some(start) = normalize(tangents[0], chord) else {
            return Ok(());
        };
        let Some(end) = normalize(tangents[1], chord) else {
            return Ok(());
        };
        let radius = self.stroke.geometry * 0.5;
        let a = mul([-start[1], start[0]], radius);
        let b = mul([-end[1], end[0]], radius);
        let m = self.draw.frame.transform;
        let points = [
            m.transform_point(add(points[0], a)),
            m.transform_point(add(points[1], b)),
            m.transform_point(sub(points[1], b)),
            m.transform_point(sub(points[0], a)),
        ];
        let kind = match ends {
            [true, true] => 14.0,
            [true, false] => 16.0,
            [false, true] => 17.0,
            [false, false] => 15.0,
        };
        self.quadrilateral(points, kind)
    }
    pub(super) fn arc(&mut self, start: [f32; 2], arc: ArcSegment) -> Result<(), Error> {
        let appended = if self.stroke.use_affine {
            self.affine_arc(start, arc)?
        } else {
            self.analytic_arc(start, arc)?
        };
        if !appended {
            let points = arc.flattened(start, ArcSegment::DEFAULT_FLATTEN_ANGLE)?;
            for pair in points.windows(2) {
                if pair[0] != pair[1] {
                    self.line(pair[0], pair[1])?;
                }
            }
        }
        Ok(())
    }
    fn affine_arc(&mut self, start: [f32; 2], arc: ArcSegment) -> Result<bool, Error> {
        let Some(p) = arc.shader_parameters(start, Matrix4::IDENTITY) else {
            return Ok(false);
        };
        let count = arc.flattened_segment_count(start, PI / 24.0);
        if count == 0 {
            return Ok(false);
        }
        let traversal = 1.0f32.copysign(p.delta);
        let mut previous = start;
        let mut previous_tangent = mul(arc_tangent(p, p.theta), traversal);
        for i in 1..=count {
            let t = i as f32 / count as f32;
            let theta = p.theta + t * p.delta;
            let point = if i == count {
                arc.end
            } else {
                p.evaluate(theta)
            };
            let tangent = mul(arc_tangent(p, theta), traversal);
            self.curve_section(
                [previous, point],
                [previous_tangent, tangent],
                [i == 1, i == count],
            )?;
            previous = point;
            previous_tangent = tangent;
        }
        Ok(true)
    }
    fn analytic_arc(&mut self, start: [f32; 2], arc: ArcSegment) -> Result<bool, Error> {
        let Some(p) = arc.shader_parameters(start, self.draw.frame.transform) else {
            return Ok(false);
        };
        let determinant = cross(p.axis_x, p.axis_y);
        if !determinant.is_finite() || determinant.abs() <= EPS {
            return Ok(false);
        }
        let width = self.stroke.encoded;
        let retained = if width < -1.0 { -width - 1.0 } else { width };
        let pad = if width == -1.0 {
            2.0
        } else {
            retained * 0.5 + 2.0
        };
        let (min, max) = if let Some(bounds) = p.bounds() {
            (
                sub([bounds[0], bounds[1]], [pad; 2]),
                add([bounds[2], bounds[3]], [pad; 2]),
            )
        } else {
            let extent = [
                p.axis_x[0].abs() + p.axis_y[0].abs() + pad,
                p.axis_x[1].abs() + p.axis_y[1].abs() + pad,
            ];
            (sub(p.center, extent), add(p.center, extent))
        };
        if !finite(min) || !finite(max) || min[0] >= max[0] || min[1] >= max[1] {
            return Ok(false);
        }
        let template = VectorVertex {
            color: [p.center[0], p.center[1], p.theta, p.delta],
            texture_coordinate: p.axis_x,
            shape_size: p.axis_y,
            stroke_thickness: width,
            ..self.descriptor(12.0)
        };
        self.mesh
            .push_quad(corners(min, max).map(|position| VectorVertex {
                position,
                ..template
            }))?;
        Ok(true)
    }
    pub(super) fn join(
        &mut self,
        point: [f32; 2],
        incoming: [f32; 2],
        outgoing: [f32; 2],
        smooth: bool,
    ) -> Result<(), Error> {
        let pen = &self.draw.pen;
        let m = self.draw.frame.transform;
        if pen.is_hairline() || pen.is_fixed() {
            if smooth {
                return Ok(());
            }
            let point = m.transform_point(point);
            let incoming = direction(m, incoming);
            let outgoing = direction(m, outgoing);
            if !finite(point)
                || !finite(incoming)
                || !finite(outgoing)
                || dot(incoming, incoming) <= EPS * EPS
                || dot(outgoing, outgoing) <= EPS * EPS
            {
                return Ok(());
            }
            let miter = if pen.options.miter_limit.is_finite() && pen.options.miter_limit >= 1.0 {
                pen.options.miter_limit
            } else {
                1.0
            };
            let vertex = VectorVertex {
                position: point,
                color: [
                    pen.options.join as u32 as f32,
                    miter,
                    self.ordinal()?,
                    if pen.options.join == PenLineJoin::Miter && pen.options.clip_miter_at_limit {
                        1.0
                    } else {
                        0.0
                    },
                ],
                texture_coordinate: incoming,
                shape_size: outgoing,
                stroke_thickness: if pen.is_hairline() {
                    -1.0
                } else {
                    encode_fixed(pen.options.thickness)
                },
                ..self.descriptor(23.0)
            };
            return self.mesh.push_quad([vertex; 4]);
        }
        let (point, incoming, outgoing, thickness) = if self.stroke.use_affine {
            (point, incoming, outgoing, self.stroke.geometry)
        } else {
            (
                m.transform_point(point),
                direction(m, incoming),
                direction(m, outgoing),
                self.stroke.geometry * m.stroke_scales().map_or(1.0, |s| s[0]),
            )
        };
        let frame = if pen.options.use_wpf_join_semantics {
            StrokeJoinFrame {
                point,
                incoming,
                outgoing,
            }
        } else {
            StrokeJoinFrame::from_line(sub(point, incoming), point, add(point, outgoing))
        };
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = pen.write_join(&mut triangles, frame, thickness, smooth, 8)?;
        let join = if pen.options.use_wpf_join_semantics && smooth {
            PenLineJoin::Round
        } else {
            pen.options.join
        };
        for (i, triangle) in triangles[..count].iter().enumerate() {
            let points = if self.stroke.use_affine {
                triangle.points.map(|p| m.transform_point(p))
            } else {
                triangle.points
            };
            self.triangle(points, join_masks(join, count, i))?;
        }
        Ok(())
    }
    pub(super) fn cap(
        &mut self,
        center: [f32; 2],
        tangent: [f32; 2],
        start: bool,
        cap: PenLineCap,
    ) -> Result<(), Error> {
        if self.draw.pen.is_hairline() || self.draw.pen.is_fixed() {
            return self.device_cap(center, tangent, start, cap, false);
        }
        let m = self.draw.frame.transform;
        if self.stroke.use_affine {
            if cap != PenLineCap::Round {
                let n = length(tangent);
                if !n.is_finite() || n <= EPS {
                    return Ok(());
                }
            }
            self.framed_cap(center, tangent, start, cap, self.stroke.geometry, Some(m))
        } else {
            self.framed_cap(
                m.transform_point(center),
                direction(m, tangent),
                start,
                cap,
                self.stroke.geometry * m.stroke_scales().map_or(1.0, |s| s[0]),
                None,
            )
        }
    }
    fn device_cap(
        &mut self,
        center: [f32; 2],
        tangent: [f32; 2],
        start: bool,
        cap: PenLineCap,
        full: bool,
    ) -> Result<(), Error> {
        if cap == PenLineCap::Flat {
            return Ok(());
        }
        let m = self.draw.frame.transform;
        let center = m.transform_point(center);
        let tangent = direction(m, tangent);
        if !finite(center) || !finite(tangent) || dot(tangent, tangent) <= EPS * EPS {
            return Ok(());
        }
        let vertex = VectorVertex {
            position: center,
            color: [
                cap as u32 as f32,
                u32::from(start) as f32,
                u32::from(full) as f32,
                0.0,
            ],
            texture_coordinate: tangent,
            shape_size: [self.ordinal()?, 0.0],
            stroke_thickness: self.stroke.encoded,
            ..self.descriptor(22.0)
        };
        self.mesh.push_quad([vertex; 4])
    }
    fn framed_cap(
        &mut self,
        center: [f32; 2],
        tangent: [f32; 2],
        start: bool,
        cap: PenLineCap,
        width: f32,
        matrix: Option<Matrix4>,
    ) -> Result<(), Error> {
        if cap == PenLineCap::Round {
            return self.round_cap(
                center,
                tangent,
                start,
                width,
                matrix.unwrap_or(Matrix4::IDENTITY),
            );
        }
        let (a, b) = if start {
            (center, add(center, tangent))
        } else {
            (sub(center, tangent), center)
        };
        let mut triangles = [StrokeTriangle::default(); MAX_STROKE_TRIANGLES];
        let count = StrokeCap {
            cap,
            thickness: width,
            is_start: start,
            max_round_segments: 8,
        }
        .write_line(&mut triangles, a, b)?;
        for (i, triangle) in triangles[..count].iter().enumerate() {
            self.triangle(
                matrix.map_or(triangle.points, |m| {
                    triangle.points.map(|p| m.transform_point(p))
                }),
                cap_masks(cap, count, i),
            )?;
        }
        Ok(())
    }
    fn round_cap(
        &mut self,
        center: [f32; 2],
        tangent: [f32; 2],
        start: bool,
        width: f32,
        matrix: Matrix4,
    ) -> Result<(), Error> {
        let n = length(tangent);
        if !n.is_finite() || n <= EPS || !width.is_finite() || width <= EPS {
            return Ok(());
        }
        let Some([_, minimum]) = matrix.stroke_scales() else {
            return Ok(());
        };
        let tangent = [tangent[0] / n, tangent[1] / n];
        let outward = if start { mul(tangent, -1.0) } else { tangent };
        let normal = [-outward[1], outward[0]];
        let radius = width * 0.5;
        let padding = 1.5 / minimum;
        let template = VectorVertex {
            color: [0.0; 4],
            shape_size: [width, 0.0],
            corner_radius: PenLineCap::Round as u32 as f32,
            ..self.descriptor(24.0)
        };
        self.mesh.push_quad(
            corners(
                [-padding, -radius - padding],
                [radius + padding, radius + padding],
            )
            .map(|coordinate| {
                let position = add(
                    add(center, mul(outward, coordinate[0])),
                    mul(normal, coordinate[1]),
                );
                VectorVertex {
                    position: matrix.transform_point(position),
                    texture_coordinate: coordinate,
                    ..template
                }
            }),
        )
    }
    pub(super) fn degenerate(&mut self, center: [f32; 2]) -> Result<(), Error> {
        let pen = &self.draw.pen;
        let a = pen.options.start_cap;
        let b = pen.options.end_cap;
        if a == PenLineCap::Flat && b == PenLineCap::Flat {
            return Ok(());
        }
        let full = a == b && matches!(a, PenLineCap::Round | PenLineCap::Square);
        if pen.is_hairline() || pen.is_fixed() {
            if full {
                return self.device_cap(center, [1.0, 0.0], false, a, true);
            }
            self.device_cap(center, [1.0, 0.0], true, a, false)?;
            return self.device_cap(center, [1.0, 0.0], false, b, false);
        }
        let m = self.draw.frame.transform;
        if full {
            let scale = m.stroke_scales().map_or(1.0, |s| s[0]);
            let local = if scale.is_finite() && scale > EPS {
                self.stroke.device / scale
            } else {
                self.stroke.device
            };
            let radius = local * 0.5;
            let padding = if a == PenLineCap::Round && scale > EPS {
                1.5 / scale
            } else {
                0.0
            };
            let extent = radius + padding;
            let color = if matches!(pen.brush.kind, BrushKind::Solid(_)) {
                self.color
            } else {
                [center[0], center[1], 0.0, 0.0]
            };
            let template = VectorVertex {
                color,
                shape_size: [radius * 2.0; 2],
                ..self.descriptor(if a == PenLineCap::Round { 1.0 } else { 7.0 })
            };
            return self
                .mesh
                .push_quad(corners([-extent; 2], [extent; 2]).map(|offset| {
                    let local = add(center, offset);
                    VectorVertex {
                        position: m.transform_point(local),
                        texture_coordinate: if a == PenLineCap::Round {
                            offset
                        } else {
                            local
                        },
                        ..template
                    }
                }));
        }
        let center = m.transform_point(center);
        let mut tangent = direction(m, [1.0, 0.0]);
        if dot(tangent, tangent) <= EPS * EPS {
            tangent = [1.0, 0.0];
        }
        self.framed_cap(center, tangent, true, a, self.stroke.device, None)?;
        self.framed_cap(center, tangent, false, b, self.stroke.device, None)
    }
}

fn cross(a: [f32; 2], b: [f32; 2]) -> f32 {
    a[0] * b[1] - a[1] * b[0]
}
fn min_point(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [minimum(a[0], b[0]), minimum(a[1], b[1])]
}
fn max_point(a: [f32; 2], b: [f32; 2]) -> [f32; 2] {
    [maximum(a[0], b[0]), maximum(a[1], b[1])]
}
fn corners(min: [f32; 2], max: [f32; 2]) -> [[f32; 2]; 4] {
    [min, [max[0], min[1]], max, [min[0], max[1]]]
}
fn normalize(mut value: [f32; 2], fallback: [f32; 2]) -> Option<[f32; 2]> {
    let mut n = length(value);
    if !n.is_finite() || n <= EPS {
        value = fallback;
        n = length(value);
    }
    (n.is_finite() && n > EPS).then(|| [value[0] / n, value[1] / n])
}
fn bezier_tangent(points: &[[f32; 2]], t: f32) -> [f32; 2] {
    let u = 1.0 - t;
    match *points {
        [a, b, c] => mul(add(mul(sub(b, a), u), mul(sub(c, b), t)), 2.0),
        [a, b, c, d] => mul(
            add(
                add(mul(sub(b, a), u * u), mul(sub(c, b), 2.0 * u * t)),
                mul(sub(d, c), t * t),
            ),
            3.0,
        ),
        _ => [0.0; 2],
    }
}
fn affine_bezier_count(points: &[[f32; 2]], matrix: Matrix4) -> usize {
    let curvature = |points: &[[f32; 2]]| {
        let a = matrix.transform_point(points[0]).map(f64::from);
        let b = matrix.transform_point(points[1]).map(f64::from);
        let c = matrix.transform_point(points[2]).map(f64::from);
        let x = a[0] - 2.0 * b[0] + c[0];
        let y = a[1] - 2.0 * b[1] + c[1];
        (x * x + y * y).sqrt()
    };
    let squared = if points.len() == 3 {
        curvature(points) / (4.0 * 0.25)
    } else {
        let a = curvature(points);
        let b = curvature(&points[1..]);
        let c = if a.is_nan() || b.is_nan() {
            f64::NAN
        } else {
            a.max(b)
        };
        (0.75 * c) / 0.25
    };
    if !squared.is_finite() {
        1024
    } else if squared <= 24.0 * 24.0 {
        24
    } else if squared >= 1024.0 * 1024.0 {
        1024
    } else {
        squared.sqrt().ceil() as usize
    }
}
fn arc_tangent(p: ArcShaderParameters, theta: f32) -> [f32; 2] {
    add(
        mul(mul(p.axis_x, -1.0), theta.sin()),
        mul(p.axis_y, theta.cos()),
    )
}
fn cap_masks(cap: PenLineCap, count: usize, index: usize) -> [u32; 2] {
    if cap == PenLineCap::Square && count == 2 {
        if index == 0 { [3, 4] } else { [2, 0] }
    } else if cap == PenLineCap::Triangle && count == 1 {
        [3, 0]
    } else {
        [2, if index + 1 < count { 4 } else { 0 }]
    }
}
fn join_masks(join: PenLineJoin, count: usize, index: usize) -> [u32; 2] {
    if count == 1 {
        return [7, 0];
    }
    if matches!(join, PenLineJoin::Miter | PenLineJoin::MiterOrBevel) && count == 2 {
        return [3, if index == 0 { 4 } else { 0 }];
    }
    let mut exterior = 2;
    if index == 0 {
        exterior |= 1;
    }
    if index + 1 == count {
        [exterior | 4, 0]
    } else {
        [exterior, 4]
    }
}
